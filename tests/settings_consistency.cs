using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Limisaw;

// W2-006/W2-007 / SRC-004:R012 + R021 — the settings consistency contract.
//
// Three truths used to diverge silently: the in-memory settings, LIMISAW.ini,
// and the external state the settings imply (the HKCU Run autostart entry,
// the timer, the palette, TopMost, the tray) plus the tray menu's cached
// check states. This harness drives the repair:
//
//   R012 — autostart is successful only after an EXACT readback of the Run
//     value; open/set/delete/read failures and wrong readbacks are visible
//     mismatches, never "Autostart on". Desired (INI) and applied (registry)
//     may legitimately differ, and that difference is named.
//
//   R021 — one accepted settings snapshot re-applies every piece of derived
//     runtime state (theme, timer, TopMost, low rearm, tray, autostart), and
//     the tray menu — built ONCE — rebinds every static settings check from
//     current state on every Opening, performing zero writes and zero
//     registry mutations while it projects.
//
// The registry boundary is the production seam (AutostartStore); a fake
// store fails exactly one operation at a time, which a real HKCU cannot be
// made to do on demand.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class SettingsConsistencyTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags NPI = BindingFlags.NonPublic | BindingFlags.Instance;
    static Type formType, settingsType;
    static LimisawSettings settings;
    static LimisawForm form;

    // ── the injectable registry backend ───────────────────────────────────
    class FakeStore : AutostartStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        public bool FailOpen, FailSet, FailDelete, FailRead, Sticky;
        public string WrongValue;
        public int Opens, Sets, Deletes, Reads, Mutations;

        public override bool Open(bool writable) { Opens++; return !FailOpen; }
        public override bool SetValue(string name, string value)
        { Sets++; Mutations++; if (FailSet) return false; Values[name] = value; return true; }
        public override bool Delete(string name)
        { Deletes++; Mutations++; if (FailDelete) return false; if (!Sticky) Values.Remove(name); return true; }
        public override bool TryRead(string name, out string value)
        {
            Reads++; value = null;
            if (FailRead) return false;
            if (Values.ContainsKey(name)) value = WrongValue ?? Values[name];
            return true;
        }
        public override void Dispose() { }
    }

    static FakeStore Store;
    static void WireStore(FakeStore store)
    { Store = store; Program.AutostartStoreSource = () => store; }
    static void UnwireStore() { Program.AutostartStoreSource = () => new RegistryAutostartStore(); }

    static string ExpectedCommand()
    {
        return "\"" + Application.ExecutablePath + "\" --minimized";
    }

    static object S(string f) { return settingsType.GetField(f).GetValue(settings); }
    static void SSet(string f, object v) { settingsType.GetField(f).SetValue(settings, v); }
    static object F(string f) { return formType.GetField(f, NP).GetValue(form); }
    static void FSet(string f, object v) { formType.GetField(f, NP).SetValue(form, v); }
    static void FCall(string name, params object[] args)
    { formType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, args); }

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

    static void PressRefresh()
    {
        FCall("RefreshData");
        Wait(() => !(bool)F("Refreshing"), 20000);
        Application.DoEvents();
    }

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
            { "Theme", "goldendefault" },
            { "LowPct", "20" },
            { "TrayMode", "single" },
            { "TrayFill", "4" },
            { "AutoStart", "0" },
        };
    }

    static Program.TrayMenuState state;

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_setcon_" + Guid.NewGuid().ToString("N"));
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

            // The sweep's CLI discovery also reads the REAL machine: the
            // registry user/machine PATH and the installer fallbacks. On a
            // machine with `agy` installed that resolves the real CLI, whose
            // live quota repopulates NotifiedLow during FormAndMenu's refreshes
            // — an external-state leak this settings harness must not own. The
            // seams are the production ones (ExecutableDiscovery), pinned for
            // this process only; process PATH was already cleared above.
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.FallbackFor = delegate { return new string[0]; };

            settingsType = typeof(LimisawSettings);
            formType = typeof(LimisawForm);

            HealthyAutostart();
            FailedAutostart(temp);
            ForeignAutostartPresence(temp);
            FormAndMenu(temp, root);
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            UnwireStore();
            ExecutableDiscovery.GetUserPath = null;
            ExecutableDiscovery.GetMachinePath = null;
            ExecutableDiscovery.FallbackFor = null;
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    // ── group 3: healthy enable/disable, exact command, idempotent ────────
    static void HealthyAutostart()
    {
        Console.WriteLine("== autostart: healthy reconciliation ==");
        settings = new LimisawSettings(Path.GetTempPath());
        settings.Load();

        WireStore(new FakeStore());
        settings.AutoStart = true;
        AutostartResult r = Program.ReconcileAutostart(settings);
        Check("enable is verified: the exact quoted command is in the Run key",
            r.Verified && r.Actual == true && r.Desired,
            "Actual=" + r.Actual);
        Check("...and the command is the exact shape: \"exe\" --minimized",
            Store.Values[Program.AutostartValueName] == ExpectedCommand(),
            Store.Values.ContainsKey(Program.AutostartValueName) ? Store.Values[Program.AutostartValueName] : "(absent)");
        Check("...and it was proven by a readback, not the write call",
            Store.Sets == 1 && Store.Reads >= 1, "sets=" + Store.Sets + " reads=" + Store.Reads);

        settings.AutoStart = false;
        r = Program.ReconcileAutostart(settings);
        Check("disable is verified: the value is proven absent",
            r.Verified && r.Actual == false && Store.Values.Count == 0,
            "Actual=" + r.Actual);

        Store.Values[Program.AutostartValueName] = ExpectedCommand();
        settings.AutoStart = true;
        r = Program.ReconcileAutostart(settings);
        Check("already enabled with the exact value reconciles idempotently",
            r.Verified && r.Actual == true, "");

        Store.Values.Clear();
        settings.AutoStart = false;
        r = Program.ReconcileAutostart(settings);
        Check("already disabled reconciles idempotently",
            r.Verified && r.Actual == false, "");

        // A stale entry with a WRONG command under the desired-on state is
        // repaired to the exact shape, and the repair is proven.
        Store.Values[Program.AutostartValueName] = "C:\\old\\LIMISAW.exe";
        settings.AutoStart = true;
        r = Program.ReconcileAutostart(settings);
        Check("a wrong existing command is repaired and the repair verified",
            r.Verified && Store.Values[Program.AutostartValueName] == ExpectedCommand(), "");
        UnwireStore();
    }

    // ── group 3b: SRC-011:R007 (W2-003) presence vs exact-command state ───
    // The one-boolean readback (`got != null && got == want`) made "a FOREIGN
    // command is in the Run key" and "nothing is in the Run key" the identical
    // observation whenever the desired state was OFF. Both the inspection and
    // the reconciliation then reported verified success while Windows kept
    // launching something. These checks pin presence and equality apart.
    static void ForeignAutostartPresence(string temp)
    {
        Console.WriteLine();
        Console.WriteLine("== autostart: a foreign Run command is NOT an absent one (W2-003) ==");
        settings = new LimisawSettings(temp);
        settings.Load();
        const string foreign = "C:\\somewhere-else\\OtherApp.exe";

        // A. The read-only look, desired OFF, a foreign command present.
        WireStore(new FakeStore());
        Store.Values[Program.AutostartValueName] = foreign;
        settings.AutoStart = false;
        AutostartResult look = Program.InspectAutostart(settings);
        Check("A1. inspection: desired-off + a FOREIGN command is NOT verified",
            !look.Verified && look.Actual == false,
            "verified=" + look.Verified + " actual=" + look.Actual + " reason=" + look.Reason);
        Check("A2. inspection reports presence and equality as separate facts",
            look.Present == true && look.Exact == false,
            "present=" + look.Present + " exact=" + look.Exact);
        Check("A3. inspection says a startup entry is still there, not that all is well",
            look.Reason != null && look.Reason.IndexOf("present", StringComparison.Ordinal) >= 0,
            look.Reason);
        Check("A4. the read-only look still wrote nothing",
            Store.Mutations == 0 && Store.Sets == 0 && Store.Deletes == 0,
            "mutations=" + Store.Mutations);
        UnwireStore();

        // B. Reconciliation, desired OFF, a foreign command that survives the
        //    delete: the sharpest form of the defect, and the RED control.
        WireStore(new FakeStore { Sticky = true, WrongValue = foreign });
        Store.Values[Program.AutostartValueName] = ExpectedCommand();
        settings.AutoStart = false;
        AutostartResult rForeign = Program.ReconcileAutostart(settings);
        Check("B1. reconcile: a surviving FOREIGN command is a mismatch, never a success",
            !rForeign.Verified && rForeign.Actual == false,
            "verified=" + rForeign.Verified + " actual=" + rForeign.Actual + " reason=" + rForeign.Reason);
        Check("B2. reconcile keeps presence and equality separate too",
            rForeign.Present == true && rForeign.Exact == false,
            "present=" + rForeign.Present + " exact=" + rForeign.Exact);
        Check("B3. the reason distinguishes a foreign entry from our own",
            rForeign.Reason.IndexOf("still present", StringComparison.Ordinal) >= 0
            && rForeign.Reason.IndexOf("different", StringComparison.Ordinal) >= 0,
            rForeign.Reason);
        UnwireStore();

        // C. Our own command surviving its delete stays the "still present" case,
        //    and must NOT be relabelled as a foreign entry.
        WireStore(new FakeStore { Sticky = true });
        Store.Values[Program.AutostartValueName] = ExpectedCommand();
        settings.AutoStart = false;
        AutostartResult rOurs = Program.ReconcileAutostart(settings);
        Check("C1. our own surviving command is still present AND exact, and still unverified",
            !rOurs.Verified && rOurs.Present == true && rOurs.Exact == true,
            "present=" + rOurs.Present + " exact=" + rOurs.Exact + " reason=" + rOurs.Reason);
        Check("C2. our own entry is not described as a different one",
            rOurs.Reason.IndexOf("still present", StringComparison.Ordinal) >= 0
            && rOurs.Reason.IndexOf("different", StringComparison.Ordinal) < 0,
            rOurs.Reason);
        UnwireStore();

        // D. Desired ON, absent — the read-only look never writes, so this is
        //    the only path where "desired on and nothing there" is observable.
        //    Presence false, equality false, reason names the missing entry.
        WireStore(new FakeStore());
        settings.AutoStart = true;
        AutostartResult lookOn = Program.InspectAutostart(settings);
        Check("D1. inspection: desired-on with nothing present is unverified with an honest reason",
            !lookOn.Verified && lookOn.Present == false && lookOn.Exact == false
            && lookOn.Reason.IndexOf("not applied", StringComparison.Ordinal) >= 0,
            "present=" + lookOn.Present + " exact=" + lookOn.Exact + " reason=" + lookOn.Reason);
        Check("D2. and the inspection still wrote nothing",
            Store.Mutations == 0, "mutations=" + Store.Mutations);
        UnwireStore();

        // E. The unreadable read is still "nothing may be claimed": both facts
        //    stay null rather than defaulting to false.
        WireStore(new FakeStore { FailRead = true });
        settings.AutoStart = false;
        AutostartResult rUnreadable = Program.ReconcileAutostart(settings);
        Check("E1. an unreadable Run key claims neither presence nor absence",
            !rUnreadable.Verified && rUnreadable.Present == null && rUnreadable.Exact == null
            && rUnreadable.Actual == null,
            "present=" + rUnreadable.Present + " exact=" + rUnreadable.Exact + " actual=" + rUnreadable.Actual);
        UnwireStore();
    }

    // ── group 4: every failure mode, individually injected ────────────────
    static void FailedAutostart(string temp)
    {
        Console.WriteLine();
        Console.WriteLine("== autostart: every failure stays a visible mismatch ==");
        settings = new LimisawSettings(temp);
        settings.Load();

        // The read-only inspection is the menu's Opening look: it must be able
        // to say "mismatch" without writing anything.
        WireStore(new FakeStore());
        settings.AutoStart = true;
        AutostartResult look = Program.InspectAutostart(settings);
        Check("the read-only inspection reports an absent entry as not applied",
            !look.Verified && look.Actual == false
            && Store.Mutations == 0 && Store.Sets == 0 && Store.Deletes == 0,
            "mutations=" + Store.Mutations + ", reason=" + look.Reason);
        UnwireStore();

        foreach (object[] fault in new object[][] {
            new object[] { "open", true, false, false, false, null },
            new object[] { "set", false, true, false, false, null },
            new object[] { "delete", false, false, true, false, null },
            new object[] { "read", false, false, false, true, null },
            new object[] { "wrong readback", false, false, false, false, "C:\\elsewhere\\x.exe" },
        })
        {
            string label = (string)fault[0];
            WireStore(new FakeStore
            {
                FailOpen = (bool)fault[1], FailSet = (bool)fault[2], FailDelete = (bool)fault[3],
                FailRead = (bool)fault[4], WrongValue = (string)fault[5],
            });
            if (label == "delete") Store.Values[Program.AutostartValueName] = ExpectedCommand();
            if (label == "wrong readback") Store.Values[Program.AutostartValueName] = ExpectedCommand();
            // The delete fault is only reachable when disable is desired.
            settings.AutoStart = label != "delete";
            AutostartResult r = Program.ReconcileAutostart(settings);
            Check(label + ": no verified success and a real reason",
                !r.Verified && r.Reason != null && r.Reason.Length > 0, r.Reason);
            if (label == "set" || label == "open")
                Check("..." + label + " failure left the Run key untouched",
                    !Store.Values.ContainsKey(Program.AutostartValueName), "sets=" + Store.Sets);
            if (label == "delete")
                Check("...delete failure left the old entry in place",
                    Store.Values.ContainsKey(Program.AutostartValueName), "");
            if (label == "wrong readback")
                Check("...the written command exists but is NOT trusted",
                    Store.Values[Program.AutostartValueName] == ExpectedCommand() && !r.Verified, "");
            if (label == "read")
                Check("...nothing readable, so nothing is claimed verified",
                    r.Actual == null && !r.Verified, "Actual=" + r.Actual);
            UnwireStore();
        }

        // The value that survives its own delete: desired off, entry present.
        WireStore(new FakeStore { Sticky = true });
        Store.Values[Program.AutostartValueName] = ExpectedCommand();
        settings.AutoStart = false;
        AutostartResult rSticky = Program.ReconcileAutostart(settings);
        Check("an entry that survives its delete is a mismatch, not a success",
            !rSticky.Verified && rSticky.Reason.IndexOf("still present", StringComparison.Ordinal) >= 0, rSticky.Reason);
        UnwireStore();

        // The UI contract, driven through the real form.
        WireStore(new FakeStore { FailSet = true });
        settings.AutoStart = false;
        FormAutostartCases();
        UnwireStore();
    }

    static void FormAutostartCases()
    {
        string ini = settings.IniPath;
        File.WriteAllText(ini, "[limisaw]\r\nAutoStart=0\r\n");
        settings.Load();
        List<Theme> themes = Theme.Load(AppDomain.CurrentDomain.BaseDirectory);
        using (var tray = new NotifyIcon())
        using (Form f = form = new LimisawForm(Path.GetTempPath(), settings, tray, themes))
        {
            object timer = F("RefreshTimer");
            timer.GetType().GetMethod("Stop").Invoke(timer, null);
            Wait(() => !(bool)F("Refreshing"), 60000);
            // The Main wiring, but against the fake store.
            settings.ZcodeReadConfig = false;
            form.AutostartReconciler = () => Program.ReconcileAutostart(settings);

            FCall("ToggleAutostart");
            Check("registry failure: the desired INI state SURVIVES (desired-but-not-applied)",
                (bool)S("AutoStart"), "AutoStart=" + S("AutoStart"));
            Check("...the UI never claims 'Autostart on' without verification",
                Note().IndexOf("Autostart wanted: on", StringComparison.Ordinal) >= 0
                && Note().IndexOf("not", StringComparison.Ordinal) >= 0
                && Note() != "Autostart on", Note());
            Check("...the mismatch stays flagged on the form",
                form.AutostartNotApplied, "");
            Check("...the registry really did not take the value",
                !Store.Values.ContainsKey(Program.AutostartValueName), "");

            // And the healthy path through the same UI:
            Store.FailSet = false;
            FCall("ToggleAutostart");
            Check("a verified toggle says 'Autostart off' plainly",
                Note() == "Autostart off" && !form.AutostartNotApplied
                && !Store.Values.ContainsKey(Program.AutostartValueName), Note());
            FCall("ToggleAutostart");
            Check("...and 'Autostart on' only after the exact readback",
                Note() == "Autostart on" && Store.Values[Program.AutostartValueName] == ExpectedCommand(), Note());

            // INI persistence failing BEFORE the registry is touched: the new
            // registry state must NOT be applied from an undurable desire.
            Store.FailSet = true;
            Store.Values.Clear();
            int mutationsBefore = Store.Mutations;
            FieldInfo hook = settingsType.GetField("WriteHook", NPI);
            var failWriter = new FailOnKey("AutoStart");
            hook.SetValue(settings, Delegate.CreateDelegate(hook.FieldType, failWriter,
                typeof(FailOnKey).GetMethod("Write")));
            FCall("ToggleAutostart");
            hook.SetValue(settings, null);
            Check("a failed INI save applies NO registry state at all",
                Store.Mutations == mutationsBefore && Store.Values.Count == 0,
                "mutations=" + (Store.Mutations - mutationsBefore));
            Check("...the desired flag reverted and the note says why",
                (bool)S("AutoStart")
                && Note().IndexOf("not writable", StringComparison.Ordinal) >= 0, Note());

            // Startup reconciliation reports its failure through the form.
            settings.AutoStart = true;
            form.ReportAutostart(Program.ReconcileAutostart(settings));
            Check("a startup reconcile failure is surfaced on screen, not swallowed",
                Note().IndexOf("Autostart wanted: on", StringComparison.Ordinal) >= 0
                && form.AutostartNotApplied, Note());
        }
    }

    class FailOnKey
    {
        readonly string Fail;
        public FailOnKey(string failKey) { Fail = failKey; }
        public bool Write(string key, string val, string file) { return key != Fail; }
    }

    static string Note() { return (string)F("Note"); }

    static string Keys(System.Collections.IDictionary d)
    {
        var names = new List<string>();
        foreach (object k in d.Keys) names.Add(k.ToString());
        return string.Join(",", names.ToArray());
    }

    // ── groups 5-7: form, external reload, menu projection ────────────────
    static void FormAndMenu(string temp, string root)
    {
        Console.WriteLine();
        Console.WriteLine("== external AutoStart reload reconciles the registry ==");
        string ini = Path.Combine(temp, "LIMISAW.ini");
        File.WriteAllText(ini, ToText(Baseline()));
        settings = new LimisawSettings(temp);
        settings.Load();
        List<Theme> themes = Theme.Load(root);
        int reconciles = 0;
        using (var tray = new NotifyIcon())
        using (Form f = form = new LimisawForm(temp, settings, tray, themes))
        {
            object timer = F("RefreshTimer");
            timer.GetType().GetMethod("Stop").Invoke(timer, null);
            Wait(() => !(bool)F("Refreshing"), 60000);
            form.AutostartReconciler = () => { reconciles++; return Program.ReconcileAutostart(settings); };

            WireStore(new FakeStore());
            Check("the starting point: desired off, registry empty",
                !(bool)S("AutoStart") && Store.Values.Count == 0, "");

            var keys = Baseline(); keys["AutoStart"] = "1";
            WriteIni(ini, keys);
            PressRefresh();
            Check("an external AutoStart=1 is reconciled into an exact verified entry",
                (bool)S("AutoStart") && Store.Values[Program.AutostartValueName] == ExpectedCommand(),
                Store.Values.ContainsKey(Program.AutostartValueName) ? "entry present" : "entry ABSENT");
            Check("...and the form reports a verified match",
                !form.AutostartNotApplied, "");
            Check("...and the reconcile ran exactly once for this accepted snapshot",
                reconciles == 1, "reconciles=" + reconciles);

            keys["AutoStart"] = "0";
            WriteIni(ini, keys);
            PressRefresh();
            Check("an external AutoStart=0 removes the entry and reconciles cleanly",
                !(bool)S("AutoStart") && Store.Values.Count == 0 && !form.AutostartNotApplied, "");

            Store.FailSet = true;
            keys["AutoStart"] = "1";
            WriteIni(ini, keys);
            PressRefresh();
            Check("a refused SetValue keeps desired true in the INI but the registry false",
                (bool)S("AutoStart") && !Store.Values.ContainsKey(Program.AutostartValueName), "");
            Check("...the mismatch survives visibly and no success was claimed",
                form.AutostartNotApplied, "");
            Store.FailSet = false;

            // ── group 7: the live derived state follows the accepted snapshot
            Console.WriteLine();
            Console.WriteLine("== the accepted snapshot re-applies live derived state ==");
            var notified = (System.Collections.IDictionary)F("NotifiedLow");
            notified["codex/home-a/five_hour"] = "x";
            PropertyInfo interval = timer.GetType().GetProperty("Interval");
            keys["Theme"] = "nord";
            keys["RefreshSeconds"] = "120";
            keys["LowPct"] = "40";
            keys["AlwaysOnTop"] = "1";
            keys["ShowUsed"] = "1";
            WriteIni(ini, keys);
            PressRefresh();
            Check("the external interval is re-armed",
                (int)interval.GetValue(timer, null) == 120000, interval.GetValue(timer, null).ToString());
            Check("...the palette follows the external theme",
                Palette.T.Slug == "nord", Palette.T.Slug);
            Check("...TopMost follows AlwaysOnTop",
                form.TopMost, "");
            Check("...an externally moved low threshold re-arms fired alerts",
                notified.Count == 0,
                notified.Count + " remembered: " + string.Join(",", Keys(notified)));
            Check("...ShowUsed flipped the readout (10% left renders as 90 used)",
                FCallShownRem(10) == 90, FCallShownRem(10).ToString());

            // Unchanged reloads must re-apply NOTHING: no reconciles, no timer
            // touch, no note.
            reconciles = 0;
            interval.SetValue(timer, (object)123456, null);
            FSet("Note", "sentinel");
            PressRefresh();
            PressRefresh();
            Check("an unchanged ini re-applies nothing (no reconcile, no timer, no note)",
                reconciles == 0 && (int)interval.GetValue(timer, null) == 123456
                && (string)F("Note") == "sentinel",
                "reconciles=" + reconciles + ", interval=" + interval.GetValue(timer, null)
                + ", note=" + F("Note"));

            // ── group 6: the tray menu rebinds on Opening ──────────────────
            Console.WriteLine();
            Console.WriteLine("== the tray menu rebinds every static check on Opening ==");
            bool shown = false;
            var ctx = Program.BuildMenu(settings, tray, () => form, () => shown = true, () => { }, themes);
            state = (Program.TrayMenuState)ctx.Tag;
            Check("the menu carries its rebind state for the Opening projection",
                state != null && state.ModeMenu != null && state.Auto != null, "");

            // Round 1: mutate + apply, then open the ALREADY-BUILT menu.
            FieldInfo hook = settingsType.GetField("WriteHook", NPI);
            var counting = new CountingWriter();
            hook.SetValue(settings, Delegate.CreateDelegate(hook.FieldType, counting,
                typeof(CountingWriter).GetMethod("Write")));
            Store.Values.Clear(); Store.Mutations = 0;

            settings.TrayMode = "dual";
            settings.TrayFill = 8;
            settings.ThemeSlug = "oled";
            settings.ShowUsed = true;
            settings.NotifyOnReset = false;
            settings.ResetSound = false;
            settings.NotifyLow = false;
            settings.LowSound = false;
            settings.AutoStart = true;
            Store.Values[Program.AutostartValueName] = ExpectedCommand();
            Program.MenuOpening(state);

            Check("Opening rebinds the layout checks from current settings",
                ModeChecked("dual") && !ModeChecked("single"), "single=" + ModeChecked("single"));
            Check("...the fill checks", FillChecked(8) && !FillChecked(4), "");
            Check("...the theme checks", ThemeChecked("oled") && !ThemeChecked("goldendefault"), "");
            Check("...ShowUsed check and text",
                state.Used.Checked && state.Used.Text == "Show Used %", state.Used.Text);
            Check("...the four alert switches",
                !state.Notify.Checked && !state.Chime.Checked && !state.LowBalloon.Checked && !state.LowChime.Checked, "");
            Check("...and Start with Windows shows the verified match",
                state.Auto.Checked && state.Auto.Text == "Start with Windows", state.Auto.Text);
            Check("Opening performed ZERO settings writes and ZERO registry mutations",
                counting.Keys.Count == 0 && Store.Mutations == 0,
                "writes=" + counting.Keys.Count + ", mutations=" + Store.Mutations);
            Check("...while the read-only registry look was allowed to run",
                Store.Opens > 0 && Store.Reads > 0, "opens=" + Store.Opens + " reads=" + Store.Reads);

            // Round 2: another mutation + apply; the SAME menu follows.
            settings.TrayMode = "grid";
            settings.ThemeSlug = "dracula";
            settings.ShowUsed = false;
            settings.NotifyOnReset = true;
            settings.AutoStart = false;
            Store.Values.Clear();
            Program.MenuOpening(state);
            Check("a second mutation round rebinds the same menu instance",
                ModeChecked("grid") && !ModeChecked("dual") && ThemeChecked("dracula")
                && !state.Used.Checked && state.Used.Text == "Show Left %"
                && state.Notify.Checked && !state.Auto.Checked
                && state.Auto.Text == "Start with Windows",
                "auto text=" + state.Auto.Text);
            Check("...still zero writes and zero mutations",
                counting.Keys.Count == 0 && Store.Mutations == 0,
                "writes=" + counting.Keys.Count + ", mutations=" + Store.Mutations);

            // The mismatch projection: desired on, registry refuses to hold it.
            settings.AutoStart = true;
            Store.Values.Clear();
            Program.MenuOpening(state);
            Check("an unverifiable autostart is named in the menu text, not ticked into truth",
                state.Auto.Text.IndexOf("not applied", StringComparison.Ordinal) >= 0, state.Auto.Text);

            // The settings ledger row follows the dirty state.
            settings.AutoStart = false;
            Program.MenuOpening(state);
            Check("the ledger row says saved while the snapshot is durable",
                state.SettingsState.Text.IndexOf("saved", StringComparison.Ordinal) >= 0
                && state.SettingsState.Text.IndexOf("not", StringComparison.Ordinal) < 0,
                state.SettingsState.Text);
            var failWriter = new FailOnKey("Theme");
            hook.SetValue(settings, Delegate.CreateDelegate(hook.FieldType, failWriter,
                typeof(FailOnKey).GetMethod("Write")));
            settings.ThemeSlug = "solarized";
            settings.Save();
            hook.SetValue(settings, null);
            Program.MenuOpening(state);
            Check("...and says the truth after a failed save: unsaved changes pending",
                state.SettingsState.Text.IndexOf("unsaved", StringComparison.OrdinalIgnoreCase) >= 0
                || state.SettingsState.Text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0,
                state.SettingsState.Text);
            hook.SetValue(settings, null);

            // ── T-42 PROFILE C: a resize/move during an unresolved conflict ──
            // The live settings are DIRTY right now (the Theme save just
            // failed and the conflict is open). The incidental geometry save
            // a resize or move performs must land the NEW WindowX/Y/W/H
            // against the durable baseline WITHOUT promoting the failed
            // theme choice — the window may not silently resolve what the
            // user was never asked about.
            string conflictTheme = (string)settingsType.GetField("ThemeSlug").GetValue(settings);
            Check("the fixture: a dirty conflict is open (the theme save just failed)",
                settings.Dirty && conflictTheme == "solarized", conflictTheme);
            // A window the user has never sized carries no durable height:
            // AutoHeight is on and the save writes WindowH=0, so an incidental
            // save (hide to tray, a right-drag move) cannot turn a fitted
            // height into a choice the user never made.
            FieldInfo autoHeight = formType.GetField("AutoHeight", NP);
            formType.GetMethod("SaveWindowGeometry", NP).Invoke(form, null);
            Check("a window still on the auto default saves no durable height",
                (bool)autoHeight.GetValue(form)
                && File.ReadAllText(ini).Contains("WindowH=0"),
                "auto=" + autoHeight.GetValue(form));
            // A real resize gesture retires AutoHeight; from then on the height
            // is the user's and IS durable geometry.
            autoHeight.SetValue(form, false);
            formType.GetMethod("SaveWindowGeometry", NP).Invoke(form, null);
            string iniAfterGeo = File.ReadAllText(ini);
            Check("the resize/move reached the file as durable geometry",
                iniAfterGeo.Contains("WindowW=" + form.ClientSize.Width)
                && iniAfterGeo.Contains("WindowH=" + form.ClientSize.Height)
                && iniAfterGeo.Contains("WindowX=" + form.Location.X)
                && iniAfterGeo.Contains("WindowY=" + form.Location.Y),
                "X=" + form.Location.X + " W=" + form.ClientSize.Width);
            Check("...the conflicted theme choice did NOT silently become durable",
                iniAfterGeo.IndexOf("Theme=solarized", StringComparison.Ordinal) < 0,
                "theme line: " + (iniAfterGeo.IndexOf("Theme=", StringComparison.Ordinal) >= 0
                    ? iniAfterGeo.Substring(iniAfterGeo.IndexOf("Theme=", StringComparison.Ordinal), 24) : "none"));
            Check("...and the conflict is still open in memory",
                settings.Dirty
                && (string)settingsType.GetField("ThemeSlug").GetValue(settings) == "solarized",
                "dirty=" + settings.Dirty);
            // The geometry baseline advanced: a reload now accepts the file
            // whose only difference from the live object is the conflict —
            // and that acceptance is still the USER's act (press Refresh).
            settings.Reload();
            Check("a reload during the still-open conflict retains the dirty choice",
                settings.Dirty
                && (string)settingsType.GetField("ThemeSlug").GetValue(settings) == "solarized",
                "dirty=" + settings.Dirty);

            // The real Opening wiring is pinned, so the direct call above can
            // never drift away from what a user's click actually runs.
            string ui = File.ReadAllText(Path.Combine(SourceRoot(root), "LIMISAW.cs"));
            Check("the Opening lambda routes through the extracted MenuOpening",
                ui.IndexOf("m.Opening += (o, e) => MenuOpening(state);", StringComparison.Ordinal) >= 0, "");
            Check("the 'Open LIMISAW' item still shows the window through its own action",
                shown == false, shown.ToString());
        }
    }

    static string ToText(Dictionary<string, string> keys)
    {
        var sb = new StringBuilder("[limisaw]\r\n");
        foreach (KeyValuePair<string, string> kv in keys)
            sb.Append(kv.Key).Append("=").Append(kv.Value).Append("\r\n");
        return sb.ToString();
    }

    static bool ModeChecked(string value)
    {
        foreach (ToolStripItem c in state.ModeMenu.DropDownItems)
        {
            var m = c as ToolStripMenuItem;
            if (m != null && (string)m.Tag == value) return m.Checked;
        }
        return false;
    }

    static bool FillChecked(int value)
    {
        foreach (ToolStripItem c in state.FillMenu.DropDownItems)
        {
            var m = c as ToolStripMenuItem;
            if (m != null && (int)m.Tag == value) return m.Checked;
        }
        return false;
    }

    static bool ThemeChecked(string slug)
    {
        foreach (ToolStripItem c in state.ThemeMenu.DropDownItems)
        {
            var m = c as ToolStripMenuItem;
            if (m != null && string.Equals((string)m.Tag, slug, StringComparison.OrdinalIgnoreCase)) return m.Checked;
        }
        return false;
    }

    static int FCallShownRem(int remaining)
    {
        return (int)formType.GetMethod("ShownRem", NP).Invoke(form, new object[] { remaining });
    }

    class CountingWriter
    {
        public readonly List<string> Keys = new List<string>();
        public bool Write(string key, string val, string file) { Keys.Add(key); return true; }
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
