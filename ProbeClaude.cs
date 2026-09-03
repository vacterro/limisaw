using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

// Claude quota. Four independent sources, none of them a guess: the CLI's own
// /usage answer, the status-line cache a bridge may have written, Claude
// Desktop's own usage sampler, and the refusals Claude Code journals when the
// API says no. The provider only decides which is freshest and how a refusal
// overrides a percentage.
namespace Limisaw
{
    static class ClaudeSource
    {
        // Past this the newest fact is old enough that the window may already
        // have rolled; the snapshot is reported STALE rather than current.
        const double StaleAfterS = 15 * 60;
        // Desktop samples every ~5 minutes while it runs. Beyond this the
        // sampler is stopped and its numbers describe a spent window.
        const double DesktopStaleAfterS = 45 * 60;

        public static string Home()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, ".claude");
        }

        // One installation is one account: ~/.claude and ~/.claude.json are two
        // faces of it, and Desktop alone still counts (its sampler is real).
        public static bool Installed()
        {
            string dir = Home();
            if (dir.Length == 0) return false;
            return Directory.Exists(dir) || File.Exists(dir + ".json")
                || File.Exists(DesktopHistoryPath());
        }

        public static ProbeAccount Probe(double deadline)
        {
            double now = Stamp.Now;
            var acc = new ProbeAccount
            {
                Provider = "claude", ProviderLabel = "Claude Code", Name = "Claude",
            };

            var readings = new List<Reading>();
            Reading cli = CliUsage(deadline, now);
            if (cli != null) readings.Add(cli);
            string bridgeError = null;
            Reading bridge = BridgeCache(out bridgeError);
            if (bridge != null) readings.Add(bridge);
            Reading desktop = DesktopSample(now);
            if (desktop != null) readings.Add(desktop);

            // An explicit refusal in Claude Code's own transcript beats any
            // percentage: that window is spent until it resets. It is also the
            // only directory-walking read here, so it is skipped when the sweep
            // has no time left rather than blowing the deadline.
            var blocks = new Dictionary<string, Block>();
            if (Stamp.Now < deadline) blocks = Transcripts.ActiveBlocks(Home(), now);

            if (readings.Count == 0 && blocks.Count == 0)
            {
                acc.Status = Model.UNAVAILABLE;
                acc.Error = bridgeError ?? "Claude has not supplied rate limits yet";
                acc.Quiet = false;
                acc.Windows.Add(ProbeWindow.Unavailable(Model.FIVE_HOUR));
                acc.Windows.Add(ProbeWindow.Unavailable(Model.WEEKLY));
                return acc;
            }

            // Freshest percentage source wins; the others only fill gaps, so a
            // six-hour-old cache can never overwrite a five-minute-old sample.
            readings.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt));
            var merged = new Dictionary<string, Slot>();
            var order = new List<string>();
            foreach (Reading r in readings)
                foreach (KeyValuePair<string, Slot> pair in r.Windows)
                {
                    Slot slot;
                    if (!merged.TryGetValue(pair.Key, out slot))
                    {
                        slot = new Slot { Source = r.Source, CapturedAt = r.CapturedAt };
                        merged[pair.Key] = slot;
                        order.Add(pair.Key);
                    }
                    if (!slot.Used.HasValue)
                    {
                        slot.Used = pair.Value.Used;
                        slot.CapturedAt = r.CapturedAt;
                        slot.Source = r.Source;
                    }
                    if (!slot.ResetEpoch.HasValue && pair.Value.ResetEpoch.HasValue)
                        slot.ResetEpoch = pair.Value.ResetEpoch;
                }

            // A refusal contributes its window even with no percentage for it:
            // "blocked until 01:50" is real, actionable state.
            foreach (KeyValuePair<string, Block> pair in blocks)
            {
                Slot slot;
                if (!merged.TryGetValue(pair.Key, out slot))
                {
                    slot = new Slot();
                    merged[pair.Key] = slot;
                    order.Add(pair.Key);
                }
                slot.Blocked = true;
                slot.ResetEpoch = pair.Value.ResetEpoch;
                if (!slot.CapturedAt.HasValue) slot.CapturedAt = pair.Value.ObservedAt;
                slot.Source = pair.Value.Source;
            }

            order.Sort((a, b) =>
            {
                int da = Model.KnownDuration(a) ?? int.MaxValue;
                int db = Model.KnownDuration(b) ?? int.MaxValue;
                return da != db ? da.CompareTo(db) : string.Compare(a, b, StringComparison.Ordinal);
            });

            double newest = 0;
            bool anyAvailable = false;
            foreach (string key in order)
            {
                Slot slot = merged[key];
                if (slot.CapturedAt.HasValue) newest = Math.Max(newest, slot.CapturedAt.Value);
                double? remaining = null;
                if (slot.Blocked) remaining = 0.0;
                else if (slot.Used.HasValue) remaining = 100.0 - slot.Used.Value;
                if (!remaining.HasValue) { acc.Windows.Add(ProbeWindow.Unavailable(key)); continue; }
                anyAvailable = true;
                acc.Windows.Add(new ProbeWindow
                {
                    Key = key,
                    DurationMinutes = Model.KnownDuration(key),
                    Available = true,
                    Remaining = remaining,
                    ResetEpoch = slot.ResetEpoch,
                    Source = slot.Source ?? "claude",
                });
            }

            if (!anyAvailable)
            {
                acc.Status = Model.UNAVAILABLE;
                acc.Error = "Claude has not supplied readable limits yet";
                return acc;
            }

            // Freshness is the NEWEST fact behind any rendered window: a live
            // refusal keeps the snapshot current even when every cache is old.
            double age = newest > 0 ? Math.Max(0.0, now - newest) : double.MaxValue;
            bool fresh = blocks.Count > 0 || age <= StaleAfterS;
            acc.Status = fresh ? Model.OK : Model.STALE;
            acc.Ok = fresh;
            if (!fresh) acc.Error = "last reading is " + (int)(age / 60) + "m old";
            return acc;
        }

        public class Slot
        {
            public double? Used, ResetEpoch, CapturedAt;
            public bool Blocked;
            public string Source;
        }

        class Reading
        {
            public Dictionary<string, Slot> Windows = new Dictionary<string, Slot>();
            public double CapturedAt;
            public string Source = "";
        }

        class Block
        {
            public double ResetEpoch, ObservedAt;
            public string Source = "";
        }

        // ── source 0: the CLI ────────────────────────────────────────────────
        // `claude -p "/usage"` is the only local source carrying BOTH the
        // percentage and the reset time, and it comes from Anthropic's own
        // endpoint. In print mode it answers with 0 turns / $0.00, so reading
        // the quota never spends any.
        static Reading CliUsage(double deadline, double now)
        {
            string exe = Cli.Resolve("claude");
            if (exe.Length == 0) return null;
            string session = Guid.NewGuid().ToString();
            string dir = ProbeDir();
            Cli.Result res = Cli.Run(exe,
                new[] { "-p", "/usage", "--output-format", "json", "--session-id", session },
                deadline, dir);
            DropTranscript(session, dir);
            if (!res.Ok) return null;
            object payload = J.Parse(res.Stdout);
            if (payload == null || J.Flag(J.Get(payload, "is_error"))) return null;
            string answer = J.Str(J.Get(payload, "result"));
            if (string.IsNullOrEmpty(answer)) return null;
            Dictionary<string, Slot> windows = ParseUsageText(answer, now);
            if (windows.Count == 0) return null;   // API-key account: no plan quota
            return new Reading { Windows = windows, CapturedAt = now, Source = "claude-cli-usage" };
        }

        // "Current session: 65% used · resets Sep 3, 4:49pm (Europe/Tallinn)"
        static readonly Regex LineRe = new Regex(
            @"^(?<title>[^:]+):\s*(?<pct>\d{1,3})%\s*used" +
            @"(?:\s*[\u00b7·\-]\s*resets\s*(?<when>[^(\n]+?)\s*(?:\((?<tz>[^)]*)\))?)?\s*$");

        // "Sep 3, 4:49pm" / "Sep 8, 5pm" / "Sep 3, 2027, 4:49pm"
        static readonly Regex WhenRe = new Regex(
            @"^(?<month>[A-Za-z]{3})\s+(?<day>\d{1,2})(?:,\s*(?<year>\d{4}))?" +
            @",\s*(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>[apAP][mM])$");

        static readonly string[] Months =
            { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        // The CLI renders the stamp in LOCAL time and appends the zone's name
        // for the reader, so it is parsed as naive local. An absent year means
        // "this year" — exactly the rule the CLI applies.
        public static double? ParseReset(string text, double now)
        {
            Match m = WhenRe.Match((text ?? "").Trim());
            if (!m.Success) return null;
            int month = Array.IndexOf(Months, m.Groups["month"].Value.ToLowerInvariant()) + 1;
            if (month == 0) return null;
            int hour = int.Parse(m.Groups["hour"].Value, CultureInfo.InvariantCulture) % 12;
            if (m.Groups["ampm"].Value.ToLowerInvariant() == "pm") hour += 12;
            DateTime reference = Stamp.Local(now);
            int year = m.Groups["year"].Success
                ? int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture) : reference.Year;
            int day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
            int minute = m.Groups["minute"].Success
                ? int.Parse(m.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
            try
            {
                var moment = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
                return Stamp.Of(moment.ToUniversalTime());
            }
            catch { return null; }
        }

        static readonly Regex ScopedWeeklyRe = new Regex(@"^current week \((?<scope>.+)\)$");

        // Nothing is inferred: a line that does not parse is dropped rather
        // than guessed at.
        public static Dictionary<string, Slot> ParseUsageText(string text, double now)
        {
            var windows = new Dictionary<string, Slot>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                Match m = LineRe.Match(raw.Trim().TrimEnd('\r'));
                if (!m.Success) continue;
                string title = Regex.Replace(m.Groups["title"].Value.Trim(), @"\s+", " ").ToLowerInvariant();
                string key;
                if (title == "current session") key = Model.FIVE_HOUR;
                else if (title == "current week (all models)") key = Model.WEEKLY;
                else if (title == "spend limit") key = "spend_limit";
                else
                {
                    Match scoped = ScopedWeeklyRe.Match(title);
                    if (!scoped.Success) continue;
                    // "Current week (Sonnet)" is a real per-model weekly pool,
                    // so it keeps its own key instead of overwriting the
                    // all-models weekly.
                    string slug = Regex.Replace(scoped.Groups["scope"].Value.ToLowerInvariant(),
                        "[^a-z0-9]+", "_").Trim('_');
                    key = slug.Length > 0 ? "weekly_" + slug : Model.WEEKLY;
                }
                double used = Math.Max(0.0, Math.Min(100.0,
                    double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture)));
                windows[key] = new Slot
                {
                    Used = used,
                    ResetEpoch = ParseReset(m.Groups["when"].Value, now),
                };
            }
            return windows;
        }

        // `claude` files its transcript under a slug derived from the working
        // directory, so probing from one fixed scratch directory keeps every
        // artefact in ONE predictable place.
        static string ProbeDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "limisaw-limit-probe");
            try { Directory.CreateDirectory(path); return path; }
            catch { return Path.GetTempPath(); }
        }

        // A quota read is not a conversation. Left alone, a 3-minute sweep
        // would file a new transcript every sweep forever — and those same
        // transcripts are what the refusal scanner reads.
        static void DropTranscript(string session, string directory)
        {
            string home = Home();
            if (home.Length == 0) return;
            string slug = Regex.Replace(Path.GetFullPath(directory), "[^A-Za-z0-9]", "-");
            try { File.Delete(Path.Combine(home, "projects", slug, session + ".jsonl")); }
            catch { }
        }

        // ── source 1: a status-line cache, if some bridge wrote one ──────────
        // Claude Code can be configured to send its structured `rate_limits`
        // block to a status-line command. LIMISAW never installs such a bridge
        // (that would edit the user's Claude settings behind their back), but
        // when one exists its cache is a free, exact reading.
        static readonly string[] CacheNames =
            { "limisaw-rate-limits.json", "fastprompter-rate-limits.json" };

        static Reading BridgeCache(out string error)
        {
            error = null;
            string home = Home();
            if (home.Length == 0) return null;
            foreach (string name in CacheNames)
            {
                string path = Path.Combine(home, name);
                if (!File.Exists(path)) continue;
                object payload;
                try { payload = J.Parse(File.ReadAllText(path)); }
                catch { error = "Claude limit cache is unreadable"; continue; }
                double? schema = J.Num(J.Get(payload, "schema_version"));
                if (!schema.HasValue || (int)schema.Value != 1)
                { error = "Claude limit cache has an unknown format"; continue; }
                Dictionary<string, object> limits = J.Obj(J.Get(payload, "rate_limits"));
                if (limits == null) { error = "Claude has not supplied rate limits yet"; continue; }
                var windows = new Dictionary<string, Slot>();
                var map = new Dictionary<string, string> {
                    { "five_hour", Model.FIVE_HOUR }, { "seven_day", Model.WEEKLY },
                    { "spend_limit", "spend_limit" },
                };
                foreach (KeyValuePair<string, string> pair in map)
                {
                    object bucket = J.Get(limits, pair.Key);
                    if (bucket == null) continue;
                    double? used = J.Num(J.Get(bucket, "used_percentage")) ?? J.Num(J.Get(bucket, "usedPercent"));
                    if (!used.HasValue) continue;
                    windows[pair.Value] = new Slot
                    {
                        Used = Math.Max(0.0, Math.Min(100.0, used.Value)),
                        ResetEpoch = Stamp.Epoch(J.Get(bucket, "resets_at") ?? J.Get(bucket, "resetsAt")),
                    };
                }
                if (windows.Count == 0) { error = "Claude has not supplied readable limits yet"; continue; }
                double? captured = J.Num(J.Get(payload, "captured_at"));
                return new Reading
                {
                    Windows = windows,
                    CapturedAt = captured.HasValue ? captured.Value : 0,
                    Source = name,
                };
            }
            return null;
        }

        // ── source 2: Claude Desktop's own usage sampler ─────────────────────
        // Claude Code's status line only runs when Claude Code renders one, so
        // a Desktop session can spend the account for hours with nothing else
        // to read. Desktop samples the same account every ~5 minutes into
        // plan-usage-history.json: `{"t": ms, "u": {"fh": 39, "sd": 20}}`.
        // It carries no reset time, which is why the CLI outranks it.
        public static string DesktopHistoryPath()
        {
            string appdata = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appdata)) return "";
            return Path.Combine(appdata, "Claude", "plan-usage-history.json");
        }

        const long DesktopMaxBytes = 8 * 1024 * 1024;

        static Reading DesktopSample(double now)
        {
            string path = DesktopHistoryPath();
            if (path.Length == 0 || !File.Exists(path)) return null;
            try { if (new FileInfo(path).Length > DesktopMaxBytes) return null; }
            catch { return null; }
            object payload;
            try { payload = J.Parse(File.ReadAllText(path)); }
            catch { return null; }
            var samples = new List<object>();
            foreach (object s in J.Arr(J.Get(payload, "samples"))) samples.Add(s);
            double bestAt = 0;
            var best = new Dictionary<string, Slot>();
            // The tail is chronological in practice; a max scan over the last
            // few hundred samples costs nothing and trusts no ordering.
            for (int i = Math.Max(0, samples.Count - 512); i < samples.Count; i++)
            {
                double? at = Stamp.Epoch(J.Get(samples[i], "t"));
                if (!at.HasValue) continue;
                Dictionary<string, object> raw = J.Obj(J.Get(samples[i], "u"));
                if (raw == null) continue;
                var windows = new Dictionary<string, Slot>();
                foreach (KeyValuePair<string, object> pair in raw)
                {
                    string key = DesktopKey(pair.Key);
                    double? used = J.Num(pair.Value);
                    if (key == null || !used.HasValue) continue;
                    windows[key] = new Slot { Used = Math.Max(0.0, Math.Min(100.0, used.Value)) };
                }
                if (windows.Count == 0) continue;
                if (at.Value > bestAt) { bestAt = at.Value; best = windows; }
            }
            if (bestAt <= 0) return null;
            // A stale sample is still returned — the caller marks the snapshot
            // STALE — but a sampler that stopped hours ago is not current truth.
            if (now - bestAt > DesktopStaleAfterS * 8) return null;
            return new Reading
            {
                Windows = best, CapturedAt = bestAt,
                Source = "claude-desktop-usage-history",
            };
        }

        static string DesktopKey(string shortName)
        {
            switch ((shortName ?? "").Trim().ToLowerInvariant())
            {
                case "fh": case "five_hour": return Model.FIVE_HOUR;
                case "sd": case "seven_day": return Model.WEEKLY;
                case "sl": case "spend_limit": return "spend_limit";
            }
            return null;
        }

        // ── source 3: the refusals Claude Code journals ──────────────────────
        // Every time the API refuses a request for quota reasons, Claude Code
        // writes a structured `quotaLimits` record into its own transcript.
        // That is the provider's own verdict: the window is spent until
        // `resetsAt`. Reading is bounded on purpose — newest transcripts only,
        // their tail only, lines mentioning the field only.
        static class Transcripts
        {
            const int TailBytes = 256 * 1024;
            const int MaxFiles = 8;
            const double MaxAgeS = 7 * 24 * 3600;

            public static Dictionary<string, Block> ActiveBlocks(string claudeDir, double now)
            {
                var blocks = new Dictionary<string, Block>();
                if (string.IsNullOrEmpty(claudeDir)) return blocks;
                foreach (string path in Recent(Path.Combine(claudeDir, "projects"), now))
                    foreach (string line in Tail(path))
                    {
                        if (line.IndexOf("quotaLimits", StringComparison.Ordinal) < 0) continue;
                        object record = J.Parse(line);
                        object quota = J.Get(record, "quotaLimits");
                        if (quota == null) continue;
                        string status = (J.Str(J.Get(quota, "status")) ?? "").Trim().ToLowerInvariant();
                        if (status != "rejected") continue;
                        double? resets = Stamp.Epoch(J.Get(quota, "resetsAt"));
                        // Once resetsAt passes the window is no longer blocked;
                        // reporting it would be a lie the moment the clock ticks.
                        if (!resets.HasValue || resets.Value <= now) continue;
                        string key = WindowKey(J.Str(J.Get(quota, "rateLimitType"))) ?? Model.FIVE_HOUR;
                        double observed = Stamp.Epoch(J.Get(record, "timestamp")) ?? now;
                        Block previous;
                        if (blocks.TryGetValue(key, out previous) && previous.ObservedAt >= observed) continue;
                        blocks[key] = new Block
                        {
                            ResetEpoch = resets.Value, ObservedAt = observed,
                            Source = "claude-code-transcript",
                        };
                    }
                return blocks;
            }

            static string WindowKey(string raw)
            {
                switch ((raw ?? "").Trim().ToLowerInvariant())
                {
                    case "five_hour": case "fivehour": case "5h": return Model.FIVE_HOUR;
                    case "seven_day": case "sevenday": case "weekly": case "week": return Model.WEEKLY;
                    case "monthly": case "month": case "thirty_day": return Model.MONTHLY;
                    case "spend_limit": return "spend_limit";
                    case "": return null;
                }
                return raw.Trim().ToLowerInvariant();
            }

            static List<string> Recent(string root, double now)
            {
                var found = new List<KeyValuePair<double, string>>();
                if (!Directory.Exists(root)) return new List<string>();
                string[] slugs;
                try { slugs = Directory.GetDirectories(root); } catch { return new List<string>(); }
                foreach (string slug in slugs)
                {
                    string[] names;
                    try { names = Directory.GetFiles(slug, "*.jsonl"); } catch { continue; }
                    foreach (string path in names)
                    {
                        double age;
                        try { age = now - Stamp.Of(File.GetLastWriteTimeUtc(path)); }
                        catch { continue; }
                        if (age > MaxAgeS) continue;
                        found.Add(new KeyValuePair<double, string>(-age, path));
                    }
                }
                found.Sort((a, b) => b.Key.CompareTo(a.Key));
                var paths = new List<string>();
                for (int i = 0; i < found.Count && i < MaxFiles; i++) paths.Add(found[i].Value);
                return paths;
            }

            // A transcript grows to tens of MB and the record we need is always
            // among the newest, so a tail read is both sufficient and the only
            // affordable option on a 3-minute timer.
            static string[] Tail(string path)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        long size = fs.Length;
                        bool partial = size > TailBytes;
                        if (partial) fs.Seek(size - TailBytes, SeekOrigin.Begin);
                        var buffer = new byte[Math.Min(size, TailBytes)];
                        int read = fs.Read(buffer, 0, buffer.Length);
                        string text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                        string[] lines = text.Split('\n');
                        if (partial && lines.Length > 1)
                        {
                            // The first line is the tail of a record whose head
                            // was never read.
                            var rest = new string[lines.Length - 1];
                            Array.Copy(lines, 1, rest, 0, rest.Length);
                            return rest;
                        }
                        return lines;
                    }
                }
                catch { return new string[0]; }
            }
        }
    }
}
