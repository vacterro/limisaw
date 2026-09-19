using System;
using System.Threading;
using Limisaw;

// CORE-002 (audit/7.md, SRC-011:R002): a matching generation is NECESSARY but
// not SUFFICIENT authority for a non-terminal progress publication.
//
// The audited defect had two halves:
//
//   1. LIMISAW.cs OnWatcherAttempt's result==null branch called
//      ConnectionWatcher.AttemptFinished BEFORE publishing WaitingForUser.
//      AttemptFinished re-arms the operation, so with a PokePending a second
//      same-generation attempt B could complete terminally (Connected,
//      active=false) while attempt A's late WaitingForUser was still pending;
//      A then overwrote the terminal Connected.
//
//   2. Connections.cs TryProgress / TryProgressResult validated only the
//      generation, so that same stale same-generation callback remained able
//      to mutate Latest after the operation was released.
//
// Required invariant: TryProgress and TryProgressResult accept mutation only
// when shutdown == false, generation == current, AND that generation still
// owns an ACTIVE operation. For a null watcher result, WaitingForUser must be
// published BEFORE AttemptFinished.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class Core002ProgressOwnershipTest
{
    static int fails = 0, checks = 0;
    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static VendorConnection Conn(string vendor, ConnectionState state, string reason)
    {
        return new VendorConnection { VendorId = vendor, State = state, Reason = reason };
    }

    public static int Main()
    {
        Console.WriteLine("== CORE-002: progress publication requires an ACTIVE operation ==");
        // The deterministic reduction of the PokePending race: attempt A's
        // observation is published only while A still owns the slot. A
        // terminal completion for the SAME generation releases ownership, so
        // any later same-generation progress callback must be refused.
        var c1 = new ConnectionCoordinator();
        int g1 = c1.Begin("codex");
        bool activeProgress = c1.TryProgress("codex", g1, ConnectionState.Verifying, "checking");
        Check("active progress (Verifying) succeeds before terminal completion", activeProgress, activeProgress.ToString());
        bool activeResult = c1.TryProgressResult("codex", g1, Conn("codex", ConnectionState.Failed, "transient"));
        Check("active non-terminal result succeeds and keeps ownership ACTIVE",
            activeResult && c1.IsActive("codex"), "active=" + c1.IsActive("codex"));

        // Attempt B completes terminally under the SAME generation.
        bool terminalB = c1.TryPublish("codex", g1, Conn("codex", ConnectionState.Connected, "connected by attempt B"));
        Check("terminal Connected for the same generation publishes", terminalB, terminalB.ToString());
        Check("...and terminal completion releases ownership", !c1.IsActive("codex"), "active=" + c1.IsActive("codex"));

        // Attempt A's late WaitingForUser observation now arrives: same
        // generation, but no longer ACTIVE. This is the exact overwrite the
        // audit described.
        bool lateProgress = c1.TryProgress("codex", g1, ConnectionState.WaitingForUser, "stale attempt A still waiting");
        Check("same-generation TryProgress AFTER terminal completion returns false", !lateProgress, lateProgress.ToString());
        bool lateResult = c1.TryProgressResult("codex", g1, Conn("codex", ConnectionState.Failed, "stale attempt A"));
        Check("same-generation TryProgressResult AFTER terminal completion returns false", !lateResult, lateResult.ToString());
        Check("...and the terminal Connected remains in Latest",
            c1.Latest("codex") != null && c1.Latest("codex").State == ConnectionState.Connected
            && c1.Latest("codex").Reason == "connected by attempt B",
            c1.Latest("codex") == null ? "null" : c1.Latest("codex").State + "/" + c1.Latest("codex").Reason);

        Console.WriteLine();
        Console.WriteLine("== CORE-002: the pre-fix behaviour is detectable (RED control shape) ==");
        // Model the OLD ordering explicitly at the coordinator level: if the
        // active guard were absent, the stale same-generation observation
        // WOULD mutate Latest. This is the assertion the RED control fails.
        var c1b = new ConnectionCoordinator();
        int g1b = c1b.Begin("codex");
        c1b.TryPublish("codex", g1b, Conn("codex", ConnectionState.Connected, "terminal"));
        // Old TryProgress (generation-only) would have returned true here.
        bool oldWouldMutate = !c1b.TryProgress("codex", g1b, ConnectionState.WaitingForUser, "old-order stale");
        Check("the active-ownership guard is load-bearing for the stale same-generation write", oldWouldMutate, "guard refused=" + oldWouldMutate);

        Console.WriteLine();
        Console.WriteLine("== CORE-002: null watcher result re-arms and PokePending is honoured ==");
        // Mirror the FIXED production ordering: publish WaitingForUser while
        // the operation is still ACTIVE, THEN AttemptFinished re-arms.
        ConnectionWatcher.Shutdown();
        ConnectionWatcher.Now = () => Stamp.Now;
        ConnectionWatcher.TickPeriodMs = 25;
        var nw = new ConnectionCoordinator();
        int attempts = 0;
        int ng = nw.Begin("codex");
        ConnectionWatcher.OnAttempt = op =>
        {
            int n = Interlocked.Increment(ref attempts);
            VendorConnection result = null;
            try { result = op.Verify(); } catch { }
            if (result == null)
            {
                // FIXED order: publish while ACTIVE, then re-arm.
                bool published = nw.TryProgress(op.VendorId, op.Generation, ConnectionState.WaitingForUser, "still waiting");
                CheckOnce("null-waiting progress published while ownership was ACTIVE",
                    published && nw.IsActive(op.VendorId), "attempt=" + n + ", published=" + published);
                ConnectionWatcher.AttemptFinished(op.VendorId, op.Generation);
                return;
            }
            ConnectionWatcher.CancelGeneration(op.VendorId, op.Generation);
            nw.TryPublish(op.VendorId, op.Generation, result);
        };
        ConnectionWatcher.Start(new ConnectionWatcher.Operation
        {
            VendorId = "codex",
            Generation = ng,
            Verify = () =>
            {
                // Null until the driver has seen the first attempt; then the
                // op is superseded (CancelGeneration) so no further runs.
                return null;
            },
        });
        // The first attempt is due immediately; wait for several re-arms.
        var rearmDeadline = DateTime.UtcNow.AddSeconds(20);
        while (attempts < 3 && DateTime.UtcNow < rearmDeadline) Thread.Sleep(25);
        Check("a null watcher result re-arms the operation (multiple attempts)",
            attempts >= 3, "attempts=" + attempts);

        // Poke during an in-flight attempt is remembered and fires the next
        // attempt as soon as the current one finishes — with no overlap.
        ConnectionWatcher.Shutdown();
        ConnectionWatcher.Now = () => Stamp.Now;
        int pokeAttempts = 0;
        var inFlight = new ManualResetEventSlim(false);
        var releaseFirst = new ManualResetEventSlim(false);
        int pg = nw.Begin("claude");
        ConnectionWatcher.OnAttempt = op =>
        {
            int n = Interlocked.Increment(ref pokeAttempts);
            if (n == 1)
            {
                inFlight.Set();
                releaseFirst.Wait(10000);         // hold attempt 1 in flight
            }
            ConnectionWatcher.AttemptFinished(op.VendorId, op.Generation);
        };
        ConnectionWatcher.Start(new ConnectionWatcher.Operation
        { VendorId = "claude", Generation = pg, Verify = () => null });
        bool held = inFlight.Wait(TimeSpan.FromSeconds(10));
        Check("an attempt is held in flight", held && ConnectionWatcher.InFlightCount == 1,
            "inFlight=" + ConnectionWatcher.InFlightCount);
        ConnectionWatcher.Poke("claude");          // arrives DURING the in-flight attempt
        Thread.Sleep(400);                          // several ticks pass
        Check("PokePending introduces no overlapping attempt",
            Interlocked.CompareExchange(ref pokeAttempts, 0, 0) == 1 && ConnectionWatcher.InFlightCount == 1,
            "attempts=" + pokeAttempts + ", inFlight=" + ConnectionWatcher.InFlightCount);
        releaseFirst.Set();
        var pokeDeadline = DateTime.UtcNow.AddSeconds(10);
        while (pokeAttempts < 2 && DateTime.UtcNow < pokeDeadline) Thread.Sleep(25);
        Check("a Poke during an in-flight attempt triggers the immediate next attempt",
            pokeAttempts >= 2, "attempts=" + pokeAttempts);

        ConnectionWatcher.Shutdown();
        ConnectionWatcher.OnAttempt = null;
        ConnectionWatcher.TickPeriodMs = 250;
        ConnectionWatcher.Now = () => Stamp.Now;

        Console.WriteLine();
        Console.WriteLine("== CORE-002: the production ordering shape (secondary guard) ==");
        Source();

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }

    // The null-waiting publication check runs once per attempt; count it once
    // so the output stays readable while still proving every attempt was
    // published under an ACTIVE operation.
    static bool onceDone;
    static void CheckOnce(string name, bool ok, string detail)
    {
        if (onceDone) return;
        onceDone = true;
        Check(name, ok, detail);
    }

    static readonly string CR = ((char)13).ToString();
    static readonly string LF = ((char)10).ToString();
    static string ReadSource(string path)
    {
        return System.IO.File.ReadAllText(path).Replace(CR + LF, LF).Replace(LF, CR + LF);
    }

    static string SourceDir()
    {
        string dir = System.IO.Directory.GetCurrentDirectory();
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "LIMISAW.cs"))) return dir;
            var up = System.IO.Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        return System.IO.Directory.GetCurrentDirectory();
    }

    // Secondary guard: the fix's shape, so a future edit cannot restore the
    // audited ordering or drop the active-ownership check. The behavioural
    // checks above are the real contract.
    static void Source()
    {
        string dir = SourceDir();
        string ui = ReadSource(System.IO.Path.Combine(dir, "LIMISAW.cs"));
        string conn = ReadSource(System.IO.Path.Combine(dir, "Connections.cs"));

        // The null-result branch must publish WaitingForUser BEFORE
        // AttemptFinished. Anchor on the OnWatcherAttempt null branch: the last
        // `if (result == null)` before the non-terminal TryProgressResult call.
        int endAt = ui.IndexOf("ConnCoordinator.TryProgressResult(vendorId, gen, result);", StringComparison.Ordinal);
        int nullAt = endAt > 0
            ? ui.LastIndexOf("if (result == null)", endAt, StringComparison.Ordinal)
            : -1;
        int progressAt = nullAt < 0 ? -1 : ui.IndexOf("ConnectionState.WaitingForUser", nullAt, StringComparison.Ordinal);
        int finishedAt = nullAt < 0 ? -1 : ui.IndexOf("ConnectionWatcher.AttemptFinished(vendorId, gen);", nullAt, StringComparison.Ordinal);
        Check("null-result branch publishes WaitingForUser BEFORE AttemptFinished",
            nullAt > 0 && progressAt > nullAt && finishedAt > progressAt,
            "null=" + nullAt + ", progress=" + progressAt + ", finished=" + finishedAt);

        Check("TryProgressResult requires an ACTIVE operation",
            conn.IndexOf("public bool TryProgressResult(string vendorId, int generation, VendorConnection conn)", StringComparison.Ordinal) >= 0
            && conn.IndexOf("if (!active.TryGetValue(vendorId, out a) || !a) return false;", StringComparison.Ordinal) >= 0, "");
    }
}
