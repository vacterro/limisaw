using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// CORE-001, second half: a tray reading's ID must be derived from the account's
// STABLE key, never from its display label.
//
// T-19 gave AccountData a stable `Key` (codex/<digest of the canonical home>)
// and taught the migration helpers to resolve saved state to it — but the place
// that MINTS a tray reading's id kept building `provider/Name/window`. The two
// halves therefore never met:
//
//   * MigrateMetricId resolved "codex/Codex/five_hour" to "codex/<id>/five_hour",
//     and no live metric ever had that id, so a pinned Codex reading silently
//     fell back to "lowest" and a saved order/hide did nothing;
//   * two homes both called "Codex" minted the SAME id for their 5h windows, so
//     one pin, one hide and one order slot covered both accounts;
//   * DisambiguateLabels renames a duplicate home for display (" · .codex-b"),
//     which means the label — and therefore the whole id — CHANGED the moment a
//     second home appeared, invalidating state saved a minute earlier.
//
// This harness drives the real form and pins:
//
//   * MetricId is the account key + window key, for every provider;
//   * two same-labelled Codex homes get distinct ids, per window;
//   * a pin/order/hide survives the arrival of a second same-labelled home;
//   * a saved LEGACY (label-derived) id still resolves — pin, order and hide —
//     while exactly one account matches, and is dropped when ambiguous;
//   * the hover panel marks the same reading the tray actually draws (the panel
//     built its own id string, so it could disagree with the tray);
//   * ToggleItem rewrites the stored hide list in live ids rather than piling a
//     migrated copy next to the legacy one;
//   * the source cannot go back: no label-derived id expression survives.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class MetricIdentity
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;

    static Assembly asm;
    static Type accType, winType, formType, settingsType, metricType;
    static object form, settings;

    static object Get(string name) { return formType.GetField(name, NP).GetValue(form); }
    static object SGet(string name) { return settingsType.GetField(name).GetValue(settings); }
    static void SSet(string name, object v) { settingsType.GetField(name).SetValue(settings, v); }

    static object Window(string key, string label, int rem)
    {
        object w = Activator.CreateInstance(winType);
        winType.GetField("Key").SetValue(w, key);
        winType.GetField("Base").SetValue(w, key);
        winType.GetField("Label").SetValue(w, label);
        winType.GetField("Group").SetValue(w, "");
        winType.GetField("GroupLabel").SetValue(w, "");
        winType.GetField("Available").SetValue(w, true);
        winType.GetField("Rem").SetValue(w, rem);
        winType.GetField("Reset").SetValue(w, DateTime.Now.AddHours(3).ToString("yyyy-MM-ddTHH:mm:ss"));
        winType.GetField("DurationMinutes").SetValue(w, key == "five_hour" ? 300 : 10080);
        return w;
    }

    // `label` is display text only; `sourceId` is identity. Two homes may share
    // the label — that is the whole point of this harness.
    static object Account(string provider, string label, string sourceId, params object[] windows)
    {
        object a = Activator.CreateInstance(accType);
        accType.GetField("Provider").SetValue(a, provider);
        accType.GetField("ProviderLabel").SetValue(a, provider == "codex" ? "Codex" : provider);
        accType.GetField("Name").SetValue(a, label);
        accType.GetField("Status").SetValue(a, "OK");
        accType.GetField("Ok").SetValue(a, true);
        accType.GetField("SourceId").SetValue(a, sourceId ?? "");
        if (sourceId != null)
            accType.GetField("ResetHome").SetValue(a, @"C:\x\.codex-" + sourceId);
        IList list = (IList)accType.GetField("Windows").GetValue(a);
        foreach (object w in windows) list.Add(w);
        return a;
    }

    static string Key(object account)
    {
        return (string)accType.GetProperty("Key").GetValue(account, null);
    }

    static void Fleet(params object[] accounts)
    {
        IList live = (IList)Get("Accounts");
        live.Clear();
        foreach (object a in accounts) live.Add(a);
    }

    static List<string> MetricIds()
    {
        var ids = new List<string>();
        foreach (object m in (IList)formType.GetMethod("AllMetrics").Invoke(form, null))
            ids.Add((string)metricType.GetField("Id").GetValue(m));
        return ids;
    }

    static List<string> TrayIds()
    {
        var ids = new List<string>();
        foreach (object m in (IList)formType.GetMethod("TrayMetrics").Invoke(form, null))
            ids.Add((string)metricType.GetField("Id").GetValue(m));
        return ids;
    }

    // The pinned reading as the tray resolves it: label plus the number, so a
    // silent fallback to "lowest" is visible rather than inferred.
    static void Pinned(out int value, out bool available, out string label)
    {
        object[] args = new object[] { 0, false, "" };
        MethodInfo m = null;
        foreach (MethodInfo cand in formType.GetMethods(NP))
            if (cand.Name == "GetTrayMetric" && cand.GetParameters().Length == 3) m = cand;
        m.Invoke(form, args);
        value = (int)args[0]; available = (bool)args[1]; label = (string)args[2];
    }

    static string Join(List<string> ids) { return string.Join("|", ids.ToArray()); }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_mid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            accType = asm.GetType("Limisaw.AccountData");
            winType = asm.GetType("Limisaw.WindowData");
            formType = asm.GetType("Limisaw.LimisawForm");
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            metricType = asm.GetType("Limisaw.Metric");
            Type themeType = asm.GetType("Limisaw.Theme");

            // Nothing discoverable: every account below is one this harness put
            // there, so no real sweep can shuffle the fleet mid-assertion.
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(temp, "no-codex"));
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string k in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(k, null);

            settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            object themes = themeType.GetMethod("Load", PS).Invoke(null, new object[] { root });

            using (var tray = new NotifyIcon())
            using (Form f = (Form)Activator.CreateInstance(formType,
                new object[] { temp, settings, tray, themes }))
            {
                form = f;
                Settle();

                Shape();
                Duplicates();
                SavedState();
                LegacyReads();
                PanelAgreement();
            }

            Source(root);
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally { try { Directory.Delete(temp, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " of " + checks + ")");
        return fails == 0 ? 0 : 1;
    }

    // The constructor starts a real sweep. Stop the timer and let it finish, then
    // hold Refreshing so nothing re-enters and replaces the fleet.
    static void Settle()
    {
        object timer = Get("RefreshTimer");
        timer.GetType().GetMethod("Stop").Invoke(timer, null);
        for (int i = 0; i < 1200 && (bool)Get("Refreshing"); i++)
        { Application.DoEvents(); Thread.Sleep(25); }
        formType.GetField("Refreshing", NP).SetValue(form, true);
    }

    // ── the id's shape ──────────────────────────────────────────────────────
    static void Shape()
    {
        Console.WriteLine("== a tray id is the account KEY plus the window ==");
        object codex = Account("codex", "Codex", "aaaa1111",
            Window("five_hour", "5h", 40), Window("weekly", "week", 70));
        object claude = Account("claude", "Claude", null, Window("weekly", "week", 55));
        Fleet(codex, claude);

        List<string> ids = MetricIds();
        Check("a Codex reading is keyed by its source id, not its label",
            ids.Contains("codex/aaaa1111/five_hour") && ids.Contains("codex/aaaa1111/weekly"),
            Join(ids));
        Check("no id carries the display label",
            !Join(ids).Contains("codex/Codex/"), Join(ids));
        Check("a provider without stable ids keeps the provider/name shape",
            ids.Contains("claude/Claude/weekly"), Join(ids));

        // The id must be exactly key + "/" + window: the migration splits on the
        // LAST separator, so any other shape would break the round trip.
        object w = ((IList)accType.GetField("Windows").GetValue(codex))[0];
        string minted = (string)formType.GetMethod("MetricId", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { codex, w });
        Check("MetricId is the account key + / + the window key",
            minted == Key(codex) + "/five_hour", minted);
    }

    // ── two homes, one label ────────────────────────────────────────────────
    static void Duplicates()
    {
        Console.WriteLine();
        Console.WriteLine("== two 'Codex' homes are two readings, not one ==");
        // Exactly what DisambiguateLabels produces once a second home appears:
        // the LABEL changes, the identity does not.
        object a = Account("codex", "Codex · .codex-a", "aaaa1111", Window("five_hour", "5h", 40));
        object b = Account("codex", "Codex · .codex-b", "bbbb2222", Window("five_hour", "5h", 8));
        Fleet(a, b);

        List<string> ids = MetricIds();
        Check("each home mints its own reading id",
            ids.Count == 2 && ids[0] != ids[1], Join(ids));
        Check("neither id contains the disambiguated label",
            !Join(ids).Contains(".codex-"), Join(ids));

        // The pin was saved when only home A existed, i.e. against A's stable id.
        SSet("TrayMetric", "codex/aaaa1111/five_hour");
        int value; bool available; string label;
        Pinned(out value, out available, out label);
        Check("a pin saved before the second home still reads home A",
            available && value == 40, "value=" + value + "%, label=" + label);

        // And it is A that is marked, never B.
        SSet("TrayHidden", "codex/bbbb2222/five_hour");
        SSet("TrayItems", "");
        Check("hiding one home does not hide the other",
            Join(TrayIds()) == "codex/aaaa1111/five_hour", Join(TrayIds()));
        SSet("TrayHidden", "");
        SSet("TrayMetric", "lowest");
    }

    // ── saved state survives a fleet change ─────────────────────────────────
    static void SavedState()
    {
        Console.WriteLine();
        Console.WriteLine("== saved order/hide/pin survive a second same-named home ==");
        object a = Account("codex", "Codex", "aaaa1111",
            Window("five_hour", "5h", 40), Window("weekly", "week", 70));
        Fleet(a);

        // The user pins and orders while ONE home exists.
        SSet("TrayMetric", "codex/aaaa1111/weekly");
        SSet("TrayItems", "codex/aaaa1111/weekly|codex/aaaa1111/five_hour");
        SSet("TrayHidden", "");
        SSet("TrayMax", 9);
        Check("the saved order is honoured with one home",
            Join(TrayIds()) == "codex/aaaa1111/weekly|codex/aaaa1111/five_hour", Join(TrayIds()));

        // A second home appears. Home A is RELABELLED by DisambiguateLabels.
        object relabelledA = Account("codex", "Codex · .codex-a", "aaaa1111",
            Window("five_hour", "5h", 40), Window("weekly", "week", 70));
        object b = Account("codex", "Codex · .codex-b", "bbbb2222",
            Window("five_hour", "5h", 8));
        Fleet(relabelledA, b);

        Check("the relabelled home keeps the user's order",
            Join(TrayIds()).StartsWith("codex/aaaa1111/weekly|codex/aaaa1111/five_hour"),
            Join(TrayIds()));
        Check("the new home is appended, visible by default",
            Join(TrayIds()).EndsWith("codex/bbbb2222/five_hour"), Join(TrayIds()));

        int value; bool available; string label;
        Pinned(out value, out available, out label);
        Check("the pin still resolves after the relabel",
            available && value == 70, "value=" + value + "%, label=" + label);

        SSet("TrayMetric", "lowest");
        SSet("TrayItems", "");
    }

    // ── an ini written before stable identity ───────────────────────────────
    static void LegacyReads()
    {
        Console.WriteLine();
        Console.WriteLine("== an ini saved before stable ids still points somewhere ==");
        object a = Account("codex", "Codex", "aaaa1111",
            Window("five_hour", "5h", 40), Window("weekly", "week", 70));
        Fleet(a);

        SSet("TrayItems", "");
        SSet("TrayHidden", "");
        SSet("TrayMetric", "codex/Codex/weekly");     // the legacy shape
        int value; bool available; string label;
        Pinned(out value, out available, out label);
        Check("a legacy pin resolves to the one matching account",
            available && value == 70, "value=" + value + "%, label=" + label);

        SSet("TrayMetric", "lowest");
        SSet("TrayItems", "codex/Codex/weekly|codex/Codex/five_hour");
        Check("a legacy order resolves to live readings",
            Join(TrayIds()) == "codex/aaaa1111/weekly|codex/aaaa1111/five_hour", Join(TrayIds()));

        SSet("TrayItems", "");
        SSet("TrayHidden", "codex/Codex/five_hour");
        Check("a legacy hide still hides its reading",
            Join(TrayIds()) == "codex/aaaa1111/weekly", Join(TrayIds()));

        // Unhiding through the UI rewrites the list in LIVE ids: a migrated copy
        // sitting beside the legacy one would leave the reading hidden forever.
        formType.GetMethod("ToggleItem", NP).Invoke(form, new object[] { "codex/aaaa1111/five_hour" });
        Check("toggling a legacy-hidden reading actually shows it",
            Join(TrayIds()).Contains("codex/aaaa1111/five_hour"), Join(TrayIds()));
        Check("and the stored hide list is rewritten in live ids",
            ((string)SGet("TrayHidden")).IndexOf("codex/Codex/", StringComparison.Ordinal) < 0,
            "TrayHidden=\"" + SGet("TrayHidden") + "\"");

        // Two homes make the old label ambiguous: it names neither, rather than
        // silently naming whichever sorts first.
        object relabelledA = Account("codex", "Codex · .codex-a", "aaaa1111", Window("five_hour", "5h", 40));
        object b = Account("codex", "Codex · .codex-b", "bbbb2222", Window("five_hour", "5h", 8));
        Fleet(relabelledA, b);
        SSet("TrayHidden", "");
        SSet("TrayItems", "");
        SSet("TrayMetric", "codex/Codex/five_hour");
        Pinned(out value, out available, out label);
        // Home A is 40%, home B is 8%: an ambiguous legacy id that silently
        // resolved to "whichever sorts first" would read 40. Falling back to
        // lowest reads 8, which is also the honest answer.
        Check("an ambiguous legacy pin falls back to lowest instead of guessing a home",
            available && value == 8, "value=" + value + "%, label=" + label);
        SSet("TrayMetric", "lowest");
    }

    // ── the hover panel and the tray agree ──────────────────────────────────
    static void PanelAgreement()
    {
        Console.WriteLine();
        Console.WriteLine("== the hover panel marks what the tray really draws ==");
        object a = Account("codex", "Codex · .codex-a", "aaaa1111", Window("five_hour", "5h", 40));
        object b = Account("codex", "Codex · .codex-b", "bbbb2222", Window("five_hour", "5h", 8));
        Fleet(a, b);
        SSet("TrayItems", "");
        SSet("TrayHidden", "codex/bbbb2222/five_hour");
        SSet("TrayMax", 9);

        Type rowType = asm.GetType("Limisaw.TrayPopup+Row");
        IList rows = (IList)formType.GetMethod("PopupRows").Invoke(form, null);
        // Gauge rows only: the header rows carry account titles, not readings.
        var drawn = new List<bool>();
        foreach (object r in rows)
        {
            if (!(bool)rowType.GetField("Gauge").GetValue(r)) continue;
            drawn.Add(!(bool)rowType.GetField("Dim").GetValue(r));
        }
        Check("the panel lists both readings",
            drawn.Count == 2, drawn.Count + " gauge row(s)");
        Check("exactly the tray's own reading is marked as drawn",
            drawn.Count == 2 && drawn[0] && !drawn[1],
            "drawn=[" + string.Join(",", new[] {
                drawn.Count > 0 ? drawn[0].ToString() : "?",
                drawn.Count > 1 ? drawn[1].ToString() : "?" }) + "]");
        SSet("TrayHidden", "");
    }

    // ── the label-derived id cannot come back ───────────────────────────────
    static void Source(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== the label-derived id cannot come back ==");
        string ui = File.ReadAllText(Path.Combine(SourceRoot(root), "LIMISAW.cs"));

        Check("no id is built from Provider + Name",
            ui.IndexOf("a.Provider + \"/\" + a.Name + \"/\"", StringComparison.Ordinal) < 0, "");
        Check("one mint site, used by both the metric list and the panel",
            Count(ui, "public static string MetricId(") == 1
            && Count(ui, "MetricId(a, w)") == 2,
            Count(ui, "MetricId(a, w)") + " call site(s)");
        Check("the migration resolves through the account key",
            ui.IndexOf("string MigrateAccountKey(string key)", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (a.LegacyKey != key) continue;", StringComparison.Ordinal) >= 0, "");
        Check("the hide list is read through the migration everywhere",
            Count(ui, "HiddenMetricIds()") == 4
            && Count(ui, "Settings.HiddenItems()") == 1,
            Count(ui, "HiddenMetricIds()") + " migrated read(s), "
            + Count(ui, "Settings.HiddenItems()") + " raw");
    }

    static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
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
