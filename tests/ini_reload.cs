using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// W2-003: LIMISAW.ini is a documented configuration path — README says so, and
// the app itself prints "Opened LIMISAW.ini — press Refresh after editing" — but
// startup used to be the file's ONLY reader. So every hand edit, including the
// deliberately manual `ZcodeReadConfig=1` that grants the Zcode credential
// permission, did nothing until the process was restarted. The instruction on
// screen was false.
//
// Refresh now re-reads the file and re-applies the runtime state the new values
// imply. This harness drives the real LimisawForm through the documented
// workflow: write the ini from outside the process, press Refresh, and assert
// the new value reached the thing it configures — the probe's credential
// permission, the refresh timer, the theme — with no restart.
//
// It also pins the three refusals that keep a live reload from being worse than
// no reload at all:
//
//   * a value that does not parse leaves the live one standing (the old
//     TryParse-then-clamp turned `RefreshSeconds=abc` into 60s polling);
//   * a save we KNOW failed is not overwritten by the stale bytes on disk;
//   * an unchanged file re-applies nothing, so the periodic sweep does not
//     repaint and re-arm alerts every few minutes for no reason.
//
// The sweep body is injected (LimisawForm.SweepSource) purely to OBSERVE the
// permission each sweep runs under; the reload path itself is production.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class IniReload
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Type accType, winType, resType, formType, settingsType;
    static object Snapshot;

    // What each injected sweep was allowed to do. The Zcode permission is the
    // audit's own example of an INI edit that must take effect without a restart.
    static readonly List<bool> Observed = new List<bool>();

    static T Sweep<T>(bool zcodeReadConfig)
    {
        lock (Observed) Observed.Add(zcodeReadConfig);
        return (T)Snapshot;
    }

    static bool LastSweepSaw
    {
        get { lock (Observed) return Observed.Count > 0 && Observed[Observed.Count - 1]; }
    }
    static int SweepCount { get { lock (Observed) return Observed.Count; } }

    static object MakeSnapshot()
    {
        object w = Activator.CreateInstance(winType);
        winType.GetField("Key").SetValue(w, "five_hour");
        winType.GetField("Base").SetValue(w, "five_hour");
        winType.GetField("Label").SetValue(w, "5h");
        winType.GetField("Available").SetValue(w, true);
        winType.GetField("Rem").SetValue(w, 70);
        winType.GetField("Reset").SetValue(w, "2126-09-05T18:00:00");
        winType.GetField("DurationMinutes").SetValue(w, 300);

        object a = Activator.CreateInstance(accType);
        accType.GetField("Provider").SetValue(a, "codex");
        accType.GetField("ProviderLabel").SetValue(a, "Codex");
        accType.GetField("Name").SetValue(a, "Codex");
        accType.GetField("SourceId").SetValue(a, "home-a");
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

    static object Get(object form, string name) { return Field(name).GetValue(form); }
    static bool Flag(object form, string name) { return (bool)Field(name).GetValue(form); }

    static void Call(object form, string name)
    {
        formType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(form, null);
    }

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

    // The documented user gesture: press Refresh, then let the sweep settle.
    static void PressRefresh(object form)
    {
        Call(form, "RefreshData");
        Wait(() => !Flag(form, "Refreshing"), 20000);
        Application.DoEvents();
    }

    // An external editor writing the whole file, which is what a text editor
    // does. Keys not named here are ABSENT, and an absent key means the
    // documented default.
    static void WriteIni(string path, Dictionary<string, string> keys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[limisaw]");
        foreach (KeyValuePair<string, string> kv in keys)
            sb.AppendLine(kv.Key + "=" + kv.Value);
        File.WriteAllText(path, sb.ToString());
    }

    static Dictionary<string, string> Baseline()
    {
        return new Dictionary<string, string>
        {
            { "RefreshSeconds", "300" },
            { "ZcodeReadConfig", "0" },
            { "Theme", "goldendefault" },
            { "LowPct", "20" },
            { "TrayMode", "single" },
            { "TrayFill", "4" },
        };
    }

    static string Slug(Assembly asm)
    {
        object theme = asm.GetType("Limisaw.Palette").GetField("T").GetValue(null);
        return (string)theme.GetType().GetField("Slug").GetValue(theme);
    }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_inireload_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // No vendor home, no CLI, no Zcode key: the constructor's own sweep
            // finds nothing and finishes at once.
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
            Snapshot = MakeSnapshot();

            string ini = Path.Combine(temp, "LIMISAW.ini");
            WriteIni(ini, Baseline());

            object settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            object themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { root });

            Unit(settings, ini);

            using (var tray = new NotifyIcon())
            using (Form form = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            {
                object timer = Get(form, "RefreshTimer");
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                Wait(() => !Flag(form, "Refreshing"), 60000);

                MethodInfo sweep = typeof(IniReload)
                    .GetMethod("Sweep", BindingFlags.NonPublic | BindingFlags.Static)
                    .MakeGenericMethod(resType);
                Field("SweepSource").SetValue(form,
                    Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(bool), resType), sweep));

                // Back to the documented starting point, in case the constructor's
                // own reload moved anything.
                WriteIni(ini, Baseline());
                settingsType.GetMethod("Load").Invoke(settings, null);
                lock (Observed) Observed.Clear();

                Workflow(asm, form, settings, ini, timer);
            }
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

    // The reader's own rules, without a form in the way.
    static void Unit(object settings, string ini)
    {
        Console.WriteLine("== the reader: reload reports change, and refuses to poison itself ==");
        MethodInfo reload = settingsType.GetMethod("Reload");
        FieldInfo refreshSeconds = settingsType.GetField("RefreshSeconds");
        FieldInfo lowPct = settingsType.GetField("LowPct");
        FieldInfo saveFailed = settingsType.GetField("LastSaveFailed");

        var keys = Baseline();
        keys["RefreshSeconds"] = "900";
        WriteIni(ini, keys);
        Check("a changed key makes Reload report a change",
            (bool)reload.Invoke(settings, null), "");
        Check("...and the new value is live",
            (int)refreshSeconds.GetValue(settings) == 900,
            refreshSeconds.GetValue(settings).ToString());
        Check("an unchanged file reports NO change, so nothing is re-applied",
            !(bool)reload.Invoke(settings, null), "");

        // The old reader did `int.TryParse(...)` straight into the field, so a
        // failed parse wrote 0 and the clamp turned it into the 60s floor.
        keys["RefreshSeconds"] = "abc";
        WriteIni(ini, keys);
        Check("a value that does not parse reports no change",
            !(bool)reload.Invoke(settings, null), "");
        Check("...and leaves the live value standing, NOT clamped to the floor",
            (int)refreshSeconds.GetValue(settings) == 900,
            refreshSeconds.GetValue(settings).ToString());

        keys["RefreshSeconds"] = "5";
        WriteIni(ini, keys);
        reload.Invoke(settings, null);
        Check("a parsable out-of-range value is clamped, not accepted",
            (int)refreshSeconds.GetValue(settings) == 60,
            refreshSeconds.GetValue(settings).ToString());

        keys["RefreshSeconds"] = "900";
        keys["TrayMode"] = "nonsense";
        keys["TrayFill"] = "999";
        WriteIni(ini, keys);
        reload.Invoke(settings, null);
        Check("a nonsense enum falls back to its default",
            (string)settingsType.GetField("TrayMode").GetValue(settings) == "single",
            (string)settingsType.GetField("TrayMode").GetValue(settings));
        Check("...and a nonsense step count too",
            (int)settingsType.GetField("TrayFill").GetValue(settings) == 4,
            settingsType.GetField("TrayFill").GetValue(settings).ToString());

        // An ABSENT key is not a malformed one: the file is the truth, so
        // deleting a line restores the documented default.
        keys = Baseline();
        keys["RefreshSeconds"] = "900";
        keys.Remove("LowPct");
        WriteIni(ini, keys);
        reload.Invoke(settings, null);
        Check("a deleted key returns to the documented default",
            (int)lowPct.GetValue(settings) == 20, lowPct.GetValue(settings).ToString());

        // A save that failed means the file does NOT hold the user's choice.
        // Reading it back would silently undo what they just asked for.
        keys["RefreshSeconds"] = "1200";
        WriteIni(ini, keys);
        saveFailed.SetValue(settings, true);
        Check("after a FAILED save the stale file is not read back",
            !(bool)reload.Invoke(settings, null), "");
        Check("...and the user's live choice survives",
            (int)refreshSeconds.GetValue(settings) == 900,
            refreshSeconds.GetValue(settings).ToString());
        saveFailed.SetValue(settings, false);
        Check("once saving works again the file is authoritative",
            (bool)reload.Invoke(settings, null)
            && (int)refreshSeconds.GetValue(settings) == 1200,
            refreshSeconds.GetValue(settings).ToString());

        // The file may be gone entirely; that is not a reason to throw inside a
        // refresh.
        File.Delete(ini);
        Check("a missing ini is not a reload and not a crash",
            !(bool)reload.Invoke(settings, null), "");
    }

    // The documented workflow, end to end, through the real form.
    static void Workflow(Assembly asm, object form, object settings, string ini, object timer)
    {
        Console.WriteLine();
        Console.WriteLine("== the workflow: edit the ini, press Refresh, no restart ==");
        FieldInfo zcode = settingsType.GetField("ZcodeReadConfig");
        FieldInfo note = Field("Note");
        PropertyInfo interval = timer.GetType().GetProperty("Interval");

        PressRefresh(form);
        Check("the baseline sweep runs without the Zcode credential permission",
            SweepCount == 1 && !LastSweepSaw, "sweeps=" + SweepCount);

        // The audit's own example: the one permission a user must grant by hand.
        var keys = Baseline();
        keys["ZcodeReadConfig"] = "1";
        WriteIni(ini, keys);
        Check("...and the running app has not been told yet",
            !(bool)zcode.GetValue(settings), "");
        PressRefresh(form);
        Check("pressing Refresh after editing reloads ZcodeReadConfig",
            (bool)zcode.GetValue(settings), "");
        Check("...and the very next sweep probes WITH that permission, no restart",
            SweepCount == 2 && LastSweepSaw, "sweeps=" + SweepCount);
        Check("...and the reload is reported to the user",
            ((string)note.GetValue(form)).IndexOf("Reloaded", StringComparison.Ordinal) >= 0,
            (string)note.GetValue(form));

        // A runtime-dependent value: the timer must be re-armed, not merely
        // stored.
        keys["RefreshSeconds"] = "900";
        WriteIni(ini, keys);
        PressRefresh(form);
        Check("an externally edited interval re-arms the refresh timer",
            (int)interval.GetValue(timer, null) == 900000,
            interval.GetValue(timer, null).ToString());

        // And a visual one.
        keys["Theme"] = "nord";
        WriteIni(ini, keys);
        PressRefresh(form);
        Check("an externally chosen theme is applied to the live palette",
            Slug(asm) == "nord", Slug(asm));

        // Exactly once per actual change: an unchanged file must not re-apply
        // anything, or every periodic sweep would repaint and re-arm alerts.
        note.SetValue(form, "sentinel");
        PressRefresh(form);
        PressRefresh(form);
        Check("an unchanged ini re-applies nothing on later refreshes",
            (string)note.GetValue(form) == "sentinel", (string)note.GetValue(form));
        Check("...while the sweeps themselves still happen",
            SweepCount == 6, "sweeps=" + SweepCount);

        // Moving the low threshold from outside must re-arm alerts the same way
        // the slider does, or a window already above the new threshold stays
        // silent forever.
        var notified = (IDictionary)Get(form, "NotifiedLow");
        notified.Clear();
        notified["codex/home-a/five_hour"] = "2126-09-05T18:00:00";
        keys["LowPct"] = "45";
        WriteIni(ini, keys);
        PressRefresh(form);
        Check("an externally moved low threshold is live",
            (int)settingsType.GetField("LowPct").GetValue(settings) == 45,
            settingsType.GetField("LowPct").GetValue(settings).ToString());
        Check("...and alerts fired for the OLD threshold are re-armed",
            notified.Count == 0, notified.Count + " remembered");

        // A locked file is a refresh that still has to work. GetPrivateProfileString
        // cannot say "unreadable" — it answers with the DEFAULT for every key —
        // so a reload that trusted it would silently reset every setting.
        keys["RefreshSeconds"] = "600";
        keys["LowPct"] = "45";
        keys["ZcodeReadConfig"] = "1";
        keys["Theme"] = "nord";
        WriteIni(ini, keys);
        using (var hold = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            PressRefresh(form);
            Check("a locked ini does not lose the sweep",
                SweepCount == 8, "sweeps=" + SweepCount);
            Check("...and an unreadable file does not reset the live settings to defaults",
                (int)settingsType.GetField("LowPct").GetValue(settings) == 45
                && (bool)zcode.GetValue(settings)
                && (int)interval.GetValue(timer, null) == 900000,
                "LowPct=" + settingsType.GetField("LowPct").GetValue(settings)
                + ", zcode=" + zcode.GetValue(settings)
                + ", interval=" + interval.GetValue(timer, null));
        }
        PressRefresh(form);
        Check("once the file is readable again the edit lands",
            (int)interval.GetValue(timer, null) == 600000,
            interval.GetValue(timer, null).ToString());

        // The contract itself, so a future edit cannot quietly make the
        // on-screen instruction false again.
        string ui = File.ReadAllText(Path.Combine(SourceRoot(Directory.GetCurrentDirectory()), "LIMISAW.cs"));
        Check("Refresh is still where the ini is re-read",
            ui.IndexOf("ReloadSettings();", StringComparison.Ordinal) >= 0
            && ui.IndexOf("public bool Reload()", StringComparison.Ordinal) >= 0, "");
        Check("...and the promise on screen is still the one being kept",
            ui.IndexOf("press Refresh after editing", StringComparison.Ordinal) >= 0, "");
    }

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
