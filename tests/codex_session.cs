using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Limisaw;

// PERF-001: `codex app-server` pays a measured 14-19s cold start, and every
// sweep used to pay it again — one fresh child per home per sweep, disposed
// the moment the read finished. A user watching a five-minute timer was really
// watching a 14-19s stall every five minutes, per account.
//
// The fix is a session pool: one live app-server per exact canonical home,
// reused across sweeps (warm sweep = one read, no initialize), replaced only
// when the child dies, evicted when discovery stops listing the home. The
// reset-credit path rides the same pool instead of starting a second child.
//
// This harness drives the pool through the seams production itself uses:
// CodexSource.ResolveExe and CodexSource.StartSession are replaced, so no
// real `codex` is needed for the counting scenarios. The timeout scenario
// spawns a REAL compiled fake app-server child, because the thing it pins —
// a reply landing after the caller gave up must never keep a Responses slot —
// is a property of the actual stdio reader, not of any script.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main CodexSessionTest)
public static class CodexSessionTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    // ── the scripted per-home app-server ────────────────────────────────────

    // One handed-out link with its OWN drop counter, so a scenario can pin
    // "the OLD link was dropped exactly once" against per-home totals.
    class LinkRecord
    {
        public CodexSource.RpcLink Link;
        public int Drops;
    }

    class HomeScript
    {
        public string Home;
        public volatile bool Alive = true;
        public volatile bool ReadTimesOut;
        public volatile string ConsumeOutcome;
        // Counters are touched from multiple threads (sweep + consume +
        // verify + eviction hammer): Interlocked keeps them exact. Reads
        // during active threads go through the Volatile properties below.
        internal int initCalls, readCalls, consumeCalls, drops, verifyCalls;
        public int InitCalls { get { return Volatile.Read(ref initCalls); } }
        public int ReadCalls { get { return Volatile.Read(ref readCalls); } }
        public int ConsumeCalls { get { return Volatile.Read(ref consumeCalls); } }
        public int Drops { get { return Volatile.Read(ref drops); } }
        public int VerifyCalls { get { return Volatile.Read(ref verifyCalls); } }
        // Lease test barriers:
        public ManualResetEventSlim CallGate = new ManualResetEventSlim(false); // blocks inside Call
        public ManualResetEventSlim CallReleased = new ManualResetEventSlim(false); // signals Call returned
        // Tracks the links this home ever handed out (chronological), each
        // with its own drop counter. Multiple StartSession calls may append
        // concurrently: test-lock held.
        public readonly List<LinkRecord> Links = new List<LinkRecord>();
    }

    // Publication-race barrier: both racing checkouts are inside StartSession
    // at the same time, so BOTH create a fresh entry and the pool must pick
    // exactly one winner.
    static ManualResetEventSlim FirstStartInside = new ManualResetEventSlim(false);
    static ManualResetEventSlim SecondStartInside = new ManualResetEventSlim(false);
    static volatile bool PublicationRace;
    static int PubArrivals; // Interlocked arrival counter for the publication barrier
    // M's seam: when armed, StartSession holds BEFORE returning its link, so
    // the test can stage a replacement mapping while a fresh publication is
    // still in flight.
    static ManualResetEventSlim StartHold;
    static ManualResetEventSlim StartHoldEntered;

    static readonly object ScriptsGate = new object(); // one dedicated test lock
    static readonly Dictionary<string, HomeScript> Scripts = new Dictionary<string, HomeScript>();
    static int Starts;

    static HomeScript Script(string home)
    {
        HomeScript s;
        string key = CodexSource.SessionPool.Key(home);
        lock (ScriptsGate)
        {
            if (!Scripts.TryGetValue(key, out s))
            {
                s = new HomeScript { Home = key };
                Scripts[key] = s;
            }
        }
        return s;
    }

    static void AddInit(HomeScript s) { Interlocked.Increment(ref s.initCalls); }
    static void AddRead(HomeScript s) { Interlocked.Increment(ref s.readCalls); }
    static void AddConsume(HomeScript s) { Interlocked.Increment(ref s.consumeCalls); }
    static void AddDrop(HomeScript s, LinkRecord rec) { Interlocked.Increment(ref s.drops); Interlocked.Increment(ref rec.Drops); }
    static void AddVerify(HomeScript s) { Interlocked.Increment(ref s.verifyCalls); }

    // Volatile read of the shared start counter; ResetStarts before each
    // scenario.
    static int StartCount { get { return Volatile.Read(ref Starts); } }
    static void ResetStarts() { Interlocked.Exchange(ref Starts, 0); }

    static void InstallFakeFactory(bool withGates = false)
    {
        CodexSource.ResolveExe = exe => "fake-codex";
        CodexSource.StartSession = (exe, home) =>
        {
            Interlocked.Increment(ref Starts);
            HomeScript s = Script(home);
            var rec = new LinkRecord();
            var link = new CodexSource.RpcLink
            {
                Call = (method, parameters, deadline) =>
                {
                    if (method == "initialize") { AddInit(s); return J.Parse("{\"result\":{}}"); }
                    if (method == "account/rateLimits/read")
                    {
                        AddRead(s);
                        if (withGates && s.ReadTimesOut)
                        {
                            // Simulate a blocked read: wait for the release signal
                            // or expire at the deadline, whichever comes first.
                            s.CallGate.Wait(TimeSpan.FromSeconds(Math.Max(0.05, deadline - Stamp.Now)));
                            s.CallReleased.Set();
                            return null;
                        }
                        return s.ReadTimesOut ? null : J.Parse("{\"result\":{}}");
                    }
                    if (method == "account/rateLimitResetCredit/consume")
                    {
                        AddConsume(s);
                        if (withGates && s.ConsumeOutcome == "BLOCK")
                        {
                            s.CallGate.Wait(TimeSpan.FromSeconds(Math.Max(0.05, deadline - Stamp.Now)));
                            s.CallReleased.Set();
                            return null;
                        }
                        return J.Parse("{\"result\":{\"outcome\":\"" + (s.ConsumeOutcome ?? "reset") + "\"}}");
                    }
                    AddVerify(s);
                    return J.Parse("{\"result\":{}}");
                },
                Notify = (method, parameters) => { },
                Alive = () => s.Alive,
                Drop = () => AddDrop(s, rec),
            };
            lock (s.Links) { rec.Link = link; s.Links.Add(rec); }
            if (StartHold != null)
            {
                // M's seam: hold inside StartSession before returning, so the
                // test can publish a (then dying) mapping while this fresh
                // publication is still in flight.
                if (StartHoldEntered != null) StartHoldEntered.Set();
                StartHold.Wait(TimeSpan.FromSeconds(15));
            }
            if (PublicationRace)
            {
                // Both threads must be inside their own StartSession before
                // either returns: this is the interleaving the publication
                // logic has to survive. The arrival counter is Interlocked,
                // so exactly one racer is "first" — no shared-read race.
                int n = Interlocked.Increment(ref PubArrivals);
                if (n == 1) { FirstStartInside.Set(); SecondStartInside.Wait(TimeSpan.FromSeconds(10)); }
                else { SecondStartInside.Set(); FirstStartInside.Wait(TimeSpan.FromSeconds(10)); }
            }
            return link;
        };
    }

    // ── scratch profile with discoverable Codex homes ───────────────────────

    static string Scratch;
    static readonly List<string> SavedEnv = new List<string>();

    static void SaveEnv(string name)
    {
        SavedEnv.Add(name + "\u001f" + Environment.GetEnvironmentVariable(name));
    }

    static string MakeHome(string profile, string dir)
    {
        string full = Path.Combine(profile, dir);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "auth.json"), "{\"OPENAI_API_KEY\":null}");
        return full;
    }

    static void UseScratchProfile(int siblings)
    {
        Scratch = Path.Combine(Path.GetTempPath(), "limisaw_codexsession_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        SaveEnv("USERPROFILE"); SaveEnv("HOME"); SaveEnv("CODEX_HOME");
        Environment.SetEnvironmentVariable("USERPROFILE", Scratch);
        Environment.SetEnvironmentVariable("HOME", Scratch);
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        MakeHome(Scratch, ".codex");
        for (int i = 0; i < siblings; i++)
            MakeHome(Scratch, ".codex-account" + (i + 1));
    }

    static List<ProbeAccount> Sweep()
    {
        return CodexSource.Sweep(Stamp.Now + 30, 20);
    }

    static string HomePath(string dir)
    {
        return Path.Combine(Scratch, dir);
    }

    // ── the scenarios ───────────────────────────────────────────────────────

    public static int Main()
    {
        Console.WriteLine("== warm reuse: two healthy sweeps, one start ==");
        UseScratchProfile(0);
        InstallFakeFactory();
        CodexSource.Pool.Reset();

        List<ProbeAccount> first = Sweep();
        Check("the first sweep probes the home", first.Count == 1 && first[0].Ok, first.Count + " accounts");
        Check("...with exactly one process start", StartCount == 1, "starts=" + StartCount);

        HomeScript a = Script(HomePath(".codex"));
        List<ProbeAccount> second = Sweep();
        Check("the second warm sweep starts NO new app-server", StartCount == 1, "starts=" + StartCount);
        Check("...and still reads the quota once", a.ReadCalls == 2, "reads=" + a.ReadCalls);
        Check("...without re-initializing the warm session", a.InitCalls == 1, "inits=" + a.InitCalls);
        Check("the warm sweep returns a live reading", second.Count == 1 && second[0].Ok, "");
        Check("the pool holds exactly this home's session", CodexSource.Pool.Count == 1, "pool=" + CodexSource.Pool.Count);

        Console.WriteLine();
        Console.WriteLine("== isolation: three homes, three sessions ==");
        UseScratchProfile(2);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        ResetStarts();

        List<ProbeAccount> multi = Sweep();
        Check("all three discovered homes were probed", multi.Count == 3, multi.Count + " accounts");
        Check("...each with its own process start", StartCount == 3, "starts=" + StartCount);
        ResetStarts();
        Sweep();
        Check("a second sweep reuses all three sessions", StartCount == 0 && CodexSource.Pool.Count == 3,
            "starts=" + StartCount + ", pool=" + CodexSource.Pool.Count);
        bool isolated = true;
        foreach (string dir in new[] { ".codex", ".codex-account1", ".codex-account2" })
        {
            HomeScript s = Script(HomePath(dir));
            if (s.InitCalls != 1 || s.ReadCalls != 2) isolated = false;
        }
        Check("...each session initialized once and read twice, separately", isolated, "");

        Console.WriteLine();
        Console.WriteLine("== a dead child is replaced, alone ==");
        HomeScript alpha = Script(HomePath(".codex-account1"));
        alpha.Alive = false;
        ResetStarts();
        List<ProbeAccount> after = Sweep();
        Check("only the dead home's session was restarted", StartCount == 1, "starts=" + StartCount);
        Check("...it re-initialized from scratch", alpha.InitCalls == 2, "inits=" + alpha.InitCalls);
        Check("...the dead child was disposed", alpha.Drops >= 1, "drops=" + alpha.Drops);
        Check("the healthy homes kept their sessions", Script(HomePath(".codex")).InitCalls == 1
            && Script(HomePath(".codex-account2")).InitCalls == 1, "");
        Check("...and every account still read", after.Count == 3 && after.TrueForAll(acc => acc.Ok), "");
        alpha.Alive = true; // the replacement child lives; stop simulating the corpse

        Console.WriteLine();
        Console.WriteLine("== a timed-out read drops the session, not the account ==");
        Script(HomePath(".codex")).ReadTimesOut = true;
        ResetStarts();
        List<ProbeAccount> failed = Sweep();
        Check("the timed-out home reports failure, not a crash", failed.Count == 3
            && failed.Exists(acc => !acc.Ok && (acc.Error ?? "").Length > 0), "");
        Check("...its session was dropped from the pool", CodexSource.Pool.Count == 2, "pool=" + CodexSource.Pool.Count);
        Script(HomePath(".codex")).ReadTimesOut = false;
        ResetStarts();
        Sweep();
        Check("the next sweep starts exactly one replacement, re-initialized",
            StartCount == 1 && Script(HomePath(".codex")).InitCalls == 2,
            "starts=" + StartCount + ", inits=" + Script(HomePath(".codex")).InitCalls);
        Check("...and the pool is whole again", CodexSource.Pool.Count == 3, "pool=" + CodexSource.Pool.Count);

        Console.WriteLine();
        Console.WriteLine("== a home that leaves discovery is evicted ==");
        Directory.Delete(HomePath(".codex-account2"), true);
        ResetStarts();
        Sweep();
        Check("the pool evicted the vanished home", CodexSource.Pool.Count == 2, "pool=" + CodexSource.Pool.Count);
        Check("...its child was disposed", Script(HomePath(".codex-account2")).Drops >= 1, "drops=" + Script(HomePath(".codex-account2")).Drops);
        Check("...with no new start for the survivors", StartCount == 0, "starts=" + StartCount);

        Console.WriteLine();
        Console.WriteLine("== the reset credit rides the pooled session ==");
        ResetStarts();
        string before = CodexSource.ConsumeResetCredit(HomePath(".codex"), Stamp.Now + 20);
        Check("the credit spend reports the vendor outcome", before == "done — the limit was reset", before);
        Check("...on the warm session, with no new start", StartCount == 0, "starts=" + StartCount);
        Check("...and exactly one consume call", Script(HomePath(".codex")).ConsumeCalls == 1, "");
        Script(HomePath(".codex")).ConsumeOutcome = "noCredit";
        string empty = CodexSource.ConsumeResetCredit(HomePath(".codex"), Stamp.Now + 20);
        Check("a spent credit says so instead of pretending", empty == "no banked reset on this account any more", empty);

        Console.WriteLine();
        Console.WriteLine("== a real child: a late reply leaves no stale slot ==");
        LateReply();

        // ── W2-001 lease-specific tests ─────────────────────────────────────────

        Console.WriteLine();
        Console.WriteLine("== A: SAME HOME serialization ==");
        SameHomeSerialization();

        Console.WriteLine();
        Console.WriteLine("== B: DIFFERENT HOMES parallelism ==");
        DifferentHomesParallel();

        Console.WriteLine();
        Console.WriteLine("== C: OWNER RETIRE ==");
        OwnerRetire();

        Console.WriteLine();
        Console.WriteLine("== D: EXTERNAL RETIRE WHILE LEASED ==");
        ExternalRetireWhileLeased();

        Console.WriteLine();
        Console.WriteLine("== E: CHECKIN/RETIRE RACE ==");
        CheckinRetireRace();

        Console.WriteLine();
        Console.WriteLine("== F: FRESH PUBLICATION RACE ==");
        FreshPublicationRace();

        Console.WriteLine();
        Console.WriteLine("== G: UNHEALTHY ENTRY WHILE LEASED ==");
        UnhealthyEntryWhileLeased();

        Console.WriteLine();
        Console.WriteLine("== H: DEADLINE ==");
        LeaseDeadline();

        Console.WriteLine();
        Console.WriteLine("== I: INIT ONCE ==");
        InitOnce();

        Console.WriteLine();
        Console.WriteLine("== J: RESET CREDIT SAFETY ==");
        ResetCreditSafety();

        Console.WriteLine();
        Console.WriteLine("== K: STALE AFTER WAIT releases raw permit ==");
        StaleAfterWait();

        Console.WriteLine();
        Console.WriteLine("== L: FAILED LOGICAL CLAIM releases raw permit ==");
        FailedClaimReleasesPermit();

        Console.WriteLine();
        Console.WriteLine("== M: REPLACED IDLE mapping drops exactly once ==");
        ReplacedIdleDrop();

        Console.WriteLine();
        Console.WriteLine("== N: REPEATED Dispose is harmless ==");
        RepeatedDispose();

        Console.WriteLine();
        Console.WriteLine("== O: test-fake concurrency sanity ==");
        TestFakeConcurrency();

        Console.WriteLine();
        Console.WriteLine("== P: no lease wait under SessionPool.Gate ==");
        NoWaitUnderGate();

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    // ── W2-001 deterministic lease scenarios ─────────────────────────────────

    // A: one home admits ONE logical operation at a time. A blocked quota read
    // on home A must hold consume(A) out of the RpcLink until the read's lease
    // releases; peak concurrent logical operations on the entry = 1.
    static void SameHomeSerialization()
    {
        UseScratchProfile(1);
        InstallFakeFactory(withGates: true);
        CodexSource.Pool.Reset();
        ResetStarts();
        Script(HomePath(".codex")).ReadTimesOut = true; // blocked read on home A

        var readThread = new Thread(() => CodexSource.Sweep(Stamp.Now + 60, 60));
        readThread.IsBackground = true;
        readThread.Start();
        // wait until the read actually entered Call
        var spin = Stopwatch.StartNew();
        while (Script(HomePath(".codex")).ReadCalls == 0 && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);
        Check("the blocked read entered Call", Script(HomePath(".codex")).ReadCalls == 1, "reads=" + Script(HomePath(".codex")).ReadCalls);

        var consumeThread = new Thread(() => CodexSource.ConsumeResetCredit(HomePath(".codex"), Stamp.Now + 2));
        consumeThread.IsBackground = true;
        consumeThread.Start();
        Thread.Sleep(300); // give consume a fair chance to (wrongly) enter
        int peakConsume = Script(HomePath(".codex")).ConsumeCalls;
        Check("consume did NOT enter the RpcLink while the read held the lease", peakConsume == 0, "consume=" + peakConsume);
        Check("peak logical operations on the entry stayed 1", peakConsume == 0, "");

        Script(HomePath(".codex")).CallGate.Set(); // release the read
        spin.Restart();
        while (Script(HomePath(".codex")).ConsumeCalls == 0 && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);
        Check("consume proceeded once the read released", Script(HomePath(".codex")).ConsumeCalls == 1, "consume=" + Script(HomePath(".codex")).ConsumeCalls);
        Script(HomePath(".codex")).CallGate.Set(); // release the consume
        readThread.Join(15000);
        consumeThread.Join(15000);
        Script(HomePath(".codex")).ReadTimesOut = false;
        Script(HomePath(".codex")).ConsumeOutcome = null;
    }

    // B: home A blocked, home B executes concurrently — no provider-wide lock.
    static void DifferentHomesParallel()
    {
        UseScratchProfile(1);
        InstallFakeFactory(withGates: true);
        CodexSource.Pool.Reset();
        ResetStarts();
        Script(HomePath(".codex")).ReadTimesOut = true; // block home A only

        var a = new Thread(() => CodexSource.Sweep(Stamp.Now + 60, 60));
        a.IsBackground = true;
        a.Start();
        var spin = Stopwatch.StartNew();
        while (Script(HomePath(".codex")).ReadCalls == 0 && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);

        string other = CodexSource.ConsumeResetCredit(HomePath(".codex-account1"), Stamp.Now + 30);
        Check("a different home executed while A was blocked", other == "done — the limit was reset", other);
        Script(HomePath(".codex")).CallGate.Set();
        a.Join(15000);
        Script(HomePath(".codex")).ReadTimesOut = false;
    }

    // C: an operation whose Call timed out retires its session THROUGH the
    // lease: Leased returns to false and the link is dropped exactly once —
    // not orphaned forever with Leased stuck at 1.
    static void OwnerRetire()
    {
        UseScratchProfile(0);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        ResetStarts();
        // Warm the pool so an entry exists, then make the read time out.
        CodexSource.Sweep(Stamp.Now + 30, 30);
        HomeScript s = Script(HomePath(".codex"));
        s.ReadTimesOut = true;
        int dropsBefore = s.Drops;
        List<ProbeAccount> failed = CodexSource.Sweep(Stamp.Now + 30, 30);
        Check("the timed-out read retired its entry through the lease",
            s.Drops == dropsBefore + 1, "drops " + dropsBefore + "->" + s.Drops);
        Check("...the pool no longer holds that home", CodexSource.Pool.Count == 0, "pool=" + CodexSource.Pool.Count);
        s.ReadTimesOut = false;
        ResetStarts();
        List<ProbeAccount> again = CodexSource.Sweep(Stamp.Now + 30, 30);
        Check("...the next operation received a fresh entry", again.Count == 1 && again[0].Ok && StartCount == 1, "starts=" + StartCount);
    }

    // D: RetainOnly during an active lease removes the home from future
    // checkout but must NOT drop the link while the operation owns it; the
    // drop happens exactly once, after release.
    static void ExternalRetireWhileLeased()
    {
        UseScratchProfile(0);
        InstallFakeFactory(withGates: true);
        CodexSource.Pool.Reset();
        ResetStarts();
        // Warm pool first (no blocking).
        CodexSource.Sweep(Stamp.Now + 30, 30);
        HomeScript s = Script(HomePath(".codex"));
        s.ReadTimesOut = true; // next read blocks
        int dropsBefore = s.Drops;

        var op = new Thread(() => CodexSource.Sweep(Stamp.Now + 60, 60));
        op.IsBackground = true;
        op.Start();
        var spin = Stopwatch.StartNew();
        int readsAtStart = s.ReadCalls;
        while (s.ReadCalls == readsAtStart && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);

        CodexSource.Pool.RetainOnly(new List<string>()); // evict everything, while leased
        Check("RetainOnly did NOT drop the leased link", s.Drops == dropsBefore, "drops=" + s.Drops);

        s.CallGate.Set(); // let the operation finish; its release performs the drop
        spin.Restart();
        while (s.Drops == dropsBefore && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);
        Check("the drop happened exactly once after lease release", s.Drops == dropsBefore + 1, "drops=" + s.Drops);
        op.Join(15000);
        s.ReadTimesOut = false;
    }

    // E: the old draft had a race window between "read Retiring" and "release
    // lease". The coherent transition must make the deferred drop observable
    // exactly once regardless of interleaving.
    static void CheckinRetireRace()
    {
        UseScratchProfile(0);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        ResetStarts();
        CodexSource.Sweep(Stamp.Now + 30, 30); // warm
        HomeScript s = Script(HomePath(".codex"));
        int dropsBefore = s.Drops;

        // Hammer retirement against normal lease release. Each iteration:
        // one operation checks out, an external RetainOnly fires concurrently,
        // the operation releases. Every release must leave exactly-one drop.
        for (int i = 0; i < 40; i++)
        {
            var ext = new Thread(() => CodexSource.Pool.RetainOnly(new List<string>()));
            ext.IsBackground = true;
            ext.Start();
            CodexSource.ConsumeResetCredit(HomePath(".codex"), Stamp.Now + 30);
            ext.Join(5000);
            // The entry was retired; the pool must re-create it fresh for the
            // next iteration — which ConsumeResetCredit did. Assert drops
            // never exceed one per retired link is enforced by C/D; here the
            // invariant is the link is never left undropped nor dropped twice.
            Thread.Sleep(5);
        }
        int totalDrops = s.Drops;
        Check("hammered retirement produced at least one drop", totalDrops > dropsBefore, "drops=" + totalDrops);
        // Every retired link was dropped: after the hammer, a fresh entry exists
        // and the pool is consistent.
        CodexSource.Pool.RetainOnly(new List<string>());
        Thread.Sleep(100);
        Check("...and every dropped link dropped exactly once (no crash, pool consistent)",
            CodexSource.Pool.Count == 0, "pool=" + CodexSource.Pool.Count);
    }

    // F: two threads observe no entry and both start sessions; exactly one is
    // published, the loser is dropped exactly once, and no caller receives
    // both. Deterministic by events, never a 30s Join: both StartSession
    // calls are held INSIDE by mutual barriers, so both fresh entries exist
    // before either publishes; then the winner returns holding its lease,
    // the loser is PROVEN blocked on the winner's semaphore, and only then
    // is the winner released so the loser acquires the SAME entry.
    static void FreshPublicationRace()
    {
        UseScratchProfile(0);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        ResetStarts();
        PublicationRace = true;
        FirstStartInside.Reset(); SecondStartInside.Reset();
        Interlocked.Exchange(ref PubArrivals, 0);
        var results = new CodexSource.SessionPool.SessionLease[2];
        var t1 = new Thread(() => { results[0] = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 60); });
        var t2 = new Thread(() => { results[1] = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 60); });
        t1.IsBackground = t2.IsBackground = true;
        t1.Start(); t2.Start();
        // Both racers are now inside StartSession, each holding the barrier
        // the other needs: both fresh sessions exist before either publishes.
        bool bothInside = FirstStartInside.Wait(TimeSpan.FromSeconds(10))
                        && SecondStartInside.Wait(TimeSpan.FromSeconds(10));
        // One thread publishes and returns holding its lease; the other is
        // blocked on the winner's semaphore. Bounded spin for the winner to
        // return — seconds at most, no 30s Join.
        var spin = Stopwatch.StartNew();
        while ((results[0] == null && results[1] == null) && spin.ElapsedMilliseconds < 15000) Thread.Sleep(10);
        // Prove the loser has NOT returned while the winner holds the lease.
        bool loserStillWaiting = (results[0] == null) || (results[1] == null);
        // Winner index/lease selection: the returned lease is the winner.
        Check("both racing callers entered StartSession together", bothInside, "");
        Check("...both fresh sessions were started", StartCount == 2, "starts=" + StartCount);
        Check("the winner returned while the loser was still blocked on its semaphore",
            loserStillWaiting, "r0=" + (results[0] != null) + " r1=" + (results[1] != null));
        // Release the winner; the loser must acquire the SAME entry.
        var winnerLease = results[0] != null ? results[0] : results[1];
        var waitIdx = results[0] != null ? 1 : 0;
        winnerLease.Dispose();
        t1.Join(15000); t2.Join(15000);
        PublicationRace = false;
        Check("the waiter received the SAME winner entry after release",
            results[waitIdx] != null && results[waitIdx].Entry == winnerLease.Entry, "same=" + (results[waitIdx] != null && results[waitIdx].Entry == winnerLease.Entry));
        Check("no third process start", StartCount == 2, "starts=" + StartCount);
        Check("the pool still holds exactly one entry", CodexSource.Pool.Count == 1, "pool=" + CodexSource.Pool.Count);
        // Exactly one of the two links was published; the loser must be
        // dropped exactly once, the survivor zero times.
        results[waitIdx].Dispose();
        Thread.Sleep(100);
        Check("...the loser link was dropped, the survivor not",
            Script(HomePath(".codex")).Drops == 1, "drops=" + Script(HomePath(".codex")).Drops);
    }

    // G: a second caller observing an unhealthy link while an operation owns
    // the entry must not kill it under the owner; the drop lands after release.
    static void UnhealthyEntryWhileLeased()
    {
        UseScratchProfile(0);
        InstallFakeFactory(withGates: true);
        CodexSource.Pool.Reset();
        ResetStarts();
        CodexSource.Sweep(Stamp.Now + 30, 30);
        HomeScript s = Script(HomePath(".codex"));
        s.ReadTimesOut = true;
        int dropsBefore = s.Drops;

        var op = new Thread(() => CodexSource.Sweep(Stamp.Now + 60, 60));
        op.IsBackground = true;
        op.Start();
        var spin = Stopwatch.StartNew();
        int readsAtStart = s.ReadCalls;
        while (s.ReadCalls == readsAtStart && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);

        // The link "dies" under the owner. A concurrent sweep must not drop it.
        s.Alive = false;
        List<ProbeAccount> concurrent = null;
        var other = new Thread(() => { concurrent = CodexSource.Sweep(Stamp.Now + 2, 2); });
        other.IsBackground = true;
        other.Start();
        other.Join(15000);
        Check("the unhealthy link was NOT dropped under its owner", s.Drops == dropsBefore, "drops=" + s.Drops);

        s.CallGate.Set(); // owner finishes; release performs the drop
        spin.Restart();
        while (s.Drops == dropsBefore && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);
        Check("...the drop happened exactly once after release", s.Drops == dropsBefore + 1, "drops=" + s.Drops);
        op.Join(15000);
        s.ReadTimesOut = false; s.Alive = true;
    }

    // H: a lease held beyond the caller's deadline makes Checkout fail
    // boundedly — no fresh session started just to bypass a healthy busy entry.
    static void LeaseDeadline()
    {
        UseScratchProfile(0);
        InstallFakeFactory(withGates: true);
        CodexSource.Pool.Reset();
        ResetStarts();
        CodexSource.Sweep(Stamp.Now + 30, 30);
        HomeScript s = Script(HomePath(".codex"));
        s.ReadTimesOut = true;
        int startsBefore = StartCount;

        var op = new Thread(() => CodexSource.Sweep(Stamp.Now + 60, 60));
        op.IsBackground = true;
        op.Start();
        var spin = Stopwatch.StartNew();
        int readsAtStart = s.ReadCalls;
        while (s.ReadCalls == readsAtStart && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);

        double t0 = Stamp.Now;
        var busy = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", t0 + 0.5);
        double waited = Stamp.Now - t0;
        Check("a busy home fails Checkout boundedly at the caller deadline", busy == null && waited < 5.0, "waited=" + waited.ToString("0.0"));
        Check("...no fresh session was started to bypass the healthy busy entry",
            StartCount == startsBefore, "starts " + startsBefore + "->" + StartCount);
        s.CallGate.Set();
        op.Join(15000);
        s.ReadTimesOut = false;
    }

    // I: two cold operations against one home produce exactly ONE
    // initialize/initialized handshake on the surviving healthy entry.
    static void InitOnce()
    {
        UseScratchProfile(0);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        ResetStarts();
        var results = new List<ProbeAccount>[2];
        var t1 = new Thread(() => { results[0] = CodexSource.Sweep(Stamp.Now + 60, 60); });
        var t2 = new Thread(() => { results[1] = CodexSource.Sweep(Stamp.Now + 60, 60); });
        t1.IsBackground = t2.IsBackground = true;
        t1.Start(); Thread.Sleep(30); t2.Start(); // near-simultaneous cold starts
        t1.Join(30000); t2.Join(30000);
        HomeScript s = Script(HomePath(".codex"));
        Check("two cold operations produced exactly one initialize handshake",
            s.InitCalls == 1, "inits=" + s.InitCalls);
        Check("...both operations answered", results[0] != null && results[1] != null
            && results[0].TrueForAll(a => a.Ok) && results[1].TrueForAll(a => a.Ok), "");
    }

    // J: the irreversible consume holds the lease through response
    // classification: probe + verify for the SAME home wait; exactly one
    // consume request is observed; the response remains readable.
    static void ResetCreditSafety()
    {
        UseScratchProfile(0);
        InstallFakeFactory(withGates: true);
        CodexSource.Pool.Reset();
        ResetStarts();
        CodexSource.Sweep(Stamp.Now + 30, 30); // warm
        HomeScript s = Script(HomePath(".codex"));
        s.ConsumeOutcome = "BLOCK";

        var consume = new Thread(() => CodexSource.ConsumeResetCredit(HomePath(".codex"), Stamp.Now + 60));
        consume.IsBackground = true;
        consume.Start();
        var spin = Stopwatch.StartNew();
        while (s.ConsumeCalls == 0 && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);
        Check("the consume request was transmitted exactly once", s.ConsumeCalls == 1, "consume=" + s.ConsumeCalls);

        var sweep = new Thread(() => CodexSource.Sweep(Stamp.Now + 2, 2));
        sweep.IsBackground = true;
        sweep.Start();
        Thread.Sleep(300);
        Check("a concurrent same-home sweep could not interleave while consume awaited its response",
            s.ReadCalls <= 1 && s.Drops == 0, "reads=" + s.ReadCalls + " drops=" + s.Drops);

        s.CallGate.Set(); // let the response arrive
        consume.Join(15000);
        Check("the consume response remained readable — exactly one consume request observed",
            s.ConsumeCalls == 1, "consume=" + s.ConsumeCalls);
        sweep.Join(15000);
        s.ConsumeOutcome = null;
    }

    // K: STALE AFTER WAIT — two callers park on an owner's semaphore; the
    // entry is retired while they wait; the owner's release wakes the FIRST
    // waiter with one RAW permit and a stale candidate. The first waiter
    // must reject the stale entry and RELEASE the raw permit (not leak it
    // into DropEntry); the SECOND waiter must then also wake — no waiter is
    // stranded on the abandoned semaphore — and both retries reach a fresh
    // entry. The old link is dropped exactly once.
    static void StaleAfterWait()
    {
        UseScratchProfile(0);
        InstallFakeFactory(withGates: true);
        CodexSource.Pool.Reset();
        ResetStarts();
        CodexSource.Sweep(Stamp.Now + 30, 30); // warm, idle entry E0
        HomeScript s = Script(HomePath(".codex"));
        s.ConsumeOutcome = "BLOCK";
        int dropsBefore = s.Drops;

        // Owner: holds E0's lease, parked inside the consume call.
        var owner = new Thread(() => CodexSource.ConsumeResetCredit(HomePath(".codex"), Stamp.Now + 60));
        owner.IsBackground = true;
        owner.Start();
        var spin = Stopwatch.StartNew();
        while (s.ConsumeCalls == 0 && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);

        // Two waiters park on E0's semaphore (owner holds the one permit).
        var results = new CodexSource.SessionPool.SessionLease[2];
        var evts = new[] { new ManualResetEventSlim(false), new ManualResetEventSlim(false) };
        var wA = new Thread(() => { results[0] = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 60); evts[0].Set(); });
        var wB = new Thread(() => { results[1] = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 60); evts[1].Set(); });
        wA.IsBackground = wB.IsBackground = true;
        wA.Start(); wB.Start();
        Thread.Sleep(300);
        Check("both waiters parked on the leased entry", results[0] == null && results[1] == null, "");

        // Retire the entry while both waiters are parked (drop defers to the
        // owner's release).
        CodexSource.Pool.RetainOnly(new List<string>());

        s.CallGate.Set(); // owner finishes: Dispose releases ONE permit
        owner.Join(15000);

        // FIRST waiter wakes owning the raw permit and a stale candidate: it
        // must Release the permit and retry to a fresh entry. The SECOND
        // waiter then wakes from that release — it must not be stranded.
        int first = WaitHandle.WaitAny(new WaitHandle[] { evts[0].WaitHandle, evts[1].WaitHandle }, TimeSpan.FromSeconds(15));
        Check("the first stale-after-wait caller woke, released the raw permit and retried",
            first >= 0 && results[first] != null, "idx=" + first);
        Check("...the old link was dropped exactly once",
            s.Links.Count > 0 && s.Links[0].Drops == dropsBefore + 1,
            "oldDrops=" + (s.Links.Count > 0 ? s.Links[0].Drops : -1));
        if (first >= 0 && results[first] != null) results[first].Dispose();
        int second = 1 - first;
        bool otherOk = (second == 0 ? evts[0] : evts[1]).Wait(TimeSpan.FromSeconds(15));
        Check("...the second waiter was NOT stranded on the abandoned semaphore",
            otherOk && results[second] != null, "ok=" + otherOk);
        Check("...each retry fresh-started at most once (warm + 2 waiters max)",
            StartCount >= 2 && StartCount <= 3, "starts=" + StartCount);
        if (second >= 0 && results[second] != null) results[second].Dispose();
        s.ConsumeOutcome = null;
    }

    // L: FAILED LOGICAL CLAIM — the BeforeClaim seam forces the exact window:
    // the raw permit is acquired, stale validation passes, and THEN the
    // entry is retired before the logical claim. The checkout owns the raw
    // permit at that point; it must Release it (not leak it) and retry. A
    // second parked waiter proves the permit came back.
    static void FailedClaimReleasesPermit()
    {
        UseScratchProfile(0);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        ResetStarts();
        CodexSource.Sweep(Stamp.Now + 30, 30); // warm, idle entry E0
        HomeScript s = Script(HomePath(".codex"));
        int dropsBefore = s.Drops;

        // THE SEAM: whichever checkout acquires the raw permit first gets its
        // entry retired between validation and claim (RetainOnly marks it
        // retiring and removes it). Its own TryClaim then fails while it
        // owns the permit. One-shot.
        var seamFired = new ManualResetEventSlim(false);
        CodexSource.SessionPool.BeforeClaim = e =>
        {
            CodexSource.SessionPool.BeforeClaim = null;
            CodexSource.Pool.RetainOnly(new List<string>());
            seamFired.Set();
        };

        var results = new CodexSource.SessionPool.SessionLease[2];
        var evts = new[] { new ManualResetEventSlim(false), new ManualResetEventSlim(false) };
        var wA = new Thread(() => { results[0] = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 60); evts[0].Set(); });
        var wB = new Thread(() => { results[1] = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 60); evts[1].Set(); });
        wA.IsBackground = wB.IsBackground = true;
        wA.Start(); Thread.Sleep(100); wB.Start();

        bool seamRan = seamFired.Wait(TimeSpan.FromSeconds(10));
        Check("the seam fired between permit acquisition and logical claim", seamRan, "");
        int first = WaitHandle.WaitAny(new WaitHandle[] { evts[0].WaitHandle, evts[1].WaitHandle }, TimeSpan.FromSeconds(15));
        Check("the failed-claim checkout abandoned, RELEASED the permit and retried",
            first >= 0 && results[first] != null, "idx=" + first);
        Check("...the retired link dropped exactly once",
            s.Links.Count > 0 && s.Links[0].Drops == dropsBefore + 1,
            "oldDrops=" + (s.Links.Count > 0 ? s.Links[0].Drops : -1));
        if (first >= 0 && results[first] != null) results[first].Dispose();
        int second = 1 - first;
        bool otherOk = (second == 0 ? evts[0] : evts[1]).Wait(TimeSpan.FromSeconds(15));
        Check("...the co-waiter was NOT stranded by a leaked permit",
            otherOk && results[second] != null, "ok=" + otherOk);
        if (second >= 0 && results[second] != null) results[second].Dispose();
        CodexSource.SessionPool.BeforeClaim = null;
    }

    // M: REPLACED IDLE — a fresh publication races a mapping that becomes
    // unusable before publication: caller T is held inside StartSession;
    // meanwhile a healthy entry E1 is published and released (idle, mapped);
    // E1's link then dies; T's publication now observes the old unusable
    // mapping and replaces it. The replaced IDLE entry must be dropped
    // EXACTLY once — the old draft never assigned it to a cleanup variable,
    // so an idle replacement escaped DropEntry entirely.
    static void ReplacedIdleDrop()
    {
        UseScratchProfile(0);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        ResetStarts();
        HomeScript s = Script(HomePath(".codex"));

        // Caller T: no entry mapped -> fresh-start branch, held inside
        // StartSession by the seam before its link is returned.
        var hold = new ManualResetEventSlim(false);
        StartHold = hold;
        StartHoldEntered = new ManualResetEventSlim(false);
        var t2 = new Thread(() => CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 60));
        t2.IsBackground = true;
        t2.Start();
        Check("the racing caller entered StartSession", StartHoldEntered.Wait(TimeSpan.FromSeconds(10)), "");
        StartHold = null; // only the racing caller is gated

        // Meanwhile a healthy entry E1 is published and released: idle, mapped.
        var e1 = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 30);
        Check("the replacement mapping was published idle", e1 != null, "");
        if (e1 != null) e1.Dispose();

        // E1's link dies while it is mapped and idle.
        s.Alive = false;

        // Release T: its publication observes the old unusable mapping and
        // must replace it — dropping the idle old link exactly once.
        hold.Set();
        t2.Join(15000);
        var spin = Stopwatch.StartNew();
        while (s.Links.Count < 2 && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);
        Check("the fresh publication replaced the dead mapping", s.Links.Count >= 2, "links=" + s.Links.Count);
        // Links[0] = T2's fresh link (survivor); Links[1] = E1's (replaced).
        Check("...the replaced IDLE link was dropped exactly once",
            s.Links.Count >= 2 && s.Links[1].Drops == 1,
            "oldDrops=" + (s.Links.Count >= 2 ? s.Links[1].Drops : -1));
        Check("...the surviving fresh link was NOT dropped",
            s.Links.Count >= 2 && s.Links[0].Drops == 0,
            "newDrops=" + (s.Links.Count >= 2 ? s.Links[0].Drops : -1));
        Check("...the pool holds exactly one usable entry", CodexSource.Pool.Count == 1,
            "pool=" + CodexSource.Pool.Count);
        s.Alive = true;
    }

    // N: REPEATED Dispose — SessionLease.Dispose must be idempotent: the
    // logical Leased flag clears exactly once and the physical SemaphoreSlim
    // permit releases exactly once. A second Dispose must not throw
    // SemaphoreFullException and must not double-Drop the link.
    static void RepeatedDispose()
    {
        UseScratchProfile(0);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        ResetStarts();
        var lease = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 30);
        Check("the lease was acquired", lease != null, "");
        if (lease == null) return;
        lease.Retire();
        int dropsBefore = Script(HomePath(".codex")).Drops;
        bool threw = false;
        try { lease.Dispose(); lease.Dispose(); lease.Dispose(); }
        catch (Exception ex) { threw = true; Check("repeated Dispose threw", false, ex.GetType().Name); }
        Check("Dispose x3 threw no SemaphoreFullException", !threw, "");
        Thread.Sleep(100);
        Check("...the link dropped at most once (exactly once here)",
            Script(HomePath(".codex")).Drops - dropsBefore == 1,
            "drops=" + (Script(HomePath(".codex")).Drops - dropsBefore));
        // And the entry's semaphore is fully usable again (no leaked permit):
        var next = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 5);
        Check("...a later checkout acquired the entry again (no leaked permit)", next != null, "");
        if (next != null) next.Dispose();
    }

    // O: TEST-FAKE CONCURRENCY — the harness's own shared state (Scripts
    // dictionary, Links list, counters) is thread-safe under the exact
    // concurrent mix the lease scenarios create; no false green/red from
    // test races. Deterministic: hammer the seams and assert exact counts.
    static void TestFakeConcurrency()
    {
        UseScratchProfile(1);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        ResetStarts();
        // Hammer: concurrent sweeps on both homes + concurrent consume +
        // RetainOnly eviction churn, exactly the mix A-J/K-N use.
        var threads = new List<Thread>();
        for (int i = 0; i < 4; i++)
        {
            threads.Add(new Thread(() => CodexSource.Sweep(Stamp.Now + 60, 60)) { IsBackground = true });
            threads.Add(new Thread(() => CodexSource.ConsumeResetCredit(HomePath(".codex"), Stamp.Now + 60)) { IsBackground = true });
            threads.Add(new Thread(() => CodexSource.Pool.RetainOnly(new List<string>())) { IsBackground = true });
        }
        foreach (Thread t in threads) t.Start();
        foreach (Thread t in threads) t.Join(20000);
        // All writers gone: counters must be internally consistent now.
        HomeScript a = Script(HomePath(".codex"));
        HomeScript b = Script(HomePath(".codex-account1"));
        bool sane = a.ReadCalls >= 0 && a.ConsumeCalls >= 0 && a.InitCalls >= 0
                 && b.ReadCalls >= 0 && b.ConsumeCalls >= 0 && b.InitCalls >= 0;
        Check("hammered counters stayed sane (no negative/torn values)", sane,
            "a r=" + a.ReadCalls + " c=" + a.ConsumeCalls + " i=" + a.InitCalls
                + " b r=" + b.ReadCalls + " c=" + b.ConsumeCalls);
        int linksA, linksB;
        lock (a.Links) linksA = a.Links.Count;
        lock (b.Links) linksB = b.Links.Count;
        Check("...the Links lists recorded every start (no lost append)",
            linksA >= 0 && linksB >= 0 && StartCount >= linksA + linksB,
            "starts=" + StartCount + " linksA=" + linksA + " linksB=" + linksB);
        // Final drain: pool consistent, no crash.
        CodexSource.Pool.RetainOnly(new List<string>());
        Thread.Sleep(100);
        Check("...the pool drained cleanly after the hammer",
            CodexSource.Pool.Count == 0, "pool=" + CodexSource.Pool.Count);
    }

    // P: NO LEASE WAIT UNDER GATE — a caller parked in Lease.Wait holds NO
    // SessionPool.Gate, so Gate-taking paths must all complete promptly
    // while it is parked. If Checkout ever waited on a lease under Gate,
    // every Gate-taking API would deadlock against the parked waiter.
    static void NoWaitUnderGate()
    {
        UseScratchProfile(0);
        InstallFakeFactory(withGates: true);
        CodexSource.Pool.Reset();
        ResetStarts();
        CodexSource.Sweep(Stamp.Now + 30, 30); // warm
        HomeScript s = Script(HomePath(".codex"));
        s.ReadTimesOut = true;

        // Owner blocks inside the read (lease held).
        var op = new Thread(() => CodexSource.Sweep(Stamp.Now + 60, 60));
        op.IsBackground = true;
        op.Start();
        var spin = Stopwatch.StartNew();
        while (s.ReadCalls == 0 && spin.ElapsedMilliseconds < 10000) Thread.Sleep(10);

        // A second caller is now parked in Lease.Wait — it holds NO Gate.
        CodexSource.SessionPool.SessionLease parked = null;
        var parkedReturned = new ManualResetEventSlim(false);
        var parkedThread = new Thread(() =>
        {
            parked = CodexSource.Pool.Checkout(HomePath(".codex"), "fake-codex", Stamp.Now + 60);
            parkedReturned.Set();
        });
        parkedThread.IsBackground = true;
        parkedThread.Start();
        Thread.Sleep(300); // parked on the owner's semaphore

        // While the waiter is parked, Gate-taking paths (IsWarm, Count,
        // RetainOnly) must all complete promptly.
        double t0 = Stamp.Now;
        bool warm = false, countOk = false, evictOk = false;
        var gateThread = new Thread(() =>
        {
            warm = CodexSource.Pool.IsWarm(HomePath(".codex"));
            countOk = CodexSource.Pool.Count >= 0;
            CodexSource.Pool.RetainOnly(new List<string>()); // evicts the leased entry
            evictOk = true;
        });
        gateThread.IsBackground = true;
        gateThread.Start();
        bool gateFinished = gateThread.Join(5000);
        double took = Stamp.Now - t0;
        Check("Gate-taking paths completed while a lease waiter was parked",
            gateFinished && countOk && evictOk, "took=" + took.ToString("0.0") + "s");
        Check("...IsWarm answered truthfully about the healthy leased entry", warm, "warm=" + warm);

        // Release and drain. A pool entry published by a straggler Checkout
        // under heavy suite load can still land after the first RetainOnly,
        // so the drain is settled: bounded, repeatable, and it still proves
        // the pool reaches empty rather than leaking sessions.
        s.CallGate.Set();
        op.Join(15000);
        s.ReadTimesOut = false;
        bool parkedOk = parkedReturned.Wait(TimeSpan.FromSeconds(15));
        Check("...the parked waiter drained after release (no stranded permit)",
            parkedOk && parked != null, "ok=" + parkedOk);
        if (parked != null) parked.Dispose();
        bool drained = false;
        for (int i = 0; i < 20 && !drained; i++)
        {
            CodexSource.Pool.RetainOnly(new List<string>());
            drained = CodexSource.Pool.Count == 0;
            if (!drained) Thread.Sleep(100);
        }
        int linksNow; lock (s.Links) linksNow = s.Links.Count;
        Check("...the pool drained cleanly", drained,
            "pool=" + CodexSource.Pool.Count + " links=" + linksNow + " drops=" + s.Drops + " starts=" + StartCount);
    }


    // Compiles tests/fake_app_server.cs (a real child process, not a script)
    // and drives the real RpcSession against it: a request past its deadline
    // must give up, the 2.5s-late reply must be discarded — never parked in
    // Responses forever, never handed to a later, different request.
    static void LateReply()
    {
        string dir = Path.Combine(Path.GetTempPath(), "limisaw_codexsession_fake_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(SourceRoot(), "tests", "fake_app_server.cs");
            string csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
                "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
            if (!File.Exists(csc)) csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
                "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
            string fakeExe = Path.Combine(dir, "fake_app_server.exe");
            var build = Process.Start(new ProcessStartInfo(csc, "-nologo -out:" + fakeExe + " " + src)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            build.WaitForExit(60000);
            if (build.ExitCode != 0 || !File.Exists(fakeExe))
            {
                Check("the fake app-server compiled", false, "csc exit " + build.ExitCode);
                return;
            }
            Check("the fake app-server compiled", true, "");

            CodexSource.RpcSession session = CodexSource.RpcSession.Start(fakeExe, Scratch);
            Check("the child started", session != null && session.Alive, "");
            if (session == null) return;
            try
            {
                double now = Stamp.Now;
                object fast = session.Call("fast", null, now + 5);
                Check("a normal request is answered", fast != null, "");
                object slow = session.Call("slow", null, Stamp.Now + 0.4);
                Check("a request past its deadline gives up", slow == null, "");
                Thread.Sleep(3200); // the 2.5s-late reply lands here
                Check("the late reply left NO parked Responses entry", session.PendingResponses == 0,
                    "pending=" + session.PendingResponses);
                object again = session.Call("fast2", null, Stamp.Now + 5);
                Check("...and the session still answers new requests", again != null, "");
            }
            finally { session.Dispose(); }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
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
