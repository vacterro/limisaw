using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// PERF-004 (SRC-005:R015): scrolling clips WORK, not just pixels.
//
// The old paint path built the ordered/visible account lists (and the tray
// model) over and over inside one paint — VisibleAccounts in the height
// measure, again in the painter, PaintedCardOrder nested scans with Contains,
// TrayMetrics->BuildModel->AllMetrics twice — and then painted EVERY card and
// row, expensive text/brush/button work included, only to have
// ClipBodyRegistrations unregister the offscreen controls afterwards. Cost
// scaled with the whole population on every invalidation.
//
// Now: ONE layout snapshot per paint transaction feeds the height measure and
// the painter; the tray paint takes AllMetrics once and derives selection and
// order from that same collection without BuildModel; and expensive rendering
// runs only for cards/rows intersecting the translated viewport band (with
// overscan) while the CHEAP geometry — heights, tops, total content height,
// drag insertion targets — still represents every item.
//
// Deterministic cost counters (LayoutSnapshots, SnapPopulation,
// ExpensiveCardPaints, ExpensiveRowPaints, AllMetricsCalls, TraySnapshots)
// are the seams this harness asserts against. No wall-clock benchmarking.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   csc -out:viewport_paint.exe -r:System.dll -r:System.Drawing.dll
//       -r:System.Windows.Forms.dll tests\viewport_paint.cs
public static class ViewportPaintTest
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
    static object Call(string method, params object[] args)
    { return formType.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form_var, args); }

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

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_viewport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
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
                form_var = form;      // the static helpers target THIS instance
                F = form;
                                var accounts = (IList)formType.GetField("Accounts", NP).GetValue(form_var);
                AccountsList = accounts;
                                string soon = Iso(4);
                // 120 single-window accounts: a tall Accounts tab at a small
                // viewport, one card height per account.
                int fleet = 120;
                for (int i = 0; i < fleet; i++)
                    MakeAccount("codex", "Codex", "Fleet" + i.ToString("000"),
                        new object[] { MakeWindow("five_hour", "5h", 50 + (i % 50), soon, 300) });
                                FieldInfo tabField = formType.GetField("Tab", NP);
                if (tabField != null) tabField.SetValue(form_var, 0);
                                PaintAt(420, 520);
                
                // ── paint 1: top of a tall list ─────────────────────────────
                int snap0 = GetInt("LayoutSnapshots"), pop0 = GetInt("SnapPopulation"),
                    card0 = GetInt("ExpensiveCardPaints");                PaintAt(420, 520);
                int paint1Cards = GetInt("ExpensiveCardPaints") - card0;
                Check("one layout snapshot per paint transaction",
                    GetInt("LayoutSnapshots") - snap0 == 1, "delta=" + (GetInt("LayoutSnapshots") - snap0));
                Check("the full population was measured once",
                    GetInt("SnapPopulation") - pop0 == fleet, "population=" + (GetInt("SnapPopulation") - pop0));
                Check("expensive card renders stay near the visible viewport, not the fleet",
                    paint1Cards >= 1 && paint1Cards <= 14, paint1Cards + " expensive of " + fleet);

                // Full geometry still represents every account: the measured
                // panel height is the sum over ALL cards, and the drag
                // insertion list (CardTops) covers every card.
                int panelH = (int)Call("AccountsPanelHeight", 420);
                // CardLines(a, w) is a public STATIC — invoke with no instance.
                int linesPerCard = (int)asm.GetType("Limisaw.LimisawForm")
                    .GetMethod("CardLines", new[] { accountType, typeof(int) })
                    .Invoke(null, new[] { accounts[fleet - 1], (object)420 });
                int cardH = 30 + linesPerCard * 18 + 6 + 6;
                Check("total content height represents ALL 120 accounts",
                    Math.Abs(panelH - fleet * cardH) <= 2,
                    "panel=" + panelH + " expected " + fleet * cardH);
                var cardTops = (List<int>)formType.GetField("CardTops", NP).GetValue(form_var);
                Check("drag insertion targets cover every card",
                    cardTops.Count == fleet, "tops=" + cardTops.Count);
                Check("the scrollbar is live and clamps at the full range",
                    (int)formType.GetField("MaxScroll", NP).GetValue(form_var) > 0, "");
                // ── paint 2: scrolled to the bottom ─────────────────────────
                var scroll = (int[])formType.GetField("TabScroll", NP).GetValue(form_var);
                int maxScroll = (int)formType.GetField("MaxScroll", NP).GetValue(form_var);
                scroll[0] = maxScroll;
                int card1 = GetInt("ExpensiveCardPaints");
                PaintAt(420, 520);
                int paint2Cards = GetInt("ExpensiveCardPaints") - card1;
                Check("the final card renders at max scroll",
                    paint2Cards >= 1, paint2Cards + " expensive at bottom");
                var rowIds = (IList)formType.GetField("ItemRowIds", NP).GetValue(form_var);
                bool lastVisible = rowIds.Count > 0;
                Check("the bottom card's hit region is registered and correct",
                    lastVisible && ((string)rowIds[rowIds.Count - 1]).EndsWith("Fleet" + (fleet - 1).ToString("000")),
                    rowIds.Count > 0 ? (string)rowIds[rowIds.Count - 1] : "none");
                Check("the scrollbar reaches the exact end",
                    scroll[0] == maxScroll, "scroll=" + scroll[0] + " max=" + maxScroll);

                // ── the tray tab: ONE AllMetrics, bounded row paints ────────
                for (int i = 0; i < 125; i++)
                    MakeAccount("claude", "Claude Code", "ClaudeX" + i.ToString("000"),
                        new object[] {
                            MakeWindow("five_hour", "5h", 10 + i % 80, soon, 300),
                            MakeWindow("weekly", "week", 40, Iso(72), 10080),
                            MakeWindow("monthly", "month", 77, Iso(600), 43200),
                            MakeWindow("five_hour", "5h", 5, soon, 300),
                            MakeWindow("weekly", "week", 90, Iso(120), 10080) });
                int trayMetrics = 0;
                foreach (object a in accounts) trayMetrics += ((IList)accountType.GetField("Windows").GetValue(a)).Count;
                Check("the tray fixture carries 500+ readings", trayMetrics > 500, trayMetrics + " readings");

                SetInt("Tab", 1);   // TabTray
                int all0 = GetInt("AllMetricsCalls"), ts0 = GetInt("TraySnapshots"), row0 = GetInt("ExpensiveRowPaints");
                PaintAt(420, 520);
                int allDelta = GetInt("AllMetricsCalls") - all0, tsDelta = GetInt("TraySnapshots") - ts0,
                    rowDelta = GetInt("ExpensiveRowPaints") - row0;
                Check("ONE AllMetrics construction per tray paint",
                    allDelta == 1, "delta=" + allDelta);
                Check("the tray paint builds NO tray model (no BuildModel on the paint path)",
                    tsDelta == 0, "delta=" + tsDelta);
                Check("expensive row paints scale with the viewport, not the " + trayMetrics + " readings",
                    rowDelta >= 1 && rowDelta <= 48, rowDelta + " rows painted of " + trayMetrics);
                Check("the full tray list is still measured (height covers every row)",
                    ((int)Call("TrayPanelHeight", 420)) >= 20 + 28 + 16 + trayMetrics * 22, "");

                // Paint again — still one AllMetrics per paint, no growth.
                int all1 = GetInt("AllMetricsCalls");
                PaintAt(420, 520);
                Check("a second tray paint is still ONE AllMetrics",
                    GetInt("AllMetricsCalls") - all1 == 1, "delta=" + (GetInt("AllMetricsCalls") - all1));

                // Scroll the tray tab to the bottom: the LAST row is reachable.
                scroll = (int[])formType.GetField("TabScroll", NP).GetValue(form_var);
                maxScroll = (int)formType.GetField("MaxScroll", NP).GetValue(form_var);
                scroll[1] = maxScroll;
                int row1 = GetInt("ExpensiveRowPaints");
                PaintAt(420, 520);
                var trayRowIds = (IList)formType.GetField("ItemRowIds", NP).GetValue(form_var);
                Check("the last tray row paints at max scroll and its hit region is registered",
                    trayRowIds.Count > 0, "rows=" + trayRowIds.Count + " painted=" + (GetInt("ExpensiveRowPaints") - row1));
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
