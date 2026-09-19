using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// CORE-004 (audit/7.md): Apply and SetError fell back into worker-thread
// publication when BeginInvoke failed.
//
//   * Apply attempted BeginInvoke, and on a throw after the live-handle check
//     fell through to Publish(snapshot) ON THE SWEEP WORKER — the exact
//     worker-side publication PERF-004 removed (Accounts/PrevAccounts/
//     NotifiedLow/tray/connection projection mutated off the UI thread).
//   * SetError had the same shape: a failed BeginInvoke ran fail() on the
//     worker, mutating Stale/LastError and running CompleteSweep there.
//   * Worse, simply dropping the failed dispatch was also wrong: Refreshing
//     would stay true forever and every later RefreshData would only set
//     PendingRefresh, wedging the single-flight sweep.
//
// The contract this harness holds:
//
//   * a sweep result/error reaches a live UI owner through ONE dispatch
//     boundary (RunOnUiThread) modelled on RequestRefresh /
//     MarshalConnectionRepaint: teardown/no-handle/dropped-request -> DROP;
//   * a failed worker dispatch NEVER executes Publish or fail() on the worker;
//   * the undeliverable result is DISCARDED and only the minimal thread-safe
//     sweep-flight bookkeeping is released (AbandonSweep), so Refreshing
//     cannot wedge and an undeliverable PendingRefresh is not run from the
//     worker;
//   * normal success and normal error still marshal to the UI thread, and a
//     coalesced PendingRefresh still produces exactly one follow-up;
//   * a shutdown / no-handle race cannot strand the app in Refreshing=true.
//
// The dispatch race is forced deterministically by the production-null
// LimisawForm.VerifyDispatchHook seam (executed immediately before the real
// BeginInvoke), never by sleeps or probabilistic handle timing. Real form,
// real UI thread and message pump; Control.CheckForIllegalCrossThreadCalls is
// enabled so an illegal WinForms touch from the worker would throw.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class Core004DispatchFailure
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

    static Assembly asm;
    static Type accType, winType, resType, formType, settingsType, themeType;
    static Form form;
    static object themes;
    static string tempRoot;
    static int UiThread;

    // ── injected sweep bodies ────────────────────────────────────────────────
    static readonly ManualResetEvent SweepEntered = new ManualResetEvent(false);
    static readonly ManualResetEvent SweepRelease = new ManualResetEvent(false);
    static readonly ManualResetEvent ErrEntered = new ManualResetEvent(false);
    static readonly ManualResetEvent ErrRelease = new ManualResetEvent(false);
    static bool HoldSweep;
    static int SweepCalls, ErrCalls;
    static object Fleet;

    static T Sweep<T>(bool zcodeReadConfig)
    {
        Interlocked.Increment(ref SweepCalls);
        if (HoldSweep)
        {
            SweepEntered.Set();
            SweepRelease.WaitOne(20000);
        }
        return (T)Fleet;
    }

    static T ErrSweep<T>(bool zcodeReadConfig)
    {
        int n = Interlocked.Increment(ref ErrCalls);
        if (n == 1)
        {
            ErrEntered.Set();
            ErrRelease.WaitOne(20000);
            throw new InvalidOperationException("core004 vendor exploded mid-sweep");
        }
        return (T)Fleet;
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

    static object EmptyList()
    {
        return Activator.CreateInstance(typeof(List<>).MakeGenericType(accType));
    }

    static FieldInfo Field(string name) { return formType.GetField(name, NP); }
    static object Get(string name) { return Field(name).GetValue(form); }
    static bool Flag(string name) { return (bool)Field(name).GetValue(form); }
    static void Set(string name, object value) { Field(name).SetValue(form, value); }

    // Tolerant int read: the RED control source predates ErrorPublishThread, so
    // reflect defensively rather than crash the verifier on the control run.
    static int IntOr(string name)
    {
        FieldInfo f = formType.GetField(name, NP);
        if (f == null) return 0;
        object v = f.GetValue(form);
        return v == null ? 0 : (int)v;
    }

    static IList Accounts() { return (IList)Get("Accounts"); }
    static IDictionary Notified() { return (IDictionary)Get("NotifiedLow"); }
    static IList Conns() { return (IList)Get("Connections"); }

    static void Call(string name) { formType.GetMethod(name, BindingFlags.Public | NP).Invoke(form, null); }
    static void BeginShutdown() { formType.GetMethod("BeginShutdown", NP).Invoke(form, null); }

    static void SetSweepSource(string method)
    {
        MethodInfo m = typeof(Core004DispatchFailure).GetMethod(method, NS).MakeGenericMethod(resType);
        Field("SweepSource").SetValue(form,
            Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(bool), resType), m));
    }

    // The deterministic dispatch race: production default is null/inert; the
    // seam throws, which is exactly the handle-vanished-between-check-and-
    // BeginInvoke case the repair must survive without falling through.
    static void SetDispatchHook(bool forcedFailure)
    {
        FieldInfo hook = formType.GetField("VerifyDispatchHook", NS);
        if (hook == null) { Check("the production dispatch seam exists", false, "VerifyDispatchHook missing"); return; }
        hook.SetValue(null, forcedFailure
            ? (Action)(() => { throw new InvalidOperationException("core004 forced dispatch failure"); })
            : (Action)null);
    }

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

    // A fresh form, timer stopped, start-up sweep settled and form state reset
    // to a known empty baseline so every scenario measures only what it drives.
    static void Scenario(string title, Action body)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
        string dir = Directory.CreateDirectory(Path.Combine(tempRoot, "s" + Guid.NewGuid().ToString("N"))).FullName;
        object st = Activator.CreateInstance(settingsType, new object[] { dir });
        settingsType.GetMethod("Load").Invoke(st, null);
        settingsType.GetField("SoundVolume").SetValue(st, 0);
        settingsType.GetField("NotifyLow").SetValue(st, true);
        settingsType.GetField("LowPct").SetValue(st, 90);
        using (var tray = new NotifyIcon())
        {
            Form f = (Form)Activator.CreateInstance(formType, new object[] { dir, st, tray, themes });
            Form saved = form;
            form = f;
            try
            {
                object timer = Field("RefreshTimer").GetValue(form);
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                Wait(() => !Flag("Refreshing"), 60000);
                Set("Accounts", EmptyList());
                Set("PrevAccounts", EmptyList());
                Notified().Clear();
                if (Field("PublishThread") != null) Set("PublishThread", 0);
                if (Field("ErrorPublishThread") != null) Set("ErrorPublishThread", 0);
                Set("LastError", "");
                Set("LastFetch", "");
                Set("Stale", false);
                Set("PendingRefresh", false);
                if (Conns() != null) Conns().Clear();
                Interlocked.Exchange(ref SweepCalls, 0);
                Interlocked.Exchange(ref ErrCalls, 0);
                HoldSweep = false;
                SweepEntered.Reset(); SweepRelease.Reset();
                ErrEntered.Reset(); ErrRelease.Reset();
                SetDispatchHook(false);

                body();
            }
            finally
            {
                SetDispatchHook(false);
                form = saved;
                try { f.Dispose(); } catch { }
            }
        }
    }

    public static int Main()
    {
        UiThread = Thread.CurrentThread.ManagedThreadId;
        string root = Directory.GetCurrentDirectory();
        tempRoot = Path.Combine(Path.GetTempPath(), "limisaw_core004_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Exception fault = null;
        try
        {
            // The form's constructor probes for real; empty the environment so
            // the start-up sweep finds nothing and finishes at once. Every sweep
            // measured below is one this harness injected.
            Environment.SetEnvironmentVariable("USERPROFILE", tempRoot);
            Environment.SetEnvironmentVariable("HOME", tempRoot);
            Environment.SetEnvironmentVariable("APPDATA", tempRoot);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", tempRoot);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(tempRoot, "no-codex"));
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string key in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(key, "");

            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            accType = asm.GetType("Limisaw.AccountData");
            winType = asm.GetType("Limisaw.WindowData");
            resType = asm.GetType("Limisaw.ProbeResult");
            formType = asm.GetType("Limisaw.LimisawForm");
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            themeType = asm.GetType("Limisaw.Theme");
            Fleet = Snapshot(2, 2, 50);
            themes = themeType.GetMethod("Load", PS).Invoke(null, new object[] { root });

            // An illegal WinForms touch from the worker must throw, not be
            // silently allowed, so the OLD fallback cannot pass by accident.
            Control.CheckForIllegalCrossThreadCalls = true;

            ScenarioA();
            ScenarioB();
            ScenarioC();
            ScenarioD();
            ScenarioE();
            ScenarioF();
            ScenarioG();

            Source(root);
        }
        catch (Exception ex) { fault = ex; }
        finally
        {
            Control.CheckForIllegalCrossThreadCalls = false;
            try { Directory.Delete(tempRoot, true); } catch { }
        }

        if (fault != null)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(fault.ToString());
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    // ── A: Apply dispatch failure -> discard, never worker-side Publish. ─────
    static void ScenarioA()
    {
        Scenario("A: a failed Apply dispatch discards the result, never publishes on the worker", () =>
        {
            SetSweepSource("Sweep");
            SetDispatchHook(true);
            int accountsBefore = Accounts().Count;
            int notifiedBefore = Notified().Count;

            Call("RefreshData");
            bool settled = Wait(() => !Flag("Refreshing"), 20000);

            Check("the failed dispatch still released the sweep (not wedged)", settled,
                "Refreshing=" + Flag("Refreshing"));
            Check("PublishThread was never set -- Publish did not run", IntOr("PublishThread") == 0,
                "PublishThread=" + IntOr("PublishThread") + ", UI=" + UiThread + ", worker likely " + SweepCalls);
            Check("the account list was not replaced by the worker",
                Accounts().Count == accountsBefore, Accounts().Count + " vs " + accountsBefore);
            Check("PrevAccounts was not mutated",
                ((IList)Get("PrevAccounts")).Count == 0, ((IList)Get("PrevAccounts")).Count + " prev accounts");
            Check("NotifiedLow was not mutated off-thread",
                Notified().Count == notifiedBefore, Notified().Count + " entries");
            Check("no error was recorded in its place",
                Flag("Stale") == false && (string)Get("LastError") == "",
                "Stale=" + Flag("Stale") + ", LastError='" + Get("LastError") + "'");
            Check("PendingRefresh was not left armed", !Flag("PendingRefresh"), "");
        });
    }

    // ── B: SetError dispatch failure -> no worker Stale/LastError mutation. ─
    static void ScenarioB()
    {
        Scenario("B: a failed SetError dispatch never runs fail() on the worker", () =>
        {
            SetSweepSource("Sweep"); // returns a snapshot, but the hook kills the dispatch
            SetDispatchHook(true);

            // Drive SetError directly from a real worker thread, exactly as the
            // sweep's catch block does.
            Exception workerFault = null;
            MethodInfo setError = formType.GetMethod("SetError", NP);
            var worker = new Thread(() =>
            {
                try { setError.Invoke(form, new object[] { "core004 forced error" }); }
                catch (Exception ex) { workerFault = ex.InnerException != null ? ex.InnerException : ex; }
            });
            worker.IsBackground = true;
            worker.Start();
            worker.Join(10000);
            Wait(() => false, 200);

            Check("the worker-side SetError completed without throwing", workerFault == null,
                workerFault == null ? "" : workerFault.GetType().Name + ": " + workerFault.Message);
            Check("Stale was not mutated by the worker fallback", !Flag("Stale"), "Stale=" + Flag("Stale"));
            Check("LastError was not mutated by the worker fallback",
                (string)Get("LastError") == "", "LastError='" + Get("LastError") + "'");
            Check("fail() did not run off the UI thread (ErrorPublishThread unset)",
                IntOr("ErrorPublishThread") == 0, "ErrorPublishThread=" + IntOr("ErrorPublishThread"));
            // fail() is the only road to CompleteSweep here, so the absence of
            // the fail body also proves no worker AutoFitHeight/Refresh/UpdateTray.
            Check("no worker-side completion repaint occurred",
                IntOr("PublishThread") == 0 && Conns().Count == 0, "");
        });
    }

    // ── C: PendingRefresh during a failed dispatch. ─────────────────────────
    static void ScenarioC()
    {
        Scenario("C: an undeliverable PendingRefresh is discarded, not run from the worker", () =>
        {
            HoldSweep = true;
            SetSweepSource("Sweep");
            SweepEntered.Reset(); SweepRelease.Reset();

            Call("RefreshData");
            bool held = SweepEntered.WaitOne(10000);
            Check("a sweep is held in flight", held && Flag("Refreshing"), "Refreshing=" + Flag("Refreshing"));

            Call("RefreshData");
            Check("a second request is coalesced, not started in parallel",
                Flag("PendingRefresh") && SweepCalls == 1,
                "PendingRefresh=" + Flag("PendingRefresh") + ", sweeps=" + SweepCalls);

            SetDispatchHook(true);       // the publication the held sweep carries cannot land
            SweepRelease.Set();
            bool settled = Wait(() => !Flag("Refreshing"), 20000);
            Wait(() => false, 300);

            Check("the abandoned sweep released its flight", settled, "Refreshing=" + Flag("Refreshing"));
            Check("the worker did NOT call RefreshData (no second sweep)",
                SweepCalls == 1, "sweeps=" + SweepCalls);
            Check("the undeliverable PendingRefresh was discarded",
                !Flag("PendingRefresh"), "PendingRefresh=" + Flag("PendingRefresh"));
            Check("still no worker-side publication",
                IntOr("PublishThread") == 0 && Accounts().Count == 0,
                "PublishThread=" + IntOr("PublishThread") + ", accounts=" + Accounts().Count);

            // A later normal UI refresh must still be able to start a fresh sweep.
            SetDispatchHook(false);
            Call("RefreshData");
            bool fresh = Wait(() => !Flag("Refreshing") && IntOr("PublishThread") == UiThread
                && Accounts().Count == 2, 20000);
            Check("a later normal UI refresh starts a fresh, healthy sweep", fresh,
                "sweeps=" + SweepCalls + ", published on " + IntOr("PublishThread") + ", accounts=" + Accounts().Count);
            HoldSweep = false;
        });
    }

    // ── D: normal Apply still marshals and lands. ───────────────────────────
    static void ScenarioD()
    {
        Scenario("D: a normal Apply still marshals its snapshot to the UI thread", () =>
        {
            SetSweepSource("Sweep");
            SetDispatchHook(false);
            Call("RefreshData");
            bool landed = Wait(() => !Flag("Refreshing") && IntOr("PublishThread") == UiThread
                && Accounts().Count == 2, 20000);
            Check("the worker result was published ON the UI thread", landed,
                "PublishThread=" + IntOr("PublishThread") + " vs UI " + UiThread + ", accounts=" + Accounts().Count);
            Check("the snapshot landed intact", Accounts().Count == 2, Accounts().Count + " accounts");
            Check("the suppression dictionary filled from the same thread",
                Notified().Count == 4, Notified().Count + " NotifiedLow entries");
            Check("a successful publication reports no error",
                !Flag("Stale") && (string)Get("LastError") == "",
                "Stale=" + Flag("Stale") + ", LastError='" + Get("LastError") + "'");
        });
    }

    // ── E: normal error still marshals; coalescing preserved. ───────────────
    static void ScenarioE()
    {
        Scenario("E: a normal error marshals to the UI thread and PendingRefresh still coalesces", () =>
        {
            SetSweepSource("ErrSweep");
            SetDispatchHook(false);
            ErrEntered.Reset(); ErrRelease.Reset();

            Call("RefreshData");
            Check("the error sweep is held in flight", ErrEntered.WaitOne(10000) && Flag("Refreshing"),
                "Refreshing=" + Flag("Refreshing"));
            Call("RefreshData");
            Check("a request during the throwing sweep coalesces",
                Flag("PendingRefresh"), "PendingRefresh=" + Flag("PendingRefresh"));

            ErrRelease.Set();
            bool settled = Wait(() => !Flag("Refreshing") && IntOr("ErrorPublishThread") == UiThread
                && Accounts().Count == 2, 20000);
            Check("the error publication ran ON the UI thread", settled,
                "ErrorPublishThread=" + IntOr("ErrorPublishThread") + " vs UI " + UiThread);
            Check("exactly one coalesced follow-up ran",
                ErrCalls == 2, "errSweeps=" + ErrCalls);
            Check("the follow-up's fresh snapshot is what remains",
                Accounts().Count == 2 && IntOr("PublishThread") == UiThread,
                Accounts().Count + " accounts, published on " + IntOr("PublishThread"));
            Check("the pending flag was drained", !Flag("PendingRefresh"), "");
        });
    }

    // ── F: shutdown race. ───────────────────────────────────────────────────
    static void ScenarioF()
    {
        Scenario("F: shutdown racing the completion drops the result and does not wedge", () =>
        {
            HoldSweep = true;
            SetSweepSource("Sweep");
            SetDispatchHook(false);
            SweepEntered.Reset(); SweepRelease.Reset();

            Call("RefreshData");
            Check("a sweep is held in flight", SweepEntered.WaitOne(10000) && Flag("Refreshing"),
                "Refreshing=" + Flag("Refreshing"));

            int pubBefore = IntOr("PublishThread");
            int errBefore = IntOr("ErrorPublishThread");
            int repaintBefore = IntOr("RepaintThread");
            int accountsBefore = Accounts().Count;
            string fetchBefore = (string)Get("LastFetch");
            bool staleBefore = Flag("Stale");

            BeginShutdown();
            SweepRelease.Set();
            bool settled = Wait(() => !Flag("Refreshing"), 15000);
            Application.DoEvents();
            Wait(() => false, 200);

            Check("the abandoned sweep did not remain stuck", settled, "Refreshing=" + Flag("Refreshing"));
            Check("no Publish occurred after shutdown", IntOr("PublishThread") == pubBefore,
                IntOr("PublishThread") + " vs " + pubBefore);
            Check("no error UI mutation occurred after shutdown", IntOr("ErrorPublishThread") == errBefore,
                IntOr("ErrorPublishThread") + " vs " + errBefore);
            Check("no repaint/tray work occurred from the worker", IntOr("RepaintThread") == repaintBefore,
                IntOr("RepaintThread") + " vs " + repaintBefore);
            Check("the account list was not replaced", Accounts().Count == accountsBefore,
                Accounts().Count + " vs " + accountsBefore);
            Check("the fetch stamp did not move", (string)Get("LastFetch") == fetchBefore,
                "'" + Get("LastFetch") + "' vs '" + fetchBefore + "'");
            Check("no error was recorded on the way out",
                (string)Get("LastError") == "" && Flag("Stale") == staleBefore,
                "LastError='" + Get("LastError") + "', Stale=" + Flag("Stale"));
            Check("no follow-up sweep was started", SweepCalls == 1, "sweeps=" + SweepCalls);
            HoldSweep = false;
        });
    }

    // ── G: no usable handle. ────────────────────────────────────────────────
    static void ScenarioG()
    {
        Scenario("G: a completed result with no usable handle is discarded, not published inline", () =>
        {
            HoldSweep = true;
            SetSweepSource("Sweep");
            SetDispatchHook(false);
            SweepEntered.Reset(); SweepRelease.Reset();

            Call("RefreshData");
            Check("a sweep is held in flight", SweepEntered.WaitOne(10000) && Flag("Refreshing"),
                "Refreshing=" + Flag("Refreshing"));

            // Destroy the handle so the dispatch boundary finds no UI owner.
            formType.GetMethod("DestroyHandle", NP).Invoke(form, null);
            Check("the form has no usable handle for the dispatch", !form.IsHandleCreated,
                "IsHandleCreated=" + form.IsHandleCreated);

            SweepRelease.Set();
            bool settled = Wait(() => !Flag("Refreshing"), 15000);
            Application.DoEvents();
            Wait(() => false, 200);

            Check("the no-handle sweep released its flight", settled, "Refreshing=" + Flag("Refreshing"));
            Check("no inline worker publication occurred", IntOr("PublishThread") == 0,
                "PublishThread=" + IntOr("PublishThread"));
            Check("no worker error publication occurred", IntOr("ErrorPublishThread") == 0,
                "ErrorPublishThread=" + IntOr("ErrorPublishThread"));
            Check("no UI state mutated", Accounts().Count == 0 && Notified().Count == 0,
                Accounts().Count + " accounts, " + Notified().Count + " NotifiedLow");
            Check("Refreshing is not permanently true", !Flag("Refreshing"), "");
            HoldSweep = false;
        });
    }

    // ── shape guard (secondary): the fix's shape. ───────────────────────────
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

    static readonly string CR = ((char)13).ToString();
    static readonly string LF = ((char)10).ToString();
    static string ReadSource(string path)
    {
        string text = File.ReadAllText(path);
        return text.Replace(CR + LF, LF).Replace(LF, CR + LF);
    }

    static void Source(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== shape of the fix (secondary guard) ==");
        string ui = ReadSource(Path.Combine(SourceDir(), "LIMISAW.cs"));

        Check("Apply routes through the one UI-dispatch boundary and abandons on failure",
            ui.IndexOf("if (!RunOnUiThread(() => Publish(snapshot))) AbandonSweep();", StringComparison.Ordinal) >= 0, "");
        Check("SetError routes through the same boundary and abandons on failure",
            ui.IndexOf("if (!RunOnUiThread(fail)) AbandonSweep();", StringComparison.Ordinal) >= 0, "");
        Check("the old worker-thread Apply fallback is gone",
            ui.IndexOf("try { BeginInvoke((Action)(() => Publish(snapshot))); return; }", StringComparison.Ordinal) < 0, "");
        Check("the old worker-thread SetError fallback is gone",
            ui.IndexOf("if (IsHandleCreated && InvokeRequired) { try { BeginInvoke(fail); return; } catch { } }",
                StringComparison.Ordinal) < 0, "");
        Check("the dispatch boundary has the MarshalConnectionRepaint gate order",
            ui.IndexOf("bool RunOnUiThread(Action work)", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (ShuttingDown || IsDisposed || Disposing) return false;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (!IsHandleCreated) return false;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (InvokeRequired)", StringComparison.Ordinal) >= 0, "");
        Check("the abandoned-sweep completion does no UI work",
            ui.IndexOf("void AbandonSweep()", StringComparison.Ordinal) >= 0
            && ui.IndexOf("PendingRefresh = false;\r\n            }\r\n        }", StringComparison.Ordinal) >= 0, "");
        Check("the sweep-flight state is guarded by ONE shared primitive",
            ui.IndexOf("readonly object SweepGate = new object();", StringComparison.Ordinal) >= 0
            && ui.IndexOf("lock (SweepGate)", StringComparison.Ordinal) >= 0, "");
    }
}
