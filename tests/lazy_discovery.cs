using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Limisaw;

// PERF-002 (SRC-005:R013): lazy, deadline-aware filesystem discovery.
//
// Both metadata scanners used to materialize whole-directory arrays before
// any filter ran: Antigravity's RecentConversations called GetDirectories over
// the whole conversation tree, and EventsFrom called GetFiles over every
// selected directory, with Claude's Transcripts.Recent shaped the same way.
// The deadline checked inside the foreach was already too late — the runtime
// had paid for the full array — so on a large producer-owned history tree the
// caps bounded nothing and refresh latency scaled with historical clutter.
//
// Both walks are now LAZY (EnumerateDirectories/EnumerateFiles), deadline-
// checked WHILE enumerating, and keep bounded newest-K candidate sets
// (K = MaxDirs / MaxFilesPerDir / MaxFiles) maintained by worst-eviction —
// filesystem order is not freshness order, so Take(N) would be wrong.
// A deadline-cut scan is explicitly partial: it reports evidence it saw and
// may never claim unseen evidence does not exist.
//
// The deterministic cost control is a counting SEAM over the enumeration
// functions themselves: the harness wraps them, counts PULLED entries, and
// asserts a cut scan pulls a bounded number — never the whole tree. Restoring
// the eager GetDirectories/GetFiles calls bypasses the seams and the counts
// collapse to zero, which is exactly what the red control must fail on.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main LazyDiscoveryTest)
public static class LazyDiscoveryTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Scratch;
    static int AgyPulls, ClaudeDirPulls, ClaudeFilePulls;

    const string Benign = "{\"timestamp\":\"2026-09-05T08:00:00Z\",\"sender\":\"user\",\"content\":\"hello there\"}";

    static string Refusal(double hours, string observed)
    {
        return "{\"timestamp\":\"" + observed + "\",\"sender\":\"system\",\"content\":"
            + "\"... RESOURCE_EXHAUSTED (code 429): Individual quota reached. Resets in "
            + hours.ToString("0") + "h0m0s.\"}";
    }

    static void Write(string dir, string name, string body, double ageS)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, body);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-ageS));
    }

    static void TouchDir(string dir, double ageS)
    {
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddSeconds(-ageS));
    }

    static string Iso(double secondsAgo)
    {
        return DateTime.UtcNow.AddSeconds(-secondsAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    static string MessagesDir(string conv)
    {
        string dir = Path.Combine(Scratch, ".gemini", "antigravity", "brain", conv, ".system_generated", "messages");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void NewProfile()
    {
        Scratch = Path.Combine(Path.GetTempPath(), "limisaw_lazydisc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        Environment.SetEnvironmentVariable("USERPROFILE", Scratch);
        Environment.SetEnvironmentVariable("HOME", Scratch);
    }

    static void InstallCountingSeams()
    {
        AgyPulls = 0;
        AntigravitySource.EnumerateDirs = root => Counted(Directory.EnumerateDirectories(root), () => AgyPulls++);
        AntigravitySource.EnumerateFiles = (dir, pattern) => Counted(Directory.EnumerateFiles(dir, pattern), () => AgyPulls++);
    }

    static IEnumerable<string> Counted(IEnumerable<string> source, Action onPull)
    {
        foreach (string item in source) { onPull(); yield return item; }
    }

    static void InstallEagerSeams()
    {
        // The RED shape: whole-array materialization, presented through the
        // same seam signature. The scanner cannot stop it early because the
        // array is fully built before the first pull.
        AntigravitySource.EnumerateDirs = root =>
        {
            AgyPulls += Directory.GetDirectories(root).Length;
            return Directory.GetDirectories(root);
        };
        AntigravitySource.EnumerateFiles = (dir, pattern) =>
        {
            AgyPulls += Directory.GetFiles(dir, pattern).Length;
            return Directory.GetFiles(dir, pattern);
        };
    }

    public static int Main()
    {
        Console.WriteLine("== Antigravity: a deadline-cut scan stops pulling the tree ==");
        NewProfile();
        InstallCountingSeams();
        // 400 old conversations, each with a few messages; the ONLY live
        // refusal sits in a recent conversation. Nothing is sorted by name,
        // so the fixture exercises bounded selection, not order luck.
        for (int i = 0; i < 400; i++)
        {
            string dir = MessagesDir("conv-old-" + i.ToString("000"));
            for (int j = 0; j < 3; j++) Write(dir, "m-" + j + ".json", Benign, 30 * 24 * 3600);
            TouchDir(dir, 30 * 24 * 3600);
        }
        string recent = MessagesDir("conv-live");
        Write(recent, "refusal.json", Refusal(5, Iso(60)), 60);
        TouchDir(recent, 60);

        AntigravitySource.ResetBodyCache();
        double now = Stamp.Now;
        AntigravitySource.JournalScan cut = AntigravitySource.LatestRefusal(
            AntigravitySource.DataDir(), now, now);   // expired before it began
        Check("an expired deadline marks the scan partial", cut.DeadlineHit && !cut.Completed, "");
        Check("...and pulls a bounded number of entries, not the 400-conversation tree",
            AgyPulls > 0 && AgyPulls < 30, "pulled=" + AgyPulls + " of 401 dirs");

        Console.WriteLine();
        Console.WriteLine("== Antigravity: a complete scan finds the newest evidence ==");
        AntigravitySource.ResetBodyCache();
        now = Stamp.Now;
        AntigravitySource.JournalScan full = AntigravitySource.LatestRefusal(
            AntigravitySource.DataDir(), now, now + 60);
        Check("the live refusal is found even though 400 dead dirs came first",
            full.Best != null && full.Best.ResetEpoch > now + 4 * 3600,
            full.Best == null ? "no refusal" : "resets in " + ((full.Best.ResetEpoch - now) / 3600).ToString("0.0") + "h");
        Check("...the scan completed", !full.DeadlineHit, "");
        Check("...bounded retention: no body beyond the newest 200 of any directory was opened",
            full.OpenedFiles <= 200, "opened=" + full.OpenedFiles);

        Console.WriteLine();
        Console.WriteLine("== Antigravity: old evidence cannot crowd out the newest in one directory ==");
        NewProfile();
        InstallCountingSeams();
        string big = MessagesDir("conv-mixed");
        for (int i = 1; i <= 300; i++) Write(big, "aaa-" + i.ToString("000") + ".json", Benign, 3600);
        Write(big, "zzz-refusal.json", Refusal(7, Iso(30)), 30);
        TouchDir(big, 30);
        AntigravitySource.ResetBodyCache();
        now = Stamp.Now;
        AntigravitySource.JournalScan mixed = AntigravitySource.LatestRefusal(
            AntigravitySource.DataDir(), now, now + 60);
        Check("the newest refusal (alphabetically LAST) still wins",
            mixed.Best != null && mixed.Best.ResetEpoch > now + 6 * 3600, "");
        Check("...with the cap still bounding opens at 200", mixed.OpenedFiles == 200,
            "opened=" + mixed.OpenedFiles);

        Console.WriteLine();
        Console.WriteLine("== Claude: lazy walk, deadline inside, bounded newest-8 ==");
        NewProfile();
        string projects = Path.Combine(Scratch, ".claude", "projects");
        Directory.CreateDirectory(projects);
        for (int i = 0; i < 60; i++)
        {
            string slug = Path.Combine(projects, "proj-" + i.ToString("00"));
            Directory.CreateDirectory(slug);
            for (int j = 0; j < 5; j++)
                Write(slug, "t-" + j + ".jsonl", "{\"line\":1}\n", 30 * 24 * 3600);
            Directory.SetLastWriteTimeUtc(slug, DateTime.UtcNow.AddDays(-30));
        }
        // The freshest transcript is in the LAST slug enumeration reaches.
        string live = Path.Combine(projects, "proj-live");
        Directory.CreateDirectory(live);
        Write(live, "t-live.jsonl", "{\"quotaLimits\":{\"status\":\"rejected\",\"rateLimitType\":\"five_hour\",\"resetsAt\":\""
            + DateTime.UtcNow.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",\"timestamp\":\""
            + Iso(60) + "\"}}\n", 60);

        ClaudeSource.Transcripts.ResetCounters();
        ClaudeSource.Transcripts.EnumerateDirs = root => Counted(Directory.EnumerateDirectories(root), () => ClaudeDirPulls++);
        ClaudeSource.Transcripts.EnumerateFiles = (dir, pattern) => Counted(Directory.EnumerateFiles(dir, pattern), () => ClaudeFilePulls++);
        double cnow = Stamp.Now;
        ClaudeSource.Transcripts.Clock = () => cnow;   // already expired
        var blocks = ClaudeSource.Transcripts.ActiveBlocks(Path.Combine(Scratch, ".claude"), cnow, cnow);
        Check("a cut Claude walk reports DeadlineHit", ClaudeSource.Transcripts.DeadlineHit, "");
        Check("...and pulls a bounded number of entries, not the 61-project tree",
            ClaudeDirPulls > 0 && ClaudeDirPulls < 10, "dirs pulled=" + ClaudeDirPulls);
        Check("...and returns no fabricated blocks", blocks.Count == 0, blocks.Count + " blocks");

        ClaudeSource.Transcripts.ResetCounters();
        ClaudeDirPulls = ClaudeFilePulls = 0;
        cnow = Stamp.Now;
        blocks = ClaudeSource.Transcripts.ActiveBlocks(Path.Combine(Scratch, ".claude"), cnow, cnow + 60);
        Check("a complete walk finds the live refusal block",
            blocks.ContainsKey(Model.FIVE_HOUR) && blocks[Model.FIVE_HOUR].ResetEpoch > cnow + 3 * 3600,
            blocks.Count + " blocks");
        Check("...bounded retention: only MaxFiles transcripts are ever returned",
            ClaudeSource.Transcripts.TailReads <= 8, "tails=" + ClaudeSource.Transcripts.TailReads);
        Check("...the walk stayed deadline-clean", !ClaudeSource.Transcripts.DeadlineHit, "");

        Console.WriteLine();
        Console.WriteLine("== source guard: the eager APIs are gone from the scanners ==");
        string agy = File.ReadAllText(Path.Combine(SourceRoot(), "ProbeAntigravity.cs"));
        string claude = File.ReadAllText(Path.Combine(SourceRoot(), "ProbeClaude.cs"));
        Check("Antigravity discovery no longer materializes GetDirectories/GetFiles",
            agy.IndexOf("Directory.GetDirectories(root)", StringComparison.Ordinal) < 0
            && agy.IndexOf("Directory.GetFiles(directory", StringComparison.Ordinal) < 0, "");
        Check("Claude discovery no longer materializes GetDirectories/GetFiles",
            claude.IndexOf("Directory.GetDirectories(root)", StringComparison.Ordinal) < 0
            && claude.IndexOf("Directory.GetFiles(slug", StringComparison.Ordinal) < 0, "");

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    static string SourceRoot()
    {
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) return dir;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
