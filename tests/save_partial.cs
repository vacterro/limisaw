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
// CORE-006's staging also owns preservation: the temp is SEEDED from the live
// ini and reconciled against a stale one, so a successful save keeps unknown
// keys and foreign sections, a failed save leaves the live file byte-identical
// with no temp behind, and an abandoned temp can never leak into a later save.
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
    // is written for real INTO THE STAGED FILE Save() names (CORE-006: saves are
    // transactional — the staged temp is what receives the keys, and only a
    // fully-successful save replaces the live ini), so a partial stage is
    // genuinely produced on disk and the recovery claim can be checked rather
    // than assumed.
    class Writer
    {
        public readonly List<string> Keys = new List<string>();
        readonly string Fail;

        public Writer(string failKey) { Fail = failKey; }

        public bool Write(string key, string val, string file)
        {
            Keys.Add(key);
            if (key == Fail) return false;
            Ini.Set(file, key, val);
            return true;
        }
    }

    // A minimal INI writer, so the harness does not need the same P/Invoke the
    // product uses. Section-aware like WritePrivateProfileString: a key is
    // replaced only inside [limisaw], and a NEW key is inserted at the end of
    // the [limisaw] block — never after a foreign section header that happens
    // to follow it, which is where a naive append would silently file a
    // LIMISAW-owned key under somebody else's section.
    static class Ini
    {
        public static void Set(string path, string key, string val)
        {
            var lines = new List<string>();
            if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
            bool done = false;
            int limisawStart = -1;
            int limisawEnd = lines.Count;
            bool inLimisaw = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Length > 0 && t[0] == '[')
                {
                    bool lim = t.Equals("[limisaw]", StringComparison.OrdinalIgnoreCase);
                    if (inLimisaw && !lim) limisawEnd = i;
                    inLimisaw = lim;
                    if (lim && limisawStart < 0) limisawStart = i;
                }
                else if (inLimisaw && t.StartsWith(key + "=", StringComparison.Ordinal))
                {
                    lines[i] = key + "=" + val;
                    done = true;
                    break;
                }
            }
            if (!done)
            {
                if (limisawStart < 0) { lines.Add("[limisaw]"); lines.Add(key + "=" + val); }
                else lines.Insert(limisawEnd, key + "=" + val);
            }
            File.WriteAllLines(path, lines.ToArray());
        }
    }

    static object NewSettings(string dir)
    {
        object s = Activator.CreateInstance(settingsType, new object[] { dir });
        settingsType.GetMethod("Load").Invoke(s, null);
        return s;
    }

    // The structured save (W2-006/R020): invoke it and read one field of the
    // result.
    static bool InvokeSaveResult(object s, string field)
    {
        object r = settingsType.GetMethod("SaveSettings").Invoke(s, null);
        return (bool)r.GetType().GetField(field).GetValue(r);
    }

    static void Save(object s) { settingsType.GetMethod("Save").Invoke(s, null); }
    static bool SaveApplied(object s) { return (bool)settingsType.GetMethod("SaveApplied").Invoke(s, null); }
    static bool Failed(object s) { return (bool)failedField.GetValue(s); }
    static object Value(object s, string field) { return settingsType.GetField(field).GetValue(s); }
    static void Set(object s, string field, object v) { settingsType.GetField(field).SetValue(s, v); }

    static Writer Hook(object s, string failKey)
    {
        var w = new Writer(failKey);
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
            Preservation(temp);
            LostUpdate(temp);
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
        Writer all = Hook(s, null);
        Save(s);
        Check("a save that fully lands reports success",
            !Failed(s), "LastSaveFailed=" + Failed(s));
        Check("...and writes every persisted key",
            all.Keys.Count >= 24, all.Keys.Count + " keys");
        Check("...including the ones the audit named specifically",
            all.Keys.Contains("AutoStart") && all.Keys.Contains("TrayItems")
            && all.Keys.Contains("WindowY"), string.Join(",", all.Keys.ToArray()));
        Check("a fully successful save lands the live ini",
            File.Exists(ini) && new FileInfo(ini).Length > 0, ini);
        Check("...and the temp file is cleaned up",
            !File.Exists(ini + ".tmp"), ini + ".tmp");

        // The three keys W2-004 called out, plus the first and the last: the old
        // code could only ever detect the first.
        foreach (string key in new[] { "RefreshSeconds", "TrayItems", "AutoStart", "WindowY" })
        {
            Writer w = Hook(s, key);
            Save(s);
            Check("a failure on " + key + " is reported as a failed save",
                Failed(s), "LastSaveFailed=" + Failed(s));
            Check("...and the save still attempts every remaining key, not just up to it",
                w.Keys.Count == all.Keys.Count, w.Keys.Count + " of " + all.Keys.Count);
            Check("...and the failed save does not touch the live ini",
                File.Exists(ini), ini);
        }

        // WindowY is the LAST write. This is the case the old design was
        // structurally blind to: the probe key succeeded, so success was reported
        // before the failing write even happened.
        Writer last = Hook(s, "WindowH");
        Save(s);
        Check("the LAST key failing is not reported as success (the old blind spot)",
            Failed(s), "LastSaveFailed=" + Failed(s));
        Check("...and it really was the last key attempted",
            last.Keys[last.Keys.Count - 1] == "WindowH", last.Keys[last.Keys.Count - 1]);

        // And a healthy save afterwards must clear the flag again.
        Hook(s, null);
        Save(s);
        Check("a later fully successful save clears the flag",
            !Failed(s), "LastSaveFailed=" + Failed(s));
        Check("...and the live ini still has the just-saved snapshot",
            File.Exists(ini) && new FileInfo(ini).Length > 0, ini);
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
        Hook(good, null);
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

        // Now a save where a middle key fails. CORE-006 says the live file is
        // the pre-failure snapshot: the staged temp is thrown away and the
        // live ini keeps what was there before the doomed save started.
        object partial = NewSettings(dir);
        Set(partial, "ThemeSlug", "oled");
        Set(partial, "LowPct", 77);
        Set(partial, "TrayItems", "codex/weekly");
        Hook(partial, "TrayItems");
        Save(partial);
        Unhook(partial);
        Check("the partial save reports failure",
            Failed(partial), "LastSaveFailed=" + Failed(partial));

        object after = NewSettings(dir);
        Check("...and the live ini is UNCHANGED (the staging means a failure cannot leave a half-saved snapshot)",
            (string)Value(after, "ThemeSlug") == "nord"
            && (string)Value(after, "TrayItems") == "codex/five_hour|claude/weekly",
            "Theme=" + Value(after, "ThemeSlug") + ", TrayItems=" + Value(after, "TrayItems"));

        // W2-006/R020: the failed save did not merely set a flag — the live
        // object is now DIRTY against the durable baseline (disk still holds
        // the accepted snapshot, live holds the newer choices). The refusal
        // that keeps the stale file from clobbering them stays, but it is no
        // longer a sticky veto: the SAME reader accepts a file that changed.
        PropertyInfo dirtyProp = settingsType.GetProperty("Dirty");
        Set(partial, "LowPct", 88);
        Check("the failed save left the live object dirty against the baseline",
            (bool)dirtyProp.GetValue(partial, null) && Failed(partial),
            "Dirty=" + dirtyProp.GetValue(partial, null));
        Check("a dirty choice is not overwritten by the unchanged stale file",
            !(bool)settingsType.GetMethod("Reload").Invoke(partial, null)
            && (int)Value(partial, "LowPct") == 88 && (string)Value(partial, "ThemeSlug") == "oled",
            "LowPct=" + Value(partial, "LowPct") + ", Theme=" + Value(partial, "ThemeSlug"));

        // The recovery path: an externally CHANGED coherent file is the user's
        // own repair. It is accepted WHOLE — no merging of the dirty live
        // object with the file — and it clears the conflict.
        File.WriteAllLines(ini, new string[] {
            "[limisaw]",
            "Theme=dracula", "LowPct=50", "RefreshSeconds=900",
            "TrayItems=codex/five_hour|claude/weekly",
        });
        Check("an externally edited file is accepted after a failed save",
            (bool)settingsType.GetMethod("Reload").Invoke(partial, null), "");
        Check("...the live object becomes the external snapshot (no merge: LowPct=88 did not survive)",
            (string)Value(partial, "ThemeSlug") == "dracula" && (int)Value(partial, "LowPct") == 50,
            "Theme=" + Value(partial, "ThemeSlug") + ", LowPct=" + Value(partial, "LowPct"));
        Check("...and the conflict is resolved: dirty false, error cleared",
            !(bool)dirtyProp.GetValue(partial, null) && !Failed(partial),
            "Dirty=" + dirtyProp.GetValue(partial, null) + ", LastSaveFailed=" + Failed(partial));
        Check("...and the external snapshot is now the durable baseline (a new failed save diffs against it)",
            (bool)InvokeSaveResult(partial, "Saved") && !(bool)dirtyProp.GetValue(partial, null), "");

        // SaveApplied is the gate for side effects that outlive the process.
        object gated = NewSettings(dir);
        Hook(gated, "AutoStart");
        Check("SaveApplied() says NO when a key failed",
            !SaveApplied(gated), "");
        Hook(gated, null);
        Check("...and YES when the whole snapshot landed",
            SaveApplied(gated), "");
        Unhook(gated);
    }

    // The staging contract: a save rewrites ONLY the keys LIMISAW owns, so
    // everything else in the ini must come through a save untouched — and a
    // failed save must leave no trace at all, not even the temp it staged.
    static void Preservation(string temp)
    {
        Console.WriteLine();
        Console.WriteLine("== a save preserves what it does not own ==");
        string dir = Path.Combine(temp, "preserve");
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "LIMISAW.ini");

        // A live ini carrying an unknown limisaw key and a foreign section —
        // the shapes a newer version or another tool leaves behind. The owned
        // keys a save must change sit in the same section, under the ini key
        // names Save() actually writes ("Theme", not the field name).
        File.WriteAllLines(ini, new string[] {
            "[limisaw]",
            "Theme=nord",
            "LowPct=33",
            "FutureKey=keep-me",
            "[codex]",
            "home=C:\\not-ours",
            "session=abc123",
        });

        object s = NewSettings(dir);
        Set(s, "ThemeSlug", "dracula");
        Set(s, "LowPct", 66);
        Hook(s, null);
        Save(s);
        Unhook(s);
        string after = File.ReadAllText(ini);
        Check("a successful save reports success",
            !Failed(s), "LastSaveFailed=" + Failed(s));
        Check("...and leaves no temp file behind",
            !File.Exists(ini + ".tmp"), ini + ".tmp");
        Check("...changes the owned values",
            after.IndexOf("Theme=dracula", StringComparison.Ordinal) >= 0
            && after.IndexOf("LowPct=66", StringComparison.Ordinal) >= 0
            && after.IndexOf("Theme=nord", StringComparison.Ordinal) < 0,
            "Theme=" + (string)Value(s, "ThemeSlug") + " LowPct=" + Value(s, "LowPct"));
        Check("...preserves the unknown key in the limisaw section",
            after.IndexOf("FutureKey=keep-me", StringComparison.Ordinal) >= 0, "");
        Check("...preserves the foreign [codex] section and its keys",
            after.IndexOf("[codex]", StringComparison.Ordinal) >= 0
            && after.IndexOf("session=abc123", StringComparison.Ordinal) >= 0, "");
        object reread = NewSettings(dir);
        Check("...and the changed values read back through Load",
            (string)Value(reread, "ThemeSlug") == "dracula"
            && (int)Value(reread, "LowPct") == 66,
            (string)Value(reread, "ThemeSlug") + "/" + Value(reread, "LowPct"));

        // A failed save: the live ini must survive BYTE-identical, and neither
        // the temp nor any promoted partial state may outlive the call.
        byte[] before = File.ReadAllBytes(ini);
        object doomed = NewSettings(dir);
        Set(doomed, "ThemeSlug", "oled");
        Hook(doomed, "LowPct");
        Save(doomed);
        Unhook(doomed);
        byte[] survived = File.ReadAllBytes(ini);
        bool identical = survived.Length == before.Length;
        if (identical) for (int i = 0; i < before.Length; i++) if (before[i] != survived[i]) { identical = false; break; }
        Check("a failed save leaves the known-good live ini byte-identical",
            Failed(doomed) && identical,
            "LastSaveFailed=" + Failed(doomed) + " bytes=" + survived.Length);
        Check("...and leaves no temp and no promoted partial state",
            !File.Exists(ini + ".tmp"), ini + ".tmp");

        // A temp abandoned by an interrupted save must never be mistaken for
        // the staging area of the next one.
        File.WriteAllLines(ini + ".tmp", new string[] {
            "[limisaw]",
            "ThemeSlug=evil",
            "FutureKey=poison",
            "[ghost]",
            "x=1",
        });
        object later = NewSettings(dir);
        Set(later, "ThemeSlug", "solarized");
        Hook(later, null);
        Save(later);
        Unhook(later);
        string late = File.ReadAllText(ini);
        Check("a stale temp from an interrupted save cannot contaminate a later save",
            !Failed(later)
            && late.IndexOf("evil", StringComparison.Ordinal) < 0
            && late.IndexOf("poison", StringComparison.Ordinal) < 0
            && late.IndexOf("[ghost]", StringComparison.Ordinal) < 0,
            "Theme=" + (string)Value(later, "ThemeSlug"));
        Check("...and the stale temp is gone once the save is done",
            !File.Exists(ini + ".tmp"), ini + ".tmp");
    }

    // W2-002: a settings save used to be unconditional. Staging is seeded from
    // the live ini and then atomically replaces it — but if another editor (a
    // second LIMISAW instance, a sync tool, the user's own text editor) wrote
    // between the seed and the replace, the replace silently discarded their
    // bytes. The commit is now optimistic: the live file is re-fingerprinted
    // immediately before the move, and a mismatch refuses the save so the
    // external edit wins and the live object is left dirty for an explicit
    // reconcile.
    static void LostUpdate(string temp)
    {
        Console.WriteLine();
        Console.WriteLine("== W2-002: a concurrent external edit is never silently overwritten ==");
        string dir = Path.Combine(temp, "lostupdate");
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "LIMISAW.ini");

        object seed = NewSettings(dir);
        Set(seed, "ThemeSlug", "nord");
        Set(seed, "LowPct", 33);
        Hook(seed, null);
        Save(seed);
        Unhook(seed);
        Check("the W2-002 baseline save lands", !Failed(seed) && File.Exists(ini), ini);

        FieldInfo beforeField = settingsType.GetField("BeforeSettingsCommit",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        Check("the pre-commit seam exists", beforeField != null, "");

        object s = NewSettings(dir);
        Set(s, "ThemeSlug", "dracula");
        Set(s, "LowPct", 66);
        Hook(s, null);

        // The external editor writes a coherent file — an owned key changed, an
        // unknown limisaw key added, a foreign section added — in the window
        // between staging and the atomic replace.
        string[] external = new string[] {
            "[limisaw]",
            "Theme=solarized",
            "LowPct=55",
            "FutureKey=keep-me",
            "[codex]",
            "session=abc123",
        };
        string externalBytes = null;
        beforeField.SetValue(s, (Action)(() => { File.WriteAllLines(ini, external); externalBytes = File.ReadAllText(ini); }));
        object r = settingsType.GetMethod("SaveSettings").Invoke(s, null);
        beforeField.SetValue(s, null);
        Unhook(s);

        bool saved = (bool)r.GetType().GetField("Saved").GetValue(r);
        string reason = (string)r.GetType().GetField("Reason").GetValue(r);
        Check("the save refuses when the live ini changed under it", !saved,
            "Saved=" + saved + " reason=" + reason);
        Check("...and says why", reason.IndexOf("changed while settings", StringComparison.Ordinal) >= 0, reason);
        Check("...and the external editor's bytes survive untouched",
            File.ReadAllText(ini) == externalBytes, "len=" + new FileInfo(ini).Length);
        Check("...and no staging temp is left behind", !File.Exists(ini + ".tmp"), ini + ".tmp");
        Check("...and the live object is dirty against the durable baseline",
            (bool)settingsType.GetProperty("Dirty").GetValue(s, null)
            && (string)Value(s, "ThemeSlug") == "dracula", "Theme=" + Value(s, "ThemeSlug"));

        // The explicit reconcile: the user accepts the external file, then the
        // retried save preserves the keys LIMISAW does not own.
        Check("the external file is accepted by an explicit reload",
            (bool)settingsType.GetMethod("Reload").Invoke(s, null), "");
        Hook(s, null);
        Save(s);
        Unhook(s);
        string after = File.ReadAllText(ini);
        Check("...the retried save lands", !Failed(s), "LastSaveFailed=" + Failed(s));
        Check("...and preserves the unknown key",
            after.IndexOf("FutureKey=keep-me", StringComparison.Ordinal) >= 0, "");
        Check("...and the foreign section",
            after.IndexOf("[codex]", StringComparison.Ordinal) >= 0
            && after.IndexOf("session=abc123", StringComparison.Ordinal) >= 0, "");

        // ── CORE-001 (audit/6): the pre-stage external edit ─────────────────
        // W2-002 above only proves an edit DURING staging. The far more common
        // window is an edit AFTER the last accepted Load/Reload and BEFORE the
        // save even begins: the old Stage seeded its temp from that newer file
        // and then overwrote every owned key from stale memory, so the external
        // edit was destroyed with the commit check passing. A plain resize was
        // enough to trigger it.
        Console.WriteLine();
        Console.WriteLine("== CORE-001: an external edit BEFORE staging is never overwritten ==");
        string cdir = Path.Combine(temp, "core001_prestage");
        Directory.CreateDirectory(cdir);
        string cini = Path.Combine(cdir, "LIMISAW.ini");

        object baselineA = NewSettings(cdir);
        Set(baselineA, "ThemeSlug", "nord");
        Hook(baselineA, null);
        Save(baselineA);
        Unhook(baselineA);
        Check("BASELINE A lands (Theme=nord)", !Failed(baselineA) && File.ReadAllText(cini).IndexOf("Theme=nord", StringComparison.Ordinal) >= 0, "");

        // The running process: Load accepted the nord snapshot. Then an external
        // editor changes an OWNED key and adds a foreign section — BEFORE any
        // save starts. There is no BeforeSettingsCommit seam involved here: that
        // is the whole point. The live object keeps another unrelated change.
        object live = NewSettings(cdir);
        File.WriteAllLines(cini, new string[] {
            "[limisaw]",
            "Theme=dracula",
            "LowPct=33",
            "FutureKey=external-kept",
            "[foreign]",
            "note=external section survives",
        });
        Set(live, "LowPct", 77);                 // an in-memory change to another setting
        Hook(live, null);
        object pre = settingsType.GetMethod("SaveSettings").Invoke(live, null);
        Unhook(live);
        bool preSaved = (bool)pre.GetType().GetField("Saved").GetValue(pre);
        string preReason = (string)pre.GetType().GetField("Reason").GetValue(pre);
        string preOnDisk = File.ReadAllText(cini);
        Check("a pre-stage external edit refuses the full settings write", !preSaved,
            "Saved=" + preSaved + " reason=" + preReason);
        Check("...and says why (reload before saving)",
            preReason.IndexOf("changed externally", StringComparison.Ordinal) >= 0, preReason);
        Check("...and the external owned value survives",
            preOnDisk.IndexOf("Theme=dracula", StringComparison.Ordinal) >= 0, preOnDisk);
        Check("...and the external unknown key survives",
            preOnDisk.IndexOf("FutureKey=external-kept", StringComparison.Ordinal) >= 0, preOnDisk);
        Check("...and the foreign section survives",
            preOnDisk.IndexOf("[foreign]", StringComparison.Ordinal) >= 0
            && preOnDisk.IndexOf("note=external section survives", StringComparison.Ordinal) >= 0, preOnDisk);
        Check("...and the in-memory change was NOT written",
            preOnDisk.IndexOf("LowPct=77", StringComparison.Ordinal) < 0, preOnDisk);
        Check("...and no temp residue remains", !File.Exists(cini + ".tmp"), cini + ".tmp");
        Check("...and Dirty stays coherent (the live choice is still unsaved)",
            (bool)settingsType.GetProperty("Dirty").GetValue(live, null), "");

        // The user accepts the external revision, then their own change lands.
        object reloadResult = settingsType.GetMethod("ReloadEx").Invoke(live, null);
        bool accepted = (bool)reloadResult.GetType().GetField("Accepted").GetValue(reloadResult);
        Check("ReloadEx accepts the external revision", accepted, "Accepted=" + accepted);
        Set(live, "LowPct", 77);
        Hook(live, null);
        object retry = settingsType.GetMethod("SaveSettings").Invoke(live, null);
        Unhook(live);
        string retryOnDisk = File.ReadAllText(cini);
        Check("after ReloadEx the user's own setting save succeeds",
            (bool)retry.GetType().GetField("Saved").GetValue(retry),
            (string)retry.GetType().GetField("Reason").GetValue(retry));
        Check("...and the accepted external Theme is now the live one",
            retryOnDisk.IndexOf("Theme=dracula", StringComparison.Ordinal) >= 0, retryOnDisk);
        Check("...and the user's setting reached the disk",
            retryOnDisk.IndexOf("LowPct=77", StringComparison.Ordinal) >= 0, retryOnDisk);
        Check("...and the foreign section still survives",
            retryOnDisk.IndexOf("[foreign]", StringComparison.Ordinal) >= 0, retryOnDisk);
        Check("...and no temp residue remains", !File.Exists(cini + ".tmp"), cini + ".tmp");

        // The incidental geometry save: a move/resize/hide must never destroy an
        // unaccepted external edit either.
        Console.WriteLine();
        Console.WriteLine("== CORE-001: an incidental geometry save defers instead of overwriting ==");
        string gdir = Path.Combine(temp, "core001_geometry");
        Directory.CreateDirectory(gdir);
        string gini = Path.Combine(gdir, "LIMISAW.ini");

        object gBase = NewSettings(gdir);
        Set(gBase, "ThemeSlug", "nord");
        Hook(gBase, null);
        Save(gBase);
        Unhook(gBase);
        object geometry = NewSettings(gdir);
        File.WriteAllLines(gini, new string[] {
            "[limisaw]",
            "Theme=dracula",
            "LowPct=33",
        });
        Set(geometry, "WindowX", 111);
        Set(geometry, "WindowY", 222);
        Hook(geometry, null);
        object geo = settingsType.GetMethod("SavePosition").Invoke(geometry, null);
        Unhook(geometry);
        string geoOnDisk = File.ReadAllText(gini);
        Check("the incidental geometry save is deferred on an unaccepted revision",
            !(bool)geo.GetType().GetField("Saved").GetValue(geo), "Saved on " + geoOnDisk);
        Check("...and the external edit survives untouched",
            geoOnDisk.IndexOf("Theme=dracula", StringComparison.Ordinal) >= 0, geoOnDisk);
        Check("...and the new geometry stays in memory",
            (int)Value(geometry, "WindowX") == 111 && (int)Value(geometry, "WindowY") == 222,
            Value(geometry, "WindowX") + "/" + Value(geometry, "WindowY"));
        Check("...and no temp residue remains", !File.Exists(gini + ".tmp"), gini + ".tmp");
    }

    static void Source(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== the autostart side effect stays behind the save gate ==");
        string ui = File.ReadAllText(Path.Combine(SourceRoot(root), "LIMISAW.cs"));

        // A registry value that outlives the process must not be written on the
        // strength of a save that failed — and not on the strength of a bare
        // SetValue either: only a verified readback may claim success.
        Check("the Settings toggle applies autostart only after a successful save",
            ui.IndexOf("if (!Settings.SaveSettings().Saved)", StringComparison.Ordinal) >= 0, "");
        Check("the tray toggle does too",
            ui.IndexOf("if (!s.SaveApplied()) { s.AutoStart = !want; return; }", StringComparison.Ordinal) >= 0, "");
        Check("...and both routes go through the verified reconciler",
            ui.IndexOf("ReconcileAutostart(s)", StringComparison.Ordinal) >= 0
            && ui.IndexOf("void ApplyAutostart(", StringComparison.Ordinal) < 0, "");
        Check("every save outcome is recorded from the one transaction",
            ui.IndexOf("LastSaveFailed = !ok;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("SettingsSaveResult Stage(LimisawSettings src, bool explicitSave)", StringComparison.Ordinal) >= 0, "");
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
