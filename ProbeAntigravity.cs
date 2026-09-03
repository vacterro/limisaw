using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

// Antigravity quota. The CLI states it exactly; the IDE's refusal journal
// proves a block when the CLI is absent. Nothing in between is estimated —
// without either source the answer is "unknown", never "free".
namespace Limisaw
{
    static class AntigravitySource
    {
        // Antigravity keeps its data under ~/.gemini/antigravity, not
        // ~/.antigravity (that holds only argv.json and extensions).
        public static string DataDir()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, ".gemini", "antigravity");
        }

        public static bool Installed()
        {
            string dir = DataDir();
            return dir.Length > 0 && (Directory.Exists(dir) || Cli.Resolve("antigravity").Length > 0);
        }

        // How long after a block's reset the refilled window is still reported,
        // so the reset alert has a chance to fire before the state honestly
        // becomes unknown again.
        // ponytail: fixed 1h grace, not a setting — nothing has asked for one.
        const double ResetGraceS = 3600.0;

        public static ProbeAccount Probe(double deadline)
        {
            var acc = new ProbeAccount
            {
                Provider = "antigravity", ProviderLabel = "Antigravity", Name = "Antigravity",
            };
            if (Stamp.Now >= deadline)
                return Unavailable(acc, "deadline_exceeded",
                    "no time left in this sweep to read Antigravity's quota");

            List<ProbeWindow> windows = CliWindows(deadline);
            if (windows != null && windows.Count > 0)
            {
                acc.Status = Model.OK; acc.Ok = true; acc.Windows = windows;
                return acc;
            }
            return Journal(acc);
        }

        static ProbeAccount Unavailable(ProbeAccount acc, string code, string summary)
        {
            acc.Status = Model.UNAVAILABLE;
            acc.Ok = false;
            acc.Quiet = Model.Quiet(code);
            acc.Error = summary;
            acc.Windows.Add(ProbeWindow.Unavailable("quota"));
            return acc;
        }

        // ── source 0: the CLI ────────────────────────────────────────────────
        // `agy -p "/usage" --output-format json` answers without starting an
        // agent turn, and returns the server's own numbers:
        //
        //   {"command":{"data":{"groups":[
        //     {"name":"Gemini Models","buckets":[
        //       {"window":"weekly","remaining_fraction":0.681,"reset_time":"..."},
        //       {"window":"5h","remaining_fraction":0.096,"reset_time":"..."}]},
        //     {"name":"Claude and GPT models","buckets":[
        //       {"window":"weekly","remaining_fraction":0,"reset_time":"..."},
        //       {"window":"5h","disabled":true,"remaining_fraction":1}]}]}}}
        //
        // The groups are INDEPENDENT pools — "within each group, models share a
        // weekly limit and a 5-hour limit" — so every window carries its group
        // and gating never crosses pools.
        static List<ProbeWindow> CliWindows(double deadline)
        {
            string exe = Cli.Resolve("antigravity");
            if (exe.Length == 0) return null;
            Cli.Result res = Cli.Run(exe,
                new[] { "-p", "/usage", "--output-format", "json" }, deadline, null);
            if (!res.Ok) return null;
            object payload = J.Parse(res.Stdout);
            if (payload == null) return null;
            string status = J.Str(J.Get(payload, "status"));
            if (!string.IsNullOrEmpty(status) && status != "SUCCESS") return null;
            List<Row> rows = ParseUsagePayload(payload);
            if (rows.Count == 0) return null;
            return WindowsFrom(rows, "antigravity-cli-usage");
        }

        public class Row
        {
            public string Key = "", Group = "", GroupLabel = "";
            public double? Remaining;
            public double? ResetEpoch;
            public int? DurationMinutes;
            public bool Disabled;
        }

        static string WindowKey(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "5h": case "five_hour": return Model.FIVE_HOUR;
                case "weekly": case "7d": return Model.WEEKLY;
                case "monthly": case "30d": return Model.MONTHLY;
            }
            return null;
        }

        // One record per quota window. An unknown window name or a bucket with
        // no readable fraction is dropped: a pool that cannot state a number is
        // not rendered as free.
        //
        // A `disabled` bucket IS kept, flagged and without a number. It is a
        // window the pool really has (the Claude/GPT 5-hour limit exists whether
        // or not it currently applies), but its reported fraction is meaningless:
        // Antigravity disables it exactly while that pool's weekly limit is
        // spent, and still reports remaining_fraction 1. Trusting that showed
        // "100% free" on a pool that refuses every request; dropping the bucket
        // hid the limit entirely.
        public static List<Row> ParseUsagePayload(object payload)
        {
            var rows = new List<Row>();
            object data = J.Get(J.Get(payload, "command"), "data");
            object groups = J.Get(data, "groups");
            if (groups == null) return rows;
            int index = 0;
            foreach (object group in J.Arr(groups))
            {
                index++;
                if (J.Obj(group) == null) continue;
                string label = (J.Str(J.Get(group, "name")) ?? ("group " + index)).Trim();
                string id = Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
                if (id.Length == 0) id = "group" + index;
                foreach (object bucket in J.Arr(J.Get(group, "buckets")))
                {
                    if (J.Obj(bucket) == null) continue;
                    string key = WindowKey(J.Str(J.Get(bucket, "window")));
                    if (key == null) continue;
                    bool disabled = J.Flag(J.Get(bucket, "disabled"));
                    double? fraction = J.Num(J.Get(bucket, "remaining_fraction"));
                    double? remaining = fraction.HasValue
                        ? Math.Max(0.0, Math.Min(100.0, fraction.Value * 100.0)) : (double?)null;
                    if (!remaining.HasValue && !disabled) continue;
                    rows.Add(new Row
                    {
                        Key = key, Group = id, GroupLabel = label,
                        Remaining = disabled ? (double?)null : remaining,
                        ResetEpoch = Stamp.Epoch(J.Get(bucket, "reset_time")),
                        DurationMinutes = Model.KnownDuration(key),
                        Disabled = disabled,
                    });
                }
            }
            return rows;
        }

        // A disabled bucket reads 0% when its own pool's longer window is spent
        // — the same answer gating would give if the number were readable — and
        // stays "--" otherwise, because there is nothing to infer.
        public static List<ProbeWindow> WindowsFrom(List<Row> rows, string source)
        {
            var spent = new List<KeyValuePair<string, int>>();
            foreach (Row row in rows)
            {
                if (row.Disabled) continue;
                double remaining = row.Remaining ?? 0.0;
                if (remaining > Model.ZeroRemaining) continue;
                spent.Add(new KeyValuePair<string, int>(row.Group,
                    row.DurationMinutes ?? int.MaxValue));
            }

            var windows = new List<ProbeWindow>();
            foreach (Row row in rows)
            {
                int mine = row.DurationMinutes ?? int.MaxValue;
                bool gatedByPool = false;
                foreach (KeyValuePair<string, int> block in spent)
                    if (block.Key == row.Group && block.Value > mine) { gatedByPool = true; break; }
                bool blocked = row.Disabled && gatedByPool;
                bool usable = !row.Disabled || blocked;
                double? remaining = blocked ? 0.0 : row.Remaining;
                windows.Add(new ProbeWindow
                {
                    // Pool-qualified: two pools each report a "weekly", and a
                    // window key owns an alert rule, so they must not collide.
                    Key = Model.Qualified(row.Key, row.Group),
                    Group = row.Group, GroupLabel = row.GroupLabel,
                    DurationMinutes = row.DurationMinutes,
                    Available = usable,
                    Remaining = usable ? remaining : null,
                    ResetEpoch = row.ResetEpoch,
                    Source = source,
                });
            }
            // Shortest window first within a pool, pools alphabetically, so the
            // cards draw in a predictable order.
            windows.Sort((a, b) =>
            {
                int g = string.Compare(a.Group, b.Group, StringComparison.Ordinal);
                if (g != 0) return g;
                int da = a.DurationMinutes ?? int.MaxValue, db = b.DurationMinutes ?? int.MaxValue;
                return da != db ? da.CompareTo(db) : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            return windows;
        }

        // ── source 1: the brain refusal journal ──────────────────────────────
        // Antigravity publishes no usage file: its conversation stores are
        // protobuf blobs and app_storage.json carries only UI state. What it
        // DOES write, verbatim, is the backend's own refusal:
        //
        //   {"timestamp":"2026-08-22T10:48:59Z","sender":"system",
        //    "content":"... RESOURCE_EXHAUSTED (code 429): Individual quota
        //               reached. ... Resets in 81h17m43s."}
        //
        // That is the provider's own verdict: spent until timestamp + delay.
        static ProbeAccount Journal(ProbeAccount acc)
        {
            double now = Stamp.Now;
            Refusal refusal = LatestRefusal(DataDir(), now);
            if (refusal == null)
                return Unavailable(acc, "no_refusal_recorded",
                    "install the Antigravity CLI for exact quota — without it "
                    + "Antigravity only reports a limit when it refuses work");
            if (refusal.ResetEpoch <= now - ResetGraceS)
                return Unavailable(acc, "quota_unknown",
                    "last Antigravity block already reset — install the "
                    + "Antigravity CLI for exact quota");
            acc.Status = Model.OK;
            acc.Ok = true;
            // Once resets_at passes, Model.Resolve renders this same window as
            // refilled (AssumedFull) and the reset alert fires; that is why an
            // elapsed stamp is passed through as-is.
            acc.Windows.Add(new ProbeWindow
            {
                Key = "quota", Available = true, Remaining = 0.0,
                ResetEpoch = refusal.ResetEpoch, Source = "antigravity-brain-message",
            });
            return acc;
        }

        class Refusal
        {
            public double ObservedAt, ResetEpoch;
        }

        // Bounded scan: only recently touched conversations, only their message
        // directory, only files that mention the marker. A machine accumulates
        // hundreds of conversations, and a refusal that still matters is always
        // in a recent one.
        const double MaxAgeS = 7 * 24 * 3600;
        const int MaxDirs = 16;
        const int MaxFilesPerDir = 200;
        const string Marker = "RESOURCE_EXHAUSTED";

        static readonly Regex ResetRe = new Regex(
            @"Resets? in\s+(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?\s*(?:(\d+)\s*s)?",
            RegexOptions.IgnoreCase);

        // `Resets in 0s` and messages with no reset clause at all ("You have
        // exhausted your capacity on this model.") are per-model hiccups, not
        // account quota windows — they carry no window to render.
        static double? ResetDelay(string text)
        {
            Match m = ResetRe.Match(text ?? "");
            if (!m.Success) return null;
            double total = 0;
            if (m.Groups[1].Success) total += int.Parse(m.Groups[1].Value) * 3600.0;
            if (m.Groups[2].Success) total += int.Parse(m.Groups[2].Value) * 60.0;
            if (m.Groups[3].Success) total += int.Parse(m.Groups[3].Value);
            return total > 0 ? total : (double?)null;
        }

        // An ACTIVE block (reset still ahead) always wins over an elapsed one,
        // however recent the latter: an old expired block must never mask a
        // live one. Among equals the newest observation wins.
        static Refusal LatestRefusal(string dataDir, double now)
        {
            if (string.IsNullOrEmpty(dataDir)) return null;
            string root = Path.Combine(dataDir, "brain");
            if (!Directory.Exists(root)) return null;
            var events = new List<Refusal>();
            foreach (string dir in RecentConversations(root, now))
                events.AddRange(EventsFrom(dir, now));
            if (events.Count == 0) return null;
            Refusal best = null;
            foreach (Refusal ev in events)
            {
                bool activeNow = ev.ResetEpoch > now;
                bool activeBest = best != null && best.ResetEpoch > now;
                if (best == null || (activeNow && !activeBest)
                    || (activeNow == activeBest && ev.ObservedAt > best.ObservedAt))
                    best = ev;
            }
            return best;
        }

        static List<string> RecentConversations(string root, double now)
        {
            var found = new List<KeyValuePair<double, string>>();
            string[] names;
            try { names = Directory.GetDirectories(root); } catch { return new List<string>(); }
            foreach (string name in names)
            {
                string dir = Path.Combine(name, ".system_generated", "messages");
                double mtime;
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    mtime = Stamp.Of(Directory.GetLastWriteTimeUtc(dir));
                }
                catch { continue; }
                if (now - mtime > MaxAgeS) continue;
                found.Add(new KeyValuePair<double, string>(mtime, dir));
            }
            found.Sort((a, b) => b.Key.CompareTo(a.Key));
            var dirs = new List<string>();
            for (int i = 0; i < found.Count && i < MaxDirs; i++) dirs.Add(found[i].Value);
            return dirs;
        }

        static List<Refusal> EventsFrom(string directory, double now)
        {
            var events = new List<Refusal>();
            string[] names;
            try { names = Directory.GetFiles(directory, "*.json"); } catch { return events; }
            int opened = 0;
            foreach (string path in names)
            {
                if (opened >= MaxFilesPerDir) break;
                string text;
                double mtime;
                try
                {
                    mtime = Stamp.Of(File.GetLastWriteTimeUtc(path));
                    if (now - mtime > MaxAgeS) continue;
                    text = ReadCapped(path, 64 * 1024);
                }
                catch { continue; }
                opened++;
                if (text.IndexOf(Marker, StringComparison.Ordinal) < 0) continue;
                object record = J.Parse(text);
                string content = J.Str(J.Get(record, "content"));
                if (content == null || content.IndexOf(Marker, StringComparison.Ordinal) < 0) continue;
                double? delay = ResetDelay(content);
                if (!delay.HasValue) continue;
                double observed = Stamp.Epoch(J.Get(record, "timestamp")) ?? mtime;
                events.Add(new Refusal { ObservedAt = observed, ResetEpoch = observed + delay.Value });
            }
            return events;
        }

        static string ReadCapped(string path, int maxBytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buffer = new byte[Math.Min(fs.Length, maxBytes)];
                int read = fs.Read(buffer, 0, buffer.Length);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
        }
    }

    // ── the sweep ────────────────────────────────────────────────────────────
    // One snapshot from every vendor, inside a budget the caller's kill timer
    // outlives. A vendor that fails is one card with an error, never a lost
    // snapshot.
    static class Probe
    {
        // Measured, not guessed: the FIRST `codex app-server` of a session pays
        // a cold start and was seen answering in 14-19s, and `agy -p "/usage"`
        // normally answers in ~3s but has been observed at 14s+. A 12s
        // per-account budget turned those tails into a permanent "first Codex
        // account is broken" and a phantom "Antigravity UNAVAILABLE".
        public const double TotalBudgetS = 66.0;
        public const double PerAccountBudgetS = 26.0;

        public static ProbeResult Run()
        {
            double now = Stamp.Now;
            double deadline = now + TotalBudgetS;
            var result = new ProbeResult();
            var accounts = new List<ProbeAccount>();

            try { accounts.AddRange(CodexSource.Sweep(deadline, PerAccountBudgetS)); }
            catch (Exception ex) { accounts.Add(Broken("codex", "Codex", ex)); }

            try
            {
                if (ClaudeSource.Installed())
                {
                    double budget = Math.Min(PerAccountBudgetS, deadline - Stamp.Now);
                    if (budget > 0.2) accounts.Add(ClaudeSource.Probe(Stamp.Now + budget));
                }
            }
            catch (Exception ex) { accounts.Add(Broken("claude", "Claude Code", ex)); }

            try
            {
                if (AntigravitySource.Installed())
                {
                    double budget = Math.Min(PerAccountBudgetS, deadline - Stamp.Now);
                    if (budget > 0.2) accounts.Add(AntigravitySource.Probe(Stamp.Now + budget));
                }
            }
            catch (Exception ex) { accounts.Add(Broken("antigravity", "Antigravity", ex)); }

            double at = Stamp.Now;
            foreach (ProbeAccount acc in accounts) result.Accounts.Add(Model.Flatten(acc, at));
            result.Clis = Cli.Status();
            return result;
        }

        static ProbeAccount Broken(string provider, string label, Exception ex)
        {
            return new ProbeAccount
            {
                Provider = provider, ProviderLabel = label, Name = label,
                Status = Model.ERROR, Ok = false, Error = ex.GetType().Name,
            };
        }
    }
}
