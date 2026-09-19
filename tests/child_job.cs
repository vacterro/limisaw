using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Limisaw;

// SRC-004 CORE-004 + W2-005: who owns the death of a spawned process.
//
// The old ChildSweeper.Arm put LIMISAW ITSELF into a KILL_ON_JOB_CLOSE job, so
// every process the app ever started joined it — including the two the user can
// SEE: the vendor installer's PowerShell window (LIMISAW.cs InstallCli) and the
// ini editor (OpenIni). Quitting the tray icon while an installer was writing
// files would terminate it mid-install. That is the CORE-004 half.
//
// The W2-005 half is the opposite failure: killing is too SHALLOW. proc.Kill()
// and RpcSession.Dispose end one process each, so a vendor helper or grandchild
// survives a timeout and accumulates across retries.
//
// Both are answered by per-operation jobs: Cli.Run opens one per invocation and
// RpcSession owns one per pooled child. Disposing the scope terminates the whole
// tree immediately, and because the handle carries KILL_ON_JOB_CLOSE, an app
// killed mid-sweep still takes its probe children with it — while anything
// launched WITHOUT a scope (installer, editor) is structurally outside.
//
// What this harness proves with real processes: a probe child's grandchild dies
// with the scope, a process outside any scope survives the same event, LIMISAW
// itself is never inside a scope, ten timed-out operations accumulate nothing,
// and the containment survives the parent being killed rather than exiting.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main ChildJobTest)
public static class ChildJobTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Dir;
    static string SpawnerExe;   // starts a child then sleeps: the tree case
    static string SleeperExe;   // sleeps: the standalone case

    public static int Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "limisaw_childjob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        try
        {
            if (!BuildFixtures()) return Report();

            ScopeShape();
            TreeDiesWithScope();
            OldDesignKillsTheInstaller();
            RealCliRunTimeout();
            RealRpcSessionDispose();
            NonProbeSurvives();
            RepeatedTimeoutsAccumulateNothing();
            AbnormalExitStillSweeps();
            SourceGuards();
        }
        finally
        {
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

    // ── fixtures: two tiny real console programs ────────────────────────────

    static bool BuildFixtures()
    {
        string spawnerSrc = Path.Combine(Dir, "spawner.cs");
        File.WriteAllText(spawnerSrc,
            "using System;using System.Diagnostics;using System.IO;using System.Threading;\n" +
            "static class S{static void Main(string[] a){\n" +
            "  var p=Process.Start(new ProcessStartInfo(a[0],\"600\"){UseShellExecute=false,CreateNoWindow=true});\n" +
            "  File.WriteAllText(a[1], p.Id.ToString());\n" +
            "  Thread.Sleep(600000);}}\n");
        string sleeperSrc = Path.Combine(Dir, "sleeper.cs");
        File.WriteAllText(sleeperSrc,
            "using System;using System.Threading;\n" +
            "static class L{static void Main(string[] a){Thread.Sleep(int.Parse(a.Length>0?a[0]:\"600\")*1000);}}\n");

        SpawnerExe = Path.Combine(Dir, "spawner.exe");
        SleeperExe = Path.Combine(Dir, "sleeper.exe");
        bool ok = Compile(spawnerSrc, SpawnerExe) && Compile(sleeperSrc, SleeperExe);
        Check("the process fixtures compiled", ok, ok ? "" : "csc failed");
        return ok;
    }

    static bool Compile(string src, string exe)
    {
        string csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        if (!File.Exists(csc)) csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
            "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
        try
        {
            var build = Process.Start(new ProcessStartInfo(csc, "-nologo -out:\"" + exe + "\" \"" + src + "\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            build.WaitForExit(60000);
            return build.ExitCode == 0 && File.Exists(exe);
        }
        catch { return false; }
    }

    static bool Alive(int pid)
    {
        try { Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    static bool GoneWithin(int pid, int ms)
    {
        for (int waited = 0; waited < ms; waited += 50)
        {
            if (!Alive(pid)) return true;
            Thread.Sleep(50);
        }
        return !Alive(pid);
    }

    // ── the scope's own shape ───────────────────────────────────────────────

    static void ScopeShape()
    {
        ChildSweeper.Scope scope = ChildSweeper.Open();
        Check("a scope arms on this Windows", scope.Armed, "");
        // The whole CORE-004 defect in one assertion: the app must not be a member.
        Check("LIMISAW itself is NOT inside the probe scope", !ChildSweeper.SelfInside(scope), "");
        var proc = Process.Start(new ProcessStartInfo(SleeperExe, "600")
        { UseShellExecute = false, CreateNoWindow = true });
        int pid = proc.Id;
        Check("an adopted child joins the scope", scope.Adopt(proc) && scope.Contains(proc),
            "adopted=" + scope.AdoptedCount);
        var outside = Process.Start(new ProcessStartInfo(SleeperExe, "600")
        { UseShellExecute = false, CreateNoWindow = true });
        Check("a child started without adoption is not a member", !scope.Contains(outside), "");
        scope.Dispose();
        Check("disposing the scope ends the adopted child", GoneWithin(pid, 5000), "");
        Check("...and leaves the unadopted one running", Alive(outside.Id), "");
        try { outside.Kill(); } catch { }
        try { proc.Dispose(); outside.Dispose(); } catch { }
    }

    // ── W2-005: the GRANDCHILD is the point ─────────────────────────────────

    static void TreeDiesWithScope()
    {
        string pidFile = Path.Combine(Dir, "grandchild_" + Guid.NewGuid().ToString("N") + ".txt");
        ChildSweeper.Scope scope = ChildSweeper.Open();
        var parent = Process.Start(new ProcessStartInfo(SpawnerExe, "\"" + SleeperExe + "\" \"" + pidFile + "\"")
        { UseShellExecute = false, CreateNoWindow = true });
        scope.Adopt(parent);
        int childPid = WaitForPid(pidFile, 20000);
        Check("the probe child started a grandchild", childPid > 0, "pid=" + childPid);
        if (childPid <= 0) { scope.Dispose(); return; }
        Check("the grandchild is alive before the timeout", Alive(childPid), "");

        // What the OLD code did at a timeout: kill the direct process only.
        try { parent.Kill(); } catch { }
        Check("killing only the direct process leaves the grandchild alive",
            Alive(childPid), "this is the reported defect");

        scope.Dispose();
        Check("disposing the operation scope kills the grandchild too",
            GoneWithin(childPid, 5000), "");
        try { parent.Dispose(); } catch { }
        try { File.Delete(pidFile); } catch { }
    }

    static int WaitForPid(string file, int ms)
    {
        for (int waited = 0; waited < ms; waited += 100)
        {
            try
            {
                if (File.Exists(file))
                {
                    int pid;
                    if (int.TryParse(File.ReadAllText(file).Trim(), out pid) && pid > 0) return pid;
                }
            }
            catch { }
            Thread.Sleep(100);
        }
        return -1;
    }

    // ── the old design, reproduced, so the harm is measured not asserted ────

    // The RED CONTROL for CORE-004. A host process replays exactly what
    // ChildSweeper.Arm used to do — assign ITSELF to a KILL_ON_JOB_CLOSE job —
    // then launches a visible-installer-shaped process and is killed. Under the
    // old design that installer dies with the app; a scope-based design leaves
    // it running (already proven above by NonProbeSurvives).
    static void OldDesignKillsTheInstaller()
    {
        string pidFile = Path.Combine(Dir, "olddesign.txt");
        string src = Path.Combine(Dir, "oldarm.cs");
        File.WriteAllText(src,
            "using System;using System.Diagnostics;using System.IO;using System.Runtime.InteropServices;using System.Threading;\n" +
            "static class O{\n" +
            "  [DllImport(\"kernel32.dll\",CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a,string n);\n" +
            "  [DllImport(\"kernel32.dll\")] static extern bool SetInformationJobObject(IntPtr j,int c,ref EXT i,int cb);\n" +
            "  [DllImport(\"kernel32.dll\")] static extern bool AssignProcessToJobObject(IntPtr j,IntPtr p);\n" +
            "  [DllImport(\"kernel32.dll\")] static extern IntPtr GetCurrentProcess();\n" +
            "  [StructLayout(LayoutKind.Sequential)] struct BASIC{public long a,b;public uint f;public UIntPtr c,d;public uint e;public UIntPtr g;public uint h,i;}\n" +
            "  [StructLayout(LayoutKind.Sequential)] struct IOC{public ulong a,b,c,d,e,f;}\n" +
            "  [StructLayout(LayoutKind.Sequential)] struct EXT{public BASIC B;public IOC I;public UIntPtr a,b,c,d;}\n" +
            "  static void Main(string[] v){\n" +
            "    IntPtr job=CreateJobObject(IntPtr.Zero,null);\n" +
            "    var info=new EXT(); info.B.f=0x2000;\n" +
            "    SetInformationJobObject(job,9,ref info,Marshal.SizeOf(typeof(EXT)));\n" +
            "    AssignProcessToJobObject(job,GetCurrentProcess());\n" +   // the old Arm
            "    var p=Process.Start(new ProcessStartInfo(v[0],\"600\"){UseShellExecute=false,CreateNoWindow=true});\n" +
            "    File.WriteAllText(v[1], p.Id.ToString());\n" +
            "    Thread.Sleep(600000);}}\n");
        string exe = Path.Combine(Dir, "oldarm.exe");
        if (!Compile(src, exe))
        {
            Check("the old-design control compiled", false, "csc failed");
            return;
        }
        var host = Process.Start(new ProcessStartInfo(exe, "\"" + SleeperExe + "\" \"" + pidFile + "\"")
        { UseShellExecute = false, CreateNoWindow = true });
        int installerPid = WaitForPid(pidFile, 20000);
        Check("the old design's host launched an installer-shaped child",
            installerPid > 0 && Alive(installerPid), "pid=" + installerPid);
        try { host.Kill(); } catch { }
        Check("the OLD process-wide job kills that installer with the app",
            installerPid > 0 && GoneWithin(installerPid, 8000),
            "measured harm, not an assertion about the fix");
        try { host.Dispose(); } catch { }
        try { File.Delete(pidFile); } catch { }
    }

    // ── the two production spawn sites, driven for real ─────────────────────

    // Cli.Run's own timeout path: a fixture CLI that starts a helper and then
    // hangs. The audit's requirement is that after ONE timeout both the CLI and
    // its helper are gone while LIMISAW keeps running.
    static void RealCliRunTimeout()
    {
        string pidFile = Path.Combine(Dir, "clirun.txt");
        double t0 = Stamp.Now;
        Cli.Result res = Cli.Run(SpawnerExe, new[] { SleeperExe, pidFile }, Stamp.Now + 1.5, Dir);
        int childPid = WaitForPid(pidFile, 5000);
        Check("Cli.Run reports the timeout", res.Error == "timeout", "error=" + res.Error);
        Check("...within its deadline", Stamp.Now - t0 < 20, "elapsed=" + (int)(Stamp.Now - t0) + "s");
        Check("Cli.Run's timeout sweeps the CLI's own helper",
            childPid > 0 && GoneWithin(childPid, 6000), "pid=" + childPid);
        try { File.Delete(pidFile); } catch { }
    }

    // The pooled Codex app-server: dropping the session must end its tree, not
    // just the direct child. Its stdio is redirected, so the fixture is started
    // as an app-server would be and then dropped through Dispose.
    static void RealRpcSessionDispose()
    {
        string pidFile = Path.Combine(Dir, "rpc.txt");
        // RpcSession.Start appends "app-server --stdio"; the spawner reads its
        // own first two arguments, so a wrapper passes the paths ahead of them.
        string wrapperSrc = Path.Combine(Dir, "rpcfix.cs");
        File.WriteAllText(wrapperSrc,
            "using System;using System.Diagnostics;using System.IO;using System.Threading;\n" +
            "static class W{static void Main(){\n" +
            "  string sleeper=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_SLEEPER\");\n" +
            "  string pf=Environment.GetEnvironmentVariable(\"LIMISAW_TEST_PIDFILE\");\n" +
            "  var p=Process.Start(new ProcessStartInfo(sleeper,\"600\"){UseShellExecute=false,CreateNoWindow=true});\n" +
            "  File.WriteAllText(pf, p.Id.ToString());\n" +
            "  string line; while((line=Console.ReadLine())!=null){}\n" +
            "  Thread.Sleep(600000);}}\n");
        string wrapperExe = Path.Combine(Dir, "rpcfix.exe");
        if (!Compile(wrapperSrc, wrapperExe))
        {
            Check("the app-server fixture compiled", false, "csc failed");
            return;
        }
        Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", SleeperExe);
        Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", pidFile);
        try
        {
            CodexSource.RpcSession session = CodexSource.RpcSession.Start(wrapperExe, Dir);
            Check("the pooled session started", session != null && session.Alive, "");
            if (session == null) return;
            int childPid = WaitForPid(pidFile, 20000);
            Check("the app-server started a helper", childPid > 0 && Alive(childPid), "pid=" + childPid);
            session.Dispose();
            Check("dropping the session sweeps the app-server's helper",
                childPid > 0 && GoneWithin(childPid, 6000), "");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIMISAW_TEST_SLEEPER", null);
            Environment.SetEnvironmentVariable("LIMISAW_TEST_PIDFILE", null);
            try { File.Delete(pidFile); } catch { }
        }
    }

    // ── CORE-004: the installer must survive ────────────────────────────────
    static void NonProbeSurvives()
    {
        // InstallCli/OpenIni open no scope, so a user-visible process is outside
        // every job. Two live probe scopes closing must not touch it.
        var visible = Process.Start(new ProcessStartInfo(SleeperExe, "600")
        { UseShellExecute = false, CreateNoWindow = true });
        int visiblePid = visible.Id;

        var probes = new List<int>();
        var scopes = new List<ChildSweeper.Scope>();
        for (int i = 0; i < 2; i++)
        {
            ChildSweeper.Scope scope = ChildSweeper.Open();
            var proc = Process.Start(new ProcessStartInfo(SleeperExe, "600")
            { UseShellExecute = false, CreateNoWindow = true });
            scope.Adopt(proc);
            probes.Add(proc.Id);
            scopes.Add(scope);
        }
        foreach (ChildSweeper.Scope s in scopes) s.Dispose();

        bool probesGone = true;
        foreach (int pid in probes) if (!GoneWithin(pid, 5000)) probesGone = false;
        Check("closing every probe scope ends every probe child", probesGone, "");
        Check("a process launched outside any scope survives", Alive(visiblePid),
            "the installer/editor case");
        try { visible.Kill(); visible.Dispose(); } catch { }
    }

    // ── W2-005: ten timeouts, nothing left behind ───────────────────────────

    static void RepeatedTimeoutsAccumulateNothing()
    {
        var leaked = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            string pidFile = Path.Combine(Dir, "rep_" + i + ".txt");
            ChildSweeper.Scope scope = ChildSweeper.Open();
            var parent = Process.Start(new ProcessStartInfo(SpawnerExe, "\"" + SleeperExe + "\" \"" + pidFile + "\"")
            { UseShellExecute = false, CreateNoWindow = true });
            scope.Adopt(parent);
            int childPid = WaitForPid(pidFile, 20000);
            int parentPid = parent.Id;
            try { parent.Kill(); } catch { }       // the timeout path
            scope.Dispose();                        // the containment
            try { parent.Dispose(); } catch { }
            if (childPid > 0 && !GoneWithin(childPid, 5000)) leaked.Add(childPid);
            if (!GoneWithin(parentPid, 5000)) leaked.Add(parentPid);
            try { File.Delete(pidFile); } catch { }
        }
        Check("ten timed-out probe trees leave nothing running", leaked.Count == 0,
            "leaked=" + leaked.Count);
    }

    // ── the mid-sweep exit that started all of this ─────────────────────────

    static void AbnormalExitStillSweeps()
    {
        // A separate LIMISAW-shaped process opens a scope, adopts a child and is
        // then KILLED — no finally, no Dispose, exactly what a ThreadPool sweep
        // gets at process exit. KILL_ON_JOB_CLOSE must still sweep the child,
        // because the dying process's handles close.
        string pidFile = Path.Combine(Dir, "abnormal.txt");
        string hostSrc = Path.Combine(Dir, "host.cs");
        File.WriteAllText(hostSrc,
            "using System;using System.Diagnostics;using System.IO;using System.Runtime.InteropServices;using System.Threading;\n" +
            "static class H{\n" +
            "  [DllImport(\"kernel32.dll\",CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a,string n);\n" +
            "  [DllImport(\"kernel32.dll\")] static extern bool SetInformationJobObject(IntPtr j,int c,ref EXT i,int cb);\n" +
            "  [DllImport(\"kernel32.dll\")] static extern bool AssignProcessToJobObject(IntPtr j,IntPtr p);\n" +
            "  [StructLayout(LayoutKind.Sequential)] struct BASIC{public long a,b;public uint f;public UIntPtr c,d;public uint e;public UIntPtr g;public uint h,i;}\n" +
            "  [StructLayout(LayoutKind.Sequential)] struct IOC{public ulong a,b,c,d,e,f;}\n" +
            "  [StructLayout(LayoutKind.Sequential)] struct EXT{public BASIC B;public IOC I;public UIntPtr a,b,c,d;}\n" +
            "  static void Main(string[] v){\n" +
            "    IntPtr job=CreateJobObject(IntPtr.Zero,null);\n" +
            "    var info=new EXT(); info.B.f=0x2000;\n" +
            "    SetInformationJobObject(job,9,ref info,Marshal.SizeOf(typeof(EXT)));\n" +
            "    var p=Process.Start(new ProcessStartInfo(v[0],\"600\"){UseShellExecute=false,CreateNoWindow=true});\n" +
            "    AssignProcessToJobObject(job,p.Handle);\n" +
            "    File.WriteAllText(v[1], p.Id.ToString());\n" +
            "    Thread.Sleep(600000);}}\n");
        string hostExe = Path.Combine(Dir, "host.exe");
        if (!Compile(hostSrc, hostExe))
        {
            Check("the abnormal-exit host compiled", false, "csc failed");
            return;
        }
        var host = Process.Start(new ProcessStartInfo(hostExe, "\"" + SleeperExe + "\" \"" + pidFile + "\"")
        { UseShellExecute = false, CreateNoWindow = true });
        int childPid = WaitForPid(pidFile, 20000);
        Check("the host adopted a probe child", childPid > 0 && Alive(childPid), "pid=" + childPid);
        try { host.Kill(); } catch { }               // no Dispose, no finally
        Check("killing the host still sweeps its probe child",
            childPid > 0 && GoneWithin(childPid, 8000), "");
        try { host.Dispose(); } catch { }
        try { File.Delete(pidFile); } catch { }
    }

    // ── the shape must stay in the source ───────────────────────────────────

    static void SourceGuards()
    {
        string root = SourceRoot();
        string sweeper = File.ReadAllText(Path.Combine(root, "ChildSweeper.cs"));
        string probe = File.ReadAllText(Path.Combine(root, "Probe.cs"));
        string form = File.ReadAllText(Path.Combine(root, "LIMISAW.cs"));

        // The defect's exact signature: the win32 GetCurrentProcess import that
        // existed only to hand THIS process to the job, plus the Arm entry point
        // that did it. SelfInside's managed Process.GetCurrentProcess is the
        // opposite — it checks membership rather than granting it.
        Check("ChildSweeper no longer assigns this process to a job",
            !sweeper.Contains("static extern IntPtr GetCurrentProcess")
            && !sweeper.Contains("AssignProcessToJobObject(handle, GetCurrentProcess")
            && !sweeper.Contains("void Arm("),
            "the CORE-004 defect");
        Check("Main does not arm a process-wide job", !form.Contains("ChildSweeper.Arm"), "");
        Check("the scope carries KILL_ON_JOB_CLOSE",
            sweeper.Contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE"), "");
        Check("the scope can terminate a tree on demand",
            sweeper.Contains("TerminateJobObject"), "");
        Check("Cli.Run opens a scope and adopts its child",
            probe.Contains("ChildSweeper.Open()") && probe.Contains("scope.Adopt(proc)"), "");
        Check("RpcSession owns a scope for its pooled child",
            probe.Contains("ChildSweeper.Scope Scope") && probe.Contains("Scope.Dispose()"), "");
        int opens = 0;
        for (int i = probe.IndexOf("ChildSweeper.Open()"); i >= 0; i = probe.IndexOf("ChildSweeper.Open()", i + 1)) opens++;
        Check("exactly the two probe spawn sites open scopes", opens == 2, "opens=" + opens);
        Check("the visible installer stays outside containment",
            form.Contains("UseShellExecute = true") && !form.Contains("ChildSweeper.Open"), "");
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
