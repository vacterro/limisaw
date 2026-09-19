using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// PERF-001 (SRC-006:R018): the Settings tab violated the per-paint snapshot
// discipline the tray already obeyed. One Settings paint materialized the
// whole fleet THREE times (OnPaint snapshot + PreviewModel + the explicit
// totalCount walk) and up to SEVEN on a scrollbar-width feedback miss, while
// the height measurement ran the real painter INCLUDING the preview bitmap,
// whose render cannot change the row's fixed 70px height.
//
// The contract this harness holds:
//
//   * ONE AllMetrics per whole Settings paint transaction — first paint at a
//     new width, the scrollbar-feedback case, a repeat paint, every paint of
//     a >=100-value live slider drag alike;
//   * the height measurement performs ZERO preview renders and ZERO metric
//     materializations — a miss is a geometry operation, not a second
//     rendering transaction;
//   * the actual painter renders the preview exactly once, from the snapshot
//     the paint already owns;
//   * and none of the cost work changed any geometry: the measured height is
//     stable, the scrollbar maximum is stable, and hit targets are live at
//     the 360px minimum width and across the two-column breakpoint.
//
// Deterministic seams: AllMetricsCalls, LayoutSnapshots, PreviewRenders.
// No wall-clock benchmarking.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   csc -out:settings_paint_cost.exe -r:System.dll -r:System.Drawing.dll
//       -r:System.Windows.Forms.dll tests\settings_paint_cost.cs
public static class SettingsPaintCostTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Assembly Load()
    {
        string root = Directory.GetCurrentDirectory();
        string exe = Path.Combine(root, "LIMISAW.exe");
        if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
        return Assembly.LoadFrom(Path.GetFullPath(exe));
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static Type formType, settingsType, themeType, windowType, accountType;
    static object settings, themes;
    static object form_var;
    static Form F;

    static int GetInt(string field) { return (int)formType.GetField(field, NP).GetValue(form_var); }
    static void SetInt(string field, int v) { formType.GetField(field, NP).SetValue(form_var, v); }
    static object SField(string name) { return settingsType.GetField(name).GetValue(settings); }
    static void SSet(string name, object v) { settingsType.GetField(name).SetValue(settings, v); }
    static object Call(string method, params object[] args)
    { return formType.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form_var, args); }
    static object PrivCall(string method, params object[] args)
    { return formType.GetMethod(method, NP).Invoke(form_var, args); }

    static object MakeWindow(string key, string label, int rem, string reset, int minutes)
    {
        var w = Activator.CreateInstance(windowType);
        windowType.GetField("Key").SetValue(w, key);
        windowType.GetField("Base").SetValue(w, key.Split('@')[0]);
        windowType.GetField("Label").SetValue(w, label);
        windowType.GetField("Group").SetValue(w, "");
        windowType.GetField("GroupLabel").SetValue(w, "");
        windowType.GetField("Available").SetValue(w, true);
        windowType.GetField("Rem").SetValue(w, rem);
        windowType.GetField("Reset").SetValue(w, reset);
        windowType.GetField("DurationMinutes").SetValue(w, minutes);
        return w;
    }

    static IList AccountsList;

    static void MakeAccount(string provider, string providerLabel, string name, object[] windows)
    {
        var a = Activator.CreateInstance(accountType);
        accountType.GetField("Provider").SetValue(a, provider);
        accountType.GetField("ProviderLabel").SetValue(a, providerLabel);
        accountType.GetField("Name").SetValue(a, name);
        accountType.GetField("Status").SetValue(a, "OK");
        accountType.GetField("Ok").SetValue(a, true);
        IList list = (IList)accountType.GetField("Windows").GetValue(a);
        foreach (object w in windows) list.Add(w);
        if (AccountsList != null) AccountsList.Add(a);
    }

    static string Iso(double hoursAhead)
    {
        return DateTime.UtcNow.AddHours(hoursAhead).ToString("yyyy-MM-ddTHH:mm:ss");
    }

    static void PaintAt(int w, int h)
    {
        F.ClientSize = new Size(w, h);
        Call("FitWindow");
        using (var bmp = new Bitmap(Math.Max(1, F.Width), Math.Max(1, F.Height), PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            var args = new PaintEventArgs(g, new Rectangle(Point.Empty, F.ClientSize));
            formType.GetMethod("OnPaint", NP).Invoke(form_var, new object[] { args });
        }
    }

    static void Mouse(string handler, int x, int y)
    {
        formType.GetMethod(handler, NP).Invoke(form_var,
            new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_setcost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // The constructor probes for real. An empty environment makes the
            // start-up sweep find nothing and settle at once, so no worker is
            // publishing state underneath the measurements.
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(temp, "no-codex"));
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string key in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(key, "");

            Assembly asm = Load();
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            themeType = asm.GetType("Limisaw.Theme");
            formType = asm.GetType("Limisaw.LimisawForm");
            windowType = asm.GetType("Limisaw.WindowData");
            accountType = asm.GetType("Limisaw.AccountData");

            settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { Directory.GetCurrentDirectory() });

            using (var tray = new NotifyIcon())
            using (Form form = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            {
                form_var = form;
                F = form;
                AccountsList = (IList)formType.GetField("Accounts", NP).GetValue(form_var);

                string soon = Iso(4);
                // 125 four-window accounts: a 500+ reading fleet, so every
                // AllMetrics is a full materialization the counts cannot hide.
                int fleet = 125;
                for (int i = 0; i < fleet; i++)
                    MakeAccount("codex", "Codex", "SetF" + i.ToString("000"), new object[] {
                        MakeWindow("five_hour", "5h", 10 + i % 80, soon, 300),
                        MakeWindow("weekly", "week", 40, Iso(72), 10080),
                        MakeWindow("monthly", "month", 77, Iso(600), 43200),
                        MakeWindow("five_hour", "5h", 5, soon, 300) });
                int readings = 0;
                foreach (object a in AccountsList)
                    readings += ((IList)accountType.GetField("Windows").GetValue(a)).Count;
                Check("the fixture carries 500+ readings", readings >= 500, readings + " readings");

                // The Settings tab with both chimes on: the volume rail is
                // live, so the drag patterns below exercise the real path.
                SSet("ResetSound", true);
                SSet("NotifyLow", true);
                SSet("SoundVolume", 0);
                SetInt("Tab", 2);   // TabSettings
                // Held down for the whole run: a sweep starting mid-drag would
                // repaint and re-publish underneath the measurements.
                formType.GetField("Refreshing", NP).SetValue(form_var, true);
                Call("ShowTab", 2);

                // ── the first paint at a NEW width ─────────────────────────
                int all0 = GetInt("AllMetricsCalls"), pv0 = GetInt("PreviewRenders"), snap0 = GetInt("LayoutSnapshots");
                PaintAt(700, 620);
                int allDelta = GetInt("AllMetricsCalls") - all0;
                int pvDelta = GetInt("PreviewRenders") - pv0;
                int snaps = GetInt("LayoutSnapshots") - snap0;
                Check("the first Settings paint materializes the fleet EXACTLY ONCE",
                    allDelta == 1, "AllMetricsCalls delta=" + allDelta);
                Check("...and renders the preview exactly once, from the snapshot it owns",
                    pvDelta == 1, "PreviewRenders delta=" + pvDelta);
                Check("...as ONE paint transaction", snaps == 1, "LayoutSnapshots delta=" + snaps);

                // ── a repeat paint at the SAME width ───────────────────────
                int all1 = GetInt("AllMetricsCalls"), pv1 = GetInt("PreviewRenders");
                PaintAt(700, 620);
                Check("a second paint at the same width is still EXACTLY ONE AllMetrics",
                    GetInt("AllMetricsCalls") - all1 == 1, "delta=" + (GetInt("AllMetricsCalls") - all1));
                Check("...with exactly one preview render",
                    GetInt("PreviewRenders") - pv1 == 1, "delta=" + (GetInt("PreviewRenders") - pv1));

                // ── the height measurement is a GEOMETRY operation ─────────
                // 640px was never painted or measured: SettingsPanelHeight
                // must run the real painter with Measuring=true, which may do
                // NO preview rendering and NO metric materialization.
                int all2 = GetInt("AllMetricsCalls"), pv2 = GetInt("PreviewRenders");
                int measured = (int)PrivCall("SettingsPanelHeight", 640);
                Check("a height measurement performs ZERO AllMetrics",
                    GetInt("AllMetricsCalls") - all2 == 0, "delta=" + (GetInt("AllMetricsCalls") - all2));
                Check("a height measurement performs ZERO preview renders",
                    GetInt("PreviewRenders") - pv2 == 0, "delta=" + (GetInt("PreviewRenders") - pv2));
                Check("the measured height is a real number", measured > 0, "h=" + measured);

                // ── the scrollbar-width feedback case ──────────────────────
                // A narrow width forces ResolveScroll to measure the reduced
                // body width too — the case that used to cost up to SEVEN
                // materializations in one transaction.
                int all3 = GetInt("AllMetricsCalls"), pv3 = GetInt("PreviewRenders");
                PaintAt(360, 500);
                Check("the first paint at the 360px minimum (scrollbar feedback) is ONE AllMetrics",
                    GetInt("AllMetricsCalls") - all3 == 1, "delta=" + (GetInt("AllMetricsCalls") - all3));
                Check("...with exactly one preview render",
                    GetInt("PreviewRenders") - pv3 == 1, "delta=" + (GetInt("PreviewRenders") - pv3));
                int maxScroll360 = (int)formType.GetField("MaxScroll", NP).GetValue(form_var);
                Check("the 360px tab actually scrolls", maxScroll360 > 0, "max=" + maxScroll360);

                // ── geometry is unchanged by the cost work ─────────────────
                // Measure the SAME width twice with the cache cleared: the
                // measure==paint parity must be exact, and a repeat must be
                // deterministic.
                SetInt("SettingsMeasuredW", -1);
                int h700a = (int)PrivCall("SettingsPanelHeight", 700);
                SetInt("SettingsMeasuredW", -1);
                int h700b = (int)PrivCall("SettingsPanelHeight", 700);
                Check("the measured panel height is deterministic",
                    h700a == h700b && h700a > 0, "h=" + h700a + " vs " + h700b);
                SetInt("SettingsMeasuredW", -1);
                int h360 = (int)PrivCall("SettingsPanelHeight", 360);
                Check("the 360px height differs from the 700px one (real reflow)",
                    h360 > 0 && h360 != h700a, "360=" + h360 + " 700=" + h700a);

                // Hit targets are live at both widths after a real paint.
                PaintAt(360, 500);
                var buttons360 = (IList)formType.GetField("Buttons", NP).GetValue(form_var);
                Check("hit targets are registered at the 360px minimum",
                    buttons360.Count > 0, buttons360.Count + " buttons");
                PaintAt(700, 620);
                var buttons700 = (IList)formType.GetField("Buttons", NP).GetValue(form_var);
                Check("hit targets are registered at the two-column breakpoint",
                    buttons700.Count > 0, buttons700.Count + " buttons");
                var hints700 = (IList)formType.GetField("HintZones", NP).GetValue(form_var);
                Check("hover explanations are registered on Settings",
                    hints700.Count > 0, hints700.Count + " hints");

                // ── the live slider drag: one AllMetrics per paint ─────────
                // The audit's own event shape: >=100 distinct live values in
                // ONE drag. The product repaints on every CHANGED pointer
                // value (SetVolumeFromX -> Refresh); the harness performs
                // that repaint explicitly (the suite's standard OnPaint
                // drive) and asserts each repaint is exactly ONE
                // materialization and ONE preview render — never
                // three-per-paint, never a measurement render.
                Rectangle rail = (Rectangle)formType.GetField("VolRailVolume", NP).GetValue(form_var);
                Check("the volume rail is live and wide enough to drag across",
                    rail.Width >= 60, "rail " + rail.Width + "px at 700px");
                int mid = rail.Y + 6;
                Mouse("OnMouseDown", rail.X, mid);
                Check("the drag is armed", (string)formType.GetField("VolDrag", NP).GetValue(form_var) == "volume",
                    "VolDrag=" + formType.GetField("VolDrag", NP).GetValue(form_var));
                int distinct = 0, lastVal = -1, lastX = rail.X, repaints = 0;
                var seenVals = new List<int>();
                int allD0 = GetInt("AllMetricsCalls"), pvD0 = GetInt("PreviewRenders"),
                    snapD0 = GetInt("LayoutSnapshots");
                for (int px = rail.X; px <= rail.Right; px++)
                {
                    Mouse("OnMouseMove", px, mid);
                    int v = (int)SField("SoundVolume");
                    if (v != lastVal)
                    {
                        if (!seenVals.Contains(v)) distinct++;
                        lastVal = v;
                        repaints++;
                        PaintAt(700, 620);
                    }
                    lastX = px;
                }
                int allUD = GetInt("AllMetricsCalls"), snapUD = GetInt("LayoutSnapshots"),
                    pvUD = GetInt("PreviewRenders");
                Mouse("OnMouseUp", lastX, mid);
                Check("the drag produced >=100 distinct live values",
                    distinct >= 100, distinct + " distinct values");
                Check("drag paints are ONE AllMetrics each, never three",
                    allUD - allD0 == snapUD - snapD0 && allUD - allD0 == repaints && allUD - allD0 >= 100,
                    "AllMetrics=" + (allUD - allD0) + ", paints=" + (snapUD - snapD0) + ", values=" + distinct);
                Check("drag paints render the preview exactly once per paint",
                    pvUD - pvD0 == snapUD - snapD0,
                    "previews=" + (pvUD - pvD0) + ", paints=" + (snapUD - snapD0));

                // The low threshold's own drag at the same discipline.
                SSet("LowPct", 20);
                rail = (Rectangle)formType.GetField("VolRailLow", NP).GetValue(form_var);
                Check("the low rail is live", rail.Width >= 60, "rail " + rail.Width + "px");
                mid = rail.Y + 6;
                allD0 = GetInt("AllMetricsCalls"); snapD0 = GetInt("LayoutSnapshots");
                Mouse("OnMouseDown", rail.X + rail.Width / 2, mid);
                int lowMoves = 0; lastX = rail.X + rail.Width / 2; distinct = 0; seenVals.Clear();
                for (int px = rail.X + rail.Width / 2; px <= rail.Right; px += 2)
                {
                    Mouse("OnMouseMove", px, mid);
                    lowMoves++;
                    int v = (int)SField("LowPct");
                    if (!seenVals.Contains(v)) { distinct++; seenVals.Add(v); PaintAt(700, 620); }
                    lastX = px;
                }
                Check("the low-threshold drag paints one AllMetrics per paint",
                    GetInt("AllMetricsCalls") - allD0 == GetInt("LayoutSnapshots") - snapD0,
                    "AllMetrics=" + (GetInt("AllMetricsCalls") - allD0) + ", paints=" + (GetInt("LayoutSnapshots") - snapD0));
                Mouse("OnMouseUp", lastX, mid);
                Check("...and it moved the threshold", lowMoves > 10 && distinct > 1, lowMoves + " moves, " + distinct + " distinct");
            }
        }
        catch (Exception ex)
        {
            string detail = ex.GetType().Name + ": "
                + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            Exception walk = ex;
            while (walk.InnerException != null) walk = walk.InnerException;
            if (walk.StackTrace != null)
            {
                string[] frames = walk.StackTrace.Split('\n');
                for (int i = 0; i < frames.Length && i < 5; i++) detail += " @ " + frames[i].Trim();
            }
            Check("harness", false, detail);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
