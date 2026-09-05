using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// W2-001: spending a banked reset is the only thing LIMISAW does that CHANGES
// state at a vendor, and it is worthless without the re-read that follows it —
// after the credit is consumed the windows on screen are wrong, and only a fresh
// sweep can say what the account really has now.
//
// The refresh gate used to be `if (Refreshing) return;`, so that mandatory
// re-read was silently thrown away whenever a scheduled or manual sweep happened
// to be in flight; the older sweep then published its PRE-reset numbers as the
// final truth. The gate now COALESCES instead of dropping, and this harness
// drives the real LimisawForm through the exact ordering that hid the defect:
//
//   * a refresh asked for during a sweep is remembered, not discarded;
//   * it does not start a second concurrent sweep;
//   * several requests inside one sweep collapse into ONE follow-up;
//   * the follow-up runs after the in-flight sweep has published, so the
//     POST-reset snapshot is what remains on screen;
//   * and there is no follow-up carousel once the queue is drained.
//
// The sweep body is injected (LimisawForm.SweepSource) because real vendor CLIs
// cannot be held open at a chosen instant. The reset side is the production path
// itself: LimisawForm.ResetCompleted, the completion RedeemCredit runs.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:refresh_coalesce.exe -r:System.dll -r:System.Drawing.dll ^
//     -r:System.Windows.Forms.dll tests\refresh_coalesce.cs
//   refresh_coalesce.exe          (exit 0 = all PASS)
public static class RefreshCoalesce
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Type accType, winType, resType, formType;

    static readonly ManualResetEvent Started = new ManualResetEvent(false);
    static readonly ManualResetEvent Release = new ManualResetEvent(false);
    static int Sweeps;
    static object PreReset, PostReset;

    const int PreRem = 40;
    const int PostRem = 55;   // a rise of 15 is below the reset-balloon threshold

    // The injected sweep body. Generic so a harness that knows ProbeResult only
    // by reflection can still hand the form a strongly typed Func<ProbeResult>.
    // The first sweep is the one held open; every later one is the follow-up.
    static T Sweep<T>(bool zcodeReadConfig)
    {
        int n = Interlocked.Increment(ref Sweeps);
        if (n == 1)
        {
            Started.Set();
            Release.WaitOne(20000);
            return (T)PreReset;
        }
        return (T)PostReset;
    }

    static object Snapshot(int rem)
    {
        object w = Activator.CreateInstance(winType);
        winType.GetField("Key").SetValue(w, "five_hour");
        winType.GetField("Base").SetValue(w, "five_hour");
        winType.GetField("Label").SetValue(w, "5h");
        winType.GetField("Available").SetValue(w, true);
        winType.GetField("Rem").SetValue(w, rem);
        winType.GetField("Reset").SetValue(w, "2126-09-05T18:00:00");
        winType.GetField("DurationMinutes").SetValue(w, 300);

        object a = Activator.CreateInstance(accType);
        accType.GetField("Provider").SetValue(a, "codex");
        accType.GetField("ProviderLabel").SetValue(a, "Codex");
        accType.GetField("Name").SetValue(a, "Codex");
        accType.GetField("SourceId").SetValue(a, "home-a");
        accType.GetField("ResetHome").SetValue(a, Path.Combine(Path.GetTempPath(), "home-a"));
        accType.GetField("Status").SetValue(a, "OK");
        accType.GetField("Ok").SetValue(a, true);
        ((IList)accType.GetField("Windows").GetValue(a)).Add(w);

        object res = Activator.CreateInstance(resType);
        ((IList)resType.GetField("Accounts").GetValue(res)).Add(a);
        return res;
    }

    static FieldInfo Field(string name)
    {
        return formType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
    }

    static bool Flag(object form, string name) { return (bool)Field(name).GetValue(form); }

    static void Call(object form, string name)
    {
        MethodInfo m = formType.GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        m.Invoke(form, null);
    }

    static void ResetCompleted(object form, string outcome)
    {
        MethodInfo m = formType.GetMethod("ResetCompleted", BindingFlags.NonPublic | BindingFlags.Instance);
        m.Invoke(form, new object[] { outcome });
    }

    // The remaining percent the form currently shows for the injected account.
    static int Shown(object form)
    {
        IList accounts = (IList)Field("Accounts").GetValue(form);
        foreach (object a in accounts)
        {
            if ((string)accType.GetField("SourceId").GetValue(a) != "home-a") continue;
            IList windows = (IList)accType.GetField("Windows").GetValue(a);
            if (windows.Count == 0) return -1;
            return (int)winType.GetField("Rem").GetValue(windows[0]);
        }
        return -1;
    }

    // Bounded wait that keeps pumping: the coalesced follow-up is marshalled with
    // BeginInvoke when the form already owns a window handle.
    static bool Wait(Func<bool> condition, int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Application.DoEvents();
            Thread.Sleep(15);
        }
        return condition();
    }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_coalesce_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // The form's constructor probes for real, so the environment is made
            // empty first: no vendor home, no CLI on PATH, no Zcode key. That
            // start-up sweep then finds nothing and finishes at once, and every
            // sweep this harness counts is one it injected itself.
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
            Type settingsType = asm.GetType("Limisaw.LimisawSettings");
            Type themeType = asm.GetType("Limisaw.Theme");

            object settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            object themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { root });

            PreReset = Snapshot(PreRem);
            PostReset = Snapshot(PostRem);

            using (var tray = new NotifyIcon())
            using (Form form = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            {
                // The periodic timer would inject a sweep of its own mid-scenario.
                object timer = Field("RefreshTimer").GetValue(form);
                timer.GetType().GetMethod("Stop").Invoke(timer, null);

                bool idle = Wait(() => !Flag(form, "Refreshing"), 60000);
                Check("the start-up sweep finishes against an empty environment", idle,
                    "Refreshing=" + Flag(form, "Refreshing"));

                // A clean slate: whatever that first sweep found is not this
                // harness's subject.
                Field("Accounts").SetValue(form, Activator.CreateInstance(typeof(List<>).MakeGenericType(accType)));
                Field("PrevAccounts").SetValue(form, Activator.CreateInstance(typeof(List<>).MakeGenericType(accType)));

                MethodInfo sweep = typeof(RefreshCoalesce)
                    .GetMethod("Sweep", BindingFlags.NonPublic | BindingFlags.Static)
                    .MakeGenericMethod(resType);
                Field("SweepSource").SetValue(form,
                    Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(bool), resType), sweep));
                Interlocked.Exchange(ref Sweeps, 0);

                // ── refresh A starts and is held open ──
                Call(form, "RefreshData");
                Check("an ordinary refresh starts a sweep", Started.WaitOne(10000),
                    "sweeps=" + Sweeps);
                Check("...and the gate reports it in flight", Flag(form, "Refreshing"), "");

                // ── reset B completes while A is still running ──
                ResetCompleted(form, "account/rateLimitResetCredit/consume -> ok");
                Check("a reset's mandatory re-read is RECORDED, not dropped",
                    Flag(form, "PendingRefresh"), "PendingRefresh=" + Flag(form, "PendingRefresh"));
                Check("...and it does not start a second concurrent sweep",
                    Sweeps == 1, "sweeps=" + Sweeps);
                Check("...and the reset lock is released even so",
                    !Flag(form, "Redeeming"), "Redeeming=" + Flag(form, "Redeeming"));

                // ── more requests inside the same sweep ──
                Call(form, "RefreshData");
                Call(form, "RefreshData");
                ResetCompleted(form, "second reset -> ok");
                Check("several requests during one sweep stay ONE pending request",
                    Flag(form, "PendingRefresh") && Sweeps == 1,
                    "PendingRefresh=" + Flag(form, "PendingRefresh") + ", sweeps=" + Sweeps);

                // ── A publishes its PRE-reset data, the follow-up must run after it ──
                Release.Set();
                bool settled = Wait(() => Sweeps >= 2 && !Flag(form, "Refreshing"), 30000);
                Check("the coalesced refresh runs after the held sweep published", settled,
                    "sweeps=" + Sweeps + ", Refreshing=" + Flag(form, "Refreshing"));
                Check("four requests inside one sweep collapse to ONE follow-up",
                    Sweeps == 2, "sweeps=" + Sweeps);
                Check("the POST-reset snapshot is what stays on screen",
                    Shown(form) == PostRem, "shown=" + Shown(form) + "%, pre-reset was " + PreRem + "%");
                Check("the queue is drained, not left armed",
                    !Flag(form, "PendingRefresh"), "PendingRefresh=" + Flag(form, "PendingRefresh"));

                Wait(() => false, 750);
                Check("and one follow-up does not become a refresh carousel",
                    Sweeps == 2 && !Flag(form, "Refreshing"),
                    "sweeps=" + Sweeps + " after 750ms idle");
            }

            // The gate and the reset completion are the two halves of this
            // contract; a future edit that restores the silent return, or that
            // stops re-reading after a reset, must fail here and not on screen.
            string ui = File.ReadAllText(Path.Combine(SourceRoot(root), "LIMISAW.cs"));
            Check("the gate coalesces instead of returning",
                ui.IndexOf("if (Refreshing) { PendingRefresh = true; return; }", StringComparison.Ordinal) >= 0
                && ui.IndexOf("if (Refreshing) return;", StringComparison.Ordinal) < 0, "");
            Check("the reset completion still asks for the re-read",
                ui.IndexOf("void ResetCompleted(string outcome)", StringComparison.Ordinal) >= 0
                && ui.IndexOf("Action done = () => ResetCompleted(outcome);", StringComparison.Ordinal) >= 0, "");
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

    // The repository root: build.ps1 runs the harnesses from there, but the exe
    // itself lives in tests\bin.
    static string SourceRoot(string start)
    {
        string dir = start;
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) return dir;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        return start;
    }
}
