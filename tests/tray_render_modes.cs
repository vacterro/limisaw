using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Limisaw;

// The tray's state-space regression: production resolution + production
// rendering across the matrix the user can actually dial in.
//
//   LAYOUT   single / dual / gauge / bars / rows / grid   (all six modes)
//   SHOW     off / pct / time
//   FILL     8 / 100   (the user-facing choices; legacy 2/4 are migration tests)
//   COUNT    0 / 1 / 2 / 4 / 7 / 8 / 9 selected readings
//   DATA     all readable, mixed readable/unavailable, all unavailable,
//            genuine 0%, 1%, 34%, 99%, 100%
//   PIN      lowest / a valid readable metric / an existing-but-unavailable
//            metric / a vanished metric
//
// Section 4 pins the ONE-READING Bars bug the user reported: one narrow
// centred ~4px column, NOT a partition of the whole 14px interior — and
// every count 1..9 owning its own distinct slot in selected order.
// Section 7 pins the Gauge-vs-Rows semantic distinction (Gauge is the thin
// single-reading meter, Rows is the multi-reading family). Section 8 pins
// the horizontal fill arithmetic: the fill unit of Gauge/Rows is WIDTH, so
// 47% of a 14px bar is exactly 6px, never the old area-as-width overflow.
// Section 10 pins the legacy TrayFill 2/4 -> 8 settings migration.
// Sections 19-26 pin the SRC-027 decoupling of the tray NUMBER from TrayMax,
// and sections 30-34 pin the two automatic selectors apart: "lowest
// remaining" minimises over the complete eligible pool, while "any
// available" is a STABLE-ORDER selector that keeps its current usable reading
// and never compares percentages — including the 0%-is-not-usable rule and the
// truthful labelling of both modes under Show Used.
//
// The engine sources are LINKED (not reflected), so every private seam is
// reachable and what runs here is the same code the shell gets. Nothing here
// talks to a vendor: accounts are injected, the sweep is starved by an empty
// PATH, and Zcode's profile is a scratch directory.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class TrayRenderModesTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static Type formType;
    static LimisawSettings settings;
    static LimisawForm form;

    static object F(string f) { return formType.GetField(f, NP).GetValue(form); }
    static void FSet(string f, object v) { formType.GetField(f, NP).SetValue(form, v); }
    static object Call(string name, params object[] args)
    { return formType.GetMethod(name, NP).Invoke(form, args); }

    // ── fixtures ────────────────────────────────────────────────────────────
    static WindowData Win(string key, string label, int rem, bool available, bool short_,
                          double? epoch)
    {
        return new WindowData
        {
            Key = key, Base = key, Label = label, GroupLabel = "",
            Rem = rem, Available = available,
            DurationMinutes = short_ ? 300 : 10080,
            ResetEpoch = epoch,
        };
    }

    static WindowData Available5h(int rem) { return Win("five_hour", "5h", rem, true, true, Epoch(+2)); }
    static WindowData AvailableWeek(int rem) { return Win("weekly", "week", rem, true, false, Epoch(+72)); }
    static WindowData Dead5h() { return Win("five_hour", "5h", 0, false, true, Epoch(+2)); }
    static WindowData DeadWeek() { return Win("weekly", "week", 0, false, false, Epoch(+72)); }

    static double? Epoch(double hoursAhead)
    {
        return Stamp.Of(DateTime.UtcNow.AddHours(hoursAhead));
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

    static WindowData WinOf(AccountData a, string key)
    {
        foreach (WindowData w in a.Windows) if (w.Key == key) return w;
        return null;
    }

    static void Select(params string[] ids)
    {
        settings.TrayItems = ids.Length == 0 ? "" : string.Join("|", ids);
        settings.TrayHidden = "";
    }

    static Bitmap LiveRender()
    {
        MethodInfo render = formType.GetMethod("RenderTrayBitmap", NP, null, Type.EmptyTypes, null);
        using (Bitmap bmp = (Bitmap)render.Invoke(form, null))
            return new Bitmap(bmp);
    }

    // Every pixel of the 16x16 master that is neither the background nor the
    // border — i.e. the ink the tray actually drew.
    static List<Point> Ink(Bitmap bmp)
    {
        var ink = new List<Point>();
        for (int y = 1; y <= 14; y++)
            for (int x = 1; x <= 14; x++)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.ToArgb() != Palette.BG.ToArgb() && c.ToArgb() != Palette.BEVEL.ToArgb())
                    ink.Add(new Point(x, y));
            }
        return ink;
    }

    // The exact lit set the bitmap alphabet produces for `text` in `box` — the
    // same table, recomputed here so the renderer cannot drift from it.
    static HashSet<string> ExpectedInk(string text, Rectangle box, int forcedScale)
    {
        var lit = new HashSet<string>();
        int scale = forcedScale > 0 ? forcedScale : TrayGlyphs.FitScale(text, box.Width, box.Height);
        if (scale <= 0) return lit;
        int w = TrayGlyphs.Width(text, scale), h = TrayGlyphs.Height(scale);
        int x0 = box.X + (box.Width - w) / 2, y0 = box.Y + (box.Height - h) / 2;
        int x = x0;
        foreach (char ch in text)
        {
            string[] rows = Glyph(ch);
            if (rows != null)
                for (int r = 0; r < 5; r++)
                    for (int c = 0; c < 3; c++)
                        if (rows[r][c] == '1')
                            for (int sy = 0; sy < scale; sy++)
                                for (int sx = 0; sx < scale; sx++)
                                    lit.Add((x + c * scale + sx) + "," + (y0 + r * scale + sy));
            x += 4 * scale;
        }
        return lit;
    }

    static string[] Glyph(char ch)
    {
        switch (ch)
        {
            case '0': return N("111", "101", "101", "101", "111");
            case '1': return N("010", "110", "010", "010", "111");
            case '2': return N("111", "001", "111", "100", "111");
            case '3': return N("111", "001", "111", "001", "111");
            case '4': return N("101", "101", "111", "001", "001");
            case '5': return N("111", "100", "111", "001", "111");
            case '6': return N("111", "100", "111", "101", "111");
            case '7': return N("111", "001", "010", "010", "010");
            case '8': return N("111", "101", "111", "101", "111");
            case '9': return N("111", "101", "111", "001", "111");
            case '-': return N("000", "000", "111", "000", "000");
            case '<': return N("001", "010", "100", "010", "001");
            case 'm': return N("101", "111", "101", "101", "101");
            case 'h': return N("100", "100", "111", "101", "101");
            case 'd': return N("001", "001", "111", "101", "111");
            default: return null;
        }
    }

    static string[] N(params string[] rows) { return rows; }

    // The tooltip head is the reading claim: everything between the leading
    // "LIMISAW | " and the per-account tail (" | C1 55/37 ..."). The tail
    // lists accounts regardless of tray selection, so hidden-metric claims
    // are judged on the head only.
    static string TipHead(string tipText)
    {
        if (tipText == null) return "";
        int first = tipText.IndexOf(" | ", StringComparison.Ordinal);
        if (first < 0) return tipText;
        int second = tipText.IndexOf(" | ", first + 3, StringComparison.Ordinal);
        return second < 0 ? tipText.Substring(first + 3)
                          : tipText.Substring(first + 3, second - first - 3);
    }

    static bool SameInk(List<Point> got, HashSet<string> want)
    {
        if (got.Count != want.Count) return false;
        foreach (Point p in got) if (!want.Contains(p.X + "," + p.Y)) return false;
        return true;
    }

    static bool SameBitmap(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) return false;
        return true;
    }

    // Filled-width of a horizontal bar row: how many x columns in [1..14]
    // carry non-BG, non-BEVEL ink inside the y band [y0..y1]. The bitmap must
    // be pre-cleared to BG (an untouched pixel is transparent, not BG).
    static int FillWidth(Bitmap bmp, int y0, int y1)
    {
        int w = 0;
        for (int x = 1; x <= 14; x++)
            for (int y = y0; y <= y1; y++)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.ToArgb() != Palette.BG.ToArgb() && c.ToArgb() != Palette.BEVEL.ToArgb()) { w++; break; }
            }
        return w;
    }

    static TrayModel BuildTrayModel(LimisawForm form, string pin)
    {
        TrayModel m = form.BuildModel();
        m.Pin = pin;
        return m;
    }

    static Bitmap Scratch()
    {
        var bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
            g.Clear(Palette.BG);
        return bmp;
    }

    [DllImport("user32.dll")]
    static extern uint GetGuiResources(IntPtr process, uint flags);
    const uint GdiObjects = 0, UserObjects = 1;

    public static int Main()
    {
        string savedProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        string savedPath = Environment.GetEnvironmentVariable("PATH");
        string savedPrimary = Environment.GetEnvironmentVariable(ZcodeSource.EnvPrimary);
        string savedAlternate = Environment.GetEnvironmentVariable(ZcodeSource.EnvAlternate);
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_trayrm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // No vendor, no CLI, no Zcode profile: the constructor's own sweep
            // finds nothing and ends at once.
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string key in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(key, null);

            string root = Directory.GetCurrentDirectory();
            settings = new LimisawSettings(temp);
            settings.Load();
            List<Theme> themes = Theme.Load(root);
            formType = typeof(LimisawForm);
            using (var tray = new NotifyIcon())
            {
                form = new LimisawForm(temp, settings, tray, themes);
                Wait(() => !(bool)F("Refreshing"), 20000);
                Application.DoEvents();

                // ── 1. PINNED + UNAVAILABLE falls back, never "--" ──────────
                Install(
                    Acc("codex", "Account2", Available5h(55), AvailableWeek(37)),
                    Acc("zcode", "Zcode", Dead5h(), DeadWeek()));
                settings.TrayMode = "single";
                settings.TrayMetric = "zcode/Zcode/five_hour";
                settings.TrayMax = 9;
                Select();
                MethodInfo metric3 = formType.GetMethod("GetTrayMetric", NP, null,
                    new Type[] { typeof(int).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(string).MakeByRefType() }, null);
                var outs = new object[] { 0, false, null };
                metric3.Invoke(form, outs);
                Check("1. pinned-unavailable + a healthy selected 37% -> the tray answers 37",
                    (int)outs[0] == 37 && (bool)outs[1] && ((string)outs[2]).Contains("Account2"),
                    "value=" + outs[0] + " available=" + outs[1] + " label=" + outs[2]);
                TrayModel model = form.BuildModel();
                model.Pin = settings.TrayMetric;
                TrayReading r = form.ResolveReading(model);
                Check("1b. the fallback is real and disclosed",
                    r.Fallback && r.Note.Contains("pinned") && r.NoteShort.Length > 0,
                    "note=" + r.Note);
                using (Bitmap bmp = LiveRender())
                {
                    Check("1c. the icon renders 37, not --",
                        SameInk(Ink(bmp), ExpectedInk("37", new Rectangle(1, 1, 14, 14), 0)),
                        Ink(bmp).Count + " lit pixels");
                }
                string title = form.PopupTitle();
                Check("1d. the popup title discloses the fallback",
                    title.Contains("37%") && title.Contains("pinned"), title);
                MethodInfo tip = formType.GetMethod("BuildTip", NP);
                var tipArgs = new object[] { r };
                string tipText = (string)tip.Invoke(form, tipArgs);
                Check("1e. the tooltip discloses the fallback inside the 63-char budget",
                    tipText.Contains("[pin unavailable]") && tipText.Length <= 63,
                    tipText);

                // ── 2. PINNED + READABLE is authoritative ───────────────────
                settings.TrayMetric = "codex/Account2/weekly";
                model = form.BuildModel();
                model.Pin = settings.TrayMetric;
                r = form.ResolveReading(model);
                Check("2. a readable pin answers exactly, with no fallback",
                    !r.Fallback && r.Value == 37 && r.MetricId == "codex/Account2/weekly",
                    "value=" + r.Value + " id=" + r.MetricId);
                Check("2b. the pin's own reset epoch rides along (CORE-013)",
                    r.ResetEpoch.HasValue, "epoch=" + r.ResetEpoch);

                // ── 3. nothing readable -> "--" is correct ──────────────────
                Install(Acc("codex", "Account2", Dead5h(), DeadWeek()));
                settings.TrayMetric = "lowest";
                var outs3 = new object[] { 0, false, null };
                metric3.Invoke(form, outs3);
                Check("3. no readable metric: available=false, so the icon draws --",
                    !(bool)outs3[1] && (int)outs3[0] == 0, "available=" + outs3[1]);
                using (Bitmap bmp = LiveRender())
                {
                    Check("3b. and the icon really draws --",
                        SameInk(Ink(bmp), ExpectedInk("--", new Rectangle(1, 1, 14, 14), 0)),
                        Ink(bmp).Count + " lit pixels");
                }

                // ── 4. BARS: one reading is ONE NARROW CENTRED COLUMN ───────
                // The user's actual bug: a single selected reading used to
                // own the whole 14px interior (the old 14/n partition), which
                // made Bars and Cells look identical at n=1. Bars is columns:
                // one reading is one ~4px column centred in the icon, with a
                // large untouched background either side.
                Install(Acc("codex", "A1", Available5h(47)));
                settings.TrayMode = "bars";
                settings.TrayFill = 100;
                settings.TrayMax = 9;
                Select("codex/A1/five_hour");
                using (Bitmap bmp = LiveRender())
                {
                    // The BAR is the 4px band including its bevel outline: any
                    // non-background pixel. Fill ink shows inside the interior
                    // only (the 1px outline owns the band's edges).
                    var cols = new List<int>();
                    for (int x = 1; x <= 14; x++)
                        for (int y = 1; y <= 14; y++)
                        {
                            if (bmp.GetPixel(x, y).ToArgb() != Palette.BG.ToArgb()) { cols.Add(x); break; }
                        }
                    Check("4a. one-reading Bars lights exactly the centred 4px band x=6..9",
                        cols.Count == 4 && cols[0] == 6 && cols[3] == 9,
                        "lit columns: " + string.Join(",", cols.ConvertAll(v => v.ToString()).ToArray()));
                    Check("4b. large BG area remains to both sides (never the whole 14px interior)",
                        cols[0] >= 4 && cols[3] <= 11,
                        "first lit x=" + cols[0] + " last lit x=" + cols[3]);
                    // 47% exact = 26/56 area = 6 full rows + a 2px partial row
                    // (bottom-up), with the bevel outline owning the band's
                    // first and last rows. Pin the exact boundary: rows 2..7 of
                    // the interior stay empty track, rows 8..13 carry fill.
                    int emptyAbove = 0, filledMid = 0;
                    for (int y = 2; y <= 7; y++)
                        if (bmp.GetPixel(7, y).ToArgb() == Palette.BG.ToArgb()) emptyAbove++;
                    for (int y = 8; y <= 13; y++)
                    {
                        Color c = bmp.GetPixel(7, y);
                        if (c.ToArgb() != Palette.BG.ToArgb() && c.ToArgb() != Palette.BEVEL.ToArgb()) filledMid++;
                    }
                    Check("4c. the column carries the 47% fill: rows 2..7 empty, 8..13 filled",
                        emptyAbove == 6 && filledMid == 6,
                        "empty rows above=" + emptyAbove + " fill rows=" + filledMid);
                }

                // ── 4d. BARS carries every reading 1..9, each in its own slot,
                //        in the SELECTED order ───────────────────────────────
                // One window per account: the hide list can then hide exactly
                // the unselected tail (a second window per account would ride
                // back in as a "brand-new reading shown by default").
                var barAccounts = new List<AccountData>();
                for (int i = 0; i < 9; i++)
                    barAccounts.Add(Acc("codex", "A" + (i + 1), Available5h(10 + i * 10)));
                Install(barAccounts.ToArray());
                var barIds = new List<string>();
                foreach (AccountData a in barAccounts) barIds.Add(a.Key + "/five_hour");
                // The unselected tail must stay out of the icon: SelectedMetrics
                // shows brand-new readings by default, so each prefix run hides
                // everything but its own prefix.
                var barHiddenAll = string.Join("|", barIds.ToArray());
                settings.TrayFill = 100;
                for (int n = 1; n <= 9; n++)
                {
                    settings.TrayItems = string.Join("|", barIds.GetRange(0, n).ToArray());
                    settings.TrayHidden = string.Join("|", barIds.GetRange(n, barIds.Count - n).ToArray());
                    Check("4d." + n + " " + n + " reading(s) selected", form.TrayMetrics().Count == n,
                        form.TrayMetrics().Count.ToString());
                    using (Bitmap bmp = LiveRender())
                    {
                        // Column profile: which x columns belong to a bar (any
                        // non-background ink). Total lit columns == n*bw: every
                        // reading owns exactly its own slot, gaps stay BG.
                        int bw = n <= 2 ? 4 : n <= 4 ? 2 : 1;
                        var lit = new List<int>();
                        for (int x = 1; x <= 14; x++)
                            for (int y = 1; y <= 14; y++)
                                if (bmp.GetPixel(x, y).ToArgb() != Palette.BG.ToArgb()) { lit.Add(x); break; }
                        bool distinct = lit.Count == n * bw;
                        bool bounded = lit.Count > 0 && lit[0] >= 1 && lit[lit.Count - 1] <= 14
                            && (lit[lit.Count - 1] - lit[0] + 1) <= 14;
                        Check("4d." + n + "b every reading owns a distinct column slot",
                            distinct && bounded,
                            "lit=" + lit.Count + " want=" + (n * bw) + " range x=" +
                            (lit.Count > 0 ? lit[0] + ".." + lit[lit.Count - 1] : "none"));
                        // ORDER: the readings ascend 10..90, so fill heights
                        // never decrease left to right, and every slot is a
                        // different height (the 10-step spread maps to distinct
                        // fill rows). A slot is one column at bw==1, otherwise
                        // a maximal run of lit columns (its outline columns
                        // carry no fill ink, so the run's max is the fill).
                        if (n >= 2 && distinct)
                        {
                            var sample = new List<int>();
                            int runH = 0, prevX = int.MinValue;
                            foreach (int x in lit)
                            {
                                int rows = 0;
                                for (int y = 1; y <= 14; y++)
                                {
                                    Color c = bmp.GetPixel(x, y);
                                    if (c.ToArgb() != Palette.BG.ToArgb()
                                        && c.ToArgb() != Palette.BEVEL.ToArgb()) rows++;
                                }
                                if (bw == 1)
                                {
                                    sample.Add(rows);
                                    continue;
                                }
                                if (prevX == int.MinValue || x != prevX + 1)
                                {
                                    if (prevX != int.MinValue) sample.Add(runH);
                                    runH = rows;
                                }
                                else if (rows > runH) runH = rows;
                                prevX = x;
                            }
                            if (bw != 1) sample.Add(runH);
                            bool nonDecreasing = true;
                            for (int i = 1; i < sample.Count; i++)
                                if (sample[i] < sample[i - 1]) nonDecreasing = false;
                            Check("4d." + n + "c order stays the selected order (leftmost = first selected)",
                                sample.Count == n && nonDecreasing && sample[sample.Count - 1] > sample[0],
                                "slots=" + sample.Count + "/" + n + " heights=[" +
                                string.Join(",", sample.ConvertAll(v => v.ToString()).ToArray()) + "]");
                        }
                    }
                }

                // ── 5. CELLS carry every reading up to TrayMax ──────────────
                settings.TrayMode = "grid";
                Select(barIds.ToArray());
                using (Bitmap bmp = LiveRender())
                {
                    int filled = 0;
                    for (int i = 0; i < 9; i++)
                    {
                        int cx = 1 + (i % 3) * 4, cy = 1 + (i / 3) * 4;
                        // cell interiors: 4x4 with a 1px border, so sample the
                        // middle rows the fill owns
                        if (bmp.GetPixel(cx + 1, cy + 3).ToArgb() != Palette.BG.ToArgb()) filled++;
                    }
                    Check("5. all 9 cells render at 100%",
                        filled == 9, filled + " of 9 cells filled");
                }

                // ── 5b. ONE-READING Cells is the FULL BLOCK, and differs from
                //        one-reading Bars for a semantic geometry reason ────
                // Same prefix+hide discipline as 4d: the tail must not ride
                // back in as brand-new readings shown by default.
                settings.TrayItems = barIds[0];
                settings.TrayHidden = string.Join("|", barIds.GetRange(1, barIds.Count - 1).ToArray());
                settings.TrayFill = 100;
                Bitmap barsOne, cellsOne;
                settings.TrayMode = "bars";
                barsOne = LiveRender();
                settings.TrayMode = "grid";
                cellsOne = LiveRender();
                Check("5b. one-reading Bars and one-reading Cells are different bitmaps",
                    !SameBitmap(barsOne, cellsOne), "identical bitmaps");
                // Cells at n=1 may own the FULL inner block: its 47% fill spans
                // the whole 14px width (top-down area fill), while Bars spans
                // only its narrow column.
                int cellsSpan = 0, barsSpan = 0;
                for (int x = 1; x <= 14; x++)
                    for (int y = 1; y <= 14; y++)
                        if (cellsOne.GetPixel(x, y).ToArgb() != Palette.BG.ToArgb()) { cellsSpan++; break; }
                for (int x = 1; x <= 14; x++)
                    for (int y = 1; y <= 14; y++)
                        if (barsOne.GetPixel(x, y).ToArgb() != Palette.BG.ToArgb()) { barsSpan++; break; }
                Check("5c. Cells fills across the full block width; Bars stays a narrow column",
                    cellsSpan == 14 && barsSpan == 4,
                    "cells span=" + cellsSpan + " bars span=" + barsSpan);
                barsOne.Dispose(); cellsOne.Dispose();

                // ── 5d. Cells: Left and Used fill OPPOSITE directions ───────
                // Non-symmetric fixture remaining=70. LEFT fills 70% from the
                // TOP with the remaining amount; USED fills 30% from the
                // BOTTOM with the used amount. Only amount+direction express
                // the toggle; colour still reads REMAINING, so the same reading
                // is the same colour in both modes.
                settings.TrayFill = 100;
                settings.TrayMax = 9;
                Install(Acc("codex", "C1", Available5h(70)));
                Select("codex/C1/five_hour");
                Color leftTop, leftBottom, usedTop, usedBottom;
                settings.ShowUsed = false;
                using (Bitmap bmp = LiveRender())
                {
                    leftTop = bmp.GetPixel(8, 2); leftBottom = bmp.GetPixel(8, 13);
                }
                settings.ShowUsed = true;
                using (Bitmap bmp = LiveRender())
                {
                    usedTop = bmp.GetPixel(8, 2); usedBottom = bmp.GetPixel(8, 13);
                }
                settings.ShowUsed = false;
                Check("5d.LEFT 70% fills from the TOP (top edge inked, bottom edge empty)",
                    leftTop.ToArgb() != Palette.BG.ToArgb() && leftBottom.ToArgb() == Palette.BG.ToArgb(),
                    "top=" + leftTop + " bottom=" + leftBottom);
                Check("5d.USED 30% fills from the BOTTOM (bottom edge inked, top edge empty)",
                    usedBottom.ToArgb() != Palette.BG.ToArgb() && usedTop.ToArgb() == Palette.BG.ToArgb(),
                    "top=" + usedTop + " bottom=" + usedBottom);
                Check("5d.COLOUR both modes paint the REMAINING colour (not inverted)",
                    leftTop.ToArgb() == usedBottom.ToArgb(),
                    "left-top=" + leftTop + " used-bottom=" + usedBottom);
                // Coherence at the extremes: 100% remaining is a full LEFT cell
                // and an empty USED cell (0% used); 0% remaining is the mirror.
                Install(Acc("codex", "C2", Available5h(100)));
                Select("codex/C2/five_hour");
                using (Bitmap bmp = LiveRender())
                    Check("5d.LEFT 100% fills the whole interior",
                        bmp.GetPixel(8, 13).ToArgb() != Palette.BG.ToArgb(), "");
                settings.ShowUsed = true;
                using (Bitmap bmp = LiveRender())
                    Check("5d.USED at 100% remaining (0% used) leaves the cell empty",
                        bmp.GetPixel(8, 13).ToArgb() == Palette.BG.ToArgb(), "");
                settings.ShowUsed = false;
                Install(Acc("codex", "C3", Available5h(0)));
                Select("codex/C3/five_hour");
                using (Bitmap bmp = LiveRender())
                    Check("5d.LEFT at 0% leaves the cell empty",
                        bmp.GetPixel(8, 2).ToArgb() == Palette.BG.ToArgb(), "");
                settings.ShowUsed = true;
                using (Bitmap bmp = LiveRender())
                    Check("5d.USED at 0% remaining (100% used) fills the whole interior",
                        bmp.GetPixel(8, 2).ToArgb() != Palette.BG.ToArgb(), "");
                settings.ShowUsed = false;
                // An unavailable reading stays empty in BOTH modes.
                Install(Acc("codex", "C4", Dead5h()));
                Select("codex/C4/five_hour");
                // Sample off-centre: an unavailable cell keeps its single MUTED
                // marker pixel at the exact centre, so the fill region must be
                // empty everywhere else.
                using (Bitmap bmp = LiveRender())
                    Check("5d.LEFT an unavailable cell carries no fill",
                        bmp.GetPixel(4, 4).ToArgb() == Palette.BG.ToArgb(), "");
                settings.ShowUsed = true;
                using (Bitmap bmp = LiveRender())
                    Check("5d.USED an unavailable cell carries no fill",
                        bmp.GetPixel(4, 4).ToArgb() == Palette.BG.ToArgb(), "");
                settings.ShowUsed = false;
                // ── 6. a real 0% is distinct from an unavailable reading ────
                Install(
                    Acc("codex", "A1", Available5h(0)),
                    Acc("zcode", "Z", Dead5h()));
                Select("codex/A1/five_hour", "zcode/Z/five_hour");
                settings.TrayMode = "bars";
                using (Bitmap bmp = LiveRender())
                {
                    // n=2 -> two 7px slots; centres at x=4 and x=11, y=8.
                    Color zero = bmp.GetPixel(4, 8);
                    Color dead = bmp.GetPixel(11, 8);
                    Check("6a. a genuine 0% leaves its track empty",
                        zero.ToArgb() == Palette.BG.ToArgb(), zero.ToString());
                    Check("6b. an unavailable reading carries the muted marker",
                        dead.ToArgb() == Palette.MUTED.ToArgb(), dead.ToString());
                }

                // ── 7. "34" is deterministic pixel typography ───────────────
                Install(Acc("codex", "A1", Available5h(34)));
                Select("codex/A1/five_hour");
                settings.TrayMode = "single";
                settings.TrayShow = "pct";
                using (Bitmap bmp = LiveRender())
                {
                    List<Point> got = Ink(bmp);
                    Check("7a. '34' renders as the exact glyph bitmap at scale 2",
                        SameInk(got, ExpectedInk("34", new Rectangle(1, 1, 14, 14), 0)),
                        got.Count + " lit vs " + ExpectedInk("34", new Rectangle(1, 1, 14, 14), 0).Count);
                    Check("7b. '34' is readable-sized: two glyphs at half height",
                        got.Count > 50 && got.Count < 120, got.Count + " lit pixels");
                }
                Check("7c. the glyph table itself says scale 2 fits 14px",
                    TrayGlyphs.FitScale("34", 14, 14) == 2, "scale=" + TrayGlyphs.FitScale("34", 14, 14));

                // ── 8. 100 fits the tray cell ───────────────────────────────
                Install(Acc("codex", "A1", Available5h(100)));
                using (Bitmap bmp = LiveRender())
                {
                    List<Point> got = Ink(bmp);
                    bool inside = true;
                    foreach (Point p in got)
                        if (p.X < 1 || p.X > 14 || p.Y < 1 || p.Y > 14) inside = false;
                    Check("8. '100' renders fully inside the 14x14 interior",
                        inside && got.Count > 0, got.Count + " lit pixels, inside=" + inside);
                }

                // ── 9. dual keeps independent short/long semantics ──────────
                Install(
                    Acc("codex", "A1", Available5h(34), AvailableWeek(90)));
                settings.TrayMode = "dual";
                using (Bitmap bmp = LiveRender())
                {
                    Check("9a. dual top half renders the short reading exactly",
                        SameInk(Ink(bmp).FindAll(p => p.Y <= 7), ExpectedInk("34", new Rectangle(1, 1, 14, 7), 0)),
                        "top ink=" + Ink(bmp).FindAll(p => p.Y <= 7).Count);
                    Check("9b. dual bottom half renders the long reading exactly",
                        SameInk(Ink(bmp).FindAll(p => p.Y >= 8), ExpectedInk("90", new Rectangle(1, 8, 14, 7), 0)),
                        "bottom ink=" + Ink(bmp).FindAll(p => p.Y >= 8).Count);
                }
                Install(
                    Acc("codex", "A1", Dead5h(), AvailableWeek(90)));
                using (Bitmap bmp = LiveRender())
                {
                    Check("9c. a dead short half reads --, never a faked long value",
                        SameInk(Ink(bmp).FindAll(p => p.Y <= 7), ExpectedInk("--", new Rectangle(1, 1, 14, 7), 0)),
                        "top ink=" + Ink(bmp).FindAll(p => p.Y <= 7).Count);
                    Check("9d. the long half stays 90",
                        SameInk(Ink(bmp).FindAll(p => p.Y >= 8), ExpectedInk("90", new Rectangle(1, 8, 14, 7), 0)),
                        "bottom ink=" + Ink(bmp).FindAll(p => p.Y >= 8).Count);
                }

                // ── 9e. HORIZONTAL FILL IS WIDTH, NOT PIXEL AREA ────────────
                // The defect this pins: the old shared routine computed the
                // fill as an AREA (width*height*pct) and then reused that AREA
                // as a WIDTH on the partial final column, painting extra
                // full-height columns. A 14px Gauge/Rows bar at 47% must be
                // EXACTLY floor(14*47/100) = 6px wide, at every row height.
                MethodInfo fillHoriz = formType.GetMethod("FillHorizontal", NP);
                settings.TrayFill = 100;
                var widths = new Dictionary<int, int[]>();
                foreach (int pct in new[] { 0, 1, 31, 47, 59, 99, 100 })
                {
                    using (Bitmap bmp = Scratch())
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        int shown = pct; // ShowUsed=false -> shown == remaining
                        var box = new Rectangle(1, 6, 14, 4);
                        fillHoriz.Invoke(form, new object[] { g, box, shown });
                        int w = FillWidth(bmp, 6, 9);
                        widths[pct] = new[] { w };
                        int want = 14 * pct / 100;
                        Check("9e." + pct + "a Gauge 47%-class exact fill is floor(width*pct/100) px",
                            w == want, "pct=" + pct + " got " + w + " want " + want);
                    }
                }
                Check("9e.47a the 47% width is exactly 6px — the pinned flooring rule",
                    widths[47][0] == 6, widths[47][0] + "px");
                // 1/8 quantisation maps onto the row's own width too.
                settings.TrayFill = 8;
                var eighth = new Dictionary<int, int>();
                foreach (int pct in new[] { 0, 47, 100 })
                {
                    using (Bitmap bmp = Scratch())
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        var box = new Rectangle(1, 6, 14, 4);
                        fillHoriz.Invoke(form, new object[] { g, box, pct });
                        eighth[pct] = FillWidth(bmp, 6, 9);
                    }
                }
                int steps47 = 47 * 8 / 100; // 3
                Check("9e.47b 1/8 quantises 47% to its eighth and maps onto the 14px width",
                    eighth[47] == 14 * steps47 / 8, "got " + eighth[47] + " want " + (14 * steps47 / 8));
                Check("9e.47c 1/8 and Exact genuinely differ at 47%",
                    eighth[47] != widths[47][0], "both " + eighth[47]);
                Check("9e.100a 100% fills the whole row in both modes",
                    eighth[100] == 14, eighth[100] + "px");

                // ── 9f. ONE-READING GAUGE vs ONE-READING ROWS: NOT IDENTICAL ─
                // Gauge is the minimal single-reading meter (its own thin
                // band); Rows is the multi-reading layout family, whose
                // one-row case keeps the row height. If both ever render
                // pixel-identically, one of the two layouts is decoration.
                Install(Acc("codex", "A1", Available5h(47)));
                Select("codex/A1/five_hour");
                settings.TrayFill = 100;
                settings.TrayShow = "off";
                settings.TrayMode = "gauge";
                Bitmap gaugeOne = LiveRender();
                settings.TrayMode = "rows";
                Bitmap rowsOne = LiveRender();
                Check("9f. one-reading Gauge and one-reading Rows are different bitmaps",
                    !SameBitmap(gaugeOne, rowsOne), "identical bitmaps");
                int gaugeH = 0, rowsH = 0;
                for (int y = 1; y <= 14; y++)
                {
                    bool gLit = false, rLit = false;
                    for (int x = 1; x <= 14; x++)
                    {
                        Color cg = gaugeOne.GetPixel(x, y);
                        Color cr = rowsOne.GetPixel(x, y);
                        if (cg.ToArgb() != Palette.BG.ToArgb() && cg.ToArgb() != Palette.BEVEL.ToArgb()) gLit = true;
                        if (cr.ToArgb() != Palette.BG.ToArgb() && cr.ToArgb() != Palette.BEVEL.ToArgb()) rLit = true;
                    }
                    if (gLit) gaugeH++;
                    if (rLit) rowsH++;
                }
                Check("9f.2 Gauge's band is thinner than the Rows family's row height",
                    gaugeH > 0 && rowsH > 0 && gaugeH != rowsH,
                    "gauge band=" + gaugeH + " rows band=" + rowsH);
                gaugeOne.Dispose(); rowsOne.Dispose();

                // ── 10. the whole matrix renders without exceptions ─────────
                // All SIX modes, the two user-facing fills, TrayShow where it
                // is semantically meaningful, every legal reading count and
                // data shape. Legacy fills 2/4 are NOT render options; they
                // are covered by the settings migration tests in section 10b.
                var broken = new List<string>();
                foreach (string mode in new[] { "single", "dual", "gauge", "bars", "rows", "grid" })
                    foreach (string show in new[] { "off", "pct", "time" })
                        foreach (int fill in new[] { 8, 100 })
                            foreach (int count in new[] { 0, 1, 2, 4, 7, 8, 9 })
                                foreach (int shape in new[] { 0, 1, 2, 3, 4 })
                                {
                                    settings.TrayMode = mode;
                                    settings.TrayShow = show;
                                    settings.TrayFill = fill;
                                    settings.TrayMax = 9;
                                    Fixture(shape, count);
                                    try
                                    {
                                        using (Bitmap bmp = LiveRender()) { }
                                    }
                                    catch (Exception ex)
                                    {
                                        broken.Add(mode + "/" + show + "/" + fill + "/" + count
                                            + "/" + shape + ": " + ex.GetType().Name);
                                    }
                                }
                Check("10. every layout x show x fill x count x data shape renders",
                    broken.Count == 0, broken.Count == 0
                        ? "6x3x2x7x5 = 1260 renders" : broken[0]);

                // ── 10b. legacy TrayFill 2/4 normalize to 8 on load, and stay
                //         written only after a real change ───────────────────
                string iniPath = settings.IniPath;
                foreach (var legacy in new[] { "2", "4" })
                {
                    File.WriteAllText(iniPath, "[limisaw]\r\nTrayFill=" + legacy + "\r\n");
                    var s2 = new LimisawSettings(temp);
                    s2.Load();
                    Check("10b." + legacy + "a legacy TrayFill=" + legacy + " loads as 8",
                        s2.TrayFill == 8, s2.TrayFill.ToString());
                }
                File.WriteAllText(iniPath, "[limisaw]\r\nTrayFill=8\r\n");
                var s8 = new LimisawSettings(temp);
                s8.Load();
                Check("10b.8a TrayFill=8 stays 8", s8.TrayFill == 8, s8.TrayFill.ToString());
                File.WriteAllText(iniPath, "[limisaw]\r\nTrayFill=100\r\n");
                var s100 = new LimisawSettings(temp);
                s100.Load();
                Check("10b.100a TrayFill=100 stays 100", s100.TrayFill == 100, s100.TrayFill.ToString());
                // The durable-baseline contract: loading a legacy value must not
                // immediately rewrite the disk behind the user's back.
                File.WriteAllText(iniPath, "[limisaw]\r\nTrayFill=2\r\n");
                var sNoWrite = new LimisawSettings(temp);
                sNoWrite.Load();
                string diskAfterLoad = File.ReadAllText(iniPath);
                Check("10b.2b loading a legacy 2 does not rewrite the disk",
                    diskAfterLoad.IndexOf("TrayFill=2", StringComparison.Ordinal) >= 0,
                    diskAfterLoad.Replace("\r\n", "\\n"));
                Check("10b.2c the in-memory value is nevertheless the new 8",
                    sNoWrite.TrayFill == 8, sNoWrite.TrayFill.ToString());

                // ── 11. rapid mode changes never crash ──────────────────────
                Fixture(1, 4);
                string[] cycle = { "single", "dual", "gauge", "bars", "rows", "grid" };
                bool crashed = false;
                string crash = "";
                try
                {
                    for (int i = 0; i < 60 * cycle.Length; i++)
                    {
                        settings.TrayMode = cycle[i % cycle.Length];
                        Application.DoEvents();
                        form.UpdateTray();
                        Application.DoEvents();
                    }
                    // Preview rendering and theme repaint across the same churn.
                    for (int i = 0; i < 60; i++)
                    {
                        MethodInfo previewBmp = formType.GetMethod("RenderPreviewBitmap", NP, null, Type.EmptyTypes, null);
                        using (var pb = (Bitmap)previewBmp.Invoke(form, null)) { }
                        form.ApplyTheme(settings.ThemeSlug);
                        form.UpdateTray();
                        if (i % 10 == 0) Application.DoEvents();
                    }
                }
                catch (Exception ex) { crashed = true; crash = ex.GetType().Name + ": " + ex.Message; }
                Check("11. 360 six-mode cycles + 60 preview/theme repaints survive",
                    !crashed, crash);

                // ── 12. the preview never mutates the real tray ─────────────
                Fixture(2, 3);
                settings.TrayMode = "bars";
                Bitmap real1 = LiveRender();
                Bitmap fake;
                MethodInfo preview = formType.GetMethod("RenderPreviewBitmap", NP, null, Type.EmptyTypes, null);
                using (fake = (Bitmap)preview.Invoke(form, null)) { }
                Bitmap real2 = LiveRender();
                bool identical = real1.Width == real2.Width;
                if (identical)
                    for (int y = 0; y < real1.Height && identical; y++)
                        for (int x = 0; x < real1.Width && identical; x++)
                            if (real1.GetPixel(x, y).ToArgb() != real2.GetPixel(x, y).ToArgb())
                                identical = false;
                real1.Dispose(); real2.Dispose();
                Check("12. a preview pass leaves the real render bit-identical",
                    identical, identical ? "identical" : "the real picture moved");
                Check("12b. no mutable preview flag exists on the form",
                    formType.GetField("PreviewPct", NP) == null, "field absent");

                // ── 12c. USER-FACING FILL CONTRACT ──────────────────────────
                // Settings + the tray context menu project the SAME one fill
                // metadata model: exactly 1/8 and Exact, never 2 or 4.
                var fillDefs = (Array)LimisawSettings.TrayFills;
                var fillValues = new List<int>();
                foreach (object fd in fillDefs)
                    fillValues.Add((int)fd.GetType().GetField("Value").GetValue(fd));
                Check("12c. exactly two user-facing fill options: 8 and 100",
                    fillValues.Count == 2 && fillValues.Contains(8) && fillValues.Contains(100),
                    string.Join(",", fillValues.ConvertAll(v => v.ToString()).ToArray()));
                var modeDefs = (Array)LimisawSettings.TrayModes;
                var modeIds = new List<string>();
                foreach (object md in modeDefs)
                    modeIds.Add((string)md.GetType().GetField("Id").GetValue(md));
                Check("12d. all six layouts are registered metadata",
                    modeIds.Count == 6 && modeIds.Contains("single") && modeIds.Contains("dual")
                        && modeIds.Contains("gauge") && modeIds.Contains("bars")
                        && modeIds.Contains("rows") && modeIds.Contains("grid"),
                    string.Join(",", modeIds.ToArray()));

                // ── 13. pixel purity of the glyphs (33 / 34 / 100 / --) ─────
                var allowed = new HashSet<int>
                {
                    Palette.BG.ToArgb(), Palette.BEVEL.ToArgb(), Palette.BDARK.ToArgb(),
                    Palette.MUTED.ToArgb(), Palette.DANGERTXT.ToArgb(), Palette.WARNING.ToArgb(),
                    Palette.LINK.ToArgb(), Palette.SUCCESS.ToArgb(), Palette.TEXT.ToArgb(),
                };
                var impure = new List<string>();
                foreach (int pct in new[] { 33, 34, 100 })
                {
                    Install(Acc("codex", "A1", Available5h(pct)));
                    settings.TrayMode = "single";
                    using (Bitmap bmp = LiveRender())
                    {
                        for (int y = 0; y < bmp.Height; y++)
                            for (int x = 0; x < bmp.Width; x++)
                            {
                                Color c = bmp.GetPixel(x, y);
                                if (c.A < 255) continue;
                                if (!allowed.Contains(c.ToArgb())) impure.Add(pct + ": " + c.ToString());
                            }
                    }
                }
                Install(Acc("codex", "A1", Dead5h()));
                using (Bitmap bmp = LiveRender())
                {
                    for (int y = 0; y < bmp.Height; y++)
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            if (c.A < 255) continue;
                            if (!allowed.Contains(c.ToArgb())) impure.Add("--: " + c.ToString());
                        }
                }
                Check("13. tray glyph pixels are palette-exact at 33/34/100/--",
                    impure.Count == 0, impure.Count == 0 ? "no blend anywhere" : impure[0]);

                // ── 14. time tokens fit and render ──────────────────────────
                settings.TrayShow = "time";
                Install(Acc("codex", "A1", Available5h(55), AvailableWeek(37)));
                settings.TrayMode = "single";
                settings.TrayMetric = "codex/A1/weekly";
                using (Bitmap bmp = LiveRender())
                {
                    List<Point> got = Ink(bmp);
                    Check("14. a ~72h reset renders a '3d' token",
                        SameInk(got, ExpectedInk("3d", new Rectangle(1, 1, 14, 14), 0)),
                        got.Count + " lit pixels");
                }
                settings.TrayShow = "pct";

                // ── 15. resource stability across repeated renders ──────────
                Fixture(2, 4);
                settings.TrayMode = "bars";
                IntPtr proc = Process.GetCurrentProcess().Handle;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                uint gdiBefore = GetGuiResources(proc, GdiObjects);
                uint userBefore = GetGuiResources(proc, UserObjects);
                for (int i = 0; i < 200; i++)
                {
                    using (Bitmap bmp = LiveRender()) { }
                    form.UpdateTray();
                    if (i % 25 == 0) Application.DoEvents();
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Application.DoEvents();
                uint gdiAfter = GetGuiResources(proc, GdiObjects);
                uint userAfter = GetGuiResources(proc, UserObjects);
                Check("15a. 200 renders + tray updates leak no GDI handles",
                    gdiAfter <= gdiBefore + 40,
                    gdiBefore + " -> " + gdiAfter);
                Check("15b. 200 renders + tray updates leak no USER handles",
                    userAfter <= userBefore + 40,
                    userBefore + " -> " + userAfter);

                // ── 16. hidden readings stay hidden ─────────────────────────
                // model.Items is the eligible tray selection; model.All also
                // carries readings the user explicitly hid. An emptied
                // selection must answer "--", never resurrect a hidden metric,
                // and an unavailable pin's fallback must look only at eligible
                // readings. The tooltip tail lists every account regardless of
                // tray selection, so the head (before the first account
                // piece) is the surface these checks judge.
                Install(Acc("codex", "A1", Available5h(55), AvailableWeek(37)));
                settings.TrayMode = "single";
                settings.TrayShow = "pct";
                settings.TrayMetric = "lowest";
                settings.TrayMax = 9;
                // Everything hidden: Select() seeds the order, then hide all.
                var allIds = new List<string>();
                foreach (Metric m in form.AllMetrics()) allIds.Add(m.Id);
                settings.TrayItems = string.Join("|", allIds.ToArray());
                settings.TrayHidden = string.Join("|", allIds.ToArray());
                var outs16 = new object[] { 0, false, null };
                metric3.Invoke(form, outs16);
                Check("16a. all readings hidden + all readable -> '--', never a hidden metric",
                    !(bool)outs16[1] && (int)outs16[0] == 0,
                    "value=" + outs16[0] + " available=" + outs16[1]);
                TrayModel hidden = form.BuildModel();
                hidden.Pin = "lowest";
                Check("16b. the eligible set is really empty while All still reads",
                    form.TrayMetrics().Count == 0 && form.AllMetrics().Count == 2,
                    "items=" + form.TrayMetrics().Count + " all=" + form.AllMetrics().Count);
                TrayReading r16 = form.ResolveReading(hidden);
                Check("16c. the resolver itself answers unavailable over Items-only scope",
                    !r16.Available, "available=" + r16.Available + " id=" + r16.MetricId);
                using (Bitmap bmp = LiveRender())
                {
                    Check("16d. the icon draws -- and never a hidden metric's number",
                        SameInk(Ink(bmp), ExpectedInk("--", new Rectangle(1, 1, 14, 14), 0)),
                        Ink(bmp).Count + " lit pixels");
                }
                string head16 = TipHead((string)tip.Invoke(form, new object[] { r16 }));
                Check("16e. the tooltip head claims no hidden metric",
                    head16.Contains("--") && head16.IndexOf("55", StringComparison.Ordinal) < 0
                        && head16.IndexOf("37", StringComparison.Ordinal) < 0,
                    head16);
                string title16 = form.PopupTitle();
                Check("16f. the popup title claims no hidden metric",
                    title16.Contains("no reading") && title16.IndexOf("55", StringComparison.Ordinal) < 0
                        && title16.IndexOf("37", StringComparison.Ordinal) < 0,
                    title16);

                // ── 17. an unavailable hidden pin falls back to eligible only ─
                Install(
                    Acc("codex", "A1", Available5h(55), AvailableWeek(37)),
                    Acc("zcode", "Z", Dead5h(), DeadWeek()));
                settings.TrayMetric = "zcode/Z/five_hour";
                Select();  // everything eligible again
                TrayReading pinnedOk = form.ResolveReading(BuildTrayModel(form, settings.TrayMetric));
                Check("17a. an unavailable pin falls back to the selected healthy reading",
                    pinnedOk.Available && pinnedOk.Value == 37 && pinnedOk.Fallback,
                    "value=" + pinnedOk.Value + " fallback=" + pinnedOk.Fallback);
                // The role fixture: a dead explicit pin, healthy readings that
                // are ALL hidden, no eligible selection at all -> "--" even
                // though hidden 55/37 remain perfectly readable in All. The
                // hide list is rebuilt from the CURRENT fleet, the pin dead
                // window included, so nothing can ride back in by discovery.
                var hideIds = new List<string>();
                foreach (Metric m in form.AllMetrics()) hideIds.Add(m.Id);
                settings.TrayItems = string.Join("|", hideIds.ToArray());
                settings.TrayHidden = string.Join("|", hideIds.ToArray());
                TrayReading pinDead = form.ResolveReading(BuildTrayModel(form, settings.TrayMetric));
                Check("17b. unavailable hidden pin + no eligible readings -> '--', not a hidden 37",
                    !pinDead.Available && form.TrayMetrics().Count == 0,
                    "available=" + pinDead.Available + " items=" + form.TrayMetrics().Count);
                using (Bitmap bmp = LiveRender())
                {
                    Check("17c. the icon draws -- for the dead hidden pin",
                        SameInk(Ink(bmp), ExpectedInk("--", new Rectangle(1, 1, 14, 14), 0)),
                        Ink(bmp).Count + " lit pixels");
                }
                string head17 = TipHead((string)tip.Invoke(form, new object[] { pinDead }));
                Check("17d. the tooltip head discloses the fallback without a hidden value",
                    head17.Contains("[pin unavailable]") && head17.Contains("--")
                        && head17.IndexOf("37", StringComparison.Ordinal) < 0
                        && head17.IndexOf("55", StringComparison.Ordinal) < 0,
                    head17);
                // One eligible healthy reading survives hiding: the fallback
                // must reach exactly that one.
                Install(
                    Acc("codex", "A1", Available5h(55), AvailableWeek(37)),
                    Acc("zcode", "Z", Dead5h(), DeadWeek()));
                settings.TrayMetric = "zcode/Z/five_hour";
                settings.TrayItems = "codex/A1/weekly";
                settings.TrayHidden = "codex/A1/five_hour|zcode/Z/five_hour|zcode/Z/weekly";
                TrayReading pinOne = form.ResolveReading(BuildTrayModel(form, settings.TrayMetric));
                Check("17e. the fallback uses the one eligible healthy reading",
                    pinOne.Available && pinOne.Value == 37 && pinOne.MetricId == "codex/A1/weekly",
                    "value=" + pinOne.Value + " id=" + pinOne.MetricId);
                // The readable hidden pin keeps its documented authority.
                Install(Acc("codex", "A1", Available5h(55), AvailableWeek(37)));
                settings.TrayItems = "codex/A1/weekly";
                settings.TrayHidden = "codex/A1/weekly";
                settings.TrayMetric = "codex/A1/weekly";
                TrayReading pinHidden = form.ResolveReading(BuildTrayModel(form, settings.TrayMetric));
                Check("17f. a READABLE hidden pin stays authoritative (explicit choice)",
                    pinHidden.Available && pinHidden.Value == 37 && !pinHidden.Fallback,
                    "value=" + pinHidden.Value + " fallback=" + pinHidden.Fallback);

                // ── 18. one UpdateTray = one snapshot, one resolution ────────
                // The tooltip and the bitmap must be drawn from the SAME model
                // snapshot and the SAME resolved reading. The form carries two
                // regression counters: BuildModel increments TraySnapshots,
                // ResolveReading increments TrayResolutions. One update must
                // cost exactly one of each.
                Install(Acc("codex", "A1", Available5h(34)));
                Select("codex/A1/five_hour");
                settings.TrayMetric = "lowest";
                FSet("TraySnapshots", 0);
                FSet("TrayResolutions", 0);
                form.UpdateTray();
                int snaps = (int)F("TraySnapshots"), res = (int)F("TrayResolutions");
                Check("18a. one UpdateTray builds exactly one model snapshot",
                    snaps == 1, "snapshots=" + snaps);
                Check("18b. one UpdateTray resolves exactly one reading for tip+icon",
                    res == 1, "resolutions=" + res);
                Check("18c. the snapshot update left no tray error",
                    (string)F("TrayError") == "", (string)F("TrayError"));

                // ── 19. TrayMax=4 does not hide an eligible fifth reading from lowest ─────
                // Install 5 readings, set TrayMax=4, verify lowest still picks from beyond TrayMax
                Install(
                    Acc("codex", "A1", Available5h(80), AvailableWeek(70)),
                    Acc("claude", "Claude", Available5h(60), AvailableWeek(50)),
                    Acc("antigravity", "Ag", Available5h(40), AvailableWeek(30)),
                    Acc("zcode", "Z", Available5h(20), AvailableWeek(10)),
                    Acc("gpt4", "GPT4", Available5h(5), AvailableWeek(1)));  // 5th account, weekly=1%
                settings.TrayMax = 4;
                settings.TrayMetric = "lowest";
                settings.TrayItems = "";  // discovery order
                // BuildModel populates both Items (capped) and Eligible (uncapped)
                TrayModel bm = form.BuildModel();
                TrayReading lowest5 = form.ResolveReading(bm);
                Check("19a. TrayMax=4 still picks from beyond cap (gpt4 weekly=1%)",
                    lowest5.Available && lowest5.Value == 1,
                    "value=" + lowest5.Value + " id=" + lowest5.MetricId);
                // But tray items are capped at 4
                Check("19b. TrayMetrics() still returns only 4 items (TrayMax applied)",
                    form.TrayMetrics().Count == 4, "count=" + form.TrayMetrics().Count);

                // ── 20. Any available prefers current usable reading ─────
                // Set to "any" mode, verify it picks a reading, then "refresh" with it still usable
                settings.TrayMetric = "any";
                TrayModel am = form.BuildModel();
                TrayReading any1 = form.ResolveReading(am);
                Check("20a. any available picks a usable reading",
                    any1.Available && any1.Value > 0,
                    "value=" + any1.Value + " available=" + any1.Available);
                // Simulate refresh (same reading still usable) — set CurrentMetricId to what any1 picked
                TrayModel am2 = form.BuildModel();
                am2.CurrentMetricId = any1.MetricId;
                TrayReading any2 = form.ResolveReading(am2);
                Check("20b. any available keeps same reading on refresh",
                    any1.MetricId == any2.MetricId,
                    "same id=" + any1.MetricId + " vs " + any2.MetricId);

                // ── 21. Any available switches when current becomes unusable ─────
                // Simulate the previously-picked reading now having 0% (unusable)
                TrayModel am3 = form.BuildModel();
                am3.CurrentMetricId = any1.MetricId;
                foreach (Metric m in am3.Eligible)
                    if (m.Id == any1.MetricId) { m.Value = 0; m.Available = false; break; }
                TrayReading any3 = form.ResolveReading(am3);
                Check("21a. any available switches to a different reading when current hits 0%",
                    any3.MetricId != any1.MetricId && any3.Available,
                    "switched from " + any1.MetricId + " to " + any3.MetricId);

                // ── 22. Lowest remaining selects global positive minimum, not capped ─────
                // Put a reading beyond the TrayMax cap and verify lowest finds it
                Install(
                    Acc("codex", "A1", Available5h(55), AvailableWeek(37)),
                    Acc("zcode", "Z", Available5h(99), AvailableWeek(1)));
                settings.TrayMax = 4;
                settings.TrayMetric = "lowest";
                settings.TrayItems = "codex/A1/five_hour|codex/A1/weekly|zcode/Z/five_hour|zcode/Z/weekly";
                bm = form.BuildModel();
                // Eligible should include all non-hidden (all 4 here, none hidden)
                Check("22a. Eligible pool includes all non-hidden readings, not capped by TrayMax",
                    bm.Eligible.Count >= 4, "eligible count=" + bm.Eligible.Count);
                TrayReading lowestGlobal = form.ResolveReading(bm);
                // Lowest positive remaining = 1% (zcode/Z/weekly) or 37% (codex/A1/weekly)
                int expectedMin = int.MaxValue;
                foreach (Metric m in bm.Eligible)
                    if (m.Available && m.Value > 0 && m.Value < expectedMin) expectedMin = m.Value;
                Check("22b. lowest remaining picks the global minimum positive reading (not capped)",
                    lowestGlobal.Available && lowestGlobal.Value == expectedMin,
                    "value=" + lowestGlobal.Value + " expectedMin=" + expectedMin);

                // ── 23. Manual pin behavior unchanged ─────
                settings.TrayMetric = "zcode/Z/five_hour";
                settings.TrayItems = "";
                bm = form.BuildModel();
                TrayReading pinned = form.ResolveReading(bm);
                Check("23a. explicit pin picks exact reading",
                    pinned.MetricId == "zcode/Z/five_hour" && pinned.Available,
                    "id=" + pinned.MetricId);

                // ── 24. Any available switches on UNAVAILABLE (not just zero) ─────
                // Start held A1 five_hour. Refresh with it Available=false, B readable.
                settings.TrayMetric = "any";
                Install(
                    Acc("codex", "A1", Available5h(40), AvailableWeek(10)),
                    Acc("claude", "Claude", Available5h(30), AvailableWeek(20)));
                settings.TrayMax = 4;
                settings.TrayItems = "";
                bm = form.BuildModel();
                TrayReading anyFirst = form.ResolveReading(bm);
                Check("24a. any available initially picks a usable reading",
                    anyFirst.Available, "available=" + anyFirst.Available);
                // Simulate refresh: the held reading becomes unavailable
                TrayModel am24 = form.BuildModel();
                am24.CurrentMetricId = anyFirst.MetricId;
                foreach (Metric m in am24.Eligible)
                    if (m.Id == anyFirst.MetricId) { m.Available = false; break; }
                TrayReading any24 = form.ResolveReading(am24);
                Check("24b. any available switches when current becomes unavailable",
                    any24.Available && any24.MetricId != anyFirst.MetricId,
                    "switched from " + anyFirst.MetricId + " to " + any24.MetricId);

                // ── 25. Any available does NOT report a 0% reading as usable ─────
                // SRC-027:R003: usable is a conjunction — readable AND remaining
                // > 0. Every eligible reading here is 0%-and-readable or simply
                // unavailable, so "any available" has nothing to offer and must
                // say so instead of handing back a 0% reading as usable. The two
                // modes must NOT share this fallback: see 26a below.
                Install(
                    Acc("codex", "A1", Available5h(0), AvailableWeek(0)),
                    Acc("claude", "Claude", Dead5h(), DeadWeek()));
                settings.TrayMax = 4;
                settings.TrayItems = "";
                settings.TrayMetric = "any";
                FSet("AnyAvailableMetricId", "");
                TrayModel zeroModel = form.BuildModel();
                Check("25a. any available with no usable reading reports unavailable, not 0%",
                    !form.ResolveReading(zeroModel).Available,
                    "available=" + form.ResolveReading(zeroModel).Available);

                // ── 26. Lowest remaining keeps a 0% reading as its LAST RESORT ─────
                // SRC-027:R005: lowest may report 0% because a spent-but-readable
                // window is still a truthful "you have nothing left". This block
                // used to re-resolve the SAME model with the Pin still captured as
                // "any", so it silently re-tested Any — a TrayModel carries its
                // own Pin, so the model has to be rebuilt under the lowest pin.
                settings.TrayMetric = "lowest";
                TrayModel zeroLowModel = form.BuildModel();
                Check("26a. the lowest path really runs under the lowest pin",
                    zeroLowModel.Pin == "lowest", "pin=" + zeroLowModel.Pin);
                TrayReading low26 = form.ResolveReading(zeroLowModel);
                Check("26b. lowest remaining may report 0% when no positive usable reading exists",
                    low26.Available && low26.Value == 0,
                    "available=" + low26.Available + " value=" + low26.Value);
                Check("26c. the lowest zero reading is a readable 0% window, not an unreadable one",
                    low26.MetricId == "codex/A1/five_hour", "id=" + low26.MetricId);

                // ── 27. Hidden reading stays ineligible even after TrayMax decouple ─────
                // Claude's 10/5 are the numerically LOWEST readings in the whole
                // fleet, so any path that leaked a hidden reading back into the
                // candidate pool would select one of them. Eligible order is
                // codex/A1/five_hour (40) then codex/A1/weekly (30), so:
                //   lowest -> 30 (the global positive minimum of the VISIBLE pool)
                //   any    -> 40 (the FIRST usable reading in stable order)
                // Asserting the exact id on both proves hidden exclusion AND
                // that the two automatic modes are genuinely different.
                Install(
                    Acc("codex", "A1", Available5h(40), AvailableWeek(30)),
                    Acc("claude", "Claude", Available5h(10), AvailableWeek(5)));
                settings.TrayMax = 9;
                settings.TrayItems = "";
                settings.TrayHidden = "claude/Claude/five_hour|claude/Claude/weekly";
                settings.TrayMetric = "lowest";
                bm = form.BuildModel();
                TrayReading low27 = form.ResolveReading(bm);
                Check("27a. lowest remaining does not resurrect a hidden reading",
                    low27.Available && low27.MetricId == "codex/A1/weekly" && low27.Value == 30,
                    "id=" + low27.MetricId + " value=" + low27.Value);
                settings.TrayMetric = "any";
                bm = form.BuildModel();
                TrayReading any27 = form.ResolveReading(bm);
                Check("27b. any available does not resurrect a hidden reading",
                    any27.Available && any27.MetricId == "codex/A1/five_hour" && any27.Value == 40,
                    "id=" + any27.MetricId + " value=" + any27.Value);
                Check("27c. hidden metrics stay out of the ELIGIBLE pool (All still carries them)",
                    !bm.Eligible.Exists(delegate(Metric m) { return m.Id.StartsWith("claude/", StringComparison.Ordinal); })
                    && bm.All.Exists(delegate(Metric m) { return m.Id.StartsWith("claude/", StringComparison.Ordinal); }),
                    "eligible=" + bm.Eligible.Count + " all=" + bm.All.Count);

                // ── 28. Stable lowest tie: first in eligible order wins ─────
                Install(
                    Acc("codex", "A1", Available5h(15), AvailableWeek(40)),
                    Acc("claude", "Claude", Available5h(15), AvailableWeek(50)));
                settings.TrayHidden = "";
                settings.TrayItems = "";
                settings.TrayMetric = "lowest";
                bm = form.BuildModel();
                TrayReading low28 = form.ResolveReading(bm);
                Check("28a. lowest remaining breaks ties by stable eligible order",
                    low28.MetricId == "codex/A1/five_hour",
                    "id=" + low28.MetricId);

                // ── 29. Preview does not steal Any available sticky state ─────
                // Live Any holds A1. Rendering a Settings preview must not move it.
                settings.TrayMetric = "any";
                Install(
                    Acc("codex", "A1", Available5h(55), AvailableWeek(10)),
                    Acc("claude", "Claude", Available5h(30), AvailableWeek(20)));
                settings.TrayMax = 4;
                settings.TrayItems = "";
                // Live resolution: commit A1 as the sticky any metric
                form.UpdateTray();
                // Check the live sticky field was committed (via UpdateTray)
                string sticky = (string)formType.GetField("AnyAvailableMetricId", NP).GetValue(form);
                Check("29a. live UpdateTray commits the any sticky metric",
                    sticky.Length > 0, "sticky=" + sticky);
                string heldBefore = sticky;
                // Render a Settings preview with fake metrics — must not steal sticky state
                MethodInfo prev29 = formType.GetMethod("RenderPreviewBitmap", NP, null, Type.EmptyTypes, null);
                Bitmap prevBmp29 = (Bitmap)prev29.Invoke(form, null);
                prevBmp29.Dispose();
                string stickyAfter = (string)formType.GetField("AnyAvailableMetricId", NP).GetValue(form);
                Check("29b. preview render does not mutate the any sticky metric",
                    stickyAfter == heldBefore,
                    "before=" + heldBefore + " after=" + stickyAfter);
                // Re-render the live tray and confirm it stays on the same reading
                form.UpdateTray();
                string stickyLive = (string)formType.GetField("AnyAvailableMetricId", NP).GetValue(form);
                Check("29c. re-rendering the live tray keeps the same any sticky metric",
                    stickyLive == heldBefore,
                    "held=" + heldBefore + " live=" + stickyLive);

                // ── 30. Any available takes the FIRST usable, not the LOWEST ─────
                // SRC-027:R002/R003/R004. This is the red control for the
                // selector's core semantics: the old implementation minimised the
                // percentage and answered weekly=15 here. "Any available" promises
                // a USABLE reading, not the worst one, so it must answer the first
                // usable entry of stable eligible order: five_hour=80.
                settings.TrayHidden = "";
                settings.TrayMax = 9;
                settings.TrayItems = "";
                settings.TrayMetric = "any";
                FSet("AnyAvailableMetricId", "");
                Install(Acc("codex", "A1", Available5h(80), AvailableWeek(15)));
                TrayModel anyInit = form.BuildModel();
                Check("30a. stable eligible order puts the 80 ahead of the 15",
                    anyInit.Eligible.Count == 2
                    && anyInit.Eligible[0].Id == "codex/A1/five_hour"
                    && anyInit.Eligible[1].Id == "codex/A1/weekly",
                    "first=" + (anyInit.Eligible.Count > 0 ? anyInit.Eligible[0].Id : "-")
                    + " second=" + (anyInit.Eligible.Count > 1 ? anyInit.Eligible[1].Id : "-"));
                TrayReading anyInitR = form.ResolveReading(anyInit);
                Check("30b. any available initial pick is the first usable in stable order, not the lowest positive",
                    anyInitR.Available && anyInitR.MetricId == "codex/A1/five_hour" && anyInitR.Value == 80,
                    "id=" + anyInitR.MetricId + " value=" + anyInitR.Value
                    + " (a minimiser would answer codex/A1/weekly=15)");

                // ── 31. Any available REPLACEMENT walks stable order, never the minimum ───
                // SRC-027:R004. The held reading disappears while two numerically
                // LOWER readings sit further down the pool. Stable order says
                // codex/A1/weekly=90; a minimiser says claude/Claude/weekly=5.
                // This is the strongest red control in the file.
                Install(
                    Acc("codex", "A1", Available5h(80), AvailableWeek(90)),
                    Acc("claude", "Claude", Available5h(10), AvailableWeek(5)));
                settings.TrayMax = 9;
                settings.TrayItems = "";
                settings.TrayHidden = "";
                settings.TrayMetric = "any";
                TrayModel anyRep = form.BuildModel();
                anyRep.CurrentMetricId = "codex/A1/five_hour";
                foreach (Metric m in anyRep.Eligible)
                    if (m.Id == "codex/A1/five_hour") { m.Value = 0; m.Available = false; break; }
                TrayReading anyRepR = form.ResolveReading(anyRep);
                Check("31a. any available replacement is the first usable in stable order, not the lowest positive",
                    anyRepR.Available && anyRepR.MetricId == "codex/A1/weekly" && anyRepR.Value == 90,
                    "id=" + anyRepR.MetricId + " value=" + anyRepR.Value
                    + " (a minimiser would answer claude/Claude/weekly=5)");
                Check("31b. the switch is disclosed as a stable-order switch, not a minimisation",
                    anyRepR.Note.IndexOf("first usable reading in stable eligible order", StringComparison.Ordinal) >= 0
                    && anyRepR.Note.IndexOf("lowest", StringComparison.Ordinal) < 0,
                    "note=" + anyRepR.Note);

                // ── 32. Any available is sticky while the held reading stays usable ───
                // SRC-027:R004/R008: the numbers may move, but selection churn is
                // what the sticky id exists to prevent.
                Install(Acc("codex", "A1", Available5h(80), AvailableWeek(15)));
                settings.TrayMetric = "any";
                settings.TrayItems = "";
                TrayModel stick1 = form.BuildModel();
                stick1.CurrentMetricId = "codex/A1/five_hour";
                TrayReading stick1R = form.ResolveReading(stick1);
                Check("32a. any available initially holds the usable current reading",
                    stick1R.MetricId == "codex/A1/five_hour" && stick1R.Value == 80,
                    "id=" + stick1R.MetricId + " value=" + stick1R.Value);
                Install(Acc("codex", "A1", Available5h(70), AvailableWeek(5)));
                TrayModel stick2 = form.BuildModel();
                stick2.CurrentMetricId = "codex/A1/five_hour";
                TrayReading stick2R = form.ResolveReading(stick2);
                Check("32b. any available stays on the held reading across a refresh",
                    stick2R.MetricId == "codex/A1/five_hour" && stick2R.Value == 70,
                    "id=" + stick2R.MetricId + " value=" + stick2R.Value
                    + " (the 5 is lower, and that is exactly why it must not win)");
                Check("32c. keeping the current reading is silent — no churn note",
                    stick2R.Note.Length == 0, "note=" + stick2R.Note);

                // ── 33. TrayMax caps the PICTURE, never the candidate pool ─────
                // SRC-027:R002/R007/R010 with the two selectors on one fixture so
                // their difference is visible: slots 1-4 carry a readable 0% (not
                // usable), slot 5 is 42%, slot 6 is 7%. Both must reach past the
                // TrayMax=4 cap — and they must still disagree.
                Install(
                    Acc("codex", "A1", Available5h(0)),
                    Acc("codex", "A2", Available5h(0)),
                    Acc("codex", "A3", Available5h(0)),
                    Acc("codex", "A4", Available5h(0)),
                    Acc("codex", "A5", Available5h(42)),
                    Acc("codex", "A6", Available5h(7)));
                settings.TrayMax = 4;
                settings.TrayItems = "";
                settings.TrayHidden = "";
                settings.TrayMetric = "any";
                FSet("AnyAvailableMetricId", "");
                TrayModel capAny = form.BuildModel();
                Check("33a. the drawn item list still respects TrayMax=4",
                    form.TrayMetrics().Count == 4, "items=" + form.TrayMetrics().Count);
                Check("33b. the eligible pool ignores TrayMax entirely",
                    capAny.Eligible.Count == 6, "eligible=" + capAny.Eligible.Count);
                TrayReading capAnyR = form.ResolveReading(capAny);
                Check("33c. any available reaches past TrayMax to the first usable reading",
                    capAnyR.Available && capAnyR.MetricId == "codex/A5/five_hour" && capAnyR.Value == 42,
                    "id=" + capAnyR.MetricId + " value=" + capAnyR.Value);
                settings.TrayMetric = "lowest";
                TrayModel capLow = form.BuildModel();
                TrayReading capLowR = form.ResolveReading(capLow);
                Check("33d. lowest remaining reaches past TrayMax to the global positive minimum",
                    capLowR.Available && capLowR.MetricId == "codex/A6/five_hour" && capLowR.Value == 7,
                    "id=" + capLowR.MetricId + " value=" + capLowR.Value);

                // ── 34. Show Used changes the presentation, never the selection ───
                // SRC-027 Milestone 5: "any available" must keep its own name when
                // the percentage flips to the spent share. The old head replaced it
                // with "highest used", which claimed a minimisation that the ANY
                // selector does not perform. 25% remaining is chosen so the spent
                // reading is the 75% used in the contract's own example.
                Install(
                    Acc("codex", "A1", Available5h(25), AvailableWeek(90)),
                    Acc("claude", "Claude", Available5h(10), AvailableWeek(5)));
                settings.TrayMax = 9;
                settings.TrayItems = "";
                settings.TrayHidden = "";
                settings.TrayMetric = "any";
                FSet("AnyAvailableMetricId", "codex/A1/five_hour");
                settings.ShowUsed = false;
                TrayReading showRem = form.ResolveReading(form.BuildModel());
                string titleRem = form.PopupTitle();
                string tipRem = (string)tip.Invoke(form, new object[] { showRem });
                settings.ShowUsed = true;
                TrayReading showUsedR = form.ResolveReading(form.BuildModel());
                string titleUsed = form.PopupTitle();
                string tipUsed = (string)tip.Invoke(form, new object[] { showUsedR });
                Check("34a. any + remaining is labelled 'any available', not 'highest used'",
                    titleRem.IndexOf("any available", StringComparison.Ordinal) >= 0
                    && titleRem.IndexOf("highest used", StringComparison.Ordinal) < 0,
                    titleRem);
                Check("34b. any + used keeps the 'any available' name and marks the share as used",
                    titleUsed.IndexOf("any available", StringComparison.Ordinal) >= 0
                    && titleUsed.IndexOf("highest used", StringComparison.Ordinal) < 0
                    && titleUsed.IndexOf("75% used", StringComparison.Ordinal) >= 0,
                    titleUsed);
                Check("34c. the tooltip follows the same rule inside its 63-char budget",
                    tipUsed.IndexOf("any available", StringComparison.Ordinal) >= 0
                    && tipUsed.IndexOf("highest used", StringComparison.Ordinal) < 0
                    && tipRem.IndexOf("any available", StringComparison.Ordinal) >= 0
                    && tipUsed.Length <= 63,
                    "rem='" + tipRem + "' used='" + tipUsed + "'");
                Check("34d. flipping the display never moves the selected reading",
                    showRem.MetricId == "codex/A1/five_hour" && showUsedR.MetricId == showRem.MetricId
                    && showRem.Value == 25 && showUsedR.Value == 25,
                    "rem=" + showRem.MetricId + "/" + showRem.Value
                    + " used=" + showUsedR.MetricId + "/" + showUsedR.Value);
                settings.TrayMetric = "lowest";
                settings.ShowUsed = false;
                string titleLowRem = form.PopupTitle();
                settings.ShowUsed = true;
                string titleLowUsed = form.PopupTitle();
                Check("34e. lowest + remaining keeps 'lowest remaining'",
                    titleLowRem.IndexOf("lowest remaining", StringComparison.Ordinal) >= 0, titleLowRem);
                Check("34f. lowest + used keeps 'highest used' (same window, other reading)",
                    titleLowUsed.IndexOf("highest used", StringComparison.Ordinal) >= 0, titleLowUsed);
                settings.ShowUsed = false;
                settings.TrayMetric = "any";

                // ── 35. SRC-028: an auth-rejected carried reading is NOT usable ───
                // The minimal safe model: a GENERIC carried reading stays
                // conservatively selectable (dropping every carried number on a
                // hiccup would make the tray look healthier after a failure, the
                // one direction it must never lie in), but an EXPLICIT
                // authentication rejection is stronger evidence — the vendor
                // REFUSED the authenticated read — so "Any available" must skip
                // it while "Lowest remaining" keeps reporting it, because hiding
                // a real worst window under-reports how bad things are.
                var authDead = Acc("codex", "A1", Available5h(3), AvailableWeek(90));
                authDead.Ok = false;
                authDead.Status = "ERROR";
                authDead.Error = "authentication required (401 Unauthorized)";
                authDead.Carried = true;
                authDead.CarriedAt = "12:00:00";
                authDead.CarriedNote = authDead.Error;
                authDead.AuthFailed = true;
                authDead.AuthFailedClass = CodexSource.RateLimitFailure.AuthRejected;
                authDead.AuthFailureReason = authDead.Error;
                Install(authDead, Acc("claude", "Claude", Available5h(60), AvailableWeek(50)));
                settings.TrayMetric = "any";
                FSet("AnyAvailableMetricId", "");
                TrayModel authAny = form.BuildModel();
                Check("35a. the auth-rejected reading is still a real, readable reading",
                    authAny.Eligible.Count > 0 && authAny.Eligible[0].Id == "codex/A1/five_hour"
                    && authAny.Eligible[0].Value == 3 && authAny.Eligible[0].AuthFailed,
                    "eligible0=" + (authAny.Eligible.Count > 0
                        ? authAny.Eligible[0].Id + "/" + authAny.Eligible[0].Value + "/authFail=" + authAny.Eligible[0].AuthFailed
                        : "-"));
                TrayReading authAnyR = form.ResolveReading(authAny);
                Check("35b. any available does NOT select an auth-rejected carried reading",
                    authAnyR.Available && authAnyR.MetricId == "claude/Claude/five_hour" && authAnyR.Value == 60,
                    "id=" + authAnyR.MetricId + " value=" + authAnyR.Value
                    + " (the 3 is both lower AND auth-rejected; a minimiser would report it)");
                TrayModel authLow = form.BuildModel();
                authLow.Pin = "lowest";
                TrayReading authLowR = form.ResolveReading(authLow);
                Check("35c. lowest remaining still reports the auth-rejected worst window",
                    authLowR.Available && authLowR.MetricId == "codex/A1/five_hour" && authLowR.Value == 3,
                    "id=" + authLowR.MetricId + " value=" + authLowR.Value);

                // ── 35d. a GENERIC carried reading stays selectable for any ─────
                // Proves the exclusion is typed, not "all carried numbers are
                // now invalid": only a current auth rejection disqualifies.
                var softStale = Acc("codex", "A1", Available5h(3), AvailableWeek(90));
                softStale.Ok = false;
                softStale.Status = "ERROR";
                softStale.Error = "codex app-server did not answer rateLimits";
                softStale.Carried = true;
                softStale.CarriedAt = "12:00:00";
                softStale.CarriedNote = softStale.Error;
                Install(softStale, Acc("claude", "Claude", Available5h(60), AvailableWeek(50)));
                settings.TrayMetric = "any";
                FSet("AnyAvailableMetricId", "");
                TrayReading softAnyR = form.ResolveReading(form.BuildModel());
                Check("35d. any available still accepts a generically carried reading",
                    softAnyR.Available && softAnyR.MetricId == "codex/A1/five_hour" && softAnyR.Value == 3,
                    "id=" + softAnyR.MetricId + " value=" + softAnyR.Value);

                // ── 35e. the failure must not rewrite history as a fresh 0% ─────
                // Driven through the REAL CarryForward: last-good windows stay
                // visible as stale history carrying the sanitized 401 reason, and
                // the account is NOT Ok — never a fresh reading, never a fresh 0%.
                var prevGood = Acc("codex", "A1", Available5h(44), AvailableWeek(88));
                var freshFail = Acc("codex", "A1", Dead5h(), DeadWeek());
                freshFail.Ok = false;
                freshFail.Status = "ERROR";
                freshFail.Error = "authentication required (401 Unauthorized)";
                freshFail.AuthFailed = true;
                freshFail.AuthFailedClass = CodexSource.RateLimitFailure.AuthRejected;
                freshFail.AuthFailureReason = freshFail.Error;
                var failedList = new List<AccountData> { freshFail };
                Call("CarryForward", failedList, new List<AccountData> { prevGood });
                AccountData carried = failedList[0];
                Check("35f. the auth-rejected sweep keeps the last good windows as stale history",
                    carried.Carried && WinOf(carried, "five_hour") != null
                    && WinOf(carried, "five_hour").Rem == 44 && WinOf(carried, "five_hour").Available,
                    "carried=" + carried.Carried + " rem="
                    + (WinOf(carried, "five_hour") != null ? WinOf(carried, "five_hour").Rem.ToString() : "-"));
                Check("35g. the stale history is not rewritten as a fresh 0% and is never fresh",
                    WinOf(carried, "five_hour").Rem != 0 && !carried.Ok,
                    "ok=" + carried.Ok + " rem=" + WinOf(carried, "five_hour").Rem);
                Check("35h. the sanitized auth reason survives as the stale note",
                    carried.CarriedNote.IndexOf("401", StringComparison.Ordinal) >= 0
                    && carried.CarriedNote.IndexOf("authentication required", StringComparison.Ordinal) >= 0,
                    "note=" + carried.CarriedNote);
                Check("35i. the carried auth-rejected reading is still skipped by any available",
                    !carried.Ok && carried.AuthFailed,
                    "ok=" + carried.Ok + " authFailed=" + carried.AuthFailed);

                // ── 36. SRC-028: Lowest may keep history, but must label it ──
                // The defect this prevents: a successful overall refresh can
                // carry one account's last-good quota after a typed 401 while
                // global Stale stays false. Lowest deliberately keeps that
                // conservative value; every surface that presents it must say
                // it is historical, not current spendable quota.
                var lowestOldGood = Acc("codex", "C1", Available5h(44), AvailableWeek(88));
                var lowestAuthFail = Acc("codex", "C1", Dead5h(), DeadWeek());
                lowestAuthFail.Ok = false;
                lowestAuthFail.Status = "ERROR";
                lowestAuthFail.Error = "authentication required (401 Unauthorized)";
                lowestAuthFail.AuthFailed = true;
                lowestAuthFail.AuthFailedClass = CodexSource.RateLimitFailure.AuthRejected;
                lowestAuthFail.AuthFailureReason = lowestAuthFail.Error;
                var lowestFailedList = new List<AccountData> { lowestAuthFail };
                FSet("LastFetch", "12:00:00");
                Call("CarryForward", lowestFailedList, new List<AccountData> { lowestOldGood });
                AccountData lowestCarried = lowestFailedList[0];
                Install(lowestCarried, Acc("claude", "Claude", Available5h(60), AvailableWeek(70)));
                settings.TrayMetric = "lowest";
                settings.TrayShow = "pct";
                settings.TrayMode = "single";
                Select();
                TrayReading authHistory = form.ResolveReading(form.BuildModel());
                FieldInfo readingCarriedField = typeof(TrayReading).GetField("Carried");
                FieldInfo readingAuthField = typeof(TrayReading).GetField("AuthFailed");
                bool readingIsHistory = readingCarriedField != null && (bool)readingCarriedField.GetValue(authHistory);
                bool readingAuthRejected = readingAuthField != null && (bool)readingAuthField.GetValue(authHistory);
                Check("36a. the actual lowest reading carries typed stale/auth provenance",
                    authHistory.MetricId == "codex/C1/five_hour" && authHistory.Value == 44
                    && readingIsHistory && readingAuthRejected,
                    "id=" + authHistory.MetricId + " value=" + authHistory.Value
                    + " carried=" + readingIsHistory + " authFailed=" + readingAuthRejected);
                string historyTitle = form.PopupTitle();
                string historyTip = (string)tip.Invoke(form, new object[] { authHistory });
                Check("36b. popup title identifies the selected number as last-good history",
                    historyTitle.Contains("44%") && historyTitle.IndexOf("last good", StringComparison.OrdinalIgnoreCase) >= 0,
                    historyTitle);
                Check("36c. shell tooltip labels last-good history beside the number within 63 chars",
                    historyTip.Contains("44%") && historyTip.IndexOf("last good", StringComparison.OrdinalIgnoreCase) >= 0
                    && historyTip.Length <= 63,
                    historyTip);
                using (Bitmap bmp = LiveRender())
                {
                    bool muted = false, historyBadge = false;
                    var mutedInk = new List<Point>();
                    for (int y = 1; y <= 14; y++)
                        for (int x = 1; x <= 14; x++)
                        {
                            int pixel = bmp.GetPixel(x, y).ToArgb();
                            if (pixel == Palette.MUTED.ToArgb())
                            {
                                muted = true;
                                mutedInk.Add(new Point(x, y));
                            }
                            if (pixel == Palette.WARNING.ToArgb()) historyBadge = true;
                        }
                    Check("36d. the bitmap mutes the carried number and draws its history badge",
                        muted && historyBadge, "muted=" + muted + " history badge=" + historyBadge);
                    Check("36d2. the complete 44 glyph remains readable beside the history badge",
                        SameInk(mutedInk, ExpectedInk("44", new Rectangle(4, 1, 11, 14), 0)),
                        "muted glyph pixels=" + mutedInk.Count);
                }
                settings.TrayShow = "off";
                using (Bitmap bmp = LiveRender())
                {
                    bool badge = false, number = false;
                    for (int y = 1; y <= 14; y++)
                        for (int x = 1; x <= 14; x++)
                        {
                            int pixel = bmp.GetPixel(x, y).ToArgb();
                            if (pixel == Palette.WARNING.ToArgb()) badge = true;
                            if (pixel == Palette.MUTED.ToArgb()) number = true;
                        }
                    Check("36d3. the history badge remains visible when tray number is hidden",
                        badge && !number, "badge=" + badge + " number=" + number);
                }
                settings.TrayShow = "pct";
                List<TrayPopup.Row> historyRows = form.PopupRows();
                bool popupHeaderHistory = historyRows.Exists(row => row.Header
                    && row.Right.IndexOf("last good", StringComparison.OrdinalIgnoreCase) >= 0);
                bool popupReason = historyRows.Exists(row => row.Left.IndexOf("stale:", StringComparison.OrdinalIgnoreCase) >= 0
                    && row.Left.IndexOf("401", StringComparison.Ordinal) >= 0);
                bool popupValueDim = historyRows.Exists(row => row.Gauge && row.Pct == 44 && row.Dim);
                Check("36e. popup account row labels, explains, and dims the carried value",
                    popupHeaderHistory && popupReason && popupValueDim,
                    "header=" + popupHeaderHistory + " reason=" + popupReason + " dim=" + popupValueDim);
                bool accountBad;
                string accountCardNote = LimisawForm.CardNote(lowestCarried, out accountBad);
                Check("36f. account card labels its carried reading and keeps its auth reason",
                    accountCardNote != null && accountCardNote.IndexOf("stale:", StringComparison.OrdinalIgnoreCase) >= 0
                    && accountCardNote.IndexOf("401", StringComparison.Ordinal) >= 0
                    && lowestCarried.Carried && lowestCarried.CarriedAt.Length > 0,
                    "tag=last good " + lowestCarried.CarriedAt + " note=" + accountCardNote);

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
            Environment.SetEnvironmentVariable("HOME", savedProfile);
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, savedPrimary);
            Environment.SetEnvironmentVariable(ZcodeSource.EnvAlternate, savedAlternate);
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }

    static bool Wait(Func<bool> condition, int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Application.DoEvents();
            System.Threading.Thread.Sleep(15);
        }
        return condition();
    }

    // The data shapes: 0 all readable, 1 mixed, 2 all unavailable, 3 genuine
    // zeros, 4 the band values 1/34/99/100. `count` readings distributed over
    // the accounts.
    static void Fixture(int shape, int count)
    {
        var accounts = new List<AccountData>();
        int made = 0, i = 0;
        while (made < count)
        {
            var wins = new List<WindowData>();
            if (made < count) { wins.Add(ShapeWin(shape, i * 2)); made++; }
            if (made < count) { wins.Add(ShapeWin(shape, i * 2 + 1)); made++; }
            accounts.Add(Acc("codex", "A" + (i + 1), wins.ToArray()));
            i++;
        }
        var ids = new List<string>();
        foreach (AccountData a in accounts)
            foreach (WindowData w in a.Windows) ids.Add(a.Key + "/" + w.Key);
        Select(ids.Count > 0 ? ids.ToArray() : new string[0]);
        Install(accounts.ToArray());
    }

    static WindowData ShapeWin(int shape, int index)
    {
        switch (shape)
        {
            case 0: return index % 2 == 0 ? Available5h(20 + index * 7) : AvailableWeek(30 + index * 5);
            case 1: return index % 3 == 0 ? Dead5h() : Available5h(10 + index * 9);
            case 2: return index % 2 == 0 ? Dead5h() : DeadWeek();
            case 3: return index % 2 == 0 ? Available5h(0) : AvailableWeek(0);
            default:
                int[] band = { 1, 34, 99, 100 };
                return index % 2 == 0 ? Available5h(band[index % band.Length])
                                      : AvailableWeek(band[(index + 1) % band.Length]);
        }
    }
}
