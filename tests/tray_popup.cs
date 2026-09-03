using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// The tray hover panel replaced a 63-character plain-text tooltip, so the two
// things that made the tooltip unreadable must not come back:
//
//   * every reading gets its own row with a gauge, not one run-on line;
//   * the row's colours come from the same rules the window uses (a low
//     percentage is danger-coloured, a reading the tray does not draw is dim).
//
// This harness builds the panel from a synthetic fleet, paints it to a bitmap
// and asserts: one row per window plus one header per account, every pixel is a
// palette colour (so a theme swap cannot leak a blended colour in), the panel
// fits on screen, and the gauge of a spent window is NOT drawn in the "full"
// colour. It also pins that the OS tooltip is left empty, since two tooltips
// over one icon is worse than either alone.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:tray_popup.exe -r:System.dll -r:System.Drawing.dll ^
//     -r:System.Windows.Forms.dll tests\tray_popup.cs
//   tray_popup.exe            (exit 0 = all PASS)
public static class TrayPopupTest
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

    static object MakeWindow(Assembly asm, string key, string label, string group,
                             int rem, bool available, string reset, string gated, int minutes)
    {
        Type t = asm.GetType("Limisaw.WindowData");
        object w = Activator.CreateInstance(t);
        t.GetField("Key").SetValue(w, key);
        t.GetField("Base").SetValue(w, key.Split('@')[0]);
        t.GetField("Label").SetValue(w, label);
        t.GetField("Group").SetValue(w, group);
        t.GetField("GroupLabel").SetValue(w, group);
        t.GetField("Available").SetValue(w, available);
        t.GetField("Rem").SetValue(w, rem);
        t.GetField("Reset").SetValue(w, reset);
        t.GetField("GatedBy").SetValue(w, gated);
        t.GetField("DurationMinutes").SetValue(w, minutes);
        return w;
    }

    static object MakeAccount(Assembly asm, string provider, string providerLabel,
                              string name, string plan, string error, object[] windows)
    {
        Type t = asm.GetType("Limisaw.AccountData");
        object a = Activator.CreateInstance(t);
        t.GetField("Provider").SetValue(a, provider);
        t.GetField("ProviderLabel").SetValue(a, providerLabel);
        t.GetField("Name").SetValue(a, name);
        t.GetField("Status").SetValue(a, error == null ? "OK" : "ERROR");
        t.GetField("Plan").SetValue(a, plan);
        t.GetField("Error").SetValue(a, error);
        t.GetField("Ok").SetValue(a, error == null);
        IList list = (IList)t.GetField("Windows").GetValue(a);
        foreach (object w in windows) list.Add(w);
        return a;
    }

    static int Inject(Assembly asm, Type formType, object form)
    {
        IList accounts = (IList)formType.GetField("Accounts", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
        accounts.Clear();
        string soon = DateTime.Now.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ss");
        string later = DateTime.Now.AddDays(3).ToString("yyyy-MM-ddTHH:mm:ss");
        accounts.Add(MakeAccount(asm, "codex", "Codex", "Codex", "plus", null, new object[] {
            MakeWindow(asm, "five_hour", "5h", "", 0, true, soon, "weekly", 300),
            MakeWindow(asm, "weekly", "week", "", 0, true, later, null, 10080) }));
        accounts.Add(MakeAccount(asm, "claude", "Claude Code", "Claude", null, null, new object[] {
            MakeWindow(asm, "five_hour", "5h", "", 7, true, soon, null, 300),
            MakeWindow(asm, "weekly", "week", "", 33, true, later, null, 10080) }));
        // Antigravity: two pools, and the Claude/GPT 5-hour window that the CLI
        // reports as disabled while that pool's weekly limit is spent.
        accounts.Add(MakeAccount(asm, "antigravity", "Antigravity", "Antigravity", null, null, new object[] {
            MakeWindow(asm, "five_hour@gemini_models", "5h", "Gemini Models", 82, true, soon, null, 300),
            MakeWindow(asm, "weekly@gemini_models", "week", "Gemini Models", 63, true, later, null, 10080),
            MakeWindow(asm, "five_hour@claude_and_gpt_models", "5h", "Claude and GPT models", 0, true, null, "weekly@claude_and_gpt_models", 300),
            MakeWindow(asm, "weekly@claude_and_gpt_models", "week", "Claude and GPT models", 0, true, later, null, 10080) }));
        accounts.Add(MakeAccount(asm, "claude", "Claude Code", "Claude2", null,
            "AUTH_REQUIRED: not logged in", new object[0]));
        return 4;
    }

    static Color PalColor(Assembly asm, string name)
    {
        return (Color)asm.GetType("Limisaw.Palette").GetProperty(name).GetValue(null, null);
    }

    // The panel lays its rows out from private consts; the assertion reads the
    // same consts instead of copying the numbers, so a geometry change cannot
    // leave the test quietly checking the wrong pixels.
    static int Pc(Type t, string name)
    {
        return (int)t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
    }

    static Color[] PaletteColors(Assembly asm)
    {
        Type p = asm.GetType("Limisaw.Palette");
        string[] names = { "BG", "SURFACE", "RAISED", "ALT", "BDARK", "BEVEL",
                           "TEXT", "TEXT2", "MUTED", "SUCCESS", "WARNING",
                           "DANGER", "DANGERTXT", "LINK" };
        var list = new List<Color>();
        foreach (string n in names) list.Add((Color)p.GetProperty(n).GetValue(null, null));
        return list.ToArray();
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_popup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assembly asm = Load();
            Type settingsType = asm.GetType("Limisaw.LimisawSettings");
            Type themeType = asm.GetType("Limisaw.Theme");
            Type formType = asm.GetType("Limisaw.LimisawForm");
            Type popupType = asm.GetType("Limisaw.TrayPopup");
            Type rowType = asm.GetType("Limisaw.TrayPopup+Row");

            object settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            object themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { Directory.GetCurrentDirectory() });

            using (var tray = new NotifyIcon())
            using (Form form = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            {
                int accountCount = Inject(asm, formType, form);
                MethodInfo rowsMethod = formType.GetMethod("PopupRows");
                MethodInfo titleMethod = formType.GetMethod("PopupTitle");

                IList rows = (IList)rowsMethod.Invoke(form, null);
                int headers = 0, gauges = 0, dim = 0;
                foreach (object r in rows)
                {
                    if ((bool)rowType.GetField("Header").GetValue(r)) headers++;
                    if ((bool)rowType.GetField("Gauge").GetValue(r)) gauges++;
                    if ((bool)rowType.GetField("Dim").GetValue(r)) dim++;
                }
                Check("one header per account", headers == accountCount, headers + " headers for " + accountCount + " accounts");
                Check("one gauge row per quota window", gauges == 8, gauges + " gauge rows");
                Check("readings the tray does not draw are dimmed, not dropped",
                    dim > 0 && dim < gauges, dim + " of " + gauges + " dimmed");
                Check("an account with only an error still gets a row",
                    rows.Count == headers + gauges + 1, rows.Count + " rows total");

                string title = (string)titleMethod.Invoke(form, null);
                Check("the title states the metric, not just a number",
                    title.StartsWith("LIMISAW") && title.Contains("%"), title);

                // Paint every theme: the panel is only allowed palette colours,
                // so a theme swap cannot leak a blended pixel in.
                MethodInfo applyTheme = formType.GetMethod("ApplyTheme");
                MethodInfo showMethod = popupType.GetMethod("Show", new[] { typeof(string), typeof(List<>).MakeGenericType(rowType), typeof(Point) });
                var themeList = (IList)themes;
                string firstSlug = themeList.Count > 0
                    ? (string)themeType.GetField("Slug").GetValue(themeList[0]) : "";
                using (Form popup = (Form)Activator.CreateInstance(popupType))
                {
                    foreach (object t in themeList)
                    {
                        string slug = (string)themeType.GetField("Slug").GetValue(t);
                        applyTheme.Invoke(form, new object[] { slug });
                        rows = (IList)rowsMethod.Invoke(form, null);

                        popupType.GetField("Rows", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(popup, rows);
                        popupType.GetField("Title", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(popup, titleMethod.Invoke(form, null));
                        Size size = (Size)popupType.GetMethod("Measure", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(popup, null);
                        popup.ClientSize = size;

                        var allowed = new HashSet<int>();
                        foreach (Color c in PaletteColors(asm)) allowed.Add(c.ToArgb());
                        var bad = new Dictionary<int, int>();
                        int dimFilled = -1, dimPct = 0, zeroFilled = -1;
                        using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
                        {
                            using (Graphics g = Graphics.FromImage(bmp))
                                popupType.GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)
                                    .Invoke(popup, new object[] { new PaintEventArgs(g, new Rectangle(Point.Empty, size)) });
                            for (int y = 0; y < bmp.Height; y++)
                                for (int x = 0; x < bmp.Width; x++)
                                {
                                    Color px = bmp.GetPixel(x, y);
                                    if (px.A < 255) continue;
                                    if (!allowed.Contains(px.ToArgb()))
                                    {
                                        if (!bad.ContainsKey(px.ToArgb())) bad[px.ToArgb()] = 0;
                                        bad[px.ToArgb()]++;
                                    }
                                }

                            // A dimmed reading is a real reading: the percent is
                            // true, so the bar must show it. When Dim was folded
                            // into "not available" every dimmed bar drew an empty
                            // track, so the whole panel read as either full or
                            // empty no matter what the numbers said.
                            if (slug == firstSlug)
                            {
                                Color track = PalColor(asm, "BDARK"), edge = PalColor(asm, "BEVEL");
                                Color back = PalColor(asm, "BG");
                                int padX = Pc(popupType, "PadX"), headH = Pc(popupType, "HeadH");
                                int gap = Pc(popupType, "Gap"), rowH = Pc(popupType, "RowH");
                                int gw = Pc(popupType, "GaugeW");
                                int gx = size.Width - padX - gw, ry = headH + gap;
                                foreach (object r in rows)
                                {
                                    bool header = (bool)rowType.GetField("Header").GetValue(r);
                                    if (header) { ry += headH; continue; }
                                    bool avail = (bool)rowType.GetField("Available").GetValue(r);
                                    if ((bool)rowType.GetField("Gauge").GetValue(r))
                                    {
                                        int pct = (int)rowType.GetField("Pct").GetValue(r);
                                        int n = 0;
                                        for (int x = gx; x < gx + gw; x++)
                                        {
                                            Color c = bmp.GetPixel(x, ry + 6);
                                            if (c.ToArgb() != track.ToArgb() && c.ToArgb() != edge.ToArgb()
                                                && c.ToArgb() != back.ToArgb()) n++;
                                        }
                                        if ((bool)rowType.GetField("Dim").GetValue(r) && avail && pct >= 50 && dimFilled < 0)
                                        { dimFilled = n; dimPct = pct; }
                                        if (avail && pct == 0 && zeroFilled < 0) zeroFilled = n;
                                    }
                                    ry += rowH;
                                }
                            }
                        }
                        Check("popup is palette-pure in theme " + slug, bad.Count == 0,
                            bad.Count == 0 ? size.Width + "x" + size.Height
                                : bad.Count + " off-palette colour(s)");
                        Check("popup fits a 1024x768 screen in theme " + slug,
                            size.Width <= 1024 && size.Height <= 768, size.Width + "x" + size.Height);

                        if (slug == firstSlug)
                        {
                            int gw = Pc(popupType, "GaugeW");
                            int want = gw * dimPct / 100 - 2;
                            Check("a dimmed bar is filled to its percent, not left empty",
                                dimFilled >= want,
                                dimPct + "% asked for ~" + want + "px, got " + dimFilled + "px");
                            Check("a spent reading still draws an empty bar",
                                zeroFilled == 0, zeroFilled + "px filled at 0%");
                        }
                    }
                }

                Check("the OS tooltip is not used for the reading list",
                    (bool)formType.GetField("PopupOwnsTooltip").GetValue(form) == false
                        && tray.Text.Length == 0,
                    "tray.Text=\"" + tray.Text + "\" (the panel takes over at runtime)");
            }
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.GetType().Name + ": "
                + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
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
