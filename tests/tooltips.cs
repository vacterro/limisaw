using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Limisaw;

// The tooltip contract, driven through the REAL form paint and interaction
// registries — never by counting source strings. After every paint the
// harness reads the actual Buttons/HintZones/HintTexts/Marks lists the
// painters built and answers the only question that matters: does every
// visible clickable surface explain itself, immediately (footer hint) and
// after the dwell (themed popup), without ever becoming a click target
// itself, and without ever leaking a secret.
//
//   A header   B settings parity   C tray parity   D connections parity
//   E sliders  F scrollbar         G dwell         H same-target movement
//   I target change                J click         K scroll
//   L tab switch                   M resize        N geometry
//   O click transparency           P redaction     Q context menu
//
// Build + run: pwsh .\build.ps1 -Tests
public static class TooltipsTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    const BindingFlags NPS = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    static Type formType;
    static LimisawSettings settings;
    static LimisawForm form;

    static object F(string f) { return formType.GetField(f, NP).GetValue(form); }
    static void FSet(string f, object v) { formType.GetField(f, NP).SetValue(form, v); }
    static object Call(string m, params object[] a)
    { return formType.GetMethod(m, NP).Invoke(form, a); }
    static object CallS(string m, params object[] a)
    { return formType.GetMethod(m, NPS).Invoke(form, a); }

    static System.Collections.IList Buttons { get { return (System.Collections.IList)F("Buttons"); } }
    static System.Collections.IList HintZones { get { return (System.Collections.IList)F("HintZones"); } }
    static System.Collections.IList HintTexts { get { return (System.Collections.IList)F("HintTexts"); } }
    static System.Collections.IList ButtonActions { get { return (System.Collections.IList)F("ButtonActions"); } }

    // A real paint pass: the same OnPaint the window runs, onto a bitmap.
    static void Paint()
    {
        using (var bmp = new Bitmap(Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height)))
        using (Graphics g = Graphics.FromImage(bmp))
        using (var args = new PaintEventArgs(g, form.ClientRectangle))
            formType.GetMethod("OnPaint", NP).Invoke(form, new object[] { args });
    }

    static string HintAt(int x, int y)
    { return (string)Call("HintAt", new Point(x, y)); }

    static WindowData Win(string key, string label, int rem, bool available, bool short_)
    {
        return new WindowData
        {
            Key = key, Base = key, Label = label, GroupLabel = "",
            Rem = rem, Available = available,
            DurationMinutes = short_ ? 300 : 10080,
            ResetEpoch = Stamp.Of(DateTime.UtcNow.AddHours(short_ ? 2 : 72)),
        };
    }

    static AccountData Acc(string provider, string name, params WindowData[] wins)
    {
        var a = new AccountData { Provider = provider, ProviderLabel = provider, Name = name, Status = "OK", Ok = true };
        foreach (WindowData w in wins) a.Windows.Add(w);
        return a;
    }

    static void Install(params AccountData[] accounts)
    {
        var list = new List<AccountData>();
        foreach (AccountData a in accounts) list.Add(a);
        FSet("Accounts", list);
        FSet("PrevAccounts", new List<AccountData>());
        FSet("Stale", false);
    }

    // The banked-reset fixture: one Codex account carrying one credit.
    static List<AccountData> BuildWithReset()
    {
        var a = Acc("codex", "Account2", Win("five_hour", "5h", 55, true, true), Win("weekly", "week", 37, true, false));
        a.ResetCredits = 1;
        a.ResetCreditTitle = "one usage-limit reset";
        var list = new List<AccountData> { a };
        FSet("Accounts", list);
        FSet("PrevAccounts", new List<AccountData>());
        FSet("Stale", false);
        return list;
    }

    // Parity: every clickable button's CENTRE must resolve to a non-empty hint.
    static List<string> ParityMisses(List<string> labels)
    {
        var misses = new List<string>();
        for (int i = 0; i < Buttons.Count; i++)
        {
            var r = (Rectangle)Buttons[i];
            Point c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            string hint = HintAt(c.X, c.Y);
            if (string.IsNullOrWhiteSpace(hint))
                misses.Add("#" + i + " @ " + r.ToString());
        }
        return misses;
    }

    public static int Main()
    {
        string savedProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        string savedPath = Environment.GetEnvironmentVariable("PATH");
        string savedSecret = Environment.GetEnvironmentVariable("ZAI_API_KEY");
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_tips_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("PATH", "");
            string root = Directory.GetCurrentDirectory();
            settings = new LimisawSettings(temp);
            settings.Load();
            // Sound alerts on: the volume slider row and its rail must be live.
            settings.ResetSound = true;
            settings.NotifyLow = true;
            List<Theme> themes = Theme.Load(root);
            formType = typeof(LimisawForm);
            using (var tray = new NotifyIcon())
            {
                form = new LimisawForm(temp, settings, tray, themes);
                while ((bool)F("Refreshing")) Application.DoEvents();

                Install(
                    Acc("codex", "Account2", Win("five_hour", "5h", 55, true, true), Win("weekly", "week", 37, true, false)),
                    Acc("zcode", "Zcode", Win("five_hour", "5h", 0, false, true), Win("weekly", "week", 12, true, false)));
                settings.TrayMax = 9;

                // ── A. HEADER: refresh / showing / close / every tab ────────
                Paint();
                Check("A. a paint produced buttons and hints",
                    Buttons.Count > 0 && HintZones.Count >= Buttons.Count,
                    "buttons=" + Buttons.Count + " hint zones=" + HintZones.Count);
                var headerMisses = ParityMisses(null);
                Check("A2. every header/chrome button centre resolves to a hint",
                    headerMisses.Count == 0,
                    headerMisses.Count == 0 ? Buttons.Count + " buttons covered" : headerMisses[0]);

                // ── B. SETTINGS parity, normal + narrow ─────────────────────
                Call("ShowTab", 2);
                Paint();
                var settingsMisses = ParityMisses(null);
                Check("B. every Settings action centre resolves to a hint (normal width)",
                    settingsMisses.Count == 0,
                    settingsMisses.Count == 0 ? Buttons.Count + " actions covered" : settingsMisses[0]);
                form.ClientSize = new Size(360, 620);
                Paint();
                var narrowMisses = ParityMisses(null);
                Check("B2. every Settings action centre resolves to a hint (narrow width)",
                    narrowMisses.Count == 0,
                    narrowMisses.Count == 0 ? Buttons.Count + " actions covered" : narrowMisses[0]);
                form.ClientSize = new Size(640, 620);
                Paint();

                // ── C. TRAY parity: reorder / pin / show / hide ─────────────
                Call("ShowTab", 1);
                Paint();
                var trayMisses = ParityMisses(null);
                Check("C. every Tray tab action centre resolves to a hint",
                    trayMisses.Count == 0,
                    trayMisses.Count == 0 ? Buttons.Count + " actions covered" : trayMisses[0]);
                // The row itself must explain BOTH gestures on one rectangle.
                string rowHint = null;
                var rows = (System.Collections.IList)F("ItemRows");
                if (rows.Count > 0)
                {
                    Rectangle r0 = (Rectangle)rows[0];
                    rowHint = HintAt(r0.X + 8, r0.Y + r0.Height / 2);
                }
                Check("C2. a tray row's hover explains click-to-pin AND drag-to-reorder",
                    rowHint != null && rowHint.IndexOf("pin", StringComparison.OrdinalIgnoreCase) >= 0
                        && rowHint.IndexOf("drag", StringComparison.OrdinalIgnoreCase) >= 0,
                    rowHint ?? "no row hint");

                // ── D. CONNECTIONS parity ───────────────────────────────────
                Call("ShowTab", 3);
                Paint();
                var connMisses = ParityMisses(null);
                Check("D. every Connections action centre resolves to a hint",
                    connMisses.Count == 0,
                    connMisses.Count == 0 ? Buttons.Count + " actions covered" : connMisses[0]);

                // ── D2. PARITY MATRIX: the same invariant survives the three
                // responsive widths on every tab. Offscreen controls are not
                // registered at all (layout_fit owns that check), so this is
                // exactly "every registered action keeps its explanation".
                var matrixMisses = new List<string>();
                foreach (int mtab in new[] { 0, 1, 2, 3 })
                    foreach (int mw in new[] { 360, 420, 640 })
                    {
                        form.ClientSize = new Size(mw, 620);
                        Call("ShowTab", mtab);
                        Paint();
                        var mm = ParityMisses(null);
                        if (mm.Count > 0) matrixMisses.Add("tab " + mtab + " @" + mw + ": " + mm[0]);
                    }
                Check("D2. action/hint parity holds for all four tabs at 360/420/640",
                    matrixMisses.Count == 0,
                    matrixMisses.Count == 0 ? "4 tabs x 3 widths covered" : matrixMisses[0]);
                form.ClientSize = new Size(640, 620);

                // ── E. SLIDERS: enabled rails/knobs resolve hints ───────────
                Call("ShowTab", 2);
                Paint();
                Rectangle railPreview = (Rectangle)F("VolRailPreview");
                Rectangle railVolume = (Rectangle)F("VolRailVolume");
                Rectangle railLow = (Rectangle)F("VolRailLow");
                Check("E. the preview slider rail resolves a hint",
                    railPreview.Width > 0 && HintAt(railPreview.X + railPreview.Width / 2, railPreview.Y + 6).Length > 0,
                    railPreview.ToString());
                Check("E2. the volume rail resolves a hint while a sound is on",
                    railVolume.Width > 0 && HintAt(railVolume.X + railVolume.Width / 2, railVolume.Y + 6).Length > 0,
                    railVolume.ToString());
                Check("E3. the low-threshold rail resolves a hint while a low channel is on",
                    railLow.Width > 0 && HintAt(railLow.X + railLow.Width / 2, railLow.Y + 6).Length > 0,
                    railLow.ToString());

                // ── F. SCROLLBAR: thumb AND page-click track ────────────────
                form.ClientSize = new Size(420, 380); // force overflow
                Call("ShowTab", 2);
                Paint();
                Rectangle track = (Rectangle)Call("CurrentTrack");
                bool trackHasHint = false, thumbHasHint = false;
                if (track != Rectangle.Empty)
                {
                    Rectangle thumb = (Rectangle)Call("ScrollThumb", track, 0);
                    string trackHint = HintAt(track.X + track.Width / 2, track.Y + 2);
                    string thumbHint = HintAt(thumb.X + thumb.Width / 2, thumb.Y + thumb.Height / 2);
                    trackHasHint = trackHint.IndexOf("page", StringComparison.OrdinalIgnoreCase) >= 0
                        && trackHint.IndexOf("drag", StringComparison.OrdinalIgnoreCase) >= 0;
                    thumbHasHint = thumbHint.IndexOf("drag", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                Check("F. the scrollbar TRACK answers a page click question",
                    track != Rectangle.Empty && trackHasHint, track.ToString());
                Check("F2. the scrollbar THUMB explains dragging",
                    track != Rectangle.Empty && thumbHasHint, track.ToString());
                form.ClientSize = new Size(640, 620);
                Paint();

                // ── G. DWELL: nothing shows before the delay, popup after ───
                Rectangle target = Buttons.Count > 0 ? (Rectangle)Buttons[0] : new Rectangle(8, 8, 40, 20);
                Point pIn = new Point(target.X + target.Width / 2, target.Y + target.Height / 2);
                string hint0 = HintAt(pIn.X, pIn.Y);
                Call("TooltipHover", hint0, pIn, target);
                Check("G. a pending dwell shows nothing yet",
                    (string)F("TooltipShown") == "" && ((string)F("TooltipPending")) == hint0,
                    "shown='" + F("TooltipShown") + "'");
                Call("TooltipFire");
                string shownRect = ((Rectangle)F("TooltipRect")).ToString();
                Check("G2. firing the seam shows the themed popup",
                    ((string)F("TooltipShown")) == hint0
                        && ((Rectangle)F("TooltipRect")).Width > 0
                        && (int)F("TooltipShownCount") == 1,
                    shownRect);

                // ── H. SAME TARGET MOVEMENT never restarts the dwell ────────
                Call("TooltipDismiss");
                int startsBefore = (int)F("TooltipTimerStarts");
                Call("TooltipHover", hint0, pIn, target);
                for (int i = 1; i <= 6; i++)
                    Call("TooltipHover", hint0,
                        new Point(target.X + 2 + i, target.Y + 3), target);
                int startsAfter = (int)F("TooltipTimerStarts");
                Check("H. six moves inside the SAME pending target do not restart the dwell",
                    startsAfter == startsBefore + 1 && (string)F("TooltipPending") == hint0,
                    "timer starts " + startsBefore + " -> " + startsAfter);
                Call("TooltipFire");
                Check("H2. the dwell still completes into a shown popup",
                    ((string)F("TooltipShown")) == hint0, "");

                // ── I. TARGET CHANGE: same words, different rectangle ──────
                Rectangle other = new Rectangle(target.X + target.Width + 10, target.Y, 30, 20);
                int startsI = (int)F("TooltipTimerStarts");
                int hiddenBefore = (int)F("TooltipHiddenCount");
                Call("TooltipHover", hint0, new Point(other.X + 2, other.Y + 2), other);
                Check("I. moving to ANOTHER target (even with the same sentence) dismisses and re-dwells",
                    (string)F("TooltipShown") == "" && (int)F("TooltipHiddenCount") == hiddenBefore + 1
                        && (int)F("TooltipTimerStarts") == startsI + 1,
                    "hidden " + hiddenBefore + "->" + F("TooltipHiddenCount")
                        + " starts +" + ((int)F("TooltipTimerStarts") - startsI));

                // ── J. CLICK dismisses ──────────────────────────────────────
                Call("TooltipFire");
                bool shownJ = ((string)F("TooltipShown")).Length > 0;
                int buttonsJ = Buttons.Count;
                var downArgs = new MouseEventArgs(MouseButtons.Left, 1, ((Rectangle)Buttons[0]).X + 2, ((Rectangle)Buttons[0]).Y + 2, 0);
                formType.GetMethod("OnMouseDown", NP).Invoke(form, new object[] { downArgs });
                Check("J. a mouse click dismisses the popup",
                    shownJ && (string)F("TooltipShown") == "",
                    "shown before=" + shownJ + " after='" + F("TooltipShown") + "'");
                Check("J2. the dismiss click did not add any hit target",
                    Buttons.Count == buttonsJ, "buttons " + buttonsJ + "->" + Buttons.Count);

                // ── K. SCROLL dismisses ─────────────────────────────────────
                Call("TooltipHover", hint0, pIn, target);
                Call("TooltipFire");
                var wheelArgs = new MouseEventArgs(MouseButtons.None, 3, 100, 100, 120);
                formType.GetMethod("OnMouseWheel", NP).Invoke(form, new object[] { wheelArgs });
                Check("K. the wheel dismisses the popup",
                    (string)F("TooltipShown") == "", "");

                // ── L. TAB SWITCH dismisses ─────────────────────────────────
                Call("TooltipHover", hint0, pIn, target);
                Call("TooltipFire");
                Call("ShowTab", 2);
                Check("L. a tab switch dismisses the popup",
                    (string)F("TooltipShown") == "", "");
                Call("ShowTab", 0);

                // ── M. RESIZE lifecycle dismisses ───────────────────────────
                Call("TooltipHover", hint0, pIn, target);
                Call("TooltipFire");
                formType.GetMethod("OnResizeEnd", NP).Invoke(form, new object[] { EventArgs.Empty });
                Check("M. the end of a resize/move gesture dismisses the popup",
                    (string)F("TooltipShown") == "", "");

                // ── N. GEOMETRY: the popup stays inside the client ──────────
                Rectangle far = new Rectangle(form.ClientSize.Width - 30, form.ClientSize.Height - 30, 24, 20);
                Call("TooltipHover", "a long sentence about a control near the bottom right corner of the window",
                    new Point(far.X + 4, far.Y + 4), far);
                Call("TooltipFire");
                Rectangle tr = (Rectangle)F("TooltipRect");
                Rectangle client = form.ClientRectangle;
                Check("N. the popup rectangle stays inside the client rectangle",
                    client.Contains(tr) && tr.Width > 0, tr + " in " + client);

                // ── O. CLICK TRANSPARENCY: the popup is never a hit target ──
                // The registry must be judged against TWO paints on the SAME
                // tab: a paint with the popup shown must register exactly what
                // a paint with it hidden did, nothing more.
                Call("ShowTab", 0);
                Paint();
                int bBefore = Buttons.Count, aBefore = ButtonActions.Count, hBefore = HintZones.Count;
                Call("TooltipHover", hint0, pIn, ((Rectangle)Buttons[0]));
                Call("TooltipFire");
                Paint();
                Check("O. a paint with the popup shown registers no new actions/hints",
                    Buttons.Count == bBefore && ButtonActions.Count == aBefore && HintZones.Count == hBefore,
                    "buttons " + bBefore + "->" + Buttons.Count + " zones " + hBefore + "->" + HintZones.Count);
                Rectangle shownT = (Rectangle)F("TooltipRect");
                // The popup IS an overlay and may visually sit over content —
                // like every tooltip. The contract is narrower: it registers no
                // hover/click target of its own, and a click takes it down (J),
                // so it can never steal the click it covers.
                bool selfRegistered = false;
                foreach (object z in HintZones)
                    if (((Rectangle)z).Equals(shownT)) selfRegistered = true;
                Check("O2. the popup registered no hover/click target of its own",
                    !selfRegistered, shownT.ToString());
                Call("TooltipDismiss");

                // ── P. REDACTION: a secret never reaches hover text ─────────
                const string fakeSecret = "LIMISAW_TEST_SECRET_9f27";
                Environment.SetEnvironmentVariable("ZAI_API_KEY", fakeSecret);
                MethodInfo drawFit = formType.GetMethod("DrawTextFit", NP);
                using (Bitmap bmp = new Bitmap(320, 40))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    drawFit.Invoke(form, new object[] { g,
                        "token", 4, 4, 30, Palette.MUTED, 10, false,
                        "api_key = \"" + fakeSecret + "\" the resolved path" });
                }
                bool leaked = false;
                foreach (string h in HintTexts) if (h.IndexOf(fakeSecret, StringComparison.Ordinal) >= 0) leaked = true;
                Check("P. an elided credential line's full-text tooltip is redacted",
                    !leaked && HintTexts.Count > 0, leaked ? "SECRET REACHED A HINT" : "no secret in any hint zone");
                // The live Connections panel too: a fabricated reason carrying
                // the secret must never appear in a painted hint.
                object fakeConn = FakeConnection(fakeSecret);
                if (fakeConn != null)
                {
                    var conns = new List<VendorConnection>((List<VendorConnection>)F("Connections"));
                    conns.Add((VendorConnection)fakeConn);
                    FSet("Connections", conns);
                    Call("ShowTab", 3);
                    Paint();
                    bool leaked2 = false;
                    foreach (string h in HintTexts) if (h.IndexOf(fakeSecret, StringComparison.Ordinal) >= 0) leaked2 = true;
                    string footerNow = (string)F("Hover");
                    Check("P2. the Connections panel with a secret-bearing reason leaks nothing",
                        !leaked2 && (footerNow == null || footerNow.IndexOf(fakeSecret, StringComparison.Ordinal) < 0),
                        leaked2 ? "SECRET IN PAINTED HINTS" : "panel hints clean");
                }
                Environment.SetEnvironmentVariable("ZAI_API_KEY", savedSecret);

// ── Q. CONTEXT MENU: actionable items carry ToolTipText ─────
                // Engine-linked: the internal statics are in-assembly and
                // reachable directly (they live on Program).
                var menu = Program.BuildMenu(settings, tray,
                    () => form, () => { }, () => { }, themes);
                Program.MenuOpening((Program.TrayMenuState)menu.Tag);
                Check("Q. the tray menu enables item tooltips",
                    menu.ShowItemToolTips, "ShowItemToolTips=" + menu.ShowItemToolTips);
                var menuMisses = new List<string>();
                CollectMenuMisses(menu.Items, "menu", menuMisses);
                Check("Q2. every ENABLED actionable menu item has a non-empty ToolTipText",
                    menuMisses.Count == 0,
                    menuMisses.Count == 0 ? "all actionable items covered" : menuMisses[0]);
                // The theme submenu opened from the Settings tab too.
                var themeMenu = form.BuildThemeMenu();
                var themeMisses = new List<string>();
                CollectMenuMisses(themeMenu.Items, "theme", themeMisses);
                Check("Q3. every theme item has a non-empty ToolTipText",
                    themeMisses.Count == 0,
                    themeMisses.Count == 0 ? themeMenu.Items.Count + " themes covered" : themeMisses[0]);

                // ── Use reset: the banked-reset button explains itself ──────
                Install(Acc("codex", "Account2", Win("five_hour", "5h", 55, true, true), Win("weekly", "week", 37, true, false)));
                FSet("Accounts", BuildWithReset());
                Call("ShowTab", 0);
                Paint();
                bool useHintFound = false;
                foreach (object hintObj in HintTexts)
                {
                    string h = (string)hintObj;
                    if (h.IndexOf("banked", StringComparison.OrdinalIgnoreCase) >= 0
                        && h.IndexOf("confirmation", StringComparison.OrdinalIgnoreCase) >= 0
                        && h.IndexOf("cannot be undone", StringComparison.OrdinalIgnoreCase) >= 0)
                        useHintFound = true;
                }
                Check("R. the Use reset hover names the cost, the confirmation and the permanence",
                    useHintFound, useHintFound ? "hint covers one banked reset + confirmation first + cannot be undone"
                        : "no hint mentions one banked reset + confirmation first + cannot be undone");

                form.Close();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  harness");
            Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", savedProfile);
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("ZAI_API_KEY", savedSecret);
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }

    static void CollectMenuMisses(ToolStripItemCollection items, string path, List<string> misses)
    {
        foreach (ToolStripItem it in items)
        {
            var mi = it as ToolStripMenuItem;
            if (mi == null) continue; // separators
            if (!mi.Enabled) continue; // status-only rows explain nothing actionable
            if (string.IsNullOrWhiteSpace(mi.ToolTipText))
                misses.Add(path + "/" + mi.Text);
            if (mi.HasDropDownItems)
                CollectMenuMisses(mi.DropDownItems, path + "/" + mi.Text, misses);
        }
    }

    // A fake VendorConnection with a reason that carries the secret.
    static object FakeConnection(string secret)
    {
        return new VendorConnection
        {
            VendorId = "zcode",
            State = ConnectionState.Failed,
            Installed = true,
            Reason = "probe failed with api_key = \"" + secret + "\" in the response",
        };
    }
}
