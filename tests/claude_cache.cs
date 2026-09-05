using System;
using System.Collections.Generic;
using System.IO;
using Limisaw;

// PERF-005: Claude's two local fallback sources both did unbounded work on a
// periodic timer.
//
//  1. DesktopSample read and fully J.Parse'd plan-usage-history.json on EVERY
//     sweep. That file is permitted to be 8 MiB and its producer (Claude
//     Desktop) writes about every five minutes, while the refresh timer goes
//     down to sixty seconds — so the same bytes were parsed several times
//     between two producer updates.
//  2. Transcripts.ActiveBlocks treated the sweep deadline as an ENTRY condition:
//     checked once, then ignored while Recent() stat'ed every *.jsonl under
//     every project directory. A large accumulated tree carried filesystem work
//     past the provider's entire budget, and a walk cut short returned the same
//     empty dictionary a healthy account returns.
//
// The parse is now cached by canonical path + mtime + length, the deadline is
// threaded through ActiveBlocks/Recent, and a cut scan is reported. The
// guardrails this harness defends: mtime is ONLY an invalidation signal (never
// the quota timestamp), a changed file invalidates immediately, and an active
// transcript refusal still overrides every percentage.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main ClaudeCacheTest)
public static class ClaudeCacheTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Scratch, AppData, Home;
    static readonly List<string> Profiles = new List<string>();

    // A fresh profile per scenario: APPDATA carries the Desktop history,
    // USERPROFILE carries ~/.claude/projects. The fixtures here are ~8 MiB
    // each, so unlike the smaller harnesses this one sweeps up after itself.
    static void NewProfile()
    {
        Scratch = Path.Combine(Path.GetTempPath(), "limisaw_claudecache_" + Guid.NewGuid().ToString("N"));
        Profiles.Add(Scratch);
        AppData = Path.Combine(Scratch, "AppData", "Roaming");
        Home = Scratch;
        Directory.CreateDirectory(Path.Combine(AppData, "Claude"));
        Environment.SetEnvironmentVariable("APPDATA", AppData);
        Environment.SetEnvironmentVariable("USERPROFILE", Home);
        Environment.SetEnvironmentVariable("HOME", Home);
        // The CLI must not answer: this harness is about the FALLBACK sources.
        Environment.SetEnvironmentVariable("PATH", Path.Combine(Scratch, "no-tools"));
        ClaudeSource.ResetDesktopCache();
    }

    static string HistoryPath() { return Path.Combine(AppData, "Claude", "plan-usage-history.json"); }

    // An 8 MiB-class history file: thousands of samples, the newest carrying the
    // readings we assert on. Padding is real samples, so the parse cost is real.
    static void WriteHistory(int samples, double fiveHourUsed, double weeklyUsed, double newestAgeS)
    {
        var sb = new System.Text.StringBuilder(9 * 1024 * 1024);
        sb.Append("{\"samples\":[");
        double newest = (DateTime.UtcNow.AddSeconds(-newestAgeS)
            - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        for (int i = 0; i < samples; i++)
        {
            if (i > 0) sb.Append(',');
            // Oldest first: the newest sample is the LAST one, and it is the one
            // whose percentages must be reported.
            double t = newest - (samples - 1 - i) * 300000.0;
            bool last = i == samples - 1;
            sb.Append("{\"t\":").Append(t.ToString("0"))
              .Append(",\"u\":{\"fh\":").Append((last ? fiveHourUsed : 3).ToString("0"))
              .Append(",\"sd\":").Append((last ? weeklyUsed : 4).ToString("0"))
              .Append("},\"pad\":\"")
              .Append(new string('x', 1500))
              .Append("\"}");
        }
        sb.Append("]}");
        File.WriteAllText(HistoryPath(), sb.ToString());
    }

    static ProbeAccount Probe(double budgetS)
    {
        return ClaudeSource.Probe(Stamp.Now + budgetS);
    }

    static double? Remaining(ProbeAccount acc, string key)
    {
        foreach (ProbeWindow w in acc.Windows)
            if (w.Key == key) return w.Available ? w.Remaining : null;
        return null;
    }

    static void WriteTranscript(string project, string name, string body)
    {
        string dir = Path.Combine(Home, ".claude", "projects", project);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), body);
    }

    static string Iso(double secondsFromNow)
    {
        return DateTime.UtcNow.AddSeconds(secondsFromNow).ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    static string RejectedRecord(string type, double resetsInS)
    {
        return "{\"timestamp\":\"" + Iso(-60) + "\",\"quotaLimits\":{\"status\":\"rejected\","
            + "\"rateLimitType\":\"" + type + "\",\"resetsAt\":\"" + Iso(resetsInS) + "\"}}";
    }

    public static int Main()
    {
        try { return Run(); }
        finally
        {
            foreach (string dir in Profiles)
                try { Directory.Delete(dir, true); } catch { }
        }
    }

    static int Run()
    {
        Console.WriteLine("== the 8 MiB history is parsed once, not once per sweep ==");
        NewProfile();
        WriteHistory(5000, 61, 22, 120);
        long bytes = new FileInfo(HistoryPath()).Length;
        Check("the fixture is a realistically large history",
            bytes > 7 * 1024 * 1024, "bytes=" + bytes);

        ProbeAccount first = Probe(30);
        Check("the newest sample is what gets reported",
            Remaining(first, Model.FIVE_HOUR) == 39.0 && Remaining(first, Model.WEEKLY) == 78.0,
            "5h=" + Remaining(first, Model.FIVE_HOUR) + " weekly=" + Remaining(first, Model.WEEKLY));
        Check("the cold sweep parsed it exactly once",
            ClaudeSource.DesktopParses == 1, "parses=" + ClaudeSource.DesktopParses);

        for (int i = 0; i < 4; i++) Probe(30);
        Check("five sweeps over an unchanged file still parse once",
            ClaudeSource.DesktopParses == 1, "parses=" + ClaudeSource.DesktopParses);
        Check("...and the fifth sweep still reports the same readings",
            Remaining(Probe(30), Model.FIVE_HOUR) == 39.0, "");
        long readOnce = ClaudeSource.DesktopBytesRead;
        Check("...having read the bytes only once", readOnce < 9 * 1024 * 1024,
            "bytes read=" + readOnce + " over 6 sweeps of a " + bytes + "-byte file");

        Console.WriteLine();
        Console.WriteLine("== a changed file is reparsed exactly once, with its OWN timestamp ==");
        WriteHistory(5000, 88, 30, 60);
        ProbeAccount changed = Probe(30);
        Check("the edit costs exactly one new parse",
            ClaudeSource.DesktopParses == 2, "parses=" + ClaudeSource.DesktopParses);
        Check("...and the NEW readings are the ones reported",
            Remaining(changed, Model.FIVE_HOUR) == 12.0 && Remaining(changed, Model.WEEKLY) == 70.0,
            "5h=" + Remaining(changed, Model.FIVE_HOUR) + " weekly=" + Remaining(changed, Model.WEEKLY));
        Probe(30);
        Check("...and the sweep after that parses nothing again",
            ClaudeSource.DesktopParses == 2, "parses=" + ClaudeSource.DesktopParses);

        Console.WriteLine();
        Console.WriteLine("== mtime is an invalidation signal, never the quota timestamp ==");
        // Backdate the FILE by two days while its newest sample stays two
        // minutes old. If mtime were mistaken for CapturedAt the account would
        // be called stale; the content must decide.
        File.SetLastWriteTimeUtc(HistoryPath(), DateTime.UtcNow.AddDays(-2));
        ProbeAccount aged = Probe(30);
        Check("a two-day-old FILE with a two-minute-old sample is fresh",
            aged.Ok && aged.Status == Model.OK, aged.Status + " " + (aged.Error ?? ""));
        Check("...and the mtime change alone forced one reparse",
            ClaudeSource.DesktopParses == 3, "parses=" + ClaudeSource.DesktopParses);

        // The mirror: a genuinely old SAMPLE is refused however fresh the file is.
        NewProfile();
        WriteHistory(200, 50, 50, 45 * 60 * 8 + 600);
        ProbeAccount stale = Probe(30);
        Check("a sampler that stopped hours ago supplies nothing",
            Remaining(stale, Model.FIVE_HOUR) == null, "");

        Console.WriteLine();
        Console.WriteLine("== the transcript walk is BOUNDED by the deadline, not gated by it ==");
        NewProfile();
        // No Desktop history at all here: the transcript is the only source.
        for (int p = 0; p < 40; p++)
            for (int f = 0; f < 6; f++)
                WriteTranscript("proj-" + p.ToString("00"), "session-" + f + ".jsonl",
                    "{\"timestamp\":\"" + Iso(-300) + "\",\"type\":\"user\"}\n");
        ClaudeSource.Transcripts.ResetCounters();
        ProbeAccount full = Probe(30);
        long allStats = ClaudeSource.Transcripts.StatCalls;
        Check("a sweep with time to spare stats the whole tree",
            allStats == 240, "stats=" + allStats);
        Check("...and reports the honest 'nothing readable' state",
            !full.Ok && (full.Error ?? "").IndexOf("ran out of sweep time", StringComparison.Ordinal) < 0,
            full.Error);

        ClaudeSource.Transcripts.ResetCounters();
        ProbeAccount cut = Probe(0);   // the budget is already spent
        Check("an expired deadline stops the metadata walk", ClaudeSource.Transcripts.StatCalls < allStats,
            "stats=" + ClaudeSource.Transcripts.StatCalls + " of " + allStats);
        Check("...and a cut scan says so instead of claiming nothing was recorded",
            (cut.Error ?? "").IndexOf("ran out of sweep time", StringComparison.Ordinal) >= 0,
            cut.Error);

        // The check an entry-only guard cannot survive: the budget is healthy
        // when the walk STARTS and expires while it is running. An expired
        // budget alone proves nothing — the reported defect (a deadline checked
        // once, then ignored) passes that case too.
        ClaudeSource.Transcripts.ResetCounters();
        double dl = Stamp.Now + 30;
        int ticks = 0;
        ClaudeSource.Transcripts.Clock = delegate { ticks++; return ticks <= 60 ? dl - 1 : dl + 1; };
        ProbeAccount midway;
        try { midway = Probe(30); }
        finally { ClaudeSource.Transcripts.Clock = null; }
        long midStats = ClaudeSource.Transcripts.StatCalls;
        Check("a budget that expires MID-walk stops it where it stands",
            midStats > 0 && midStats < allStats, "stats=" + midStats + " of " + allStats);
        Check("...and that partial walk is reported, not passed off as idle",
            (midway.Error ?? "").IndexOf("ran out of sweep time", StringComparison.Ordinal) >= 0,
            midway.Error);

        Console.WriteLine();
        Console.WriteLine("== an active refusal still overrides every percentage ==");
        NewProfile();
        WriteHistory(200, 20, 20, 120);            // Desktop says 80% left
        WriteTranscript("proj-live", "s.jsonl", RejectedRecord("five_hour", 3600));
        ProbeAccount blocked = Probe(30);
        Check("the refusal zeroes the window the percentage called healthy",
            Remaining(blocked, Model.FIVE_HOUR) == 0.0,
            "5h=" + Remaining(blocked, Model.FIVE_HOUR));
        Check("...and the untouched window keeps its percentage",
            Remaining(blocked, Model.WEEKLY) == 80.0, "weekly=" + Remaining(blocked, Model.WEEKLY));
        Check("...and the account is FRESH because a live refusal is current truth",
            blocked.Ok, blocked.Status);

        // A refusal appearing in a transcript must be seen on the very next
        // sweep: the Desktop cache must not be able to hold a stale no-block
        // decision.
        NewProfile();
        WriteHistory(200, 20, 20, 120);
        Probe(30);
        Check("no block before the refusal is written", Remaining(Probe(30), Model.FIVE_HOUR) == 80.0, "");
        WriteTranscript("proj-live", "s.jsonl", RejectedRecord("five_hour", 3600));
        Check("...and the new refusal lands on the next sweep, cached parse or not",
            Remaining(Probe(30), Model.FIVE_HOUR) == 0.0, "");

        // An expired refusal is not a block.
        NewProfile();
        WriteHistory(200, 20, 20, 120);
        WriteTranscript("proj-old", "s.jsonl", RejectedRecord("five_hour", -60));
        Check("a refusal whose reset already passed does not block",
            Remaining(Probe(30), Model.FIVE_HOUR) == 80.0, "");

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }
}
