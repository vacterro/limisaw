using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// The Settings tab's own rules, which are about being UNDERSTOOD rather than
// being correct. Every check here is a complaint that was made out loud:
//
//   * four-character buttons ("1/4", "Two", "Off") explain nothing, so every
//     control owes a sentence;
//   * a control that does nothing in the current layout must LOOK dead, not
//     silently swallow clicks — "you change it and see no difference" is what
//     made the panel feel broken;
//   * a long reading name cut to "codex/Accou..." makes three rows identical,
//     because every id in this app is distinguished by its TAIL;
//   * the preview must show a chosen quota level, not whatever the account
//     happens to sit at, or it can only ever answer one question.
//
// Driven through the real form by reflection, at several widths, so a rule that
// only holds at one window size fails here.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class SettingsUx
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

    static Assembly Load()
    {
        string root = Directory.GetCurrentDirectory();
        string exe = Path.Combine(root, "LIMISAW.exe");
        if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
        return Assembly.LoadFrom(Path.GetFullPath(exe));
    }

    static object NewForm(Assembly asm, string tempDir, NotifyIcon tray, out Type formType, out object settings)
    {
        Type settingsType = asm.GetType("Limisaw.LimisawSettings");
        Type themeType = asm.GetType("Limisaw.Theme");
        formType = asm.GetType("Limisaw.LimisawForm");
        settings = Activator.CreateInstance(settingsType, new object[] { tempDir });
        settingsType.GetMethod("Load").Invoke(settings, null);
        object themes = themeType.GetMethod("Load", PS).Invoke(null, new object[] { Directory.GetCurrentDirectory() });
        return Activator.CreateInstance(formType, new object[] { tempDir, settings, tray, themes });
    }

    // Repaint into a bitmap and hand back what the panel registered: the
    // clickable rects, the hint zones, and any label it had to crop.
    class Frame
    {
        public IList Buttons, Marks, HintZones, HintTexts;
        public IList Cropped;
        public int Width, Height;
    }

    static Frame Paint(Type formType, object form, int width)
    {
        ((Form)form).ClientSize = new Size(width, ((Form)form).ClientSize.Height);
        formType.GetMethod("FitWindow", NP).Invoke(form, null);
        var f = (Form)form;
        using (var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            var args = new PaintEventArgs(g, new Rectangle(0, 0, bmp.Width, bmp.Height));
            formType.GetMethod("OnPaint", NP).Invoke(form, new object[] { args });
        }
        return new Frame
        {
            Buttons = (IList)formType.GetField("Buttons", NP).GetValue(form),
            Marks = (IList)formType.GetField("Marks", NP).GetValue(form),
            HintZones = (IList)formType.GetField("HintZones", NP).GetValue(form),
            HintTexts = (IList)formType.GetField("HintTexts", NP).GetValue(form),
            Cropped = (IList)formType.GetField("Cropped", NP).GetValue(form),
            Width = f.Width, Height = f.Height,
        };
    }

    static string HintAt(Type formType, object form, Point p)
    {
        return (string)formType.GetMethod("HintAt", NP).Invoke(form, new object[] { p });
    }

    // Is there a clickable rect whose centre sits inside `area`?
    static bool Clickable(Frame f, Rectangle area)
    {
        foreach (Rectangle r in f.Buttons)
            if (area.Contains(new Point(r.X + r.Width / 2, r.Y + r.Height / 2))) return true;
        return false;
    }

    static Rectangle HintZoneFor(Frame f, string needle)
    {
        for (int i = f.HintTexts.Count - 1; i >= 0; i--)
            if (((string)f.HintTexts[i]).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                return (Rectangle)f.HintZones[i];
        return Rectangle.Empty;
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_ux_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assembly asm = Load();
            Elide(asm);
            Cycles(asm);

            Type formType; object settings;
            using (var tray = new NotifyIcon())
            {
                object form = NewForm(asm, temp, tray, out formType, out settings);
                using ((IDisposable)form)
                {
                    Type st = settings.GetType();
                    // Block the refresh worker: a real sweep would replace the
                    // injected state mid-test and make every assertion a race.
                    formType.GetField("Refreshing", NP).SetValue(form, true);
                    formType.GetMethod("ShowTab").Invoke(form, new object[] { 2 });

                    Hints(formType, form, settings, st);
                    Context(formType, form, settings, st);
                    PreviewFake(formType, form, settings, st, asm);
                    CardDrag(asm, formType, form, settings, st);
                    PinTray(asm, formType, form, settings, st);
                }
            }

            Console.WriteLine("---");
            Console.WriteLine(checks + " checks");
            Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
            return fails == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  harness");
            Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    // ── mid-string elide ────────────────────────────────────────────────────
    // Every reading id ends in the part that identifies it. A trailing ellipsis
    // throws exactly that away, which is how four Codex windows became four
    // rows reading "codex/Accou...".
    static void Elide(Assembly asm)
    {
        Console.WriteLine("== a shortened name keeps its tail ==");
        Type formType = asm.GetType("Limisaw.LimisawForm");
        MethodInfo elide = formType.GetMethod("Elide", NP);
        using (var bmp = new Bitmap(8, 8))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            // Elide is an instance method only because it measures with the
            // form's font cache; an uninitialised instance is enough for that.
            object form = FormatterServicesCreate(formType);
            Func<string, int, string> E = (s, w) => (string)elide.Invoke(form, new object[] { g, s, w, 10 });

            string full = "codex/Account3Free/five_hour";
            string cut = E(full, 90);
            Check("a long id is shortened from the middle",
                cut.Length < full.Length && cut.IndexOf("..", StringComparison.Ordinal) > 0, cut);
            Check("...and the tail survives, because the tail is the identity",
                cut.EndsWith("r", StringComparison.Ordinal), cut);
            Check("...and so does the head",
                cut.StartsWith("c", StringComparison.Ordinal), cut);

            // The whole point: two ids that share a long prefix must not
            // collapse into the same string.
            string a = E("codex/Account3Free/five_hour", 90);
            string b = E("codex/Account3Free/weekly", 90);
            Check("two readings on the same account stay distinguishable",
                a != b, "\"" + a + "\" vs \"" + b + "\"");

            Check("text that already fits is untouched",
                E("5h", 400) == "5h", E("5h", 400));
            Check("a width that fits nothing yields nothing, never a stray glyph",
                E("anything", 2) == "", "\"" + E("anything", 2) + "\"");
            Check("no width yields nothing", E("anything", 0) == "", "");
            Check("null and empty are handled", E(null, 50) == "" && E("", 50) == "", "");
        }
    }

    static object FormatterServicesCreate(Type t)
    {
        return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
    }

    // ── the low-alert replay fix ────────────────────────────────────────────
    // A ROLLING window (Zcode's 5-hour credit pool) pushes its reset out a
    // little on every spend, so its stamp differs on almost every sweep. The old
    // rule treated any stamp change as a new cycle, which fired the chime on
    // every single refresh — "the sound plays constantly".
    static void Cycles(Assembly asm)
    {
        Console.WriteLine("== a drifting reset is not a new cycle ==");
        Type formType = asm.GetType("Limisaw.LimisawForm");
        Type winType = asm.GetType("Limisaw.WindowData");
        MethodInfo newCycle = formType.GetMethod("NewCycle", BindingFlags.NonPublic | BindingFlags.Static);

        Func<int, string, string, bool> Rolled = (minutes, before, after) =>
        {
            object w = Activator.CreateInstance(winType);
            winType.GetField("DurationMinutes").SetValue(w, minutes);
            return (bool)newCycle.Invoke(null, new object[] { w, before, after });
        };
        DateTime t0 = new DateTime(2126, 5, 1, 12, 0, 0);
        Func<DateTime, string> S = d => d.ToString("yyyy-MM-ddTHH:mm:ss");

        Check("an identical stamp is never a new cycle",
            !Rolled(300, S(t0), S(t0)), "");
        Check("a few minutes of consumption drift is not a new cycle",
            !Rolled(300, S(t0), S(t0.AddMinutes(7))), "5h window, +7m");
        Check("...nor is an hour of it on a 5-hour window",
            !Rolled(300, S(t0), S(t0.AddMinutes(90))), "5h window, +90m");
        Check("a jump of half the window IS a rollover",
            Rolled(300, S(t0), S(t0.AddMinutes(150))), "5h window, +150m");
        Check("a full window jump is certainly one",
            Rolled(300, S(t0), S(t0.AddMinutes(300))), "5h window, +300m");
        Check("a weekly window tolerates a whole day of drift",
            !Rolled(10080, S(t0), S(t0.AddDays(1))), "weekly, +1d");
        Check("...and rolls over at half a week",
            Rolled(10080, S(t0), S(t0.AddDays(4))), "weekly, +4d");
        // Without a stated duration there is nothing to measure against, so
        // recovery above the threshold is the only honest re-arm.
        Check("a window with no duration re-arms on recovery alone, never on a guess",
            !Rolled(0, S(t0), S(t0.AddDays(30))), "no duration");
        Check("an unparseable stamp does not invent a cycle",
            !Rolled(300, "whenever", S(t0)), "");
        // A reset that moved BACKWARDS is a vendor correcting itself, not a
        // rollover: alerting on it would be a replay.
        Check("a reset that moved backwards is not a rollover",
            !Rolled(300, S(t0), S(t0.AddMinutes(-200))), "5h window, -200m");
    }

    // ── hints ───────────────────────────────────────────────────────────────
    static void Hints(Type formType, object form, object settings, Type st)
    {
        Console.WriteLine("== every control explains itself ==");
        Frame f = Paint(formType, form, 560);
        Check("the panel registers hint zones at all", f.HintZones.Count > 10,
            f.HintZones.Count + " zones");
        Check("every zone carries a sentence, and every sentence a zone",
            f.HintZones.Count == f.HintTexts.Count,
            f.HintZones.Count + " zones / " + f.HintTexts.Count + " texts");

        bool allReal = true;
        foreach (string s in f.HintTexts)
            if (s == null || s.Trim().Length < 8) allReal = false;
        Check("no hint is a stub — a four-word label needs a real sentence", allReal, "");

        // Collected across both layout families on purpose: a disabled control
        // registers NO hint (that is the Context contract below), so the fill
        // buttons only speak in a bars/cells layout and the readout buttons only
        // in a number one. Every control still owes its sentence in the mode
        // where it does something.
        FieldInfo mode = st.GetField("TrayMode");
        object savedMode = mode.GetValue(settings);
        var said = new List<string>();
        foreach (string m in new[] { "single", "bars" })
        {
            mode.SetValue(settings, m);
            Frame fm = Paint(formType, form, 560);
            foreach (string s in fm.HintTexts) said.Add(s);
        }
        mode.SetValue(settings, savedMode);

        string[] needles = {
            "one number", "two numbers", "one vertical bar", "one cell",
            "halves", "quarters", "eighths", "exact",
            "time until", "percent left", "no number at all",
            "balloon:", "chime:", "threshold", "count down", "count up",
            "nothing real changes", "start LIMISAW with Windows",
        };
        var missing = new List<string>();
        foreach (string n in needles)
        {
            bool found = false;
            foreach (string s in said)
                if (s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
            if (!found) missing.Add(n);
        }
        Check("each control the user could not decode now has its own words",
            missing.Count == 0, missing.Count == 0 ? needles.Length + " checked"
                : "missing: " + string.Join(", ", missing.ToArray()));

        // A hint reaches the footer through HintAt, so hovering a control has to
        // resolve to that control's sentence and not to its row's.
        f = Paint(formType, form, 560);
        Rectangle layoutRow = HintZoneFor(f, "what the 16x16 tray icon draws");
        Check("hovering a row explains the row", layoutRow != Rectangle.Empty
            && HintAt(formType, form, new Point(layoutRow.X + 4, layoutRow.Y + 4)).Length > 0,
            layoutRow.ToString());
        Rectangle barsBtn = HintZoneFor(f, "one vertical bar");
        Check("hovering a control beats its row — the specific answer wins",
            barsBtn != Rectangle.Empty
            && HintAt(formType, form, new Point(barsBtn.X + barsBtn.Width / 2, barsBtn.Y + barsBtn.Height / 2))
                .IndexOf("one vertical bar", StringComparison.Ordinal) >= 0,
            HintAt(formType, form, new Point(barsBtn.X + barsBtn.Width / 2, barsBtn.Y + barsBtn.Height / 2)));
        Check("empty space explains nothing rather than something wrong",
            HintAt(formType, form, new Point(0, 0)) == "", "");
    }

    // ── context ─────────────────────────────────────────────────────────────
    // A setting that cannot matter in the current mode is drawn dead AND is not
    // clickable. Half-measures are the bug: a greyed button that still works
    // teaches the user that grey means nothing.
    static void Context(Type formType, object form, object settings, Type st)
    {
        Console.WriteLine("== a setting that cannot matter looks and acts dead ==");
        FieldInfo mode = st.GetField("TrayMode");
        FieldInfo notifyLow = st.GetField("NotifyLow");
        FieldInfo lowSound = st.GetField("LowSound");
        FieldInfo resetSound = st.GetField("ResetSound");

        mode.SetValue(settings, "single");
        Frame f = Paint(formType, form, 560);
        Rectangle fill = HintZoneFor(f, "only bars and cells have a fill");
        Check("Number layout: the fill row says why it is dead",
            fill != Rectangle.Empty, "");
        Check("...and none of its buttons is clickable",
            !Clickable(f, new Rectangle(fill.X + 100, fill.Y, fill.Width - 100, fill.Height)),
            "");
        Check("...while the readout row IS live, because a number has a readout",
            HintZoneFor(f, "only a number layout has a readout") == Rectangle.Empty, "");

        mode.SetValue(settings, "bars");
        f = Paint(formType, form, 560);
        Rectangle shows = HintZoneFor(f, "only a number layout has a readout");
        Check("Bars layout: the readout row says why it is dead",
            shows != Rectangle.Empty, "");
        Check("...and none of its buttons is clickable",
            !Clickable(f, new Rectangle(shows.X + 100, shows.Y, shows.Width - 100, shows.Height)),
            "");
        Check("...while the fill row is live again",
            HintZoneFor(f, "only bars and cells have a fill") == Rectangle.Empty, "");

        // The volume belongs to the CHIMES. With both muted it controls nothing.
        // CORE-005: the low alert's chime is its own switch now, so the low half
        // of that pair is LowSound — NotifyLow is only the balloon.
        lowSound.SetValue(settings, false);
        resetSound.SetValue(settings, false);
        f = Paint(formType, form, 560);
        Check("both chimes off: the volume says there is nothing to set it for",
            HintZoneFor(f, "nothing to set a volume for") != Rectangle.Empty, "");
        int mutedHeight = f.Height, mutedButtons = f.Buttons.Count;

        lowSound.SetValue(settings, true);
        f = Paint(formType, form, 560);
        Check("arming the low chime brings its WAV and Play buttons back",
            f.Buttons.Count == mutedButtons + 2, mutedButtons + " -> " + f.Buttons.Count);
        Check("...and the volume with them",
            HintZoneFor(f, "alert volume") != Rectangle.Empty, "");
        // The picker collapses INSIDE the alert's own row, so the panel keeps its
        // height: a row that appears and disappears made the window jump, and at a
        // small height the new row was the one that got clipped.
        Check("...without the panel jumping, because the row was always there",
            f.Height == mutedHeight, mutedHeight + " -> " + f.Height);

        // Both low channels off is the only state with no threshold to set.
        notifyLow.SetValue(settings, false);
        lowSound.SetValue(settings, false);
        f = Paint(formType, form, 560);
        Check("both low channels off: the threshold says why it is dead",
            HintZoneFor(f, "both low channels are off") != Rectangle.Empty, "");
        notifyLow.SetValue(settings, true);
        f = Paint(formType, form, 560);
        Check("...and the balloon alone brings the threshold back to life",
            HintZoneFor(f, "the level the low alert fires at") != Rectangle.Empty, "");

        resetSound.SetValue(settings, true);
        f = Paint(formType, form, 560);
        Check("with a chime on, the volume is live",
            HintZoneFor(f, "alert volume") != Rectangle.Empty, "");

        // Every width, because a rule that only holds at 560px is not a rule.
        var bad = new List<string>();
        foreach (int width in new[] { 420, 480, 560, 700, 900 })
        {
            f = Paint(formType, form, width);
            if (f.Cropped.Count > 0)
                bad.Add(width + "px cropped \"" + f.Cropped[0] + "\"");
            foreach (Rectangle mark in f.Marks)
                foreach (Rectangle btn in f.Buttons)
                    if (btn.IntersectsWith(mark) && !btn.Contains(mark))
                    {
                        // A button overlapping measured content is the class of
                        // bug layout_fit.cs exists for; catching it here too
                        // keeps the reworked panel honest at every width.
                        bad.Add(width + "px button " + btn + " over content " + mark);
                        break;
                    }
        }
        Check("no width crops a label or lets a button sit on content",
            bad.Count == 0, bad.Count == 0 ? "420/480/560/700/900" : bad[0]);
    }

    // ── the preview ─────────────────────────────────────────────────────────
    static void PreviewFake(Type formType, object form, object settings, Type st, Assembly asm)
    {
        Console.WriteLine("== the preview shows a level you choose, not the account's ==");
        FieldInfo previewPct = st.GetField("PreviewPct");
        FieldInfo mode = st.GetField("TrayMode");
        MethodInfo render = formType.GetMethod("RenderPreviewBitmap", NP);
        FieldInfo live = formType.GetField("PreviewPct", NP);

        Check("the pretend level is a saved setting, so it survives a restart",
            previewPct != null && (int)previewPct.GetValue(settings) >= 0,
            "PreviewPct=" + previewPct.GetValue(settings));

        // With no accounts probed at all — the first-launch case — a preview must
        // still draw something, or the layout picker is useless until a vendor
        // logs in.
        mode.SetValue(settings, "bars");
        previewPct.SetValue(settings, 90);
        Bitmap high = (Bitmap)render.Invoke(form, null);
        previewPct.SetValue(settings, 5);
        Bitmap low = (Bitmap)render.Invoke(form, null);
        try
        {
            Check("a preview renders with no accounts discovered yet",
                high != null && high.Width >= 16, high == null ? "null" : high.Width + "px");
            int diff = 0;
            for (int y = 0; y < Math.Min(high.Height, low.Height); y++)
                for (int x = 0; x < Math.Min(high.Width, low.Width); x++)
                    if (high.GetPixel(x, y).ToArgb() != low.GetPixel(x, y).ToArgb()) diff++;
            Check("moving the slider actually changes the picture",
                diff > 0, diff + " pixels differ between 90% and 5%");
        }
        finally { if (high != null) high.Dispose(); if (low != null) low.Dispose(); }

        Check("the preview flag is cleared afterwards, so the real tray is real again",
            (int)live.GetValue(form) == -1, "PreviewPct=" + live.GetValue(form));

        // Rendering through the real path is the point: a mock-up can agree with
        // the icon today and drift tomorrow. Every layout must survive it.
        var broke = new List<string>();
        foreach (string m in new[] { "single", "dual", "bars", "grid" })
        {
            mode.SetValue(settings, m);
            foreach (int pct in new[] { 0, 1, 50, 99, 100 })
            {
                previewPct.SetValue(settings, pct);
                try
                {
                    using (var bmp = (Bitmap)render.Invoke(form, null))
                        if (bmp == null || bmp.Width < 16) broke.Add(m + "@" + pct);
                }
                catch (Exception ex) { broke.Add(m + "@" + pct + ": " + ex.GetType().Name); }
            }
        }
        Check("every layout previews at every level without throwing",
            broke.Count == 0, broke.Count == 0 ? "4 layouts x 5 levels" : broke[0]);
    }

    // ── account cards reorder by dragging ───────────────────────────────────
    // Same gesture as the Tray tab, but the rows are not a constant height: a
    // card is as tall as its window count, so the drop slot has to be measured
    // from real geometry. Cards of DIFFERENT heights are the whole point of the
    // fixture — three equal cards would pass with a hardcoded row height.
    static void CardDrag(Assembly asm, Type formType, object form, object settings, Type st)
    {
        Console.WriteLine("== account cards reorder by dragging ==");
        Type accType = asm.GetType("Limisaw.AccountData");
        Type winType = asm.GetType("Limisaw.WindowData");

        Func<string, string, int, object> account = (provider, name, windows) =>
        {
            object a = Activator.CreateInstance(accType);
            accType.GetField("Provider").SetValue(a, provider);
            accType.GetField("ProviderLabel").SetValue(a, provider);
            accType.GetField("Name").SetValue(a, name);
            accType.GetField("Status").SetValue(a, "OK");
            accType.GetField("Ok").SetValue(a, true);
            var list = (IList)accType.GetField("Windows").GetValue(a);
            for (int i = 0; i < windows; i++)
            {
                object w = Activator.CreateInstance(winType);
                winType.GetField("Key").SetValue(w, "w" + i);
                winType.GetField("Base").SetValue(w, "five_hour");
                winType.GetField("Label").SetValue(w, "5h");
                winType.GetField("Group").SetValue(w, "");
                winType.GetField("GroupLabel").SetValue(w, "");
                winType.GetField("Available").SetValue(w, true);
                winType.GetField("Rem").SetValue(w, 50);
                winType.GetField("DurationMinutes").SetValue(w, 300);
                list.Add(w);
            }
            return a;
        };

        var accounts = (IList)formType.GetField("Accounts", NP).GetValue(form);
        accounts.Clear();
        // Deliberately uneven: 1, 4 and 2 windows.
        accounts.Add(account("codex", "Codex", 1));
        accounts.Add(account("antigravity", "Antigravity", 4));
        accounts.Add(account("zcode", "Zcode", 2));
        formType.GetMethod("ShowTab").Invoke(form, new object[] { 0 });
        Frame f = Paint(formType, form, 560);

        var tops = (IList)formType.GetField("CardTops", NP).GetValue(form);
        Check("every card registers a drag row", f.HintZones.Count > 0 && tops.Count == 3,
            tops.Count + " card tops");
        bool uneven = tops.Count == 3
            && (int)tops[1] - (int)tops[0] != (int)tops[2] - (int)tops[1];
        Check("the fixture really has cards of different heights", uneven,
            tops.Count == 3 ? ((int)tops[0] + "/" + (int)tops[1] + "/" + (int)tops[2]) : "?");
        Check("a card row carries the drag hint",
            HintZoneFor(f, "drag a card to reorder") != Rectangle.Empty, "");

        // Drive the drop slot directly: the pointer's Y decides, and the answer
        // must follow the real card tops rather than a fixed row height.
        FieldInfo dragY = formType.GetField("DragY", NP);
        MethodInfo drop = formType.GetMethod("DropIndex", NP);
        Func<int, int> slotAt = y => { dragY.SetValue(form, y); return (int)drop.Invoke(form, new object[] { 3 }); };

        Check("a pointer above the list drops at the top",
            slotAt((int)tops[0] - 20) == 0, "slot=" + slotAt((int)tops[0] - 20));
        Check("inside the first card is still slot 0",
            slotAt((int)tops[0] + 4) == 0, "slot=" + slotAt((int)tops[0] + 4));
        Check("inside the tall middle card is slot 1",
            slotAt((int)tops[1] + 4) == 1, "slot=" + slotAt((int)tops[1] + 4));
        Check("...and its BOTTOM half is still slot 1, not the next one",
            slotAt((int)tops[2] - 6) == 1, "slot=" + slotAt((int)tops[2] - 6));
        Check("inside the last card is the last slot",
            slotAt((int)tops[2] + 4) == 2, "slot=" + slotAt((int)tops[2] + 4));
        Check("far below the list clamps to the last slot",
            slotAt((int)tops[2] + 4000) == 2, "slot=" + slotAt((int)tops[2] + 4000));

        // The saved order is what the cards are drawn from, and an account that
        // is no longer reported must not resurrect from it.
        object saved = st.GetField("AccountOrder").GetValue(settings);
        try
        {
            st.GetField("AccountOrder").SetValue(settings, "zcode/Zcode|codex/Codex");
            MethodInfo ordered = formType.GetMethod("OrderedAccounts", NP);
            var cards = (IList)ordered.Invoke(form, null);
            Check("the saved order decides which card is first",
                (string)accType.GetField("Provider").GetValue(cards[0]) == "zcode",
                (string)accType.GetField("Provider").GetValue(cards[0]));
            Check("an account missing from the saved order still appears, at the end",
                cards.Count == 3
                && (string)accType.GetField("Provider").GetValue(cards[2]) == "antigravity",
                cards.Count + " cards, last=" + (string)accType.GetField("Provider").GetValue(cards[2]));

            st.GetField("AccountOrder").SetValue(settings, "gone/Ghost|zcode/Zcode");
            cards = (IList)ordered.Invoke(form, null);
            Check("a stale key in the saved order is skipped, never resurrected",
                cards.Count == 3
                && (string)accType.GetField("Provider").GetValue(cards[0]) == "zcode",
                cards.Count + " cards, first=" + (string)accType.GetField("Provider").GetValue(cards[0]));
        }
        finally
        {
            st.GetField("AccountOrder").SetValue(settings, saved);
            accounts.Clear();
            formType.GetMethod("ShowTab").Invoke(form, new object[] { 2 });
        }
    }

    // ── the tray-number pin, reachable from the window ─────────────────────
    // The tray menu had it; the window did not. A user who never opens the
    // context menu could not set which reading the number reads, which is what
    // "every setting is in the window too" promised.
    static void PinTray(Assembly asm, Type formType, object form, object settings, Type st)
    {
        Console.WriteLine("== the tray number is pinnable from the window ==");
        FieldInfo metric = st.GetField("TrayMetric");
        FieldInfo rows = formType.GetField("ItemRows", NP);
        FieldInfo rowIds = formType.GetField("ItemRowIds", NP);
        MethodInfo hintAt = formType.GetMethod("HintAt", NP);

        // Build a known fleet so the pin has something real to point at.
        var accounts = (IList)formType.GetField("Accounts", NP).GetValue(form);
        Type accType = asm.GetType("Limisaw.AccountData");
        Type winType = asm.GetType("Limisaw.WindowData");
        accounts.Clear();
        foreach (var spec in new[] { new { p = "codex", n = "Codex", key = "five_hour" }, new { p = "zcode", n = "Zcode", key = "weekly" } })
        {
            object a = Activator.CreateInstance(accType);
            accType.GetField("Provider").SetValue(a, spec.p);
            accType.GetField("ProviderLabel").SetValue(a, spec.p);
            accType.GetField("Name").SetValue(a, spec.n);
            accType.GetField("Status").SetValue(a, "OK");
            accType.GetField("Ok").SetValue(a, true);
            var list = (IList)accType.GetField("Windows").GetValue(a);
            object w = Activator.CreateInstance(winType);
            winType.GetField("Key").SetValue(w, spec.key);
            winType.GetField("Base").SetValue(w, spec.key);
            winType.GetField("Label").SetValue(w, "5h");
            winType.GetField("Group").SetValue(w, "");
            winType.GetField("GroupLabel").SetValue(w, "");
            winType.GetField("Available").SetValue(w, true);
            winType.GetField("Rem").SetValue(w, 60);
            winType.GetField("DurationMinutes").SetValue(w, 300);
            list.Add(w);
            accounts.Add(a);
        }

        string savedMetric = (string)metric.GetValue(settings);
        try
        {
            metric.SetValue(settings, "lowest");
            formType.GetMethod("ShowTab").Invoke(form, new object[] { 1 });
            Frame f = Paint(formType, form, 560);

            // A pinned reading is MARKED in the list, so the user can see what
            // the number reads without hovering anything.
            IList ids = (IList)rowIds.GetValue(form);
            Check("the tray tab lists the fleet", ids != null && ids.Count == 2,
                ids == null ? "no rows" : ids.Count + " rows");
            Check("the pin hint exists in the lowest state",
                HintZoneFor(f, "click to pin this reading") != Rectangle.Empty, "");

            // Hovering a row explains pin vs drag.
            IList rowList = (IList)rows.GetValue(form);
            if (rowList != null && rowList.Count > 0)
            {
                Rectangle r0 = (Rectangle)rowList[0];
                Check("a row hover explains the gesture",
                    HintAt(formType, form, new Point(r0.X + 30, r0.Y + 5)).Length > 0, "");
            }

            // Simulate the CLICK path directly: OnMouseUp with no drag.
            string target = (string)((IList)rowIds.GetValue(form))[0];
            formType.GetField("DragId", NP).SetValue(form, target);
            formType.GetField("ClickPinCandidate", NP).SetValue(form, target);
            formType.GetField("Dragging", NP).SetValue(form, false);
            formType.GetMethod("OnMouseUp", NP).Invoke(form,
                new object[] { new MouseEventArgs(MouseButtons.Left, 1, 40, 20, 0) });
            Check("a plain click on a row pins it as the tray number",
                (string)metric.GetValue(settings) == target,
                "TrayMetric=" + metric.GetValue(settings));

            Frame pinned = Paint(formType, form, 560);
            Check("the pinned row carries its own hover sentence",
                HintZoneFor(pinned, "pinned:") != Rectangle.Empty
                || HintZoneFor(pinned, "pinned: this reading") != Rectangle.Empty, "");

            // Clicking the pinned row again returns to lowest.
            formType.GetField("DragId", NP).SetValue(form, target);
            formType.GetField("ClickPinCandidate", NP).SetValue(form, target);
            formType.GetField("Dragging", NP).SetValue(form, false);
            formType.GetMethod("OnMouseUp", NP).Invoke(form,
                new object[] { new MouseEventArgs(MouseButtons.Left, 1, 40, 20, 0) });
            Check("clicking the pinned row unpins it back to lowest",
                (string)metric.GetValue(settings) == "lowest",
                "TrayMetric=" + metric.GetValue(settings));

            // A DRAG on the same row must NOT pin: the gestures are distinct.
            formType.GetField("DragId", NP).SetValue(form, target);
            formType.GetField("ClickPinCandidate", NP).SetValue(form, target);
            formType.GetField("Dragging", NP).SetValue(form, true);
            formType.GetMethod("OnMouseUp", NP).Invoke(form,
                new object[] { new MouseEventArgs(MouseButtons.Left, 1, 40, 60, 0) });
            Check("a drag on a row never pins it",
                (string)metric.GetValue(settings) == "lowest",
                "TrayMetric=" + metric.GetValue(settings));
        }
        finally
        {
            metric.SetValue(settings, savedMetric);
            accounts.Clear();
            formType.GetMethod("ShowTab").Invoke(form, new object[] { 2 });
        }
    }
}
