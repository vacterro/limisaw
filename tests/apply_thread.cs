using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// PERF-004: the vendor sweep runs on a ThreadPool thread, but everything it
// PUBLISHES is UI-owned state — the account list every paint walks, the
// NotifiedLow suppression dictionary a slider release clears, the tray icon, the
// balloon. Apply() used to mutate all of it from the worker, so a refresh
// finishing while the user dragged the low-quota slider was two threads writing
// one Dictionary: RearmLowAlerts enumerating it throws InvalidOperationException
// ("Collection was modified"), and the sweep then ends up reported as an ERROR
// the user cannot explain. The same worker also drove SoundCue's process-wide
// player and scaled-copy cache while the Settings Play button could drive it
// from the UI thread.
//
// The contract this harness holds:
//
//   * the sweep body really does run on a worker thread (or the rest proves
//     nothing);
//   * every publication runs on the UI thread — LimisawForm.PublishThread is
//     the thread the last publication actually used;
//   * a publication racing a UI-thread NotifiedLow gesture cannot tear the
//     dictionary: no publication reports a collection-modified failure, and the
//     account list still lands;
//   * two cues asked for at once (the preview button and an alert raised by a
//     finishing sweep) never overlap inside SoundCue.
//
// The sweep body is injected (LimisawForm.SweepSource) and the player is
// replaced (SoundCue.PlayBackend) because a shared SoundPlayer cannot be asked
// whether two cues overlapped, and real vendor CLIs cannot be made to finish at
// a chosen instant.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class ApplyThread
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

    static Type accType, winType, resType, formType, settingsType;
    static object form, settings;
    static int UiThread;

    static int SweepThread;

    static T Sweep<T>(bool zcodeReadConfig)
    {
        SweepThread = Thread.CurrentThread.ManagedThreadId;
        return (T)Snapshot(3, 4, 5);
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

    // A fleet big enough that a publication spends real time inside
    // RearmLowAlerts and DetectLow — the two loops that walk NotifiedLow.
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
    static IDictionary Notified() { return (IDictionary)Get("NotifiedLow"); }
    static int PublishThread() { return (int)Get("PublishThread"); }

    static void Call(string name)
    {
        formType.GetMethod(name, BindingFlags.Public | NP).Invoke(form, null);
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

    public static int Main()
    {
        UiThread = Thread.CurrentThread.ManagedThreadId;
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_applythread_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // The constructor probes for real, so the environment is emptied
            // first: no vendor home, no CLI on PATH, no Zcode key. Every sweep
            // this harness measures is one it injected itself.
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
            Type themeType = asm.GetType("Limisaw.Theme");

            settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            // Every window in the fleet counts as low, so DetectLow walks the
            // whole dictionary; volume 0 keeps the alert silent.
            settingsType.GetField("NotifyLow").SetValue(settings, true);
            settingsType.GetField("LowPct").SetValue(settings, 90);
            settingsType.GetField("SoundVolume").SetValue(settings, 0);
            object themes = themeType.GetMethod("Load", PS).Invoke(null, new object[] { root });

            using (var tray = new NotifyIcon())
            using (Form f = (Form)Activator.CreateInstance(formType,
                new object[] { temp, settings, tray, themes }))
            {
                form = f;
                object timer = Field("RefreshTimer").GetValue(form);
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                Check("the form owns a window handle before any sweep can publish",
                    f.IsHandleCreated, "IsHandleCreated=" + f.IsHandleCreated);
                Wait(() => !Flag("Refreshing"), 60000);

                Publication();
                Race();
            }

            Cues(asm, temp);
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

    // ── where a sweep's result is published ─────────────────────────────────
    static void Publication()
    {
        Console.WriteLine("== the sweep reads on a worker, but publishes on the UI thread ==");
        MethodInfo sweep = typeof(ApplyThread).GetMethod("Sweep", NS).MakeGenericMethod(resType);
        Field("SweepSource").SetValue(form,
            Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(bool), resType), sweep));
        SweepThread = UiThread;

        Call("RefreshData");
        bool done = Wait(() => !Flag("Refreshing") && PublishThread() != 0, 30000);
        Check("the injected sweep ran and settled", done,
            "Refreshing=" + Flag("Refreshing") + ", PublishThread=" + PublishThread());
        Check("the vendor read really happened off the UI thread",
            SweepThread != UiThread, "sweep thread " + SweepThread + " vs UI " + UiThread);
        Check("...and its result was published ON the UI thread",
            PublishThread() == UiThread, "published on " + PublishThread() + ", UI is " + UiThread);
        Check("...with the accounts actually landing",
            ((IList)Get("Accounts")).Count == 3, ((IList)Get("Accounts")).Count + " accounts");
        Check("...and the suppression dictionary filled from the same thread",
            Notified().Count == 12, Notified().Count + " NotifiedLow entries");
        Check("a publication that succeeded reports no error",
            (string)Get("LastError") == "" && !Flag("Stale"),
            "LastError=" + Get("LastError") + ", Stale=" + Flag("Stale"));
    }

    // ── a finishing sweep against a live slider gesture ─────────────────────
    // The defect's exact shape: RearmLowAlerts enumerates NotifiedLow while the
    // UI thread clears and refills it (what EndVolDrag does on every low-slider
    // release). Publishing on the UI thread makes the interleaving impossible
    // rather than unlikely.
    static void Race()
    {
        Console.WriteLine();
        Console.WriteLine("== a publication cannot tear the dictionary a gesture is clearing ==");
        MethodInfo apply = formType.GetMethod("Apply", NP);
        Field("LastError").SetValue(form, "");
        Field("Stale").SetValue(form, false);

        const int Rounds = 40;
        int published = 0, offThread = 0;
        Exception workerFault = null;
        var finished = new ManualResetEvent(false);

        // The worker: what the sweep's ThreadPool thread does when it finishes.
        var worker = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < Rounds; i++)
                {
                    apply.Invoke(form, new object[] { Snapshot(3, 4, 5) });
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex) { workerFault = ex; }
            finally { finished.Set(); }
        });
        worker.IsBackground = true;
        worker.Start();

        // The UI thread: the low-slider gesture, over and over, while pumping
        // the queued publications through.
        var sw = Stopwatch.StartNew();
        int last = 0;
        while (sw.ElapsedMilliseconds < 20000)
        {
            IDictionary fired = Notified();
            fired.Clear();
            for (int k = 0; k < 40; k++) fired["gesture/" + k] = "2026-09-05T10:00:00";
            Application.DoEvents();
            int seen = PublishThread();
            if (seen != 0 && seen != UiThread) offThread++;
            if (seen != last) { published++; last = seen; }
            if (finished.WaitOne(0) && published > 0) break;
        }
        finished.WaitOne(5000);
        Wait(() => false, 200);

        Check("the worker's publications did not throw at it",
            workerFault == null, workerFault == null ? "" : workerFault.GetType().Name);
        Check("no publication ran on a thread other than the UI thread",
            offThread == 0, offThread + " off-thread publications sampled");
        Check("the last publication is still the UI thread's",
            PublishThread() == UiThread, "published on " + PublishThread());
        Check("no publication reported a torn collection",
            ((string)Get("LastError")).IndexOf("Collection was modified", StringComparison.Ordinal) < 0
            && (string)Get("LastError") == "",
            "LastError=" + Get("LastError"));
        Check("...and the fleet is intact afterwards",
            ((IList)Get("Accounts")).Count == 3 && !Flag("Stale"),
            ((IList)Get("Accounts")).Count + " accounts, Stale=" + Flag("Stale"));
    }

    // ── two cues at once ────────────────────────────────────────────────────
    static int live, peak, plays;

    static string Backend(string path)
    {
        int now = Interlocked.Increment(ref live);
        lock (typeof(ApplyThread)) { if (now > peak) peak = now; plays++; }
        Thread.Sleep(20);
        Interlocked.Decrement(ref live);
        return null;
    }

    static void Cues(Assembly asm, string temp)
    {
        Console.WriteLine();
        Console.WriteLine("== the preview button and an alert never overlap in the player ==");
        Type cue = asm.GetType("Limisaw.SoundCue");
        FieldInfo backend = cue.GetField("PlayBackend", NS);
        Check("the player has a seam a test can observe", backend != null, "");
        if (backend == null) return;

        string wav = Path.Combine(temp, "cue.wav");
        File.WriteAllBytes(wav, Tone(8000));
        MethodInfo play = cue.GetMethod("Play", PS);
        backend.SetValue(null, Delegate.CreateDelegate(backend.FieldType,
            typeof(ApplyThread).GetMethod("Backend", NS)));
        try
        {
            var threads = new List<Thread>();
            var faults = new List<string>();
            for (int t = 0; t < 4; t++)
            {
                var th = new Thread(() =>
                {
                    for (int i = 0; i < 5; i++)
                    {
                        object why = play.Invoke(null, new object[] { temp, "", wav, 50 });
                        if (why != null) lock (faults) faults.Add((string)why);
                    }
                });
                th.IsBackground = true;
                threads.Add(th);
            }
            foreach (Thread th in threads) th.Start();
            foreach (Thread th in threads) th.Join(30000);

            Check("every cue was actually handed to the player",
                plays == 20, plays + " of 20 cues played");
            Check("...one at a time, whichever thread asked",
                peak == 1, "peak concurrency " + peak);
            Check("...and none of them reported a failure",
                faults.Count == 0, faults.Count == 0 ? "" : faults[0]);
        }
        finally { backend.SetValue(null, null); }
    }

    // A tiny 16-bit mono WAV, so the volume-scaling path has real samples to
    // quantise and cache — the shared state the lock protects.
    static byte[] Tone(int amp)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        short[] s = new short[64];
        for (int i = 0; i < s.Length; i++) s[i] = (short)amp;
        byte[] data = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, data, 0, data.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + data.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(8000); w.Write(16000); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    // The shape of the fix, so a future edit cannot quietly move the publication
    // back onto the sweep's own thread.
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
        string ui = File.ReadAllText(Path.Combine(dir ?? root, "LIMISAW.cs"));

        Check("Apply only marshals; Publish owns the mutation",
            ui.IndexOf("try { BeginInvoke((Action)(() => Publish(snapshot))); return; }", StringComparison.Ordinal) >= 0
            && ui.IndexOf("void Publish(ProbeResult snapshot)", StringComparison.Ordinal) >= 0, "");
        Check("the old worker-thread publication is gone",
            ui.IndexOf("try { BeginInvoke((Action)(() => { FitWindow(); Refresh(); UpdateTray(); if (runAgain) RefreshData(); })); }",
                StringComparison.Ordinal) < 0, "");
        Check("the handle exists before the first sweep is started",
            ui.IndexOf("try { IntPtr unused = Handle; } catch { }", StringComparison.Ordinal) >= 0
            && ui.IndexOf("try { IntPtr unused = Handle; } catch { }", StringComparison.Ordinal)
               < ui.IndexOf("RefreshTimer.Start(); RefreshData();", StringComparison.Ordinal), "");
        Check("the cue path serialises the shared player and its cache",
            ui.IndexOf("lock (Gate)", StringComparison.Ordinal) >= 0
            && ui.IndexOf("static readonly object Gate = new object();", StringComparison.Ordinal) >= 0, "");
    }
}
