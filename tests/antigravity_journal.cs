using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Limisaw;

// PERF-002: the Antigravity refusal journal was scanned without a deadline and
// without a cache. A conversation can hold more message files than the 200-per-
// directory cap, and the cap was applied in whatever order GetFiles answered —
// so the NEWEST refusal, the only one that still matters, could sit outside the
// prefix and never be read, while a full uncapped walk could open 16 dirs x 200
// files x 64KiB — the 200MiB the audit measured, every sweep, forever.
//
// The scan now sorts newest-first BEFORE the cap, stops reading bodies the
// moment the sweep deadline expires (reporting that explicitly instead of
// pretending "no refusal"), and parses a body only when path+mtime+length says
// the file changed. This harness builds real journal fixtures on disk and
// drives the real scan; the counters (AntigravitySource.BodyReads/BytesRead)
// prove the reads actually shrank.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main AntigravityJournalTest)
public static class AntigravityJournalTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Scratch;

    // A fresh profile per scenario: one harness process, but every scan sees
    // exactly the conversations the scenario built.
    static void NewProfile()
    {
        Scratch = Path.Combine(Path.GetTempPath(), "limisaw_agyjournal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        Environment.SetEnvironmentVariable("USERPROFILE", Scratch);
        Environment.SetEnvironmentVariable("HOME", Scratch);
    }

    static string MessagesDir(string conv)
    {
        string dir = Path.Combine(Scratch, ".gemini", "antigravity", "brain", conv, ".system_generated", "messages");
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    public static int Main()
    {
        Console.WriteLine("== newest-first: the cap can no longer hide the live block ==");
        NewProfile();
        // One conversation, 219 old benign messages named to sort FIRST
        // alphabetically, and the ONLY refusal as "zzz-refusal.json" — outside
        // any first-200 prefix the enumeration order could produce, with an
        // active block (Resets in 5h).
        string conv = MessagesDir("conv-big");
        for (int i = 1; i <= 219; i++)
            Write(conv, "msg-" + i.ToString("000") + ".json", Benign, 3600);
        Write(conv, "zzz-refusal.json", Refusal(5, Iso(60)), 60);
        TouchDir(conv, 60);

        double now = Stamp.Now;
        AntigravitySource.ResetBodyCache();
        AntigravitySource.JournalScan scan = AntigravitySource.LatestRefusal(
            AntigravitySource.DataDir(), now, now + 30);
        Check("the newest refusal beyond the old prefix is found",
            scan.Best != null && scan.Best.ResetEpoch > now + 4 * 3600,
            scan.Best == null ? "no refusal" : "resets in "
                + ((scan.Best.ResetEpoch - now) / 3600).ToString("0.0") + "h");
        Check("...with the cap still bounding the opens", scan.OpenedFiles == 200,
            "opened=" + scan.OpenedFiles);
        Check("...and reads far below the 200MiB the audit measured",
            AntigravitySource.BytesRead < 2 * 1024 * 1024,
            "bytes=" + AntigravitySource.BytesRead);

        // W2-005 mirror: the cap's accepted cost, asserted so it cannot drift
        // into an accident. An ACTIVE refusal sitting behind 200 NEWER messages
        // is deliberately not read — the cap is what bounds the scan, and a
        // block that still matters is always in a recent message. Strict
        // Best == null: "it happened to still be active" must not pass this.
        // Fresh profile, or conv-big's refusal above would answer for it.
        NewProfile();
        string convOld = MessagesDir("conv-old");
        for (int i = 1; i <= 200; i++)
            Write(convOld, "msg-" + i.ToString("000") + ".json", Benign, 60);
        Write(convOld, "aaa-older-than-200.json", Refusal(1, Iso(3500)), 3500);
        TouchDir(convOld, 60);
        now = Stamp.Now;
        AntigravitySource.ResetBodyCache();
        AntigravitySource.JournalScan oldScan = AntigravitySource.LatestRefusal(
            AntigravitySource.DataDir(), now, now + 30);
        Check("a refusal behind 200 newer messages is never opened",
            oldScan.Best == null,
            oldScan.Best == null ? "" : "read a file the cap should have cut");
        Check("...because the newest 200 are what filled the cap",
            oldScan.OpenedFiles == 200, "opened=" + oldScan.OpenedFiles);

        Console.WriteLine();
        Console.WriteLine("== the cache: unchanged bodies are never reread ==");
        NewProfile(); // only conv-cache exists here: the counters are unambiguous
        string convCache = MessagesDir("conv-cache");
        for (int i = 1; i <= 30; i++)
            Write(convCache, "m-" + i.ToString("00") + ".json",
                i == 30 ? Refusal(4, Iso(30)) : Benign, i == 30 ? 30 : 120);
        TouchDir(convCache, 30);
        now = Stamp.Now;
        AntigravitySource.ResetBodyCache();
        AntigravitySource.LatestRefusal(AntigravitySource.DataDir(), now, now + 30);
        long firstReads = AntigravitySource.BodyReads;
        Check("the cold sweep reads every fresh file once", firstReads == 30, "reads=" + firstReads);

        long before = AntigravitySource.BodyReads;
        AntigravitySource.JournalScan warm = AntigravitySource.LatestRefusal(
            AntigravitySource.DataDir(), now, now + 30);
        Check("a second unchanged sweep does NO body rereads",
            AntigravitySource.BodyReads == before && warm.Best != null,
            "delta=" + (AntigravitySource.BodyReads - before));

        // Rewrite exactly one file: exactly one body is reread, and the new
        // refusal is the one reported.
        File.WriteAllText(Path.Combine(convCache, "m-30.json"), Refusal(9, Iso(5)));
        File.SetLastWriteTimeUtc(Path.Combine(convCache, "m-30.json"), DateTime.UtcNow);
        AntigravitySource.LatestRefusal(AntigravitySource.DataDir(), now, now + 30);
        Check("a changed file costs exactly one new read",
            AntigravitySource.BodyReads - before == 1,
            "delta=" + (AntigravitySource.BodyReads - before));

        Console.WriteLine();
        Console.WriteLine("== the deadline: an unfinished scan is not 'no refusal' ==");
        AntigravitySource.ResetBodyCache(); // cold again: nothing may serve for free
        now = Stamp.Now;
        AntigravitySource.JournalScan cut = AntigravitySource.LatestRefusal(
            AntigravitySource.DataDir(), now, now); // expired before it began
        Check("an expired deadline stops the scan", cut.DeadlineHit && cut.Best == null,
            "hit=" + cut.DeadlineHit);
        Check("...with (almost) no bodies read", AntigravitySource.BodyReads <= 2,
            "reads=" + AntigravitySource.BodyReads);

        var acc = new ProbeAccount { Provider = "antigravity", ProviderLabel = "Antigravity", Name = "Antigravity" };
        AntigravitySource.Journal(acc, null, now);
        Check("a cut scan reports deadline_exceeded, not the healthy resting state",
            (acc.Error ?? "").IndexOf("ran out of sweep time", StringComparison.Ordinal) >= 0,
            acc.Error);

        // But a scan the cache already answered is not lost to the deadline:
        // the warm refusal is still reported.
        AntigravitySource.LatestRefusal(AntigravitySource.DataDir(), Stamp.Now, Stamp.Now + 30);
        var accWarm = new ProbeAccount { Provider = "antigravity", ProviderLabel = "Antigravity", Name = "Antigravity" };
        AntigravitySource.Journal(accWarm, null, Stamp.Now); // the sweep is out of time
        Check("a warm cache still answers past the deadline",
            accWarm.Ok && accWarm.Windows.Count > 0 && accWarm.Windows[0].Available, "");

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }
}
