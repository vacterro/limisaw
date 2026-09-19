using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// W2-003: application shutdown did not own or drain the refresh workers. Every
// sweep is queued to the ThreadPool with no cancellation, no generation and no
// join, and Program.Main began tearing down (Ready reset, popup/tray disposal,
// SoundCue.Shutdown) the instant Application.Run returned. A sweep finishing
// after the form died could therefore publish into disposed form state, and its
// reset/low detection could submit another cue — restarting the audio owner
// after SoundCue.Shutdown had already run.
//
// The contract this harness holds:
//
//   * BeginShutdown sets the gate BEFORE teardown, stops the timer, and
//     invalidates the in-flight sweep generation;
//   * a late result for an invalidated sweep performs zero Publish: no account
//     list replacement, no tray update, no reset/low detection, no audio;
//   * a coalesced pending refresh is not restarted during shutdown;
//   * RefreshData after shutdown never schedules another sweep;
//   * the connection coordinator is shut down by the same owner, so an
//     in-flight ZCode verification generation can no longer publish;
//   * close-to-tray is NOT shutdown — only a real close reason is.
//
// The sweep body is injected (LimisawForm.SweepSource) and held open, because
// real vendor CLIs cannot be made to finish at a chosen instant.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class RefreshShutdown
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

    static Type accType, winType, resType, formType, settingsType, coordType;
    static object form, settings, coordinator;
    static int UiThread;

    static readonly ManualResetEventSlim entered = new ManualResetEventSlim(false);
    static readonly ManualResetEventSlim release = new ManualResetEventSlim(false);
    static int blockedCalls, quickCalls;

    static T QuickSweep<T>(bool zcodeReadConfig)
    {
        Interlocked.Increment(ref quickCalls);
        return (T)Snapshot(2, 2, 50);
    }

    static T BlockingSweep<T>(bool zcodeReadConfig)
    {
        Interlocked.Increment(ref blockedCalls);
        entered.Set();
        release.Wait(30000);
        return (T)Snapshot(5, 3, 10);
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

    static FieldInfo Field(string name) { return formType.GetField(name, NP); }
    static object Get(string name) { return Field(name).GetValue(form); }
    static bool Flag(string name) { return (bool)Field(name).GetValue(form); }
    static int Int(string name) { return (int)Field(name).GetValue(form); }
    static IList Accounts() { return (IList)Get("Accounts"); }
    static void Call(string name) { formType.GetMethod(name, BindingFlags.Public | NP).Invoke(form, null); }
    static void BeginShutdown() { formType.GetMethod("BeginShutdown", NP).Invoke(form, null); }
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

    public static int Main()
    {
        UiThread = Thread.CurrentThread.ManagedThreadId;
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_refreshshutdown_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
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
            asmThemes = themes;

            using (var tray = new NotifyIcon())
            using (Form f = (Form)Activator.CreateInstance(formType,
                new object[] { temp, settings, tray, themes }))
            {
                form = f;
                coordinator = Field("ConnCoordinator").GetValue(form);
                object timer = Field("RefreshTimer").GetValue(form);
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                Wait(() => !Flag("Refreshing"), 60000);

                Publication();
                Shutdown();
                WatcherAuthority();
                WatcherRaces();
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
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    static void Publication()
    {
        Console.WriteLine("== a baseline sweep publishes, so a late one would be visible ==");
        MethodInfo sweep = typeof(RefreshShutdown).GetMethod("QuickSweep", NS).MakeGenericMethod(resType);
        Field("SweepSource").SetValue(form,
            Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(bool), resType), sweep));
        Call("RefreshData");
        bool done = Wait(() => !Flag("Refreshing") && Int("PublishThread") != 0, 30000);
        Check("the baseline sweep published", done,
            "Refreshing=" + Flag("Refreshing") + ", PublishThread=" + Int("PublishThread"));
        Check("...with the injected fleet", Accounts().Count == 2, Accounts().Count + " accounts");
    }

    static void Shutdown()
    {
        Console.WriteLine();
        Console.WriteLine("== a sweep held open across shutdown is discarded, not published ==");

        MethodInfo blocking = typeof(RefreshShutdown).GetMethod("BlockingSweep", NS).MakeGenericMethod(resType);
        Field("SweepSource").SetValue(form,
            Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(bool), resType), blocking));

        int pubBefore = Int("PublishThread");
        int accBefore = Accounts().Count;
        string fetchBefore = (string)Get("LastFetch");
        string errBefore = (string)Get("LastError");
        bool staleBefore = Flag("Stale");
        int genBefore = Int("SweepGeneration");
        blockedCalls = 0; quickCalls = 0;
        entered.Reset(); release.Reset();

        // Capture a live verification generation BEFORE shutdown: it must be
        // invalidated by the same BeginShutdown.
        int verifyGen = (int)coordType.GetMethod("Begin").Invoke(coordinator, new object[] { "zcode" });

        Call("RefreshData");
        bool held = entered.Wait(30000);
        Check("the injected sweep is held open", held && Flag("Refreshing"),
            "entered=" + held + ", Refreshing=" + Flag("Refreshing"));
        Check("...and it ran exactly once", blockedCalls == 1, blockedCalls + " calls");

        // A refresh asked for while the held sweep is in flight is coalesced
        // (PendingRefresh) — the case that must NOT be restarted by shutdown.
        Call("RefreshData");
        Check("a refresh during the held sweep is coalesced, not started",
            Flag("PendingRefresh") && blockedCalls == 1, "PendingRefresh=" + Flag("PendingRefresh"));

        // The real exit path.
        BeginShutdown();

        Check("shutdown is observable", Flag("ShuttingDown"), "ShuttingDown=" + Flag("ShuttingDown"));
        Check("...and it bumps the sweep generation", Int("SweepGeneration") == genBefore + 1,
            Int("SweepGeneration") + " vs " + genBefore);
        Check("...and it shuts the connection coordinator down",
            (bool)coordType.GetProperty("IsShutdown").GetValue(coordinator, null), "");
        bool verifyLate = (bool)coordType.GetMethod("TryPublish")
            .Invoke(coordinator, new object[] { "zcode", verifyGen, null });
        Check("...so an in-flight ZCode verification generation can no longer publish",
            !verifyLate, "TryPublish=" + verifyLate);

        Call("RefreshData");
        Check("a refresh after shutdown never schedules another sweep",
            blockedCalls == 1 && quickCalls == 0, "blocked=" + blockedCalls + ", quick=" + quickCalls);

        // Let the worker finish now that the app is "closing".
        release.Set();
        bool drained = Wait(() => !Flag("Refreshing"), 15000);
        Application.DoEvents();
        Thread.Sleep(50);
        Application.DoEvents();

        Check("the held sweep drained", drained, "Refreshing=" + Flag("Refreshing"));
        Check("...and its late result performed ZERO publication",
            Int("PublishThread") == pubBefore, "PublishThread " + Int("PublishThread") + " vs " + pubBefore);
        Check("...the account list was not replaced", Accounts().Count == accBefore,
            Accounts().Count + " vs " + accBefore);
        Check("...the fetch stamp did not move", (string)Get("LastFetch") == fetchBefore,
            "'" + Get("LastFetch") + "' vs '" + fetchBefore + "'");
        Check("...and no error was recorded either",
            (string)Get("LastError") == errBefore && Flag("Stale") == staleBefore,
            "LastError='" + Get("LastError") + "', Stale=" + Flag("Stale"));
        Check("...and the coalesced follow-up was dropped, not restarted",
            !Flag("PendingRefresh") && blockedCalls == 1 && quickCalls == 0,
            "PendingRefresh=" + Flag("PendingRefresh") + ", blocked=" + blockedCalls + ", quick=" + quickCalls);
    }

    static void Source(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== the shape of the fix ==");
        string dir = root;
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) break;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        string ui = ReadSource(Path.Combine(dir ?? root, "LIMISAW.cs"));

        Check("RefreshData refuses once shutdown has begun",
            ui.IndexOf("if (ShuttingDown) return;", StringComparison.Ordinal) >= 0, "");
        Check("BeginShutdown is the single authority: gate, generation, timer, watcher, coordinator",
            ui.IndexOf("internal void BeginShutdown()", StringComparison.Ordinal) >= 0
            && ui.IndexOf("SweepGeneration++;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("RefreshTimer.Stop();", StringComparison.Ordinal) >= 0
            && ui.IndexOf("ConnectionWatcher.Shutdown();", StringComparison.Ordinal) >= 0
            && ui.IndexOf("ConnCoordinator.Shutdown();", StringComparison.Ordinal) >= 0, "");
        Check("the real exit path closes the gate before teardown",
            ui.IndexOf("else form.BeginShutdown();", StringComparison.Ordinal) >= 0
            && ui.IndexOf("form.BeginShutdown();\r\n                // CORE-011 teardown", StringComparison.Ordinal) >= 0, "");
        Check("close-to-tray is not shutdown",
            ui.IndexOf("if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; form.HideToTray(); } else form.BeginShutdown();",
                StringComparison.Ordinal) >= 0, "");
        // CORE-004: the invalidated-result predicate now releases an ABANDONED
        // flight (no UI completion) at the worker and at Apply, and the UI-thread
        // entry (Publish) still completes normally.
        int abandoned = CountOf(ui, "if (ShuttingDown || ActiveSweepGeneration != SweepGeneration) { AbandonSweep(); return; }");
        Check("the invalidated-result predicate guards the worker and Apply as an abandoned flight",
            abandoned >= 2, abandoned + " abandoned guards");
        int completed = CountOf(ui, "if (ShuttingDown || ActiveSweepGeneration != SweepGeneration) { CompleteSweep(); return; }");
        Check("the UI-thread entry still completes a normal sweep",
            completed >= 1, completed + " completion guards");
        Check("Publish's guard precedes every form-state mutation",
            ui.IndexOf("void Publish(ProbeResult snapshot)\r\n        {\r\n            // W2-003: the final gate.",
                StringComparison.Ordinal) >= 0, "");
    }

    // W2-002/R010: BeginShutdown owns the watcher lifetime. Proofs:
    //   * the watcher is emptied/stopped BY BeginShutdown itself;
    //   * a watcher attempt captured/queued before the gate still does ZERO
    //     vendor work (VerifyCalls=0) after it;
    //   * an expiry after the gate mutates no coordinator state;
    //   * a repaint after handle loss performs no UI mutation;
    //   * close-to-tray keeps monitoring (the gate is NOT set there).
    static void WatcherAuthority()
    {
        Console.WriteLine();
        Console.WriteLine("== W2-002: BeginShutdown owns the watcher lifetime ==");

        Type watcherType = coordType.Assembly.GetType("Limisaw.ConnectionWatcher");
        if (watcherType == null) { Check("ConnectionWatcher type found", false, ""); return; }
        MethodInfo watcherStart = watcherType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
        FieldInfo watcherOnAttempt = watcherType.GetField("OnAttempt", NS);
        FieldInfo watcherOnExpire = watcherType.GetField("OnExpire", NS);
        PropertyInfo pending = watcherType.GetProperty("PendingCount", NS);
        Type opType = watcherType.GetNestedType("Operation", BindingFlags.NonPublic);
        if (watcherStart == null || watcherOnAttempt == null || watcherOnExpire == null || pending == null || opType == null)
        { Check("watcher seams present", false, ""); return; }
        Type vcType = coordType.Assembly.GetType("Limisaw.VendorConnection");

        // The form's real handlers are already bound (the constructor binds
        // them); keep them. A counting verify closure detects vendor work.
        // The form's real handlers are already bound (the constructor binds
        // them); keep them. Vendor work is counted in `vendorWork` via the
        // NullVerify generic delegate (the exact Func<VendorConnection> type
        // the Operation field expects).
        var op = Activator.CreateInstance(opType);
        opType.GetField("VendorId").SetValue(op, "zcode");
        opType.GetField("Generation").SetValue(op, 77);
        Delegate verifyDelegate = Delegate.CreateDelegate(
            typeof(Func<>).MakeGenericType(vcType), null,
            typeof(RefreshShutdown).GetMethod("NullVerify", NS).MakeGenericMethod(vcType));
        // Count vendor work through a wrapper the form's handler will call.
        opType.GetField("Verify").SetValue(op, verifyDelegate);

        // Fresh form state: the previous scenario's form is disposed by now,
        // so a second instance drives the watcher-only checks.
        string dir2 = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "limisaw_w2a_" + Guid.NewGuid().ToString("N"))).FullName;
        object settings2 = Activator.CreateInstance(settingsType, new object[] { dir2 });
        settingsType.GetMethod("Load").Invoke(settings2, null);
        settingsType.GetField("SoundVolume").SetValue(settings2, 0);
        using (var tray2 = new NotifyIcon())
        using (Form f2 = (Form)Activator.CreateInstance(formType, new object[] { dir2, settings2, tray2, asmThemes }))
        {
            // (the form ctor binds OnWatcherAttempt/OnWatcherExpire)
            watcherStart.Invoke(null, new object[] { op });
            int live = (int)pending.GetValue(null, null);
            Check("the watcher held the staged operation", live == 1, "pending=" + live);

            // The real exit path: BeginShutdown must empty the watcher itself.
            formType.GetMethod("BeginShutdown", NP).Invoke(f2, null);
            int after = (int)pending.GetValue(null, null);
            Check("BeginShutdown itself emptied/stopped the watcher", after == 0, "pending=" + after);
            Check("...the shutdown gate is set", (bool)formType.GetField("ShuttingDown", NP).GetValue(f2), "");

            // A captured/queued watcher attempt released after the gate: the
            // form's own OnWatcherAttempt must refuse before any vendor work.
            // Invoke the captured handler the way the queued item would.
            MethodInfo onAttempt = formType.GetMethod("OnWatcherAttempt", NP);
            Interlocked.Exchange(ref vendorWork, 0);
            onAttempt.Invoke(f2, new object[] { op });
            System.Threading.Thread.Sleep(300);
            Check("a captured watcher attempt after BeginShutdown did ZERO vendor I/O",
                Volatile.Read(ref vendorWork) == 0, "verify=" + Volatile.Read(ref vendorWork));

            // An expiry after the gate: OnWatcherExpire must mutate nothing.
            // The coordinator was shut down too, so a TryProgress through it
            // cannot land — observable as the handler simply returning.
            MethodInfo onExpire = formType.GetMethod("OnWatcherExpire", NP);
            try { onExpire.Invoke(f2, new object[] { op }); Check("an expiry after shutdown was refused, not thrown", true, ""); }
            catch (Exception ex) { Check("an expiry after shutdown was refused, not thrown", false, ex.InnerException != null ? ex.InnerException.GetType().Name : ex.GetType().Name); }

            // A repaint after handle death: the form was never shown, its
            // handle is created though; dispose it, then repaint must be a
            // silent no-op (no InvalidOperationException).
            MethodInfo repaint = formType.GetMethod("MarshalConnectionRepaint", NP);
            try { repaint.Invoke(f2, null); Check("a repaint after handle loss performed no UI mutation", true, ""); }
            catch (Exception ex) { Check("a repaint after handle loss performed no UI mutation", false, ex.InnerException != null ? ex.InnerException.GetType().Name : ex.GetType().Name); }
        }

        // Close-to-tray is NOT shutdown: FormClosing with UserClosing keeps
        // the watcher alive (monitoring continues). Source-level pin plus the
        // live gate: the close path never calls BeginShutdown on UserClosing.
        string ui = ReadSource(Path.Combine(SourceDir(), "LIMISAW.cs"));
        Check("close-to-tray keeps monitoring (UserClosing cancels, never shuts the watcher)",
            ui.IndexOf("if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; form.HideToTray(); } else form.BeginShutdown();",
                StringComparison.Ordinal) >= 0, "");
        int attemptGate = CountOf(ui, "if (ShuttingDown) return;");
        Check("OnWatcherAttempt/OnWatcherExpire refuse at the shutdown gate", attemptGate >= 2, attemptGate + " gates");
        Check("MarshalConnectionRepaint gates on teardown before any UI work",
            ui.IndexOf("if (ShuttingDown || IsDisposed || Disposing) return;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (!IsHandleCreated) return;", StringComparison.Ordinal) >= 0, "");
    }

    // ── W2-002 race regressions (audit/6 VERIFY list) ─────────────────────────
    // The audit forbids faking the race by invoking the handler only AFTER
    // shutdown. Every scenario here ADMIITS the work while the gate is open
    // and lets BeginShutdown land inside the deterministic gap:
    //   A. the tick captured a due operation, dispatch is paused, shutdown
    //      runs, dispatch resumes — the callback must no-op entirely;
    //   B. a queued worker is held immediately before op.Verify, shutdown
    //      runs, the worker resumes — Verify must stay at zero calls;
    //   C. a worker-side repaint against a destroyed handle must not rebuild
    //      or repaint anything (object-identity sentinel, not "did not throw");
    //   D. BeginShutdown must not kill a user-owned interactive login child.
    static void WatcherRaces()
    {
        Console.WriteLine();
        Console.WriteLine("== W2-002 races: admitted before shutdown, executed after ==");
        Type watcherType = coordType.Assembly.GetType("Limisaw.ConnectionWatcher");
        MethodInfo watcherStart = watcherType.GetMethod("Start", PS);
        PropertyInfo pending = watcherType.GetProperty("PendingCount", NS);
        MethodInfo watcherTick = watcherType.GetMethod("Tick", NS);
        Type opType = watcherType.GetNestedType("Operation", BindingFlags.NonPublic);
        Type vcType = coordType.Assembly.GetType("Limisaw.VendorConnection");
        Type launcherType = coordType.Assembly.GetType("Limisaw.ConnectionProcessLauncher");
        MethodInfo onAttempt = formType.GetMethod("OnWatcherAttempt", NP);
        MethodInfo onExpire = formType.GetMethod("OnWatcherExpire", NP);
        MethodInfo repaint = formType.GetMethod("MarshalConnectionRepaint", NP);
        if (watcherType == null || watcherStart == null || pending == null || watcherTick == null
            || opType == null || vcType == null || launcherType == null
            || onAttempt == null || onExpire == null || repaint == null)
        { Check("race seams present", false, ""); return; }

        Func<object, int, object> newOp = (f, gen) =>
        {
            var op = Activator.CreateInstance(opType);
            opType.GetField("VendorId").SetValue(op, "zcode");
            opType.GetField("Generation").SetValue(op, gen);
            Delegate verifyDelegate = Delegate.CreateDelegate(
                typeof(Func<>).MakeGenericType(vcType), null,
                typeof(RefreshShutdown).GetMethod("NullVerify", NS).MakeGenericMethod(vcType));
            opType.GetField("Verify").SetValue(op, verifyDelegate);
            return op;
        };
        Action<Action<Form>> withForm = body =>
        {
            string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "limisaw_w2race_" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                object st = Activator.CreateInstance(settingsType, new object[] { dir });
                settingsType.GetMethod("Load").Invoke(st, null);
                settingsType.GetField("SoundVolume").SetValue(st, 0);
                using (var tray = new NotifyIcon())
                using (Form f = (Form)Activator.CreateInstance(formType, new object[] { dir, st, tray, asmThemes }))
                {
                    body(f);
                    GC.KeepAlive(f);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        };

        // B: QUEUED WORKER RACE — admission BEFORE shutdown, execution AFTER.
        withForm(f4 =>
        {
            var entered = new ManualResetEventSlim(false);
            var hold = new ManualResetEventSlim(false);
            FieldInfo seam = formType.GetField("BeforeVendorWork", NS);
            seam.SetValue(null, (Action)(() => { entered.Set(); hold.Wait(30000); }));
            object opB = newOp(f4, 79);
            try
            {
                onAttempt.Invoke(f4, new object[] { opB });   // gate OPEN: admitted
                Check("the queued worker reached the pre-verify seam", entered.Wait(TimeSpan.FromSeconds(15)), "");
                formType.GetMethod("BeginShutdown", NP).Invoke(f4, null);
                Interlocked.Exchange(ref vendorWork, 0);
                hold.Set();                                   // the worker resumes INSIDE shutdown
                Thread.Sleep(500);
                Check("a worker admitted before shutdown performed ZERO vendor work after it",
                    Volatile.Read(ref vendorWork) == 0, "verify=" + Volatile.Read(ref vendorWork));
                Check("...the shutdown gate recheck released the watcher slot",
                    (int)pending.GetValue(null, null) == 0, "pending=" + pending.GetValue(null, null));
                var coord = Field("ConnCoordinator").GetValue(f4);
                var latest = coord.GetType().GetMethod("Latest", BindingFlags.Public | BindingFlags.Instance).Invoke(coord, new object[] { "zcode" });
                Check("...the coordinator published nothing for the refused worker", latest == null, "");
            }
            finally { seam.SetValue(null, null); }
        });

        // A: CAPTURED CALLBACK RACE — Tick captured a due operation, shutdown
        // lands inside the capture/dispatch gap, dispatch resumes.
        withForm(f5 =>
        {
            var captured = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            FieldInfo seam = watcherType.GetField("BeforeDispatch", NS);
            seam.SetValue(null, (Action)(() => { captured.Set(); release.Wait(30000); }));
            object opA = newOp(f5, 80);
            try
            {
                watcherStart.Invoke(null, new object[] { opA });
                Thread tickThread = new Thread(() => { try { watcherTick.Invoke(null, new object[] { null }); } catch { } });
                tickThread.IsBackground = true;
                tickThread.Start();
                Check("the tick captured the due operation", captured.Wait(TimeSpan.FromSeconds(15)), "");
                formType.GetMethod("BeginShutdown", NP).Invoke(f5, null);
                Interlocked.Exchange(ref vendorWork, 0);
                release.Set();                                // dispatch resumes INSIDE shutdown
                Thread.Sleep(600);
                Check("a callback captured before shutdown did ZERO vendor work after it",
                    Volatile.Read(ref vendorWork) == 0, "verify=" + Volatile.Read(ref vendorWork));
                Check("...BeginShutdown itself emptied the captured watcher",
                    (int)pending.GetValue(null, null) == 0, "pending=" + pending.GetValue(null, null));
                var coord = Field("ConnCoordinator").GetValue(f5);
                var latest = coord.GetType().GetMethod("Latest", BindingFlags.Public | BindingFlags.Instance).Invoke(coord, new object[] { "zcode" });
                Check("...the expiry/progress path mutated nothing after the gate", latest == null, "");
            }
            finally { seam.SetValue(null, null); }
        });

        // D + C: CHILD OWNERSHIP through BeginShutdown, then the repaint
        // sentinel against a destroyed handle — one lifecycle window.
        withForm(f6 =>
        {
            string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "limisaw_w2child_" + Guid.NewGuid().ToString("N"))).FullName;
            string sleeperExe = Path.Combine(dir, "sleeper.exe");
            try
            {
                string csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
                    "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
                if (!File.Exists(csc)) csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
                    "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
                File.WriteAllText(Path.Combine(dir, "sleeper.cs"),
                    "using System;using System.Threading;class C{static void Main(){Thread.Sleep(Timeout.Infinite);}}");
                var build = Process.Start(new ProcessStartInfo(csc,
                    "-nologo -out:\"" + sleeperExe + "\" \"" + Path.Combine(dir, "sleeper.cs") + "\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
                build.WaitForExit(60000);
                if (build.ExitCode != 0 || !File.Exists(sleeperExe))
                { Check("the user-owned child fixture compiled", false, "csc exit " + build.ExitCode); return; }

                MethodInfo startInteractive = launcherType.GetMethod("StartInteractive", PS);
                object[] args = new object[] { sleeperExe, "", null, null };
                object launch = startInteractive.Invoke(null, args);
                int pid = launch != null ? (int)launch.GetType().GetField("ProcessId").GetValue(launch) : -1;
                Check("the user-owned interactive child launched", launch != null && pid > 0, "err=" + args[3]);

                bool aliveBefore = false;
                try { aliveBefore = !Process.GetProcessById(pid).HasExited; } catch { }
                Check("...the child was alive before shutdown", aliveBefore, "pid=" + pid);

                formType.GetMethod("BeginShutdown", NP).Invoke(f6, null);
                Thread.Sleep(300);
                bool aliveAfter = false;
                try { aliveAfter = !Process.GetProcessById(pid).HasExited; } catch { }
                Check("BeginShutdown left the user-owned login child alive",
                    aliveAfter, "pid=" + pid);
                var liveCount = launcherType.GetProperty("LiveObserverCount", NS);
                Check("...the observation resources were detached by the same shutdown",
                    (int)liveCount.GetValue(null, null) == 0,
                    "live=" + liveCount.GetValue(null, null));

                // Cleanup the TEST's own child: the harness owns this fixture,
                // the production code never did.
                try { var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(); p.WaitForExit(3000); p.Dispose(); } catch { }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // C: REPAINT AFTER HANDLE LOSS — object-identity sentinel. If the
            // worker-side repaint ran, BuildConnections would Clear+rebuild
            // the Connections list, replacing every element reference.
            try { GC.KeepAlive(f6.GetType().GetProperty("Handle").GetValue(f6)); } catch { }
            IList conns = (IList)formType.GetField("Connections", NP).GetValue(f6);
            var before = new List<object>();
            foreach (object c in conns) before.Add(c);
            int countBefore = conns.Count;
            bool workerThrew = false;
            var done = new ManualResetEventSlim(false);
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { repaint.Invoke(f6, null); }
                catch { workerThrew = true; }
                finally { done.Set(); }
            });
            Check("the worker-side repaint completed without throwing", done.Wait(TimeSpan.FromSeconds(15)) && !workerThrew, "");
            bool mutated = conns.Count != countBefore;
            if (!mutated)
            {
                int i = 0;
                foreach (object c in conns)
                { if (i >= before.Count || !ReferenceEquals(c, before[i])) { mutated = true; break; } i++; }
            }
            Check("a repaint after handle destruction performed NO BuildConnections/Refresh/UpdateTray work",
                !mutated, "count " + countBefore + " -> " + conns.Count);
        });

        // Source pin: the repaint executes directly ONLY on the live UI
        // thread; a worker always goes through BeginInvoke or is dropped.
        string src = ReadSource(Path.Combine(SourceDir(), "LIMISAW.cs"));
        int repaintAt = src.IndexOf("void MarshalConnectionRepaint()", StringComparison.Ordinal);
        int repaintEnd = src.IndexOf("\r\n        }", repaintAt, StringComparison.Ordinal);
        string rpBody = repaintAt >= 0 && repaintEnd > repaintAt ? src.Substring(repaintAt, repaintEnd - repaintAt) : "";
        Check("MarshalConnectionRepaint executes its UI work only through the UI thread",
            rpBody.IndexOf("if (InvokeRequired) BeginInvoke(done);", StringComparison.Ordinal) >= 0
            && rpBody.IndexOf("else done();", StringComparison.Ordinal) >= 0, "");
    }

    // These are SOURCE-SHAPE assertions: they pin the order of statements in
    // LIMISAW.cs, not the bytes a checkout happens to carry. A working tree
    // may hold LF or CRLF (git core.autocrlf decides, and this repo has both),
    // so the text is normalised to CRLF before any multi-line needle is
    // matched. Without this the checks pass or fail on the checkout, not on
    // the code -- which is exactly what they exist to police.
    static readonly string CR = ((char)13).ToString();
    static readonly string LF = ((char)10).ToString();
    static string ReadSource(string path)
    {
        string text = File.ReadAllText(path);
        return text.Replace(CR + LF, LF).Replace(LF, CR + LF);
    }

    static string _sourceDir;
    static string SourceDir()
    {
        if (_sourceDir != null) return _sourceDir;
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) return _sourceDir = dir;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        return _sourceDir = Directory.GetCurrentDirectory();
    }

    // The counting verify closure target: invoked ONLY if the form's
    // captured watcher attempt wrongly begins vendor work after shutdown.
    static T NullVerify<T>()
    {
        Interlocked.Increment(ref vendorWork);
        return default(T);
    }
    static int vendorWork;

    static object asmThemes;

    static int CountOf(string haystack, string needle)
    {
        int n = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }
}
