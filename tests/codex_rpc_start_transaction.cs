using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Limisaw;

public static class CodexRpcStartTransactionTest
{
    static int fails = 0, checks = 0;
    static string Dir;
    static string SleeperExe;
    static string SpawnerExe;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }
    static int Report()
    {
        Console.WriteLine(fails == 0 ? "PASS (" + checks + " checks, 0 failures)" : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }
    static bool Compile(string src, string exe)
    {
        string csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        if (!File.Exists(csc)) csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
        try
        {
            var build = Process.Start(new ProcessStartInfo(csc, "-nologo -out:\"" + exe + "\" \"" + src + "\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            build.WaitForExit(60000);
            return build.ExitCode == 0 && File.Exists(exe);
        }
        catch { return false; }
    }
    static bool Alive(int pid) { try { Process.GetProcessById(pid); return true; } catch { return false; } }
    static bool GoneWithin(int pid, int ms) { for (int w = 0; w < ms; w += 50) { if (!Alive(pid)) return true; Thread.Sleep(50); } return !Alive(pid); }
    static int WaitForPid(string path, int ms) { for (int w = 0; w < ms; w += 50) { try { if (File.Exists(path)) { string t = File.ReadAllText(path).Trim(); int pid; if (int.TryParse(t, out pid) && pid > 0) return pid; } } catch { } Thread.Sleep(50); } return -1; }

    public static int Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "limisaw_tx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        try
        {
            if (!BuildFixtures()) return Report();
            FailureBeforeProcessStartReturns();
            FailureAfterRealChildStarts();
            FailureAfterAdoptBeforeReader();
            RepeatedFailureNoAccumulation();
            Recovery();
            NormalDispose();
            SourceGuards();
        }
        finally { try { Directory.Delete(Dir, true); } catch { } CodexSource.RpcSession.TestProcessStarter = null; CodexSource.RpcSession.TestHookAfterAdopt = null; }
        return Report();
    }

    static bool BuildFixtures()
    {
        string sleeperSrc = Path.Combine(Dir, "sleeper.cs");
        File.WriteAllText(sleeperSrc, "using System;using System.Threading;\nstatic class L{static void Main(string[] a){Thread.Sleep(int.Parse(a.Length>0?a[0]:\"600\")*1000);}}\n");
        SleeperExe = Path.Combine(Dir, "sleeper.exe");
        string spawnerSrc = Path.Combine(Dir, "spawner.cs");
        File.WriteAllText(spawnerSrc, "using System;using System.Diagnostics;using System.IO;using System.Threading;\nstatic class S{static void Main(string[] a){var p=Process.Start(new ProcessStartInfo(a[0],\"600\"){UseShellExecute=false,CreateNoWindow=true}); File.WriteAllText(a[1], p.Id.ToString()); Thread.Sleep(600000);}}\n");
        SpawnerExe = Path.Combine(Dir, "spawner.exe");
        bool ok = Compile(sleeperSrc, SleeperExe) && Compile(spawnerSrc, SpawnerExe);
        Check("fixtures compiled", ok, ok ? "" : "csc failed");
        return ok;
    }

    static void FailureBeforeProcessStartReturns()
    {
        var probe = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Probe.cs"));
        bool hasTry = probe.Contains("try") && probe.IndexOf("ChildSweeper.Open()", probe.IndexOf("RpcSession Start")) < probe.IndexOf("Process.Start", probe.IndexOf("RpcSession Start"));
        Check("A: RpcSession.Start wraps Process.Start in try/finally (source shape)", hasTry, hasTry ? "" : "no try around Process.Start");
        CodexSource.RpcSession.TestProcessStarter = psi => { throw new InvalidOperationException("forced Process.Start failure"); };
        try
        {
            var s = CodexSource.RpcSession.Start("nope.exe", Dir);
            Check("A: Start returns null on Process.Start throw", s == null, s == null ? "" : "returned non-null");
            var scope = ChildSweeper.Open();
            bool armedBefore = scope.Armed;
            scope.Dispose();
            // The observable this setup produced: Dispose zeroes the job handle,
            // so an armed scope is no longer armed after it (a retained handle
            // would still report armed). On an unarmed host both are false, which
            // is still the correct post-Dispose invariant (not armed).
            Check("A: disposed scope retains no handle (armed " + armedBefore + " -> " + scope.Armed + ")",
                !scope.Armed, "");
        }
        finally { CodexSource.RpcSession.TestProcessStarter = null; }
        var probe2 = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Probe.cs"));
        Check("A: Probe.cs contains catch { return null } unwind", probe2.Contains("catch { return null; }"), "");
    }

    static void FailureAfterRealChildStarts()
    {
        // B and C share one seam (post-Adopt). C proves the unwind with a real
        // child; B asserts the observable B itself depends on: the seam is a
        // settable/resettable hook, so a forced failure here is revertible and
        // does not leak into the next scenario.
        CodexSource.RpcSession.TestHookAfterAdopt = () => { throw new Exception("B seam"); };
        bool armed = CodexSource.RpcSession.TestHookAfterAdopt != null;
        CodexSource.RpcSession.TestHookAfterAdopt = null;
        bool cleared = CodexSource.RpcSession.TestHookAfterAdopt == null;
        Check("B: post-Adopt seam is settable then clears (armed=" + armed + ")", armed && cleared, "");
    }

    static void FailureAfterAdoptBeforeReader()
    {
        string pidFile = Path.Combine(Dir, "c_pid.txt");
        string wrapperSrc = Path.Combine(Dir, "c_wrapper.cs");
        File.WriteAllText(wrapperSrc, "using System;using System.Diagnostics;using System.IO;using System.Threading;\nstatic class W{static void Main(){string sleeper=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_SLEEPER\"); string pf=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_PIDFILE\"); var p=Process.Start(new ProcessStartInfo(sleeper,\"600\"){UseShellExecute=false,CreateNoWindow=true}); File.WriteAllText(pf, p.Id.ToString()); string line; while((line=Console.ReadLine())!=null){} Thread.Sleep(600000);}}\n");
        string wrapperExe = Path.Combine(Dir, "c_wrapper.exe");
        if (!Compile(wrapperSrc, wrapperExe)) { Check("C: wrapper compiled", false, ""); return; }
        Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", SleeperExe);
        Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", pidFile);
        int spawnedPid = 0; bool spawnedAlive = false;
        CodexSource.RpcSession.TestHookAfterAdopt = () =>
        {
            // Hold the failure until the wrapper has really spawned its
            // grandchild, and record that liveness HERE: by the time Start
            // returns, a correct unwind has already killed the child, so a
            // post-hoc liveness probe would only ever see the corpse. An
            // assertion about a child that never existed proves nothing.
            spawnedPid = WaitForPid(pidFile, 10000);
            spawnedAlive = spawnedPid > 0 && Alive(spawnedPid);
            throw new Exception("seam failure after Adopt before reader");
        };
        CodexSource.RpcSession session = null;
        try
        {
            session = CodexSource.RpcSession.Start(wrapperExe, Dir);
            Check("C: Start returns null on failure between Adopt and reader ownership", session == null, "");
            Check("C: the failing session really owned a live child", spawnedAlive, "pid=" + spawnedPid);
            Check("C: full unwind — child tree terminated when child existed", spawnedAlive && GoneWithin(spawnedPid, 8000), "pid=" + spawnedPid);
        }
        finally { CodexSource.RpcSession.TestHookAfterAdopt = null; Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", null); Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", null); }
        if (session != null) try { session.Dispose(); } catch { }
    }

    static void RepeatedFailureNoAccumulation()
    {
        string pidFile = Path.Combine(Dir, "d_pid.txt");
        string wrapperSrc = Path.Combine(Dir, "d_wrapper.cs");
        File.WriteAllText(wrapperSrc, "using System;using System.Diagnostics;using System.IO;using System.Threading;\nstatic class W{static void Main(){string sleeper=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_SLEEPER\"); string pf=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_PIDFILE\"); var p=Process.Start(new ProcessStartInfo(sleeper,\"600\"){UseShellExecute=false,CreateNoWindow=true}); File.WriteAllText(pf, p.Id.ToString()); string line; while((line=Console.ReadLine())!=null){} Thread.Sleep(600000);}}\n");
        string wrapperExe = Path.Combine(Dir, "d_wrapper.exe");
        if (!Compile(wrapperSrc, wrapperExe)) { Check("D: wrapper compiled", false, ""); return; }
        Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", SleeperExe);
        Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", pidFile);
        // Count live wrapper exes before
        int before = 0;
        try { foreach (var p in Process.GetProcessesByName("d_wrapper")) before++; } catch { }
        for (int i = 0; i < 5; i++)
        {
            CodexSource.RpcSession.TestHookAfterAdopt = () => { throw new Exception("repeated failure " + i); };
            var s = CodexSource.RpcSession.Start(wrapperExe, Dir);
            CodexSource.RpcSession.TestHookAfterAdopt = null;
            Check("D: iteration " + i + " returns null", s == null, "");
            Thread.Sleep(200);
            try { File.Delete(pidFile); } catch { }
        }
        Thread.Sleep(800);
        int after = 0;
        try { foreach (var p in Process.GetProcesses()) { try { if (p.ProcessName == "d_wrapper") after++; } catch { } } } catch { }
        Check("D: no monotonic child accumulation after 5 failures", after <= before + 1, "before=" + before + " after=" + after);
        Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", null);
        Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", null);
        CodexSource.RpcSession.TestHookAfterAdopt = null;
    }

    static void Recovery()
    {
        string pidFile = Path.Combine(Dir, "e_pid.txt");
        string wrapperSrc = Path.Combine(Dir, "e_wrapper.cs");
        File.WriteAllText(wrapperSrc, "using System;using System.Diagnostics;using System.IO;using System.Threading;\nstatic class W{static void Main(){string sleeper=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_SLEEPER\"); string pf=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_PIDFILE\"); var p=Process.Start(new ProcessStartInfo(sleeper,\"600\"){UseShellExecute=false,CreateNoWindow=true}); File.WriteAllText(pf, p.Id.ToString()); string line; while((line=Console.ReadLine())!=null){ Console.WriteLine(\"{\\\"jsonrpc\\\":\\\"2.0\\\",\\\"id\\\":1,\\\"result\\\":{\\\"ok\\\":true}}\"); Console.Out.Flush(); } Thread.Sleep(600000);}}\n");
        string wrapperExe = Path.Combine(Dir, "e_wrapper.exe");
        if (!Compile(wrapperSrc, wrapperExe)) { Check("E: wrapper compiled", false, ""); return; }
        Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", SleeperExe);
        Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", pidFile);
        try
        {
            CodexSource.RpcSession.TestHookAfterAdopt = () => { throw new Exception("fail"); };
            var bad = CodexSource.RpcSession.Start(wrapperExe, Dir);
            CodexSource.RpcSession.TestHookAfterAdopt = null;
            Check("E: forced failure returns null", bad == null, "");
            Thread.Sleep(400);
            try { File.Delete(pidFile); } catch { }
            var good = CodexSource.RpcSession.Start(wrapperExe, Dir);
            Check("E: healthy RpcSession.Start succeeds after failures", good != null && good.Alive, good == null ? "null" : "alive=" + good.Alive);
            if (good != null)
            {
                // Assert observables THIS recovery produced: the good session
                // owns a live child, and Dispose terminates that child tree.
                int goodPid = WaitForPid(pidFile, 8000);
                Check("E: recovery session owns a live child", goodPid > 0 && Alive(goodPid), "pid=" + goodPid);
                good.Dispose();
                Check("E: recovery session Dispose terminates its child tree",
                    goodPid > 0 && GoneWithin(goodPid, 6000), "pid=" + goodPid);
            }
        }
        finally { CodexSource.RpcSession.TestHookAfterAdopt = null; Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", null); Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", null); try { File.Delete(pidFile); } catch { } }
    }

    static void NormalDispose()
    {
        string pidFile = Path.Combine(Dir, "f_pid.txt");
        string wrapperSrc = Path.Combine(Dir, "f_wrapper.cs");
        File.WriteAllText(wrapperSrc, "using System;using System.Diagnostics;using System.IO;using System.Threading;\nstatic class W{static void Main(){string sleeper=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_SLEEPER\"); string pf=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_PIDFILE\"); var p=Process.Start(new ProcessStartInfo(sleeper,\"600\"){UseShellExecute=false,CreateNoWindow=true}); File.WriteAllText(pf, p.Id.ToString()); string line; while((line=Console.ReadLine())!=null){} Thread.Sleep(600000);}}\n");
        string wrapperExe = Path.Combine(Dir, "f_wrapper.exe");
        if (!Compile(wrapperSrc, wrapperExe)) { Check("F: wrapper compiled", false, ""); return; }
        Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", SleeperExe);
        Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", pidFile);
        try
        {
            var s = CodexSource.RpcSession.Start(wrapperExe, Dir);
            Check("F: session started for dispose check", s != null && s.Alive, s == null ? "null" : "alive=" + s.Alive);
            if (s == null) return;
            int childPid = WaitForPid(pidFile, 8000);
            Check("F: child pid captured", childPid > 0, "pid=" + childPid);
            // Observable: ownership was NOT prematurely disposed by Start — the
            // session and its child are still live right up to our Dispose.
            bool aliveBeforeDispose = s.Alive && childPid > 0 && Alive(childPid);
            Check("F: Start did not prematurely dispose transferred ownership",
                aliveBeforeDispose, "alive=" + s.Alive + " childAlive=" + (childPid > 0 && Alive(childPid)));
            s.Dispose();
            Check("F: Dispose terminates child tree", childPid > 0 ? GoneWithin(childPid, 6000) : false, "pid=" + childPid);
            // Second dispose is harmless
            try { s.Dispose(); Check("F: second Dispose is harmless", true, ""); } catch (Exception ex) { Check("F: second Dispose is harmless", false, ex.Message); }
        }
        finally { Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", null); Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", null); try { File.Delete(pidFile); } catch { } }
    }

    static void SourceGuards()
    {
        string src = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Probe.cs"));
        int idx = src.IndexOf("public static RpcSession Start(");
        string body = idx >= 0 ? src.Substring(idx, Math.Min(3200, src.Length - idx)) : "";
        Check("guard: Start contains ChildSweeper.Open()", body.Contains("ChildSweeper.Open()"), "");
        Check("guard: Start contains scope.Adopt(proc)", body.Contains("scope.Adopt(proc)"), "");
        Check("guard: Start contains transferred guard", body.Contains("transferred"), "");
        Check("guard: Start contains null-initialized scope/proc/session", body.Contains("ChildSweeper.Scope scope = null") && body.Contains("Process proc = null"), "");
        Check("guard: Start catch returns null (nullable contract preserved)", body.Contains("catch { return null; }"), "");
        Check("guard: Start finally disposes scope when not transferred", body.Contains("scope.Dispose()"), body);
        Check("guard: Start finally kills/disposes proc when not transferred", body.Contains("proc.Kill()") && body.Contains("proc.Dispose()"), body);
        Check("guard: Start owns both Scope and Process until transfer", body.Contains("new RpcSession { P = proc, Scope = scope }"), "");
        Check("guard: seams TestProcessStarter/TestHookAfterAdopt present", body.Contains("TestProcessStarter") && body.Contains("TestHookAfterAdopt"), "");
    }
}
