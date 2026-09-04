using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Limisaw
{
    static class Native
    {
        [DllImport("gdi32.dll")] public static extern IntPtr CreateFont(int nHeight, int nWidth, int nEscapement, int nOrientation,
            int fnWeight, uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet,
            uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality, uint fdwPitchAndFamily, string lpszFace);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr handle);
        public const int NONANTIALIASED_QUALITY = 3;
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        // The shell's own icon loader. Unlike System.Drawing.Icon it reads a
        // PNG-compressed frame out of a multi-frame .ico correctly, which is
        // what lets heh.ico stay 4 KB instead of 99 KB of raw DIBs (Assets.cs).
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);
        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x0010;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    // One named colour set. The built-in default is Golden Default (UI.md);
    // every other theme is a Wintage palette file in Themes\, so the two
    // programs cannot drift apart on colour.
    class Theme
    {
        public string Slug = "goldendefault", Label = "Golden Default";
        public int Order = 22;
        public Color BG = C(0x1A1810), SURFACE = C(0x332E22), RAISED = C(0x3D372A), ALT = C(0x453D30);
        public Color BDARK = C(0x100E08), BEVEL = C(0x75663D);
        public Color TEXT = C(0xD4C89A), TEXT2 = C(0x9C9371), MUTED = C(0x6E674E);
        public Color SUCCESS = C(0x4A7A20), WARNING = C(0x7A7A20), DANGER = C(0x7A2020);
        public Color DANGERTXT = C(0xD66464), LINK = C(0xF0D060);

        public static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }

        static Color Hex(object value, Color fallback)
        {
            string s = value as string;
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6) return fallback;
            try { return C(Convert.ToInt32(s, 16)); } catch { return fallback; }
        }

        // Every palette LIMISAW ships is embedded in the exe; a Themes\ folder
        // next to it adds to them, and a file with the same slug REPLACES the
        // embedded one, so a palette can be edited without a rebuild.
        public static List<Theme> Load(string root)
        {
            var list = new List<Theme> { new Theme() };
            var bySlug = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in Assets.ThemeFiles())
                Add(list, bySlug, pair.Value, pair.Key);
            string dir = Path.Combine(root ?? "", "Themes");
            if (Directory.Exists(dir))
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    string text;
                    try { text = File.ReadAllText(file); } catch { continue; }
                    Add(list, bySlug, text, Path.GetFileNameWithoutExtension(file));
                }
            list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order)
                : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        static void Add(List<Theme> list, Dictionary<string, Theme> bySlug,
                        string json, string fallbackSlug)
        {
            var ser = new JavaScriptSerializer();
            try
            {
                var doc = ser.Deserialize<Dictionary<string, object>>(json);
                var tok = doc.ContainsKey("tokens") ? doc["tokens"] as Dictionary<string, object> : null;
                if (tok == null) return;
                string slug = (doc.ContainsKey("slug") ? doc["slug"] as string : null) ?? fallbackSlug;
                var t = new Theme { Slug = slug, Label = (doc.ContainsKey("label") ? doc["label"] as string : slug) ?? slug };
                if (doc.ContainsKey("order")) try { t.Order = Convert.ToInt32(doc["order"]); } catch { }
                t.BG = Hex(Get(tok, "background"), t.BG);
                t.SURFACE = Hex(Get(tok, "surface"), t.SURFACE);
                t.RAISED = Hex(Get(tok, "surfaceRaised"), t.RAISED);
                t.ALT = Hex(Get(tok, "surfaceAlt"), t.ALT);
                t.BDARK = Hex(Get(tok, "borderDark"), t.BDARK);
                t.BEVEL = Hex(Get(tok, "bevelLight"), t.BEVEL);
                t.TEXT = Hex(Get(tok, "textPrimary"), t.TEXT);
                t.TEXT2 = Hex(Get(tok, "textSecondary"), t.TEXT2);
                t.MUTED = Hex(Get(tok, "textMuted"), t.MUTED);
                t.SUCCESS = Hex(Get(tok, "success"), t.SUCCESS);
                t.WARNING = Hex(Get(tok, "warning"), t.WARNING);
                t.DANGER = Hex(Get(tok, "danger"), t.DANGER);
                t.DANGERTXT = Hex(Get(tok, "dangerText"), t.DANGERTXT);
                t.LINK = Hex(Get(tok, "link"), t.LINK);
                Theme prior;
                if (bySlug.TryGetValue(t.Slug, out prior)) list[list.IndexOf(prior)] = t;
                else if (t.Slug == "goldendefault") list[0] = t;
                else list.Add(t);
                bySlug[t.Slug] = t;
            }
            catch { }
        }

        static object Get(Dictionary<string, object> d, string key) { return d.ContainsKey(key) ? d[key] : null; }
    }

    // The active theme, read by every draw call. Themes are switched by
    // pointing this at another Theme, never by recolouring in place, so a
    // repaint can never mix two palettes.
    static class Palette
    {
        public static Theme T = new Theme();
        public static Color BG { get { return T.BG; } }
        public static Color SURFACE { get { return T.SURFACE; } }
        public static Color RAISED { get { return T.RAISED; } }
        public static Color ALT { get { return T.ALT; } }
        public static Color BDARK { get { return T.BDARK; } }
        public static Color BEVEL { get { return T.BEVEL; } }
        public static Color TEXT { get { return T.TEXT; } }
        public static Color TEXT2 { get { return T.TEXT2; } }
        public static Color MUTED { get { return T.MUTED; } }
        public static Color SUCCESS { get { return T.SUCCESS; } }
        public static Color WARNING { get { return T.WARNING; } }
        public static Color DANGER { get { return T.DANGER; } }
        public static Color DANGERTXT { get { return T.DANGERTXT; } }
        public static Color LINK { get { return T.LINK; } }
    }

    class LimisawSettings
    {
        public string Dir;
        public string IniPath;
        public int RefreshSeconds = 300;
        public string TrayMetric = "lowest";
        public string TrayMode = "single";
        // What the single/dual number READS: off (icon only), pct, or the time
        // left until that window's own reset. Separate from ShowUsed (which
        // flips %) and from TrayMetric (which picks the window).
        public string TrayShow = "pct";
        public int TrayFill = 4;
        public int TrayMax = 4;
        // Explicit tray order and explicit hides, '|' separated metric ids.
        // Two lists, not one: an order alone cannot express "I do not want
        // this reading", and a hide list alone cannot express order.
        public string TrayItems = "";
        public string TrayHidden = "";
        public string ThemeSlug = "goldendefault";
        public bool NotifyOnReset = true;
        // Every alert is two switches, not one: the balloon and the sound have
        // different reasons to be off. A silent balloon on a second monitor and
        // a chime with no balloon are both things people actually want.
        public bool ResetSound = true;
        public string ResetSoundFile = "success_powerup.wav";
        public bool NotifyLow = true;
        public int LowPct = 20;
        public string LowSoundFile = "pop_cartoon_pop.wav";
        public int SoundVolume = 5;
        public string SoundDir = "";
        public bool AutoStart = false;
        public bool ShowUsed = false;
        // Zcode has no CLI to ask, so its quota needs an API key. Every other
        // vendor authenticates through its own CLI and LIMISAW never holds a
        // credential; taking Zcode's key out of Zcode's config is a different
        // permission, so it is off until the user writes this line themselves.
        // `ZAI_API_KEY` in the environment needs no switch — that key was handed
        // over deliberately.
        public bool ZcodeReadConfig = false;
        // Account card order on the Accounts tab, '|' separated account keys.
        // Empty = discovery order, which is the vendor sweep order.
        public string AccountOrder = "";
        // What the Settings preview pretends the quota is. A preview wired to the
        // live number can only show one picture, which is useless for choosing a
        // layout: the whole question is "what does 8% look like in this mode".
        public int PreviewPct = 65;
        public int WindowX = int.MinValue, WindowY = int.MinValue;

        public static readonly string[] Modes = { "single", "dual", "bars", "grid" };
        public static readonly string[] ModeLabels = { "Single number", "Two numbers (short / long)", "One bar per item", "One cell per item" };
        public static readonly string[] ModeShort = { "Number", "Two", "Bars", "Cells" };
        // What each layout is FOR. Four characters in a button cannot say it, and
        // guessing from "Two" is what the Settings tab was asking users to do.
        public static readonly string[] ModeHints = {
            "one number: the reading you pick, biggest and clearest",
            "two numbers: worst short window over worst long one",
            "one vertical bar per reading, no digits",
            "one cell per reading in a 1x1 / 2x2 / 3x3 grid",
        };
        public static readonly int[] Fills = { 2, 4, 8, 100 };
        public static readonly string[] FillLabels = { "Halves (1/2)", "Quarters (1/4)", "Eighths (1/8)", "Exact (per pixel)" };
        public static readonly string[] FillShort = { "1/2", "1/4", "1/8", "Exact" };
        public static readonly string[] FillHints = {
            "halves: empty, half, full — readable at a glance, no detail",
            "quarters: four steps, the usual compromise",
            "eighths: eight steps, still countable at 16px",
            "exact: fills by the pixel, most detail, hardest to read fast",
        };
        public const int MaxTrayItems = 9;

        public LimisawSettings(string dir) { Dir = dir; IniPath = Path.Combine(dir, "LIMISAW.ini"); }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileString(string app, string key, string def, System.Text.StringBuilder buf, int size, string file);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool WritePrivateProfileString(string app, string key, string val, string file);

        // 260 chars is not enough for an ordered list of every window three
        // vendors can expose; the buffer must fit the value it reads back.
        string Read(string key, string def) { var sb = new System.Text.StringBuilder(2048); GetPrivateProfileString("limisaw", key, def, sb, sb.Capacity, IniPath); return sb.ToString(); }
        bool Write(string key, string val) { return WritePrivateProfileString("limisaw", key, val, IniPath); }

        // Last write outcome, so a caller can tell the user WHY their settings
        // will not survive a restart. One bool, not a list of failures: the ini
        // is one file, so if writing one key fails they all fail, and reporting
        // 21 errors about one read-only file is noise. Set only by Save(); false
        // again only after a Save() that succeeded.
        public bool LastSaveFailed;

        public void Save()
        {
            // The first key doubles as the write probe: with a read-only ini all
            // 21 writes fail, and one failing syscall already says everything —
            // hammering the file 20 more times for identical failures is noise.
            if (!Write("RefreshSeconds", RefreshSeconds.ToString()))
            {
                LastSaveFailed = true;
                return;
            }
            LastSaveFailed = false;
            Write("TrayMetric", TrayMetric); Write("TrayMode", TrayMode); Write("TrayShow", TrayShow);
            Write("TrayFill", TrayFill.ToString()); Write("TrayMax", TrayMax.ToString());
            Write("TrayItems", TrayItems); Write("TrayHidden", TrayHidden);
            Write("Theme", ThemeSlug);
            Write("NotifyOnReset", NotifyOnReset ? "1" : "0");
            Write("ResetSound", ResetSound ? "1" : "0");
            Write("ResetSoundFile", ResetSoundFile);
            Write("NotifyLow", NotifyLow ? "1" : "0");
            Write("LowPct", LowPct.ToString());
            Write("LowSoundFile", LowSoundFile);
            Write("SoundVolume", SoundVolume.ToString());
            Write("SoundDir", SoundDir);
            Write("AutoStart", AutoStart ? "1" : "0");
            Write("ShowUsed", ShowUsed ? "1" : "0");
            Write("ZcodeReadConfig", ZcodeReadConfig ? "1" : "0");
            Write("AccountOrder", AccountOrder);
            Write("PreviewPct", PreviewPct.ToString());
            Write("WindowX", WindowX.ToString()); Write("WindowY", WindowY.ToString());
        }

        public static List<string> Split(string value)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(value)) return list;
            foreach (string part in value.Split('|'))
            { string t = part.Trim(); if (t.Length > 0 && !list.Contains(t)) list.Add(t); }
            return list;
        }
        public List<string> ItemOrder() { return Split(TrayItems); }
        public List<string> HiddenItems() { return Split(TrayHidden); }
        public void SetItemOrder(List<string> ids) { TrayItems = string.Join("|", ids.ToArray()); }
        public void SetHiddenItems(List<string> ids) { TrayHidden = string.Join("|", ids.ToArray()); }
        public List<string> CardOrder() { return Split(AccountOrder); }
        public void SetCardOrder(List<string> keys) { AccountOrder = string.Join("|", keys.ToArray()); }

        public void Load()
        {
            int.TryParse(Read("RefreshSeconds", "300"), out RefreshSeconds); if (RefreshSeconds < 60) RefreshSeconds = 60;
            if (RefreshSeconds > 3600) RefreshSeconds = 3600;
            TrayMetric = Read("TrayMetric", "lowest");
            if (string.IsNullOrEmpty(TrayMetric)) TrayMetric = "lowest";
            TrayMode = Read("TrayMode", "single");
            if (Array.IndexOf(Modes, TrayMode) < 0) TrayMode = "single";
            TrayShow = Read("TrayShow", "pct");
            if (TrayShow != "off" && TrayShow != "pct" && TrayShow != "time") TrayShow = "pct";
            int.TryParse(Read("TrayFill", "4"), out TrayFill);
            if (Array.IndexOf(Fills, TrayFill) < 0) TrayFill = 4;
            int.TryParse(Read("TrayMax", "4"), out TrayMax);
            if (TrayMax < 1) TrayMax = 1; if (TrayMax > MaxTrayItems) TrayMax = MaxTrayItems;
            TrayItems = Read("TrayItems", "");
            TrayHidden = Read("TrayHidden", "");
            ThemeSlug = Read("Theme", "goldendefault");
            if (string.IsNullOrEmpty(ThemeSlug)) ThemeSlug = "goldendefault";
            NotifyOnReset = Read("NotifyOnReset", "1") == "1";
            ResetSound = Read("ResetSound", "1") == "1";
            ResetSoundFile = Read("ResetSoundFile", "success_powerup.wav");
            NotifyLow = Read("NotifyLow", "1") == "1";
            int.TryParse(Read("LowPct", "20"), out LowPct);
            if (LowPct < 5) LowPct = 5; if (LowPct > 95) LowPct = 95;
            LowSoundFile = Read("LowSoundFile", "pop_cartoon_pop.wav");
            int.TryParse(Read("SoundVolume", "5"), out SoundVolume);
            if (SoundVolume < 0) SoundVolume = 0; if (SoundVolume > 100) SoundVolume = 100;
            SoundDir = Read("SoundDir", "");
            AutoStart = Read("AutoStart", "0") == "1";
            ShowUsed = Read("ShowUsed", "0") == "1";
            ZcodeReadConfig = Read("ZcodeReadConfig", "0") == "1";
            AccountOrder = Read("AccountOrder", "");
            int.TryParse(Read("PreviewPct", "65"), out PreviewPct);
            if (PreviewPct < 0) PreviewPct = 0; if (PreviewPct > 100) PreviewPct = 100;
            int.TryParse(Read("WindowX", int.MinValue.ToString()), out WindowX);
            int.TryParse(Read("WindowY", int.MinValue.ToString()), out WindowY);
        }
    }

    // One quota window as the probe resolved it. `Rem` is ALWAYS remaining
    // percent; the Used/Left switch flips only what is printed. `GatedBy` is
    // set when a longer window in the same pool is spent, which makes this
    // one unusable no matter what the vendor reports for it.
    class WindowData
    {
        public string Key = "", Base = "", Label = "", Group = "", GroupLabel = "", Reset, GatedBy;
        public bool Available, AssumedFull;
        public int Rem;
        public int DurationMinutes;
    }

    class AccountData
    {
        public string Provider = "", ProviderLabel = "", Name = "", Status = "", Plan, Error;
        public bool Ok, Quiet;
        // Set when this account had no usable reading THIS sweep and the
        // previous sweep's windows were carried forward, so the card shows the
        // last known numbers (dimmed, with the reason) instead of going blank on
        // a transient CLI hiccup.
        public bool Carried; public string CarriedAt = "", CarriedNote = "";
        public List<WindowData> Windows = new List<WindowData>();
        public string Key { get { return Provider + "/" + Name; } }

        // "Did this sweep produce anything worth drawing?" A card with only
        // unavailable windows is just as blank as a card with none.
        public bool HasReading
        {
            get
            {
                foreach (WindowData w in Windows) if (w.Available) return true;
                return false;
            }
        }

        public WindowData Find(string windowKey)
        {
            foreach (WindowData w in Windows) if (w.Key == windowKey) return w;
            return null;
        }

        public string Abbrev() { return Abbrev(false); }
        public string Abbrev(bool showUsed)
        {
            var parts = new List<string>();
            foreach (WindowData w in Windows)
            {
                int shown = showUsed ? 100 - w.Rem : w.Rem;
                parts.Add(w.Label + " " + (w.Available ? shown + "%" : "--"));
            }
            return parts.Count > 0 ? string.Join(" · ", parts.ToArray()) : (Error ?? Status);
        }
    }

    class CliInfo
    {
        public string Key = "", Label = "", Path = "", Command = "", PowerShell = "", Source = "", Target = "";
        public bool Installed;
    }

    class ResetEvent
    {
        public string AccountLabel = "", LimitLabel = "";
        public int NewRemaining; public string ResetAt;
        // A 5h window can refill while the weekly window is spent. The refill is
        // real and worth announcing, but the percentage is not usable, so the
        // balloon must not read as "you have 100% again".
        public bool LockedByWeekly;
    }

    // One selectable tray reading: "the lowest of everything", or one exact
    // account+window. Built from the live snapshot, so a new vendor account
    // appears in the menu without a code change.
    class Metric
    {
        public string Id = "", Label = "", Short = "";
        public int Value; public bool Available, IsShort;
        // The window's own reset stamp: the countdown readout and the tooltip
        // need the same instant the alert logic keys on.
        public string Reset;
    }

    // Verdana at a fixed pixel height, never antialiased (UI.md). One font per
    // size for the whole process: a repaint asks for a dozen and measuring asks
    // for more, so an HFONT per call was pure GDI churn.
    static class Pix
    {
        static readonly Dictionary<int, Font> Cache = new Dictionary<int, Font>();

        public static Font Make(int pt)
        {
            IntPtr hf = Native.CreateFont(-(int)(pt * 96 / 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, Native.NONANTIALIASED_QUALITY, 0, "Verdana");
            try { using (Font wrapped = Font.FromHfont(hf)) return (Font)wrapped.Clone(); }
            finally { if (hf != IntPtr.Zero) Native.DeleteObject(hf); }
        }

        public static Font Get(int pt)
        {
            Font f;
            if (!Cache.TryGetValue(pt, out f)) { f = Make(pt); Cache[pt] = f; }
            return f;
        }
    }

    // Shared drawing rules, so the window, the hover popup and the tray icon
    // cannot disagree about what a percentage looks like.
    static class Draw
    {
        // Colour always reads the REMAINING percent, in both display modes: red
        // means "almost out", never "barely used".
        public static Color PctColor(int pct)
        {
            if (pct < 10) return Palette.DANGERTXT;
            if (pct < 30) return Palette.TEXT;
            return Palette.LINK;
        }

        public static Color FillColor(int pct, bool available)
        {
            if (!available) return Palette.MUTED;
            if (pct < 25) return Palette.DANGERTXT;
            if (pct < 50) return Palette.WARNING;
            if (pct < 75) return Palette.LINK;
            return Palette.SUCCESS;
        }

        public static void Text(Graphics g, string s, int x, int y, Color c, int pt)
        { using (var br = new SolidBrush(c)) g.DrawString(s, Pix.Get(pt), br, (float)x, (float)y); }

        public static int Width(Graphics g, string s, int pt)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return (int)Math.Ceiling(g.MeasureString(s, Pix.Get(pt)).Width);
        }

        // The bar fills by what the user asked to SEE (remaining, or used), but
        // its colour is always derived from REMAINING. Flipping only the digits
        // made "Used" look like a full tank when the account was nearly out;
        // flipping the colour too would make red mean "barely used".
        // `dim` is a reading that exists but does not reach the tray (past the
        // cap, hidden, or carried forward). Its number is still true, so its
        // bar is drawn too - in MUTED. Filling only the non-dim rows made every
        // dim bar read as an empty account no matter what its percent said.
        public static void Gauge(Graphics g, int x, int y, int w, int h,
                                 int fillPct, int remainingPct, bool available, bool dim)
        {
            // BDARK, not BG: the track has to be a well the fill can be seen in.
            // On a BG-coloured track a dim bar is only its bevel outline, which
            // at 8px looks exactly like a filled one.
            using (var back = new SolidBrush(Palette.BDARK)) g.FillRectangle(back, x, y, w, h);
            if (available && fillPct > 0)
            {
                int fill = Math.Max(1, w * Math.Min(100, fillPct) / 100);
                Color col = dim ? Palette.MUTED : FillColor(remainingPct, true);
                using (var b = new SolidBrush(col)) g.FillRectangle(b, x, y, fill, h);
            }
            using (var p = new Pen(Palette.BEVEL)) g.DrawRectangle(p, x, y, w - 1, h - 1);
        }

        public static void Bevel(Graphics g, int x, int y, int w, int h, bool raised)
        {
            Color hi = raised ? Palette.BEVEL : Palette.BDARK, lo = raised ? Palette.BDARK : Palette.BEVEL;
            using (var p1 = new Pen(hi)) g.DrawRectangle(p1, x, y, w - 1, h - 1);
            using (var p2 = new Pen(lo)) g.DrawRectangle(p2, x + 1, y + 1, w - 3, h - 3);
        }
    }

    // Playing a WAV at a volume Windows refuses to set per-sound. Same trick
    // Problip's blip engine uses: SoundPlayer has no volume, so the samples are
    // scaled into a cached copy and that copy is what gets played.
    static class SoundCue
    {
        // One player for the process. A cue cutting the previous one is the
        // right behaviour here (two reset chimes overlapping is noise), and it
        // keeps a single undisposed SoundPlayer instead of one per event.
        static System.Media.SoundPlayer Player;
        static readonly Dictionary<string, string> Built = new Dictionary<string, string>();
        static bool Pruned;

        // Where the picker looks. A folder the user pointed at wins, then a
        // Sounds folder next to the exe, then the WAVs embedded in the exe, and
        // the Windows media folder last so the list is never empty.
        public static string Library(string root, string setting)
        {
            if (!string.IsNullOrEmpty(setting) && Directory.Exists(setting)) return setting;
            string local = Path.Combine(root ?? "", "Sounds");
            if (Directory.Exists(local)) return local;
            string shipped = Assets.SoundLibrary();
            if (shipped != null) return shipped;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        }

        // A stored value is either a bare name inside the library or an absolute
        // path the user picked elsewhere. Null means "nothing to play", which the
        // caller must answer with silence rather than an exception at notify time.
        public static string Resolve(string root, string library, string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;
            string p = Path.Combine(Library(root, library), file);
            if (File.Exists(p)) return p;
            // The shipped defaults must keep working after the user points the
            // picker at a folder that does not contain them.
            string shipped = Assets.SoundLibrary();
            if (shipped != null)
            {
                string s = Path.Combine(shipped, file);
                if (File.Exists(s)) return s;
            }
            return File.Exists(file) ? file : null;
        }

        public static void Play(string root, string library, string file, int volume)
        {
            string path = Resolve(root, library, file);
            if (path == null || volume <= 0) return;
            try
            {
                if (volume < 100) path = Scaled(path, volume) ?? path;
                if (Player == null) Player = new System.Media.SoundPlayer();
                else Player.Stop();
                Player.SoundLocation = path;
                // Load() on purpose: Play() alone starts an async load and can
                // miss the first play of a file this process has not heard yet.
                Player.Load();
                Player.Play();
            }
            catch { }
        }

        static string CacheDir() { return Path.Combine(Path.GetTempPath(), "limisaw_sounds"); }

        // Volume is quantised to 5% steps so dragging the slider cannot mint one
        // temp WAV per pixel — the exact orphan problem Problip's volume drag
        // had before it learned to reuse its cache.
        static string Scaled(string src, int volume)
        {
            int q = Math.Max(5, Math.Min(100, volume / 5 * 5));
            string key = src + "|" + q;
            string cached;
            if (Built.TryGetValue(key, out cached) && File.Exists(cached)) return cached;
            try
            {
                string dir = CacheDir();
                Directory.CreateDirectory(dir);
                if (!Pruned) { Pruned = true; PruneOld(dir); }
                string outPath = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(src) + "_v" + q + ".wav");
                if (!File.Exists(outPath) || File.GetLastWriteTimeUtc(outPath) < File.GetLastWriteTimeUtc(src))
                    File.WriteAllBytes(outPath, Scale(File.ReadAllBytes(src), q / 100.0));
                Built[key] = outPath;
                return outPath;
            }
            catch { return null; }
        }

        static void PruneOld(string dir)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-3);
                foreach (string f in Directory.GetFiles(dir, "*.wav"))
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
            }
            catch { }
        }

        // PCM samples only. A compressed or unrecognised WAV comes back as the
        // untouched bytes, so an odd file is played at full volume rather than
        // never played at all.
        static byte[] Scale(byte[] b, double gain)
        {
            if (gain >= 0.999999 || b.Length < 12) return b;
            byte[] outb = (byte[])b.Clone();
            int dataStart = -1, dataLen = 0, fmtBits = 0, pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                if (len < 0 || pos + 8 + len > b.Length) break;
                if (id == "fmt " && len >= 16) fmtBits = BitConverter.ToUInt16(b, pos + 22);
                else if (id == "data") { dataStart = pos + 8; dataLen = len; break; }
                pos += 8 + len + (len % 2);
            }
            if (dataStart < 0 || fmtBits % 8 != 0) return b;
            int bps = fmtBits / 8;
            int end = Math.Min(b.Length, dataStart + dataLen);
            for (int i = dataStart; i + bps <= end; i += bps)
            {
                if (fmtBits == 8)
                {
                    int v = (int)Math.Round((outb[i] - 128) * gain) + 128;
                    outb[i] = (byte)Math.Max(0, Math.Min(255, v));
                }
                else if (fmtBits == 16)
                {
                    int n = (int)Math.Round(BitConverter.ToInt16(outb, i) * gain);
                    n = Math.Max(short.MinValue, Math.Min(short.MaxValue, n));
                    outb[i] = (byte)(n & 0xFF); outb[i + 1] = (byte)((n >> 8) & 0xFF);
                }
                else if (fmtBits == 24)
                {
                    int raw = outb[i] | (outb[i + 1] << 8) | (outb[i + 2] << 16);
                    int v = (raw & 0x800000) != 0 ? raw - 0x1000000 : raw;
                    long n = Math.Max(-8388608L, Math.Min(8388607L, (long)Math.Round(v * gain)));
                    int u = (int)n & 0xFFFFFF;
                    outb[i] = (byte)(u & 0xFF); outb[i + 1] = (byte)((u >> 8) & 0xFF); outb[i + 2] = (byte)((u >> 16) & 0xFF);
                }
                else if (fmtBits == 32)
                {
                    long n = (long)Math.Round((double)BitConverter.ToInt32(outb, i) * gain);
                    n = Math.Max(int.MinValue, Math.Min((long)int.MaxValue, n));
                    byte[] t = BitConverter.GetBytes((int)n);
                    outb[i] = t[0]; outb[i + 1] = t[1]; outb[i + 2] = t[2]; outb[i + 3] = t[3];
                }
            }
            return outb;
        }
    }

    // The tray context menu in the active theme. Windows paints a ToolStrip
    // system-white by default, which looked like a different application had
    // opened; the colours are read live from Palette so a theme switch applies
    // to the next open with no rebuild.
    class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Palette.SURFACE; } }
        public override Color MenuBorder { get { return Palette.BDARK; } }
        public override Color MenuItemBorder { get { return Palette.BEVEL; } }
        public override Color MenuItemSelected { get { return Palette.ALT; } }
        public override Color MenuItemSelectedGradientBegin { get { return Palette.ALT; } }
        public override Color MenuItemSelectedGradientEnd { get { return Palette.ALT; } }
        public override Color MenuItemPressedGradientBegin { get { return Palette.RAISED; } }
        public override Color MenuItemPressedGradientMiddle { get { return Palette.RAISED; } }
        public override Color MenuItemPressedGradientEnd { get { return Palette.RAISED; } }
        public override Color ImageMarginGradientBegin { get { return Palette.SURFACE; } }
        public override Color ImageMarginGradientMiddle { get { return Palette.SURFACE; } }
        public override Color ImageMarginGradientEnd { get { return Palette.SURFACE; } }
        public override Color SeparatorDark { get { return Palette.BDARK; } }
        public override Color SeparatorLight { get { return Palette.BEVEL; } }
        public override Color CheckBackground { get { return Palette.RAISED; } }
        public override Color CheckSelectedBackground { get { return Palette.ALT; } }
        public override Color CheckPressedBackground { get { return Palette.ALT; } }
    }

    class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // A disabled item is a STATUS row here (the account summaries), not
            // an unavailable action, so it must stay readable instead of being
            // greyed into the background by the system default.
            e.TextColor = e.Item.Enabled ? Palette.TEXT : Palette.TEXT2;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        { e.ArrowColor = Palette.TEXT; base.OnRenderArrow(e); }
    }

    // A themed hover panel for the tray icon. The shell tooltip is one line of
    // 63 plain characters, which cannot show ten readings across three vendors:
    // this draws the same gauges and the same colours the window uses. The OS
    // tooltip is suppressed while the panel is up so only one thing appears.
    class TrayPopup : Form
    {
        public class Row
        {
            public string Left = "", Right = "";
            // Pct = what to PRINT and how much to fill. Rem = the remaining
            // percent the colour is derived from; they differ in Used mode.
            public int Pct, Rem; public bool Header, Gauge, Available, Dim;
        }

        List<Row> Rows = new List<Row>();
        string Title = "LIMISAW";
        const int PadX = 8, RowH = 16, HeadH = 18, GaugeW = 64, GaugeH = 8, Gap = 6;

        public TrayPopup()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; TopMost = true;
            DoubleBuffered = true; BackColor = Palette.BG;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000,
                    WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
                return cp;
            }
        }

        public void Show(string title, List<Row> rows, Point cursor)
        {
            Title = title; Rows = rows;
            BackColor = Palette.BG;
            Size size = Measure();
            Rectangle work = Screen.FromPoint(cursor).WorkingArea;
            // Anchored ABOVE and LEFT of the cursor: the panel must not cover the
            // icon it describes, or the click that follows the hover hits glass.
            int x = Math.Min(Math.Max(work.Left, cursor.X - size.Width + 16), work.Right - size.Width);
            int y = cursor.Y - size.Height - 12;
            if (y < work.Top) y = Math.Min(cursor.Y + 20, work.Bottom - size.Height);
            Bounds = new Rectangle(x, y, size.Width, size.Height);
            if (!Visible) Show();
            Invalidate();
        }

        Size Measure()
        {
            int height = HeadH + Gap;
            int widest = 0;
            using (var probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                widest = Draw.Width(g, Title, 11);
                foreach (Row r in Rows)
                {
                    int w = Draw.Width(g, r.Left, r.Header ? 11 : 10);
                    if (!r.Header) w += Gap + 34 + Gap + GaugeW;
                    w += Gap + Draw.Width(g, r.Right, 10);
                    widest = Math.Max(widest, w);
                    height += r.Header ? HeadH : RowH;
                }
            }
            return new Size(Math.Max(220, Math.Min(520, widest + PadX * 2 + 4)), height + Gap);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.SmoothingMode = SmoothingMode.None; g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.Clear(Palette.BG);
            using (var head = new SolidBrush(Palette.SURFACE)) g.FillRectangle(head, 0, 0, Width, HeadH);
            Draw.Text(g, Title, PadX, 2, Palette.TEXT, 11);
            Draw.Bevel(g, 0, 0, Width, Height, true);

            int right = Width - PadX;
            int gaugeX = right - GaugeW;
            int y = HeadH + Gap;
            foreach (Row r in Rows)
            {
                if (r.Header)
                {
                    Draw.Text(g, r.Left, PadX, y, Palette.LINK, 11);
                    if (r.Right.Length > 0)
                        Draw.Text(g, r.Right, right - Draw.Width(g, r.Right, 10), y + 2, Palette.TEXT2, 10);
                    y += HeadH;
                    continue;
                }
                Color pctCol = r.Dim || !r.Available ? Palette.MUTED : Draw.PctColor(r.Rem);
                string pct = r.Available ? r.Pct + "%" : "--";
                int pctW = Draw.Width(g, pct, 10);
                int whenW = r.Right.Length > 0 ? Draw.Width(g, r.Right, 10) : 0;
                int whenX = gaugeX - Gap - whenW;
                Draw.Text(g, r.Left, PadX + 6, y, r.Dim ? Palette.MUTED : Palette.TEXT2, 10);
                if (whenW > 0) Draw.Text(g, r.Right, whenX, y, Palette.MUTED, 10);
                Draw.Text(g, pct, whenX - Gap - pctW, y, pctCol, 10);
                Draw.Gauge(g, gaugeX, y + 3, GaugeW, GaugeH, r.Available ? r.Pct : 0, r.Rem, r.Available, r.Dim);
                y += RowH;
            }
        }
    }

    class LimisawForm : Form
    {
        string RootPath; LimisawSettings Settings;
        List<AccountData> Accounts = new List<AccountData>();
        List<AccountData> PrevAccounts = new List<AccountData>();
        List<CliInfo> Clis = new List<CliInfo>();
        List<Theme> Themes;
        List<string> NotifiedResetKeys = new List<string>();
        // window key -> the reset stamp it was alerted for. See DetectLow.
        Dictionary<string, string> NotifiedLow = new Dictionary<string, string>();
        bool LowBaseline;
        string LastFetch = ""; string LastError = ""; string TrayError = ""; bool Stale = false; bool Refreshing = false;
        int Tab = TabAccounts; string Note = "";
        Timer RefreshTimer; NotifyIcon Tray;
        List<Rectangle> Buttons = new List<Rectangle>(); List<Action> ButtonActions = new List<Action>();
        // Where the last repaint actually put its measured content (gauges and
        // width-bounded text). Buttons must never land on top of these, and
        // tests\layout_fit.cs asserts exactly that - a limit bar sliding under a
        // button is invisible to a rectangle-only check.
        List<Rectangle> Marks = new List<Rectangle>();
        // Labels the last repaint could NOT fit in their button, even after
        // stepping the font down. Empty is the contract: a cropped label like
        // "Showing: Le" is a layout bug, not a rendering choice.
        List<string> Cropped = new List<string>();

        const int TabAccounts = 0, TabTray = 1, TabSettings = 2, TabCli = 3;
        static readonly string[] TabLabels = { "Accounts", "Tray", "Settings", "CLIs" };

        const int CardHeight = 30, HeaderH = 22, RowH = 18, FooterH = 24, ToolbarH = 56, CliCardH = 58, Gap = 6;
        const int PickRowH = 22, SetRowH = 28, ThemeRowH = 20, BtnPad = 18;

        // Drag-to-reorder state for the Tray tab. A press only becomes a drag
        // after DragSlop pixels, so a click on a row still behaves like a click.
        const int DragSlop = 4;
        List<Rectangle> ItemRows = new List<Rectangle>(); List<string> ItemRowIds = new List<string>();
        int ItemRowsTop = 0;
        // Account-card row tops, measured before the drag preview reorders
        // anything: DropIndex needs geometry that does not move under the
        // pointer (see DropIndex).
        List<int> CardTops = new List<int>();
        string DragId; int DragStartY, DragY; bool Dragging;
        // Problip-style drag slider: one active at a time, armed by OnMouseDown
        // on the knob OR anywhere on the rail. Null = no slider in flight.
        // Both rails' rects persist across paints so OnMouseDown can hit-test
        // without repainting.
        string VolDrag; Rectangle VolRail;
        Rectangle VolRailVolume, VolKnobVolume, VolRailLow, VolKnobLow, VolRailPreview;
        Rectangle VolKnobPreview;

        // ── hints ────────────────────────────────────────────────────────────
        // Four characters and a prayer is what "1/4" and "Off" amount to without
        // an explanation, so every control on a settings row registers the
        // sentence that says what it does. Shown in the footer on hover, which is
        // the one place already reserved for "what is going on" and the only one
        // that cannot cover the thing being explained — a floating tooltip over a
        // preview hides exactly what the user is trying to see.
        List<Rectangle> HintZones = new List<Rectangle>();
        List<string> HintTexts = new List<string>();
        string Hover = "";

        void Hint(Rectangle area, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            HintZones.Add(area); HintTexts.Add(text);
        }

        // A labelled row: the label itself explains the row, so hovering anywhere
        // on it — including the dead space — answers the question.
        void HintRow(int x, int y, int w, string text) { Hint(new Rectangle(x, y, w, SetRowH), text); }

        string HintAt(Point p)
        {
            // Last registered wins: rows are added before the controls that sit
            // on them, so a control's own sentence beats its row's.
            for (int i = HintZones.Count - 1; i >= 0; i--)
                if (HintZones[i].Contains(p)) return HintTexts[i];
            return "";
        }


        // One font per size, reused for the whole process life: DrawText is
        // called dozens of times per repaint and measuring adds more, so
        // creating an HFONT per call was pure GDI churn.
        static Font MakePixelFont(int pt) { return Pix.Make(pt); }
        static Font F(int pt) { return Pix.Get(pt); }
        Font Cached(int pt) { return Pix.Get(pt); }

        public LimisawForm(string root, LimisawSettings s, NotifyIcon tray, List<Theme> themes)
        {
            RootPath = root; Settings = s; Tray = tray; Themes = themes;
            ApplyTheme(s.ThemeSlug);
            Text = "LIMISAW"; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(420, 160);
            BackColor = Palette.BG; DoubleBuffered = true; TopMost = false;
            KeyPreview = true;
            try { Icon = Assets.AppIcon(root, SystemInformation.IconSize.Width) ?? Icon; } catch { }
            if (Settings.WindowX != int.MinValue && Settings.WindowY != int.MinValue)
            {
                var saved = new Rectangle(Settings.WindowX, Settings.WindowY, Width, Height);
                foreach (Screen screen in Screen.AllScreens)
                    if (screen.WorkingArea.IntersectsWith(saved)) { StartPosition = FormStartPosition.Manual; Location = saved.Location; break; }
            }
            RefreshTimer = new Timer { Interval = Settings.RefreshSeconds * 1000 };
            RefreshTimer.Tick += (o, e) => RefreshData();
            FitWindow();
            RefreshTimer.Start(); RefreshData();
        }

        public void ApplyTheme(string slug)
        {
            foreach (Theme t in Themes)
                if (string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase)) { Palette.T = t; break; }
            BackColor = Palette.BG;
        }

        // The window sizes itself to its content instead of scrolling it: the
        // account list is short and fully known, so a scrollbar only hid rows
        // behind an extra gesture. Height is clamped to the screen the window
        // sits on, and the clip in OnPaint is what guards that edge.
        void FitWindow()
        {
            int content = Tab == TabCli ? InstallPanelHeight()
                : Tab == TabTray ? TrayPanelHeight()
                : Tab == TabSettings ? SettingsPanelHeight()
                : AccountsPanelHeight();
            int want = HeaderH + Gap + ToolbarH + content + FooterH;
            Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
            int cap = Math.Max(200, work.Height - 40);
            ClientSize = new Size(ClientSize.Width, Math.Min(want, cap));
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
        }

        int AccountsPanelHeight()
        {
            if (Accounts.Count == 0) return 26;
            int total = 0;
            foreach (AccountData a in Accounts)
                total += CardHeight + CardLines(a) * RowH + Gap + Gap;
            return total;
        }

        int InstallPanelHeight()
        {
            return 20 + (Clis.Count == 0 ? 22 : Clis.Count * (CliCardH + Gap));
        }

        int TrayPanelHeight()
        {
            int rows = Math.Max(1, AllMetrics().Count);
            return 20 + 28 + 16 + rows * PickRowH + Gap * 2;
        }

        int SettingsPanelHeight()
        {
            int cols = ThemeColumns();
            int themeRows = (Themes.Count + cols - 1) / cols;
            // Counted from what PaintSettingsPanel actually draws, in its order:
            // three section headings (18 each), the icon group's four rows plus
            // the 52px preview, the alerts group's three or four rows, and the
            // app group's two rows plus the theme grid's own header.
            int rows = 4                                  // layout, shows, fill, readings
                + 3                                       // volume, refill, low alert
                + (Settings.NotifyLow ? 1 : 0)             // low sound, only while armed
                + 2;                                      // numbers, refresh
            return 18 * 3 + rows * SetRowH + 52 + 20 + themeRows * ThemeRowH + Gap * 2;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, HTCAPTION = 2;
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST)
            {
                // LParam packs two SIGNED 16-bit screen coords. A point on a
                // monitor above the primary sets the high bit, so ToInt32()
                // overflows and the drag strip dies on that monitor: read the
                // full value and truncate unchecked instead.
                int raw = unchecked((int)m.LParam.ToInt64());
                int x = raw & 0xFFFF; if (x > 0x7FFF) x -= 0x10000;
                int y = (raw >> 16) & 0xFFFF; if (y > 0x7FFF) y -= 0x10000;
                Point p = PointToClient(new Point(x, y));
                if (p.X >= 0 && p.Y >= 0 && p.Y < HeaderH && p.X < Width - 22) m.Result = (IntPtr)HTCAPTION;
            }
        }

        public void HideToTray()
        {
            Settings.WindowX = Location.X; Settings.WindowY = Location.Y; Settings.Save();
            Hide();
        }

        public void RefreshData()
        {
            if (Refreshing) return; Refreshing = true;
            LastError = ""; Refresh();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // The probe runs IN this process (Probe.cs / ProbeClaude.cs /
                    // ProbeAntigravity.cs / ProbeZcode.cs): no interpreter, no
                    // script folder, no child of our own to time out. Each vendor
                    // CLI still gets a hard deadline of its own, and Probe.Run's
                    // total budget is what bounds the sweep.
                    Apply(Probe.Run(Settings.ZcodeReadConfig));
                }
                catch (Exception ex) { SetError(ex.Message); }
            });
        }

        void SetError(string msg)
        {
            Stale = true; LastError = msg; Refreshing = false;
            try { BeginInvoke((Action)(() => { Refresh(); UpdateTray(); })); } catch { }
        }

        void Apply(ProbeResult snapshot)
        {
            try
            {
                if (snapshot.Clis.Count > 0) Clis = snapshot.Clis;
                PrevAccounts = Accounts; Accounts = CarryForward(snapshot.Accounts, PrevAccounts);
                Stale = false; LastError = ""; LastFetch = DateTime.Now.ToString("HH:mm:ss");
                DetectResets();
                RearmLowAlerts();
                DetectLow();
            }
            catch (Exception ex) { Stale = true; LastError = ex.Message; }
            finally
            {
                Refreshing = false;
                try { BeginInvoke((Action)(() => { FitWindow(); Refresh(); UpdateTray(); })); } catch { }
            }
        }

        AccountData FindPrev(AccountData cur)
        {
            foreach (AccountData p in PrevAccounts) if (p.Key == cur.Key) return p;
            return null;
        }

        // A vendor CLI that fails ONE sweep is not a vendor without quota: the
        // Codex app-server pays a cold start, `agy` occasionally takes 15s, and
        // any of them can lose a race with a laptop waking up. Showing a bare
        // "ERROR" card in that case looked permanent and lost the numbers the
        // app already had. The last good windows are carried forward and marked,
        // so the card still reads honestly (dim, "last good HH:mm:ss") and a
        // real, lasting failure still surfaces as an error line.
        List<AccountData> CarryForward(List<AccountData> fresh, List<AccountData> previous)
        {
            foreach (AccountData a in fresh)
            {
                // The test is "nothing to draw", not "status said ERROR": a
                // vendor can answer OK with an empty or all-unavailable window
                // list, and that card is just as blank.
                if (a.HasReading) continue;
                foreach (AccountData old in previous)
                {
                    if (old.Key != a.Key || !old.HasReading) continue;
                    a.Windows = old.Windows;
                    a.Plan = a.Plan ?? old.Plan;
                    a.Carried = true;
                    a.CarriedAt = old.Carried ? old.CarriedAt : LastFetch;
                    // Why the numbers are stale is the actionable half; the
                    // original error is otherwise lost with the blank card.
                    a.CarriedNote = a.Error ?? (old.Carried ? old.CarriedNote : a.Status);
                    break;
                }
            }
            return fresh;
        }

        void DetectResets()
        {
            if (!Settings.NotifyOnReset && !Settings.ResetSound) return;
            foreach (AccountData cur in Accounts)
            {
                AccountData prev = FindPrev(cur);
                // Carried-forward windows ARE the previous windows, so comparing
                // them would compare a list with itself; and a failed sweep is
                // not evidence of a reset either way.
                if (!cur.Ok || cur.Carried || prev == null || !prev.Ok) continue;
                foreach (WindowData w in cur.Windows)
                {
                    WindowData pw = prev.Find(w.Key);
                    if (pw == null) continue;
                    // A gated window is announced as locked instead of free:
                    // the refill is real, the percentage is not usable.
                    CheckReset(cur, w, pw, w.GatedBy != null);
                }
            }
        }

        void CheckReset(AccountData acc, WindowData cur, WindowData prev, bool locked)
        {
            if (!cur.Available || !prev.Available) return;
            if (string.IsNullOrEmpty(cur.Reset) || string.IsNullOrEmpty(prev.Reset)) return;
            if (cur.Reset == prev.Reset && cur.Rem <= prev.Rem + 25) return; // no meaningful change
            if (cur.Rem <= prev.Rem + 25) return; // dropped or negligible
            string key = acc.Key + "_" + cur.Key + "_" + cur.Reset;
            if (NotifiedResetKeys.Contains(key)) return;
            NotifiedResetKeys.Add(key);
            if (NotifiedResetKeys.Count > 100) NotifiedResetKeys.RemoveRange(0, 50);
            Notify(new ResetEvent
            {
                AccountLabel = AccountTitle(acc), LimitLabel = WindowTitle(cur),
                NewRemaining = cur.Rem, ResetAt = cur.Reset, LockedByWeekly = locked,
            });
        }

        static string AccountTitle(AccountData a)
        {
            string label = string.IsNullOrEmpty(a.ProviderLabel) ? a.Provider : a.ProviderLabel;
            return string.IsNullOrEmpty(a.Name) || a.Name == label ? label : label + " · " + a.Name;
        }

        static string WindowTitle(WindowData w)
        {
            return string.IsNullOrEmpty(w.GroupLabel) ? w.Label : w.Label + " (" + w.GroupLabel + ")";
        }

        void Notify(ResetEvent ev)
        {
            try
            {
                string title = "LIMISAW — " + ev.AccountLabel;
                string text = ev.LockedByWeekly
                    ? ev.LimitLabel + " limit reset — still 0% usable, locked by a longer window"
                    : ev.LimitLabel + " limit reset — " + ev.NewRemaining + "% remaining";
                // DetectResets runs on the refresh worker thread. NotifyIcon owns
                // a window created on the UI thread, so the balloon MUST be raised
                // there or the call is an illegal cross-thread touch.
                Action show = () =>
                {
                    try
                    {
                        Tray.BalloonTipTitle = title;
                        Tray.BalloonTipText = text;
                        Tray.BalloonTipIcon = ToolTipIcon.Info;
                        Tray.ShowBalloonTip(5000);
                    }
                    catch { }
                };
                if (Settings.NotifyOnReset) { if (InvokeRequired) BeginInvoke(show); else show(); }
                if (Settings.ResetSound) Play(Settings.ResetSoundFile);
            }
            catch { }
        }

        void Play(string file)
        {
            SoundCue.Play(RootPath, Settings.SoundDir, file, Settings.SoundVolume);
        }

        // "You are down to the last few percent" is a different alert from a
        // reset: it fires once per quota window per cycle, not once per refill.
        void DetectLow()
        {
            if (!Settings.NotifyLow) return;
            // The first sweep only records what is already spent. Without this
            // every low window the user already has would fire a balloon the
            // moment the app starts, which is noise, not an alert.
            bool silent = !LowBaseline;
            LowBaseline = true;
            foreach (AccountData cur in Accounts)
            {
                if (!cur.Ok || cur.Carried) continue;
                foreach (WindowData w in cur.Windows)
                {
                    if (!w.Available || w.Rem > Settings.LowPct) continue;
                    string key = cur.Key + "_" + w.Key;
                    string stamp = w.Reset ?? "";
                    string seen;
                    if (NotifiedLow.TryGetValue(key, out seen) && !NewCycle(w, seen, stamp)) continue;
                    NotifiedLow[key] = stamp;
                    if (!silent) NotifyLowAlert(cur, w);
                }
            }
        }

        // Has this window ROLLED OVER since the alert, or has its reset merely
        // drifted?
        //
        // A fixed window (Codex, Claude) holds one reset time for the whole
        // window and then jumps forward by its own length. A ROLLING window
        // (Zcode's 5-hour credit pool) pushes its reset out a little every time
        // quota is spent, so its stamp is different on almost every sweep — and
        // comparing stamps for inequality made that window alert on every single
        // refresh, which is the "sound plays constantly" defect. Recovery above
        // the threshold is the primary re-arm (RearmLowAlerts); this is the
        // secondary one, for a window that refilled and was spent back down
        // between two sweeps.
        //
        // Half the window's own duration separates the two: consumption drift is
        // minutes, a rollover is the full window. A window that does not state a
        // duration re-arms on recovery alone rather than on a guess.
        static bool NewCycle(WindowData w, string alertedFor, string now)
        {
            if (alertedFor == now) return false;
            if (w.DurationMinutes <= 0) return false;
            double? before = Stamp.Epoch(alertedFor);
            double? after = Stamp.Epoch(now);
            if (!before.HasValue || !after.HasValue) return false;
            return after.Value - before.Value >= w.DurationMinutes * 30.0;   // half, in seconds
        }

        void NotifyLowAlert(AccountData acc, WindowData w)
        {
            try
            {
                string title = "LIMISAW — " + AccountTitle(acc);
                string text = WindowTitle(w) + " — only " + w.Rem + "% left"
                    + (string.IsNullOrEmpty(w.Reset) ? "" : ", resets " + FriendlyTime(w.Reset));
                Action show = () =>
                {
                    try
                    {
                        Tray.BalloonTipTitle = title;
                        Tray.BalloonTipText = text;
                        // A low limit is a warning, not news: the exclamation
                        // icon is what tells the two balloons apart in the feed.
                        Tray.BalloonTipIcon = ToolTipIcon.Warning;
                        Tray.ShowBalloonTip(5000);
                    }
                    catch { }
                };
                // DetectLow already owns the on/off switch, so the alert that
                // gets this far always shows both halves.
                if (InvokeRequired) BeginInvoke(show); else show();
                Play(Settings.LowSoundFile);
            }
            catch { }
        }

        // An alert that fired at 20% must be able to fire again in the next
        // cycle, so a window that climbed back above the threshold (plus a
        // 10-point band, so a number hovering on the boundary cannot ping every
        // sweep) drops its record.
        void RearmLowAlerts()
        {
            if (NotifiedLow.Count == 0) return;
            var gone = new List<string>();
            foreach (KeyValuePair<string, string> kv in NotifiedLow)
            {
                bool still = false;
                foreach (AccountData a in Accounts)
                {
                    foreach (WindowData w in a.Windows)
                    {
                        if (a.Key + "_" + w.Key != kv.Key) continue;
                        still = w.Available && w.Rem <= Settings.LowPct + 10;
                        break;
                    }
                    if (still) break;
                }
                if (!still) gone.Add(kv.Key);
            }
            foreach (string k in gone) NotifiedLow.Remove(k);
        }

        // ── metrics ──────────────────────────────────────────────────────────
        // Every account+window pair is a selectable tray reading, plus the
        // synthetic "lowest". Ids are provider/account/window so a saved
        // choice survives a restart and new vendors need no new code.
        public List<Metric> AllMetrics()
        {
            var list = new List<Metric>();
            foreach (AccountData a in Accounts)
                foreach (WindowData w in a.Windows)
                    list.Add(new Metric
                    {
                        Id = a.Provider + "/" + a.Name + "/" + w.Key,
                        Label = AccountTitle(a) + " · " + WindowTitle(w),
                        Short = ShortName(a) + "·" + w.Label,
                        // A carried-forward reading still counts. Dropping it
                        // would make the tray jump UP when a vendor CLI hiccups,
                        // i.e. report more quota than the user has — the one
                        // direction that must never happen silently.
                        Value = w.Rem, Available = (a.Ok || a.Carried) && w.Available,
                        IsShort = w.DurationMinutes > 0 ? w.DurationMinutes <= 300 : w.Base == "five_hour",
                        Reset = w.Reset,
                    });
            return list;
        }

        // What the TRAY actually draws: the user's explicit order first, then
        // any newly discovered reading, minus the hidden ones, capped at
        // TrayMax. Four vendors' worth of windows cannot fit in 16 pixels, so
        // the cap is the whole point - the alternative is unreadable mush.
        public List<Metric> TrayMetrics()
        {
            List<Metric> picked = SelectedMetrics();
            // While the Settings preview renders, every reading reports the
            // pretend level: a preview that mixes one fake number with three real
            // ones cannot answer "what does this layout look like at 5%".
            if (PreviewPct >= 0)
            {
                if (picked.Count == 0)
                {
                    // Nothing discovered yet (first launch, no vendor logged in)
                    // still deserves a preview, so the shapes are inventable.
                    int n = Settings.TrayMode == "single" || Settings.TrayMode == "dual" ? 2 : 4;
                    for (int i = 0; i < n; i++)
                        picked.Add(new Metric
                        {
                            Id = "preview/" + i, Label = "preview", Short = "pv",
                            Value = PreviewPct, Available = true, IsShort = i % 2 == 0,
                            Reset = PreviewReset(),
                        });
                    return picked;
                }
                var faked = new List<Metric>();
                foreach (Metric m in picked)
                    faked.Add(new Metric
                    {
                        Id = m.Id, Label = m.Label, Short = m.Short,
                        Value = PreviewPct, Available = true, IsShort = m.IsShort,
                        Reset = PreviewReset(),
                    });
                return faked;
            }
            return picked;
        }

        List<Metric> SelectedMetrics()
        {
            List<Metric> all = AllMetrics();
            List<string> hidden = Settings.HiddenItems();
            var picked = new List<Metric>();
            foreach (string id in Settings.ItemOrder())
            {
                if (hidden.Contains(id)) continue;
                foreach (Metric m in all)
                    if (m.Id == id && !picked.Contains(m)) { picked.Add(m); break; }
            }
            // A reading the user has never seen is shown by default: silently
            // hiding a brand-new account would look like the vendor broke.
            foreach (Metric m in all)
                if (!hidden.Contains(m.Id) && !picked.Contains(m)) picked.Add(m);
            if (picked.Count > Settings.TrayMax) picked.RemoveRange(Settings.TrayMax, picked.Count - Settings.TrayMax);
            return picked;
        }

        void GetTrayMetric(out int value, out bool available, out string label, out string reset)
        {
            value = 0; available = false; label = "lowest"; reset = null;
            if (Settings.TrayMetric != "lowest")
            {
                // A pin is an explicit choice, so it is looked up in the FULL
                // list: hiding a reading from a bars/grid picture must not
                // silently retarget the single number somewhere else.
                foreach (Metric m in AllMetrics())
                    if (m.Id == Settings.TrayMetric) { value = m.Value; available = m.Available; label = m.Label; reset = m.Reset; return; }
                // The pinned metric can still vanish (account logged out,
                // vendor uninstalled). Falling back to "lowest" beats showing
                // "--" forever with no hint why.
                label = "lowest";
            }
            // "lowest" answers "what stops me first" over the readings the user
            // selected — the same set the tray picture draws.
            List<Metric> all = TrayMetrics();
            if (all.Count == 0) all = AllMetrics();
            int minAny = int.MaxValue, minPos = int.MaxValue;
            string labelAny = "", labelPos = "", resetAny = null, resetPos = null;
            bool any = false, pos = false;
            foreach (Metric m in all)
            {
                if (!m.Available) continue;
                if (m.Value < minAny) { minAny = m.Value; labelAny = m.Label; resetAny = m.Reset; any = true; }
                if (m.Value > 0 && m.Value < minPos) { minPos = m.Value; labelPos = m.Label; resetPos = m.Reset; pos = true; }
            }
            if (pos) { value = minPos; label = labelPos; reset = resetPos; }
            else if (any) { value = minAny; label = labelAny; reset = resetAny; }
            available = any;
        }

        void GetTrayMetric(out int value, out bool available, out string label)
        {
            string reset;
            GetTrayMetric(out value, out available, out label, out reset);
        }

        // "How long until I can work again" in the fewest characters that stay
        // true: minutes under an hour ("12m"), whole hours under a day ("3h"),
        // days above that ("2d"). A past/unknown stamp is "--", never "0m" —
        // zero minutes reads as "right now" and lies when the probe is stale.
        static string CountdownText(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "--";
            DateTime reset;
            if (!DateTime.TryParse(iso, out reset)) return "--";
            TimeSpan left = reset - DateTime.Now;
            if (left.TotalSeconds < 60) return left.TotalSeconds >= 0 ? "<1m" : "--";
            if (left.TotalMinutes < 60) return ((int)left.TotalMinutes) + "m";
            if (left.TotalHours < 24) return ((int)Math.Round(left.TotalHours)) + "h";
            return ((int)Math.Round(left.TotalDays)) + "d";
        }

        // ── tray item picker ─────────────────────────────────────────────────
        // The order list is materialised on first edit: an implicit "discovery
        // order" cannot be reordered, and writing it down is also what makes a
        // move survive a restart.
        void MoveItem(string id, int delta)
        {
            List<string> order = PaintedOrder();
            int at = order.IndexOf(id);
            int to = at + delta;
            if (at < 0 || to < 0 || to >= order.Count) return;
            order.RemoveAt(at); order.Insert(to, id);
            Settings.SetItemOrder(order); Settings.Save();
            Refresh(); UpdateTray();
        }

        // The order as the Tray tab PAINTS it: the saved order, restricted to
        // readings that still exist, then anything newly discovered. Both the
        // arrows and the drag commit against this list, so an id left over from
        // a logged-out account cannot shift a row by one.
        List<string> PaintedOrder()
        {
            var order = new List<string>();
            List<Metric> all = AllMetrics();
            foreach (string id in Settings.ItemOrder())
                foreach (Metric m in all)
                    if (m.Id == id && !order.Contains(id)) { order.Add(id); break; }
            foreach (Metric m in all) if (!order.Contains(m.Id)) order.Add(m.Id);
            return order;
        }

        // The same rule for account cards: the saved order first, then anything
        // newly discovered. A vendor that logs in mid-session lands at the
        // bottom rather than shuffling the cards the user arranged.
        List<string> PaintedCardOrder()
        {
            var order = new List<string>();
            foreach (string key in Settings.CardOrder())
                foreach (AccountData a in Accounts)
                    if (a.Key == key && !order.Contains(key)) { order.Add(key); break; }
            foreach (AccountData a in Accounts) if (!order.Contains(a.Key)) order.Add(a.Key);
            return order;
        }

        // Cards in the order they are drawn, which is the order OnMouseUp
        // commits against — one list, so the insertion marker cannot lie.
        List<AccountData> OrderedAccounts()
        {
            var cards = new List<AccountData>();
            foreach (string key in PaintedCardOrder())
                foreach (AccountData a in Accounts)
                    if (a.Key == key && !cards.Contains(a)) { cards.Add(a); break; }
            foreach (AccountData a in Accounts) if (!cards.Contains(a)) cards.Add(a);
            return cards;
        }

        void ToggleItem(string id)
        {
            List<string> hidden = Settings.HiddenItems();
            if (hidden.Contains(id)) hidden.Remove(id); else hidden.Add(id);
            Settings.SetHiddenItems(hidden);
            if (Settings.ItemOrder().Count == 0) Settings.SetItemOrder(PaintedOrder());
            Settings.Save();
            Refresh(); UpdateTray();
        }

        void SetTrayMax(int delta)
        {
            int next = Settings.TrayMax + delta;
            if (next < 1 || next > LimisawSettings.MaxTrayItems) return;
            Settings.TrayMax = next; Settings.Save();
            Refresh(); UpdateTray();
        }

        void ResetTrayItems()
        {
            Settings.TrayItems = ""; Settings.TrayHidden = ""; Settings.Save();
            Note = "Tray items reset to discovery order";
            FitWindow(); Refresh(); UpdateTray();
        }

        // ── painting ─────────────────────────────────────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.SmoothingMode = SmoothingMode.None; g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.Clear(Palette.BG); Buttons.Clear(); ButtonActions.Clear(); Marks.Clear(); Cropped.Clear();
            HintZones.Clear(); HintTexts.Clear();
            int w = Width, h = Height;
            using (var b = new SolidBrush(Palette.SURFACE)) g.FillRectangle(b, 0, 0, w, HeaderH);
            DrawText(g, "LIMISAW", 6, 3, Palette.TEXT, 12, true);
            string st = Refreshing ? "..." : (Stale ? "STALE" : "OK");
            DrawText(g, st, w - 62, 3, Stale ? Palette.DANGERTXT : (Accounts.Count > 0 ? Palette.LINK : Palette.MUTED), 12, true);
            var xr = new Rectangle(w - 22, 1, 20, 20); Buttons.Add(xr); ButtonActions.Add(() => HideToTray());
            DrawText(g, "X", w - 18, 3, Palette.TEXT2, 12);

            int y = HeaderH + 6;
            // Buttons are laid out RIGHT to LEFT from their measured labels, and
            // the status text gets whatever is left: a fixed-width box cropped
            // "Showing: Left" to "Showing: Le" as soon as a theme's font was
            // wider than the guess.
            string usedLabel = Settings.ShowUsed ? "Showing: Used" : "Showing: Left";
            int usedW = ButtonWidth(g, usedLabel), refreshW = ButtonWidth(g, "Refresh (F5)");
            var ur = new Rectangle(w - 8 - usedW, y, usedW, 22); Buttons.Add(ur); ButtonActions.Add(() => ToggleShowUsed());
            DrawButton(g, ur, usedLabel, Settings.ShowUsed);
            var rr = new Rectangle(ur.Left - Gap - refreshW, y, refreshW, 22); Buttons.Add(rr); ButtonActions.Add(() => RefreshData());
            DrawButton(g, rr, "Refresh (F5)", false);
            string update = Refreshing ? "Checking..." : (Stale ? "Update failed" : (LastFetch.Length > 0 ? "Updated " + LastFetch : "Not checked"));
            DrawTextFit(g, update, 8, y + 5, rr.Left - 16, Stale ? Palette.DANGERTXT : Palette.MUTED, 10);
            y += 28;
            // Every setting the tray menu offers also lives on a tab here: a
            // context menu is a bad place to reorder a list or compare readings.
            int tabW = (w - 16) / TabLabels.Length;
            for (int i = 0; i < TabLabels.Length; i++)
            {
                var tr = new Rectangle(8 + i * tabW, y, tabW, 22);
                int pick = i;
                Buttons.Add(tr); ButtonActions.Add(() => { Tab = pick; FitWindow(); Refresh(); });
                DrawButton(g, tr, TabLabels[i], Tab == i);
            }
            y += 28;

            // The window is sized to fit every card (FitWindow), so nothing
            // scrolls. The clip is only a guard for the one case FitWindow
            // cannot satisfy: more content than the screen is tall.
            var clip = new Rectangle(0, y, w, Math.Max(0, h - FooterH - y));
            g.SetClip(clip);
            ItemRows.Clear(); ItemRowIds.Clear();
            if (Tab == TabCli) PaintInstallPanel(g, y, w);
            else if (Tab == TabTray) PaintTrayPanel(g, y, w);
            else if (Tab == TabSettings) PaintSettingsPanel(g, y, w);
            else PaintAccounts(g, y, w);
            g.ResetClip();

            // A fetch error outranks a tray-render error: without data the icon
            // has nothing to draw anyway. A save failure outranks both hints:
            // every change the user makes in this session is being thrown away,
            // and they cannot know that from behaviour alone — the settings work
            // until the app restarts and then revert, the worst possible moment.
            string problem = Settings.LastSaveFailed
                ? "Settings NOT saved — LIMISAW.ini is not writable"
                : LastError.Length > 0 ? "Error: " + LastError
                : TrayError.Length > 0 ? "Tray icon failed: " + TrayError : "";
            // Hover explanation beats the keyboard cheatsheet but yields to a
            // real problem and to the note about what just changed: an
            // explanation of a control is only useful while nothing is wrong.
            string footer = problem.Length > 0 ? problem
                : Note.Length > 0 ? Note
                : Hover.Length > 0 ? Hover
                : "F5 refresh · U used/left · T theme · 1-4 tabs · drag to reorder · Esc hide";
            DrawTextFit(g, footer, 8, h - 20, w - 16,
                problem.Length > 0 ? Palette.DANGERTXT
                    : Hover.Length > 0 && Note.Length == 0 ? Palette.TEXT2 : Palette.MUTED, 10);
        }

        // Card geometry is shared with AccountsPanelHeight/InstallPanelHeight
        // through the same constants, so the measured window height and the
        // painted content cannot drift apart and clip a row. Columns are
        // derived from the window width, never hardcoded: fixed x-offsets are
        // what let the gauge slide under the reset time.
        void PaintAccounts(Graphics g, int cursor, int w)
        {
            if (Accounts.Count == 0)
            {
                DrawText(g, Refreshing ? "Probing vendors..." : "No accounts reported yet.", 14, cursor + 4, Palette.TEXT2, 11);
                return;
            }
            int cw = w - 16;
            int whenW = 96, gaugeW = 78, pctW = 40;
            int whenX = 8 + cw - 8 - whenW;
            int gaugeX = whenX - Gap - gaugeW;
            int pctX = gaugeX - Gap - pctW;
            int nameW = pctX - 18 - Gap;

            // Cards are reordered by dragging, same gesture as the Tray tab: the
            // vendor that matters most belongs at the top, and which one that is
            // is the user's call, not the sweep order's.
            List<AccountData> cards = OrderedAccounts();
            // Row tops of the UNPREVIEWED list, measured before anything moves,
            // because DropIndex must not compute against geometry the preview is
            // still shifting. A card's height is known from its own contents, so
            // this needs no paint.
            CardTops.Clear();
            {
                int probe = cursor;
                foreach (AccountData a in cards)
                {
                    CardTops.Add(probe);
                    probe += CardHeight + CardLines(a) * RowH + Gap + Gap;
                }
            }
            int dragFrom = -1;
            if (Dragging && DragId != null)
            {
                for (int i = 0; i < cards.Count; i++) if (cards[i].Key == DragId) { dragFrom = i; break; }
                if (dragFrom >= 0)
                {
                    int target = DropIndex(cards.Count);
                    if (target != dragFrom)
                    {
                        AccountData moved = cards[dragFrom];
                        cards.RemoveAt(dragFrom);
                        cards.Insert(Math.Min(target, cards.Count), moved);
                    }
                }
            }

            foreach (AccountData a in cards)
            {
                bool bad;
                string lead = CardNote(a, out bad);
                int cardH = CardHeight + CardLines(a) * RowH + Gap;
                bool held = Dragging && a.Key == DragId;
                ItemRows.Add(new Rectangle(8, cursor, cw, cardH + Gap));
                ItemRowIds.Add(a.Key);
                using (var bg = new SolidBrush(held ? Palette.ALT : Palette.RAISED)) g.FillRectangle(bg, 8, cursor, cw, cardH);
                DrawBevel(g, 8, cursor, cw, cardH, false);
                Hint(new Rectangle(8, cursor, cw, cardH), "drag a card to reorder the accounts");
                string tag = a.Carried ? "last good" + (a.CarriedAt.Length > 0 ? " " + a.CarriedAt : "")
                    : a.Plan != null ? a.Plan : (a.Ok ? "" : a.Status.ToLowerInvariant());
                int tagW = tag.Length > 0 ? TextWidth(g, tag, 10) : 0;
                DrawTextFit(g, AccountTitle(a), 14, cursor + 5, cw - 20 - (tagW > 0 ? tagW + Gap : 0), Palette.LINK, 11, true);
                if (tagW > 0) DrawText(g, tag, 8 + cw - 8 - tagW, cursor + 6,
                    a.Carried ? Palette.WARNING : Palette.TEXT2, 10);
                int ry = cursor + CardHeight - 4;
                if (lead != null)
                {
                    // The reason goes ABOVE the rows, whether or not there are
                    // any: a failed sweep still leaves unavailable windows
                    // behind, and drawing this only for an empty list is what
                    // hid every provider's error message (see T-006).
                    DrawTextFit(g, lead, 14, ry, cw - 20,
                        a.Carried ? Palette.WARNING : bad ? Palette.DANGERTXT : Palette.MUTED, 10);
                    ry += RowH;
                }
                foreach (WindowData win in a.Windows)
                {
                    string name = win.Label + (win.GroupLabel.Length > 0 ? " · " + win.GroupLabel : "");
                    DrawTextFit(g, name, 18, ry, nameW, a.Carried ? Palette.MUTED : Palette.TEXT2, 10);
                    DrawText(g, FormatPct(win.Available, win.Rem), pctX, ry,
                        a.Carried ? Palette.MUTED : PctColor(win.Rem), 11, true);
                    DrawGauge(g, gaugeX, ry + 4, gaugeW, 8, win.Available ? win.Rem : 100, win.Available, a.Carried);
                    string when = win.GatedBy != null ? "locked by " + win.GatedBy
                        : win.AssumedFull ? "refilled" : FriendlyTime(win.Reset);
                    DrawTextFit(g, when, whenX, ry, whenW, Palette.MUTED, 10);
                    ry += RowH;
                }
                cursor += cardH + Gap;
            }

            // Insertion marker, same as the Tray tab: where the held card lands.
            if (Dragging && dragFrom >= 0 && CardTops.Count > 0)
            {
                int slot = Math.Min(DropIndex(cards.Count), CardTops.Count - 1);
                using (var p = new Pen(Palette.LINK)) g.DrawLine(p, 10, CardTops[slot], w - 10, CardTops[slot]);
            }
        }

        // The one line a card may carry above its window rows, and whether it is
        // a fault. Reason and stale note are the same slot on purpose: a carried
        // card's note already holds the failure text (CarryForward copies Error
        // into CarriedNote), so showing both would print it twice.
        //
        // `bad` separates a fault from a provider that is idle by design —
        // Antigravity can only quote quota when its backend refuses work, and a
        // permanently lit red line with nothing to fix trains the user to ignore
        // it (Model.QuietCodes).
        public static string CardNote(AccountData a, out bool bad)
        {
            bad = false;
            if (a.Carried)
                return a.CarriedNote.Length > 0 ? "stale: " + a.CarriedNote : null;
            if (a.Ok) return null;
            bad = !a.Quiet;
            return (a.Quiet ? "idle: " : "ERROR: ") + (a.Error ?? a.Status);
        }

        // Height and paint read the SAME count, so a card can never be measured
        // shorter than it draws and clip its last row.
        public static int CardLines(AccountData a)
        {
            bool bad;
            int lines = a.Windows.Count + (CardNote(a, out bad) != null ? 1 : 0);
            return Math.Max(1, lines);
        }

        void PaintInstallPanel(Graphics g, int cursor, int w)
        {
            DrawTextFit(g, "Installs the vendor's own published command. Nothing runs without a confirmation.",
                14, cursor, w - 24, Palette.TEXT2, 10);
            cursor += 20;
            if (Clis.Count == 0)
            {
                DrawText(g, "CLI status not probed yet — press Refresh.", 14, cursor, Palette.MUTED, 10);
                return;
            }
            int cw = w - 16;
            foreach (CliInfo cli in Clis)
            {
                using (var bg = new SolidBrush(Palette.RAISED)) g.FillRectangle(bg, 8, cursor, cw, CliCardH);
                DrawBevel(g, 8, cursor, cw, CliCardH, false);
                string action = cli.Installed ? "Reinstall" : "Install";
                int actW = Math.Max(84, ButtonWidth(g, action));
                var br = new Rectangle(8 + cw - 8 - actW, cursor + 6, actW, 22);
                CliInfo target = cli;
                Buttons.Add(br); ButtonActions.Add(() => InstallCli(target));
                DrawButton(g, br, action, false);
                DrawTextFit(g, cli.Label, 14, cursor + 5, br.Left - 20, Palette.LINK, 11, true);
                string state = cli.Installed ? "installed" : "not installed";
                int stateW = TextWidth(g, state, 10);
                DrawText(g, state, 14, cursor + 24, cli.Installed ? Palette.SUCCESS : Palette.DANGERTXT, 10);
                // The path shares its row with the Install button, so it stops
                // at the button's left edge, not at the card's.
                DrawTextFit(g, cli.Installed ? cli.Path : cli.Source, 14 + stateW + Gap, cursor + 24,
                    br.Left - Gap - (14 + stateW + Gap), Palette.MUTED, 10);
                DrawTextFit(g, cli.PowerShell, 14, cursor + 40, cw - 20, Palette.TEXT2, 10);
                cursor += CliCardH + Gap;
            }
        }

        // ── Tray tab: which readings, how many, in what order ────────────────
        // 16 tray pixels cannot carry every window three vendors expose, so the
        // user picks. Rows are shown in the order the tray draws them, and the
        // ones past the cap are dimmed instead of removed - the cap is a
        // decision, and hiding its effect would make it feel broken.
        //
        // Rows can be reordered by dragging as well as with the arrows: a
        // ten-row list takes nine clicks to move one row to the top, and the
        // arrows stay for keyboard-free precision and accessibility.
        void PaintTrayPanel(Graphics g, int cursor, int w)
        {
            List<Metric> all = AllMetrics();
            List<string> hidden = Settings.HiddenItems();
            int shown = TrayMetrics().Count;
            DrawTextFit(g, "Tray shows " + shown + " of " + all.Count + " readings — drag a row or use the arrows:",
                14, cursor, w - 24, Palette.TEXT2, 10);
            cursor += 20;

            // Row 2 is laid out from measured labels, left to right, so the
            // "Reset order" button can never land on top of the counter.
            string resetLabel = "Reset order";
            int resetW = ButtonWidth(g, resetLabel);
            var reset = new Rectangle(w - 8 - resetW, cursor, resetW, 22);
            Buttons.Add(reset); ButtonActions.Add(() => ResetTrayItems());
            DrawButton(g, reset, resetLabel, false);

            int labelW = TextWidth(g, "Max in tray", 10);
            DrawText(g, "Max in tray", 14, cursor + 5, Palette.TEXT2, 10);
            int x = 14 + labelW + Gap;
            var minus = new Rectangle(x, cursor, 24, 22); Buttons.Add(minus); ButtonActions.Add(() => SetTrayMax(-1));
            DrawButton(g, minus, "-", false);
            x += 24 + Gap;
            string maxText = Settings.TrayMax.ToString();
            int maxW = Math.Max(14, TextWidth(g, maxText, 11));
            DrawText(g, maxText, x, cursor + 4, Palette.LINK, 11, true);
            x += maxW + Gap;
            var plus = new Rectangle(x, cursor, 24, 22); Buttons.Add(plus); ButtonActions.Add(() => SetTrayMax(1));
            DrawButton(g, plus, "+", false);
            cursor += 28;

            // Column geometry, derived once and reused by both the header and
            // every row, so a header can never sit over a different column.
            int rightEdge = w - 8;
            int eyeW = 62, arrowW = 22;
            int eyeX = rightEdge - eyeW;
            int downX = eyeX - Gap - arrowW;
            int upX = downX - 2 - arrowW;
            int gaugeW = 56, pctW = 34;
            int gaugeX = upX - Gap - gaugeW;
            int pctX = gaugeX - Gap - pctW;
            int nameX = 40, nameW = pctX - nameX - Gap;

            DrawText(g, "#", 16, cursor, Palette.MUTED, 10);
            DrawTextFit(g, "reading", nameX, cursor, nameW, Palette.MUTED, 10);
            DrawText(g, "now", pctX, cursor, Palette.MUTED, 10);
            DrawTextFit(g, "move / show", upX, cursor, rightEdge - upX, Palette.MUTED, 10);
            cursor += 16;
            ItemRowsTop = cursor;

            if (all.Count == 0)
            {
                DrawText(g, Refreshing ? "Probing vendors..." : "No readings yet — press Refresh.", 14, cursor + 2, Palette.MUTED, 10);
                return;
            }

            // Ordered = the tray order, then the hidden ones so they stay
            // reachable (a hidden row is the only way back).
            var ordered = new List<Metric>();
            foreach (string id in PaintedOrder())
                foreach (Metric m in all)
                    if (m.Id == id && !ordered.Contains(m)) { ordered.Add(m); break; }
            foreach (Metric m in all) if (!ordered.Contains(m)) ordered.Add(m);

            // While dragging, the row under the cursor is previewed in its new
            // slot: a drag that only commits on release, with no visible
            // movement, feels broken.
            int dragFrom = -1;
            if (Dragging && DragId != null)
            {
                for (int i = 0; i < ordered.Count; i++) if (ordered[i].Id == DragId) { dragFrom = i; break; }
                if (dragFrom >= 0)
                {
                    int target = DropIndex(ordered.Count);
                    if (target != dragFrom)
                    {
                        Metric moved = ordered[dragFrom];
                        ordered.RemoveAt(dragFrom);
                        ordered.Insert(Math.Min(target, ordered.Count), moved);
                    }
                }
            }

            int rank = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                Metric m = ordered[i];
                bool off = hidden.Contains(m.Id);
                bool held = Dragging && m.Id == DragId;
                if (!off) rank++;
                bool overflow = !off && rank > Settings.TrayMax;
                var row = new Rectangle(8, cursor, w - 16, PickRowH);
                ItemRows.Add(row); ItemRowIds.Add(m.Id);
                // The held row is highlighted, and the readings that DO reach
                // the tray get a faint plate so the cap is visible as a group.
                if (held) using (var bg = new SolidBrush(Palette.ALT)) g.FillRectangle(bg, row.X, row.Y, row.Width, row.Height);
                else if (!off && !overflow) using (var bg = new SolidBrush(Palette.SURFACE)) g.FillRectangle(bg, row.X, row.Y, row.Width, row.Height);

                Color name = off ? Palette.MUTED : overflow ? Palette.TEXT2 : Palette.TEXT;
                DrawText(g, off ? "-" : (overflow ? "·" : rank.ToString()), 16, cursor + 4, name, 10);
                DrawTextFit(g, m.Label, nameX, cursor + 4, nameW, name, 10);
                DrawText(g, m.Available ? ShownRem(m.Value) + "%" : "--", pctX, cursor + 4,
                    off || overflow ? Palette.MUTED : PctColor(m.Value), 10, true);
                DrawGauge(g, gaugeX, cursor + 7, gaugeW, 8, m.Available ? m.Value : 100, m.Available, off || overflow);

                string id2 = m.Id;
                var up = new Rectangle(upX, cursor + 2, arrowW, PickRowH - 4);
                Buttons.Add(up); ButtonActions.Add(() => MoveItem(id2, -1));
                DrawButton(g, up, "^", false);
                var down = new Rectangle(downX, cursor + 2, arrowW, PickRowH - 4);
                Buttons.Add(down); ButtonActions.Add(() => MoveItem(id2, 1));
                DrawButton(g, down, "v", false);
                var eye = new Rectangle(eyeX, cursor + 2, eyeW, PickRowH - 4);
                Buttons.Add(eye); ButtonActions.Add(() => ToggleItem(id2));
                DrawButton(g, eye, off ? "hidden" : "shown", off);
                cursor += PickRowH;
            }

            // Insertion marker: where the held row would land on release.
            if (Dragging && dragFrom >= 0)
            {
                int slot = Math.Min(DropIndex(ordered.Count), ordered.Count);
                int lineY = ItemRowsTop + slot * PickRowH;
                using (var p = new Pen(Palette.LINK)) g.DrawLine(p, 10, lineY, w - 10, lineY);
            }
        }

        // Which slot the pointer is currently over, clamped to the list.
        //
        // Row height differs per tab: the Tray tab's picker rows are a constant,
        // while an account card is as tall as its window count. The Accounts tab
        // therefore measures against CardTops — the row tops of the list BEFORE
        // the drag preview moved anything.
        //
        // Not against the painted rects: OnPaint clears them before dispatching
        // to the panel, so the preview would be computing against an empty list
        // and every drag would read as "move to the bottom". Measuring against
        // the pre-preview geometry is also what keeps the answer stable — tops
        // derived from the previewed order shift under the pointer and make the
        // marker oscillate.
        int DropIndex(int count)
        {
            if (count <= 0) return 0;
            if (Tab == TabAccounts)
            {
                for (int i = 0; i < CardTops.Count && i < count; i++)
                    if (DragY < CardTops[i]) return Math.Max(0, i - 1);
                return Math.Max(0, count - 1);
            }
            int rel = DragY - ItemRowsTop;
            int slot = (rel + PickRowH / 2) / PickRowH;
            if (slot < 0) slot = 0;
            if (slot > count - 1) slot = count - 1;
            return slot;
        }

        // ── Settings tab: everything the tray menu offers, in one place ───────
        // Every row measures its own label and its own buttons, then lays them
        // out left to right. Fixed x-offsets were what let a long label slide
        // under the first button in a wide-font theme.
        void PaintSettingsPanel(Graphics g, int cursor, int w)
        {
            int right = w - 8;
            // Every row: a label column wide enough for the widest label so the
            // rows line up, and controls that start after it.
            int labelW = 0;
            foreach (string s in new[] { "Tray layout", "Tray shows", "Fill steps",
                "Readings", "Refresh", "Volume", "On refill", "Low alert",
                "Low sound", "Numbers", "Theme" })
                labelW = Math.Max(labelW, TextWidth(g, s, 10));
            int optX = 14 + labelW + Gap * 2;

            // Which controls actually do anything depends on the layout: a fill
            // granularity means nothing when the icon draws a bare number, and a
            // number readout means nothing when the icon draws bars. A control
            // that is visibly dead is honest; one that silently ignores you is
            // what makes settings feel broken.
            bool drawsNumber = Settings.TrayMode == "single" || Settings.TrayMode == "dual";
            bool drawsFill = Settings.TrayMode == "bars" || Settings.TrayMode == "grid";
            bool multiReading = Settings.TrayMode != "single";

            // ── group 1: what the tray icon looks like ───────────────────────
            cursor = Section(g, "TRAY ICON", cursor, right);

            HintRow(14, cursor, right - 14, "what the 16x16 tray icon draws");
            DrawText(g, "Tray layout", 14, cursor + 5, Palette.TEXT2, 10);
            int bw = (right - optX) / LimisawSettings.Modes.Length;
            for (int i = 0; i < LimisawSettings.Modes.Length; i++)
            {
                string value = LimisawSettings.Modes[i];
                string label = LimisawSettings.ModeShort[i];
                string why = LimisawSettings.ModeHints[i];
                var r = new Rectangle(optX + i * bw, cursor, bw - 2, 22);
                Buttons.Add(r); ButtonActions.Add(() =>
                { Settings.TrayMode = value; Settings.Save(); Note = "Layout: " + label; FitWindow(); Refresh(); UpdateTray(); });
                Hint(r, why);
                DrawButton(g, r, label, Settings.TrayMode == value);
            }
            cursor += SetRowH;

            // The readout belongs to the number, so it sits directly under the
            // layout that produces one and greys out for the ones that do not.
            HintRow(14, cursor, right - 14, drawsNumber
                ? "what the number in the icon reads"
                : "only a number layout has a readout — pick Number or Two above");
            DrawText(g, "Tray shows", 14, cursor + 5, drawsNumber ? Palette.TEXT2 : Palette.MUTED, 10);
            string[] showLabels = { "Off", "%", "Time" };
            string[] showVals = { "off", "pct", "time" };
            string[] showHints = {
                "no number at all — the icon is the picture",
                "percent left (or used, see Numbers below)",
                "time until this window's own reset: 12m, 3h, 2d",
            };
            int sw = (right - optX) / showVals.Length;
            for (int i = 0; i < showVals.Length; i++)
            {
                string sv = showVals[i], sl = showLabels[i], sh = showHints[i];
                var r = new Rectangle(optX + i * sw, cursor, sw - 2, 22);
                if (drawsNumber)
                {
                    Buttons.Add(r); ButtonActions.Add(() =>
                    { Settings.TrayShow = sv; Settings.Save(); Note = "Tray shows: " + sl; Refresh(); UpdateTray(); });
                    Hint(r, sh);
                }
                DrawButton(g, r, sl, Settings.TrayShow == sv, drawsNumber);
            }
            cursor += SetRowH;

            HintRow(14, cursor, right - 14, drawsFill
                ? "how coarsely a bar or cell fills — coarse reads faster at 16px"
                : "only bars and cells have a fill — pick Bars or Cells above");
            DrawText(g, "Fill steps", 14, cursor + 5, drawsFill ? Palette.TEXT2 : Palette.MUTED, 10);
            bw = (right - optX) / LimisawSettings.Fills.Length;
            for (int i = 0; i < LimisawSettings.Fills.Length; i++)
            {
                // Both the value AND the label are copied out of the loop: a
                // `for` variable is captured by reference, so a lambda holding
                // `i` would index past the end when it finally runs.
                int value = LimisawSettings.Fills[i];
                string label = LimisawSettings.FillShort[i];
                string why = LimisawSettings.FillHints[i];
                var r = new Rectangle(optX + i * bw, cursor, bw - 2, 22);
                if (drawsFill)
                {
                    Buttons.Add(r); ButtonActions.Add(() =>
                    { Settings.TrayFill = value; Settings.Save(); Note = "Fill: " + label; Refresh(); UpdateTray(); });
                    Hint(r, why);
                }
                DrawButton(g, r, label, Settings.TrayFill == value, drawsFill);
            }
            cursor += SetRowH;

            // ── the preview, on its own row ──────────────────────────────────
            // Its own row on purpose: the volume slider used to run across this
            // strip and covered the very thing it was meant to help judge.
            //
            // The picture is drawn from a PRETEND quota, not the live one. A
            // preview locked to whatever the account happens to be at can only
            // ever answer one question, and the question is "what does 5% look
            // like in this mode" — which a healthy account can never show.
            HintRow(14, cursor, right - 14, "drag to see any quota level in the layout above — nothing real changes");
            DrawText(g, "Preview", 14, cursor + 5, Palette.TEXT2, 10);
            int pvX = optX;
            using (Bitmap prev = RenderPreviewBitmap())
            {
                int zoom = 3, pw = 16 * zoom, ph = 16 * zoom;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(prev, pvX, cursor + 2, pw, ph);
                using (var edge = new Pen(Palette.BEVEL)) g.DrawRectangle(edge, pvX, cursor + 2, pw - 1, ph - 1);
                var box = new Rectangle(pvX, cursor + 2, pw, ph);
                Marks.Add(box);
                Hint(box, "the real tray renderer, on a pretend " + Settings.PreviewPct + "% remaining");
                pvX += pw + Gap * 2;
            }
            string pvText = ShownRem(Settings.PreviewPct) + "%";
            int pvNumW = TextWidth(g, pvText, 11);
            int pvSliderW = Math.Max(60, right - pvX - pvNumW - Gap * 2);
            PaintVolSlider(g, pvX, cursor, pvSliderW, 0, 100, Settings.PreviewPct, "preview");
            Hint(new Rectangle(pvX, cursor, pvSliderW, 22), "pretend quota for the preview only");
            pvX += pvSliderW + Gap;
            DrawText(g, pvText, pvX, cursor + 4, PctColor(Settings.PreviewPct), 11, true);
            Marks.Add(new Rectangle(pvX, cursor + 4, pvNumW, 15));
            // The preview is 48px tall, taller than one 28px row.
            cursor += 52;

            HintRow(14, cursor, right - 14, multiReading
                ? "how many readings reach the icon, and in what order"
                : "one number shows one reading — the Tray tab picks which");
            DrawText(g, "Readings", 14, cursor + 5, Palette.TEXT2, 10);
            int shownCount = TrayMetrics().Count, totalCount = AllMetrics().Count;
            string pickLabel = multiReading
                ? shownCount + " of " + totalCount + " — pick and reorder"
                : "pick which reading";
            int pickW = Math.Min(right - optX, ButtonWidth(g, pickLabel));
            var pickBtn = new Rectangle(optX, cursor, pickW, 22);
            Buttons.Add(pickBtn); ButtonActions.Add(() => ShowTab(TabTray));
            Hint(pickBtn, "opens the Tray tab, where readings are ordered, hidden and capped");
            DrawButton(g, pickBtn, pickLabel, false);
            cursor += SetRowH;

            // ── group 2: alerts ─────────────────────────────────────────────
            cursor = Section(g, "ALERTS", cursor, right);

            bool anySound = Settings.ResetSound || Settings.NotifyLow;
            HintRow(14, cursor, right - 14, anySound
                ? "one volume for every alert — Windows has no per-sound volume, so the WAV itself is scaled"
                : "both alert sounds are off, so there is nothing to set a volume for");
            DrawText(g, "Volume", 14, cursor + 5, anySound ? Palette.TEXT2 : Palette.MUTED, 10);
            int x = optX;
            string volText = Settings.SoundVolume + "%";
            int volNumW = TextWidth(g, volText, 11);
            int dirW = ButtonWidth(g, "Folder");
            int volSliderW = Math.Max(60, Math.Min(160, right - x - volNumW - dirW - Gap * 4));
            PaintVolSlider(g, x, cursor, volSliderW, 0, 100, Settings.SoundVolume, "volume", anySound);
            if (anySound) Hint(new Rectangle(x, cursor, volSliderW, 22), "alert volume, 0 = silent");
            x += volSliderW + Gap;
            DrawText(g, volText, x, cursor + 4, anySound ? Palette.LINK : Palette.MUTED, 11, true);
            Marks.Add(new Rectangle(x, cursor + 4, volNumW, 15));
            x += volNumW + Gap;
            var dirBtn = new Rectangle(x, cursor, dirW, 22);
            Buttons.Add(dirBtn); ButtonActions.Add(() => PickSoundDir());
            Hint(dirBtn, "where the WAV pickers look — the shipped sounds keep working either way");
            DrawButton(g, dirBtn, "Folder", false);
            x += dirW + Gap;
            DrawTextFit(g, SoundCue.Library(RootPath, Settings.SoundDir), x, cursor + 5, right - x, Palette.MUTED, 10);
            cursor += SetRowH;

            // Both alerts have the same shape — a balloon switch, a chime switch,
            // the WAV and a preview — so they are drawn by one row builder. The
            // old panel had the reset chime, the low threshold and the two sounds
            // in four unrelated shapes, which is what made it unreadable.
            cursor = AlertRow(g, cursor, right, optX,
                "On refill", "a quota window refilled",
                Settings.NotifyOnReset, () =>
                {
                    Settings.NotifyOnReset = !Settings.NotifyOnReset; Settings.Save();
                    Note = "Refill balloon " + (Settings.NotifyOnReset ? "on" : "off"); Refresh();
                },
                "balloon: a Windows notification when a 5h or weekly window resets",
                Settings.ResetSound, () =>
                {
                    Settings.ResetSound = !Settings.ResetSound; Settings.Save();
                    Note = "Refill chime " + (Settings.ResetSound ? "on" : "off"); FitWindow(); Refresh();
                },
                "chime: play a sound when a window resets",
                Settings.ResetSoundFile,
                () => { string picked = PickSound(Settings.ResetSoundFile); if (picked == null) return;
                        Settings.ResetSoundFile = picked; Settings.Save(); Note = "Refill sound: " + picked; Refresh(); },
                () => Preview(Settings.ResetSoundFile));

            // The threshold owns its own row, directly above the alert it arms:
            // a slider three rows away from the switch it feeds is a guess.
            HintRow(14, cursor, right - 14, "the level the low alert fires at, once per window per reset cycle");
            DrawText(g, "Low alert", 14, cursor + 5, Palette.TEXT2, 10);
            x = optX;
            string lowLabel = Settings.NotifyLow ? "on" : "off";
            int lowW = Math.Max(ButtonWidth(g, "on"), ButtonWidth(g, "off"));
            var lowBtn = new Rectangle(x, cursor, lowW, 22);
            Buttons.Add(lowBtn); ButtonActions.Add(() =>
            {
                Settings.NotifyLow = !Settings.NotifyLow; Settings.Save();
                Note = "Low alert " + (Settings.NotifyLow ? "on" : "off"); FitWindow(); Refresh();
            });
            Hint(lowBtn, "warn me when a window drops to the threshold on the right");
            DrawButton(g, lowBtn, lowLabel, Settings.NotifyLow);
            x += lowW + Gap;
            string pctText = "at " + Settings.LowPct + "%";
            int pctNumW = TextWidth(g, pctText, 11);
            int lowSliderW = Math.Max(60, Math.Min(160, right - x - pctNumW - Gap * 3));
            PaintVolSlider(g, x, cursor, lowSliderW, 5, 95, Settings.LowPct, "lowpct", Settings.NotifyLow);
            if (Settings.NotifyLow)
                Hint(new Rectangle(x, cursor, lowSliderW, 22), "fire the low alert when a window drops to this much left");
            x += lowSliderW + Gap;
            DrawText(g, pctText, x, cursor + 4,
                Settings.NotifyLow ? Palette.LINK : Palette.MUTED, 11, true);
            Marks.Add(new Rectangle(x, cursor + 4, pctNumW, 15));
            cursor += SetRowH;

            // Its sound row only exists while the alert does.
            if (Settings.NotifyLow)
            {
                HintRow(14, cursor, right - 14, "the sound the low alert plays");
                DrawText(g, "Low sound", 14, cursor + 5, Palette.TEXT2, 10);
                PaintSoundTail(g, optX, cursor, right, Settings.LowSoundFile,
                    () => { string picked = PickSound(Settings.LowSoundFile); if (picked == null) return;
                            Settings.LowSoundFile = picked; Settings.Save(); Note = "Low sound: " + picked; Refresh(); },
                    () => Preview(Settings.LowSoundFile));
                cursor += SetRowH;
            }

            // ── group 3: the rest ───────────────────────────────────────────
            cursor = Section(g, "APP", cursor, right);

            HintRow(14, cursor, right - 14, "whether every percentage counts what is LEFT or what is SPENT");
            DrawText(g, "Numbers", 14, cursor + 5, Palette.TEXT2, 10);
            x = optX;
            string[] usedLabels = { "Left", "Used" };
            bool[] usedStates = { !Settings.ShowUsed, Settings.ShowUsed };
            string[] usedHints = {
                "count down: 20% means 20% of the quota is still yours",
                "count up: 80% means 80% is spent — bars fill as you work",
            };
            int uw = Math.Max(ButtonWidth(g, "Left"), ButtonWidth(g, "Used"));
            for (int i = 0; i < usedLabels.Length; i++)
            {
                bool wantUsed = i == 1;
                var r = new Rectangle(x, cursor, uw, 22);
                Buttons.Add(r); ButtonActions.Add(() =>
                { if (Settings.ShowUsed != wantUsed) ToggleShowUsed(); });
                Hint(r, usedHints[i]);
                DrawButton(g, r, usedLabels[i], usedStates[i]);
                x += uw + Gap;
            }
            x += Gap;
            DrawTextFit(g, "colours always warn on what is left", x, cursor + 5, right - x, Palette.MUTED, 10);
            cursor += SetRowH;

            HintRow(14, cursor, right - 14, "how often LIMISAW asks the vendors — one sweep runs every vendor CLI");
            DrawText(g, "Refresh", 14, cursor + 5, Palette.TEXT2, 10);
            x = optX;
            var slower = new Rectangle(x, cursor, 24, 22);
            Buttons.Add(slower); ButtonActions.Add(() => SetRefresh(60));
            Hint(slower, "ask less often");
            DrawButton(g, slower, "-", false);
            x += 24 + Gap;
            string mins = (Settings.RefreshSeconds / 60) + " min";
            int minsW = Math.Max(TextWidth(g, mins, 11), TextWidth(g, "60 min", 11));
            DrawText(g, mins, x, cursor + 4, Palette.LINK, 11, true);
            Marks.Add(new Rectangle(x, cursor + 4, minsW, 15));
            x += minsW + Gap;
            var faster = new Rectangle(x, cursor, 24, 22);
            Buttons.Add(faster); ButtonActions.Add(() => SetRefresh(-60));
            Hint(faster, "ask more often");
            DrawButton(g, faster, "+", false);
            x += 24 + Gap * 2;
            string autoLabel = Settings.AutoStart ? "Autostart: on" : "Autostart: off";
            int autoW = ButtonWidth(g, autoLabel);
            if (x + autoW <= right)
            {
                var autoBtn = new Rectangle(x, cursor, autoW, 22);
                Buttons.Add(autoBtn); ButtonActions.Add(() => ToggleAutostart());
                Hint(autoBtn, "start LIMISAW with Windows, silently in the tray");
                DrawButton(g, autoBtn, autoLabel, Settings.AutoStart);
                x += autoW + Gap;
            }
            int iniW = ButtonWidth(g, "Open the ini");
            if (x + iniW <= right)
            {
                var iniBtn = new Rectangle(x, cursor, iniW, 22);
                Buttons.Add(iniBtn); ButtonActions.Add(() => OpenIni());
                Hint(iniBtn, Settings.IniPath);
                DrawButton(g, iniBtn, "Open the ini", false);
            }
            cursor += SetRowH;

            HintRow(14, cursor, right - 14, "colours for the window, the tray icon and the hover panel");
            DrawText(g, "Theme", 14, cursor + 5, Palette.TEXT2, 10);
            cursor += 20;
            // Column count follows the widest theme name, so a long label like
            // "Dark Golden (Win95)" gets a wider cell instead of an ellipsis.
            int widest = 0;
            foreach (Theme t in Themes) widest = Math.Max(widest, ButtonWidth(g, t.Label));
            int avail = right - 14;
            int cols = Math.Max(1, Math.Min(3, avail / Math.Max(1, widest)));
            int colW = avail / cols;
            for (int i = 0; i < Themes.Count; i++)
            {
                Theme t = Themes[i];
                var r = new Rectangle(14 + (i % cols) * colW, cursor + (i / cols) * ThemeRowH, colW - 4, ThemeRowH - 2);
                bool active = string.Equals(t.Slug, Settings.ThemeSlug, StringComparison.OrdinalIgnoreCase);
                Buttons.Add(r); ButtonActions.Add(() =>
                {
                    Settings.ThemeSlug = t.Slug; Settings.Save(); ApplyTheme(t.Slug);
                    Note = "Theme: " + t.Label; Refresh(); UpdateTray();
                });
                Hint(r, t.Label + " — press T to cycle themes from anywhere");
                DrawButton(g, r, t.Label, active);
            }
        }

        // A group heading with a rule to its right. Three labelled groups beat
        // eleven equal rows: the panel is scanned for a category first and a
        // control second.
        int Section(Graphics g, string title, int cursor, int right)
        {
            DrawText(g, title, 14, cursor, Palette.MUTED, 10, true);
            int tw = TextWidth(g, title, 10);
            int lineY = cursor + 7;
            using (var p = new Pen(Palette.BDARK))
                g.DrawLine(p, 14 + tw + Gap, lineY, right, lineY);
            return cursor + 18;
        }

        // One alert = one row: two switches (balloon, chime) then the WAV and a
        // preview. The sound half is only drawn when the chime is on, because a
        // WAV picker for a muted alert is a control with no effect.
        int AlertRow(Graphics g, int cursor, int right, int optX,
            string label, string what,
            bool balloonOn, Action toggleBalloon, string balloonHint,
            bool chimeOn, Action toggleChime, string chimeHint,
            string wav, Action pickWav, Action previewWav)
        {
            HintRow(14, cursor, right - 14, "when " + what);
            DrawText(g, label, 14, cursor + 5, Palette.TEXT2, 10);
            int x = optX;
            int bw = Math.Max(ButtonWidth(g, "balloon"), ButtonWidth(g, "chime"));
            var balloon = new Rectangle(x, cursor, bw, 22);
            Buttons.Add(balloon); ButtonActions.Add(toggleBalloon);
            Hint(balloon, balloonHint);
            DrawButton(g, balloon, "balloon", balloonOn);
            x += bw + Gap;
            var chime = new Rectangle(x, cursor, bw, 22);
            Buttons.Add(chime); ButtonActions.Add(toggleChime);
            Hint(chime, chimeHint);
            DrawButton(g, chime, "chime", chimeOn);
            x += bw + Gap;
            if (chimeOn) PaintSoundTail(g, x, cursor, right, wav, pickWav, previewWav);
            else DrawTextFit(g, balloonOn ? "silent, balloon only" : "off", x, cursor + 5,
                right - x, Palette.MUTED, 10);
            return cursor + SetRowH;
        }

        int ThemeColumns()
        {
            // Mirrors PaintSettingsPanel so the measured height matches the grid
            // that is actually painted.
            using (var probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                int widest = 0;
                foreach (Theme t in Themes) widest = Math.Max(widest, ButtonWidth(g, t.Label));
                int avail = ClientSize.Width - 22;
                return Math.Max(1, Math.Min(3, avail / Math.Max(1, widest)));
            }
        }

        void SetRefresh(int delta)
        {
            int next = Settings.RefreshSeconds + delta;
            if (next < 60 || next > 3600) return;
            Settings.RefreshSeconds = next; Settings.Save();
            RefreshTimer.Interval = next * 1000;
            Note = "Refresh every " + (next / 60) + " min";
            Refresh();
        }

        void OpenIni()
        {
            try
            {
                if (!File.Exists(Settings.IniPath)) Settings.Save();
                Process.Start(new ProcessStartInfo(Settings.IniPath) { UseShellExecute = true });
                Note = "Opened LIMISAW.ini — press Refresh after editing";
            }
            catch (Exception ex) { Note = "Could not open the ini (" + ex.GetType().Name + ")"; }
            Refresh();
        }

        // One drag-slider renderer for both numeric rows (Problip's pattern: a
        // 12px rail, a filled portion, a 10px knob). `id` routes the mouse back
        // to the right setting. Painted 12px wide hit area stays Buttons-based?
        // No — the knob and rail are hit-tested in OnMouseDown directly, because
        // a drag is a press-move-release gesture, not a click.
        void PaintVolSlider(Graphics g, int x, int y, int w, int lo, int hi, int val, string id)
        {
            PaintVolSlider(g, x, y, w, lo, hi, val, id, true);
        }

        void PaintVolSlider(Graphics g, int x, int y, int w, int lo, int hi, int val, string id, bool enabled)
        {
            var rail = new Rectangle(x, y + 5, w, 12);
            float frac0 = hi <= lo ? 0 : (float)(val - lo) / (hi - lo);
            frac0 = Math.Max(0, Math.Min(1, frac0));
            int thx0 = rail.X + (int)((rail.Width - 10) * frac0);
            var knob0 = new Rectangle(thx0, y + 4, 10, 14);
            // A disabled rail registers no hit rectangle at all, so a drag cannot
            // start on a control that does nothing in this mode.
            if (id == "volume") { VolRailVolume = enabled ? rail : Rectangle.Empty; VolKnobVolume = enabled ? knob0 : Rectangle.Empty; }
            else if (id == "preview") { VolRailPreview = enabled ? rail : Rectangle.Empty; VolKnobPreview = enabled ? knob0 : Rectangle.Empty; }
            else { VolRailLow = enabled ? rail : Rectangle.Empty; VolKnobLow = enabled ? knob0 : Rectangle.Empty; }
            if (id == VolDrag) VolRail = rail;
            Draw.Bevel(g, rail.X, rail.Y, rail.Width, rail.Height, false);
            using (var bg = new SolidBrush(Palette.SURFACE))
                g.FillRectangle(bg, rail.X + 1, rail.Y + 1, rail.Width - 2, rail.Height - 2);
            using (var fill = new SolidBrush(enabled ? Palette.LINK : Palette.MUTED))
                g.FillRectangle(fill, rail.X + 1, rail.Y + 1, (int)((rail.Width - 2) * frac0), rail.Height - 2);
            // Knob is 14px tall inside the 22px row (y+4): an 18px knob bled
            // 1px into the next row's buttons and tripped layout_fit.
            var knob = new Rectangle(knob0.X, y + 4, knob0.Width, 14);
            Draw.Bevel(g, knob.X, knob.Y, knob.Width, knob.Height, true);
            using (var kb = new SolidBrush(enabled ? Palette.ALT : Palette.SURFACE))
                g.FillRectangle(kb, knob.X + 1, knob.Y + 1, knob.Width - 2, knob.Height - 2);
            // The knob must not eat a layout-fit check: register it as content
            // the buttons may not cover, same as gauges.
            Marks.Add(knob);
        }

        void SetVolumeFromX(int px)
        {
            int next = Math.Max(0, Math.Min(100,
                (int)Math.Round((double)(px - VolRail.X) / Math.Max(1, VolRail.Width) * 100)));
            if (next == Settings.SoundVolume) return;
            Settings.SoundVolume = next; Settings.Save();
            Note = "Volume " + next + "%";
            Refresh();
        }

        void SetLowPctFromX(int px)
        {
            int next = 5 + (int)Math.Round((double)(px - VolRail.X) / Math.Max(1, VolRail.Width) * 90);
            next = Math.Max(5, Math.Min(95, next));
            if (next == Settings.LowPct) return;
            Settings.LowPct = next; Settings.Save();
            // The alert that already fired was for the old threshold, so let a
            // window that is now above the new one alert again.
            NotifiedLow.Clear();
            Note = "Low alert at " + next + "% left";
            Refresh();
        }

        // The preview's pretend quota. Nothing real changes — it only moves the
        // number the preview icon is drawn from, so a layout can be judged at 5%
        // and at 90% without waiting for the account to get there.
        void SetPreviewFromX(int px)
        {
            int next = (int)Math.Round((double)(px - VolRail.X) / Math.Max(1, VolRail.Width) * 100);
            next = Math.Max(0, Math.Min(100, next));
            if (next == Settings.PreviewPct) return;
            Settings.PreviewPct = next; Settings.Save();
            Note = "Preview at " + next + "% left";
            Refresh();
        }

        void EndVolDrag()
        {
            if (VolDrag == null) return;
            VolDrag = null; Capture = false;
            Refresh(); UpdateTray();
        }

        void SetVolume(int delta)
        {
            int next = Math.Max(0, Math.Min(100, Settings.SoundVolume + delta));
            if (next == Settings.SoundVolume) return;
            Settings.SoundVolume = next; Settings.Save();
            Note = "Volume " + next + "%";
            Refresh();
        }

        void SetLowPct(int delta)
        {
            int next = Math.Max(5, Math.Min(95, Settings.LowPct + delta));
            if (next == Settings.LowPct) return;
            Settings.LowPct = next; Settings.Save();
            // The alert that already fired was for the old threshold, so let a
            // window that is now above the new one alert again.
            NotifiedLow.Clear();
            Note = "Low alert at " + next + "% left";
            Refresh();
        }

        // The tail of an alert row: the sound that is set, then the two buttons
        // that change it. Laid out from the right so a WAV with a long name
        // yields with an ellipsis instead of pushing Play off the window.
        void PaintSoundTail(Graphics g, int x, int y, int right, string file, Action pick, Action preview)
        {
            int wavW = ButtonWidth(g, "WAV"), playW = ButtonWidth(g, "Play");
            string path = SoundCue.Resolve(RootPath, Settings.SoundDir, file) ?? "";
            bool missing = path.Length == 0;
            string label = missing ? "(no sound: pick one)" : Path.GetFileName(path);
            int nameW = right - x - wavW - playW - Gap * 3;
            if (nameW > 12)
                DrawTextFit(g, label, x, y + 5, nameW, missing ? Palette.DANGERTXT : Palette.TEXT2, 10);
            var wav = new Rectangle(right - wavW - playW - Gap, y, wavW, 22);
            Buttons.Add(wav); ButtonActions.Add(pick);
            DrawButton(g, wav, "WAV", false);
            var play = new Rectangle(right - playW, y, playW, 22);
            Buttons.Add(play); ButtonActions.Add(preview);
            DrawButton(g, play, "Play", false);
        }

        // Choosing the WAV behind an alert. A file inside the sound folder is
        // stored as a bare name so the settings survive the app moving to
        // another machine; anything else is stored as the absolute path it is.
        // Returns null when the dialog was cancelled, so the caller keeps the
        // old value instead of writing an empty one.
        string PickSound(string current)
        {
            try
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Choose a WAV for this alert";
                    dlg.Filter = "WAV sound (*.wav)|*.wav|All files (*.*)|*.*";
                    string lib = Path.GetFullPath(SoundCue.Library(RootPath, Settings.SoundDir));
                    if (Directory.Exists(lib)) dlg.InitialDirectory = lib;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                    string picked = Path.GetFullPath(dlg.FileName);
                    string prefix = lib.TrimEnd('\\') + "\\";
                    if (picked.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return picked.Substring(prefix.Length);
                    return picked;
                }
            }
            catch (Exception ex) { Note = "Could not open the file dialog (" + ex.GetType().Name + ")"; Refresh(); return null; }
        }

        void PickSoundDir()
        {
            try
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Which folder holds your alert WAVs?";
                    string lib = Path.GetFullPath(SoundCue.Library(RootPath, Settings.SoundDir));
                    if (Directory.Exists(lib)) dlg.SelectedPath = lib;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    Settings.SoundDir = dlg.SelectedPath; Settings.Save();
                    Note = "Sounds from " + dlg.SelectedPath;
                }
            }
            catch (Exception ex) { Note = "Could not open the folder dialog (" + ex.GetType().Name + ")"; }
            Refresh();
        }

        // A preview is heard even when the alert is switched off, but ALWAYS
        // at the real volume: the request is "let the test button reflect the
        // volume", so flooring to 25% was the exact lie being removed.
        void Preview(string file)
        {
            if (SoundCue.Resolve(RootPath, Settings.SoundDir, file) == null)
            {
                Note = "No such WAV: " + file; Refresh(); return;
            }
            SoundCue.Play(RootPath, Settings.SoundDir, file, Settings.SoundVolume);
            Note = "Preview at " + Settings.SoundVolume + "%";
            Refresh();
        }

        public Action AutostartApplier;

        void ToggleAutostart()
        {
            Settings.AutoStart = !Settings.AutoStart; Settings.Save();
            if (AutostartApplier != null) AutostartApplier();
            Note = "Autostart " + (Settings.AutoStart ? "on" : "off");
            Refresh();
        }

        // Piping a remote script into a shell is exactly the kind of action
        // that must never be implicit: the exact command and its publisher are
        // shown, and only an explicit Yes runs it, in a VISIBLE console.
        void InstallCli(CliInfo cli)
        {
            string body = cli.Label + "\n\nThis runs the vendor's published installer:\n\n"
                + cli.Command + "\n\nPublisher: " + cli.Source
                + "\nInstalls to: " + cli.Target
                + "\n\nA PowerShell window will open so you can watch it. Continue?";
            if (MessageBox.Show(this, body, "LIMISAW — install " + cli.Label,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoLogo -ExecutionPolicy Bypass -NoExit -Command \"" + cli.PowerShell.Replace("\"", "`\"") + "\"")
                { UseShellExecute = true };
                Process.Start(psi);
                Note = cli.Label + ": installer started — press Refresh when it finishes.";
            }
            catch (Exception ex) { Note = cli.Label + ": could not start installer (" + ex.GetType().Name + ")"; }
            Refresh();
        }

        static string ShortText(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max - 3) + "...";
        }

        // Every reading is stored and compared as REMAINING percent. Only what
        // is DISPLAYED flips, so colour thresholds, the lowest-metric pick and
        // reset detection keep one meaning in both modes.
        int ShownRem(int remaining) { return Settings.ShowUsed ? 100 - remaining : remaining; }

        string FormatPct(bool avail, int pct) { return avail ? (ShownRem(pct) + "%") : "--"; }
        Color PctColor(int pct) { return Draw.PctColor(pct); }
        Color FillColor(int pct, bool available) { return Draw.FillColor(pct, available); }

        // The bar shows what the toggle asks for — remaining, or used — while
        // its colour still reads the remaining percent. Before this, Used mode
        // flipped only the digits, so an exhausted account drew a full green bar
        // labelled "100%".
        void DrawGauge(Graphics g, int x, int y, int w, int h, int rem, bool available, bool dim)
        {
            Marks.Add(new Rectangle(x, y, w, h));
            Draw.Gauge(g, x, y, w, h, ShownRem(rem), rem, available, dim);
        }

        static string FriendlyTime(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "--";
            DateTime t;
            if (!DateTime.TryParse(iso, out t)) return iso;
            if (t.Kind == DateTimeKind.Unspecified) t = DateTime.SpecifyKind(t, DateTimeKind.Local);
            TimeSpan delta = t - DateTime.Now;
            if (delta.TotalSeconds < 0) return "now";
            if (delta.TotalHours < 1) return "in " + (int)delta.TotalMinutes + "m";
            if (delta.TotalDays < 1) return "in " + (int)delta.TotalHours + "h " + delta.Minutes + "m";
            return "in " + (int)delta.TotalDays + "d " + t.ToString("HH:mm");
        }

        void DrawText(Graphics g, string s, int x, int y, Color c, int pt, bool bold = false)
        { using (var br = new SolidBrush(c)) g.DrawString(s, Cached(pt), br, (float)x, (float)y); }

        int TextWidth(Graphics g, string s, int pt)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return (int)Math.Ceiling(g.MeasureString(s, Cached(pt)).Width);
        }

        // Text that must not run into the control on its right. Measuring and
        // trimming beats clipping: a half-drawn glyph reads as a rendering bug,
        // while an ellipsis reads as "there is more".
        void DrawTextFit(Graphics g, string s, int x, int y, int maxWidth, Color c, int pt, bool bold = false)
        {
            if (string.IsNullOrEmpty(s) || maxWidth <= 0) return;
            string text = Elide(g, s, maxWidth, pt);
            if (text.Length == 0) return;
            Marks.Add(new Rectangle(x, y, TextWidth(g, text, pt), pt + 4));
            DrawText(g, text, x, y, c, pt, bold);
        }

        // Shortened from the MIDDLE, not the end. Every identifier in this app is
        // distinguished by its tail — "codex/Account2/five_hour" vs
        // ".../weekly", "Antigravity · Claude and GPT models" vs "· Gemini
        // models" — so a trailing ellipsis throws away the only part that says
        // WHICH reading a row is. `Codex/Accou...` is three identical rows.
        string Elide(Graphics g, string s, int maxWidth, int pt)
        {
            if (string.IsNullOrEmpty(s) || maxWidth <= 0) return "";
            if (TextWidth(g, s, pt) <= maxWidth) return s;
            const string cut = "..";
            int cutW = TextWidth(g, cut, pt);
            if (cutW > maxWidth) return "";
            // Grow head and tail alternately, tail first: the tail carries the
            // identity, so it wins the last available pixel.
            int head = 0, tail = 0;
            while (true)
            {
                bool grew = false;
                if (head + tail < s.Length)
                {
                    string tryTail = s.Substring(0, head) + cut + s.Substring(s.Length - (tail + 1));
                    if (TextWidth(g, tryTail, pt) <= maxWidth) { tail++; grew = true; }
                }
                if (head + tail < s.Length)
                {
                    string tryHead = s.Substring(0, head + 1) + cut + s.Substring(s.Length - tail);
                    if (TextWidth(g, tryHead, pt) <= maxWidth) { head++; grew = true; }
                }
                if (!grew) break;
            }
            if (head == 0 && tail == 0) return "";
            return s.Substring(0, head) + cut + s.Substring(s.Length - tail);
        }

        void DrawBevel(Graphics g, int x, int y, int w, int h, bool raised)
        { Draw.Bevel(g, x, y, w, h, raised); }

        // A button label is never truncated by the caller's guess: the label
        // decides the font it needs, stepping down 10 -> 9 -> 8 until it fits,
        // and only then loses characters. "Showing: Le" was exactly this bug -
        // a fixed 10pt label in a box measured for shorter text.
        void DrawButton(Graphics g, Rectangle r, string label, bool selected)
        {
            DrawButton(g, r, label, selected, true);
        }

        // `enabled: false` = the control exists but does nothing in the current
        // mode. Drawn flat and muted rather than hidden: a row that disappears
        // makes the panel jump, and the user cannot learn a setting they never
        // see. The caller must also skip registering it in Buttons.
        void DrawButton(Graphics g, Rectangle r, string label, bool selected, bool enabled)
        {
            Color face = !enabled ? Palette.SURFACE : selected ? Palette.ALT : Palette.RAISED;
            using (var bg = new SolidBrush(face)) g.FillRectangle(bg, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
            DrawBevel(g, r.X, r.Y, r.Width, r.Height, enabled && !selected);
            int inner = r.Width - 8;
            int pt = 10;
            while (pt > 8 && TextWidth(g, label, pt) > inner) pt--;
            string text = label;
            if (TextWidth(g, text, pt) > inner)
            {
                Cropped.Add(label);
                text = Elide(g, label, inner, pt);
            }
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var br = new SolidBrush(enabled ? Palette.TEXT : Palette.MUTED))
                g.DrawString(text, Cached(pt), br, new RectangleF(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), fmt);
        }

        // Width a button needs for its label at 10pt, so a row can be laid out
        // from its content instead of from a hardcoded guess.
        int ButtonWidth(Graphics g, string label) { return TextWidth(g, label, 10) + BtnPad; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // A slider rail grabs the press before any button: the rail spans the
            // row, so without this the knob press would fall through to whatever
            // sits behind it. Must run BEFORE Buttons, and the rail rect is the
            // one painted last frame (VolRail is refreshed every OnPaint).
            if (Tab == TabSettings && e.Button == MouseButtons.Left && VolDrag == null)
            {
                // Which rail? Re-derive by hit: the rail rect alone does not say
                // which setting it belongs to, so probe both rows' rails by
                // repaint position — instead: PaintVolSlider stored only the
                // ACTIVE drag rail. So store both rails at paint time.
                if (VolRailVolume.Contains(e.Location) || VolKnobVolume.Contains(e.Location))
                {
                    VolDrag = "volume"; VolRail = VolRailVolume; Capture = true;
                    SetVolumeFromX(e.X); return;
                }
                if (VolRailLow.Contains(e.Location) || VolKnobLow.Contains(e.Location))
                {
                    VolDrag = "lowpct"; VolRail = VolRailLow; Capture = true;
                    SetLowPctFromX(e.X); return;
                }
                if (VolRailPreview.Contains(e.Location) || VolKnobPreview.Contains(e.Location))
                {
                    VolDrag = "preview"; VolRail = VolRailPreview; Capture = true;
                    SetPreviewFromX(e.X); return;
                }
            }
            for (int i = 0; i < Buttons.Count; i++) { if (Buttons[i].Contains(e.Location)) { Note = ""; ButtonActions[i](); return; } }
            // A press on a tray row arms a drag but does not start one: the
            // pointer must travel DragSlop pixels first, so a plain click on a
            // row (which does nothing) never reorders anything by accident.
            if ((Tab == TabTray || Tab == TabAccounts) && e.Button == MouseButtons.Left)
                for (int i = 0; i < ItemRows.Count; i++)
                    if (ItemRows[i].Contains(e.Location))
                    { DragId = ItemRowIds[i]; DragStartY = e.Y; DragY = e.Y; Dragging = false; Capture = true; return; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            // A volume slider in flight owns the pointer until release: every
            // move re-sets from the x position, live, like Problip's slider.
            if (VolDrag != null && e.Button == MouseButtons.Left)
            {
                if (VolDrag == "volume") SetVolumeFromX(e.X);
                else if (VolDrag == "preview") SetPreviewFromX(e.X);
                else SetLowPctFromX(e.X);
                return;
            }
            if (DragId == null)
            {
                // Hover explanation. Only repaint when the sentence actually
                // changes: a repaint per mouse-move over a panel of gauges is
                // visible churn for nothing.
                string next = HintAt(e.Location);
                if (next != Hover) { Hover = next; Refresh(); }
                return;
            }
            DragY = e.Y;
            if (!Dragging && Math.Abs(e.Y - DragStartY) >= DragSlop) Dragging = true;
            if (Dragging) Refresh();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (Hover.Length > 0) { Hover = ""; Refresh(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (VolDrag != null) { EndVolDrag(); return; }
            if (DragId == null) return;
            string id = DragId; bool dragged = Dragging;
            DragId = null; Dragging = false; Capture = false;
            if (!dragged) { Refresh(); return; }
            // The drop is committed against the SAME ordered list the panel
            // painted, so the row lands exactly where the marker showed.
            List<string> order = Tab == TabAccounts ? PaintedCardOrder() : PaintedOrder();
            int from = order.IndexOf(id);
            if (from < 0) { Refresh(); return; }
            int to = DropIndex(order.Count);
            if (to == from) { Refresh(); return; }
            order.RemoveAt(from); order.Insert(Math.Min(to, order.Count), id);
            if (Tab == TabAccounts) Settings.SetCardOrder(order);
            else Settings.SetItemOrder(order);
            Settings.Save();
            Refresh(); UpdateTray();
        }

        public void ToggleShowUsed()
        {
            Settings.ShowUsed = !Settings.ShowUsed; Settings.Save();
            Refresh(); UpdateTray();
        }

        public void ShowTab(int tab) { Tab = tab; FitWindow(); Refresh(); }

        public void CycleTheme()
        {
            if (Themes.Count == 0) return;
            int at = 0;
            for (int i = 0; i < Themes.Count; i++)
                if (string.Equals(Themes[i].Slug, Settings.ThemeSlug, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            Theme next = Themes[(at + 1) % Themes.Count];
            Settings.ThemeSlug = next.Slug; Settings.Save();
            ApplyTheme(next.Slug);
            Note = "Theme: " + next.Label;
            Refresh(); UpdateTray();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { RefreshData(); e.Handled = true; return; }
            if (e.KeyCode == Keys.U) { ToggleShowUsed(); e.Handled = true; return; }
            if (e.KeyCode == Keys.T) { CycleTheme(); e.Handled = true; return; }
            if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D4)
            { ShowTab(e.KeyCode - Keys.D1); e.Handled = true; return; }
            if (e.KeyCode == Keys.Escape) { HideToTray(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        // ── tray ─────────────────────────────────────────────────────────────
        // The shell tooltip is one line of 63 plain characters — unreadable for
        // ten readings across three vendors. A themed panel is shown on hover
        // instead, with the same gauges and colours as the window; the OS
        // tooltip is left empty so only one thing appears.
        public List<TrayPopup.Row> PopupRows()
        {
            var rows = new List<TrayPopup.Row>();
            if (Accounts.Count == 0)
            {
                rows.Add(new TrayPopup.Row
                {
                    Left = Refreshing ? "probing vendors..." : "no readings yet",
                    Available = false, Dim = true,
                });
                return rows;
            }
            List<Metric> selected = TrayMetrics();
            var inTray = new List<string>();
            foreach (Metric m in selected) inTray.Add(m.Id);
            foreach (AccountData a in Accounts)
            {
                rows.Add(new TrayPopup.Row
                {
                    Header = true, Left = AccountTitle(a),
                    Right = a.Carried ? "last good" + (a.CarriedAt.Length > 0 ? " " + a.CarriedAt : "")
                        : a.Plan != null ? a.Plan : (a.Ok ? "" : a.Status.ToLowerInvariant()),
                });
                // Same slot and same rule as the card (CardNote): the reason is
                // drawn whether or not unavailable windows follow it.
                bool bad;
                string lead = CardNote(a, out bad);
                if (lead != null)
                    rows.Add(new TrayPopup.Row
                    { Left = lead, Available = false, Dim = true });
                foreach (WindowData w in a.Windows)
                {
                    string id = a.Provider + "/" + a.Name + "/" + w.Key;
                    string label = w.Label + (w.GroupLabel.Length > 0 ? " · " + w.GroupLabel : "");
                    // A reading the tray does not draw is dimmed rather than
                    // dropped: the panel is also how the user sees what the cap
                    // and the hide list are doing. Carried-forward numbers are
                    // dimmed for the same reason - they are real, but not fresh.
                    bool drawn = inTray.Contains(id) && !a.Carried;
                    rows.Add(new TrayPopup.Row
                    {
                        Left = (drawn ? "● " : "  ") + label,
                        Right = w.GatedBy != null ? "locked" : w.AssumedFull ? "refilled" : FriendlyTime(w.Reset),
                        Pct = ShownRem(w.Rem), Rem = w.Rem,
                        Available = w.Available, Gauge = true, Dim = !drawn,
                    });
                }
            }
            return rows;
        }

        public string PopupTitle()
        {
            int value; bool available; string label;
            GetTrayMetric(out value, out available, out label);
            string head = "LIMISAW — " + (Settings.ShowUsed ? "highest used " : "lowest remaining ");
            head += available ? ShownRem(value) + "% (" + label + ")" : "no reading";
            if (Stale) head += " · stale";
            return head;
        }

        // Set once by Program when the themed hover panel is live. The shell
        // tooltip is then left EMPTY, because Windows would draw it on top of
        // the panel and two tooltips over one icon is worse than either. If the
        // panel cannot be created the one-line tip is still assigned, so the
        // 63-character clamp below stays load-bearing rather than becoming
        // decoration.
        public bool PopupOwnsTooltip;

        // Never throws: callers are a BeginInvoke from the refresh thread and the
        // tray menu, neither of which can handle an exception. But a swallowed
        // failure used to freeze the tray on a stale number with no trace, so the
        // reason is recorded and shown in the window footer instead.
        public void UpdateTray()
        {
            try
            {
                int value; bool available; string metricLabel;
                GetTrayMetric(out value, out available, out metricLabel);
                Tray.Text = PopupOwnsTooltip ? "" : BuildTip(metricLabel, value, available);

                using (Bitmap bmp = RenderTrayBitmap(value, available))
                {
                    IntPtr handle = bmp.GetHicon();
                    try
                    {
                        Icon next;
                        using (Icon borrowed = Icon.FromHandle(handle)) next = (Icon)borrowed.Clone();
                        Icon old = Tray.Icon; Tray.Icon = next;
                        // A leaked replaced icon is a leak, not a wrong reading:
                        // it must not mask an update that already succeeded.
                        if (old != null) { try { old.Dispose(); } catch { } }
                    }
                    finally { Native.DestroyIcon(handle); }
                }
                TrayError = "";
            }
            catch (Exception ex)
            {
                TrayError = ex.GetType().Name + ": " + ex.Message;
                try { Refresh(); } catch { }
            }
        }

        // The tray art is a fixed 16x16 pixel grid. To stay pixel-exact at any
        // DPI the master is blown up by a whole factor only and centred on the
        // canvas the shell asked for, so Windows never resamples it.
        // No-arg render for the Settings preview: reads the live metric itself,
        // so the preview can never disagree with the real tray icon.
        Bitmap RenderTrayBitmap()
        {
            int value; bool available; string label, reset;
            GetTrayMetric(out value, out available, out label, out reset);
            return RenderTrayBitmap(value, available, reset);
        }

        // The Settings preview: the SAME renderer the shell gets, driven by a
        // pretend percentage. Going through the real path is the point — a
        // hand-drawn mock-up can agree with the icon today and drift tomorrow.
        Bitmap RenderPreviewBitmap()
        {
            PreviewPct = Settings.PreviewPct;
            try { return RenderTrayBitmap(Settings.PreviewPct, true, PreviewReset()); }
            finally { PreviewPct = -1; }
        }

        // >= 0 while a preview is rendering. The multi-reading layouts read the
        // live metric list, so this is what makes them draw the pretend level
        // too; -1 means "not previewing" and every path uses real values.
        int PreviewPct = -1;

        // A plausible reset for the Time readout, derived from the pretend level
        // so it moves with the slider instead of sitting at a constant.
        string PreviewReset()
        {
            double hours = 0.25 + 4.75 * Math.Max(0, Math.Min(100, Settings.PreviewPct)) / 100.0;
            return DateTime.Now.AddHours(hours).ToString("yyyy-MM-ddTHH:mm:ss");
        }

        Bitmap RenderTrayBitmap(int value, bool available)
        {
            return RenderTrayBitmap(value, available, null);
        }

        Bitmap RenderTrayBitmap(int value, bool available, string reset)
        {
            int size = 16;
            try { size = Math.Max(16, SystemInformation.SmallIconSize.Width); } catch { }
            int scale = size / 16;
            if (scale < 1) scale = 1;
            int art = 16 * scale;
            int pad = (size - art) / 2;
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Bitmap master = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics mg = Graphics.FromImage(master))
                {
                    mg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                    mg.SmoothingMode = SmoothingMode.None;
                    mg.InterpolationMode = InterpolationMode.NearestNeighbor;
                    mg.PixelOffsetMode = PixelOffsetMode.None;
                    mg.Clear(Palette.BG);
                    using (var edge = new Pen(Palette.BEVEL)) mg.DrawRectangle(edge, 0, 0, 15, 15);
                    if (Settings.TrayMode == "grid") DrawGrid(mg);
                    else if (Settings.TrayMode == "bars") DrawBars(mg);
                    else if (Settings.TrayMode == "dual") DrawDual(mg);
                    else DrawSingle(mg, value, available, reset);
                }
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.SmoothingMode = SmoothingMode.None;
                    using (var back = new SolidBrush(Palette.BG)) g.FillRectangle(back, 0, 0, size, size);
                    g.DrawImage(master, pad, pad, art, art);
                }
            }
            return bmp;
        }

        // NotifyIcon.Text throws ArgumentOutOfRangeException above 63 characters,
        // and UpdateTray sets the tooltip BEFORE the icon: one over-long tip used
        // to abort the whole update and freeze the tray on the previous picture.
        // Every branch must therefore leave through the same clamp.
        const int TrayTipMax = 63;

        string BuildTip(string metricLabel, int value, bool available)
        {
            // The metric is always picked on REMAINING, so the same window is the
            // lowest-left one and the most-used one: only the wording flips.
            string head = "LIMISAW | " + (Settings.ShowUsed ? "highest used " : "lowest remaining ");
            string tip = head + (available ? ShownRem(value) + "% (" + ShortText(metricLabel, 28) + ")" : "--");
            foreach (AccountData a in Accounts)
            {
                if (!a.Ok && !a.Carried) continue;
                string piece = " | " + ShortName(a) + " " + a.Abbrev(Settings.ShowUsed);
                if (tip.Length + piece.Length > TrayTipMax - (Stale ? 8 : 0)) break;
                tip += piece;
            }
            if (Stale) tip += " | stale";
            return ClampTip(tip);
        }

        static string ShortName(AccountData a)
        {
            string p = a.Provider.Length > 0 ? char.ToUpperInvariant(a.Provider[0]).ToString() : "?";
            string n = a.Name != null && a.Name.Length > 0 ? a.Name : "";
            for (int i = n.Length - 1; i >= 0; i--)
                if (char.IsDigit(n[i])) return p + n[i];
            return p;
        }

        static string ClampTip(string tip)
        {
            if (string.IsNullOrEmpty(tip) || tip.Length <= TrayTipMax) return tip;
            return tip.Substring(0, TrayTipMax - 3) + "...";
        }

        void DrawSingle(Graphics g, int value, bool available)
        {
            DrawSingle(g, value, available, null);
        }

        void DrawSingle(Graphics g, int value, bool available, string reset)
        {
            // Colour keeps reading the REMAINING percent in both modes: red still
            // means "almost out", never "barely used".
            // TrayShow picks the TEXT: off = icon only (no number at all), pct =
            // the old behaviour, time = CountdownText of this window's reset.
            // A 16px cell holds 2 countdown chars at 8pt ("3h", "12m"); anything
            // longer steps down and clips to the cell instead of overflowing it.
            string text;
            if (!available) text = "--";
            else if (Settings.TrayShow == "off") text = "";
            else if (Settings.TrayShow == "time") text = CountdownText(reset);
            else text = ShownRem(value).ToString();
            if (text.Length == 0) return;
            Color col = Stale ? Palette.MUTED : (available ? PctColor(value) : Palette.MUTED);
            int size = text.Length >= 3 ? 6 : 8;
            using (var br = new SolidBrush(col))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, Pix.Get(size), br, new RectangleF(1, 1, 14, 14), fmt);
            }
        }

        // Two stacked numbers: the lowest SHORT window (5h-class) over the
        // lowest LONG one (weekly/monthly), across the SELECTED readings only.
        // One glance answers both "can I work now" and "will I last the week".
        void DrawDual(Graphics g)
        {
            int shortRem = int.MaxValue, longRem = int.MaxValue;
            foreach (Metric m in TrayMetrics())
            {
                if (!m.Available) continue;
                if (m.IsShort) shortRem = Math.Min(shortRem, m.Value);
                else longRem = Math.Min(longRem, m.Value);
            }
            DrawHalfNumber(g, 1, shortRem);
            DrawHalfNumber(g, 8, longRem);
        }

        void DrawHalfNumber(Graphics g, int top, int rem)
        {
            bool available = rem != int.MaxValue;
            string text = available ? (Settings.ShowUsed ? 100 - rem : rem).ToString() : "--";
            Color col = Stale ? Palette.MUTED : (available ? PctColor(rem) : Palette.MUTED);
            using (var br = new SolidBrush(col))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, Pix.Get(text.Length >= 3 ? 5 : 6), br, new RectangleF(1, top, 14, 7), fmt);
            }
        }

        // Fill granularity is a setting, not a constant: 1/2 and 1/4 read
        // instantly at 16px, 1/8 and exact carry more detail on a large tray.
        int FillSteps(int pct)
        {
            int steps = Settings.TrayFill;
            if (steps >= 100) return -1;
            return Math.Min(steps, Math.Max(0, pct * steps / 100));
        }

        // `rem` is always the remaining percent: the area filled follows the
        // Used/Left toggle, the colour follows what is LEFT. A tray cell that
        // fills up as quota is spent is the whole point of Used mode.
        void FillArea(Graphics g, Rectangle cell, int rem, bool available, bool bottomUp)
        {
            using (var under = new SolidBrush(Palette.BG)) g.FillRectangle(under, cell.X, cell.Y, cell.Width, cell.Height);
            if (!available) return;
            int shown = ShownRem(rem);
            int cellArea = cell.Width * cell.Height;
            int steps = FillSteps(shown);
            int fillArea = steps < 0 ? cellArea * Math.Min(100, shown) / 100 : cellArea * steps / Settings.TrayFill;
            if (fillArea <= 0) return;
            using (var brush = new SolidBrush(FillColor(rem, true)))
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

        // One cell per SELECTED reading, in the user's order, laid out as
        // 1x1 / 2x2 / 3x3. The cap (Tray tab) is what keeps a cell big enough
        // to read: nine windows in 14 pixels is already the practical floor.
        void DrawGrid(Graphics g)
        {
            List<Metric> items = TrayMetrics();
            int n = Math.Max(1, Math.Min(LimisawSettings.MaxTrayItems, items.Count));
            int cols = n <= 1 ? 1 : n <= 4 ? 2 : 3;
            int rows = (n + cols - 1) / cols;
            int cw = 14 / cols, ch = 14 / rows;
            for (int i = 0; i < n; i++)
            {
                bool have = i < items.Count;
                bool available = have && items[i].Available;
                // 100 for the missing slot: FillArea skips it anyway, and a 0
                // would ask for the danger colour on an empty cell.
                int value = have ? items[i].Value : 100;
                var cell = new Rectangle(1 + (i % cols) * cw, 1 + (i / cols) * ch, cw, ch);
                FillArea(g, cell, value, available, false);
                using (var p = new Pen(Palette.BEVEL)) g.DrawRectangle(p, cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
            }
        }

        // One vertical bar per SELECTED reading, in the user's order, filled
        // bottom-up.
        void DrawBars(Graphics g)
        {
            List<Metric> items = TrayMetrics();
            int n = Math.Max(1, Math.Min(7, items.Count));
            int bw = Math.Max(1, 14 / n);
            for (int i = 0; i < n; i++)
            {
                bool have = i < items.Count;
                bool available = have && items[i].Available;
                int value = have ? items[i].Value : 100;
                var bar = new Rectangle(1 + i * bw, 1, bw, 14);
                FillArea(g, bar, value, available, true);
                using (var p = new Pen(Palette.BEVEL)) g.DrawRectangle(p, bar.X, bar.Y, bar.Width - 1, bar.Height - 1);
            }
        }

        public string AccountSummary(int index, bool showUsed)
        {
            if (index >= Accounts.Count) return null;
            AccountData a = Accounts[index];
            string body = (a.Ok || a.Carried) ? a.Abbrev(showUsed) : (a.Error ?? a.Status);
            if (a.Carried) body += "  (last good" + (a.CarriedAt.Length > 0 ? " " + a.CarriedAt : "") + ")";
            return AccountTitle(a) + ": " + body;
        }

        public int AccountCount { get { return Accounts.Count; } }
        public List<CliInfo> CliList { get { return Clis; } }
        public void RunInstall(CliInfo cli) { InstallCli(cli); }
        public void ApplyChoice() { Refresh(); UpdateTray(); }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "Local\\LimisawApp", out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (var signal = System.Threading.EventWaitHandle.OpenExisting("Local\\LimisawShow"))
                            signal.Set();
                    }
                    catch
                    {
                        IntPtr existing = Native.FindWindow(null, "LIMISAW");
                        if (existing != IntPtr.Zero) { Native.ShowWindow(existing, 5); Native.SetForegroundWindow(existing); }
                    }
                    return;
                }
                var showEvent = new System.Threading.EventWaitHandle(false,
                    System.Threading.EventResetMode.AutoReset, "Local\\LimisawShow");

                // Everything the app needs is inside the exe, so its own folder
                // is the root: LIMISAW.ini is written there, and Themes\ /
                // Sounds\ next to it override the embedded copies.
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                var s = new LimisawSettings(dir); s.Load();
                List<Theme> themes = Theme.Load(dir);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                NotifyIcon tray = new NotifyIcon(); tray.Visible = true;
                LimisawForm form = null; form = new LimisawForm(dir, s, tray, themes);
                // Autostart is a registry write, so the form asks Program to do
                // it rather than reaching into HKCU itself.
                form.AutostartApplier = () => ApplyAutostart(s);
                // BeginInvoke from the single-instance signal needs a handle even
                // when Windows starts the application hidden in the tray.
                IntPtr hiddenHandle = form.Handle;
                Action show = () => { form.Show(); form.Activate(); };
                var showWait = System.Threading.ThreadPool.RegisterWaitForSingleObject(showEvent, (state, timedOut) =>
                {
                    if (form.IsDisposed || !form.IsHandleCreated) return;
                    try { form.BeginInvoke(show); } catch { }
                }, null, System.Threading.Timeout.Infinite, false);
                tray.ContextMenuStrip = BuildMenu(s, tray, () => form, show, () => { form.RefreshData(); }, themes);

                // Themed hover panel with one gauge per reading. If it cannot be
                // created the app keeps the plain one-line tooltip rather than
                // losing the reading entirely.
                TrayPopup popup = null;
                Timer hoverGuard = null;
                try
                {
                    popup = new TrayPopup();
                    hoverGuard = new Timer { Interval = 350 };
                    Rectangle hotspot = Rectangle.Empty;
                    TrayPopup panel = popup; Timer guard = hoverGuard;
                    // NotifyIcon has no "mouse left" event, so a poll decides
                    // when the pointer is gone: the shell only reports entry.
                    tray.MouseMove += (o, e) =>
                    {
                        if (form.IsDisposed) return;
                        Point at = Cursor.Position;
                        // The icon's own rectangle is not exposed, so the hotspot
                        // grows around every point the shell reported.
                        Rectangle near = new Rectangle(at.X - 14, at.Y - 14, 28, 28);
                        hotspot = hotspot == Rectangle.Empty ? near : Rectangle.Union(hotspot, near);
                        try { panel.Show(form.PopupTitle(), form.PopupRows(), at); } catch { }
                        guard.Start();
                    };
                    hoverGuard.Tick += (o, e) =>
                    {
                        if (hotspot != Rectangle.Empty && hotspot.Contains(Cursor.Position)) return;
                        guard.Stop(); hotspot = Rectangle.Empty;
                        try { panel.Hide(); } catch { }
                    };
                    form.PopupOwnsTooltip = true;
                    // A click means the user is done reading; the window or the
                    // menu is about to cover the panel anyway.
                    tray.MouseDown += (o, e) =>
                    { guard.Stop(); hotspot = Rectangle.Empty; try { panel.Hide(); } catch { } };
                }
                catch { form.PopupOwnsTooltip = false; }

                // One left click opens the window: the tray icon is the app's
                // main entry point and a double click is not discoverable.
                // Right click still belongs to the context menu.
                tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) show(); };
                tray.DoubleClick += (o, e) => show();
                form.FormClosing += (o, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; form.HideToTray(); } };
                form.UpdateTray();
                ApplyAutostart(s);
                bool startHidden = Array.Exists(args, a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
                if (!startHidden) form.Show();
                Application.Run();
                if (hoverGuard != null) { hoverGuard.Stop(); hoverGuard.Dispose(); }
                if (popup != null) popup.Dispose();
                showWait.Unregister(null); showEvent.Dispose(); tray.Dispose();
            }
        }

        static void ApplyAutostart(LimisawSettings s)
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk == null) return;
                    if (s.AutoStart) rk.SetValue("LimisawApp", "\"" + Application.ExecutablePath + "\" --minimized");
                    else if (rk.GetValue("LimisawApp") != null) rk.DeleteValue("LimisawApp", false);
                }
            }
            catch { }
        }

        static ContextMenuStrip BuildMenu(LimisawSettings s, NotifyIcon tray, Func<LimisawForm> getForm,
            Action show, Action refresh, List<Theme> themes)
        {
            var m = new ContextMenuStrip();
            // The menu is painted in the active theme (MenuRenderer reads
            // Palette live), so opening it does not look like a different
            // application's window appeared over the tray.
            m.Renderer = new MenuRenderer();
            m.BackColor = Palette.SURFACE; m.ForeColor = Palette.TEXT;
            m.ShowImageMargin = false;
            m.Font = Pix.Get(11);
            m.Opening += (o, e) => { m.BackColor = Palette.SURFACE; m.ForeColor = Palette.TEXT; };
            m.Items.Add("Open LIMISAW", null, (o, e) => show());
            m.Items.Add("Refresh now", null, (o, e) => refresh());
            m.Items.Add(new ToolStripSeparator());
            // Status rows are rebuilt on every open: the account list is
            // discovered, so its length is not known at build time.
            var statusRows = new ToolStripMenuItem("No accounts yet") { Enabled = false };
            m.Items.Add(statusRows);
            m.Items.Add(new ToolStripSeparator());

            // Every entry below is a shortcut to a tab in the window, which is
            // where the same setting lives with room to explain itself. The menu
            // stays for one-click changes; ordering a list in a context menu is
            // exactly what the Tray tab exists to avoid.
            m.Items.Add("Tray items, order and count...", null, (o, e) => { show(); getForm().ShowTab(1); });
            m.Items.Add("All settings...", null, (o, e) => { show(); getForm().ShowTab(2); });
            m.Items.Add(new ToolStripSeparator());

            var metricMenu = new ToolStripMenuItem("Tray number");
            m.Items.Add(metricMenu);

            var modeMenu = new ToolStripMenuItem("Tray layout");
            for (int i = 0; i < LimisawSettings.Modes.Length; i++)
            {
                string value = LimisawSettings.Modes[i];
                var item = new ToolStripMenuItem(LimisawSettings.ModeLabels[i], null, (o, e) =>
                {
                    s.TrayMode = value; s.Save();
                    foreach (ToolStripItem child in modeMenu.DropDownItems)
                    {
                        var c = child as ToolStripMenuItem;
                        if (c != null) c.Checked = (string)c.Tag == s.TrayMode;
                    }
                    getForm().UpdateTray();
                }) { Tag = value, Checked = s.TrayMode == value };
                modeMenu.DropDownItems.Add(item);
            }
            m.Items.Add(modeMenu);
            var fillMenu = new ToolStripMenuItem("Tray fill steps");
            for (int i = 0; i < LimisawSettings.Fills.Length; i++)
            {
                int value = LimisawSettings.Fills[i];
                var item = new ToolStripMenuItem(LimisawSettings.FillLabels[i], null, (o, e) =>
                {
                    s.TrayFill = value; s.Save();
                    foreach (ToolStripItem child in fillMenu.DropDownItems)
                    {
                        var c = child as ToolStripMenuItem;
                        if (c != null) c.Checked = (int)c.Tag == s.TrayFill;
                    }
                    getForm().UpdateTray();
                }) { Tag = value, Checked = s.TrayFill == value };
                fillMenu.DropDownItems.Add(item);
            }
            m.Items.Add(fillMenu);

            var themeMenu = new ToolStripMenuItem("Theme");
            foreach (Theme t in themes)
            {
                Theme choice = t;
                var item = new ToolStripMenuItem(t.Label, null, (o, e) =>
                {
                    s.ThemeSlug = choice.Slug; s.Save();
                    foreach (ToolStripItem child in themeMenu.DropDownItems)
                    {
                        var c = child as ToolStripMenuItem;
                        if (c != null) c.Checked = (string)c.Tag == s.ThemeSlug;
                    }
                    getForm().ApplyTheme(choice.Slug);
                    getForm().Refresh(); getForm().UpdateTray();
                }) { Tag = t.Slug, Checked = string.Equals(t.Slug, s.ThemeSlug, StringComparison.OrdinalIgnoreCase) };
                themeMenu.DropDownItems.Add(item);
            }
            m.Items.Add(themeMenu);

            var installMenu = new ToolStripMenuItem("Install CLI");
            m.Items.Add(installMenu);

            m.Items.Add(new ToolStripSeparator());
            var usedItem = new ToolStripMenuItem(s.ShowUsed ? "Show Used %" : "Show Left %") { Checked = s.ShowUsed };
            usedItem.Click += (o, e) =>
            {
                getForm().ToggleShowUsed();
                usedItem.Checked = s.ShowUsed;
                usedItem.Text = s.ShowUsed ? "Show Used %" : "Show Left %";
            };
            m.Items.Add(usedItem);
            var notifyItem = new ToolStripMenuItem("Notify on reset") { Checked = s.NotifyOnReset };
            notifyItem.Click += (o, e) => { notifyItem.Checked = !notifyItem.Checked; s.NotifyOnReset = notifyItem.Checked; s.Save(); };
            m.Items.Add(notifyItem);
            // The two sounds next to the balloon: each alert is a switch here and
            // a sound here, so muting one does not mute the other.
            var chimeItem = new ToolStripMenuItem("Chime on reset") { Checked = s.ResetSound };
            chimeItem.Click += (o, e) => { chimeItem.Checked = !chimeItem.Checked; s.ResetSound = chimeItem.Checked; s.Save(); };
            m.Items.Add(chimeItem);
            var lowItem = new ToolStripMenuItem("Alert when quota is low") { Checked = s.NotifyLow };
            lowItem.Click += (o, e) => { lowItem.Checked = !lowItem.Checked; s.NotifyLow = lowItem.Checked; s.Save(); };
            m.Items.Add(lowItem);
            var autoItem = new ToolStripMenuItem("Start with Windows") { Checked = s.AutoStart };
            autoItem.Click += (o, e) => { autoItem.Checked = !autoItem.Checked; s.AutoStart = autoItem.Checked; s.Save(); ApplyAutostart(s); };
            m.Items.Add(autoItem);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Exit", null, (o, e) => { var f = getForm(); f.Close(); try { tray.Visible = false; } catch { } Application.Exit(); });

            m.Opening += (o, e) =>
            {
                LimisawForm f = getForm();
                // Accounts, metrics and CLIs are all discovered at runtime, so
                // these three submenus are rebuilt from the live snapshot.
                statusRows.DropDownItems.Clear();
                int count = f.AccountCount;
                statusRows.Text = count == 0 ? "No accounts yet" : count + " account(s)";
                statusRows.Enabled = count > 0;
                for (int i = 0; i < count; i++)
                {
                    string line = f.AccountSummary(i, s.ShowUsed);
                    if (line != null) statusRows.DropDownItems.Add(new ToolStripMenuItem(line) { Enabled = false });
                }

                metricMenu.DropDownItems.Clear();
                var lowest = new ToolStripMenuItem("Lowest remaining (recommended)", null, (o2, e2) =>
                { s.TrayMetric = "lowest"; s.Save(); f.ApplyChoice(); })
                { Tag = "lowest", Checked = s.TrayMetric == "lowest" };
                metricMenu.DropDownItems.Add(lowest);
                foreach (Metric metric in f.AllMetrics())
                {
                    Metric pick = metric;
                    metricMenu.DropDownItems.Add(new ToolStripMenuItem(metric.Label, null, (o2, e2) =>
                    { s.TrayMetric = pick.Id; s.Save(); f.ApplyChoice(); })
                    { Tag = metric.Id, Checked = s.TrayMetric == metric.Id });
                }

                installMenu.DropDownItems.Clear();
                foreach (CliInfo cli in f.CliList)
                {
                    CliInfo pick = cli;
                    installMenu.DropDownItems.Add(new ToolStripMenuItem(
                        cli.Label + (cli.Installed ? " (installed)" : " — install"), null,
                        (o2, e2) => { show(); f.ShowTab(3); f.RunInstall(pick); }));
                }
                if (installMenu.DropDownItems.Count == 0)
                    installMenu.DropDownItems.Add(new ToolStripMenuItem("Not probed yet") { Enabled = false });

                usedItem.Checked = s.ShowUsed;
                usedItem.Text = s.ShowUsed ? "Show Used %" : "Show Left %";
            };
            return m;
        }
    }
}
