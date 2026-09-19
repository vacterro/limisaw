using System;
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

        static string Profile()
        {
            return Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME") ?? "";
        }

        // The DEFAULT home. Not "the account" any more - see Homes().
        public static string Home()
        {
            string profile = Profile();
            return profile.Length == 0 ? "" : Path.Combine(profile, ".claude");
        }

        // A discovered home with a stable id. `Path` is the exact canonical
        // config directory, `Name` is display text only. Two homes may share a
        // name (CLAUDE_CONFIG_DIR -> "Claude" and the default .claude ->
        // "Claude"); only `Id` distinguishes them.
        public class ClaudeHome
        {
            public string Path, Name, Id;
            public bool IsDefaultHome;
            public ClaudeHome(string path, string name, string id, bool isDefault)
            { Path = path; Name = name; Id = id; IsDefaultHome = isDefault; }
        }

        // CORE-001 for Claude: ONE CONFIG DIRECTORY IS ONE ACCOUNT.
        //
        // Claude Code keeps a whole account inside its config directory -
        // credentials, settings, and the transcripts the refusal scanner reads -
        // and CLAUDE_CONFIG_DIR is the CLI's own switch for which directory that
        // is. Running a second subscription on one machine is therefore a second
        // directory, which is exactly the shape Codex homes already have here.
        // Before this, Claude was hardcoded to %USERPROFILE%\.claude: a user
        // with two accounts saw ONE card, and it was whichever account the
        // default home happened to hold.
        //
        // Listing only: no subprocess, no credential read. The default home and
        // an explicit CLAUDE_CONFIG_DIR are admitted on existence alone (that is
        // what Installed() has always meant here); a `.claude-*` sibling must
        // ALSO look like a config directory, so a stray backup folder cannot
        // mint a phantom account.
        static readonly string[] HomeMarkers =
        {
            ".credentials.json", "settings.json", "settings.local.json",
            "projects", "statsig", "history.jsonl",
        };

        public static List<ClaudeHome> Homes()
        {
            var found = new List<ClaudeHome>();
            var seen = new List<string>();
            string profile = Profile();
            string defaultHome = profile.Length > 0 ? Path.Combine(profile, ".claude") : "";
            string defaultNorm = Norm(defaultHome);

            Action<string, bool> add = (dir, markerRequired) =>
            {
                if (string.IsNullOrEmpty(dir)) return;
                string full;
                try { full = Path.GetFullPath(dir); } catch { return; }
                string norm = Norm(full);
                if (norm.Length == 0 || seen.Contains(norm)) return;
                // ~/.claude.json without the directory is still an installation:
                // that is the pre-existing Installed() rule, kept exactly.
                bool exists;
                try { exists = Directory.Exists(full) || File.Exists(full + ".json"); }
                catch { return; }
                if (!exists) return;
                if (markerRequired && !HasMarker(full)) return;
                seen.Add(norm);
                found.Add(new ClaudeHome(full, LabelFor(full), HomeId(full), norm == defaultNorm));
            };

            // A Windows path cannot contain ';', so a list is unambiguous - and
            // it is how a user names two accounts that are NOT ~/.claude-*
            // siblings.
            string env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(env))
                foreach (string part in env.Split(';'))
                    add(part.Trim(), false);
            add(defaultHome, false);

            string[] siblings;
            try { siblings = profile.Length > 0 ? Directory.GetDirectories(profile, ".claude-*") : new string[0]; }
            catch { siblings = new string[0]; }
            Array.Sort(siblings, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in siblings) add(dir, true);

            DisambiguateLabels(found);
            return found;
        }

        // Does this directory hold an account yet? The same question
        // discovery asks, so the connection adapter cannot answer it
        // differently from the sweep.
        internal static bool LooksLikeHome(string dir)
        {
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) && HasMarker(dir);
        }

        static bool HasMarker(string dir)
        {
            foreach (string marker in HomeMarkers)
            {
                string path = Path.Combine(dir, marker);
                try { if (File.Exists(path) || Directory.Exists(path)) return true; }
                catch { }
            }
            return false;
        }

        // Display text only. ".claude" is "Claude", ".claude-work" is "Work",
        // any other directory is named after itself - the label is never
        // identity, so being wrong here costs a nicer word and nothing more.
        static string LabelFor(string full)
        {
            string leaf = Path.GetFileName(full.TrimEnd('\\', '/'));
            if (leaf.Length == 0) return "Claude";
            string name = leaf;
            if (name.StartsWith(".claude-", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(".claude-".Length);
            else if (string.Equals(name, ".claude", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(name, "claude", StringComparison.OrdinalIgnoreCase))
                return "Claude";
            else if (name.StartsWith(".")) name = name.Substring(1);
            name = name.Replace("_", " ").Replace("-", " ").Trim();
            return name.Length > 0 ? TitleCase(name) : "Claude";
        }

        // The first letter after any non-letter is capitalised, so
        // ".claude-account2_free" reads "Account2 Free".
        static string TitleCase(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            bool boundary = true;
            foreach (char c in text)
            {
                sb.Append(boundary ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                boundary = !char.IsLetter(c);
            }
            return sb.ToString();
        }

        // Two "Claude" cards are unusable and look broken. The label is NOT the
        // identity - this is display-only, applied uniformly to every duplicate
        // so it never reads as "the first one is the real Claude".
        static void DisambiguateLabels(List<ClaudeHome> homes)
        {
            // The collision is decided on the ORIGINAL labels. Renaming as the
            // walk goes would hide the second half of every pair: once the first
            // is renamed the second no longer looks like a duplicate, and it
            // keeps the bare name as if it were the real Claude.
            var original = new List<string>();
            foreach (ClaudeHome h in homes) original.Add(h.Name);
            for (int i = 0; i < homes.Count; i++)
            {
                var group = new List<int>();
                for (int j = 0; j < homes.Count; j++)
                    if (original[j] == original[i]) group.Add(j);
                if (group.Count < 2) continue;
                homes[i].Name = original[i] + " · " + Distinguisher(homes, group, i);
            }
        }

        // The last path segment the colliding group does NOT share. Two accounts
        // are usually both in a directory called ".claude", so appending that
        // leaf would say nothing at all - the walk climbs until the segments
        // actually differ.
        static string Distinguisher(List<ClaudeHome> homes, List<int> group, int self)
        {
            string[] mine = Segments(homes[self].Path);
            for (int depth = 0; depth < mine.Length; depth++)
            {
                string segment = mine[mine.Length - 1 - depth];
                bool unique = true;
                foreach (int j in group)
                {
                    if (j == self) continue;
                    string[] other = Segments(homes[j].Path);
                    string theirs = depth < other.Length ? other[other.Length - 1 - depth] : "";
                    if (string.Equals(theirs, segment, StringComparison.OrdinalIgnoreCase))
                    { unique = false; break; }
                }
                if (unique) return segment;
            }
            return homes[self].Path;
        }

        static string[] Segments(string path)
        {
            return (path ?? "").TrimEnd('\\', '/')
                .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // A 64-bit digest of the canonical home path. Identical on every run;
        // distinct for distinct homes; never derived from the display name.
        static string HomeId(string full)
        {
            byte[] bytes = System.Security.Cryptography.SHA256.Create()
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(Norm(full)));
            var sb = new System.Text.StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        static string Norm(string path)
        {
            return (path ?? "").TrimEnd('\\', '/').ToLowerInvariant();
        }

        // The home a plain "Claude" means: the default one, else the first
        // discovered, else nothing at all.
        public static ClaudeHome DefaultHome()
        {
            List<ClaudeHome> homes = Homes();
            foreach (ClaudeHome h in homes) if (h.IsDefaultHome) return h;
            return homes.Count > 0 ? homes[0] : null;
        }

        // Desktop alone still counts: its sampler is real even when no CLI
        // config directory exists on disk yet.
        public static bool Installed()
        {
            if (Homes().Count > 0) return true;
            string history = DesktopHistoryPath();
            return history.Length > 0 && File.Exists(history);
        }

        // One card per home, inside the vendor's share of the sweep. The share
        // is split evenly: the CLI read is one child process per home, and a
        // slow first account must never eat the second account's budget - that
        // is the exact failure W2-001 fixed between providers.
        public static List<ProbeAccount> Sweep(double deadline, double share)
        {
            var accounts = new List<ProbeAccount>();
            List<ClaudeHome> homes = Homes();
            if (homes.Count == 0)
            {
                // No config directory, but Desktop's history exists: that is one
                // real account, read exactly as it was before.
                accounts.Add(Probe(deadline));
                return accounts;
            }
            double start = Stamp.Now;
            double slice = Math.Max(0.2, (deadline - start) / homes.Count);
            for (int i = 0; i < homes.Count; i++)
            {
                double end = Math.Min(deadline, start + slice * (i + 1));
                if (end - Stamp.Now <= 0.2) { accounts.Add(OutOfTime(homes[i])); continue; }
                accounts.Add(Probe(homes[i], end, homes[i].IsDefaultHome || homes.Count == 1));
            }
            return accounts;
        }

        // A home the sweep never reached. Reported as its own stale card, so the
        // UI carries the last known numbers forward instead of the account
        // silently vanishing from the list.
        static ProbeAccount OutOfTime(ClaudeHome home)
        {
            var acc = new ProbeAccount
            {
                Provider = "claude", ProviderLabel = "Claude Code", Name = home.Name,
                SourceId = home.Id, ResetHome = home.Path,
                Status = Model.STALE, Ok = false,
                Error = "the sweep ran out of time before this account",
            };
            acc.Windows.Add(ProbeWindow.Unavailable(Model.FIVE_HOUR));
            acc.Windows.Add(ProbeWindow.Unavailable(Model.WEEKLY));
            return acc;
        }

        // The single-account entry point, kept whole: the default home, with
        // Desktop's sampler allowed.
        public static ProbeAccount Probe(double deadline)
        {
            ClaudeHome home = DefaultHome();
            if (home == null)
            {
                string path = Home();
                home = new ClaudeHome(path, "Claude", HomeId(path), true);
            }
            return Probe(home, deadline, true);
        }

        // `allowDesktop` is not a preference. Claude Desktop samples the ONE
        // account it is signed into, so its history is evidence for the default
        // home only - attaching it to a second home would invent a reading for
        // an account nothing measured.
        public static ProbeAccount Probe(ClaudeHome home, double deadline, bool allowDesktop)
        {
            double now = Stamp.Now;
            var acc = new ProbeAccount
            {
                Provider = "claude", ProviderLabel = "Claude Code", Name = home.Name,
                SourceId = home.Id, ResetHome = home.Path,
            };

            var readings = new List<Reading>();
            string cliError;
            Reading cli = CliUsage(home.Path, deadline, now, out cliError);
            if (cli != null) readings.Add(cli);
            string bridgeError = null;
            Reading bridge = BridgeCache(home.Path, out bridgeError);
            if (bridge != null) readings.Add(bridge);
            Reading desktop = allowDesktop ? DesktopSample(now) : null;
            if (desktop != null) readings.Add(desktop);

            // An explicit refusal in Claude Code's own transcript beats any
            // percentage: that window is spent until it resets. It is also the
            // only directory-walking read here, so the deadline is threaded all
            // the way in (PERF-005) rather than only guarding the entry.
            var blocks = new Dictionary<string, Block>();
            bool scanCut = false;
            if (Stamp.Now < deadline)
            {
                blocks = Transcripts.ActiveBlocks(home.Path, now, deadline);
                scanCut = Transcripts.DeadlineHit;
            }
            else scanCut = true;

            if (readings.Count == 0 && blocks.Count == 0)
            {
                acc.Status = Model.UNAVAILABLE;
                // PERF-005: a transcript scan the deadline cut short is not
                // evidence of anything. Saying "Claude has not supplied rate
                // limits yet" for a journal that was never read tells a blocked
                // user they are merely idle.
                acc.Error = scanCut && string.IsNullOrEmpty(cliError) && string.IsNullOrEmpty(bridgeError)
                    ? "the Claude transcript scan ran out of sweep time"
                    : Summary(cliError, bridgeError);
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

        public class Reading
        {
            public Dictionary<string, Slot> Windows = new Dictionary<string, Slot>();
            public double CapturedAt;
            public string Source = "";
        }

        internal class Block
        {
            public double ResetEpoch, ObservedAt;
            public string Source = "";
        }

        // Which sentence the card gets when no source produced a reading.
        //
        // The CLI's own words win. It is the primary source and the only one that
        // can say WHY: "Not logged in" is a thing the user can fix, while
        // "has not supplied rate limits yet" reads as "nothing has run yet" and
        // sends them to wait instead of to re-auth. The bridge's complaint is
        // second because it only describes an optional status-line cache.
        public static string Summary(string cliError, string bridgeError)
        {
            if (!string.IsNullOrEmpty(cliError)) return cliError;
            if (!string.IsNullOrEmpty(bridgeError)) return bridgeError;
            return "Claude has not supplied rate limits yet";
        }

        // ── source 0: the CLI ────────────────────────────────────────────────
        // `claude -p "/usage"` is the only local source carrying BOTH the
        // percentage and the reset time, and it comes from Anthropic's own
        // endpoint. In print mode it answers with 0 turns / $0.00, so reading
        // the quota never spends any.
        //
        // `error` is the reason the CLI could not answer, or null when there was
        // nothing to ask (not installed) — that is not a failure to report, the
        // CLIs tab already says which vendors are missing.
        // `internal` so the connection adapter can classify the SAME read
        // (SRC-002 R068: the adapter never forks the quota parser).
        internal static Reading CliUsage(double deadline, double now, out string error)
        {
            ClaudeHome home = DefaultHome();
            return CliUsage(home != null ? home.Path : Home(), deadline, now, out error);
        }

        // `home` IS the account: the CLI reads its whole identity from
        // CLAUDE_CONFIG_DIR, so scoping the child is what makes a second
        // subscription readable at all. It is set on the CHILD only - this
        // process's own environment is never touched, so nothing else in the
        // sweep, and nothing in the user's shell, is affected.
        internal static Reading CliUsage(string home, double deadline, double now, out string error)
        {
            error = null;
            string exe = Cli.Resolve("claude");
            if (exe.Length == 0) return null;
            string session = Guid.NewGuid().ToString();
            string dir = ProbeDir();
            var env = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(home)) env["CLAUDE_CONFIG_DIR"] = home;
            Cli.Result res = Cli.Run(exe,
                new[] { "-p", "/usage", "--output-format", "json", "--session-id", session },
                deadline, dir, env);
            DropTranscript(home, session, dir);
            if (!res.Ok) { error = "claude /usage: " + res.Error; return null; }
            object payload = J.Parse(res.Stdout);
            if (payload == null || J.Flag(J.Get(payload, "is_error")))
            { error = "claude /usage reported an error"; return null; }
            string answer = J.Str(J.Get(payload, "result"));
            if (string.IsNullOrEmpty(answer))
            { error = "claude /usage returned no text"; return null; }
            Dictionary<string, Slot> windows = ParseUsageText(answer, now);
            // An API-key (non-subscription) account has no plan quota and the CLI
            // answers with a cost summary instead. That is not a fault, but it is
            // still the reason the card is empty, so it is worth saying.
            if (windows.Count == 0)
            { error = "claude /usage reported no subscription limits"; return null; }
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
        // for the reader, so it is parsed as naive local.
        //
        // CORE-013: an explicit year is the vendor's own text and wins exactly
        // as printed. An ABSENT year is not "this year": the CLI omits the year
        // only because a reset is always within days, so around New Year that
        // reading built Jan 1 in the year that was ENDING — a reset a year in
        // the past, which ApplyElapsedResets then promoted into a false full
        // refill. The honest contract is the NEAREST plausible occurrence: the
        // month/day/time is built for reference.Year-1, reference.Year and
        // reference.Year+1 in local time (non-leap Feb 29 candidates simply do
        // not exist as dates and are dropped by the calendar itself), and the
        // candidate closest to now wins. "Always future" would be a different,
        // wrong rule: a stamp from just after midnight belongs to the recent
        // Dec 31, not to the one eleven months out.
        public static double? ParseReset(string text, double now)
        {
            Match m = WhenRe.Match((text ?? "").Trim());
            if (!m.Success) return null;
            int month = Array.IndexOf(Months, m.Groups["month"].Value.ToLowerInvariant()) + 1;
            if (month == 0) return null;
            int hour = int.Parse(m.Groups["hour"].Value, CultureInfo.InvariantCulture) % 12;
            if (m.Groups["ampm"].Value.ToLowerInvariant() == "pm") hour += 12;
            DateTime reference = Stamp.Local(now);
            int day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
            int minute = m.Groups["minute"].Success
                ? int.Parse(m.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
            if (m.Groups["year"].Success)
            {
                int year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
                try
                {
                    var moment = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
                    return Stamp.Of(moment.ToUniversalTime());
                }
                catch { return null; }
            }
            double? best = null;
            double bestGap = double.MaxValue;
            for (int y = reference.Year - 1; y <= reference.Year + 1; y++)
            {
                DateTime moment;
                try { moment = new DateTime(y, month, day, hour, minute, 0, DateTimeKind.Local); }
                catch { continue; }          // e.g. Feb 29 in a non-leap year
                double epoch = Stamp.Of(moment.ToUniversalTime());
                double gap = Math.Abs(epoch - now);
                if (gap < bestGap) { bestGap = gap; best = epoch; }
            }
            return best;
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
        static void DropTranscript(string home, string session, string directory)
        {
            if (string.IsNullOrEmpty(home)) return;
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

        static Reading BridgeCache(string home, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(home)) return null;
            foreach (string name in CacheNames)
            {
                string path = Path.Combine(home, name);
                // R028 with two homes: the memo is keyed by the FULL path, never
                // by the filename. Both homes may carry
                // "limisaw-rate-limits.json", and a filename key would serve one
                // account's quota as the other's.
                string memo = Norm(path);
                if (!File.Exists(path)) { lock (BridgeLock) BridgeHits.Remove(memo); continue; }
                // PERF-005 completion (R028): the parse is memoized by canonical
                // path + LastWriteTimeUtc + length — a sweep that finds the same
                // file unchanged re-reads NOTHING. The mtime is only an
                // invalidation signal: CapturedAt inside the JSON remains the
                // quota timestamp authority, and the staleness gate is re-applied
                // by the caller on every use, cached or not.
                var info = new FileInfo(path);
                BridgeEntry hit;
                lock (BridgeLock) BridgeHits.TryGetValue(memo, out hit);
                if (hit == null || hit.Mtime != info.LastWriteTimeUtc.Ticks || hit.Length != info.Length)
                {
                    // R030: the bridge file is a LOCAL file the user's own tooling
                    // writes, but a path is still unbounded input — refuse to
                    // ReadAllText anything past the documented ceiling (the same
                    // 8 MiB policy Desktop's history already follows). A quota
                    // object is kilobytes; 8 MiB is headroom, not a fixture-sized
                    // guess. Oversized means an explicit bounded error, never a
                    // partial parse and never stale cached truth presented as
                    // fresh.
                    if (info.Length > BridgeMaxBytes)
                    {
                        error = "Claude limit cache is too large ("
                            + (info.Length / 1024) + " KiB > " + (BridgeMaxBytes / 1024) + " KiB)";
                        continue;
                    }
                    // W2-005: the open handle is the authority. A producer that
                    // grows the file past the cap after the metadata check above
                    // is refused WHOLE here, never parsed as a prefix.
                    long bytesRead;
                    string readError;
                    string text = BoundedFile.ReadAllText(path, BridgeMaxBytes, out bytesRead, out readError);
                    if (text == null)
                    {
                        error = readError == "too large"
                            ? "Claude limit cache is too large (> " + (BridgeMaxBytes / 1024) + " KiB)"
                            : "Claude limit cache is unreadable";
                        continue;
                    }
                    object payload = J.Parse(text);
                    if (payload == null) { error = "Claude limit cache is unreadable"; continue; }
                    BridgeParses++;
                    BridgeBytesRead += bytesRead;
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
                    hit = new BridgeEntry
                    {
                        Mtime = info.LastWriteTimeUtc.Ticks,
                        Length = bytesRead,
                        Windows = windows,
                        CapturedAt = captured.HasValue ? captured.Value : 0,
                        Source = name,
                    };
                    lock (BridgeLock) BridgeHits[memo] = hit;
                }
                // The cache is immutable normalized truth; the merge gets its own
                // Slot copies so probe logic can never mutate cached state.
                var copy = new Dictionary<string, Slot>();
                foreach (KeyValuePair<string, Slot> s in hit.Windows)
                    copy[s.Key] = new Slot { Used = s.Value.Used, ResetEpoch = s.Value.ResetEpoch };
                return new Reading
                {
                    Windows = copy,
                    CapturedAt = hit.CapturedAt,
                    Source = hit.Source,
                };
            }
            return null;
        }

        // The memoized bridge parse: one entry per supported bridge filename,
        // keyed by path+mtime+length. Internal counters let the harness prove
        // zero reread unchanged / exactly one reparse changed without any UI
        // surface.
        class BridgeEntry
        {
            public double Mtime;
            public long Length;
            public Dictionary<string, Slot> Windows;
            public double CapturedAt;
            public string Source;
        }
        static readonly object BridgeLock = new object();
        static readonly Dictionary<string, BridgeEntry> BridgeHits = new Dictionary<string, BridgeEntry>();
        const long BridgeMaxBytes = 8 * 1024 * 1024;    // same policy as Desktop's history file
        internal static long BridgeMaxBytesForTests { get { return BridgeMaxBytes; } }
        internal static long BridgeParses, BridgeBytesRead;

        internal static void ResetBridgeCacheForTests()
        {
            lock (BridgeLock) BridgeHits.Clear();
            BridgeParses = 0;
            BridgeBytesRead = 0;
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

        // PERF-005: Desktop's sampler writes about every five minutes, and the
        // refresh timer goes down to sixty seconds — so an 8 MiB history file was
        // read and fully parsed several times between two producer updates. The
        // parse is cached by canonical path + mtime + length; the mtime is ONLY
        // an invalidation signal, never the quota timestamp. CapturedAt still
        // comes from the sample's own `t`, and the staleness gate is re-applied
        // against the caller's `now` on every call, cached or not.
        class DesktopEntry
        {
            public string Path;
            public double Mtime;
            public long Length;
            public Dictionary<string, Slot> Windows;
            public double CapturedAt;
        }
        static DesktopEntry DesktopHit;
        internal static long DesktopParses, DesktopBytesRead;
        internal static void ResetDesktopCache()
        {
            DesktopHit = null; DesktopParses = 0; DesktopBytesRead = 0;
        }

        static Reading DesktopSample(double now)
        {
            string path = DesktopHistoryPath();
            if (path.Length == 0 || !File.Exists(path)) return null;
            double mtime;
            long length;
            try
            {
                var info = new FileInfo(path);
                // W2-005: metadata is only the fast refusal; the bounded read
                // below is the real cap, so a file that grows after this stat
                // still cannot be read past DesktopMaxBytes.
                if (info.Length > DesktopMaxBytes) return null;
                length = info.Length;
                mtime = Stamp.Of(info.LastWriteTimeUtc);
            }
            catch { return null; }

            DesktopEntry hit = DesktopHit;
            if (hit != null && hit.Path == path && hit.Mtime == mtime && hit.Length == length)
                return DesktopReading(hit, now);

            long bytesRead;
            string readError;
            string text = BoundedFile.ReadAllText(path, DesktopMaxBytes, out bytesRead, out readError);
            if (text == null) return null;
            object payload = J.Parse(text);
            if (payload == null) return null;
            DesktopParses++;
            DesktopBytesRead += bytesRead;
            double bestAt = 0;
            var best = new Dictionary<string, Slot>();
            var samples = new List<object>();
            foreach (object s in J.Arr(J.Get(payload, "samples"))) samples.Add(s);
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
            DesktopHit = new DesktopEntry
            {
                Path = path, Mtime = mtime, Length = bytesRead,
                Windows = best, CapturedAt = bestAt,
            };
            return DesktopReading(DesktopHit, now);
        }

        // The time-dependent half, applied on every call so a cached parse can
        // never freeze a verdict about staleness. A stale sample is still
        // returned — the caller marks the snapshot STALE — but a sampler that
        // stopped hours ago is not current truth.
        static Reading DesktopReading(DesktopEntry entry, double now)
        {
            if (now - entry.CapturedAt > DesktopStaleAfterS * 8) return null;
            // A fresh Slot per call: the merge in Probe writes into these.
            var windows = new Dictionary<string, Slot>();
            foreach (KeyValuePair<string, Slot> pair in entry.Windows)
                windows[pair.Key] = new Slot { Used = pair.Value.Used };
            return new Reading
            {
                Windows = windows, CapturedAt = entry.CapturedAt,
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
        internal static class Transcripts
        {
            const int TailBytes = 256 * 1024;
            const int MaxFiles = 8;
            const double MaxAgeS = 7 * 24 * 3600;

            // PERF-005: the deadline used to be an ENTRY condition — checked once
            // before the walk, then ignored while the walk stat'ed every *.jsonl
            // under every project directory. A large accumulated tree therefore
            // carried filesystem work past the provider's whole budget. It is now
            // a bound on the scan itself, and a scan it cut short says so instead
            // of returning the same empty dictionary a healthy account returns.
            internal static bool DeadlineHit;
            internal static long StatCalls, TailReads;

            // The walk asks its own clock so a test can prove the deadline binds
            // INSIDE the walk and not merely at its entry — an entry-only check
            // is exactly the defect PERF-005 reports, and it passes any test
            // that hands it an already-expired budget.
            internal static Func<double> Clock = null;
            static double NowS() { Func<double> c = Clock; return c != null ? c() : Stamp.Now; }

            internal static Dictionary<string, Block> ActiveBlocks(string claudeDir, double now, double deadline)
            {
                DeadlineHit = false;
                var blocks = new Dictionary<string, Block>();
                if (string.IsNullOrEmpty(claudeDir)) return blocks;
                foreach (string path in Recent(Path.Combine(claudeDir, "projects"), now, deadline))
                {
                    if (NowS() >= deadline) { DeadlineHit = true; break; }
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

            // Discovery is metadata-only, but on a machine with hundreds of
            // project directories the stat calls alone are the cost — so the
            // deadline bounds this walk too, and a cut walk is reported rather
            // than silently returning whatever it happened to reach.
            //
            // PERF-002: the walk is LAZY (EnumerateDirectories/EnumerateFiles)
            // and keeps a bounded newest-K candidate set (K = MaxFiles) WHILE
            // walking. The old shape materialized the whole GetDirectories and
            // GetFiles arrays before any filter ran, so a tree with tens of
            // thousands of historical entries paid the full allocation and
            // stat bill before the cap could save anything. Enumeration order
            // is not freshness order, so selection is bounded-worst-eviction,
            // never Take(MaxFiles).
            internal static Func<string, IEnumerable<string>> EnumerateDirs =
                root => Directory.EnumerateDirectories(root);
            internal static Func<string, string, IEnumerable<string>> EnumerateFiles =
                (dir, pattern) => Directory.EnumerateFiles(dir, pattern);
            internal static int DirVisits, FileVisits;
            internal static void ResetCounters() { StatCalls = 0; TailReads = 0; DeadlineHit = false; DirVisits = 0; FileVisits = 0; }

            static List<string> Recent(string root, double now, double deadline)
            {
                var best = new List<KeyValuePair<double, string>>(MaxFiles);
                if (!Directory.Exists(root)) return best.ConvertAll(kv => kv.Value);
                IEnumerable<string> slugs;
                try { slugs = EnumerateDirs(root); }
                catch { return best.ConvertAll(kv => kv.Value); }
                foreach (string slug in slugs)
                {
                    DirVisits++;
                    if (NowS() >= deadline) { DeadlineHit = true; break; }
                    IEnumerable<string> names;
                    try { names = EnumerateFiles(slug, "*.jsonl"); }
                    catch { continue; }
                    foreach (string path in names)
                    {
                        FileVisits++;
                        if (NowS() >= deadline) { DeadlineHit = true; break; }
                        double age;
                        try { StatCalls++; age = now - Stamp.Of(File.GetLastWriteTimeUtc(path)); }
                        catch { continue; }
                        if (age > MaxAgeS) continue;
                        // Bounded newest-first selection: the oldest kept
                        // candidate dies when a fresher one arrives.
                        if (best.Count == MaxFiles)
                        {
                            int worst = 0;
                            for (int i = 1; i < best.Count; i++) if (best[i].Key < best[worst].Key) worst = i;
                            if (-age <= best[worst].Key) continue;
                            best.RemoveAt(worst);
                        }
                        best.Add(new KeyValuePair<double, string>(-age, path));
                    }
                    if (DeadlineHit) break;
                }
                best.Sort((a, b) => b.Key.CompareTo(a.Key));
                return best.ConvertAll(kv => kv.Value);
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
                        TailReads++;
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
