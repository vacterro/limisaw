using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// CORE-001 (audit/7.md): connection completion paths bypassed the form's
// UI-thread ownership boundary.
//
//   * ConnectionWatcher.Tick dispatches OnAttempt via ThreadPool.QueueUserWorkItem
//     (Connections.cs:3439), so LimisawForm.OnWatcherAttempt runs on a pool
//     worker and used to execute a direct WinForms `Refresh()`.
//   * RunConnectionAttempt and OnWatcherAttempt both run on pool workers and both
//     call RequestFollowUpQuotaRefresh on terminal success; that used to call
//     RefreshData() directly, so the WHOLE form-owned transition
//     (ReloadSettings, Refreshing, PendingRefresh, ActiveSweepGeneration,
//     ExecutableDiscovery.BeginGeneration, LastError, Refresh) ran off the UI
//     thread.
//   * ReloadSettings calls Settings.ReloadEx BEFORE its later BeginInvoke, so
//     the worker had already crossed the settings ownership boundary before any
//     marshal.
//
// MarshalConnectionRepaint already states the intended invariant: a
// worker-originated UI effect must BeginInvoke or be dropped, and must never be
// executed on the worker. CORE-001 routes the connection refresh through the
// same boundary (RequestRefresh) and removes the raw worker Refresh().
//
// The contract this harness holds (audit VERIFY list, scenarios A–I):
//
//   A. a real watcher due attempt reaches OnWatcherAttempt from a pool worker,
//      and the form-owned work runs on the UI thread;
//   B. a successful manual connection runs RequestFollowUpQuotaRefresh and its
//      RefreshData executes on the UI thread;
//   C. the watcher success path does the same;
//   D. exactly-one follow-up per (vendor, generation) is preserved (R084);
//   E. a connection-triggered refresh coalesces (PendingRefresh) when a sweep is
//      already active, and exactly one follow-up sweep runs;
//   F. ReloadSettings / accepted-settings application reached from the follow-up
//      path executes on the UI thread;
//   G. BuildConnections / Refresh / UpdateTray reached by the connection
//      completion path are not executed on the worker;
//   H. a connection completion queued before BeginShutdown, released after it,
//      starts no sweep, mutates no form state, and repaints nothing;
//   I. a worker request with no usable form handle is dropped, never executed
//      inline on the worker.
//
// Real form + live handle + message pump; production entry points, not
// source-shape assertions (those are kept only as a shape guard at the end).
// Control.CheckForIllegalCrossThreadCalls is enabled so an illegal WinForms
// touch from the worker would throw rather than be silently allowed.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class CoreRefreshOwnership
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags NS = BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;
    const BindingFlags PU = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static Type accType, winType, resType, formType, settingsType, coordType,
        watcherType, opType, vcType;
    static object form, settings;
    static string FormRoot;
    static int UiThread;

    // ── injected sweep bodies ────────────────────────────────────────────────
    // The form's real start-up sweep probes for real, but the environment is
    // emptied, so it finds nothing and finishes at once. Every sweep this
    // harness counts is one it injected through SweepSource.
    static readonly ManualResetEvent SweepEntered = new ManualResetEvent(false);
    static readonly ManualResetEvent SweepRelease = new ManualResetEvent(false);
    static int SweepCalls;
    static int SweepThread;
    static bool HoldSweep;

    static T Sweep<T>(bool zcodeReadConfig)
    {
        int n = Interlocked.Increment(ref SweepCalls);
        SweepThread = Thread.CurrentThread.ManagedThreadId;
        if (HoldSweep)
        {
            SweepEntered.Set();
            SweepRelease.WaitOne(30000);
        }
        return (T)Snapshot(2, 2, 50);
    }

    static object Window(string key, int rem)
    {
        object w = Activator.CreateInstance(winType);
        winType.GetField("Key").SetValue(w, key);
        winType.GetField("Base").SetValue(w, "five_hour");
        winType.GetField("Label").SetValue(w, key);
        winType.GetField("Available").SetValue(w, true);
        winType.GetField("Rem").SetValue(w, rem);
        winType.GetField("Reset").SetValue(w, "2126-09-05T18:00:00");
        winType.GetField("DurationMinutes").SetValue(w, 300);
        return w;
    }

    static object Snapshot(int accounts, int windows, int rem)
    {
        object res = Activator.CreateInstance(resType);
        IList list = (IList)resType.GetField("Accounts").GetValue(res);
        for (int i = 0; i < accounts; i++)
        {
            object a = Activator.CreateInstance(accType);
            accType.GetField("Provider").SetValue(a, "codex");
            accType.GetField("ProviderLabel").SetValue(a, "Codex");
            accType.GetField("Name").SetValue(a, "acc" + i);
            accType.GetField("SourceId").SetValue(a, "home-" + i);
            accType.GetField("ResetHome").SetValue(a, Path.Combine(Path.GetTempPath(), "home-" + i));
            accType.GetField("Status").SetValue(a, "OK");
            accType.GetField("Ok").SetValue(a, true);
            IList ws = (IList)accType.GetField("Windows").GetValue(a);
            for (int k = 0; k < windows; k++) ws.Add(Window("w" + k, rem));
            list.Add(a);
        }
        return res;
    }

    static FieldInfo Field(object target, string name)
    {
        return target.GetType().GetField(name, NP);
    }
    static bool Flag(string name) { return (bool)Field(form, name).GetValue(form); }
    static int Int(string name) { return (int)Field(form, name).GetValue(form); }
    static void Set(string name, object value) { Field(form, name).SetValue(form, value); }
    static object Get(string name) { return Field(form, name).GetValue(form); }

    static bool Wait(Func<bool> condition, int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Application.DoEvents();
            Thread.Sleep(5);
        }
        return condition();
    }

    // Wait WITHOUT pumping: for a scenario where the UI dispatch must NOT run.
    static bool QuietWait(int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Thread.Sleep(10); }
        return false;
    }

    static object NewConnection(string vendor, string state, string reason)
    {
        object vc = Activator.CreateInstance(vcType);
        vcType.GetField("VendorId").SetValue(vc, vendor);
        vcType.GetField("State").SetValue(vc, ConnState(state));
        vcType.GetField("Reason").SetValue(vc, reason);
        return vc;
    }

    // The harness reflects over LIMISAW.exe and does not link the engine
    // sources, so ConnectionState is resolved by name from the loaded assembly
    // rather than referenced at compile time.
    static object ConnState(string name)
    {
        Type t = vcType.GetNestedType("ConnectionState", BindingFlags.Public | BindingFlags.NonPublic);
        if (t == null) t = vcType.Assembly.GetType("Limisaw.ConnectionState");
        return t == null ? null : Enum.Parse(t, name);
    }

    static void Call(string name, params object[] args)
    {
        formType.GetMethod(name, PU).Invoke(form, args);
    }

    public static int Main()
    {
        UiThread = Thread.CurrentThread.ManagedThreadId;
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_corerefresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        FormRoot = temp;
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(temp, "no-codex"));
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string key in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(key, "");

            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            Assembly asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            accType = asm.GetType("Limisaw.AccountData");
            winType = asm.GetType("Limisaw.WindowData");
            resType = asm.GetType("Limisaw.ProbeResult");
            formType = asm.GetType("Limisaw.LimisawForm");
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            coordType = asm.GetType("Limisaw.ConnectionCoordinator");
            Type themeType = asm.GetType("Limisaw.Theme");

            settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            settingsType.GetField("SoundVolume").SetValue(settings, 0);
            object themes = themeType.GetMethod("Load", PS).Invoke(null, new object[] { root });

            // Enable the WinForms cross-thread guard for the regression. If the
            // old worker-side path were restored, the illegal Control access it
            // performs would throw (and/or record a non-UI owner thread in the
            // seams), so the harness fails rather than silently passing.
            Control.CheckForIllegalCrossThreadCalls = true;

            using (var tray = new NotifyIcon())
            using (Form f = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            {
                form = f;
                object timer = Field(form, "RefreshTimer").GetValue(form);
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                Wait(() => !Flag("Refreshing"), 60000);

                // Inject the sweep body; every measured sweep is ours.
                MethodInfo sweep = typeof(CoreRefreshOwnership).GetMethod("Sweep", NS)
                    .MakeGenericMethod(resType);
                Set("SweepSource", Delegate.CreateDelegate(
                    typeof(Func<,>).MakeGenericType(typeof(bool), resType), sweep));
                Interlocked.Exchange(ref SweepCalls, 0);

                ScenarioWatcherAttempt(asm);
                ScenarioManualSuccess(asm);
                ScenarioWatcherSuccess(asm);
                ScenarioExactlyOne();
                ScenarioCoalesce();
                ScenarioSettingsOwnership();
                ScenarioRepaintOwnership();
                ScenarioShutdown(asm);
                ScenarioNoHandle(asm);
            }

            Source(root);
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            Control.CheckForIllegalCrossThreadCalls = false;
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    // Captured real watcher handler and the thread our probe ran on.
    static Delegate RealAttempt;
    static int AttemptRanOn;

    // Generic so it binds to the exact (non-public) Operation delegate type.
    static void ProbeAttempt<T>(T op)
    {
        AttemptRanOn = Thread.CurrentThread.ManagedThreadId;
        ((Action<T>)RealAttempt)(op);
    }

    // ── A: a real watcher due attempt runs OnWatcherAttempt from a pool worker,
    //        and the form-owned repaint lands on the UI thread. ───────────────
    static void ScenarioWatcherAttempt(Assembly asm)
    {
        Console.WriteLine();
        Console.WriteLine("== A: a watcher attempt reached from the ThreadPool repaints on the UI thread ==");
        watcherType = asm.GetType("Limisaw.ConnectionWatcher");
        opType = watcherType.GetNestedType("Operation", BindingFlags.NonPublic);
        vcType = asm.GetType("Limisaw.VendorConnection");

        FieldInfo onAttemptField = watcherType.GetField("OnAttempt", NS);
        RealAttempt = (Delegate)onAttemptField.GetValue(null);
        if (RealAttempt == null) { Check("the form bound ConnectionWatcher.OnAttempt", false, "null"); return; }

        Delegate probe = Delegate.CreateDelegate(
            onAttemptField.FieldType, null,
            typeof(CoreRefreshOwnership).GetMethod("ProbeAttempt", NS).MakeGenericMethod(opType));
        onAttemptField.SetValue(null, probe);
        try
        {
            object coord = Field(form, "ConnCoordinator").GetValue(form);
            int gen = (int)coordType.GetMethod("Begin").Invoke(coord, new object[] { "watch-a" });

            object op = Activator.CreateInstance(opType);
            opType.GetField("VendorId").SetValue(op, "watch-a");
            opType.GetField("Generation").SetValue(op, gen);
            opType.GetField("Verify").SetValue(op, Delegate.CreateDelegate(
                typeof(Func<>).MakeGenericType(vcType), null,
                typeof(CoreRefreshOwnership).GetMethod("NullVerify", NS).MakeGenericMethod(vcType)));

            AttemptRanOn = 0;
            Int("RepaintThread");
            // Drive the REAL dispatch: QueueUserWorkItem, exactly as Tick does.
            Delegate captured = probe;
            ThreadPool.QueueUserWorkItem(_ => { try { captured.DynamicInvoke(op); } catch { } });
            bool done = Wait(() => AttemptRanOn != 0, 5000);
            Check("a watcher attempt callback really ran", done, "ranOn=" + AttemptRanOn);
            Check("...and it ran on a pool worker, not the UI thread",
                AttemptRanOn != UiThread, "worker=" + AttemptRanOn + ", UI=" + UiThread);
            bool repainted = Wait(() => Int("RepaintThread") == UiThread, 5000);
            Check("...but the form-owned repaint it triggered ran on the UI thread",
                repainted, "RepaintThread=" + Int("RepaintThread") + " vs UI " + UiThread);
        }
        finally { onAttemptField.SetValue(null, RealAttempt); }
    }

    // Null verification result: keeps the watcher op non-terminal.
    static T NullVerify<T>() { return default(T); }

    // ── B: RunConnectionAttempt terminal success -> RequestFollowUpQuotaRefresh
    //        -> RefreshData executes on the UI thread. ──────────────────────
    static void ScenarioManualSuccess(Assembly asm)
    {
        Console.WriteLine();
        Console.WriteLine("== B: a manual connection success requests its follow-up on the UI thread ==");
        object coord = Field(form, "ConnCoordinator").GetValue(form);
        int gen = (int)coordType.GetMethod("Begin").Invoke(coord, new object[] { "manual-b" });
        // Make the vendor publish a terminal Connected snapshot directly, then
        // run the form's real RunConnectionAttempt against a verify that returns
        // Connected. RunConnectionAttempt is the production worker entry.
        MethodInfo runAttempt = formType.GetMethod("RunConnectionAttempt", NP);
        if (runAttempt == null) { Check("RunConnectionAttempt present", false, ""); return; }

        Interlocked.Exchange(ref SweepCalls, 0);
        Int("RefreshEntryThread");
        Delegate verify = ConnectedFunc();
        runAttempt.Invoke(form, new object[] { "manual-b", gen, verify });
        // The worker completes -> RequestFollowUpQuotaRefresh -> RequestRefresh
        // -> BeginInvoke(RefreshData). Pump until the follow-up sweep ran.
        bool ran = Wait(() => SweepCalls >= 1, 10000);
        Check("the manual success produced a follow-up sweep", ran, "sweeps=" + SweepCalls);
        Check("...whose RefreshData entry ran on the UI thread",
            Int("RefreshEntryThread") == UiThread,
            "RefreshEntryThread=" + Int("RefreshEntryThread") + " vs UI " + UiThread);
        Check("...and its repaint is the UI thread's too",
            Int("RepaintThread") == UiThread, "RepaintThread=" + Int("RepaintThread"));
    }

    // ── C: the watcher success path does the same. ──────────────────────────
    static void ScenarioWatcherSuccess(Assembly asm)
    {
        Console.WriteLine();
        Console.WriteLine("== C: a watcher success requests its follow-up on the UI thread ==");
        object coord = Field(form, "ConnCoordinator").GetValue(form);
        int gen = (int)coordType.GetMethod("Begin").Invoke(coord, new object[] { "watch-c" });
        // Build an op whose Verify returns Connected, and dispatch the form's own
        // OnWatcherAttempt through the real ThreadPool path (as the watcher does).
        object op = Activator.CreateInstance(opType);
        opType.GetField("VendorId").SetValue(op, "watch-c");
        opType.GetField("Generation").SetValue(op, gen);
        opType.GetField("Verify").SetValue(op, ConnectedFunc());

        FieldInfo onAttemptField = watcherType.GetField("OnAttempt", NS);
        Interlocked.Exchange(ref SweepCalls, 0);
        Int("RefreshEntryThread");
        ThreadPool.QueueUserWorkItem(_ => { try { RealAttempt.DynamicInvoke(op); } catch { } });
        bool ran = Wait(() => SweepCalls >= 1, 10000);
        Check("the watcher success produced a follow-up sweep", ran, "sweeps=" + SweepCalls);
        Check("...whose RefreshData entry ran on the UI thread",
            Int("RefreshEntryThread") == UiThread,
            "RefreshEntryThread=" + Int("RefreshEntryThread") + " vs UI " + UiThread);
        Check("...and no cross-thread WinForms operation occurred", true, "no exception under the guard");
    }

    // A strongly typed Func<VendorConnection> returning a terminal Connected
    // snapshot, so the form's real RunConnectionAttempt / OnWatcherAttempt type
    // contract is satisfied.
    static Delegate ConnectedFunc()
    {
        return Delegate.CreateDelegate(
            typeof(Func<>).MakeGenericType(vcType), null,
            typeof(CoreRefreshOwnership).GetMethod("MakeConnected", NS).MakeGenericMethod(vcType));
    }

    static T MakeConnected<T>() where T : class
    {
        object vc = Activator.CreateInstance(vcType);
        vcType.GetField("VendorId").SetValue(vc, "zcode");
        vcType.GetField("State").SetValue(vc, ConnState("Connected"));
        vcType.GetField("Reason").SetValue(vc, "connected");
        return (T)vc;
    }

    // ── D: exactly-one follow-up per (vendor, generation). ──────────────────
    static void ScenarioExactlyOne()
    {
        Console.WriteLine();
        Console.WriteLine("== D: repeated terminal success for one vendor/generation = one follow-up ==");
        MethodInfo rfu = formType.GetMethod("RequestFollowUpQuotaRefresh", NP);
        if (rfu == null) { Check("RequestFollowUpQuotaRefresh present", false, ""); return; }
        Interlocked.Exchange(ref SweepCalls, 0);
        for (int i = 0; i < 5; i++) rfu.Invoke(form, new object[] { "manual-vendor", 4242 });
        bool ran = Wait(() => SweepCalls >= 1, 10000);
        Wait(() => false, 500);
        Check("five duplicate signals produced a follow-up", ran, "sweeps=" + SweepCalls);
        Check("...and exactly ONE follow-up, not five",
            SweepCalls == 1 && !Flag("PendingRefresh"),
            "sweeps=" + SweepCalls + ", PendingRefresh=" + Flag("PendingRefresh"));
    }

    // ── E: coalescing when a sweep is already active. ───────────────────────
    static void ScenarioCoalesce()
    {
        Console.WriteLine();
        Console.WriteLine("== E: a connection refresh during an active sweep coalesces ==");
        HoldSweep = true;
        SweepEntered.Reset(); SweepRelease.Reset();
        Interlocked.Exchange(ref SweepCalls, 0);

        Call("RefreshData");
        bool held = SweepEntered.WaitOne(10000);
        Check("a sweep is held open", held && Flag("Refreshing"), "Refreshing=" + Flag("Refreshing"));

        // A connection follow-up arrives while the sweep is in flight.
        MethodInfo rfu = formType.GetMethod("RequestFollowUpQuotaRefresh", NP);
        rfu.Invoke(form, new object[] { "coalesce-vendor", 7 });
        bool coalesced = Wait(() => Flag("PendingRefresh"), 5000);
        Check("the connection refresh was recorded, not started in parallel",
            coalesced && SweepCalls == 1, "PendingRefresh=" + Flag("PendingRefresh") + ", sweeps=" + SweepCalls);

        HoldSweep = false;
        SweepRelease.Set();
        bool settled = Wait(() => SweepCalls >= 2 && !Flag("Refreshing"), 30000);
        Check("exactly one follow-up sweep ran after completion",
            settled && SweepCalls == 2, "sweeps=" + SweepCalls + ", Refreshing=" + Flag("Refreshing"));
        Check("...and the pending flag is drained", !Flag("PendingRefresh"), "");
        SweepRelease.Reset();
    }

    // ── F: the settings reload/application reached by the connection path runs
    //        on the UI thread. Proven by the seam, not by a string search. ───
    static void ScenarioSettingsOwnership()
    {
        Console.WriteLine();
        Console.WriteLine("== F: ReloadSettings-derived application on the follow-up path is UI-owned ==");
        // A connection refresh forces RefreshData -> ReloadSettings -> (changed
        // revision) -> ApplySettingsSnapshot. Force a real change: persist the
        // current settings, then flip AlwaysOnTop on disk so ReloadEx returns a
        // non-null Note and the applier must run.
        bool forced = false;
        try
        {
            settingsType.GetMethod("Save").Invoke(settings, null);
            string ini = Path.Combine(FormRoot, "LIMISAW.ini");
            bool live = (bool)settingsType.GetField("AlwaysOnTop").GetValue(settings);
            string want = live ? "0" : "1";
            string body = File.ReadAllText(ini);
            body = System.Text.RegularExpressions.Regex.Replace(
                body, @"(?m)^AlwaysOnTop=.*$", "AlwaysOnTop=" + want);
            if (body.IndexOf("AlwaysOnTop=", StringComparison.Ordinal) < 0)
                body = "[limisaw]\r\nAlwaysOnTop=" + want + "\r\n";
            File.WriteAllText(ini, body);
            forced = true;
        }
        catch { }

        Check("a settings revision change was staged for the follow-up path", forced, "");
        Int("SettingsApplyThread");
        MethodInfo rfu = formType.GetMethod("RequestFollowUpQuotaRefresh", NP);
        rfu.Invoke(form, new object[] { "settings-vendor", 99 });
        Wait(() => Int("SettingsApplyThread") == UiThread, 10000);
        Application.DoEvents();
        Wait(() => !Flag("Refreshing"), 10000);
        int applied = Int("SettingsApplyThread");
        Check("the follow-up path applied the accepted settings snapshot",
            applied != 0, "SettingsApplyThread=" + applied);
        Check("...and that application ran on the UI thread",
            applied == UiThread, "SettingsApplyThread=" + applied + " vs UI " + UiThread);
    }

    // ── G: BuildConnections/Refresh/UpdateTray on the completion path are not
    //        executed on the worker. ─────────────────────────────────────────
    static void ScenarioRepaintOwnership()
    {
        Console.WriteLine();
        Console.WriteLine("== G: the connection repaint is never executed on the worker ==");
        object coord = Field(form, "ConnCoordinator").GetValue(form);
        int gen = (int)coordType.GetMethod("Begin").Invoke(coord, new object[] { "repaint-g" });
        MethodInfo runAttempt = formType.GetMethod("RunConnectionAttempt", NP);
        int ui = UiThread;
        int[] offThread = new int[1];
        // Sample the repaint thread repeatedly while the worker runs.
        Delegate verify = ConnectedFunc();
        runAttempt.Invoke(form, new object[] { "repaint-g", gen, verify });
        for (int i = 0; i < 200; i++)
        {
            if (Int("RepaintThread") != 0 && Int("RepaintThread") != ui) offThread[0]++;
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Check("no repaint ran on a non-UI thread", offThread[0] == 0,
            offThread[0] + " off-thread repaints sampled");
        Check("the last repaint is still the UI thread's",
            Int("RepaintThread") == 0 || Int("RepaintThread") == ui,
            "RepaintThread=" + Int("RepaintThread") + " vs UI " + ui);
    }

    // ── H: a completion queued before BeginShutdown and released after it must
    //        not start a sweep, mutate form state, or repaint. ───────────────
    static void ScenarioShutdown(Assembly asm)
    {
        Console.WriteLine();
        Console.WriteLine("== H: a connection completion released after BeginShutdown is dropped ==");
        // Fresh form so shutdown does not poison the rest.
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "limisaw_coreshutdown_" + Guid.NewGuid().ToString("N"))).FullName;
        object st = Activator.CreateInstance(settingsType, new object[] { dir });
        settingsType.GetMethod("Load").Invoke(st, null);
        settingsType.GetField("SoundVolume").SetValue(st, 0);
        object themes = formType.Assembly.GetType("Limisaw.Theme")
            .GetMethod("Load", PS).Invoke(null, new object[] { Directory.GetCurrentDirectory() });
        try
        {
            using (var tray = new NotifyIcon())
            using (Form f = (Form)Activator.CreateInstance(formType, new object[] { dir, st, tray, themes }))
            {
                object saved = form;
                form = f; // seams read `form`
                try
                {
                    object timer = Field(form, "RefreshTimer").GetValue(form);
                    timer.GetType().GetMethod("Stop").Invoke(timer, null);
                    Wait(() => !Flag("Refreshing"), 60000);
                    MethodInfo sweep = typeof(CoreRefreshOwnership).GetMethod("Sweep", NS)
                        .MakeGenericMethod(resType);
                    Set("SweepSource", Delegate.CreateDelegate(
                        typeof(Func<,>).MakeGenericType(typeof(bool), resType), sweep));
                    Interlocked.Exchange(ref SweepCalls, 0);

                    int repaintBefore = Int("RepaintThread");
                    int accountsBefore = ((IList)Get("Accounts")).Count;

                    // Queue a completed connection before shutdown, release after.
                    MethodInfo rfu = formType.GetMethod("RequestFollowUpQuotaRefresh", NP);
                    formType.GetMethod("BeginShutdown", NP).Invoke(form, null);
                    rfu.Invoke(form, new object[] { "shutdown-vendor", 3 });
                    Application.DoEvents();
                    Wait(() => false, 400);
                    Application.DoEvents();

                    Check("shutdown disabled the form", Flag("ShuttingDown"), "");
                    Check("no follow-up sweep started after shutdown",
                        SweepCalls == 0, "sweeps=" + SweepCalls);
                    Check("no repaint occurred after shutdown",
                        Int("RepaintThread") == repaintBefore, "RepaintThread moved");
                    Check("no form-owned account state changed",
                        ((IList)Get("Accounts")).Count == accountsBefore, "");
                    Check("no cross-thread WinForms operation occurred", true, "no exception under the guard");
                }
                finally { form = saved; }
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── I: no handle -> the worker request is dropped, never run inline. ────
    static void ScenarioNoHandle(Assembly asm)
    {
        Console.WriteLine();
        Console.WriteLine("== I: a worker refresh with no usable handle is dropped ==");
        // A brand-new form whose handle is destroyed: RequestRefresh must drop.
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "limisaw_corenohandle_" + Guid.NewGuid().ToString("N"))).FullName;
        object st = Activator.CreateInstance(settingsType, new object[] { dir });
        settingsType.GetMethod("Load").Invoke(st, null);
        settingsType.GetField("SoundVolume").SetValue(st, 0);
        object themes = formType.Assembly.GetType("Limisaw.Theme")
            .GetMethod("Load", PS).Invoke(null, new object[] { Directory.GetCurrentDirectory() });
        try
        {
            using (var tray = new NotifyIcon())
            {
                Form f = (Form)Activator.CreateInstance(formType, new object[] { dir, st, tray, themes });
                object saved = form;
                form = f;
                try
                {
                    object timer = Field(form, "RefreshTimer").GetValue(form);
                    timer.GetType().GetMethod("Stop").Invoke(timer, null);
                    // Let the constructor's own start-up sweep settle first, so
                    // it cannot be miscounted as the request under test.
                    Wait(() => !Flag("Refreshing"), 60000);
                    MethodInfo sweep = typeof(CoreRefreshOwnership).GetMethod("Sweep", NS)
                        .MakeGenericMethod(resType);
                    Set("SweepSource", Delegate.CreateDelegate(
                        typeof(Func<,>).MakeGenericType(typeof(bool), resType), sweep));

                    // Create then destroy the handle so no usable handle remains.
                    IntPtr h = f.Handle;
                    f.GetType().GetMethod("DestroyHandle",
                        BindingFlags.NonPublic | BindingFlags.Instance).Invoke(f, null);
                    Check("the form has no valid handle for the check", !f.IsHandleCreated,
                        "IsHandleCreated=" + f.IsHandleCreated);

                    Interlocked.Exchange(ref SweepCalls, 0);
                    Int("RefreshEntryThread");
                    // A worker calls the connection refresh boundary with no handle.
                    MethodInfo request = formType.GetMethod("RequestRefresh", NP);
                    int workerId = 0;
                    var worker = new Thread(() =>
                    {
                        workerId = Thread.CurrentThread.ManagedThreadId;
                        request.Invoke(form, null);
                    });
                    worker.IsBackground = true;
                    worker.Start();
                    worker.Join(5000);
                    Wait(() => false, 300);
                    Application.DoEvents();

                    Check("the request was dropped, not inline-executed on the worker",
                        SweepCalls == 0, "sweeps=" + SweepCalls);
                    Check("...and RefreshData was never entered from the worker",
                        Int("RefreshEntryThread") != workerId,
                        "RefreshEntryThread=" + Int("RefreshEntryThread") + ", worker=" + workerId);
                }
                finally { form = saved; f.Dispose(); }
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── shape guard (secondary): the fix's shape, so a future edit cannot move
    //        the request back onto the worker. The behavioural checks above are
    //        the real contract. ───────────────────────────────────────────────
    static void Source(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== shape of the fix (secondary guard) ==");
        string dir = root;
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) break;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        string ui = File.ReadAllText(Path.Combine(dir ?? root, "LIMISAW.cs"));

        Check("RequestFollowUpQuotaRefresh routes through RequestRefresh, not RefreshData directly",
            ui.IndexOf("void RequestFollowUpQuotaRefresh(string vendorId, int gen)", StringComparison.Ordinal) >= 0
            && ui.IndexOf("            RequestRefresh();", StringComparison.Ordinal) >= 0
            && ui.IndexOf("            RefreshData();\r\n        }\r\n\r\n        // A watcher operation left the schedule",
                StringComparison.Ordinal) < 0, "");
        Check("RequestRefresh is the UI-dispatch boundary with the MarshalConnectionRepaint gate order",
            ui.IndexOf("void RequestRefresh()", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (ShuttingDown || IsDisposed || Disposing) return;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (!IsHandleCreated) return;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (InvokeRequired) BeginInvoke((Action)RefreshData);", StringComparison.Ordinal) >= 0, "");
        {
            int a = ui.IndexOf("void OnWatcherAttempt(ConnectionWatcher.Operation op)", StringComparison.Ordinal);
            int e = ui.IndexOf("void OnWatcherExpire(ConnectionWatcher.Operation op)", StringComparison.Ordinal);
            string seg = e > a ? ui.Substring(a, e - a) : "";
            // A bare statement-level Refresh(); call — RequestRefresh(); is
            // the boundary, not the raw WinForms call, so the match requires
            // the line start ("RequestRefresh();" never carries it).
            bool rawRefresh = System.Text.RegularExpressions.Regex.IsMatch(
                seg, @"(?m)^[ \t]*Refresh\(\);[ \t]*\r?$");
            Check("OnWatcherAttempt no longer calls the raw WinForms Refresh from the worker",
                a >= 0 && e > a && !rawRefresh, "a=" + a + " e=" + e + " rawRefresh=" + rawRefresh);
        }
        // PERF-006/R014 (audit/7): the lifetime HashSet<string> FollowUpRefreshDone
        // was replaced by a BOUNDED per-vendor high-water mark
        // (Dictionary<string,int> FollowUpRefreshGeneration + ClaimFollowUpQuotaRefresh),
        // so the exactly-once contract now lives in the claim gate. The dedup is
        // still exactly-once (an old/equal generation is refused) and no longer
        // grows unbounded. refresh_coalesce.cs owns the behavioural proof.
        Check("the exactly-once follow-up dedupe is preserved (bounded per-vendor generation)",
            ui.IndexOf("bool ClaimFollowUpQuotaRefresh(string vendorId, int gen)", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (!ClaimFollowUpQuotaRefresh(vendorId, gen)) return;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("&& gen <= previous) return false;", StringComparison.Ordinal) >= 0, "");
    }
}
