using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// W2-004: a settings save is twenty-four INI writes, and only the first one used
// to be checked. If `RefreshSeconds` landed, LastSaveFailed was set to false
// immediately and the remaining twenty-three return values were discarded. So a
// disk that filled up, a file locked halfway through, or any single failing key
// produced a mixture of old and new settings while the footer said everything was
// saved — and on restart the app resurrected values the user had already watched
// change. The one bool was documented as "if one key fails they all fail", which
// is an assumption the implementation never enforced.
//
// Every write is checked now, and the failure of any single key fails the whole
// save. `SaveApplied()` is what callers use before doing something that outlives
// the process: the autostart registry value is applied only when the ini really
// recorded the choice, so Windows cannot end up launching an app whose settings
// say autostart is off.
//
// The failing key cannot be arranged on a real disk on demand, so the writer is
// injected (LimisawSettings.WriteHook) and told which key to fail — including the
// LAST one, the case the old probe-the-first-key design could never see.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class SavePartial
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Type settingsType;
    static FieldInfo hookField, failedField;

    // A writer that records every key and fails on one of them. Everything else
    // is written for real, so a partial file is genuinely produced on disk and the
    // recovery claim can be checked rather than assumed.
    class Writer
    {
        public readonly List<string> Keys = new List<string>();
        readonly string Fail;
        readonly string Path;

        public Writer(string failKey, string path) { Fail = failKey; Path = path; }

        public bool Write(string key, string val, string file)
        {
            Keys.Add(key);
            if (key == Fail) return false;
            Ini.Set(Path, key, val);
            return true;
        }
    }

    // A minimal INI writer, so the harness does not need the same P/Invoke the
    // product uses.
    static class Ini
    {
        public static void Set(string path, string key, string val)
        {
            var lines = new List<string>();
            if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
            if (lines.Count == 0) lines.Add("[limisaw]");
            bool done = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].StartsWith(key + "=", StringComparison.Ordinal)) continue;
                lines[i] = key + "=" + val;
                done = true;
                break;
            }
            if (!done) lines.Add(key + "=" + val);
            File.WriteAllLines(path, lines.ToArray());
        }
    }

    static object NewSettings(string dir)
    {
        object s = Activator.CreateInstance(settingsType, new object[] { dir });
        settingsType.GetMethod("Load").Invoke(s, null);
        return s;
    }

    static void Save(object s) { settingsType.GetMethod("Save").Invoke(s, null); }
    static bool SaveApplied(object s) { return (bool)settingsType.GetMethod("SaveApplied").Invoke(s, null); }
    static bool Failed(object s) { return (bool)failedField.GetValue(s); }
    static object Value(object s, string field) { return settingsType.GetField(field).GetValue(s); }
    static void Set(object s, string field, object v) { settingsType.GetField(field).SetValue(s, v); }

    static Writer Hook(object s, string failKey, string ini)
    {
        var w = new Writer(failKey, ini);
        hookField.SetValue(s, Delegate.CreateDelegate(hookField.FieldType, w,
            typeof(Writer).GetMethod("Write")));
        return w;
    }

    static void Unhook(object s) { hookField.SetValue(s, null); }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_savepartial_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            Assembly asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            hookField = settingsType.GetField("WriteHook",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            failedField = settingsType.GetField("LastSaveFailed");

            EveryKey(temp);
            Recovery(temp);
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

    static void EveryKey(string temp)
    {
        Console.WriteLine("== a failure on ANY key fails the whole save ==");
        string ini = Path.Combine(temp, "LIMISAW.ini");
        File.Delete(ini);
        object s = NewSettings(temp);

        // First, the shape of a healthy save: how many keys there are, and that
        // the flag is clear.
        Writer all = Hook(s, null, ini);
        Save(s);
        Check("a save that fully lands reports success",
            !Failed(s), "LastSaveFailed=" + Failed(s));
        Check("...and writes every persisted key",
            all.Keys.Count >= 24, all.Keys.Count + " keys");
        Check("...including the ones the audit named specifically",
            all.Keys.Contains("AutoStart") && all.Keys.Contains("TrayItems")
            && all.Keys.Contains("WindowY"), string.Join(",", all.Keys.ToArray()));

        // The three keys W2-004 called out, plus the first and the last: the old
        // code could only ever detect the first.
        foreach (string key in new[] { "RefreshSeconds", "TrayItems", "AutoStart", "WindowY" })
        {
            Writer w = Hook(s, key, ini);
            Save(s);
            Check("a failure on " + key + " is reported as a failed save",
                Failed(s), "LastSaveFailed=" + Failed(s));
            Check("...and the save still attempts every remaining key, not just up to it",
                w.Keys.Count == all.Keys.Count, w.Keys.Count + " of " + all.Keys.Count);
        }

        // WindowY is the LAST write. This is the case the old design was
        // structurally blind to: the probe key succeeded, so success was reported
        // before the failing write even happened.
        Writer last = Hook(s, "WindowY", ini);
        Save(s);
        Check("the LAST key failing is not reported as success (the old blind spot)",
            Failed(s), "LastSaveFailed=" + Failed(s));
        Check("...and it really was the last key attempted",
            last.Keys[last.Keys.Count - 1] == "WindowY", last.Keys[last.Keys.Count - 1]);

        // And a healthy save afterwards must clear the flag again.
        Hook(s, null, ini);
        Save(s);
        Check("a later fully successful save clears the flag",
            !Failed(s), "LastSaveFailed=" + Failed(s));
        Unhook(s);
    }

    static void Recovery(string temp)
    {
        Console.WriteLine();
        Console.WriteLine("== a partial write is never advertised as a saved snapshot ==");
        string dir = Path.Combine(temp, "recovery");
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "LIMISAW.ini");

        // A known-good snapshot on disk first.
        object good = NewSettings(dir);
        Set(good, "ThemeSlug", "nord");
        Set(good, "LowPct", 33);
        Set(good, "TrayItems", "codex/five_hour|claude/weekly");
        Hook(good, null, ini);
        Save(good);
        Unhook(good);
        Check("the known-good snapshot is on disk",
            !Failed(good) && File.Exists(ini), "LastSaveFailed=" + Failed(good));

        object reread = NewSettings(dir);
        Check("...and reads back exactly",
            (string)Value(reread, "ThemeSlug") == "nord"
            && (int)Value(reread, "LowPct") == 33
            && (string)Value(reread, "TrayItems") == "codex/five_hour|claude/weekly",
            (string)Value(reread, "ThemeSlug") + "/" + Value(reread, "LowPct"));

        // Now a save where a middle key fails.
        object partial = NewSettings(dir);
        Set(partial, "ThemeSlug", "oled");
        Set(partial, "LowPct", 77);
        Set(partial, "TrayItems", "codex/weekly");
        Hook(partial, "TrayItems", ini);
        Save(partial);
        Unhook(partial);
        Check("the partial save reports failure",
            Failed(partial), "LastSaveFailed=" + Failed(partial));

        object after = NewSettings(dir);
        Check("...and the file is genuinely mixed, which is why it must not be trusted",
            (string)Value(after, "ThemeSlug") == "oled"
            && (string)Value(after, "TrayItems") == "codex/five_hour|claude/weekly",
            "Theme=" + Value(after, "ThemeSlug") + ", TrayItems=" + Value(after, "TrayItems"));

        // The consequence the audit cared about: a failed save must not be
        // reloaded over the user's live choices, and W2-003's Reload is the
        // component that has to honour that.
        Set(partial, "LowPct", 88);
        failedField.SetValue(partial, true);
        Check("a known-failed save is not overwritten by the stale file",
            !(bool)settingsType.GetMethod("Reload").Invoke(partial, null)
            && (int)Value(partial, "LowPct") == 88, Value(partial, "LowPct").ToString());

        // SaveApplied is the gate for side effects that outlive the process.
        object gated = NewSettings(dir);
        Hook(gated, "AutoStart", ini);
        Check("SaveApplied() says NO when a key failed",
            !SaveApplied(gated), "");
        Hook(gated, null, ini);
        Check("...and YES when the whole snapshot landed",
            SaveApplied(gated), "");
        Unhook(gated);
    }

    static void Source(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== the autostart side effect stays behind the save gate ==");
        string ui = File.ReadAllText(Path.Combine(SourceRoot(root), "LIMISAW.cs"));

        // A registry value that outlives the process must not be written on the
        // strength of a save that failed. Both toggles - the Settings button and
        // the tray menu item - go through SaveApplied.
        Check("the Settings toggle applies autostart only after a successful save",
            ui.IndexOf("if (Settings.SaveApplied())", StringComparison.Ordinal) >= 0, "");
        Check("the tray toggle does too",
            ui.IndexOf("if (s.SaveApplied()) { autoItem.Checked = want; ApplyAutostart(s); }",
                StringComparison.Ordinal) >= 0, "");
        Check("...and neither one calls Save() and then applies regardless",
            ui.IndexOf("s.Save(); ApplyAutostart(s);", StringComparison.Ordinal) < 0, "");
        Check("the save no longer returns early after probing one key",
            ui.IndexOf("LastSaveFailed = !ok;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("LastSaveFailed = false;", StringComparison.Ordinal) < 0, "");
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
