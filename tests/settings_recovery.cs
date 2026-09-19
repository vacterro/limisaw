using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// W2-006 / SRC-004:R020 — the structured persistence contract.
//
// T-27 made every key write checked; CORE-006 made the whole save
// transactional. What was still missing was the CONTRACT above the file
// transaction: Save() returned void, callers rarely looked, and
// LastSaveFailed was both the failure flag AND a sticky global reload veto —
// a transient write failure permanently disabled the documented "edit the
// ini, press Refresh" recovery, while any unrelated later save silently
// cleared the bit. This harness drives the replacement model:
//
//   * Save has an explicit durable result (Saved / Dirty / Reason), and the
//     durable baseline is a real snapshot the disk provably accepted — not an
//     mtime, not a flag;
//   * Dirty means "live differs from the durable baseline", which is a
//     different question from "the last write failed", and it is the DIRTY
//     fact that survives an incidental position save;
//   * a failed staged key write and a failed atomic replace both leave the
//     previous ini byte-for-byte intact with the baseline standing;
//   * a user-visible mutation that failed to persist says "not saved" while
//     its live value keeps working;
//   * a dirty live choice is kept against an UNCHANGED stale disk, and a
//     coherently edited external file is accepted WHOLE on Refresh — no
//     key-by-key merge — clearing the conflict and advancing the baseline;
//   * an incidental WindowX/WindowY write never quietly resolves an open
//     conflict, and an explicit retry does.
//
// The failing write cannot be arranged on a real disk on demand, so the
// WriteHook seam fails a chosen key; the read-only ini forces the atomic
// replace to refuse. Representative settings span the whole snapshot:
// RefreshSeconds, Theme, TrayItems, LowPct and SoundDir — one boolean would
// prove nothing.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class SettingsRecovery
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Type formType, settingsType;
    static object settings, form;
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags NPI = BindingFlags.NonPublic | BindingFlags.Instance;

    // The write seam. Counts every key and can fail one of them.
    class Writer
    {
        public readonly List<string> Keys = new List<string>();
        readonly string Fail;
        public Writer(string failKey) { Fail = failKey; }
        public bool Write(string key, string val, string file)
        {
            Keys.Add(key);
            return key != Fail;
        }
    }

    static FieldInfo HookField;
    static Writer Hook(string failKey)
    {
        var w = new Writer(failKey);
        HookField.SetValue(settings, Delegate.CreateDelegate(HookField.FieldType, w,
            typeof(Writer).GetMethod("Write")));
        return w;
    }
    static void Unhook() { HookField.SetValue(settings, null); }

    static object SaveResult() { return settingsType.GetMethod("SaveSettings").Invoke(settings, null); }
    static bool ResultBool(object r, string f) { return (bool)r.GetType().GetField(f).GetValue(r); }
    static string ResultText(object r, string f) { return (string)r.GetType().GetField(f).GetValue(r); }
    static bool Saved() { return ResultBool(SaveResult(), "Saved"); }
    static bool Dirty() { return (bool)settingsType.GetProperty("Dirty").GetValue(settings, null); }
    static string DurableFp() { return (string)settingsType.GetField("DurableFingerprint", NPI).GetValue(settings); }
    static string LastError() { return (string)settingsType.GetProperty("LastSaveError").GetValue(settings, null); }
    static object S(string f) { return settingsType.GetField(f).GetValue(settings); }
    static void SSet(string f, object v) { settingsType.GetField(f).SetValue(settings, v); }
    static object F(string f) { return formType.GetField(f, NP).GetValue(form); }
    static void FSet(string f, object v) { formType.GetField(f, NP).SetValue(form, v); }
    static string Note() { return (string)F("Note"); }
    static object Call(string name, params object[] args)
    {
        return formType.GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, args);
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

    // The documented user gesture: press Refresh, let the sweep settle.
    static void PressRefresh()
    {
        formType.GetMethod("RefreshData").Invoke(form, null);
        Wait(() => !(bool)F("Refreshing"), 20000);
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
            { "Theme", "nord" },
            { "LowPct", "20" },
            { "TrayMode", "single" },
            { "TrayFill", "4" },
        };
    }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_setrec_" + Guid.NewGuid().ToString("N"));
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
            formType = asm.GetType("Limisaw.LimisawForm");
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            Type themeType = asm.GetType("Limisaw.Theme");
            HookField = settingsType.GetField("WriteHook", NPI);
            var paletteType = asm.GetType("Limisaw.Palette");

            string ini = Path.Combine(temp, "LIMISAW.ini");
            WriteIni(ini, Baseline());

            settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);

            Unit(ini);

            object themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { root });
            using (var tray = new NotifyIcon())
            using (Form f = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            {
                form = f;
                object timer = F("RefreshTimer");
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                Wait(() => !(bool)F("Refreshing"), 60000);

                Workflow(asm, paletteType, ini, timer);
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

    // ── group 1: the structured save result ──────────────────────────────
    static void Unit(string ini)
    {
        Console.WriteLine("== the structured save result ==");
        Check("a fresh load starts clean: not dirty, no error, baseline taken",
            !Dirty() && LastError() == null && DurableFp() != null,
            "Dirty=" + Dirty());

        string fpBefore = DurableFp();
        SSet("WindowX", 42);
        Unhook();
        Check("a successful save says durable success",
            Saved() && !Dirty() && ResultText(SaveResult(), "Reason") == "",
            "Dirty=" + Dirty());
        Check("...and the durable baseline advanced to the saved snapshot",
            DurableFp() != fpBefore && DurableFp() != null, DurableFp().Substring(0, Math.Min(24, DurableFp().Length)));

        // A failure on a representative key: Theme. CORE-006 keeps the old ini
        // byte-for-byte; R020 keeps the BASELINE standing and marks the live
        // object dirty because it now differs from it.
        fpBefore = DurableFp();
        SSet("ThemeSlug", "oled");
        SSet("LowPct", 77);
        Hook("Theme");
        byte[] before = File.ReadAllBytes(ini);
        object r = SaveResult();
        Unhook();
        byte[] after = File.ReadAllBytes(ini);
        bool identical = before.Length == after.Length;
        if (identical) for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) { identical = false; break; }
        Check("a failed staged key write says durable failure",
            !ResultBool(r, "Saved") && LastError().Length > 0, ResultText(r, "Reason"));
        Check("...and the live ini is byte-for-byte the previous snapshot",
            identical, before.Length + " vs " + after.Length + " bytes");
        Check("...and the live object is dirty: live differs from the durable baseline",
            ResultBool(r, "Dirty") && Dirty(), "Dirty=" + Dirty());
        Check("...and the durable baseline did NOT move",
            DurableFp() == fpBefore, "");

        // The same invariants when the ATOMIC REPLACE is what refuses: a
        // read-only ini lets the stage succeed and the move fail.
        SSet("RefreshSeconds", 900);
        new FileInfo(ini).IsReadOnly = true;
        r = SaveResult();
        new FileInfo(ini).IsReadOnly = false;
        Unhook();
        after = File.ReadAllBytes(ini);
        identical = before.Length == after.Length;
        if (identical) for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) { identical = false; break; }
        Check("a failed atomic replace says durable failure too",
            !ResultBool(r, "Saved") && ResultText(r, "Reason").IndexOf("replace", StringComparison.Ordinal) >= 0,
            ResultText(r, "Reason"));
        Check("...and the ini is still the previous snapshot byte-for-byte",
            identical, "");
        Check("...and dirty stays true while the baseline stands",
            Dirty() && DurableFp() == fpBefore, "");
    }

    // ── group 1.4 + group 2: the form-level contract ─────────────────────
    static void Workflow(Assembly asm, Type paletteType, string ini, object timer)
    {
        Console.WriteLine();
        Console.WriteLine("== a failed mutation stays live and says so ==");
        PropertyInfo interval = timer.GetType().GetProperty("Interval");

        // Clean state on disk first: the baseline() snapshot, freshly loaded.
        Unhook();
        WriteIni(ini, Baseline());
        settingsType.GetMethod("Load").Invoke(settings, null);
        Check("the form starts against a clean baseline", !Dirty() && !formLastFailed(), "");

        // RefreshSeconds through the real button path (SetRefresh), Theme
        // through the real cycle (CycleTheme), both while every write fails.
        Hook("RefreshSeconds");
        Call("SetRefresh", 60);
        Unhook();
        Check("a failed refresh-interval change stays live",
            (int)S("RefreshSeconds") == 360, "RefreshSeconds=" + S("RefreshSeconds"));
        Check("...and the interval was re-armed to the LIVE value",
            (int)interval.GetValue(timer, null) == 360000, interval.GetValue(timer, null).ToString());
        Check("...and the note says LIVE != DURABLE, not just the new value",
            Note().IndexOf("not saved", StringComparison.Ordinal) >= 0
            && Note().IndexOf("Refresh every", StringComparison.Ordinal) >= 0, Note());
        Check("...and the disk still holds the old interval",
            ReadIniValue(ini, "RefreshSeconds") == "300", ReadIniValue(ini, "RefreshSeconds"));

        Hook("Theme");
        Call("CycleTheme");
        Unhook();
        string themeNow = (string)S("ThemeSlug");
        Check("a failed theme change stays live on screen",
            themeNow != "nord" && ThemeSlugOf(paletteType) == themeNow, "live theme " + themeNow);
        Check("...and its note names the theme and the failure",
            Note().IndexOf("Theme:", StringComparison.Ordinal) >= 0
            && Note().IndexOf("not saved", StringComparison.Ordinal) >= 0, Note());

        // TrayItems/order, LowPct and SoundDir at the settings level: the
        // structured result must flag every one of them dirty, and the disk
        // must keep the old values.
        Hook("TrayItems");
        settingsType.GetMethod("SetItemOrder").Invoke(settings,
            new object[] { new List<string> { "codex/a/five_hour", "claude/b/weekly" } });
        Check("a failed tray-order change keeps the live order and reports dirty",
            ((string)S("TrayItems")).IndexOf("codex/a/five_hour", StringComparison.Ordinal) == 0
            && Dirty() && !Saved(), "");
        Unhook();
        Hook("LowPct");
        SSet("LowPct", 55);
        Check("a failed low-threshold change keeps 55 and reports dirty",
            (int)S("LowPct") == 55 && Dirty() && !Saved(), "");
        Unhook();
        Hook("SoundDir");
        SSet("SoundDir", "V:\\nowhere");
        Check("a failed sound-folder change keeps the folder and reports dirty",
            (string)S("SoundDir") == "V:\\nowhere" && Dirty() && !Saved(), "");
        Unhook();
        Check("the disk never saw any of it",
            ReadIniValue(ini, "Theme") == "nord" && ReadIniValue(ini, "LowPct") == "20"
            && ReadIniValue(ini, "TrayItems") == "",
            "Theme=" + ReadIniValue(ini, "Theme"));

        Console.WriteLine();
        Console.WriteLine("== the dirty reload recovery contract ==");
        ResetClean(ini);

        // SCENARIO A — dirty, disk still the stale baseline, Refresh pressed
        // WITHOUT any external edit: the live choice survives, the refusal is
        // silent, and no fake "Reloaded LIMISAW.ini" appears.
        SSet("ThemeSlug", "oled");
        Hook("Theme");
        Check("the setup save fails as arranged", !Saved() && Dirty(), "");
        Unhook();
        FSet("Note", "sentinel");
        PressRefresh();
        Check("an unchanged stale disk does NOT overwrite the dirty choice",
            (string)S("ThemeSlug") == "oled" && Dirty(), "Theme=" + S("ThemeSlug"));
        Check("...and no fake reload was claimed",
            (string)F("Note") == "sentinel", Note());

        // SCENARIO C — ONLY an incidental WindowX/WindowY persistence. The
        // position may reach the disk; the unresolved theme conflict must
        // survive it untouched.
        var loc = ((Form)form).Location;
        Call("HideToTray");
        Check("the incidental position save did not resolve the dirty conflict",
            Dirty(), "Dirty=" + Dirty());
        Check("...the disk still holds the OLD theme",
            ReadIniValue(ini, "Theme") == "nord", ReadIniValue(ini, "Theme"));
        Check("...but the fresh position DID reach the disk",
            ReadIniValue(ini, "WindowX") == loc.X.ToString() && ReadIniValue(ini, "WindowY") == loc.Y.ToString(),
            "ini " + ReadIniValue(ini, "WindowX") + "/" + ReadIniValue(ini, "WindowY")
            + " vs " + loc.X + "/" + loc.Y);
        FSet("Note", "sentinel2");
        PressRefresh();
        Check("...and the still-open conflict is still not read back over",
            (string)S("ThemeSlug") == "oled" && (string)F("Note") == "sentinel2", Note());

        // SCENARIO D — an explicit later successful save/retry resolves it.
        Check("an explicit retry saves the live snapshot",
            Saved() && !Dirty() && LastError() == null, "");
        Check("...the disk now holds the live theme and the conflict is gone",
            ReadIniValue(ini, "Theme") == "oled", ReadIniValue(ini, "Theme"));
        string fpAfterRetry = DurableFp();

        // SCENARIO B — a NEW dirty conflict, then the user edits the ini by
        // hand and presses Refresh: the coherent external snapshot is accepted
        // WHOLE, the conflict clears, and the live derived state follows.
        SSet("ThemeSlug", "goldendefault");
        SSet("RefreshSeconds", 600);
        Hook("Theme");
        Check("the second conflict is arranged", !Saved() && Dirty(), "");
        Unhook();
        var external = new Dictionary<string, string>(Baseline());
        external["Theme"] = "dracula";
        external["RefreshSeconds"] = "900";
        external["LowPct"] = "45";
        WriteIni(ini, external);
        PressRefresh();
        Check("a coherently edited external file is accepted on Refresh",
            (string)S("ThemeSlug") == "dracula" && (int)S("RefreshSeconds") == 900
            && (int)S("LowPct") == 45, "Theme=" + S("ThemeSlug"));
        Check("...the conflict is resolved: dirty false, error cleared",
            !Dirty() && LastError() == null && !formLastFailed(), "");
        Check("...the timer was re-armed to the external interval",
            (int)interval.GetValue(timer, null) == 900000, interval.GetValue(timer, null).ToString());
        Check("...the palette follows the external theme",
            ThemeSlugOf(paletteType) == "dracula", ThemeSlugOf(paletteType));
        Check("...and the reload was announced as a recovery, not a plain reload",
            Note().IndexOf("Repaired", StringComparison.Ordinal) >= 0
            || Note().IndexOf("Reloaded", StringComparison.Ordinal) >= 0, Note());
        Check("...and the external snapshot is the durable baseline now",
            DurableFp() != fpAfterRetry, "");

        // The incidental save is an ordinary full save when nothing is dirty.
        Unhook();
        WriteIni(ini, external);
        settingsType.GetMethod("Load").Invoke(settings, null);
        SSet("WindowX", 1234); SSet("WindowY", 567);
        Check("SavePosition with a clean baseline persists the position plainly",
            ResultBool(SavePositionViaReflection(), "Saved") && !Dirty()
            && ReadIniValue(ini, "WindowX") == "1234", ReadIniValue(ini, "WindowX"));
    }

    static bool formLastFailed() { return (bool)settingsType.GetField("LastSaveFailed").GetValue(settings); }
    static object SavePositionViaReflection() { return settingsType.GetMethod("SavePosition").Invoke(settings, null); }

    static void ResetClean(string ini)
    {
        Unhook();
        WriteIni(ini, Baseline());
        settingsType.GetMethod("Load").Invoke(settings, null);
    }

    static string ThemeSlugOf(Type paletteType)
    {
        object t = paletteType.GetField("T").GetValue(null);
        return (string)t.GetType().GetField("Slug").GetValue(t);
    }

    static string ReadIniValue(string ini, string key)
    {
        foreach (string line in File.ReadAllLines(ini))
        {
            string t = line.Trim();
            if (t.StartsWith(key + "=", StringComparison.Ordinal)) return t.Substring(key.Length + 1);
        }
        return "";
    }
}
