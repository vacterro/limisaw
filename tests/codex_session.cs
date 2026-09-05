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

    class HomeScript
    {
        public string Home;
        public bool Alive = true;
        public bool ReadTimesOut;
        public string ConsumeOutcome;
        public int InitCalls, ReadCalls, ConsumeCalls, Drops;
    }

    static readonly Dictionary<string, HomeScript> Scripts = new Dictionary<string, HomeScript>();
    static int Starts;

    static HomeScript Script(string home)
    {
        HomeScript s;
        string key = CodexSource.SessionPool.Key(home);
        if (!Scripts.TryGetValue(key, out s))
        {
            s = new HomeScript { Home = key };
            Scripts[key] = s;
        }
        return s;
    }

    static void InstallFakeFactory()
    {
        CodexSource.ResolveExe = exe => "fake-codex";
        CodexSource.StartSession = (exe, home) =>
        {
            Starts++;
            HomeScript s = Script(home);
            return new CodexSource.RpcLink
            {
                Call = (method, parameters, deadline) =>
                {
                    if (method == "initialize") { s.InitCalls++; return J.Parse("{\"result\":{}}"); }
                    if (method == "account/rateLimits/read") { s.ReadCalls++; return s.ReadTimesOut ? null : J.Parse("{\"result\":{}}"); }
                    if (method == "account/rateLimitResetCredit/consume")
                    {
                        s.ConsumeCalls++;
                        return J.Parse("{\"result\":{\"outcome\":\"" + (s.ConsumeOutcome ?? "reset") + "\"}}");
                    }
                    return J.Parse("{\"result\":{}}");
                },
                Notify = (method, parameters) => { },
                Alive = () => s.Alive,
                Drop = () => s.Drops++,
            };
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
        Check("...with exactly one process start", Starts == 1, "starts=" + Starts);

        HomeScript a = Script(HomePath(".codex"));
        List<ProbeAccount> second = Sweep();
        Check("the second warm sweep starts NO new app-server", Starts == 1, "starts=" + Starts);
        Check("...and still reads the quota once", a.ReadCalls == 2, "reads=" + a.ReadCalls);
        Check("...without re-initializing the warm session", a.InitCalls == 1, "inits=" + a.InitCalls);
        Check("the warm sweep returns a live reading", second.Count == 1 && second[0].Ok, "");
        Check("the pool holds exactly this home's session", CodexSource.Pool.Count == 1, "pool=" + CodexSource.Pool.Count);

        Console.WriteLine();
        Console.WriteLine("== isolation: three homes, three sessions ==");
        UseScratchProfile(2);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        Starts = 0;

        List<ProbeAccount> multi = Sweep();
        Check("all three discovered homes were probed", multi.Count == 3, multi.Count + " accounts");
        Check("...each with its own process start", Starts == 3, "starts=" + Starts);
        Starts = 0;
        Sweep();
        Check("a second sweep reuses all three sessions", Starts == 0 && CodexSource.Pool.Count == 3,
            "starts=" + Starts + ", pool=" + CodexSource.Pool.Count);
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
        Starts = 0;
        List<ProbeAccount> after = Sweep();
        Check("only the dead home's session was restarted", Starts == 1, "starts=" + Starts);
        Check("...it re-initialized from scratch", alpha.InitCalls == 2, "inits=" + alpha.InitCalls);
        Check("...the dead child was disposed", alpha.Drops >= 1, "drops=" + alpha.Drops);
        Check("the healthy homes kept their sessions", Script(HomePath(".codex")).InitCalls == 1
            && Script(HomePath(".codex-account2")).InitCalls == 1, "");
        Check("...and every account still read", after.Count == 3 && after.TrueForAll(acc => acc.Ok), "");
        alpha.Alive = true; // the replacement child lives; stop simulating the corpse

        Console.WriteLine();
        Console.WriteLine("== a timed-out read drops the session, not the account ==");
        Script(HomePath(".codex")).ReadTimesOut = true;
        Starts = 0;
        List<ProbeAccount> failed = Sweep();
        Check("the timed-out home reports failure, not a crash", failed.Count == 3
            && failed.Exists(acc => !acc.Ok && (acc.Error ?? "").Length > 0), "");
        Check("...its session was dropped from the pool", CodexSource.Pool.Count == 2, "pool=" + CodexSource.Pool.Count);
        Script(HomePath(".codex")).ReadTimesOut = false;
        Starts = 0;
        Sweep();
        Check("the next sweep starts exactly one replacement, re-initialized",
            Starts == 1 && Script(HomePath(".codex")).InitCalls == 2,
            "starts=" + Starts + ", inits=" + Script(HomePath(".codex")).InitCalls);
        Check("...and the pool is whole again", CodexSource.Pool.Count == 3, "pool=" + CodexSource.Pool.Count);

        Console.WriteLine();
        Console.WriteLine("== a home that leaves discovery is evicted ==");
        Directory.Delete(HomePath(".codex-account2"), true);
        Starts = 0;
        Sweep();
        Check("the pool evicted the vanished home", CodexSource.Pool.Count == 2, "pool=" + CodexSource.Pool.Count);
        Check("...its child was disposed", Script(HomePath(".codex-account2")).Drops >= 1, "drops=" + Script(HomePath(".codex-account2")).Drops);
        Check("...with no new start for the survivors", Starts == 0, "starts=" + Starts);

        Console.WriteLine();
        Console.WriteLine("== the reset credit rides the pooled session ==");
        Starts = 0;
        string before = CodexSource.ConsumeResetCredit(HomePath(".codex"), Stamp.Now + 20);
        Check("the credit spend reports the vendor outcome", before == "done — the limit was reset", before);
        Check("...on the warm session, with no new start", Starts == 0, "starts=" + Starts);
        Check("...and exactly one consume call", Script(HomePath(".codex")).ConsumeCalls == 1, "");
        Script(HomePath(".codex")).ConsumeOutcome = "noCredit";
        string empty = CodexSource.ConsumeResetCredit(HomePath(".codex"), Stamp.Now + 20);
        Check("a spent credit says so instead of pretending", empty == "no banked reset on this account any more", empty);

        Console.WriteLine();
        Console.WriteLine("== a real child: a late reply leaves no stale slot ==");
        LateReply();

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
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
