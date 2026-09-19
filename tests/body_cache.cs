using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Limisaw;

// PERF-003 (SRC-005:R014): the Antigravity BodyCache owns its lifetime.
//
// The cache used to grow for the life of the process: every journal path ever
// parsed stayed in the static dictionary forever, so normal journal rotation
// made memory follow historical file churn instead of the current useful
// working set. The only reset was a test helper.
//
// Two bounds now own it, without touching forensic truth — an evicted entry
// can only cost a future safe body reread, never a wrong number:
//   1. live-set pruning after a FULLY COMPLETED scan (a deadline-cut scan
//      prunes NOTHING — an unseen entry may still be live);
//   2. a hard cardinality ceiling (MaxBodyCacheEntries) enforced after every
//      scan, evicting least-recently-seen entries deterministically.
//
// Cache-hit guarantees pinned here: an unchanged current file costs zero body
// reads; a changed current file costs exactly one; a deleted/aged entry dies
// after a complete scan; a partial scan never evicts unseen live entries.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main BodyCacheTest)
public static class BodyCacheTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Scratch;

    const string Benign = "{\"timestamp\":\"2026-09-05T08:00:00Z\",\"sender\":\"user\",\"content\":\"hello there\"}";

    static string Refusal(double hours, string observed)
    {
        return "{\"timestamp\":\"" + observed + "\",\"sender\":\"system\",\"content\":"
            + "\"... RESOURCE_EXHAUSTED (code 429): Individual quota reached. Resets in "
            + hours.ToString("0") + "h0m0s.\"}";
    }

    static string MessagesDir(string conv)
    {
        string dir = Path.Combine(Scratch, ".gemini", "antigravity", "brain", conv, ".system_generated", "messages");
        Directory.CreateDirectory(dir);
        return dir;
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

    static void NewProfile()
    {
        Scratch = Path.Combine(Path.GetTempPath(), "limisaw_bodycache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        Environment.SetEnvironmentVariable("USERPROFILE", Scratch);
        Environment.SetEnvironmentVariable("HOME", Scratch);
    }

    static AntigravitySource.JournalScan Sweep(double budgetS)
    {
        double now = Stamp.Now;
        return AntigravitySource.LatestRefusal(AntigravitySource.DataDir(), now, now + budgetS);
    }

    public static int Main()
    {
        Console.WriteLine("== cache hits: unchanged 0 rereads, changed exactly one ==");
        NewProfile();
        string conv = MessagesDir("conv-hit");
        for (int i = 1; i <= 20; i++) Write(conv, "m-" + i.ToString("00") + ".json", Benign, 120);
        Write(conv, "m-21.json", Refusal(4, Iso(30)), 30);
        TouchDir(conv, 30);
        AntigravitySource.ResetBodyCache();
        Sweep(30);
        long afterCold = AntigravitySource.BodyReads;
        Check("the cold sweep read every fresh file once", afterCold == 21, "reads=" + afterCold);
        Check("...the cache holds exactly those paths", AntigravitySource.BodyCacheCount == 21,
            "count=" + AntigravitySource.BodyCacheCount);
        Sweep(30);
        Check("a second unchanged sweep costs zero body rereads",
            AntigravitySource.BodyReads == afterCold, "delta=" + (AntigravitySource.BodyReads - afterCold));
        File.WriteAllText(Path.Combine(conv, "m-21.json"), Refusal(9, Iso(5)));
        File.SetLastWriteTimeUtc(Path.Combine(conv, "m-21.json"), DateTime.UtcNow);
        Sweep(30);
        Check("a changed current file costs exactly one necessary reread",
            AntigravitySource.BodyReads - afterCold == 1, "delta=" + (AntigravitySource.BodyReads - afterCold));

        Console.WriteLine();
        Console.WriteLine("== complete scan prunes dead entries ==");
        string conv2 = MessagesDir("conv-vanish");
        for (int i = 1; i <= 5; i++) Write(conv2, "v-" + i + ".json", Benign, 60);
        TouchDir(conv2, 60);
        Sweep(30);
        Check("both conversations are cached", AntigravitySource.BodyCacheCount == 26,
            "count=" + AntigravitySource.BodyCacheCount);
        Directory.Delete(conv2, true);
        Sweep(30);
        Check("a COMPLETE scan evicted the deleted conversation's entries",
            AntigravitySource.BodyCacheCount == 21, "count=" + AntigravitySource.BodyCacheCount);
        Check("...the eviction was recorded", AntigravitySource.LastPruneRemoved == 5,
            "removed=" + AntigravitySource.LastPruneRemoved);

        Console.WriteLine();
        Console.WriteLine("== partial scan does NOT evict unseen live entries ==");
        // conv-hit is cached; the sweep dies inside the FIRST directory walk,
        // never reaching conv-hit. Its entries must survive.
        NewProfile();
        MessagesDir("conv-warm");
        for (int i = 1; i <= 3; i++) Write(MessagesDir("conv-warm"), "w-" + i + ".json", Benign, 60);
        TouchDir(MessagesDir("conv-warm"), 60);
        string convKeep = MessagesDir("conv-keep");
        for (int i = 1; i <= 3; i++) Write(convKeep, "k-" + i + ".json", Benign, 60);
        TouchDir(convKeep, 60);
        AntigravitySource.ResetBodyCache();
        Sweep(30);
        Check("both conversations warmed the cache", AntigravitySource.BodyCacheCount == 6,
            "count=" + AntigravitySource.BodyCacheCount);
        double farPast = Stamp.Now - 1000;   // long expired
        var cut = AntigravitySource.LatestRefusal(AntigravitySource.DataDir(), Stamp.Now, farPast);
        Check("the sweep was cut", cut.DeadlineHit, "");
        Check("...the cut scan evicted NOTHING", AntigravitySource.BodyCacheCount == 6,
            "count=" + AntigravitySource.BodyCacheCount);
        // And the surviving entries still serve: a warm refusal answers past
        // the deadline without a body read.
        long readsBefore = AntigravitySource.BodyReads;
        var warm = AntigravitySource.LatestRefusal(AntigravitySource.DataDir(), Stamp.Now, Stamp.Now - 1000);
        Check("...surviving cache entries still answer a later cut scan",
            warm.Best == null && AntigravitySource.BodyReads == readsBefore, "");

        Console.WriteLine();
        Console.WriteLine("== long run: rotating journals converge under the hard cap ==");
        NewProfile();
        AntigravitySource.ResetBodyCache();
        // 600 unique rotating paths over 150 sweeps, 4 live per sweep. No
        // ResetBodyCache call anywhere in the loop — production never calls
        // it. 600 > MaxBodyCacheEntries, so with eviction disabled this grows
        // past the ceiling and the assertion below fails (the red control).
        for (int sweep = 0; sweep < 150; sweep++)
        {
            string convRot = MessagesDir("conv-rot-" + sweep.ToString("000"));
            for (int j = 0; j < 4; j++)
                Write(convRot, "r-" + j + ".json",
                    j == 3 ? Refusal(2, Iso(30 + sweep)) : Benign, 30 + sweep);
            TouchDir(convRot, 30 + sweep);
            // Delete the previous conversation so the complete scan proves
            // its entries dead — the live-set prune's real workload.
            if (sweep >= 1)
            {
                string gone = Path.Combine(Scratch, ".gemini", "antigravity", "brain", "conv-rot-" + (sweep - 1).ToString("000"));
                try { Directory.Delete(gone, true); } catch { }
            }
            Sweep(30);
        }
        Check("the cache converged far below the hard maximum after 150 rotating sweeps",
            AntigravitySource.BodyCacheCount <= AntigravitySource.MaxBodyCacheEntries,
            "count=" + AntigravitySource.BodyCacheCount + " cap=" + AntigravitySource.MaxBodyCacheEntries);
        Check("...and stayed tiny — bounded by the live set, not the 600 historical paths",
            AntigravitySource.BodyCacheCount <= 16, "count=" + AntigravitySource.BodyCacheCount);

        Console.WriteLine();
        Console.WriteLine("== the ceiling binds even when partial scans keep adding ==");
        NewProfile();
        AntigravitySource.ResetBodyCache();
        // 300 fresh conversations, 8 files each. Every scan's deadline dies
        // MID-WALK (50 ms in), so each partial scan caches what it reached
        // and prunes NOTHING — the workload the ceiling exists for.
        for (int i = 0; i < 300; i++)
        {
            string convBig = MessagesDir("conv-p-" + i.ToString("000"));
            for (int j = 0; j < 8; j++) Write(convBig, "p-" + j + ".json", Benign, 60);
            TouchDir(convBig, 60);
        }
        for (int round = 0; round < 40; round++)
        {
            double dying = Stamp.Now + 0.05;
            AntigravitySource.LatestRefusal(AntigravitySource.DataDir(), Stamp.Now, dying);
        }
        Check("partial scans really did cache entries (the workload is real)",
            AntigravitySource.BodyCacheCount > 16, "count=" + AntigravitySource.BodyCacheCount);
        Check("the hard ceiling holds after hundreds of partial scans",
            AntigravitySource.BodyCacheCount <= AntigravitySource.MaxBodyCacheEntries,
            "count=" + AntigravitySource.BodyCacheCount + " cap=" + AntigravitySource.MaxBodyCacheEntries);

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }
}
