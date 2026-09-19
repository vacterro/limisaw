using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

            string cliError;
            List<ProbeWindow> windows = CliWindows(deadline, out cliError);
            if (windows != null && windows.Count > 0)
            {
                acc.Status = Model.OK; acc.Ok = true; acc.Windows = windows;
                return acc;
            }
            return Journal(acc, cliError, deadline);
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
        // `error` is why the CLI could not answer, or null when it is simply not
        // installed — that is what the journal fallback exists for, not a fault.
        static List<ProbeWindow> CliWindows(double deadline, out string error)
        {
            var windows = ReadCliUsage(deadline);
            error = windows == null ? LastCliUsageError : null;
            return windows;
        }

        // ── R074: the narrow structured entry point for the CONNECTION adapter ──
        // The connection adapter must reuse — never fork — this path: the exact
        // command (`agy -p "/usage" --output-format json`), the bounded Cli.Run,
        // the payload limits, the JSON parser and the pool-qualified windows all
        // stay owned here. LastCliUsageError holds the same sanitized `error`
        // string the journal fallback would see; a null result with a null error
        // means the CLI is simply absent.
        internal static string LastCliUsageError;

        internal static List<ProbeWindow> ReadCliUsage(double deadline)
        {
            LastCliUsageError = null;
            string exe = Cli.Resolve("antigravity");
            if (exe.Length == 0) return null;
            Cli.Result res = Cli.Run(exe,
                new[] { "-p", "/usage", "--output-format", "json" }, deadline, null);
            if (!res.Ok) { LastCliUsageError = "agy /usage: " + res.Error; return null; }
            object payload = J.Parse(res.Stdout);
            if (payload == null) { LastCliUsageError = "agy /usage did not return JSON"; return null; }
            string status = J.Str(J.Get(payload, "status"));
            if (!string.IsNullOrEmpty(status) && status != "SUCCESS")
            { LastCliUsageError = "agy /usage: " + status; return null; }
            List<Row> rows = ParseUsagePayload(payload);
            if (rows.Count == 0)
            { LastCliUsageError = "agy /usage reported no readable quota window"; return null; }
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
        //
        // `cliError` is non-null when the CLI WAS tried and refused. Telling that
        // user to install what they already have is the worst answer available:
        // it hides an actionable failure (usually "not logged in") behind advice
        // they have already followed.
        internal static ProbeAccount Journal(ProbeAccount acc, string cliError, double deadline)
        {
            double now = Stamp.Now;
            JournalScan scan = LatestRefusal(DataDir(), now, deadline);
            if (scan.Best == null)
            {
                // PERF-002: a scan the deadline cut short is NOT "no refusal
                // recorded" — the journal was never fully read, and reporting
                // the healthy resting state for an unread journal would tell
                // a blocked user they are fine.
                if (scan.DeadlineHit)
                    return Unavailable(acc, "deadline_exceeded",
                        "the Antigravity journal scan ran out of sweep time");
                return Unavailable(acc, NoQuotaCode(cliError), NoQuotaSummary(cliError));
            }
            Refusal refusal = scan.Best;
            if (refusal.ResetEpoch <= now - ResetGraceS)
                return Unavailable(acc,
                    string.IsNullOrEmpty(cliError) ? "quota_unknown" : "probe_failed",
                    cliError ?? "last Antigravity block already reset — install the "
                    + "Antigravity CLI for exact quota");
            acc.Status = Model.OK;
            acc.Ok = true;
            bool active = refusal.ResetEpoch > now;
            // W2-003: an ACTIVE block is the vendor's own verdict — exhausted
            // until the reset, a real 0. An ELAPSED one (inside the grace) is
            // only an EVENT: the block ended, but nothing has measured the
            // quota since, so the window stays unreadable instead of crossing
            // ApplyElapsedResets as a fabricated 100% "refilled". The reset
            // stamp is kept either way — the reset alert keys on it.
            acc.UnverifiedReset = !active;
            acc.Windows.Add(new ProbeWindow
            {
                Key = "quota", Available = active,
                Remaining = active ? 0.0 : (double?)null,
                ResetEpoch = refusal.ResetEpoch, Source = "antigravity-brain-message",
            });
            return acc;
        }

        // A missing CLI is Antigravity working as designed — it can only quote
        // quota when the backend refuses work, so "nothing to report" is its
        // healthy resting state and stays Quiet (muted `idle:`). A CLI that IS
        // installed and refused is a fault the user must act on, so it must be
        // loud: `probe_failed` is outside Model.QuietCodes on purpose.
        public static string NoQuotaCode(string cliError)
        {
            return string.IsNullOrEmpty(cliError) ? "no_refusal_recorded" : "probe_failed";
        }

        public static string NoQuotaSummary(string cliError)
        {
            if (!string.IsNullOrEmpty(cliError)) return cliError;
            return "install the Antigravity CLI for exact quota — without it "
                + "Antigravity only reports a limit when it refuses work";
        }

        internal class Refusal
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
        internal class JournalScan
        {
            public Refusal Best;
            public bool DeadlineHit;
            public int OpenedFiles, CachedFiles;
            // PERF-002: the deterministic cost counters. A scan must stop
            // enumerating the tree once its deadline or cap can safely end the
            // work — these count the metadata VISITS (dirs + files) so the
            // harness can prove operation counts, not wall-clock guesses.
            public int DirVisits, FileVisits;
            // PERF-002: true when the whole tree was walked to completion, so
            // the caller may treat absence (or the live set) as authoritative.
            // A deadline-cut walk leaves this false and may never prove a
            // negative or prune a live set it never saw.
            public bool Completed;
        }

        // PERF-002 cache: a body is parsed only when its file changed. Keyed by
        // path + mtime + length — three facts a stat already knows, so an
        // unchanged conversation costs zero reads on every sweep after the
        // first. The counters are what the harness asserts against: without
        // the cache and the deadline, a full scan could open 16 dirs × 200
        // files × 64KiB — the 200MiB bound the audit measured.
        class CacheEntry { public double Mtime; public long Length; public List<Refusal> Events; public long LastSeen; }
        static readonly Dictionary<string, CacheEntry> BodyCache = new Dictionary<string, CacheEntry>();
        internal static long BodyReads, BytesRead;
        // PERF-002: enumeration seams. Production defaults to the lazy
        // Directory.Enumerate* APIs; the harness wraps them with counting
        // generators to prove the scanner PULLS only what it needs (an eager
        // GetDirectories/GetFiles materializes the whole tree before the
        // scanner gets control — exactly the waste this repair removes).
        internal static Func<string, IEnumerable<string>> EnumerateDirs =
            root => Directory.EnumerateDirectories(root);
        internal static Func<string, string, IEnumerable<string>> EnumerateFiles =
            (dir, pattern) => Directory.EnumerateFiles(dir, pattern);
        internal static void ResetBodyCache()
        {
            BodyCache.Clear(); BodyReads = 0; BytesRead = 0;
            ScanGeneration = 0;
        }
        // PERF-003: monotonic scan generation stamping every cache visit, and
        // the hard cardinality ceiling — see the PERF-003 block below.
        internal static long ScanGeneration;

        internal static JournalScan LatestRefusal(string dataDir, double now, double deadline)
        {
            var scan = new JournalScan();
            if (string.IsNullOrEmpty(dataDir)) return scan;
            string root = Path.Combine(dataDir, "brain");
            if (!Directory.Exists(root)) { scan.Completed = true; return scan; }
            ScanGeneration++;
            var events = new List<Refusal>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in RecentConversations(root, now, deadline, scan))
            {
                if (scan.DeadlineHit) break;
                events.AddRange(EventsFrom(dir, now, deadline, scan, seenPaths));
            }
            Refusal best = null;
            foreach (Refusal ev in events)
            {
                bool activeNow = ev.ResetEpoch > now;
                bool activeBest = best != null && best.ResetEpoch > now;
                if (best == null || (activeNow && !activeBest)
                    || (activeNow == activeBest && ev.ObservedAt > best.ObservedAt))
                    best = ev;
            }
            scan.Best = best;
            // PERF-002: a deadline-cut walk cannot report "no refusal" while a
            // warm cache still holds parsed evidence — the old scan answered
            // cache hits past the deadline and that contract stays. The cached
            // events are served WITHOUT touching LastSeen (an unseen entry
            // must not look seen, so a partial scan still prunes nothing).
            if (scan.Best == null && scan.DeadlineHit)
            {
                foreach (KeyValuePair<string, CacheEntry> kv in BodyCache)
                    foreach (Refusal ev in kv.Value.Events)
                    {
                        bool activeNow = ev.ResetEpoch > now;
                        bool activeBest = scan.Best != null && scan.Best.ResetEpoch > now;
                        if (scan.Best == null || (activeNow && !activeBest)
                            || (activeNow == activeBest && ev.ObservedAt > scan.Best.ObservedAt))
                            scan.Best = ev;
                    }
            }
            // PERF-003: two independent bounds own the cache.
            //  Live-set pruning is a COMPLETE-scan privilege: a scan the
            //  deadline cut has not seen the whole tree, so an unseen entry
            //  may still be live and pruning it would destroy a warm cache
            //  for nothing. A complete scan may prove absence.
            //  The hard cardinality ceiling applies after EVERY scan — it is
            //  the second safety bound for repeated partial scans and name
            //  churn, evicting least-recently-seen entries deterministically.
            if (!scan.DeadlineHit) PruneLiveSet(seenPaths);
            EnforceBodyCacheCap();
            return scan;
        }

        // PERF-002: the conversation walk is LAZY and deadline-bounded. It
        // stats each candidate's message directory (the only way to know its
        // age), keeps a bounded newest-K set (K = MaxDirs) instead of sorting
        // the whole population, and stops the moment the deadline expires —
        // marking the scan partial. Enumeration order is not freshness order,
        // so the bounded set is maintained by INSERTION against the current
        // worst candidate, never by Take(MaxDirs).
        static List<string> RecentConversations(string root, double now, double deadline, JournalScan scan)
        {
            // Bounded newest-first candidates: (mtime, dir). Capacity MaxDirs.
            var best = new List<KeyValuePair<double, string>>(MaxDirs);
            IEnumerable<string> dirs;
            try { dirs = EnumerateDirs(root); }
            catch { return best.ConvertAll(kv => kv.Value); }
            foreach (string name in dirs)
            {
                scan.DirVisits++;
                if (Stamp.Now >= deadline) { scan.DeadlineHit = true; break; }
                string dir = Path.Combine(name, ".system_generated", "messages");
                double mtime;
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    mtime = Stamp.Of(Directory.GetLastWriteTimeUtc(dir));
                }
                catch { continue; }
                if (now - mtime > MaxAgeS) continue;
                InsertBounded(best, mtime, dir, MaxDirs);
            }
            best.Sort((a, b) => b.Key.CompareTo(a.Key));
            return best.ConvertAll(kv => kv.Value);
        }

        // PERF-002: one bounded newest-candidate structure shared by both
        // scanners. The caps are tiny (16 dirs, 200 files), so a linear
        // worst-eviction beats any dependency: if the list is full and the
        // newcomer is newer than the oldest kept candidate, the oldest dies.
        static void InsertBounded(List<KeyValuePair<double, string>> best, double mtime, string value, int cap)
        {
            if (best.Count < cap) { best.Add(new KeyValuePair<double, string>(mtime, value)); return; }
            int worst = 0;
            for (int i = 1; i < best.Count; i++) if (best[i].Key < best[worst].Key) worst = i;
            if (mtime <= best[worst].Key) return;
            best.RemoveAt(worst);
            best.Add(new KeyValuePair<double, string>(mtime, value));
        }

        static List<Refusal> EventsFrom(string directory, double now, double deadline, JournalScan scan, HashSet<string> seenPaths)
        {
            var events = new List<Refusal>();
            // PERF-002: the file walk is LAZY (EnumerateFiles) and the deadline
            // bounds the METADATA walk too, not only the body reads. Newest-K
            // selection happens WHILE walking: a bounded list of at most
            // MaxFilesPerDir candidates, evicting its worst member as newer
            // ones arrive. A tree with tens of thousands of stale messages
            // costs a stat each but never a materialized whole-directory
            // array, and the deadline stops the walk itself.
            var candidates = new List<KeyValuePair<double, string>>(MaxFilesPerDir);
            var lengths = new Dictionary<string, long>();
            IEnumerable<string> names;
            try { names = EnumerateFiles(directory, "*.json"); }
            catch { return events; }
            foreach (string path in names)
            {
                scan.FileVisits++;
                if (Stamp.Now >= deadline) { scan.DeadlineHit = true; break; }
                try
                {
                    double mtime = Stamp.Of(File.GetLastWriteTimeUtc(path));
                    if (now - mtime > MaxAgeS) continue;
                    // PERF-002: every live fresh file joins the live set for
                    // this scan (PERF-003 pruning) and a warm cache entry is
                    // served IMMEDIATELY — the cap was and is a bound on body
                    // OPENs, never on cached answers, so no evidence a cache
                    // already holds can drop out of the result.
                    seenPaths.Add(path);
                    CacheEntry hit;
                    long length;
                    try { length = new FileInfo(path).Length; }
                    catch { continue; }
                    if (BodyCache.TryGetValue(path, out hit) && hit.Mtime == mtime && hit.Length == length)
                    {
                        hit.LastSeen = ScanGeneration;
                        scan.CachedFiles++;
                        events.AddRange(hit.Events);
                        continue;
                    }
                    // A body-read candidate competes for one of MaxFilesPerDir
                    // bounded slots: the worst (oldest) candidate dies when a
                    // newer one arrives, so retention stays O(cap) no matter
                    // how many files the directory holds.
                    if (candidates.Count == MaxFilesPerDir)
                    {
                        int worst = 0;
                        for (int i = 1; i < candidates.Count; i++) if (candidates[i].Key < candidates[worst].Key) worst = i;
                        if (mtime <= candidates[worst].Key) continue;
                        lengths.Remove(candidates[worst].Value);
                        candidates.RemoveAt(worst);
                    }
                    candidates.Add(new KeyValuePair<double, string>(mtime, path));
                    lengths[path] = length;
                }
                catch { continue; }
            }
            // PERF-002: newest first, then the cap — the same accepted cost as
            // before (the newest 200 may still be opened), now decided over a
            // bounded candidate set instead of the whole directory.
            candidates.Sort((a, b) => b.Key.CompareTo(a.Key));
            int opened = 0;
            foreach (KeyValuePair<double, string> kv in candidates)
            {
                if (opened >= MaxFilesPerDir) break;
                string path = kv.Value;
                double mtime = kv.Key;
                CacheEntry hit;
                if (BodyCache.TryGetValue(path, out hit) && hit.Mtime == mtime && hit.Length == lengths[path])
                {
                    // (Defensive: the walk serves cached entries already; this
                    // branch keeps the body loop correct if one raced in.)
                    hit.LastSeen = ScanGeneration;
                    scan.CachedFiles++;
                    events.AddRange(hit.Events);
                    continue;
                }
                if (Stamp.Now >= deadline) { scan.DeadlineHit = true; break; }
                string text;
                try { text = ReadCapped(path, 64 * 1024); } catch { continue; }
                opened++;
                scan.OpenedFiles++;
                BodyReads++; BytesRead += Encoding.UTF8.GetByteCount(text);
                var parsed = new List<Refusal>();
                if (text.IndexOf(Marker, StringComparison.Ordinal) >= 0)
                {
                    object record = J.Parse(text);
                    string content = J.Str(J.Get(record, "content"));
                    if (content != null && content.IndexOf(Marker, StringComparison.Ordinal) >= 0)
                    {
                        double? delay = ResetDelay(content);
                        if (delay.HasValue)
                        {
                            double observed = Stamp.Epoch(J.Get(record, "timestamp")) ?? mtime;
                            parsed.Add(new Refusal { ObservedAt = observed, ResetEpoch = observed + delay.Value });
                        }
                    }
                }
                BodyCache[path] = new CacheEntry { Mtime = mtime, Length = lengths[path], Events = parsed, LastSeen = ScanGeneration };
                events.AddRange(parsed);
            }
            return events;
        }

        // ── PERF-003: bounded BodyCache ownership ────────────────────────────
        // The cache used to grow for the life of the process: every journal
        // path ever parsed stayed forever, so memory followed historical file
        // churn instead of the current useful working set. Two bounds now own
        // it, without touching forensic truth — an evicted entry can only
        // cost a future safe body reread, never a wrong number.
        //
        //  1. Live-set pruning after a COMPLETE scan: entries not seen this
        //     scan (and therefore no longer in the live candidate set) are
        //     evicted. A DEADLINE-CUT scan prunes nothing — it has not seen
        //     the whole tree and may not prove a path dead.
        //  2. A hard cardinality ceiling (MaxBodyCacheEntries): protects
        //     against repeated partial scans and filename churn. Over the
        //     cap, the least-recently-SEEN entries are evicted
        //     deterministically.
        internal const int MaxBodyCacheEntries = 512;
        internal static int BodyCacheCount { get { lock (BodyCache) return BodyCache.Count; } }
        internal static long LastPruneRemoved;

        // Bound 1 — live-set pruning, COMPLETE scans only. Every entry the
        // scan did not see is no longer provably live (unlisted or aged out),
        // so it dies. A partial scan never calls this.
        static void PruneLiveSet(HashSet<string> seenPaths)
        {
            var dead = new List<string>();
            lock (BodyCache)
            {
                foreach (KeyValuePair<string, CacheEntry> kv in BodyCache)
                    if (!seenPaths.Contains(kv.Key)) dead.Add(kv.Key);
                foreach (string path in dead) BodyCache.Remove(path);
            }
            if (dead.Count > 0) LastPruneRemoved = dead.Count;
        }

        // Bound 2 — the hard ceiling, enforced after every scan. Over the cap
        // the least-recently-SEEN entry dies first: the stalest working set.
        // Eviction never changes a number, only whether the next look at that
        // path pays a fresh, safe body read.
        static void EnforceBodyCacheCap()
        {
            List<string> evicted = null;
            lock (BodyCache)
            {
                while (BodyCache.Count > MaxBodyCacheEntries)
                {
                    string oldest = null; long seen = long.MaxValue;
                    foreach (KeyValuePair<string, CacheEntry> kv in BodyCache)
                        if (kv.Value.LastSeen < seen) { seen = kv.Value.LastSeen; oldest = kv.Key; }
                    if (oldest == null) break;
                    BodyCache.Remove(oldest);
                    if (evicted == null) evicted = new List<string>();
                    evicted.Add(oldest);
                }
            }
            if (evicted != null) LastPruneRemoved = evicted.Count;
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

        // The smallest per-provider slot that still affords a Codex cold start.
        // A cold `codex app-server` needs at least ColdStartFloorSeconds, so a
        // provider whose slot is shallower is returned `cold_start_underfunded`
        // EVERY sweep and can never warm — the starvation this guards. Equal
        // division alone starves Codex once five providers are active
        // (66 / 5 = 13.2 s < 14 s). 16.5 s is exactly the share the original
        // four-provider budget granted, kept so a machine with four or fewer
        // active providers is scheduled byte-for-byte as before.
        public const double MinViableShareS = TotalBudgetS / 4.0;

        // The per-provider slot for `providers` active vendors: the equal share,
        // floored at MinViableShareS. The total budget follows the slots
        // (deadline = start + share * providers), so the floor widens the sweep
        // only when it must and never clamps a later provider off the end.
        internal static double ProviderShare(int providers)
        {
            return Math.Max(TotalBudgetS / providers, MinViableShareS);
        }

        public static ProbeResult Run()
        {
            return Run(false);
        }

        // `zcodeReadConfig` is LIMISAW.ini's own switch, threaded in rather than
        // read here: a credential permission belongs to the settings file, and
        // one owner means the test can drive both halves.
        public static ProbeResult Run(bool zcodeReadConfig)
        {
            return Run(zcodeReadConfig, false);
        }

        // T-50: `freebuffReadConfig` is FreeBuff's own credential permission,
        // and FreeBuff is OPTIONAL — it participates only when positively
        // detected. T-51 P1-1: `freebuffFound` is the generation's PUBLISHED
        // presence, read from the snapshot so the worker that already built the
        // generation does not fork a second discovery walk; before a generation
        // exists the worker is the builder, so its own probe is authoritative.
        public static ProbeResult Run(bool zcodeReadConfig, bool freebuffReadConfig)
        {
            bool? published = ExecutableDiscovery.PublishedPresence("freebuff");
            bool freebuffFound = published.HasValue
                ? published.Value
                : (ExecutableDiscovery.OptionalVendorPresent != null
                    && ExecutableDiscovery.OptionalVendorPresent("freebuff"));
            return Run(zcodeReadConfig, freebuffReadConfig, freebuffFound);
        }

        public static ProbeResult Run(bool zcodeReadConfig, bool freebuffReadConfig, bool freebuffFound)
        {
            double now = Stamp.Now;
            var result = new ProbeResult();
            var accounts = new List<ProbeAccount>();

            // W2-001: the schedule is built from the ACTIVE providers, so each
            // one gets a distinct cumulative slot. The old fixed multiples
            // (now + share, now + 2*share, ...) counted Codex unconditionally
            // — with no Codex home, every later provider inherited a slot that
            // started before its own turn, and Claude's end was Codex's end, so
            // a slow healthy Codex expired Claude while the final share
            // (66 - 4*12.45 = 16.5s) was unreachable by construction.
            bool claudeOn = ClaudeSource.Installed();
            bool agyOn = AntigravitySource.Installed();
            bool zcodeOn = ZcodeSource.Installed();
            // T-50: FreeBuff is OPTIONAL and only present when positively
            // detected. It takes a slot only then, so the four core vendors'
            // budget is never diluted by a vendor that is not installed.
            bool freebuffOn = FreebuffSource.Installed(freebuffFound);
            int providers = 1 + (claudeOn ? 1 : 0) + (agyOn ? 1 : 0) + (zcodeOn ? 1 : 0) + (freebuffOn ? 1 : 0);
            // The per-provider slot is floored at a viable Codex cold start, and
            // the total budget follows the slots. With four or fewer providers
            // the equal share already clears the floor, so share and deadline are
            // identical to the original 66 s / 16.5 s schedule. A fifth active
            // vendor would otherwise cut every slot to 13.2 s and starve Codex
            // forever (13.2 < ColdStartFloorSeconds); the floor widens the sweep
            // exactly far enough to keep every vendor's slot viable.
            double share = ProviderShare(providers);
            double deadline = now + share * providers;
            double codexEnd = now + share;
            double claudeEnd = codexEnd + (claudeOn ? share : 0);
            double agyEnd = claudeEnd + (agyOn ? share : 0);
            double zcodeEnd = agyEnd + (zcodeOn ? share : 0);
            double freebuffEnd = zcodeEnd + (freebuffOn ? share : 0);

            try { accounts.AddRange(CodexSource.Sweep(codexEnd, share)); }
            catch (Exception ex) { accounts.Add(Broken("codex", "Codex", ex)); }

            if (claudeOn)
            {
                double end = Math.Min(claudeEnd, deadline);
                if (end - Stamp.Now > 0.2)
                {
                    // One card per Claude config directory, exactly like Codex
                    // homes: a second subscription is a second home, and it used
                    // to be invisible here.
                    try { accounts.AddRange(ClaudeSource.Sweep(end, share)); }
                    catch (Exception ex) { accounts.Add(Broken("claude", "Claude Code", ex)); }
                }
            }

            if (agyOn)
            {
                double end = Math.Min(agyEnd, deadline);
                if (end - Stamp.Now > 0.2)
                {
                    try { accounts.Add(AntigravitySource.Probe(end)); }
                    catch (Exception ex) { accounts.Add(Broken("antigravity", "Antigravity", ex)); }
                }
            }

            if (zcodeOn)
            {
                double end = Math.Min(zcodeEnd, deadline);
                if (end - Stamp.Now > 0.2)
                {
                    try { accounts.Add(ZcodeSource.Probe(end, zcodeReadConfig)); }
                    catch (Exception ex) { accounts.Add(Broken("zcode", "Zcode", ex)); }
                }
            }

            if (freebuffOn)
            {
                double end = Math.Min(freebuffEnd, deadline);
                if (end - Stamp.Now > 0.2)
                {
                    try { accounts.Add(FreebuffSource.Probe(end, freebuffReadConfig, freebuffFound)); }
                    catch (Exception ex) { accounts.Add(Broken("freebuff", "FreeBuff", ex)); }
                }
            }

            double at = Stamp.Now;
            foreach (ProbeAccount acc in CodexSource.DistinctRemoteAccounts(accounts,
                result.DuplicateCodexHomes, result.UnverifiedCodexHomes))
                result.Accounts.Add(Model.Flatten(acc, at));
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
