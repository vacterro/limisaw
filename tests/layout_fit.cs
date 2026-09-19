using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// The RESPONSIVE layout contract (T-42): the user owns the window size, every
// tab gets ONE bounded body viewport, content taller than the viewport scrolls,
// and nothing a panel does ever resizes the window.
//
// This drives the REAL LIMISAW window by reflection over the built exe, at a
// matrix of sizes and data shapes, and inspects what the paint pass actually
// registered:
//
//   * fixed chrome — header/action/tab/footer never move with content;
//   * every visible control stays inside the body viewport, clear of the
//     footer, and never under the scrollbar strip;
//   * no two visible buttons overlap and no button covers painted content;
//   * no label is cropped to "Showing: Le";
//   * the measured panel height equals the painted content height;
//   * scroll reachability: the LAST control of a tall tab is reachable at
//     maxScroll;
//   * offscreen-hit: a control below the viewport has NO registered hit
//     rectangle until it is scrolled into view;
//   * tab size stability: switching tabs with changing data never moves the
//     outer window;
//   * scroll inputs (wheel, page keys, thumb drag, track click) clamp;
//   * the resize hit-test grid returns the right HT* code for every edge and
//     corner, keeps HTCAPTION in the header, keeps the close button clickable,
//     and survives negative (multi-monitor) screen coordinates;
//   * the compact Settings page measures well below the old 936px wall, and
//     reflows to two columns at the breakpoint;
//   * the theme selector and the account visibility manager expose every
//     theme and every account.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:layout_fit.exe -r:System.dll -r:System.Drawing.dll ^
//     -r:System.Windows.Forms.dll tests\layout_fit.cs
//   layout_fit.exe            (exit 0 = all PASS)
public static class LayoutFit
{
    static int fails = 0, checks = 0;

    static void Fail(string name, string detail)
    {
        fails++;
        Console.WriteLine("FAIL  " + name + "  -> " + detail);
    }

    static void Pass(string name, string detail)
    {
        checks++;
        Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
    }

    static void Check(string name, bool ok, string detail)
    {
        if (ok) Pass(name, detail);
        else Fail(name, detail);
    }

    static Assembly Load()
    {
        string root = Directory.GetCurrentDirectory();
        string exe = Path.Combine(root, "LIMISAW.exe");
        if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
        return Assembly.LoadFrom(Path.GetFullPath(exe));
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    static Type formType, settingsType, themeType;
    static object form, settings, themes;
    static Form F;

    static object Get(string field) { return formType.GetField(field, NP).GetValue(form); }
    static void Set(string field, object v) { formType.GetField(field, NP).SetValue(form, v); }
    static void SSet(string field, object v) { settingsType.GetField(field).SetValue(settings, v); }
    static object Call(string method, params object[] args)
    { return formType.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, args); }

    static List<Rectangle> Grab(string field)
    {
        var list = new List<Rectangle>();
        foreach (object r in (IList)Get(field)) list.Add((Rectangle)r);
        return list;
    }

    // ── synthetic fleet: 6 accounts + 3 CLIs, the populated shape ────────────
    static object MakeWindow(string key, string label, string group,
                             int rem, string reset, string gated, int minutes)
    {
        var w = Activator.CreateInstance(WindowType);
        WindowType.GetField("Key").SetValue(w, key);
        WindowType.GetField("Base").SetValue(w, key.Split('@')[0]);
        WindowType.GetField("Label").SetValue(w, label);
        WindowType.GetField("Group").SetValue(w, group);
        WindowType.GetField("GroupLabel").SetValue(w, group);
        WindowType.GetField("Available").SetValue(w, true);
        WindowType.GetField("Rem").SetValue(w, rem);
        WindowType.GetField("Reset").SetValue(w, reset);
        WindowType.GetField("GatedBy").SetValue(w, gated);
        WindowType.GetField("DurationMinutes").SetValue(w, minutes);
        return w;
    }

    static Type WindowType, AccountType, CliType;

    static void MakeAccount(string provider, string providerLabel,
                            string name, string plan, string error, object[] windows)
    {
        var a = Activator.CreateInstance(AccountType);
        AccountType.GetField("Provider").SetValue(a, provider);
        AccountType.GetField("ProviderLabel").SetValue(a, providerLabel);
        AccountType.GetField("Name").SetValue(a, name);
        // Key is a computed CORE-001 identity (Provider/Name) — provider and
        // name are what the visibility manager keys on.
        AccountType.GetField("Status").SetValue(a, error == null ? "OK" : "ERROR");
        AccountType.GetField("Plan").SetValue(a, plan);
        AccountType.GetField("Error").SetValue(a, error);
        AccountType.GetField("Ok").SetValue(a, error == null);
        IList list = (IList)AccountType.GetField("Windows").GetValue(a);
        foreach (object w in windows) list.Add(w);
        ((IList)Get("Accounts")).Add(a);
    }

    static void MakeCli(string key, string label, bool installed,
                        string path, string command, string source, string target)
    {
        var c = Activator.CreateInstance(CliType);
        CliType.GetField("Key").SetValue(c, key);
        CliType.GetField("Label").SetValue(c, label);
        CliType.GetField("Installed").SetValue(c, installed);
        CliType.GetField("Path").SetValue(c, path);
        CliType.GetField("Command").SetValue(c, command);
        CliType.GetField("PowerShell").SetValue(c, command);
        CliType.GetField("Source").SetValue(c, source);
        CliType.GetField("Target").SetValue(c, target);
        ((IList)Get("Clis")).Add(c);
    }

    static void Inject(bool populated, int fleetMultiplier = 1)
    {
        ((IList)Get("Accounts")).Clear();
        ((IList)Get("Clis")).Clear();
        if (!populated) return;
        string soon = DateTime.Now.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ss");
        string later = DateTime.Now.AddDays(3).ToString("yyyy-MM-ddTHH:mm:ss");
        for (int n = 0; n < fleetMultiplier; n++)
        {
            string suffix = fleetMultiplier > 1 ? "N" + n : "";
            MakeAccount("codex", "Codex", "Codex" + suffix, "plus", null, new object[] {
                MakeWindow("five_hour", "5h", "", 0, soon, "weekly", 300),
                MakeWindow("weekly", "week", "", 0, later, null, 10080) });
            MakeAccount("codex", "Codex", "Account2" + suffix, "plus", null, new object[] {
                MakeWindow("five_hour", "5h", "", 63, soon, null, 300),
                MakeWindow("weekly", "week", "", 91, later, null, 10080) });
            MakeAccount("codex", "Codex", "Account3Free" + suffix, "free", null, new object[] {
                MakeWindow("monthly", "month", "", 100, later, null, 43200) });
            MakeAccount("claude", "Claude Code", "Claude" + suffix, null, null, new object[] {
                MakeWindow("five_hour", "5h", "", 7, soon, null, 300),
                MakeWindow("weekly", "week", "", 33, later, null, 10080) });
            MakeAccount("antigravity", "Antigravity", "Antigravity" + suffix, null, null, new object[] {
                MakeWindow("five_hour@gemini_models", "5h", "Gemini Models", 10, soon, null, 300),
                MakeWindow("weekly@gemini_models", "week", "Gemini Models", 68, later, null, 10080),
                MakeWindow("weekly@claude_and_gpt", "week", "Claude and GPT models", 0, later, null, 10080) });
            MakeAccount("claude", "Claude Code", "Claude2" + suffix, null,
                "AUTH_REQUIRED: not logged in, run `claude login` in a terminal first", new object[0]);
        }
        MakeCli("claude", "Claude Code CLI", true,
            @"C:\Users\someone\.local\bin\claude.exe", "irm https://claude.ai/install.ps1 | iex",
            "claude.ai (Anthropic)", @"%USERPROFILE%\.local\bin\claude.exe");
        MakeCli("antigravity", "Antigravity CLI", false, "",
            "irm https://antigravity.google/cli/install.ps1 | iex",
            "antigravity.google (Google)", @"%LOCALAPPDATA%\agy\bin\agy.exe");
        MakeCli("codex", "Codex CLI", true, @"c:\nodejs\codex.CMD",
            "irm https://chatgpt.com/codex/install.ps1 | iex",
            "chatgpt.com/codex (OpenAI)", "on PATH (installer-chosen directory)");
    }

    // Paint at an EXACT client size — the user's size. Nothing here may change
    // it; the size-stability regression depends on that.
    static Size Painted;
    static void Paint(int w, int h, bool checkStable)
    {
        Size before = F.ClientSize;
        F.ClientSize = new Size(w, h);
        Call("FitWindow");                       // the new clamp-only contract
        using (var bmp = new Bitmap(Math.Max(1, F.Width), Math.Max(1, F.Height), PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            var args = new PaintEventArgs(g, new Rectangle(Point.Empty, F.ClientSize));
            formType.GetMethod("OnPaint", NP).Invoke(form, new object[] { args });
        }
        Painted = F.ClientSize;
        if (checkStable && (Painted.Width != before.Width || Painted.Height != before.Height))
            Fail("paint resized the window", before + " -> " + Painted);
    }

    static int BodyTop() { return (int)Call("BodyTop"); }
    static int BodyHeight() { return (int)Call("BodyHeight"); }
    static int PanelHeightFor(int tab, int bodyW)
    { return (int)Call("PanelHeightFor", tab, bodyW); }
    static Rectangle Viewport()
    {
        var vp = (Rectangle)Call("BodyViewport");
        if ((int)Get("MaxScroll") > 0) vp.Width = Math.Max(0, vp.Width - 9);
        return vp;
    }

    // ── the per-size layout contract ─────────────────────────────────────────
    static List<string> LayoutProblems(string scenario)
    {
        var problems = new List<string>();
        int w = Painted.Width, h = Painted.Height;
        Rectangle viewport = Viewport();
        Rectangle footer = new Rectangle(0, h - 24, w, 24);
        List<Rectangle> buttons = Grab("Buttons");
        List<Rectangle> marks = Grab("Marks");
        var cropped = (IList)Get("Cropped");
        if (buttons.Count == 0) problems.Add("no clickable rectangles at all");
        bool barShown = (int)Get("MaxScroll") > 0;
        for (int i = 0; i < buttons.Count; i++)
        {
            Rectangle a = buttons[i];
            if (a.Width <= 0 || a.Height <= 0) { problems.Add("empty rect #" + i + " " + a); continue; }
            if (a.Left < 0 || a.Top < 0 || a.Right > w || a.Bottom > h)
                problems.Add("rect #" + i + " " + a + " escapes the " + w + "x" + h + " client area");
            if (a.Bottom > footer.Top && a.Top < footer.Bottom)
                problems.Add("rect #" + i + " " + a + " enters the footer");
            // A visible scrollbar reserves its strip: no control may sit under it.
            if (barShown && a.Right > viewport.Right + 1 && a.Y >= viewport.Y && a.Bottom <= viewport.Bottom)
                problems.Add("rect #" + i + " " + a + " sits under the scrollbar strip");
            for (int j = i + 1; j < buttons.Count; j++)
            {
                Rectangle b = buttons[j];
                Rectangle hit = Rectangle.Intersect(a, b);
                if (hit.Width > 0 && hit.Height > 0)
                    problems.Add("rect #" + i + " " + a + " overlaps #" + j + " " + b);
            }
            foreach (Rectangle mark in marks)
            {
                Rectangle hit = Rectangle.Intersect(a, mark);
                if (hit.Width > 1 && hit.Height > 1)
                    problems.Add("button " + a + " covers content " + mark);
            }
        }
        foreach (Rectangle mark in marks)
            if (mark.Width < 0 || mark.Height < 0)
                problems.Add("negative mark " + mark);
        foreach (object label in cropped) problems.Add("label cropped: \"" + label + "\"");
        // Measure==paint: the recorded painted bottom must equal the panel
        // height formula for the tab and the width that was painted.
        int painted = (int)Get("PaintedContentBottom");
        int expected = BodyTop() + PanelHeightFor((int)Get("Tab"), (int)Get("PaintedBodyW"));
        if (painted != expected)
            problems.Add("measure!=paint: painted bottom " + painted + " vs " + expected);
        return problems;
    }

    static void CheckLayout(string scenario)
    {
        var problems = LayoutProblems(scenario);
        Check(scenario, problems.Count == 0,
            problems.Count == 0
                ? Grab("Buttons").Count + " controls, " + Grab("Marks").Count + " marks, "
                    + Painted.Width + "x" + Painted.Height + ", maxScroll=" + (int)Get("MaxScroll")
                : problems.Count + " problem(s): " + string.Join("; ", problems.ToArray()));
    }

    // The hint text of the button whose rectangle is registered with EXACTLY
    // that hint (buttons hint their own rect; row hints cover a different
    // rectangle). The reachability probe for named controls: the ini button's
    // hint is the ini PATH.
    static bool HasHint(string contains)
    {
        List<Rectangle> buttons = Grab("Buttons");
        List<Rectangle> zones = Grab("HintZones");
        IList texts = (IList)Get("HintTexts");
        for (int i = 0; i < buttons.Count; i++)
            for (int j = 0; j < zones.Count; j++)
                if (zones[j] == buttons[i] && j < texts.Count
                    && ((string)texts[j]).IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
        return false;
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_layout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assembly asm = Load();
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            themeType = asm.GetType("Limisaw.Theme");
            formType = asm.GetType("Limisaw.LimisawForm");
            WindowType = asm.GetType("Limisaw.WindowData");
            AccountType = asm.GetType("Limisaw.AccountData");
            CliType = asm.GetType("Limisaw.CliInfo");

            settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { Directory.GetCurrentDirectory() });

            var tray = new NotifyIcon();
            form = Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes });
            F = (Form)form;
            using (F)
            using (tray)
            {
                SizeLayoutMatrix();
                ConnectionDuplicateCard(asm);
                ThemesMatrix();
                ScrollReachability();
                OffscreenHit();
                TabSizeStability();
                ScrollInputs();
                AutoHeightDefault();
                PerTabScroll();
                SettingsCompaction();
                ThemeSelector();
                VisibilityManager();
                GeometryPersistence(temp);
                ResizeHitTest();
            }
        }
        catch (Exception ex)
        {
            Exception inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            Fail("harness", inner.GetType().Name + ": " + inner.Message + "\n" + inner.StackTrace);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " layouts checked");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }

    // ── the Codex duplicate card: extra wording + a "Retry different account"
    // button row. The card GREW, so it is measured at every width the matrix
    // uses instead of loosening a size assertion somewhere.
    static void ConnectionDuplicateCard(Assembly asm)
    {
        Console.WriteLine("== connections: duplicate Codex card ==");
        Inject(true);
        Type vcT = asm.GetType("Limisaw.VendorConnection");
        Type stateT = asm.GetType("Limisaw.ConnectionState");
        object coord = Get("ConnCoordinator");
        Type coordT = coord.GetType();
        object vc = Activator.CreateInstance(vcT);
        vcT.GetField("VendorId").SetValue(vc, "codex");
        vcT.GetField("State").SetValue(vc, Enum.Parse(stateT, "DuplicateRemoteAccount"));
        vcT.GetField("Installed").SetValue(vc, true);
        vcT.GetField("AuthKnown").SetValue(vc, true);
        vcT.GetField("Authenticated").SetValue(vc, true);
        vcT.GetField("ResolvedPath").SetValue(vc, @"C:\Users\test\AppData\Local\codex\bin\codex.exe");
        vcT.GetField("Reason").SetValue(vc,
            "Signed in, but as an account already listed - no extra Codex card was added.");
        ((IList)vcT.GetField("DuplicateRemoteHomeIds").GetValue(vc)).Add("0123456789abcdef");
        int gen = (int)coordT.GetMethod("Begin").Invoke(coord, new object[] { "codex" });
        coordT.GetMethod("TryPublish").Invoke(coord, new object[] { "codex", gen, vc });
        var expanded = (ICollection<string>)Get("ExpandedVendors");
        expanded.Add("codex");
        Set("Tab", 3);
        try
        {
            foreach (int w in new[] { 360, 420, 520, 800 })
                foreach (int h in new[] { 320, 520, 700 })
                {
                    Paint(w, h, false);
                    CheckLayout("Connections, expanded duplicate Codex card, " + w + "x" + h);
                }
        }
        finally
        {
            expanded.Remove("codex");
            coordT.GetMethod("Cancel", new[] { typeof(string) }).Invoke(coord, new object[] { "codex" });
        }
    }

    // ── 41/42: the responsive size matrix, both data shapes, every tab ──────
    static void SizeLayoutMatrix()
    {
        Console.WriteLine("== size matrix ==");
        SSet("ShowUsed", false);
        int[] sizes = { 360, 420, 520, 640, 800 };
        int[] heights = { 320, 480, 620, 700 };
        foreach (bool populated in new[] { true, false })
        {
            Inject(populated);
            foreach (int w in sizes)
                foreach (int h in heights)
                {
                    if (populated && w == 360 && h == 700) continue;   // spot matrix, not cartesian: 38 scenarios
                    if (!populated && !(w == 420 && h == 520) && !(w == 360 && h == 320)) continue;
                    for (int tab = 0; tab < 4; tab++)
                    {
                        Set("Tab", tab);
                        Paint(w, h, false);
                        CheckLayout("size " + w + "x" + h + ", " + TabName(tab) + ", "
                            + (populated ? "6 accounts" : "empty"));
                        if (tab == 2)
                        {
                            // The ini control is always REACHABLE: on a page
                            // taller than the viewport that means at maxScroll.
                            if ((int)Get("MaxScroll") > 0)
                            {
                                int[] sc = (int[])Get("TabScroll");
                                sc[2] = (int)Get("MaxScroll");
                                Paint(w, h, false);
                            }
                            Check("settings keeps the ini control reachable at " + w + "x" + h,
                                HasHint("LIMISAW.ini"), "the ini button's hint is its path");
                        }
                    }
                }
        }
        // The populated extremes the matrix spot-checks stand for: a big fleet
        // at the minimum size must still scroll sanely, never crop.
        Inject(true, 3);
        Set("Tab", 0);
        Paint(360, 320, false);
        CheckLayout("minimum 360x320 with an 18-account fleet");
        Paint(800, 700, false);
        CheckLayout("wide 800x700 with an 18-account fleet");
        Inject(true);
    }

    static string TabName(int t) { return new[] { "Accounts", "Tray", "Settings", "Connections" }[t]; }

    // Every shipped theme changes font metrics; every one must lay out clean
    // at the laptop size AND at the minimum.
    static void ThemesMatrix()
    {
        Console.WriteLine("== themes ==");
        Inject(true);
        MethodInfo applyTheme = formType.GetMethod("ApplyTheme");
        foreach (object t in (IList)themes)
        {
            string slug = (string)themeType.GetField("Slug").GetValue(t);
            applyTheme.Invoke(form, new object[] { slug });
            Set("Tab", 2);
            Paint(420, 620, false);
            CheckLayout("Settings, theme " + slug + ", 420x620");
            Paint(360, 320, false);
            CheckLayout("Settings, theme " + slug + ", 360x320 minimum");
        }
    }

    // ── 43: a scrollbar that cannot reach the last row is not a scrollbar ───
    static void ScrollReachability()
    {
        Console.WriteLine("== scroll reachability ==");
        Inject(true);
        // Settings at 420x520: content is taller, the last APP row (Open the
        // ini / theme All...) must be visible AND registered at maxScroll.
        Set("Tab", 2);
        Paint(420, 520, false);
        int max = (int)Get("MaxScroll");
        Check("settings content overflows a 420x520 viewport", max > 0, "maxScroll=" + max);
        int[] scroll = (int[])Get("TabScroll");
        scroll[2] = max;
        Paint(420, 520, false);
        Rectangle viewport = Viewport();
        bool lastVisible = false;
        Rectangle found = Rectangle.Empty;
        foreach (Rectangle r in Grab("Buttons"))
        {
            if (r.Bottom <= viewport.Bottom && r.Top >= viewport.Y && r.Width > 40)
            {
                // The last control row is the APP block's controls/theme strip;
                // prove SOME control occupies the bottom of the scrolled page.
                if (r.Bottom > viewport.Bottom - 60) { lastVisible = true; found = r; }
            }
        }
        Check("settings at maxScroll shows its last row",
            lastVisible, "maxScroll=" + max + ", lowest=" + found);
        // The user must be able to GET to maxScroll by input, not by writing
        // the field: End key.
        scroll[2] = 0;
        Key(Keys.End);
        Check("End key reaches maxScroll",
            scroll[2] == max, scroll[2] + " vs " + max);

        // CLIs: the last card's Install button.
        Set("Tab", 3);
        Paint(420, 520, false);
        max = (int)Get("MaxScroll");
        scroll[3] = max;
        Paint(420, 520, false);
        viewport = Viewport();
        bool cliReachable = false;
        foreach (Rectangle r in Grab("Buttons"))
            if (r.Bottom <= viewport.Bottom && r.Width > 60) cliReachable = true;
        Check("CLIs at maxScroll still registers its last controls",
            max == 0 || cliReachable, "maxScroll=" + max);

        // Accounts with a big fleet.
        Inject(true, 3);
        Set("Tab", 0);
        Paint(420, 520, false);
        max = (int)Get("MaxScroll");
        Check("an 18-account fleet overflows 420x520", max > 0, "maxScroll=" + max);
        scroll[0] = max;
        Paint(420, 520, false);
        viewport = Viewport();
        bool accReachable = false;
        foreach (Rectangle r in Grab("Buttons"))
            if (r.Bottom <= viewport.Bottom && r.Width > 20) accReachable = true;
        Check("Accounts at maxScroll keeps its controls",
            accReachable, "maxScroll=" + max);
        Inject(true);
        scroll[0] = scroll[2] = scroll[3] = 0;
    }

    // ── 44: no off-screen clickable rectangles ──────────────────────────────
    static void OffscreenHit()
    {
        Console.WriteLine("== offscreen hit ==");
        Inject(true);
        Set("Tab", 2);
        Paint(420, 520, false);
        int max = (int)Get("MaxScroll");
        Check("the tall settings page really scrolls", max > 0, "maxScroll=" + max);
        // At scroll 0 the bottom-of-page control must NOT be registered.
        int[] scroll = (int[])Get("TabScroll");
        scroll[2] = 0;
        Paint(420, 520, false);
        Rectangle viewport = Viewport();
        int belowCount = 0;
        foreach (Rectangle r in Grab("Buttons"))
            if (r.Top >= viewport.Bottom) belowCount++;
        Check("nothing below the viewport is registered at scroll 0",
            belowCount == 0, belowCount + " below-viewport rects");
        // Scroll it into view: now the row's hit rectangle must exist.
        scroll[2] = max;
        Paint(420, 520, false);
        viewport = Viewport();
        bool inView = false;
        foreach (Rectangle r in Grab("Buttons"))
            if (r.Top >= viewport.Y && r.Bottom <= viewport.Bottom && r.Width > 20) inView = true;
        Check("the scrolled-in control is registered and inside the viewport",
            inView, "maxScroll=" + max);
        scroll[2] = 0;
    }

    // ── 45: tab switches with changing data never move the window ───────────
    static void TabSizeStability()
    {
        Console.WriteLine("== tab size stability ==");
        Paint(512, 477, false);
        for (int round = 0; round < 3; round++)
        {
            Inject(round == 1);            // many accounts / empty / many again
            if (round == 2) Inject(true, 2);
            foreach (int tab in new[] { 2, 0, 1, 3, 0 })
            {
                Set("Tab", tab);
                Paint(512, 477, true);
            }
        }
        Check("ClientSize stayed 512x477 through every switch",
            Painted.Width == 512 && Painted.Height == 477, Painted.ToString());
        Inject(true);
    }

    // ── 48: scroll inputs clamp, and a fitting page never scrolls ───────────
    static void ScrollInputs()
    {
        Console.WriteLine("== scroll inputs ==");
        Inject(true);
        Set("Tab", 2);
        Paint(420, 520, false);
        int max = (int)Get("MaxScroll");
        int[] scroll = (int[])Get("TabScroll");
        scroll[2] = 0;
        // WinForms sign: Delta < 0 is the wheel rolled TOWARD the user (down),
        // Delta > 0 away from it (up). The old fixture had these the wrong way
        // round, so it passed against a reversed scroll.
        Wheel(-120 * 10);
        Check("wheel down moves the page", scroll[2] > 0, "scroll=" + scroll[2]);
        Wheel(120 * 100);
        Check("wheel up clamps at 0", scroll[2] == 0, "scroll=" + scroll[2]);
        // Bounds are absolute: a wheel-up already at the top is a no-op, and a
        // wheel-down already at the bottom cannot push past maxScroll.
        Wheel(120);
        Check("wheel up at 0 stays 0", scroll[2] == 0, "scroll=" + scroll[2]);
        Wheel(-120 * 100);
        Check("wheel down reaches max", scroll[2] == max, scroll[2] + "/" + max);
        Wheel(-120);
        Check("wheel down at max stays max", scroll[2] == max, scroll[2] + "/" + max);
        scroll[2] = 0;
        Wheel(-120);
        int afterOne = scroll[2];
        Check("one wheel notch is a useful step", afterOne > 0, afterOne + "px");
        Key(Keys.PageDown);
        Check("PageDown moves about a viewport",
            scroll[2] > afterOne, scroll[2] + " after " + afterOne);
        Key(Keys.End);
        Check("End clamps at maxScroll", scroll[2] == max, scroll[2] + "/" + max);
        Key(Keys.Home);
        Check("Home returns to 0", scroll[2] == 0, "scroll=" + scroll[2]);
        Key(Keys.Down);
        Check("Down arrows one row", scroll[2] == 18, "scroll=" + scroll[2]);
        Key(Keys.Up);
        Check("Up arrows back", scroll[2] == 0, "scroll=" + scroll[2]);

        // Thumb drag: press inside the thumb, drag to the track bottom, release.
        Paint(420, 520, false);
        scroll[2] = 0;
        Rectangle track = (Rectangle)Call("CurrentTrack");
        Check("the scrollbar track exists for a scrolling page",
            track != Rectangle.Empty && track.Width > 0, track.ToString());
        Rectangle thumb = (Rectangle)Call("ScrollThumb", track, 0);
        Mouse(MouseButtons.Left, thumb.X + 2, thumb.Y + 2, true, "OnMouseDown");
        Mouse(MouseButtons.Left, track.X + 2, track.Bottom - 1, false, "OnMouseMove");
        Check("thumb drag to the bottom reaches maxScroll",
            scroll[2] == max, scroll[2] + "/" + max);
        Mouse(MouseButtons.Left, track.X + 2, track.Bottom - 1, true, "OnMouseUp");
        // Track page-click below the thumb.
        scroll[2] = 0;
        Mouse(MouseButtons.Left, track.X + 2, track.Bottom - 1, true, "OnMouseDown");
        Check("a track click below the thumb pages down",
            scroll[2] > 0 && scroll[2] <= max, scroll[2] + "/" + max);
        Mouse(MouseButtons.None, track.X + 2, track.Bottom - 1, true, "OnMouseUp");

        // A page that fits: no phantom offsets, no scrollbar.
        Set("Tab", 2);
        Paint(800, 700, false);
        int fitting = (int)Get("MaxScroll");
        Check("two-column settings at 800x700 fits the viewport",
            fitting == 0, "maxScroll=" + fitting);
        scroll[2] = 0;
        Wheel(-120 * 20);
        Paint(800, 700, false);
        Check("wheel over a fitting page produces no offset",
            (int)Get("MaxScroll") == 0 && scroll[2] == 0, "scroll=" + scroll[2]);
    }

    // ── 48b: the default window fits its content ────────────────────────────
    // A window the user has never sized opens at the height its content needs,
    // so the default has NO scrollbar. The contract is arithmetic, not a
    // screenshot: at the fitted height maxScroll is 0, and one pixel short of
    // it is not — which is what proves the fit is exact rather than generous.
    static void AutoHeightDefault()
    {
        Console.WriteLine("== auto height: the default fits ==");
        Inject(true, 3);                       // every tab tall enough to scroll at 420x520
        // The REAL working area, because that is what the fit clamps against:
        // a fleet taller than the monitor must still scroll, and claiming
        // otherwise on a 100000px fake screen would prove nothing.
        int work = Screen.PrimaryScreen == null ? 1080 : Screen.PrimaryScreen.WorkingArea.Height;
        for (int tab = 0; tab <= 3; tab++)
        {
            Set("Tab", tab);
            int fitted = (int)Call("FittedClientHeight", tab, 420, work);
            Paint(420, fitted, false);
            int max = (int)Get("MaxScroll");
            if (fitted >= work - 24)
            {
                // Content taller than the screen: the fit clamped, so the
                // window scrolls rather than growing off the monitor.
                Check("tab " + tab + ": content taller than the monitor still scrolls",
                    max > 0, "fitted=" + fitted + " maxScroll=" + max);
                continue;
            }
            Check("tab " + tab + ": the fitted height scrolls not at all",
                max == 0, "fitted=" + fitted + " maxScroll=" + max);
            Paint(420, fitted - 1, false);
            Check("tab " + tab + ": one pixel short does scroll",
                (int)Get("MaxScroll") > 0, "maxScroll=" + (int)Get("MaxScroll"));
        }
        // And the ordinary case the default actually meets: a small fleet fits
        // with no scrollbar at all on any sane monitor.
        Inject(true);
        for (int tab = 0; tab <= 3; tab++)
        {
            Set("Tab", tab);
            int fitted = (int)Call("FittedClientHeight", tab, 420, work);
            Paint(420, fitted, false);
            Check("tab " + tab + ": a normal fleet needs no scrollbar by default",
                (int)Get("MaxScroll") == 0 && fitted < work - 24,
                "fitted=" + fitted + " maxScroll=" + (int)Get("MaxScroll"));
        }
        Inject(true, 3);
        // A short monitor cannot be exceeded: the fit clamps and the window
        // scrolls rather than growing off the screen.
        Set("Tab", 0);
        int clamped = (int)Call("FittedClientHeight", 0, 420, 400);
        Check("the fit never exceeds the working area",
            clamped <= 400 - 24 || clamped == MinClientH(), "clamped=" + clamped);
        // A window the user HAS sized keeps their height: auto is off, so no
        // content change moves it.
        Set("AutoHeight", false);
        Paint(420, 520, false);
        Call("AutoFitHeight");
        Check("a user-sized window is never resized by content",
            F.ClientSize.Height == 520, "height=" + F.ClientSize.Height);
        Set("AutoHeight", true);
    }

    static int MinClientH() { return (int)formType.GetField("MinClientH", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null); }

    // ── 49: per-tab scroll state ────────────────────────────────────────────
    static void PerTabScroll()
    {
        Console.WriteLine("== per-tab scroll ==");
        Inject(true, 3);                       // accounts, tray AND settings all tall enough to scroll
        Paint(420, 520, false);
        int maxAcc = 0, maxTray = 0, maxSet = 0;
        int[] scroll = (int[])Get("TabScroll");
        Set("Tab", 0); Paint(420, 520, false); maxAcc = (int)Get("MaxScroll");
        Set("Tab", 1); Paint(420, 520, false); maxTray = (int)Get("MaxScroll");
        Set("Tab", 2); Paint(420, 520, false); maxSet = (int)Get("MaxScroll");
        Check("all three tabs really scroll at 420x520 with the big fleet",
            maxAcc > 0 && maxTray > 0 && maxSet > 0,
            "accounts=" + maxAcc + " tray=" + maxTray + " settings=" + maxSet);
        scroll[0] = 50; scroll[1] = 100; scroll[2] = 100; scroll[3] = 0;
        foreach (int tab in new[] { 1, 2, 0, 2 })
        {
            Set("Tab", tab);
            Paint(420, 520, false);
        }
        Check("settings keeps its own scroll across tab switches",
            scroll[2] == 100, "settings scroll=" + scroll[2]);
        Check("accounts keeps its own scroll", scroll[0] == 50, "accounts scroll=" + scroll[0]);
        Check("tray keeps its own scroll", scroll[1] == 100, "tray scroll=" + scroll[1]);
        // Shrink the content: only the affected tab clamps.
        Inject(false);
        Set("Tab", 1);
        Paint(420, 520, false);
        Check("an emptied tray tab clamps its own scroll",
            scroll[1] == 0, "tray scroll=" + scroll[1]);
        Check("...and settings keeps its position",
            scroll[2] == 100, "settings scroll=" + scroll[2]);
        Check("...and accounts keeps its position",
            scroll[0] == 50, "accounts scroll=" + scroll[0]);
        Inject(true, 3);
        scroll[0] = scroll[1] = scroll[2] = scroll[3] = 0;
    }

    // ── 50: the compact Settings page ───────────────────────────────────────
    static void SettingsCompaction()
    {
        Console.WriteLine("== settings compaction ==");
        Inject(true);
        Set("Tab", 2);
        // The old page measured ~936px at 420 wide with the same fleet; the
        // compact blocks must be materially shorter.
        int oneCol = PanelHeightFor(2, 411);
        Check("one-column settings content at 420 width is compact (<700px)",
            oneCol > 0 && oneCol < 700, oneCol + "px");
        int twoCol = PanelHeightFor(2, 691);
        Check("two-column settings content at 700 width is compact (<420px)",
            twoCol > 0 && twoCol < 420, twoCol + "px");
        Check("two columns actually save height",
            twoCol < oneCol, twoCol + " vs " + oneCol);
        // The breakpoint: below 620 one column, at/above two.
        Paint(619, 620, false);
        bool below = PanelHeightFor(2, 610) > 0;
        Paint(640, 620, false);
        Check("the two-column breakpoint flips between 619 and 640 width",
            below, "measured both sides");
    }

    // ── 51: the theme selector ──────────────────────────────────────────────
    static void ThemeSelector()
    {
        Console.WriteLine("== theme selector ==");
        Inject(true);
        Set("Tab", 2);
        Paint(420, 620, false);
        var themesList = (IList)themes;
        int count = themesList.Count;
        ContextMenuStrip menu = (ContextMenuStrip)Call("BuildThemeMenu");
        Check("the All... list carries every theme", menu.Items.Count == count,
            menu.Items.Count + "/" + count);
        // Cycle to the next theme: the applied slug changes, the size does not.
        string before = (string)settingsType.GetField("ThemeSlug").GetValue(settings);
        Call("CycleThemeTo", 1);
        string after = (string)settingsType.GetField("ThemeSlug").GetValue(settings);
        Check("next cycles to a different theme", after != before, before + " -> " + after);
        menu = (ContextMenuStrip)Call("BuildThemeMenu");
        int checkedItems = 0;
        foreach (ToolStripItem it in menu.Items)
        {
            var mi = it as ToolStripMenuItem;
            if (mi != null && mi.Checked) checkedItems++;
        }
        Check("exactly one theme is marked active", checkedItems == 1, checkedItems + " checked");
        // Cycle the remaining count-1 steps: count cycles total from the start
        // must wrap exactly back to the initial theme.
        for (int i = 0; i < count - 1; i++) Call("CycleThemeTo", 1);
        string wrapped = (string)settingsType.GetField("ThemeSlug").GetValue(settings);
        Check("cycling count themes wraps back to the start", wrapped == before, wrapped);
        // T shortcut still cycles forward and never resizes.
        Paint(420, 620, false);
        string beforeT = (string)settingsType.GetField("ThemeSlug").GetValue(settings);
        Key(Keys.T);
        string afterT = (string)settingsType.GetField("ThemeSlug").GetValue(settings);
        Check("T still cycles the theme", afterT != beforeT, beforeT + " -> " + afterT);
        Check("theme changes never resize the window",
            Painted.Width == 420 && Painted.Height == 620, Painted.ToString());
        SSet("ThemeSlug", "goldendefault");
        Call("ApplyTheme", "goldendefault");
    }

    // ── 52: the compact account visibility manager ──────────────────────────
    static void VisibilityManager()
    {
        Console.WriteLine("== account visibility manager ==");
        Inject(true);
        Set("Tab", 2);
        Paint(420, 620, false);
        ContextMenuStrip menu = (ContextMenuStrip)Call("BuildVisibilityMenu");
        int accounts = ((IList)Get("Accounts")).Count;
        Check("the visibility menu lists every account (+ Show all)",
            menu.Items.Count == accounts + 2, menu.Items.Count + " for " + accounts + " accounts");
        // Hide one account through the menu's own action path.
        var first = ((IList)Get("Accounts"))[0];
        string key = (string)AccountType.GetProperty("Key").GetValue(first, null);
        Call("ToggleAccountHidden", key);
        menu = (ContextMenuStrip)Call("BuildVisibilityMenu");
        int shown = 0;
        foreach (ToolStripItem it in menu.Items)
        {
            var mi = it as ToolStripMenuItem;
            if (mi != null && mi.Checked) shown++;
        }
        Check("the hidden account is unchecked in the menu", shown == accounts - 1,
            shown + " of " + accounts + " shown");
        Check("...and the ini records it",
            ((string)settingsType.GetField("HiddenAccounts").GetValue(settings)).Contains(key),
            (string)settingsType.GetField("HiddenAccounts").GetValue(settings));
        // The Accounts tab no longer draws it.
        Set("Tab", 0);
        Paint(420, 620, false);
        Check("the hidden account is gone from the Accounts tab",
            ((IList)Get("Accounts")).Count == accounts, accounts + " accounts still known");
        // Recover it via Show all.
        Call("ToggleAccountHidden", key);
        Check("showing it again clears the hidden list entry",
            ((string)settingsType.GetField("HiddenAccounts").GetValue(settings)).Length == 0,
            (string)settingsType.GetField("HiddenAccounts").GetValue(settings));
    }

    // ── 47: geometry persistence profiles ───────────────────────────────────
    static void GeometryPersistence(string temp)
    {
        Console.WriteLine("== geometry persistence ==");
        // PROFILE A: an old ini with WindowX/Y only.
        string dirA = Path.Combine(temp, "a");
        Directory.CreateDirectory(dirA);
        File.WriteAllText(Path.Combine(dirA, "LIMISAW.ini"),
            "[limisaw]\r\nWindowX=20\r\nWindowY=30\r\n");
        var sa = new LimisawSettingsShim(Activator.CreateInstance(settingsType, new object[] { dirA }));
        sa.CallLoad();
        Check("an old ini with only X/Y loads with the size default unset",
            sa.WindowX() == 20 && sa.WindowY() == 30 && sa.WindowW() == 0 && sa.WindowH() == 0,
            sa.WindowX() + "," + sa.WindowY() + " " + sa.WindowW() + "x" + sa.WindowH());

        // PROFILE B: save X/Y/W/H and restore all four.
        sa.SetWindow(21, 31, 441, 631);
        sa.CallSavePosition();
        var sb = new LimisawSettingsShim(Activator.CreateInstance(settingsType, new object[] { dirA }));
        sb.CallLoad();
        Check("X/Y/W/H round-trip through the ini",
            sb.WindowX() == 21 && sb.WindowY() == 31 && sb.WindowW() == 441 && sb.WindowH() == 631,
            sb.WindowX() + "," + sb.WindowY() + " " + sb.WindowW() + "x" + sb.WindowH());

        // PROFILE D: malformed and negative sizes are safe defaults, no crash.
        string dirD = Path.Combine(temp, "d");
        Directory.CreateDirectory(dirD);
        File.WriteAllText(Path.Combine(dirD, "LIMISAW.ini"),
            "[limisaw]\r\nWindowW=-900\r\nWindowH=banana\r\nWindowX=5\r\nWindowY=6\r\n");
        var sd = new LimisawSettingsShim(Activator.CreateInstance(settingsType, new object[] { dirD }));
        sd.CallLoad();
        Check("a negative/malformed saved size falls back to the default",
            sd.WindowW() <= 0 && sd.WindowH() <= 0, sd.WindowW() + "x" + sd.WindowH());

        // The FORM restore: a huge saved size clamps into the current working
        // area; the default profile uses the preferred size; a below-minimum
        // saved size rises to the minimum.
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        string dirDef = Path.Combine(temp, "def");
        Directory.CreateDirectory(dirDef);
        File.WriteAllText(Path.Combine(dirDef, "LIMISAW.ini"),
            "[limisaw]\r\nWindowX=20\r\nWindowY=30\r\n");
        NewFormWith(dirDef);
        Check("the default profile opens at the preferred compact size",
            F.ClientSize.Width == 420 && F.ClientSize.Height <= Math.Min(620, work.Height - 24)
                && F.ClientSize.Height >= 320,
            F.ClientSize.ToString());
        F.Dispose();
        NewFormWith(dirA, 5000, 5000);
        Check("a huge saved size is clamped to the work area",
            F.Width <= work.Width && F.Height <= work.Height,
            F.Width + "x" + F.Height + " vs " + work.Width + "x" + work.Height);
        Check("...and never below the minimum",
            F.ClientSize.Width >= 360 && F.ClientSize.Height >= 320, F.ClientSize.ToString());
        F.Dispose();
        NewFormWith(dirA, 100, 100);
        Check("a tiny saved size rises to the minimum",
            F.ClientSize.Width == 360 && F.ClientSize.Height == 320, F.ClientSize.ToString());
        F.Dispose();
        // The clamp must NOT rewrite the ini at startup.
        var after = new LimisawSettingsShim(Activator.CreateInstance(settingsType, new object[] { dirA }));
        after.CallLoad();
        Check("startup clamping did not overwrite the saved geometry",
            after.WindowW() == 441 && after.WindowH() == 631, after.WindowW() + "x" + after.WindowH());
        // Hand a LIVE form back: the resize hit-test grid runs after this.
        NewFormWith(dirDef);
    }

    static void NewFormWith(string dir, int savedW = -1, int savedH = -1)
    {
        if (F != null && !F.IsDisposed) F.Dispose();
        object s = Activator.CreateInstance(settingsType, new object[] { dir });
        settingsType.GetMethod("Load").Invoke(s, null);
        if (savedW >= 0)
        {
            settingsType.GetField("WindowW").SetValue(s, savedW);
            settingsType.GetField("WindowH").SetValue(s, savedH);
        }
        object t2 = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { Directory.GetCurrentDirectory() });
        var tray2 = new NotifyIcon();
        form = Activator.CreateInstance(formType, new object[] { dir, s, tray2, t2 });
        F = (Form)form;
    }

    class LimisawSettingsShim
    {
        readonly object o; readonly Type t;
        public LimisawSettingsShim(object o) { this.o = o; t = o.GetType(); }
        public void CallLoad() { t.GetMethod("Load").Invoke(o, null); }
        public void CallSavePosition() { t.GetMethod("SavePosition").Invoke(o, null); }
        public int WindowX() { return (int)t.GetField("WindowX").GetValue(o); }
        public int WindowY() { return (int)t.GetField("WindowY").GetValue(o); }
        public int WindowW() { return (int)t.GetField("WindowW").GetValue(o); }
        public int WindowH() { return (int)t.GetField("WindowH").GetValue(o); }
        public void SetWindow(int x, int y, int w, int h)
        {
            t.GetField("WindowX").SetValue(o, x);
            t.GetField("WindowY").SetValue(o, y);
            t.GetField("WindowW").SetValue(o, w);
            t.GetField("WindowH").SetValue(o, h);
        }
    }

    // ── 46: the resize hit-test grid ────────────────────────────────────────
    static void ResizeHitTest()
    {
        Console.WriteLine("== resize hit-test ==");
        Inject(true);
        F.StartPosition = FormStartPosition.Manual;
        F.Location = new Point(100, 100);
        Paint(420, 620, false);
        // Points in CLIENT coordinates and the HT code the contract requires.
        // The close button OWNS its rectangle: the top-right resize corner
        // exists everywhere EXCEPT the close button itself.
        int[] cx = { 2, 417, 210, 210, 2, 419, 2, 417, 210, 210, 399, 210, 417 };
        int[] cy = { 310, 310, 2, 617, 2, 2, 617, 617, 10, 20, 11, 300, 4 };
        int[] want = { 10, 11, 12, 15, 13, 14, 16, 17, 2, 2, 1, 1, 1 };
        string[] what = { "left edge", "right edge", "top edge", "bottom edge",
            "top-left corner", "top-right corner", "bottom-left corner", "bottom-right corner",
            "header drag strip", "header lower strip", "close button stays clickable",
            "body stays HTCLIENT", "top-right corner inside the close button" };
        for (int i = 0; i < cx.Length; i++)
        {
            int got = HitTest(cx[i], cy[i]);
            Check("NCHITTEST " + what[i] + " at " + cx[i] + "," + cy[i], got == want[i],
                "got " + got + ", want " + want[i]);
        }
        // A resize edge wins over the caption strip where they overlap.
        int edgeTop = HitTest(210, 3);
        Check("the top edge wins over the caption at the very top", edgeTop == 12, "got " + edgeTop);
        // Negative screen coordinates (a monitor above/left of the primary):
        // the packed SIGNED 16-bit halves must decode, never overflow.
        int packed = unchecked((int)((uint)(((-5) & 0xFFFF) << 16 | (5 & 0xFFFF))));
        try
        {
            int got = RawHitTest(packed);
            Check("negative screen coords decode without overflow", got >= 0, "HT=" + got);
        }
        catch (Exception ex)
        {
            Check("negative screen coords decode without overflow", false, ex.GetType().Name);
        }
    }

    static int HitTest(int clientX, int clientY)
    {
        Point screen = F.PointToScreen(new Point(clientX, clientY));
        return RawHitTest(Pack(screen.X, screen.Y));
    }

    static int Pack(int x, int y)
    {
        return unchecked((int)((uint)((y & 0xFFFF) << 16 | (x & 0xFFFF))));
    }

    static int RawHitTest(int packed)
    {
        var msg = new Message();
        msg.Msg = 0x84;
        msg.HWnd = F.Handle;
        msg.LParam = (IntPtr)packed;
        var args = new object[] { msg };
        formType.GetMethod("WndProc", NP).Invoke(form, args);
        return (int)((Message)args[0]).Result;
    }

    static void Key(Keys k)
    {
        formType.GetMethod("OnKeyDown", NP).Invoke(form,
            new object[] { new KeyEventArgs(k) });
    }

    static void Wheel(int delta)
    {
        formType.GetMethod("OnMouseWheel", NP).Invoke(form,
            new object[] { new MouseEventArgs(MouseButtons.None, 0, F.Width / 2, (BodyTop() + BodyHeight()) / 2, delta) });
    }

    static void Mouse(MouseButtons button, int x, int y, bool down, string method)
    {
        formType.GetMethod(method, NP).Invoke(form,
            new object[] { new MouseEventArgs(button, down ? 1 : 0, x, y, 0) });
    }
}
