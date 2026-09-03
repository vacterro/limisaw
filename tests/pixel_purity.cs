using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Replicates the exact render path used by LIMISAW.RenderTrayBitmap, then
// asserts every emitted pixel is a literal palette colour - i.e. the shell got
// pixel art, not a resampled blur. Runs the whole matrix: four layouts
// (single / dual / bars / grid) x four fill granularities (1/2, 1/4, 1/8,
// exact) x every theme in Themes\ x every icon size the shell may ask for.
//
// A theme is only a colour swap, so a theme that produced a blended pixel
// would prove the swap leaked into the drawing - which is what this catches.
//
// The app icon is covered by tests/standalone.cs (exact-frame loading at every
// size the shell asks for); this harness only renders the tray bitmap.
//
// Build + run: pwsh .\build.ps1 -Tests   (or see build.ps1 for the csc line)
public static class PixelPurity
{
    [DllImport("gdi32.dll")] static extern IntPtr CreateFont(int h,int w,int e,int o,int wt,uint it,uint un,uint so,uint cs,uint op,uint cp,uint q,uint pf,string face);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    class Pal
    {
        public string Label = "built-in Golden Default";
        public Color BG = C(0x1A1810), BEVEL = C(0x75663D), LINK = C(0xF0D060), TEXT = C(0xD4C89A);
        public Color MUTED = C(0x6E674E), DANGERTXT = C(0xD66464), WARNING = C(0x7A7A20), SUCCESS = C(0x4A7A20);
        public Color[] All { get { return new[] { BG, BEVEL, LINK, TEXT, MUTED, DANGERTXT, WARNING, SUCCESS }; } }
    }

    static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }

    static Color Hex(object v, Color fallback)
    {
        string s = v as string;
        if (string.IsNullOrEmpty(s)) return fallback;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6) return fallback;
        try { return C(Convert.ToInt32(s, 16)); } catch { return fallback; }
    }

    // Same loader shape as LIMISAW.Theme.Load: unknown/missing tokens fall back
    // to the built-in colour, so a partial theme file cannot inject a blend.
    static List<Pal> LoadThemes(string root)
    {
        var list = new List<Pal> { new Pal() };
        string dir = Path.Combine(root, "Themes");
        if (!Directory.Exists(dir)) return list;
        var ser = new JavaScriptSerializer();
        foreach (string file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var doc = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                var tok = doc.ContainsKey("tokens") ? doc["tokens"] as Dictionary<string, object> : null;
                if (tok == null) continue;
                var p = new Pal { Label = (doc.ContainsKey("label") ? doc["label"] as string : null) ?? Path.GetFileName(file) };
                p.BG = Hex(Tok(tok, "background"), p.BG);
                p.BEVEL = Hex(Tok(tok, "bevelLight"), p.BEVEL);
                p.LINK = Hex(Tok(tok, "link"), p.LINK);
                p.TEXT = Hex(Tok(tok, "textPrimary"), p.TEXT);
                p.MUTED = Hex(Tok(tok, "textMuted"), p.MUTED);
                p.DANGERTXT = Hex(Tok(tok, "dangerText"), p.DANGERTXT);
                p.WARNING = Hex(Tok(tok, "warning"), p.WARNING);
                p.SUCCESS = Hex(Tok(tok, "success"), p.SUCCESS);
                list.Add(p);
            }
            catch { }
        }
        return list;
    }

    static object Tok(Dictionary<string, object> d, string key) { return d.ContainsKey(key) ? d[key] : null; }

    static Font PixelFont(int pt)
    {
        IntPtr hf = CreateFont(-(int)(pt*96/72),0,0,0,400,0,0,0,1,0,0,3,0,"Verdana");
        try { using (Font w = Font.FromHfont(hf)) return (Font)w.Clone(); }
        finally { if (hf != IntPtr.Zero) DeleteObject(hf); }
    }

    // ── verbatim colour + fill maths from LIMISAW.cs ──
    static Color PctColor(Pal p, int pct)
    {
        if (pct < 10) return p.DANGERTXT; if (pct < 30) return p.TEXT; return p.LINK;
    }

    static Color FillColor(Pal p, int pct)
    {
        if (pct < 25) return p.DANGERTXT;
        if (pct < 50) return p.WARNING;
        if (pct < 75) return p.LINK;
        return p.SUCCESS;
    }

    static int Shown(int rem, bool showUsed) { return showUsed ? 100 - rem : rem; }

    // `rem` is always the REMAINING percent: the area filled follows the
    // Used/Left toggle, the colour is always derived from what is LEFT. Used
    // mode has to fill up as quota is spent, or it is just relabelled digits.
    static void FillArea(Graphics g, Pal p, Rectangle cell, int rem, bool available,
                         bool bottomUp, int steps, bool showUsed)
    {
        using (var under = new SolidBrush(p.BG)) g.FillRectangle(under, cell.X, cell.Y, cell.Width, cell.Height);
        if (!available) return;
        int shown = Shown(rem, showUsed);
        int cellArea = cell.Width * cell.Height;
        int fillArea = steps >= 100 ? cellArea * Math.Min(100, shown) / 100
                                    : cellArea * Math.Min(steps, Math.Max(0, shown * steps / 100)) / steps;
        if (fillArea <= 0) return;
        using (var brush = new SolidBrush(FillColor(p, rem)))
            for (int i = 0; i < cell.Height; i++)
            {
                int row = bottomUp ? cell.Height - 1 - i : i;
                int rowStart = i * cell.Width;
                if (rowStart >= fillArea) break;
                int rowEnd = rowStart + cell.Width;
                if (rowEnd <= fillArea) g.FillRectangle(brush, cell.X, cell.Y + row, cell.Width, 1);
                else { g.FillRectangle(brush, cell.X, cell.Y + row, fillArea - rowStart, 1); break; }
            }
    }

    static void DrawSingle(Graphics g, Pal p, int rem, bool available, bool showUsed)
    {
        string text = available ? Shown(rem, showUsed).ToString() : "--";
        Color col = available ? PctColor(p, rem) : p.MUTED;
        using (Font f = PixelFont(text.Length >= 3 ? 6 : 8))
        using (var br = new SolidBrush(col))
        {
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, f, br, new RectangleF(1, 1, 14, 14), fmt);
        }
    }

    static void DrawHalfNumber(Graphics g, Pal p, int top, int rem, bool available, bool showUsed)
    {
        string text = available ? Shown(rem, showUsed).ToString() : "--";
        Color col = available ? PctColor(p, rem) : p.MUTED;
        using (Font f = PixelFont(text.Length >= 3 ? 5 : 6))
        using (var br = new SolidBrush(col))
        {
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, f, br, new RectangleF(1, top, 14, 7), fmt);
        }
    }

    static void DrawGrid(Graphics g, Pal p, int[] rem, int steps, bool showUsed)
    {
        int n = Math.Max(1, Math.Min(9, rem.Length));
        int cols = n <= 1 ? 1 : n <= 4 ? 2 : 3;
        int rows = (n + cols - 1) / cols;
        int cw = 14 / cols, ch = 14 / rows;
        for (int i = 0; i < n; i++)
        {
            var cell = new Rectangle(1 + (i % cols) * cw, 1 + (i / cols) * ch, cw, ch);
            FillArea(g, p, cell, rem[i] < 0 ? 100 : rem[i], rem[i] >= 0, false, steps, showUsed);
            using (var pen = new Pen(p.BEVEL)) g.DrawRectangle(pen, cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
        }
    }

    static void DrawBars(Graphics g, Pal p, int[] rem, int steps, bool showUsed)
    {
        int n = Math.Max(1, Math.Min(7, rem.Length));
        int bw = Math.Max(1, 14 / n);
        for (int i = 0; i < n; i++)
        {
            var bar = new Rectangle(1 + i * bw, 1, bw, 14);
            FillArea(g, p, bar, rem[i] < 0 ? 100 : rem[i], rem[i] >= 0, true, steps, showUsed);
            using (var pen = new Pen(p.BEVEL)) g.DrawRectangle(pen, bar.X, bar.Y, bar.Width - 1, bar.Height - 1);
        }
    }

    // ── verbatim whole-pixel scaling from LIMISAW.RenderTrayBitmap ──
    static Bitmap Render(int size, Pal p, Action<Graphics> art)
    {
        int scale = Math.Max(1, size / 16), canvas = 16 * scale, pad = (size - canvas) / 2;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var master = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
        {
            using (Graphics mg = Graphics.FromImage(master))
            {
                mg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                mg.SmoothingMode = SmoothingMode.None;
                mg.InterpolationMode = InterpolationMode.NearestNeighbor;
                mg.PixelOffsetMode = PixelOffsetMode.None;
                mg.Clear(p.BG);
                using (var edge = new Pen(p.BEVEL)) mg.DrawRectangle(edge, 0, 0, 15, 15);
                art(mg);
            }
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                using (var back = new SolidBrush(p.BG)) g.FillRectangle(back, 0, 0, size, size);
                g.DrawImage(master, pad, pad, canvas, canvas);
            }
        }
        return bmp;
    }

    static int fails = 0, checks = 0;

    static void Check(string name, Pal p, Bitmap b)
    {
        checks++;
        var allowed = new HashSet<int>();
        foreach (Color c in p.All) allowed.Add(c.ToArgb());
        var bad = new Dictionary<int,int>();
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                Color px = b.GetPixel(x, y);
                if (px.A < 255) continue;
                int argb = px.ToArgb();
                if (!allowed.Contains(argb))
                {
                    if (!bad.ContainsKey(argb)) bad[argb] = 0;
                    bad[argb]++;
                }
            }
        if (bad.Count > 0)
        {
            fails++;
            Console.WriteLine("FAIL  " + name + "  [" + p.Label + "]  " + bad.Count + " off-palette colour(s):");
            foreach (var kv in bad) Console.WriteLine("        " + Color.FromArgb(kv.Key) + " x" + kv.Value);
        }
        b.Dispose();
    }

    static void CheckRule(string name, int got, int want)
    {
        checks++;
        if (got == want) Console.WriteLine("PASS  " + name);
        else { fails++; Console.WriteLine("FAIL  " + name + "  got " + got + " want " + want); }
    }

    // ── verbatim FillSteps from LIMISAW.cs ──
    static int FillSteps(int pct, int steps)
    {
        if (steps >= 100) return -1;
        return Math.Min(steps, Math.Max(0, pct * steps / 100));
    }

    public static int Main()
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        if (!Directory.Exists(Path.Combine(root, "Themes")))
            root = Path.GetFullPath(".");
        List<Pal> themes = LoadThemes(root);
        Console.WriteLine("themes loaded: " + themes.Count + " (from " + Path.Combine(root, "Themes") + ")");

        int[] sizes = { 16, 20, 24, 32, 48, 64 };
        int[] fills = { 2, 4, 8, 100 };
        // Every awkward reading: empty, exhausted, each colour band boundary,
        // full, and an unavailable window (-1).
        int[] window6 = { 0, 10, 33, 49, 74, 100 };
        int[] window9 = { 0, 1, 24, 25, 49, 50, 74, 75, 100 };
        int[] withGaps = { -1, 0, 55, -1, 100 };

        foreach (Pal p in themes)
        {
            foreach (int sz in sizes)
            {
                // Both display modes: Used mode must fill the OPPOSITE share of
                // the cell, not just relabel the digits, and must stay pure.
                foreach (bool used in new[] { false, true })
                {
                    bool u = used;
                    Check("single '75'", p, Render(sz, p, g => DrawSingle(g, p, 75, true, u)));
                    Check("single '100'", p, Render(sz, p, g => DrawSingle(g, p, 100, true, u)));
                    Check("single unavailable", p, Render(sz, p, g => DrawSingle(g, p, 0, false, u)));
                    Check("dual short/long", p, Render(sz, p, g =>
                    { DrawHalfNumber(g, p, 1, 8, true, u); DrawHalfNumber(g, p, 8, 100, true, u); }));
                    Check("dual with a missing half", p, Render(sz, p, g =>
                    { DrawHalfNumber(g, p, 1, 0, false, u); DrawHalfNumber(g, p, 8, 42, true, u); }));
                    foreach (int steps in fills)
                    {
                        int st = steps;
                        Check("grid 6 windows", p, Render(sz, p, g => DrawGrid(g, p, window6, st, u)));
                        Check("grid 9 windows", p, Render(sz, p, g => DrawGrid(g, p, window9, st, u)));
                        Check("grid 1 window", p, Render(sz, p, g => DrawGrid(g, p, new[] { 12 }, st, u)));
                        Check("bars 5 accounts, gaps", p, Render(sz, p, g => DrawBars(g, p, withGaps, st, u)));
                        Check("bars 7 accounts", p, Render(sz, p, g => DrawBars(g, p, window9, st, u)));
                    }
                }
            }
        }

        // Fill granularity: a step count is a promise about how coarse the
        // reading is, and 0% must never light a cell (that used to read as
        // "some quota left" on a dead account).
        CheckRule("halves: 0% -> 0/2", FillSteps(0, 2), 0);
        CheckRule("halves: 49% -> 0/2", FillSteps(49, 2), 0);
        CheckRule("halves: 50% -> 1/2", FillSteps(50, 2), 1);
        CheckRule("halves: 100% -> 2/2", FillSteps(100, 2), 2);
        CheckRule("quarters: 24% -> 0/4", FillSteps(24, 4), 0);
        CheckRule("quarters: 25% -> 1/4", FillSteps(25, 4), 1);
        CheckRule("quarters: 99% -> 3/4", FillSteps(99, 4), 3);
        CheckRule("quarters: 100% -> 4/4", FillSteps(100, 4), 4);
        CheckRule("eighths: 12% -> 0/8", FillSteps(12, 8), 0);
        CheckRule("eighths: 13% -> 1/8", FillSteps(13, 8), 1);
        CheckRule("eighths: 100% -> 8/8", FillSteps(100, 8), 8);
        CheckRule("exact mode is per-pixel, not stepped", FillSteps(37, 100), -1);

        // Used mode is a real inversion of the FILL, not relabelled digits.
        CheckRule("Left prints what remains", Shown(30, false), 30);
        CheckRule("Used prints what is spent", Shown(30, true), 70);
        CheckRule("Left fills a nearly-empty cell almost not at all", FillSteps(Shown(10, false), 4), 0);
        CheckRule("Used fills a nearly-empty cell almost fully", FillSteps(Shown(10, true), 4), 3);
        CheckRule("Left fills a full cell fully", FillSteps(Shown(100, false), 4), 4);
        CheckRule("Used leaves a full cell empty", FillSteps(Shown(100, true), 4), 0);
        // ...but the colour never inverts: 5% left is danger in BOTH modes.
        CheckRule("colour follows what is LEFT, not what is printed",
            FillColor(themes[0], 5).ToArgb(), themes[0].DANGERTXT.ToArgb());
        CheckRule("a full window is success-coloured in Used mode too",
            FillColor(themes[0], 100).ToArgb(), themes[0].SUCCESS.ToArgb());

        Console.WriteLine("---");
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
