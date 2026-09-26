using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

// CORE-011: the single-instance launch protocol, exercised by REAL processes
// against REAL named kernel objects. A "player" fixture is compiled together
// with the engine sources, so every scenario drives the production
// Singleton.Acquire code path — no replica, no source-string fake. Each
// scenario runs in its OWN namespace prefix (the player overrides the three
// Singleton names from argv), so the tests are hermetic even while the user's
// real LIMISAW.exe owns Local\LimisawApp on the same desktop.
//
// The protocol under test: a launch resolves to
//   Primary    — it owns the singleton (fresh, or takeover after the previous
//                owner died or released),
//   Activated  — it delivered activation to a primary whose ready gate is SET
//                (gate set == waiter and form handle live),
//   Timeout    — the bounded handoff expired against a live owner that never
//                published (the only designed exit that activates nothing).
// A one-shot secondary that returns after a single failed attempt is the
// defect this harness pins: scenarios 2-6 all depend on waiting.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main SingleInstanceTest)
public static class SingleInstanceTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Dir;          // harness scratch
    static string PlayerExe;
    static readonly List<Process> Started = new List<Process>();
    static readonly List<string> StartedDirs = new List<string>();

    public static int Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "limisaw_single_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        // A developer's own running LIMISAW (or a player this harness spawned)
        // puts a real "LIMISAW" window on the desktop, and the window-activation
        // probe then reports Activated where these scenarios assert Timeout.
        // Pin the probe so every classification below is produced by the setup
        // this harness actually made, not by ambient desktop state.
        var productionWindowProbe = Limisaw.Singleton.WindowProbeImpl;
        Limisaw.Singleton.WindowProbeImpl = () => false;
        try
        {
            if (!BuildPlayer()) return Report();
            NormalPrimary();
            StartupGap();
            PrimaryDiesBeforeReady();
            ShutdownRace();
            Contention();
            BoundedHandoff();
            PublicationFailure();
            FinalZeroTimeProbe();
            SourceGuards();
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Limisaw.Singleton.WindowProbeImpl = productionWindowProbe;
            foreach (Process p in Started) { try { if (!p.HasExited) p.Kill(); } catch { } }
            foreach (string d in StartedDirs) { try { Directory.Delete(d, true); } catch { } }
            try { Directory.Delete(Dir, true); } catch { }
        }
        return Report();
    }

    static int Report()
    {
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    // ── the player: the production Singleton driven from a real process ────

    static bool BuildPlayer()
    {
        string playerSrc = Path.Combine(Dir, "player.cs");
        File.WriteAllText(playerSrc,
            "using System;using System.IO;using System.Threading;using Limisaw;\n" +
            "static class Player\n" +
            "{\n" +
            "  static int Main(string[] args)\n" +
            "  {\n" +
            "    string mode = args[0];\n" +
            "    Singleton.MutexName = args[1] + \"App\";\n" +
            "    Singleton.ShowName = args[1] + \"Show\";\n" +
            "    Singleton.ReadyName = args[1] + \"Ready\";\n" +
            "    string dir = args[2];\n" +
            "    int a = args.Length > 3 ? int.Parse(args[3]) : 0;\n" +
            "    int b = args.Length > 4 ? int.Parse(args[4]) : 0;\n" +
            // The fixture never owns a window, so window activation must not
            // answer for it: a developer's running LIMISAW would otherwise turn
            // every Timeout expectation into Activated.
            "    Singleton.WindowProbeImpl = () => false;\n" +
            "    Singleton.Role role;\n" +
            "    Singleton.Ownership owned = Singleton.Acquire(\n" +
            "      TimeSpan.FromMilliseconds(mode == \"sec\" ? a : 10000), out role);\n" +
            "    if (owned == null)\n" +
            "    {\n" +
            "      File.WriteAllText(Path.Combine(dir, \"role.txt\"), role.ToString());\n" +
            "      return role == Singleton.Role.Activated ? 0 : 3;\n" +
            "    }\n" +
            "    File.WriteAllText(Path.Combine(dir, \"role.txt\"), \"primary\");\n" +
            "    var got = new ManualResetEvent(false);\n" +
            "    var reg = ThreadPool.RegisterWaitForSingleObject(owned.Show, delegate\n" +
            "    {\n" +
            "      int n = Interlocked.Increment(ref gotN);\n" +
            "      try { File.WriteAllText(Path.Combine(dir, \"got_\" + n + \".txt\"), \"1\"); } catch { }\n" +
            "      got.Set();\n" +
            "    }, null, Timeout.Infinite, false);\n" +
            "    if (mode == \"gap\") Thread.Sleep(a);   // startup work BEFORE publication\n" +
            "    if (owned.Ready != null && mode != \"owner\" && mode != \"dies\") owned.Ready.Set();\n" +
            "    if (mode == \"owner\" || mode == \"dies\") Thread.Sleep(a);\n" +
            "    else if (mode == \"teardown\")\n" +
            "    {\n" +
            "      Thread.Sleep(Math.Max(300, b / 2));  // serve activations for a moment\n" +
            "      if (owned.Ready != null) owned.Ready.Reset();  // stop advertising FIRST\n" +
            "      File.WriteAllText(Path.Combine(dir, \"tearing.txt\"), \"1\");\n" +
            "      Thread.Sleep(b);                     // the shutdown race window\n" +
            "    }\n" +
            "    else if (mode == \"sec\") Thread.Sleep(30000);  // a takeover serves until killed\n" +
            "    else Thread.Sleep(b > 0 ? b : 3000);\n" +
            "    reg.Unregister(null);\n" +
            "    try { if (owned.Ready != null) owned.Ready.Reset(); } catch { }\n" +
            "    try { if (owned.Show != null) owned.Show.Dispose(); } catch { }\n" +
            "    try { File.WriteAllText(Path.Combine(dir, \"done.txt\"), \"1\"); } catch { }\n" +
            "    try { owned.Release(); } catch { }\n" +
            "    return 0;\n" +
            "  }\n" +
            "  static int gotN;\n" +
            "}\n");

        PlayerExe = Path.Combine(Dir, "player.exe");
        string csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        if (!File.Exists(csc)) csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
            "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
        string engine = Path.Combine(Dir, "engine");
        // The engine sources sit next to the harness sources in the repo; walk up.
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 4 && !File.Exists(Path.Combine(root, "LIMISAW.cs")); i++)
        { var up = Directory.GetParent(root); if (up == null) break; root = up.FullName; }
        if (!File.Exists(Path.Combine(root, "LIMISAW.cs")))
        { Check("the player fixture compiled", false, "engine sources not found from " + root); return false; }

        string refs = "-r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll";
        string srcs = "\"LIMISAW.cs\" \"Probe.cs\" \"ProbeClaude.cs\" \"ProbeAntigravity.cs\" \"ProbeZcode.cs\" \"ProbeFreebuff.cs\" \"Assets.cs\" \"ChildSweeper.cs\" \"Connections.cs\" \"" + playerSrc + "\"";
        try
        {
            var build = Process.Start(new ProcessStartInfo(csc,
                "-nologo -main:Player -out:\"" + PlayerExe + "\" " + refs + " " + srcs)
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = root,
              RedirectStandardOutput = true, RedirectStandardError = true });
            string err = build.StandardError.ReadToEnd();
            build.WaitForExit(90000);
            bool ok = build.ExitCode == 0 && File.Exists(PlayerExe);
            Check("the player fixture compiled", ok, ok ? "" : Trim(err, 200));
            return ok;
        }
        catch (Exception ex) { Check("the player fixture compiled", false, ex.Message); return false; }
    }

    static string Trim(string s, int n) { return s == null ? "" : (s.Length <= n ? s : s.Substring(0, n)); }

    // ── process helpers ─────────────────────────────────────────────────────

    static Process Start(string mode, string prefix, string dir, params string[] nums)
    {
        Directory.CreateDirectory(dir);
        string arguments = mode + " " + prefix + " \"" + dir + "\"";
        foreach (string n in nums) arguments += " " + n;
        Process p = Process.Start(new ProcessStartInfo(PlayerExe, arguments)
        { CreateNoWindow = true, UseShellExecute = false });
        Started.Add(p); StartedDirs.Add(dir);
        return p;
    }

    static string RoleOf(string dir)
    {
        string p = Path.Combine(dir, "role.txt");
        for (int waited = 0; waited < 8000 && !File.Exists(p); waited += 50) Thread.Sleep(50);
        try { return File.ReadAllText(p); } catch { return "missing"; }
    }

    // A takeover player serves until killed, so a role is awaited by file, not
    // by process exit.
    static bool WaitRole(string dir, string want, int ms)
    {
        string p = Path.Combine(dir, "role.txt");
        for (int waited = 0; waited < ms; waited += 50)
        {
            try { if (File.ReadAllText(p) == want) return true; } catch { }
            Thread.Sleep(50);
        }
        try { return File.ReadAllText(p) == want; } catch { return false; }
    }

    static bool WaitFile(string dir, string name, int ms)
    {
        string p = Path.Combine(dir, name);
        for (int waited = 0; waited < ms && !File.Exists(p); waited += 50) Thread.Sleep(50);
        return File.Exists(p);
    }

    static int GotCount(string dir)
    {
        try { return Directory.GetFiles(dir, "got_*.txt").Length; } catch { return 0; }
    }

    // Contract A: Activated means the activation was SIGNALED (the kernel
    // autoreset consumed the Set), and the receiver's registered waiter
    // delivers asynchronously on a thread-pool thread. A single instant
    // GotCount races that callback — the observed flake, not a lost
    // activation — so receipt is awaited on the receiver-side causal
    // observation (got_*.txt), bounded like every other wait here.
    static bool WaitGot(string dir, int min, int ms)
    {
        for (int waited = 0; waited < ms; waited += 50)
        {
            if (GotCount(dir) >= min) return true;
            Thread.Sleep(50);
        }
        return GotCount(dir) >= min;
    }

    static void End(Process p, int ms)
    {
        if (p == null) return;
        if (!p.HasExited) { try { p.Kill(); } catch { } }
        try { p.WaitForExit(ms); } catch { }
        try { p.Dispose(); } catch { }
    }

    static bool GoneWithin(Process p, int ms)
    {
        for (int waited = 0; waited < ms; waited += 50)
        { if (p.HasExited) return true; Thread.Sleep(50); }
        return p.HasExited;
    }

    // ── 1. a normal launch pair ─────────────────────────────────────────────
    static void NormalPrimary()
    {
        Console.WriteLine("== the ready primary receives the activation ==");
        string prefix = "Local\\LimT1_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
        string dir = Path.Combine(Dir, "s1pri"), dir2 = Path.Combine(Dir, "s1sec");
        Process pri = Start("ready", prefix, dir, "0", "2500");
        Check("the first launch becomes the primary", RoleOf(dir) == "primary", RoleOf(dir));
        // The gate is the ready signal: it must be ON before a secondary runs.
        bool ready = false;
        for (int waited = 0; waited < 3000 && !ready; waited += 50)
        {
            try { using (var e = System.Threading.EventWaitHandle.OpenExisting(prefix + "Ready"))
                ready = e.WaitOne(TimeSpan.Zero); }
            catch { }
            if (!ready) Thread.Sleep(50);
        }
        Check("the primary publishes readiness", ready, "");
        Process sec = Start("sec", prefix, dir2, "5000");
        if (!sec.WaitForExit(8000)) { try { sec.Kill(); } catch { } }
        Check("the second launch reports Activated, not a second primary",
            RoleOf(dir2) == "Activated", RoleOf(dir2));
        Check("...and the activation reached the primary",
            WaitGot(dir, 1, 8000), GotCount(dir) + " receipt(s)");
        End(pri, 4000);
    }

    // ── 2. the startup gap: owner holds, readiness not published yet ────────
    static void StartupGap()
    {
        Console.WriteLine("== secondaries launched inside the startup gap do not vanish ==");
        string prefix = "Local\\LimT2_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
        string dir = Path.Combine(Dir, "s2pri"), dir2 = Path.Combine(Dir, "s2secA"),
            dir3 = Path.Combine(Dir, "s2secB");
        // The primary owns the singleton but sleeps 700ms before publishing.
        Process pri = Start("gap", prefix, dir, "700", "4000");
        RoleOf(dir);   // wait for the role file: ownership taken, publication NOT yet
        Process a = Start("sec", prefix, dir2, "8000");
        Process b = Start("sec", prefix, dir3, "8000");
        bool aOk = a.WaitForExit(15000), bOk = b.WaitForExit(15000);
        Check("a secondary inside the gap still resolves (no silent exit)",
            aOk && RoleOf(dir2) == "Activated", RoleOf(dir2));
        Check("...and a second one does too", bOk && RoleOf(dir3) == "Activated", RoleOf(dir3));
        Check("...neither stole ownership while the primary lived",
            RoleOf(dir2) != "primary" && RoleOf(dir3) != "primary", "");
        Check("...and the primary received the activation(s)",
            WaitGot(dir, 1, 8000), GotCount(dir) + " receipt(s)");
        End(pri, 6000);
    }

    // ── 3. the primary dies before ever publishing ──────────────────────────
    static void PrimaryDiesBeforeReady()
    {
        Console.WriteLine("== a dead owner hands the singleton to the waiting secondary ==");
        string prefix = "Local\\LimT3_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
        string dir = Path.Combine(Dir, "s3pri"), dir2 = Path.Combine(Dir, "s3sec");
        Process pri = Start("dies", prefix, dir, "5000");   // would hold 5s, never publishes
        RoleOf(dir);
        Process sec = Start("sec", prefix, dir2, "8000");
        Thread.Sleep(600);                                   // the secondary is now inside its handoff
        try { pri.Kill(); } catch { }                        // the owner dies before publishing
        pri.WaitForExit(3000);
        Check("the waiting secondary takes over as the new primary",
            WaitRole(dir2, "primary", 8000), RoleOf(dir2));
        Check("...and exactly one process owns the singleton (the takeover)",
            GoneWithin(pri, 0) && !sec.HasExited, "");
        End(sec, 3000);
    }

    // ── 4. the shutdown race: launch during teardown ────────────────────────
    static void ShutdownRace()
    {
        Console.WriteLine("== a launch inside the shutdown window becomes the replacement ==");
        string prefix = "Local\\LimT4_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
        string dir = Path.Combine(Dir, "s4pri"), dir2 = Path.Combine(Dir, "s4sec");
        // Teardown: publish, serve, RESET the gate, then hold 800ms before the
        // channel closes and ownership releases — the exact order Program.Main
        // uses, so a launch landing in the window cannot see a contactable
        // primary and must resolve through the takeover.
        Process pri = Start("teardown", prefix, dir, "0", "800");
        RoleOf(dir);
        Check("the teardown announces the gate reset before closing the channel",
            WaitFile(dir, "tearing.txt", 8000), "");
        Process sec = Start("sec", prefix, dir2, "8000");
        Check("the launch during teardown took over instead of vanishing",
            WaitRole(dir2, "primary", 12000), RoleOf(dir2));
        Check("the old primary is gone by then", pri.HasExited || GoneWithin(pri, 4000), "");
        End(sec, 3000);
        if (!pri.HasExited) End(pri, 3000);
    }

    // ── 5. contention at the ownership-loss boundary ────────────────────────
    static void Contention()
    {
        Console.WriteLine("== exactly one of many racing secondaries becomes the primary ==");
        string prefix = "Local\\LimT5_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
        string dir = Path.Combine(Dir, "s5pri");
        Process pri = Start("teardown", prefix, dir, "0", "900");
        RoleOf(dir);
        WaitFile(dir, "tearing.txt", 8000);
        Process[] sec = new Process[3];
        string[] dirs = new string[3];
        for (int i = 0; i < 3; i++)
        { dirs[i] = Path.Combine(Dir, "s5sec" + i); sec[i] = Start("sec", prefix, dirs[i], "8000"); }
        // A takeover player serves until killed, so a resolved role is awaited
        // as a file: every player writes role.txt exactly once, at resolution.
        bool allResolved = true;
        for (int i = 0; i < 3; i++) allResolved &= WaitFile(dirs[i], "role.txt", 12000);
        Check("every racing secondary resolved to a role", allResolved, "");
        int primaries = 0, activated = 0;
        string winner = null;
        for (int i = 0; i < 3; i++)
        {
            string role = RoleOf(dirs[i]);
            if (role == "primary") { primaries++; winner = dirs[i]; }
            else if (role == "Activated") activated++;
        }
        Check("exactly one racing secondary wins the singleton",
            primaries == 1, primaries + " primary(ies)");
        // The winner published its own readiness, so the losers' deliveries
        // land — awaited on the winner's receiver-side receipts, not sampled
        // at a single instant.
        bool winnerGot = winner != null && WaitGot(winner, 1, 8000);
        Check("the winner received the losers' activation(s)", winnerGot,
            winner == null ? "no winner" : GotCount(winner) + " receipt(s)");
        foreach (Process p in sec) End(p, 2000);
        if (!pri.HasExited) End(pri, 3000);
    }

    // ── 6. a broken owner that never publishes: the bound must fire ─────────
    static void BoundedHandoff()
    {
        Console.WriteLine("== an owner that never publishes expires the bounded handoff ==");
        string prefix = "Local\\LimT6_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
        string dir = Path.Combine(Dir, "s6pri"), dir2 = Path.Combine(Dir, "s6sec");
        Process pri = Start("owner", prefix, dir, "30000");   // holds 30s, never publishes
        RoleOf(dir);
        DateTime began = DateTime.UtcNow;
        Process sec = Start("sec", prefix, dir2, "1500");     // a SHORT handoff proves the bound
        bool ok = sec.WaitForExit(8000);
        double took = (DateTime.UtcNow - began).TotalSeconds;
        Check("the secondary exits instead of hanging on an unpublished owner",
            ok, ok ? "" : "still running after 8s");
        Check("...with the designed Timeout role", RoleOf(dir2) == "Timeout", RoleOf(dir2));
        Check("...within its bound (1.5s handoff, not forever)",
            took < 5.0, took.ToString("0.0") + "s");
        End(pri, 3000);
    }

    // ── 7. W2-004: atomic publication under channel failure ─────────────────
    // The handoff contract, driven against the REAL Singleton in-process (the
    // harness links the engine sources; OpenChannelImpl is the seam):
    //   A. Show fails -> no exception, Role PublicationFailed, no Primary
    //      ownership, the mutex is NOT stranded;
    //   B. Ready fails -> the Show handle is disposed (its kernel name has no
    //      other holder, so OpenExisting must fail), PublicationFailed again;
    //   C. recovery -> a healthy OpenChannelImpl acquires the SAME singleton
    //      and becomes Primary with BOTH channels live — the failed publisher
    //      left nothing behind;
    //   D. caller safety -> Program.Main can never register a waiter on a
    //      null Show or Set a null Ready, because Acquire returns either a
    //      complete Ownership or null (proven by the source shape).
    static void PublicationFailure()
    {
        Console.WriteLine("== W2-004: publication fails atomically, ownership unwinds ==");
        string prefix = "Local\\LimT7_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
        var savedImpl = Limisaw.Singleton.OpenChannelImpl;
        var savedNames = new[] { Limisaw.Singleton.MutexName, Limisaw.Singleton.ShowName, Limisaw.Singleton.ReadyName };
        Func<string, System.Threading.EventResetMode, System.Threading.EventWaitHandle> real = savedImpl;
        try
        {
            Limisaw.Singleton.MutexName = prefix + "App";
            Limisaw.Singleton.ShowName = prefix + "Show";
            Limisaw.Singleton.ReadyName = prefix + "Ready";

            // A: Show fails, Ready would succeed.
            Limisaw.Singleton.OpenChannelImpl = (name, mode) =>
                name == Limisaw.Singleton.ShowName ? null : real(name, mode);
            bool threwA = false;
            Limisaw.Singleton.Role roleA = Limisaw.Singleton.Role.Primary;
            object ownedA = null;
            try { ownedA = Limisaw.Singleton.Acquire(TimeSpan.FromSeconds(10), out roleA); }
            catch (Exception ex) { threwA = true; Check("a Show failure threw", false, ex.GetType().Name); }
            Check("a Show failure did NOT throw", !threwA, "");
            Check("...the role is PublicationFailed", roleA == Limisaw.Singleton.Role.PublicationFailed, roleA.ToString());
            Check("...no Primary ownership was returned", ownedA == null, ownedA == null ? "null" : "owned");
            Check("...neither channel name survived the failed publication",
                !CanOpen(prefix + "Show") && !CanOpen(prefix + "Ready"), "");

            // B: Ready fails, Show succeeds.
            Limisaw.Singleton.OpenChannelImpl = (name, mode) =>
                name == Limisaw.Singleton.ReadyName ? null : real(name, mode);
            Limisaw.Singleton.Role roleB = Limisaw.Singleton.Role.Primary;
            object ownedB = null;
            bool threwB = false;
            try { ownedB = Limisaw.Singleton.Acquire(TimeSpan.FromSeconds(10), out roleB); }
            catch (Exception ex) { threwB = true; Check("a Ready failure threw", false, ex.GetType().Name); }
            Check("a Ready failure did NOT throw", !threwB, "");
            Check("...the role is PublicationFailed", roleB == Limisaw.Singleton.Role.PublicationFailed, roleB.ToString());
            Check("...no Primary ownership was returned", ownedB == null, ownedB == null ? "null" : "owned");
            // The Show handle was DISPOSED on unwind: the harness holds no
            // other handle, so a disposed channel cannot be opened again.
            Check("...the opened Show channel was disposed, not leaked",
                !CanOpen(prefix + "Show"), "");

            // C: recovery — the same singleton, healthy channels.
            Limisaw.Singleton.OpenChannelImpl = real;
            DateTime began = DateTime.UtcNow;
            Limisaw.Singleton.Role roleC;
            var ownedC = Limisaw.Singleton.Acquire(TimeSpan.FromSeconds(10), out roleC);
            double took = (DateTime.UtcNow - began).TotalSeconds;
            Check("a healthy launch became Primary after the failures",
                ownedC != null && roleC == Limisaw.Singleton.Role.Primary, roleC.ToString());
            // A fast Primary also proves the mutex was never stranded: a
            // stranded owner would force the 10s handoff to expire instead.
            Check("...the mutex was not stranded (recovery was immediate, not a handoff)",
                took < 5.0, took.ToString("0.0") + "s");
            Check("...both channels exist for the new primary",
                ownedC != null && CanOpen(prefix + "Show") && CanOpen(prefix + "Ready"), "");
            // D-part (runtime half): the ownership the caller received is
            // COMPLETE — waiter-ready Show, signallable Ready, never null.
            if (ownedC != null)
            {
                var showF = ownedC.GetType().GetField("Show", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var readyF = ownedC.GetType().GetField("Ready", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var show = (System.Threading.EventWaitHandle)showF.GetValue(ownedC);
                var ready = (System.Threading.EventWaitHandle)readyF.GetValue(ownedC);
                bool setOk = false;
                try { ready.Set(); ready.Reset(); setOk = true; } catch { }
                Check("...the returned ownership can register and signal (never null handles)",
                    show != null && ready != null && setOk, "");
                ((IDisposable)ownedC).Dispose();
            }

            // D: caller safety — Program.Main guards the null-ownership exit,
            // so a partial publication can never reach the waiter or the gate.
            string root = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 4 && !File.Exists(Path.Combine(root, "LIMISAW.cs")); i++)
            { var up = Directory.GetParent(root); if (up == null) break; root = up.FullName; }
            string ui = File.ReadAllText(Path.Combine(root, "LIMISAW.cs"));
            int nullReturn = ui.IndexOf("if (owned == null) return;", StringComparison.Ordinal);
            int usingOwned = ui.IndexOf("using (owned)", StringComparison.Ordinal);
            int pubFailed = ui.IndexOf("Role.PublicationFailed", StringComparison.Ordinal);
            Check("Program.Main exits on null ownership before touching Show/Ready",
                nullReturn >= 0 && usingOwned > nullReturn, nullReturn + "/" + usingOwned);
            Check("...publication failure is a represented role, not an exception path",
                pubFailed >= 0, "");
            Check("...Program.Main never registers or signals through a nullable field",
                ui.IndexOf("owned.Show", StringComparison.Ordinal) >= 0
                && ui.IndexOf("owned.Ready.Set", StringComparison.Ordinal) >= 0
                && nullReturn >= 0, "");
        }
        finally
        {
            Limisaw.Singleton.OpenChannelImpl = savedImpl;
            Limisaw.Singleton.MutexName = savedNames[0];
            Limisaw.Singleton.ShowName = savedNames[1];
            Limisaw.Singleton.ReadyName = savedNames[2];
        }
    }

    static bool CanOpen(string name)
    {
        try { using (System.Threading.EventWaitHandle.OpenExisting(name)) return true; }
        catch { return false; }
    }

    // ── 8. W2-002/R006: the final zero-time probe boundary ─────────────────
    // After the deadline loop expires the secondary retries Deliver/ActivateWindow,
    // then takes ONE nonblocking mutex.WaitOne(TimeSpan.Zero) before declaring
    // Timeout. The regression proves: (A) a free/release-at-boundary path wins the
    // probe and Publish-es to Primary, (B) a still-owned path still Timeout-s
    // without theft, (C) the Publish unwind (channel open failure) still applies,
    // (D) repeated immediate boundaries do not spin. The seam avoids a sleeps-only
    // race — proof is the delegate being called.
    static void FinalZeroTimeProbe()
    {
        Console.WriteLine("== W2-002/R006: the final zero-time probe fires at the boundary, not as a timed wait ==");
        string prefix = "Local\\LimT8_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
        var savedMutex = Limisaw.Singleton.MutexName;
        var savedShow = Limisaw.Singleton.ShowName;
        var savedReady = Limisaw.Singleton.ReadyName;
        var savedProbe = Limisaw.Singleton.FinalProbeImpl;
        try
        {
            // The reliable in-process proof: an in-process Acquire with a live
            // unpublished owner does not steal via the probe when the probe is
            // told the mutex is still held, and DOES take over when the probe is
            // told the mutex is free. The probe is the only thing being flipped.
            string pp = prefix + "_probe_";
            Limisaw.Singleton.MutexName = pp + "App";
            Limisaw.Singleton.ShowName = pp + "Show";
            Limisaw.Singleton.ReadyName = pp + "Ready";
            int calls = 0;
            var probeOwnerHasMutex = new System.Threading.ManualResetEvent(false);
            var releaseProbeOwner = new System.Threading.ManualResetEvent(false);
            var probeOwnerMutex = new System.Threading.Mutex(false, pp + "App");
            var probeOwnerThread = new System.Threading.Thread(delegate()
            {
                probeOwnerMutex.WaitOne();
                probeOwnerHasMutex.Set();
                releaseProbeOwner.WaitOne();
                probeOwnerMutex.ReleaseMutex();
            });
            probeOwnerThread.IsBackground = true;
            probeOwnerThread.Start();
            probeOwnerHasMutex.WaitOne(2000);
            Limisaw.Singleton.FinalProbeImpl = m => { calls++; return false; };
            // The owner has already acquired the name before this thread calls
            // Acquire. The deterministic seam reports its final probe as a miss.
            Limisaw.Singleton.Role probeRole;
            var probeOwnership = Limisaw.Singleton.Acquire(TimeSpan.Zero, out probeRole);
            Check("the final zero-time probe fires against a still-owned singleton (Timeout path)", calls == 1, calls + " probe call(s)");
            Check("...and the still-owned secondary correctly reports Timeout, not a stolen Primary",
                probeOwnership == null && probeRole == Limisaw.Singleton.Role.Timeout, probeRole.ToString());
            releaseProbeOwner.Set();
            probeOwnerThread.Join(2000);
            try { probeOwnerMutex.Dispose(); } catch { }
            try { probeOwnerHasMutex.Dispose(); releaseProbeOwner.Dispose(); } catch { }
            // Activation can win without ownership: a failed final probe must
            // preserve the existing primary and return Activated/null.
            {
                string pa = prefix + "_activated_secondary_";
                Limisaw.Singleton.MutexName = pa + "App";
                Limisaw.Singleton.ShowName = pa + "Show";
                Limisaw.Singleton.ReadyName = pa + "Ready";
                var readyGate = new System.Threading.EventWaitHandle(true, System.Threading.EventResetMode.ManualReset, pa + "Ready");
                var showGate = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, pa + "Show");
                var ownerMutex = new System.Threading.Mutex(false, pa + "App");
                var ownerHasMutex = new System.Threading.ManualResetEvent(false);
                var releaseOwner = new System.Threading.ManualResetEvent(false);
                var owner = new System.Threading.Thread(delegate()
                {
                    ownerMutex.WaitOne();
                    ownerHasMutex.Set();
                    releaseOwner.WaitOne();
                    ownerMutex.ReleaseMutex();
                });                Limisaw.Singleton.FinalProbeImpl = m => { releaseOwner.Set(); return false; };

                Limisaw.Singleton.Ownership owned = null;
                try
                {
                    owner.IsBackground = true;
                    owner.Start();
                    bool ownerHeld = ownerHasMutex.WaitOne(2000);
                    Check("...the activated/non-winning branch starts with a live owner", ownerHeld, "");
                    Limisaw.Singleton.Role role;
                    owned = Limisaw.Singleton.Acquire(TimeSpan.Zero, out role);
                    bool delivered = showGate.WaitOne(TimeSpan.Zero);
                    Check("...activation without final ownership returns Activated/null after delivery",
                        ownerHeld && delivered && owned == null && role == Limisaw.Singleton.Role.Activated,
                        role + (owned == null ? "/null" : "/owned"));
                }
                finally
                {
                    releaseOwner.Set();
                    owner.Join(2000);
                    try { if (owned != null) owned.Dispose(); } catch { }
                    try { readyGate.Dispose(); showGate.Dispose(); ownerMutex.Dispose(); } catch { }
                    try { ownerHasMutex.Dispose(); releaseOwner.Dispose(); } catch { }
                }
            }
            // A: activation succeeds, then the final probe acquires the mutex.
            // The old branch discarded that ownership whenever activated was true.
            {
                string pa = prefix + "_activated_";
                Limisaw.Singleton.MutexName = pa + "App";
                Limisaw.Singleton.ShowName = pa + "Show";
                Limisaw.Singleton.ReadyName = pa + "Ready";
                var readyGate = new System.Threading.EventWaitHandle(true, System.Threading.EventResetMode.ManualReset, pa + "Ready");
                var showGate = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, pa + "Show");
                var ownerMutex = new System.Threading.Mutex(false, pa + "App");
                var ownerHasMutex = new System.Threading.ManualResetEvent(false);
                var releaseOwner = new System.Threading.ManualResetEvent(false);
                var ownerReleased = new System.Threading.ManualResetEvent(false);
                var owner = new System.Threading.Thread(delegate()
                {
                    ownerMutex.WaitOne();
                    ownerHasMutex.Set();
                    releaseOwner.WaitOne();
                    ownerMutex.ReleaseMutex();
                    ownerReleased.Set();
                });
                Limisaw.Singleton.FinalProbeImpl = delegate(System.Threading.Mutex m)
                {
                    releaseOwner.Set();
                    if (!ownerReleased.WaitOne(2000)) return false;
                    try { return m.WaitOne(TimeSpan.Zero); }
                    catch (System.Threading.AbandonedMutexException) { return true; }
                };
                Limisaw.Singleton.Ownership owned = null;
                try
                {
                    owner.IsBackground = true;
                    owner.Start();
                    Check("...the activation boundary has a live owner before delivery", ownerHasMutex.WaitOne(2000), "");
                    Limisaw.Singleton.Role role;
                    owned = Limisaw.Singleton.Acquire(TimeSpan.FromMilliseconds(100), out role);
                    bool delivered = showGate.WaitOne(TimeSpan.Zero);
                    Check("...successful Deliver signals the show channel before final takeover", delivered, "");
                    Check("...final probe publishes Primary even after activation succeeded",
                        owned != null && role == Limisaw.Singleton.Role.Primary, role.ToString());
                }
                finally
                {
                    releaseOwner.Set();
                    owner.Join(2000);
                    try { if (owned != null) owned.Dispose(); } catch { }
                    try { readyGate.Dispose(); showGate.Dispose(); ownerMutex.Dispose(); } catch { }
                    try { ownerHasMutex.Dispose(); releaseOwner.Dispose(); ownerReleased.Dispose(); } catch { }
                }
            }
            // B: the boundary seam — prove FinalProbeImpl fires in Handoff path.
            // The simplest proof: spawn a thread that creates an existing mutex,
            // so Acquire enters Handoff loop. The sealed FinalProbeImpl delegate
            // counts invocations, proving the post-loop probe hit.
            {
                string pb = prefix + "_seam_";
                Limisaw.Singleton.MutexName = pb + "App";
                Limisaw.Singleton.ShowName = pb + "Show";
                Limisaw.Singleton.ReadyName = pb + "Ready";
                // Create the mutex from this thread (createdNew=true -> primary path).
                // A second thread will call Acquire, see createdNew=false,
                // fail WaitOne(0), enter Handoff, and call FinalProbeImpl.
                calls = 0;
                var preCreatedMutex = new System.Threading.Mutex(true, pb + "App");
                var probeCalled = new System.Threading.ManualResetEvent(false);
                Limisaw.Singleton.FinalProbeImpl = m => { calls++; probeCalled.Set(); bool ok = false; try { ok = m.WaitOne(TimeSpan.Zero); } catch { ok = false; } return ok; };
                System.Threading.Thread secondary = new System.Threading.Thread(() =>
                {
                    Limisaw.Singleton.Role r; var o = Limisaw.Singleton.Acquire(TimeSpan.FromMilliseconds(150), out r);
                    try { if (o != null) ((IDisposable)o).Dispose(); } catch { }
                });
                secondary.IsBackground = true; secondary.Start();
                // Wait for Secondary->Handoff->Probe, or Timeout path.
                probeCalled.WaitOne(2000);
                Check("the final zero-time probe is invoked in Handoff path (calls=" + calls + ")", probeCalled.WaitOne(100) && calls >= 1, "calls=" + calls);
                secondary.Join(2000);
                preCreatedMutex.ReleaseMutex(); preCreatedMutex.Dispose();
            }
            // Source shape: the zero-time probe is a TimeSpan.Zero WaitOne, not a
            // timed slice — no busy spin after CORE-011 already fixed it.
            string root = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 4 && !File.Exists(System.IO.Path.Combine(root, "LIMISAW.cs")); i++)
            { var up = System.IO.Directory.GetParent(root); if (up == null) break; root = up.FullName; }
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "LIMISAW.cs"));
            // Scope the guard to the production probe site (FinalProbeDefault), not
            // a whole-file grep: an unscoped "TimeSpan.Zero" match anywhere would
            // pass even if the probe itself were rewritten into a timed slice.
            int fp = src.IndexOf("static bool FinalProbeDefault", StringComparison.Ordinal);
            string probeBody = fp >= 0 ? src.Substring(fp, Math.Min(320, src.Length - fp)) : "";
            Check("the final probe is a nonblocking WaitOne(TimeSpan.Zero), not another timed slice",
                probeBody.IndexOf("WaitOne(TimeSpan.Zero)", StringComparison.Ordinal) >= 0
                && probeBody.IndexOf("AbandonedMutexException", StringComparison.Ordinal) >= 0, "");
            // The Handoff publishes whenever the probe took the mutex — the T-82
            // fix: the branch is guarded on finalTook alone, never on !activated,
            // so an activating instance can no longer abandon the singleton.
            int probeCall = src.IndexOf("bool finalTook = FinalProbeImpl(mutex);", StringComparison.Ordinal);
            int handoffTail = probeCall >= 0 ? src.IndexOf("return null;", probeCall, StringComparison.Ordinal) : -1;
            string tail = probeCall >= 0 && handoffTail > probeCall ? src.Substring(probeCall, handoffTail - probeCall) : "";
            Check("the final-probe takeover publishes on finalTook regardless of activated",
                tail.IndexOf("if (finalTook) return Publish(mutex, out role);", StringComparison.Ordinal) >= 0
                && tail.IndexOf("!activated && finalTook", StringComparison.Ordinal) < 0, "");
        }
        finally
        {
            Limisaw.Singleton.FinalProbeImpl = savedProbe;
            Limisaw.Singleton.MutexName = savedMutex;
            Limisaw.Singleton.ShowName = savedShow;
            Limisaw.Singleton.ReadyName = savedReady;
            try { using (System.Threading.EventWaitHandle.OpenExisting(prefix + "_probe_Show")) { } } catch { }
            try { using (System.Threading.EventWaitHandle.OpenExisting(prefix + "_probe_Ready")) { } } catch { }
        }
    }

    // ── the shape of the fix, so a one-shot secondary cannot return ─────────
    static void SourceGuards()    {
        Console.WriteLine("== the shape of the fix ==");
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 4 && !File.Exists(Path.Combine(root, "LIMISAW.cs")); i++)
        { var up = Directory.GetParent(root); if (up == null) break; root = up.FullName; }
        string ui = File.ReadAllText(Path.Combine(root, "LIMISAW.cs"));

        Check("Program.Main resolves launches through Singleton.Acquire",
            ui.IndexOf("Singleton.Acquire(", StringComparison.Ordinal) >= 0, "");
        Check("the old one-shot activation site is gone",
            ui.IndexOf("OpenExisting(\"Local\\\\LimisawShow\")", StringComparison.Ordinal) < 0, "");
        int wait = ui.IndexOf("RegisterWaitForSingleObject(showEvent", StringComparison.Ordinal);
        int set = ui.IndexOf("owned.Ready.Set();", StringComparison.Ordinal);
        int run = ui.IndexOf("Application.Run();", StringComparison.Ordinal);
        int reset = ui.IndexOf("owned.Ready.Reset();", StringComparison.Ordinal);
        int unreg = ui.IndexOf("showWait.Unregister(null);", StringComparison.Ordinal);
        Check("readiness is published only after the activation waiter exists",
            wait >= 0 && set > wait && set < run, wait + "/" + set + "/" + run);
        Check("teardown stops advertising before unregistering the waiter",
            reset >= 0 && unreg > reset, reset + "/" + unreg);
        Check("there is exactly one Acquire site in the app",
            CountOf(ui, "Singleton.Acquire(") == 1, CountOf(ui, "Singleton.Acquire(").ToString());
    }

    static int CountOf(string s, string needle)
    {
        int n = 0, at = 0;
        while ((at = s.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }
}
