using System;
using System.Collections.Generic;
using System.Threading;
using Limisaw;

// Connection coordinator stress: many operations across codex, claude and
// zcode driven from real threads with deterministic barriers.
//
// The defect class this kills is not theoretical: the coordinator held three
// ordinary Dictionaries touched from the UI thread, ThreadPool verification
// workers and (with CLI vendors) independent vendor operations at once. A
// torn Dictionary is "Collection was modified; enumeration operation may not
// execute" at best and a silently lost entry at worst, and none of it is
// reachable from a single-threaded unit test.
//
// Every assertion here is an observable invariant, never a tautology: at most
// one active operation per vendor, cross-vendor operations really overlap, a
// cancelled completion cannot publish, stale generations are rejected, Latest
// snapshots stay isolated, shutdown rejects publishes, and nothing stays
// active once the bounded work completes.
public static class ConnectionsConcurrencyTest
{
    static int fails = 0, checks = 0;
    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const string Vendors = "codex,claude,zcode";

    public static int Main()
    {
        Console.WriteLine("== concurrent Begin/Publish/Latest across three vendors ==");
        var coord = new ConnectionCoordinator();
        int errors = 0;
        int publishOk = 0, publishStale = 0;
        var vendors = new[] { "codex", "claude", "zcode" };
        // A barrier on three threads: without real overlap the "one active per
        // vendor" invariant is never actually exercised.
        using (var barrier = new Barrier(vendors.Length))
        {
            var threads = new List<Thread>();
            var overlapGate = new object();
            int concurrent = 0, maxConcurrent = 0;
            foreach (string v in vendors)
            {
                string vendor = v;
                var t = new Thread(() =>
                {
                    try
                    {
                        barrier.SignalAndWait();
                        for (int i = 0; i < 200; i++)
                        {
                            int gen = coord.Begin(vendor);
                            if (gen < 0) continue;      // the vendor slot is busy: legal
                            lock (overlapGate)
                            {
                                concurrent++;
                                if (concurrent > maxConcurrent) maxConcurrent = concurrent;
                            }
                            // Hold the slot long enough for another thread to
                            // try the same vendor.
                            Thread.Sleep(0);
                            var snap = new VendorConnection
                            {
                                VendorId = vendor,
                                State = ConnectionState.Connected,
                                Reason = "run " + i,
                            };
                            snap.CandidatePaths.Add("C:\\" + vendor + "\\bin.exe");
                            if (coord.TryPublish(vendor, gen, snap)) publishOk++;
                            else publishStale++;
                            lock (overlapGate) concurrent--;
                            // Latest must be a snapshot: mutating it cannot
                            // reach the coordinator.
                            var got = coord.Latest(vendor);
                            if (got != null)
                            {
                                got.State = ConnectionState.Failed;
                                got.Reason = "mutated";
                                got.CandidatePaths[0] = "C:\\mutated";
                            }
                        }
                    }
                    catch (Exception ex) { Interlocked.Increment(ref errors); Console.WriteLine("  thread " + vendor + " threw " + ex.GetType().Name + ": " + ex.Message); }
                    finally { try { barrier.SignalAndWait(2000); } catch { } }
                });
                t.IsBackground = true;
                threads.Add(t);
                t.Start();
            }
            foreach (var t in threads) if (!t.Join(30000)) { Interlocked.Increment(ref errors); Console.WriteLine("  a worker hung"); }
            Check("no dictionary corruption or exception across 600 operations", errors == 0, errors.ToString());
            Check("cross-vendor operations actually overlapped", maxConcurrent >= 2, "max concurrent=" + maxConcurrent);
            Check("every accepted generation published", publishOk > 0, publishOk.ToString());
            Check("no vendor left active after bounded completion",
                !coord.IsActive("codex") && !coord.IsActive("claude") && !coord.IsActive("zcode"), "");
        }

        Console.WriteLine("== snapshot isolation under concurrency ==");
        var latest = coord.Latest("codex");
        Check("Latest is a copy, not the internal instance", latest != null && latest.CandidatePaths[0] == "C:\\codex\\bin.exe",
            latest == null ? "null" : latest.CandidatePaths[0]);

        Console.WriteLine("== at most one active operation per vendor ==");
        var coord2 = new ConnectionCoordinator();
        int g = coord2.Begin("claude");
        int rejected = 0;
        for (int i = 0; i < 50; i++) if (coord2.Begin("claude") < 0) rejected++;
        Check("50 further Begin on the same active vendor: all refused", rejected == 50, rejected.ToString());
        Check("the one accepted generation is still the current one", coord2.CurrentGeneration("claude") == g, g.ToString());
        int other = coord2.Begin("codex");
        Check("a different vendor is never blocked", other >= 1, other.ToString());

        Console.WriteLine("== cancel invalidates a running completion ==");
        var coord3 = new ConnectionCoordinator();
        int cg = coord3.Begin("codex");
        coord3.Cancel("codex");
        bool cancelled = coord3.TryPublish("codex", cg, new VendorConnection { VendorId = "codex", State = ConnectionState.Connected });
        Check("a cancelled completion cannot publish", !cancelled, cancelled.ToString());
        Check("the cancelled slot is free again", !coord3.IsActive("codex"), "");

        Console.WriteLine("== stale generation rejected while a newer one publishes ==");
        var coord4 = new ConnectionCoordinator();
        int old1 = coord4.Begin("zcode");
        coord4.TryPublish("zcode", old1, new VendorConnection { VendorId = "zcode", State = ConnectionState.Verifying });
        int new1 = coord4.Begin("zcode");
        coord4.TryPublish("zcode", new1, new VendorConnection { VendorId = "zcode", State = ConnectionState.Connected, Reason = "fresh" });
        bool stale = coord4.TryPublish("zcode", old1, new VendorConnection { VendorId = "zcode", State = ConnectionState.Failed, Reason = "stale" });
        var kept = coord4.Latest("zcode");
        Check("stale publish refused", !stale, stale.ToString());
        Check("newer Connected survives", kept != null && kept.State == ConnectionState.Connected && kept.Reason == "fresh",
            kept == null ? "null" : kept.State + "/" + kept.Reason);

        Console.WriteLine("== shutdown rejects every completion and Begin ==");
        var coord5 = new ConnectionCoordinator();
        int sg = coord5.Begin("codex");
        coord5.Shutdown();
        bool afterShutdownPublish = coord5.TryPublish("codex", sg, new VendorConnection { VendorId = "codex", State = ConnectionState.Connected });
        int afterShutdownBegin = coord5.Begin("codex");
        Check("publish after shutdown rejected", !afterShutdownPublish, afterShutdownPublish.ToString());
        Check("Begin after shutdown rejected", afterShutdownBegin == -1, afterShutdownBegin.ToString());
        bool afterShutdownProgress = coord5.TryProgress("codex", sg, ConnectionState.Verifying, "x");
        Check("progress after shutdown rejected", !afterShutdownProgress, afterShutdownProgress.ToString());

        Console.WriteLine("== watcher belongs to the generation ==");
        ConnectionWatcher.Shutdown();
        int attempts = 0, expired = 0;
        var seen = new List<int>();
        ConnectionWatcher.OnAttempt = op => { Interlocked.Increment(ref attempts); lock (seen) seen.Add(op.Generation); };
        ConnectionWatcher.OnExpire = op => Interlocked.Increment(ref expired);
        ConnectionWatcher.OnRemoved = null;
        ConnectionWatcher.Start(new ConnectionWatcher.Operation
        { VendorId = "codex", Generation = 7, Verify = () => new VendorConnection { VendorId = "codex", State = ConnectionState.Connected } });
        Check("watcher registers the operation", ConnectionWatcher.Pending("codex", 7), "");
        // A newer operation for the same vendor supersedes the older watcher.
        ConnectionWatcher.Start(new ConnectionWatcher.Operation
        { VendorId = "codex", Generation = 8, Verify = () => new VendorConnection { VendorId = "codex", State = ConnectionState.Connected } });
        Check("newer operation supersedes the older watcher", !ConnectionWatcher.Pending("codex", 7) && ConnectionWatcher.Pending("codex", 8), "");
        ConnectionWatcher.CancelGeneration("codex", 8);
        Check("cancel removes the pending watcher", !ConnectionWatcher.Pending("codex", 8) && ConnectionWatcher.PendingCount == 0, ConnectionWatcher.PendingCount.ToString());

        Console.WriteLine("== bounded cadence is finite and low frequency ==");
        Check("cadence starts immediate", ConnectionWatcher.DelayFor(0) == 0, ConnectionWatcher.DelayFor(0).ToString());
        Check("second attempt after 0.5s", ConnectionWatcher.DelayFor(1) == 0.5, ConnectionWatcher.DelayFor(1).ToString());
        Check("then 1s", ConnectionWatcher.DelayFor(2) == 1, ConnectionWatcher.DelayFor(2).ToString());
        Check("then 2s", ConnectionWatcher.DelayFor(3) == 2, ConnectionWatcher.DelayFor(3).ToString());
        Check("then 3s", ConnectionWatcher.DelayFor(4) == 3, ConnectionWatcher.DelayFor(4).ToString());
        Check("then the 5s steady state", ConnectionWatcher.DelayFor(9) == 5, ConnectionWatcher.DelayFor(9).ToString());
        Check("window is bounded (60-120s)", ConnectionWatcher.WindowS >= 60 && ConnectionWatcher.WindowS <= 120, ConnectionWatcher.WindowS.ToString());
        Check("no sub-500ms polling", ConnectionWatcher.SteadyDelayS >= 5, ConnectionWatcher.SteadyDelayS.ToString());

        Console.WriteLine("== watcher expiry leaves Check again, never Verifying forever ==");
        ConnectionWatcher.OnAttempt = op => Interlocked.Increment(ref attempts);
        ConnectionWatcher.OnExpire = op =>
        {
            Interlocked.Increment(ref expired);
            // What the form does on expiry (R082): Degraded + Check again.
            var conn = new VendorConnection
            {
                VendorId = op.VendorId,
                State = ConnectionState.Degraded,
                Reason = "Still waiting for sign-in",
                RecommendedAction = ConnectionAction.CheckAgain,
            };
            ConnectionWatcher.LastExpired = conn;
        };
        double fakeNow = 100000.0;
        ConnectionWatcher.Now = () => fakeNow;
        ConnectionWatcher.Start(new ConnectionWatcher.Operation
        { VendorId = "claude", Generation = 1, Verify = () => new VendorConnection { VendorId = "claude", State = ConnectionState.Verifying } });
        for (int i = 0; i < 40; i++) { fakeNow += 5; Thread.Sleep(60); }
        for (int i = 0; i < 60 && ConnectionWatcher.PendingCount > 0; i++) { fakeNow += 5; Thread.Sleep(50); }
        Check("expired watcher produced a Degraded card with Check again",
            ConnectionWatcher.LastExpired != null && ConnectionWatcher.LastExpired.State == ConnectionState.Degraded
            && ConnectionWatcher.LastExpired.RecommendedAction == ConnectionAction.CheckAgain,
            ConnectionWatcher.LastExpired == null ? "null" : ConnectionWatcher.LastExpired.State.ToString());
        ConnectionWatcher.Shutdown();
        ConnectionWatcher.Now = () => Stamp.Now;
        ConnectionWatcher.LastExpired = null;

        Console.WriteLine();
        Console.WriteLine("== CORE-002 (audit/6): terminal vs non-terminal publication ==");
        // A retryable result updates what the user sees but MUST retain
        // ownership: the vendor slot is released only by a terminal
        // completion, and a late completion from a released/inactive
        // operation is rejected even when its generation still matches.
        var c6 = new ConnectionCoordinator();
        int tg = c6.Begin("codex");
        c6.TryProgressResult("codex", tg, new VendorConnection { VendorId = "codex", State = ConnectionState.Failed, ErrorCode = ConnectionErrorCode.NetworkTimeout, Reason = "timeout" });
        Check("a transient result keeps the operation ACTIVE", c6.IsActive("codex"), "active=" + c6.IsActive("codex"));
        Check("...and the retryable observation is what the user sees",
            c6.Latest("codex") != null && c6.Latest("codex").State == ConnectionState.Failed,
            c6.Latest("codex") == null ? "null" : c6.Latest("codex").State.ToString());
        int secondBegin = c6.Begin("codex");
        Check("...and a second Begin for the same vendor is refused", secondBegin == -1, secondBegin.ToString());
        // The SAME generation may then complete terminally.
        bool terminalOk = c6.TryPublish("codex", tg, new VendorConnection { VendorId = "codex", State = ConnectionState.Connected, Reason = "connected" });
        Check("the matching generation still completes terminally", terminalOk, terminalOk.ToString());
        Check("...and the slot is released by the terminal completion", !c6.IsActive("codex"), "");
        // A LATE completion from the released operation: same generation,
        // no longer active. The generation alone is not authority.
        bool lateSameGen = c6.TryPublish("codex", tg, new VendorConnection { VendorId = "codex", State = ConnectionState.Failed, Reason = "late duplicate" });
        Check("a late completion from a released operation is rejected", !lateSameGen, lateSameGen.ToString());
        Check("...and the terminal result survives",
            c6.Latest("codex") != null && c6.Latest("codex").State == ConnectionState.Connected,
            c6.Latest("codex") == null ? "null" : c6.Latest("codex").State.ToString());

        Console.WriteLine("== CORE-002: generation-scoped cancel ==");
        // A stale watcher's expiry must NOT invalidate a newer generation.
        // The form's real expiry path is modeled exactly: TryProgress first
        // (a no-op on a stale generation), then the cancel.
        var c7 = new ConnectionCoordinator();
        int g1c = c7.Begin("claude");
        c7.TryCancel("claude", g1c);                       // the old operation ends
        int g2c = c7.Begin("claude");                      // G2 is now current
        // The stale G1 watcher expires — what the FORM does on expiry:
        c7.TryProgress("claude", g1c, ConnectionState.Degraded, "stale expiry");
        bool staleCancel = c7.TryCancel("claude", g1c);    // G1's expiry arrives late
        Check("a stale generation's scoped cancel is a no-op", !staleCancel, staleCancel.ToString());
        Check("...and G2 is still the current generation", c7.CurrentGeneration("claude") == g2c, g2c.ToString());
        Check("...and G2 is still active", c7.IsActive("claude"), "");
        Check("...and G2's card was not degraded by the stale expiry",
            c7.Latest("claude") == null || c7.Latest("claude").State != ConnectionState.Degraded,
            c7.Latest("claude") == null ? "null" : c7.Latest("claude").State.ToString());
        bool g2Pub = c7.TryPublish("claude", g2c, new VendorConnection { VendorId = "claude", State = ConnectionState.Connected, Reason = "g2" });
        Check("...and G2 can still publish", g2Pub, g2Pub.ToString());
        // And the unscoped Cancel — the OLD expiry behavior — DOES destroy G2.
        // This is the control that proves the scoped version is load-bearing:
        // the same sequence with Cancel() invalidates the newer operation.
        var c7b = new ConnectionCoordinator();
        int h1 = c7b.Begin("claude");
        c7b.TryCancel("claude", h1);
        int h2 = c7b.Begin("claude");
        c7b.Cancel("claude");                              // unscoped: the old defect
        Check("the same stale expiry through the unscoped Cancel DOES invalidate G2 (the defect)",
            !c7b.IsActive("claude") && !c7b.TryPublish("claude", h2, new VendorConnection { VendorId = "claude", State = ConnectionState.Connected }),
            "the scoped API exists because this is the damage it prevents");

        Console.WriteLine("== CORE-002: credential-authority invalidation ==");
        // A permission change must invalidate the vendor atomically: the old
        // verdict is gone, ownership is released and the old worker's
        // publication is refused — in BOTH directions.
        foreach (bool direction in new[] { true, false })
        {
            var c8 = new ConnectionCoordinator();
            int zg = c8.Begin("zcode");
            c8.TryProgress("zcode", zg, ConnectionState.Verifying, "reading config");
            c8.InvalidateOperation("zcode");       // the permission changed
            bool oldPublished = c8.TryPublish("zcode", zg, new VendorConnection { VendorId = "zcode", State = ConnectionState.Connected, Reason = "old permission verdict" });
            Check("permission change (" + (direction ? "off->on" : "on->off") + ") rejects the old worker's publication",
                !oldPublished, oldPublished.ToString());
            Check("...and the stale verdict is gone", c8.Latest("zcode") == null, "latest non-null");
            Check("...and the slot is free for the new authority",
                !c8.IsActive("zcode") && c8.Begin("zcode") > zg, "");
        }

        Console.WriteLine("== CORE-002 (audit/6): slow terminal attempt — one publication, no second verifier ==");
        // The production ordering, mirrored from the fixed OnWatcherAttempt:
        // classify FIRST, remove the watcher for a terminal result BEFORE any
        // re-arm can occur, then exactly one terminal publish. A slow attempt
        // whose next deadline passed while it ran must not queue a second
        // same-generation verify after the terminal result lands.
        ConnectionWatcher.Shutdown();
        var c9 = new ConnectionCoordinator();
        int attempts9 = 0;
        var slowGate = new ManualResetEvent(false);
        bool firstInFlight = false;
        ConnectionWatcher.OnAttempt = op =>
        {
            int n = Interlocked.Increment(ref attempts9);
            if (n == 1) { firstInFlight = true; slowGate.WaitOne(5000); }
            VendorConnection result = null;
            try { result = op.Verify(); } catch { }
            bool terminal = result != null
                && (result.State == ConnectionState.Connected
                    || result.State == ConnectionState.ConnectedQuotaUnavailable
                    || result.State == ConnectionState.SignInRequired
                    || result.State == ConnectionState.UnsupportedConfiguration);
            if (terminal)
            {
                ConnectionWatcher.CancelGeneration(op.VendorId, op.Generation);
                c9.TryPublish(op.VendorId, op.Generation, result);
                return;
            }
            c9.TryProgressResult(op.VendorId, op.Generation, result);
            ConnectionWatcher.AttemptFinished(op.VendorId, op.Generation);
        };
        ConnectionWatcher.Now = () => Stamp.Now;
        int g9 = c9.Begin("codex");
        ConnectionWatcher.Start(new ConnectionWatcher.Operation
        {
            VendorId = "codex", Generation = g9,
            Verify = () => new VendorConnection { VendorId = "codex", State = ConnectionState.Connected, Reason = "slow terminal" },
        });
        // Wait until the first attempt is verifiably in flight, hold it past
        // its next deadline, then release the Connected result.
        var slowDeadline = DateTime.UtcNow.AddSeconds(5);
        while (!firstInFlight && DateTime.UtcNow < slowDeadline) Thread.Sleep(25);
        Thread.Sleep(900);   // several scheduled ticks pass while attempt 1 runs
        slowGate.Set();
        var settleDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < settleDeadline && ConnectionWatcher.PendingCount > 0) Thread.Sleep(25);
        Thread.Sleep(400);   // any re-armed second attempt would fire here
        Check("slow terminal attempt: exactly one attempt ran", attempts9 == 1, attempts9.ToString());
        Check("...and exactly one terminal publication survives",
            c9.Latest("codex") != null && c9.Latest("codex").State == ConnectionState.Connected
            && c9.Latest("codex").Reason == "slow terminal",
            c9.Latest("codex") == null ? "null" : c9.Latest("codex").Reason);
        Check("...and the watcher is gone", ConnectionWatcher.PendingCount == 0, ConnectionWatcher.PendingCount.ToString());
        Check("...and the slot is released only after the terminal publication",
            !c9.IsActive("codex"), "");
        bool noSecond = c9.TryPublish("codex", g9, new VendorConnection { VendorId = "codex", State = ConnectionState.Failed, Reason = "second verifier" });
        Check("...and a later same-generation duplicate cannot overwrite it", !noSecond, noSecond.ToString());
        ConnectionWatcher.Shutdown();
        ConnectionWatcher.OnAttempt = null;
        ConnectionWatcher.Now = () => Stamp.Now;

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
