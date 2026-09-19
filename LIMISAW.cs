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
        // The honest OS version. Environment.OSVersion is shimmed by Windows to
        // report 6.2.9200 (the Windows 8 lie) for any process whose manifest does
        // not declare supportedOS GUIDs — and this build carries no manifest.
        // RtlGetVersion returns the real kernel version, which is what the
        // diagnostics report must print.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }
        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int RtlGetVersion(ref OSVERSIONINFOEX versionInfo);
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
        //
        // A file that fails to parse is NOT silent (T-010): LoadErrors collects
        // the reason, and the Settings tab shows it next to the theme grid, so a
        // typo in a user palette is a message rather than a theme that quietly
        // never appears. The good ones still load.
        public static List<Theme> Load(string root)
        {
            var list = new List<Theme> { new Theme() };
            var bySlug = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (KeyValuePair<string, string> pair in Assets.ThemeFiles())
                Add(list, bySlug, pair.Value, pair.Key, errors, null);
            string dir = Path.Combine(root ?? "", "Themes");
            if (Directory.Exists(dir))
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    string text;
                    try { text = File.ReadAllText(file); }
                    catch (Exception ex)
                    { errors.Add(Path.GetFileName(file) + ": " + ex.GetType().Name); continue; }
                    Add(list, bySlug, text, Path.GetFileNameWithoutExtension(file), errors, file);
                }
            list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order)
                : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            LoadErrors = errors;
            return list;
        }

        // Why the last Load dropped a file. Empty = everything parsed. Read by
        // the Settings tab; never cleared except by the next Load.
        public static List<string> LoadErrors = new List<string>();

        static void Add(List<Theme> list, Dictionary<string, Theme> bySlug,
                        string json, string fallbackSlug, List<string> errors, string path)
        {
            var ser = new JavaScriptSerializer();
            try
            {
                var doc = ser.Deserialize<Dictionary<string, object>>(json);
                var tok = doc.ContainsKey("tokens") ? doc["tokens"] as Dictionary<string, object> : null;
                if (tok == null)
                {
                    if (path != null) errors.Add(Path.GetFileName(path) + ": no tokens block");
                    return;
                }
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
            catch (Exception ex)
            {
                if (path != null) errors.Add(Path.GetFileName(path) + ": " + ex.GetType().Name);
            }
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

    // W2-006/R020: the structured outcome of one persistence attempt. `Saved`
    // is the durable fact (the ini now holds this snapshot); `Dirty` is the
    // conflict fact (the live settings differ from the last snapshot the disk
    // provably accepted). They are DIFFERENT questions: a failed save leaves
    // the user's choice live and the file stale (Saved=false, Dirty=true), and
    // an incidental position write can land while an older settings conflict
    // is still open (Saved=true, Dirty=true). `Reason` is a short diagnostic
    // for UI/tests, never an exception string.
    class SettingsSaveResult
    {
        public bool Saved;
        public bool Dirty;
        public string Reason;
    }

    // R020: the structured outcome of one reload attempt. The three ways a
    // reload can legitimately end: a coherent candidate was accepted (Changed
    // says whether the live object moved), a dirty live choice was kept because
    // the disk is still the same stale snapshot that preceded the failed save
    // (DirtyRetained — the documented "edit the ini, press Refresh" recovery
    // only exists when the file actually changed), or the file could not be
    // read at all (Unreadable). `Note` is what the UI may show; null means say
    // nothing, so a refused or unchanged reload can never look like a reload.
    class SettingsReloadResult
    {
        public bool Accepted, Changed, Recovered, DirtyRetained, Unreadable, Invalid;
        public string Note;
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
        // 8 = eighths, 100 = exact. Legacy 2/4 normalize on load, never here.
        public int TrayFill = 8;
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
        // CORE-005: NotifyLow is the BALLOON switch, LowSound is the chime — the
        // same two as the refill alert. One combined flag meant the settings
        // model's own rule two comments up did not hold for the low alert: a
        // balloon with no chime was impossible, and a chime with no balloon was
        // unreachable.
        public bool NotifyLow = true;
        public bool LowSound = true;
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
        // FreeBuff is optional and its balance lives in a credential-bearing
        // local file (~/.config/manicode/credentials.json). Reading that token
        // is a permission the user grants here, off by default; it must reach
        // LIMISAW.ini BEFORE any credential read. Same durable-before-authority
        // contract as ZcodeReadConfig.
        public bool FreebuffReadConfig = false;
        // Account card order on the Accounts tab, '|' separated account keys.
        // Empty = discovery order, which is the vendor sweep order.
        public string AccountOrder = "";
        // Accounts the user hid from the Accounts tab, '|' separated keys.
        // Display-only: the sweep keeps probing a hidden account, so it can
        // reappear the moment it is unhidden — the same contract the tray's
        // hide list has.
        public string HiddenAccounts = "";
        // Display filters for the Accounts tab, both display-only. Spent hides
        // accounts with no window showing quota left; FiveHourOnly keeps only
        // accounts whose 5h window is actually usable. A filter must never
        // decide what is probed — only what is drawn.
        public bool HideSpentAccounts = false;
        public bool OnlyWithFiveHour = false;
        public bool AlwaysOnTop = false;
        // What the Settings preview pretends the quota is. A preview wired to the
        // live number can only show one picture, which is useless for choosing a
        // layout: the whole question is "what does 8% look like in this mode".
        public int PreviewPct = 65;
        public int WindowX = int.MinValue, WindowY = int.MinValue;
        // T-42: the user owns the window SIZE, so it is durable too. 0 = the ini
        // predates the resize wave (or was hand-edited away) and the startup
        // restore falls back to the preferred responsive default. A malformed
        // value leaves the live value standing (the ReadInt rule), and a
        // negative or below-minimum value is clamped at restore, never here —
        // startup must not silently rewrite the file it just read.
        public int WindowW = 0, WindowH = 0;

        // One definition object per tray layout, so the Settings row, the tray
        // context menu, the preview and the renderer all consume ONE truth.
        // Parallel arrays six modes deep were one missed index from lying.
        internal class TrayModeDefinition
        {
            public string Id, ShortLabel, LongLabel, Hint;
            public bool UsesNumberReadout;   // the "Tray shows Off/%/Time" row
            public bool UsesFillDetail;      // the Fill detail row
            public bool MultiReading;       // the Readings picker wording
        }
        internal static readonly TrayModeDefinition[] TrayModes = {
            new TrayModeDefinition { Id = "single", ShortLabel = "Number", LongLabel = "Single number",
                Hint = "one number: the reading you pick, biggest and clearest",
                UsesNumberReadout = true, UsesFillDetail = false, MultiReading = false },
            new TrayModeDefinition { Id = "dual", ShortLabel = "Two", LongLabel = "Two numbers (short / long)",
                Hint = "two numbers: worst short window over worst long one",
                UsesNumberReadout = true, UsesFillDetail = false, MultiReading = true },
            new TrayModeDefinition { Id = "gauge", ShortLabel = "Gauge", LongLabel = "Gauge of the picked reading",
                Hint = "one thin horizontal gauge of the picked reading, no digits",
                UsesNumberReadout = false, UsesFillDetail = true, MultiReading = false },
            new TrayModeDefinition { Id = "bars", ShortLabel = "Bars", LongLabel = "Narrow vertical column per reading",
                Hint = "narrow vertical column per reading, no digits",
                UsesNumberReadout = false, UsesFillDetail = true, MultiReading = true },
            new TrayModeDefinition { Id = "rows", ShortLabel = "Rows", LongLabel = "Horizontal mini-bar per reading",
                Hint = "one horizontal mini-bar per reading, top to bottom",
                UsesNumberReadout = false, UsesFillDetail = true, MultiReading = true },
            new TrayModeDefinition { Id = "grid", ShortLabel = "Cells", LongLabel = "Block per reading in a grid",
                Hint = "block per reading in a grid, filled from the opposite edge",
                UsesNumberReadout = false, UsesFillDetail = true, MultiReading = true },
        };
        internal static TrayModeDefinition ModeDef(string id)
        {
            foreach (TrayModeDefinition d in TrayModes) if (d.Id == id) return d;
            return TrayModes[0];
        }

        internal class TrayFillDefinition
        {
            public int Value; public string ShortLabel, LongLabel, Hint;
        }
        // Fill granularity the UI actually offers: 1/8 and Exact. The old 1/2
        // and 1/4 steps read "instantly" but carried almost no information at
        // 16px, so they were dropped; legacy ini values 2 and 4 load as 8 (see
        // Read) without rewriting the file.
        internal static readonly TrayFillDefinition[] TrayFills = {
            new TrayFillDefinition { Value = 8, ShortLabel = "1/8", LongLabel = "Eighths (1/8)",
                Hint = "eighths: eight steps, still countable at 16px" },
            new TrayFillDefinition { Value = 100, ShortLabel = "Exact", LongLabel = "Exact (per pixel)",
                Hint = "exact: fills by the pixel, most detail, hardest to read fast" },
        };
        public const int MaxTrayItems = 9;

        public LimisawSettings(string dir) { Dir = dir; IniPath = Path.Combine(dir, "LIMISAW.ini"); }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileString(string app, string key, string def, System.Text.StringBuilder buf, int size, string file);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool WritePrivateProfileString(string app, string key, string val, string file);

        // W2-006: a fixed buffer silently truncates. GetPrivateProfileString
        // copies what fits and reports nSize-1 when it ran out of room, with no
        // error — so an ordered list long enough to fill it came back cut, often
        // mid-id, and the order the user arranged was quietly wrong after a
        // restart. Grow until the value fits: the read is the only place that
        // knows how long the value actually is.
        internal int ReadGrowths;
        string Read(string key, string def)
        {
            int size = 2048;
            while (true)
            {
                var sb = new System.Text.StringBuilder(size);
                int n = GetPrivateProfileString("limisaw", key, def, sb, sb.Capacity, IniPath);
                // n == size-1 is the API's only truncation signal; a value that
                // happens to be exactly that long costs one extra read and
                // returns identical text, which is why the test asserts the
                // TEXT, not the growth count.
                if (n < size - 1 || size >= MaxIniValueChars) return sb.ToString();
                size *= 2;
                ReadGrowths++;
            }
        }

        // 256 Ki chars is half a megabyte of ids — orders of magnitude past any
        // real ordering, and a hard stop so a pathological ini cannot spin the
        // loop forever.
        const int MaxIniValueChars = 256 * 1024;

        // CORE-004: record owned keys the file got wrong, so ReloadEx can
        // refuse any candidate that carries a malformed typed value. Startup
        // Load clears it first; a fresh process that reads malformed values
        // uses defaults (its class-initializer values), which is the correct
        // restart behavior. The live object keeps its last good value per the
        // documented no-clamp rule.
        internal List<string> MalformedKeys = new List<string>();

        // CORE-007: booleans need the same typed boundary ReadInt already gives
        // integers. `Read(key) == "1"` turned a malformed value ("yes", "on",
        // garbage) into FALSE - a typo in LIMISAW.ini silently silenced every
        // alert the line belonged to. Missing, "0" and "1" are the only shapes
        // with meaning; anything else leaves the LIVE value standing, exactly
        // the rule ReadInt has.
        bool ReadBool(string key, bool def, bool current)
        {
            const string missing = "\u0001limisaw-absent\u0001";
            string raw = Read(key, missing);
            if (raw == missing) return def;
            if (raw == "0") return false;
            if (raw == "1") return true;
            // CORE-004: record the malformed key so ReloadEx can refuse the
            // candidate instead of silently adopting the live value.
            MalformedKeys.Add(key);
            return current;
        }

        // The write seam. Production is WritePrivateProfileString; a test
        // substitutes a writer that fails on a CHOSEN key, because a disk that
        // fails on the twentieth of twenty-four writes cannot be arranged on
        // demand — and that is exactly the case W2-004 reported as success.
        // `file` is wherever Save() is staging this save: the live ini for a
        // caller that writes in place, or the temp file for the staged one.
        internal Func<string, string, string, bool> WriteHook = null;

        bool Write(string key, string val, string file)
        {
            if (WriteHook != null) return WriteHook(key, val, file);
            return WritePrivateProfileString("limisaw", key, val, file);
        }

        bool Write(string key, string val) { return Write(key, val, IniPath); }

        // A number the file got wrong must not poison the settings object.
        // TryParse writes 0 on failure, so an external `RefreshSeconds=abc` used
        // to clamp a working 900 down to the floor; a value that does not parse
        // now leaves the live one standing. A key that is ABSENT is different and
        // still means the documented default — the file is the truth, so deleting
        // a line resets that setting rather than freezing it.
        int ReadInt(string key, string def, int current, int lo, int hi)
        {
            int parsed;
            if (!int.TryParse(Read(key, def), out parsed))
            {
                // CORE-004: record the malformed key so ReloadEx can refuse
                // the candidate instead of silently adopting the live value.
                MalformedKeys.Add(key);
                return current;
            }
            return Math.Max(lo, Math.Min(hi, parsed));
        }

        // Last write outcome, so a caller can tell the user WHY their settings
        // will not survive a restart. One bool, not a list of failures: the ini
        // is one file, and reporting 24 errors about one read-only file is noise.
        // W2-006/R020: this is now COMPATIBILITY state derived from the last
        // structured save — the authoritative contract lives in SaveSettings()'s
        // SettingsSaveResult plus Dirty/DurableFingerprint below. It is cleared
        // by the next save attempt where every key landed, and by an accepted
        // reload (the disk snapshot it accepts is durable by definition).
        public bool LastSaveFailed;

        // R020: the last coherent DURABLE snapshot — what the disk provably
        // accepted last (startup load, a successful save, or an accepted
        // external reload). Kept as a full clone, not just a fingerprint,
        // because the incidental position save must be able to write "the
        // durable snapshot + the new position" WITHOUT promoting the live
        // choices that are still mid-conflict. DurableFingerprint is its
        // fingerprint; Dirty answers "do the live settings differ from it",
        // which is a DIFFERENT question from "did the last write fail": a
        // failed theme save leaves Dirty true even after a later incidental
        // position write succeeds, because the theme never reached the disk.
        LimisawSettings DurableSnapshot;
        string DurableFingerprint;
        public bool Dirty { get; private set; }
        public string LastSaveError { get; private set; }

        // CORE-001 (audit/6): the exact LIMISAW.ini BYTES this process last
        // accepted. DurableFingerprint above is SEMANTIC identity — "do the
        // live choices differ from the durable snapshot" — and can never
        // answer "did the disk change since we last looked". This one does:
        // a save may not overwrite bytes that arrived after the last accepted
        // Load/Reload/save, because an external editor's Theme would be
        // silently destroyed by the next resize. Advanced only when a disk
        // snapshot is ACCEPTED (startup Load, accepted ReloadEx, a successful
        // atomic save, an accepted creation of a previously-absent file) —
        // never by merely observing that the file differs.
        string AcceptedDiskFingerprint;
        bool AcceptedDiskAbsent;
        // CORE-003 (audit/7): authority is EXPLICIT. A fingerprint and an
        // absent flag could not tell "we proved this revision" from "we could
        // not read a file that exists" — both left `Absent == false` with a
        // null fingerprint, and Stage read that ambiguity as permission to
        // overwrite an existing file whose bytes this process never accepted.
        // Authority is now granted ONLY by a coherent acceptance: a stable
        // startup parse, an accepted ReloadEx, or a committed atomic Stage.
        bool AcceptedDiskAuthority;

        // The three explicit states, and the ONLY writers of them. An
        // unreadable / moving existing file is none of these: it retains
        // NO AUTHORITY, and Stage refuses to overwrite it until a coherent
        // acceptance (ReloadEx) grants authority.
        void AcceptDiskPresent(string fingerprint)
        {
            AcceptedDiskAuthority = true;
            AcceptedDiskFingerprint = fingerprint;
            AcceptedDiskAbsent = false;
        }
        void AcceptDiskAbsentState()
        {
            AcceptedDiskAuthority = true;
            AcceptedDiskFingerprint = null;
            AcceptedDiskAbsent = true;
        }
        void ClearDiskAuthority()
        {
            AcceptedDiskAuthority = false;
            AcceptedDiskFingerprint = null;
            AcceptedDiskAbsent = false;
        }

        // Is the live file still the exact revision this process last
        // accepted? Three outcomes: Match, Differ (an external edit happened
        // and no save may overwrite it), and Unreadable (cannot know, so the
        // safe answer is Differ — the caller refuses the write).
        internal enum DiskRevisionCheck { Match, Differ, Unreadable }

        internal DiskRevisionCheck DiskRevisionMatches()
        {
            // CORE-003: without an accepted revision there is nothing to
            // compare against, so the only safe answer is Differ — the caller
            // refuses the write. Readability NOW does not prove the process
            // ever accepted the revision its live state came from.
            if (!AcceptedDiskAuthority) return DiskRevisionCheck.Differ;
            if (File.Exists(IniPath))
            {
                if (AcceptedDiskAbsent) return DiskRevisionCheck.Differ;
                try
                {
                    return FileFingerprint(IniPath) == AcceptedDiskFingerprint
                        ? DiskRevisionCheck.Match : DiskRevisionCheck.Differ;
                }
                catch { return DiskRevisionCheck.Unreadable; }
            }
            return AcceptedDiskAbsent ? DiskRevisionCheck.Match : DiskRevisionCheck.Differ;
        }

        // W2-002: the test-only pre-commit seam. Production null. The harness
        // writes a coherent external ini here — after staging is complete but
        // before the source fingerprint is re-checked — to prove the save
        // refuses to overwrite a concurrent editor.
        internal Action BeforeSettingsCommit = null;

        // CORE-003: the test-only startup-parse seam. Production null. It runs
        // INSIDE Load's proven-readable window, after the before-fingerprint
        // and before the candidate parse, so a harness can deterministically
        // mutate the file mid-parse and prove the resulting hybrid revision is
        // never accepted. Narrow, internal, and inert in production.
        internal Action BeforeStartupParse = null;

        // W2-004: the save used to check only the FIRST write and then report
        // success, so a disk that filled up, a file locked halfway through, or any
        // single failing key left a mixture of old and new settings while the app
        // said everything was fine — and on restart resurrected values the user
        // had already seen replaced. Every write is checked now, and one failure
        // fails the whole save.
        //
        // CORE-006: the save is TRANSACTIONAL. Writing each key straight into
        // the live ini before knowing the others would land leaves a mixed
        // snapshot that never existed in memory whenever any key fails — the
        // user watched "oled + new order" while the file held "nord + old
        // order" and then the app resurrected the mixture on restart. So the
        // whole snapshot is staged into a temp file IN THE SAME DIRECTORY,
        // and only a save where EVERY owned key landed atomically replaces
        // the live file. A failure leaves the previous snapshot untouched.
        //
        // The stage is SEEDED from the live ini: a successful save rewrites
        // only the keys this build owns, so unknown keys, foreign sections
        // and settings written by a newer version or another tool survive
        // instead of being silently discarded. And a temp abandoned by an
        // interrupted earlier save is reconciled away first, so staging can
        // never begin from whatever a dead process left behind.
        //
        // R020: the values come from `src`, not always from `this`. The
        // explicit settings save stages the live object; the incidental
        // position save stages the durable baseline with only the new position
        // on top, so a window move can never quietly persist (and thereby
        // "resolve") a settings choice whose own save failed.
        SettingsSaveResult Stage(LimisawSettings src, bool explicitSave)
        {
            string dir;
            try { dir = Path.GetDirectoryName(IniPath); } catch { dir = null; }
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string tmp = Path.Combine(dir, Path.GetFileName(IniPath) + ".tmp");
            string why = "";

            // CORE-001 CAS #1: refuse the whole write before ANY owned key is
            // staged when the live file is no longer the revision this process
            // last accepted. The W2-002 check below only sees an editor that
            // writes DURING staging; this one catches the far more common
            // window — an edit after the last accepted Load/Reload and before
            // the save began (a resize is the classic trigger). Refusing keeps
            // the external bytes, unknown keys and foreign sections exactly as
            // the editor left them.
            //
            // The one exception is the explicit canonical rewrite (CORE-004):
            // a reload that found MALFORMED owned values was refused, so the
            // file cannot be accepted — but the user was told exactly what is
            // wrong, and their explicit save is consent to replace that KNOWN
            // revision's owned keys. The incidental geometry save never
            // qualifies: a window move is not consent to rewrite the file.
            string preFp = null;
            bool preAbsent = !File.Exists(IniPath);
            if (!preAbsent)
            {
                try { preFp = FileFingerprint(IniPath); }
                catch { return RefusedResult(src, "the live ini could not be read for the external-edit check"); }
            }
            // CORE-003: overwrite authority is EXPLICIT. Without a coherently
            // accepted revision the only legitimate write is CREATING an
            // absent file (a fresh object / first run). An EXISTING file whose
            // bytes were never accepted may not be overwritten merely because
            // it has become readable — the live state did not come from those
            // bytes, and writing would destroy them. With authority, the
            // original accepted-revision comparison stands.
            bool differ;
            if (!AcceptedDiskAuthority)
                differ = !preAbsent;
            else
                differ = preAbsent != AcceptedDiskAbsent
                    || (!preAbsent && preFp != AcceptedDiskFingerprint);
            bool invalidRewrite = explicitSave
                && !preAbsent
                && preFp == invalidObservedDiskFingerprint;
            if (differ && !invalidRewrite)
                return RefusedResult(src, !AcceptedDiskAuthority
                    ? "LIMISAW.ini was not coherently read; reload it before saving"
                    : "LIMISAW.ini changed externally; reload it before saving");

            // W2-002: the identity of the exact live snapshot this transaction
            // was seeded from, captured while copying it into staging.
            string sourceFingerprint = null;
            bool sourceAbsent = false;

            bool ok = true;
            // A temp the live ini's attributes carried into (a read-only ini
            // makes File.Copy stamp the temp read-only too) or a tool marked
            // read-only is reconciled by clearing the attributes, not by
            // failing the save: the temp is OURS, and a later save must not
            // stay broken because an earlier one left a stubborn temp.
            try
            {
                if (File.Exists(tmp))
                {
                    File.SetAttributes(tmp, FileAttributes.Normal);
                    File.Delete(tmp);
                }
            }
            catch { ok = false; why = "the staging file could not be reconciled"; }
            if (ok)
            {
                try
                {
                    if (File.Exists(IniPath))
                    {
                        File.Copy(IniPath, tmp, false);
                        // The bytes actually staged, not a later re-read: this
                        // is what the commit check compares the live file to.
                        sourceFingerprint = FileFingerprint(tmp);
                    }
                    else sourceAbsent = true;
                }
                catch { ok = false; why = "the staging copy could not be created"; }
            }
            if (ok)
            {
                // The copy carried the live file's attributes again — a
                // read-only ini must not make the staged temp undeletable on
                // the failure path or unwritable for the owned keys.
                try { if (File.Exists(tmp)) File.SetAttributes(tmp, FileAttributes.Normal); }
                catch { ok = false; why = "the staging copy could not be made writable"; }
            }

            if (ok)
            {
                ok &= Write("RefreshSeconds", src.RefreshSeconds.ToString(), tmp);
                ok &= Write("TrayMetric", src.TrayMetric, tmp);
                ok &= Write("TrayMode", src.TrayMode, tmp);
                ok &= Write("TrayShow", src.TrayShow, tmp);
                ok &= Write("TrayFill", src.TrayFill.ToString(), tmp);
                ok &= Write("TrayMax", src.TrayMax.ToString(), tmp);
                ok &= Write("TrayItems", src.TrayItems, tmp);
                ok &= Write("TrayHidden", src.TrayHidden, tmp);
                ok &= Write("Theme", src.ThemeSlug, tmp);
                ok &= Write("NotifyOnReset", src.NotifyOnReset ? "1" : "0", tmp);
                ok &= Write("ResetSound", src.ResetSound ? "1" : "0", tmp);
                ok &= Write("ResetSoundFile", src.ResetSoundFile, tmp);
                ok &= Write("NotifyLow", src.NotifyLow ? "1" : "0", tmp);
                ok &= Write("LowSound", src.LowSound ? "1" : "0", tmp);
                ok &= Write("LowPct", src.LowPct.ToString(), tmp);
                ok &= Write("LowSoundFile", src.LowSoundFile, tmp);
                ok &= Write("SoundVolume", src.SoundVolume.ToString(), tmp);
                ok &= Write("SoundDir", src.SoundDir, tmp);
                ok &= Write("AutoStart", src.AutoStart ? "1" : "0", tmp);
                ok &= Write("ShowUsed", src.ShowUsed ? "1" : "0", tmp);
                ok &= Write("ZcodeReadConfig", src.ZcodeReadConfig ? "1" : "0", tmp);
                ok &= Write("FreebuffReadConfig", src.FreebuffReadConfig ? "1" : "0", tmp);
                ok &= Write("AccountOrder", src.AccountOrder, tmp);
                ok &= Write("HiddenAccounts", src.HiddenAccounts, tmp);
                ok &= Write("HideSpentAccounts", src.HideSpentAccounts ? "1" : "0", tmp);
                ok &= Write("OnlyWithFiveHour", src.OnlyWithFiveHour ? "1" : "0", tmp);
                ok &= Write("AlwaysOnTop", src.AlwaysOnTop ? "1" : "0", tmp);
                ok &= Write("PreviewPct", src.PreviewPct.ToString(), tmp);
                ok &= Write("WindowX", src.WindowX.ToString(), tmp);
                ok &= Write("WindowY", src.WindowY.ToString(), tmp);
                ok &= Write("WindowW", src.WindowW.ToString(), tmp);
                ok &= Write("WindowH", src.WindowH.ToString(), tmp);
                if (!ok && why.Length == 0) why = "an ini key could not be written";
            }

            // CORE-003: the exact bytes this transaction is about to install.
            // Captured from the fully-written staged temp, never a post-commit
            // reread — the committed staged bytes ARE the authoritative
            // revision, so a transient inability to reopen the just-committed
            // file cannot turn a successful save into ambiguous ownership.
            string stagedFingerprint = null;
            if (ok)
            {
                // W2-002: the commit is OPTIMISTIC. If the live ini no longer
                // matches the snapshot this transaction staged (or appeared or
                // vanished since), a concurrent editor won: refuse the replace
                // and leave their bytes exactly as they wrote them. The staged
                // temp is dropped by the failure path below.
                if (BeforeSettingsCommit != null) { try { BeforeSettingsCommit(); } catch { } }
                try { stagedFingerprint = FileFingerprint(tmp); }
                catch { ok = false; why = "the staged ini could not be fingerprinted"; }
                string currentFingerprint = null;
                bool currentAbsent = !File.Exists(IniPath);
                if (!currentAbsent)
                {
                    try { currentFingerprint = FileFingerprint(IniPath); }
                    catch { ok = false; why = "the live ini could not be read for the commit check"; }
                }
                if (ok && (currentAbsent != sourceAbsent
                    || (!sourceAbsent && currentFingerprint != sourceFingerprint)))
                { ok = false; why = "LIMISAW.ini changed while settings were being saved"; }

                if (ok)
                {
                    // Atomic: either the whole snapshot lands or the previous
                    // one stays. MoveFileEx(MOVEFILE_REPLACE_EXISTING) is the
                    // smallest replace that can never leave a partially-written
                    // ini.
                    bool moved = false;
                    try { moved = MoveFileEx(tmp, IniPath, MOVEFILE_REPLACE_EXISTING); }
                    catch { }
                    // A false return IS the failure: the last-error value after
                    // a refused move is not evidence the file landed, so it must
                    // never be read as success.
                    if (!moved) { ok = false; why = "the atomic replace of the ini failed"; }
                }
            }

            if (!ok)
            {
                // Any failure — staging, a key, the move — drops the staged
                // temp, so the live ini stays the only readable snapshot and
                // nothing half-written outlives the call. Attributes first:
                // the seeding copy may have stamped it read-only, and a
                // cleanup that throws would still leave the temp behind.
                try
                {
                    if (File.Exists(tmp))
                    {
                        File.SetAttributes(tmp, FileAttributes.Normal);
                        File.Delete(tmp);
                    }
                }
                catch { }
            }

            var r = new SettingsSaveResult();
            if (ok)
            {
                // Durable: the disk now holds `src`. The LIVE object may still
                // differ from it (only the incidental save stages something
                // other than the live object), so Dirty is recomputed against
                // the live fingerprint, not assumed away.
                AdoptBaseline(src);
                // CORE-003: the committed staged bytes are the transaction's
                // authoritative revision, installed DIRECTLY as ACCEPTED
                // PRESENT. No post-commit reread whose failure could be
                // ignored — and whatever invalid file the user canonicalized
                // away is gone.
                AcceptDiskPresent(stagedFingerprint);
                invalidObservedDiskFingerprint = null;
            }
            else
            {
                // The previous durable snapshot stands untouched; a failed
                // save never advances it. Dirty = the live choices are not
                // what the disk holds (with no baseline at all — a first save
                // that failed before anything was ever accepted — nothing is
                // durable, so the live state is dirty by definition).
                LastSaveError = why;
                Dirty = DurableFingerprint == null || Fingerprint() != DurableFingerprint;
            }
            LastSaveFailed = !ok;
            r.Saved = ok;
            r.Dirty = Dirty;
            r.Reason = ok ? "" : why;
            return r;
        }

        // A refused save before anything was staged: the durable snapshot and
        // the accepted disk revision stand untouched, the live choices stay
        // dirty against them, and the caller gets the stable reason.
        SettingsSaveResult RefusedResult(LimisawSettings src, string reason)
        {
            LastSaveError = reason;
            Dirty = DurableFingerprint == null || Fingerprint() != DurableFingerprint;
            LastSaveFailed = true;
            return new SettingsSaveResult { Saved = false, Dirty = Dirty, Reason = reason };
        }

        // The structured settings save: the caller's persistence DECISION. This
        // is what a user-triggered mutation uses, because its success or
        // failure is allowed to resolve the dirty state — it is a retry of the
        // whole snapshot by the user's own hand.
        public SettingsSaveResult SaveSettings() { return Stage(this, true); }

        // The incidental persistence — HideToTray, EndRightDrag, OpenIni's
        // create-if-missing, ResizeEnd. It exists to keep the window GEOMETRY
        // durable (position AND size — the user owns both), and it must NOT
        // silently decide a settings conflict the user has not retried: when
        // the live settings are dirty, it stages the DURABLE BASELINE with
        // only the fresh geometry on top, so the geometry still reaches the
        // disk while the unresolved choice (theme, threshold...) stays just as
        // unresolved. Without this, resizing the window during a save failure
        // would quietly write the failed choice and erase the conflict the
        // user was never asked about.
        public SettingsSaveResult SavePosition()
        {
            // CORE-001 + CORE-003: a move/resize/hide must NEVER destroy an
            // unaccepted external edit, and must never overwrite an EXISTING
            // file the process has no coherent accepted revision for. An
            // ABSENT file is still legitimate first-run creation, so the guard
            // only bites when a file exists. DiskRevisionMatches() already
            // answers Differ when there is no authority at all.
            if (File.Exists(IniPath) && DiskRevisionMatches() != DiskRevisionCheck.Match)
                return new SettingsSaveResult { Saved = false, Dirty = Dirty,
                    Reason = !AcceptedDiskAuthority
                        ? "LIMISAW.ini was not coherently read; the window position stays in memory"
                        : "LIMISAW.ini changed externally; the window position stays in memory" };

            if (!Dirty) return Stage(this, false);
            LimisawSettings src = DurableSnapshot != null ? DurableSnapshot.Clone() : Clone();
            src.WindowX = WindowX; src.WindowY = WindowY;
            src.WindowW = WindowW; src.WindowH = WindowH;
            return Stage(src, false);
        }

        // One geometry-save boundary lives on the FORM (SaveWindowGeometry);
        // the incidental settings-level contract is this method.

        // Compatibility overloads: the ~30 existing mutation sites and the
        // harnesses read these shapes. Every one of them routes through the
        // same transaction and the same structured bookkeeping.
        public void Save() { SaveSettings(); }

        // Did the settings the caller just changed actually reach the disk? A
        // side effect that outlives this process — the autostart registry value —
        // must not be applied on the strength of a save that failed, or the two
        // disagree until someone notices Windows starting an app the ini says is
        // off.
        public bool SaveApplied() { return SaveSettings().Saved; }

        // A field-for-field copy of the persisted state: the VALIDATED
        // SNAPSHOT representation (R020/A5). Cloning this object and calling
        // the real Load() on the clone parses the candidate with exactly the
        // production rules — missing key -> documented default, malformed
        // number/boolean -> the LIVE value standing — because the clone starts
        // from the live fields. One parser, no duplicated key list.
        internal LimisawSettings Clone()
        {
            var c = new LimisawSettings(Dir);
            c.CopyPersistedFrom(this);
            return c;
        }

        void CopyPersistedFrom(LimisawSettings o)
        {
            RefreshSeconds = o.RefreshSeconds; TrayMetric = o.TrayMetric;
            TrayMode = o.TrayMode; TrayShow = o.TrayShow;
            TrayFill = o.TrayFill; TrayMax = o.TrayMax;
            TrayItems = o.TrayItems; TrayHidden = o.TrayHidden;
            ThemeSlug = o.ThemeSlug;
            NotifyOnReset = o.NotifyOnReset; ResetSound = o.ResetSound;
            ResetSoundFile = o.ResetSoundFile;
            NotifyLow = o.NotifyLow; LowSound = o.LowSound;
            LowPct = o.LowPct; LowSoundFile = o.LowSoundFile;
            SoundVolume = o.SoundVolume; SoundDir = o.SoundDir;
            AutoStart = o.AutoStart; ShowUsed = o.ShowUsed;
            ZcodeReadConfig = o.ZcodeReadConfig;
            FreebuffReadConfig = o.FreebuffReadConfig;
            AccountOrder = o.AccountOrder; HiddenAccounts = o.HiddenAccounts;
            HideSpentAccounts = o.HideSpentAccounts; OnlyWithFiveHour = o.OnlyWithFiveHour;
            AlwaysOnTop = o.AlwaysOnTop; PreviewPct = o.PreviewPct;
            WindowX = o.WindowX; WindowY = o.WindowY;
            WindowW = o.WindowW; WindowH = o.WindowH;
        }

        // The last coherent snapshot is now `accepted`: startup load, a
        // successful save, or an accepted external reload. The conflict state
        // restarts from it — Dirty is recomputed against the LIVE object, so
        // the incidental path can advance the baseline (position) while the
        // settings conflict stays open.
        // CORE-004: the fingerprint of the last external revision that was
        // REFUSED because it carried a malformed owned value. null means no
        // such refusal is outstanding. The user's explicit SaveSettings is
        // consent to canonicalize exactly that revision away; any other
        // external edit still refuses.
        string invalidObservedDiskFingerprint;

        void AdoptBaseline(LimisawSettings accepted)
        {
            DurableSnapshot = accepted.Clone();
            DurableFingerprint = accepted.Fingerprint();
            LastSaveError = null;
            LastSaveFailed = false;
            Dirty = Fingerprint() != DurableFingerprint;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string src, string dst, int flags);
        const int MOVEFILE_REPLACE_EXISTING = 0x1;

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
        public List<string> HiddenAccountList() { return Split(HiddenAccounts); }
        public void SetHiddenAccounts(List<string> keys) { HiddenAccounts = string.Join("|", keys.ToArray()); }

        public void Load()
        {
            // CORE-003 (audit/7): startup must prove it parsed ONE stable,
            // readable disk revision before it grants overwrite authority.
            // GetPrivateProfileString cannot report "unreadable" — it answers
            // the DEFAULT for every key against a locked file — so an
            // existing ini that cannot be read must NOT become the durable
            // baseline. The candidate is parsed into a clone and copied into
            // the live object only after the before/after fingerprints agree.
            if (File.Exists(IniPath))
            {
                // 1. prove readability before parsing.
                try { using (File.Open(IniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { } }
                catch { ClearDiskAuthority(); return; }
                // 2. fingerprint the revision before the parse.
                string revisionBefore;
                try { revisionBefore = FileFingerprint(IniPath); }
                catch { ClearDiskAuthority(); return; }
                // 3. deterministic mid-parse mutation seam (production null).
                if (BeforeStartupParse != null) { try { BeforeStartupParse(); } catch { } }
                // 4. parse the candidate through the canonical production parser.
                LimisawSettings candidate = Clone();
                candidate.ParsePersisted();
                // 5. fingerprint again after the parse.
                string revisionAfter;
                try { revisionAfter = FileFingerprint(IniPath); }
                catch { ClearDiskAuthority(); return; }
                // 6. accept only a stable revision. A file that moved during the
                //    parse (or vanished) was never coherently observed.
                if (revisionAfter != revisionBefore) { ClearDiskAuthority(); return; }
                // 7. install the accepted candidate and grant ACCEPTED PRESENT.
                CopyPersistedFrom(candidate);
                MalformedKeys = candidate.MalformedKeys;
                // The parse ran on the candidate; carry its read-growth
                // instrumentation with it so the live object still reports the
                // work this Load actually performed.
                ReadGrowths = candidate.ReadGrowths;
                AdoptBaseline(this);
                AcceptDiskPresent(revisionAfter);
                return;
            }
            // An ABSENT ini is a coherent observation: defaults become the
            // durable baseline and the state is ACCEPTED ABSENT. First-run
            // creation stays legitimate.
            ParsePersisted();
            AdoptBaseline(this);
            AcceptDiskAbsentState();
        }

        // The persisted-key parser, shared by startup Load and ReloadEx's
        // candidate so their definition of the on-disk fields cannot drift.
        // It reads whatever IniPath holds into THIS object; the caller owns
        // revision stability and authority.
        void ParsePersisted()
        {
            // CORE-004: each parse reads ONE file revision — the malformed-key
            // record it produces describes exactly that parse.
            MalformedKeys.Clear();
            RefreshSeconds = ReadInt("RefreshSeconds", "300", RefreshSeconds, 60, 3600);
            TrayMetric = Read("TrayMetric", "lowest");
            if (string.IsNullOrEmpty(TrayMetric)) TrayMetric = "lowest";
            TrayMode = Read("TrayMode", "single");
            if (ModeDef(TrayMode).Id != TrayMode) TrayMode = "single";
            TrayShow = Read("TrayShow", "pct");
            if (TrayShow != "off" && TrayShow != "pct" && TrayShow != "time") TrayShow = "pct";
            TrayFill = ReadInt("TrayFill", "8", TrayFill, int.MinValue, int.MaxValue);
            // Legacy 2/4 profiles stay valid: they load as the 1/8 behaviour
            // they approximated. The ini itself is not rewritten on load; the
            // normalized value becomes durable at the next explicit save.
            if (TrayFill == 2 || TrayFill == 4) TrayFill = 8;
            if (Array.IndexOf(new[] { 8, 100 }, TrayFill) < 0) TrayFill = 8;
            TrayMax = ReadInt("TrayMax", "4", TrayMax, 1, MaxTrayItems);
            TrayItems = Read("TrayItems", "");
            TrayHidden = Read("TrayHidden", "");
            ThemeSlug = Read("Theme", "goldendefault");
            if (string.IsNullOrEmpty(ThemeSlug)) ThemeSlug = "goldendefault";
            NotifyOnReset = ReadBool("NotifyOnReset", true, NotifyOnReset);
            ResetSound = ReadBool("ResetSound", true, ResetSound);
            ResetSoundFile = Read("ResetSoundFile", "success_powerup.wav");
            NotifyLow = ReadBool("NotifyLow", true, NotifyLow);
            // CORE-005 migration: an ini written before the split has no
            // LowSound key, and its single NotifyLow meant balloon AND chime.
            // Defaulting the new key to the old one therefore preserves exactly
            // what that installation already did — including the muted case,
            // where NotifyLow=0 had silenced both halves.
            LowSound = ReadBool("LowSound", NotifyLow, LowSound);
            LowPct = ReadInt("LowPct", "20", LowPct, 5, 95);
            LowSoundFile = Read("LowSoundFile", "pop_cartoon_pop.wav");
            SoundVolume = ReadInt("SoundVolume", "5", SoundVolume, 0, 100);
            SoundDir = Read("SoundDir", "");
            AutoStart = ReadBool("AutoStart", false, AutoStart);
            ShowUsed = ReadBool("ShowUsed", false, ShowUsed);
            ZcodeReadConfig = ReadBool("ZcodeReadConfig", false, ZcodeReadConfig);
            FreebuffReadConfig = ReadBool("FreebuffReadConfig", false, FreebuffReadConfig);
            AccountOrder = Read("AccountOrder", "");
            HiddenAccounts = Read("HiddenAccounts", "");
            HideSpentAccounts = ReadBool("HideSpentAccounts", false, HideSpentAccounts);
            OnlyWithFiveHour = ReadBool("OnlyWithFiveHour", false, OnlyWithFiveHour);
            AlwaysOnTop = ReadBool("AlwaysOnTop", false, AlwaysOnTop);
            PreviewPct = ReadInt("PreviewPct", "65", PreviewPct, 0, 100);
            WindowX = ReadInt("WindowX", int.MinValue.ToString(), WindowX, int.MinValue, int.MaxValue);
            WindowY = ReadInt("WindowY", int.MinValue.ToString(), WindowY, int.MinValue, int.MaxValue);
            WindowW = ReadInt("WindowW", "0", WindowW, int.MinValue, int.MaxValue);
            WindowH = ReadInt("WindowH", "0", WindowH, int.MinValue, int.MaxValue);
        }

        // W2-003: editing LIMISAW.ini by hand is a documented configuration path
        // (README, and the "press Refresh after editing" line OpenIni prints), but
        // startup was the file's only reader, so those edits — including the
        // deliberately manual `ZcodeReadConfig=1` — did nothing until a restart.
        // Refresh now re-reads the file, and the caller re-applies whatever
        // runtime state the new values imply.
        //
        // W2-006/R020: the OLD rule — refuse every read while LastSaveFailed —
        // was a sticky veto, not a recovery model: a transient save failure
        // permanently disabled the documented "edit the ini, press Refresh"
        // recovery, while any unrelated later save silently cleared the bit.
        // The decision is now made against a VALIDATED CANDIDATE snapshot and
        // the durable baseline, in three cases:
        //
        //   NOT DIRTY — the disk is authoritative: a candidate that differs
        //     from the live object is applied and becomes the new baseline.
        //   DIRTY, DISK == BASELINE — the file is still the exact stale
        //     snapshot that preceded the failed save; nothing external
        //     happened, so the live dirty choice is kept and nothing is
        //     claimed.
        //   DIRTY, DISK != BASELINE — the file changed after the failed save,
        //     which is what "the user repaired the ini by hand" looks like:
        //     the coherent external snapshot is accepted whole (never merged
        //     key-by-key with the dirty object), the conflict clears, and it
        //     becomes the new baseline.
        //
        // Two deliberate refusals survive from the old reader: a value that
        // does not parse leaves the live one standing instead of clamping it
        // to a floor, and an unreadable file is not a reload (a locked ini
        // answers every key with its DEFAULT, which would silently reset the
        // whole settings object).
        public bool Reload() { return ReloadEx().Changed; }

        public SettingsReloadResult ReloadEx()
        {
            var r = new SettingsReloadResult();
            if (!File.Exists(IniPath)) return r;
            // GetPrivateProfileString cannot report "I could not read the file":
            // against a locked ini it hands back the DEFAULT for every key, which
            // Load would then accept as the user's new choices and silently reset
            // the whole settings object. So the file is opened first, and a read
            // we cannot perform is not a reload.
            try { using (File.Open(IniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { } }
            catch { r.Unreadable = true; return r; }

            // CORE-004 (audit/6): the reload must prove it parsed ONE stable
            // file revision. The candidate is parsed through many independent
            // GetPrivateProfileString calls; an editor writing between them
            // could otherwise produce a candidate that never existed as bytes.
            // The revision is fingerprinted before and after the parse; a
            // file that moved underneath is not reloaded — the next Refresh
            // will see whichever revision won and judge it then.
            string revisionBefore;
            try { revisionBefore = FileFingerprint(IniPath); }
            catch { r.Unreadable = true; return r; }

            // The candidate: the disk's values parsed by the production reader
            // with the LIVE settings standing behind every malformed value, so
            // accepting it can never do worse than the old in-place Load did.
            LimisawSettings candidate = Clone();
            candidate.ParsePersisted();
            string candFp = candidate.Fingerprint();

            string revisionAfter;
            try { revisionAfter = FileFingerprint(IniPath); }
            catch { r.Unreadable = true; return r; }
            if (revisionAfter != revisionBefore)
            {
                // A revision that changed mid-parse is not rejected as
                // invalid: it simply was never coherently observed. Try again
                // on the next Refresh; no state moves, nothing is marked.
                return r;
            }

            // CORE-004 (audit/6): if ANY owned typed key was malformed, the
            // candidate carried the live value for it instead of the disk's
            // intent. A fresh process would use the class default. The
            // candidate is therefore not a valid representation of the file
            // and must not be adopted as a durable baseline. The reload is
            // refused with the concise reason; the live state stands, the
            // baseline does not advance, and the user's explicit save (which
            // canonicalizes the KNOWN revision away) is the only way forward.
            // CORE-004 (audit/6): if ANY owned typed key was malformed, the
            // candidate carried the live value for it instead of the disk's
            // intent. A fresh process would use the class default. The
            // candidate is therefore not a valid representation of the file
            // and must not be adopted as a durable baseline. The reload is
            // refused with the concise reason; the live state stands, the
            // baseline does not advance, and the user's explicit save (which
            // canonicalizes the KNOWN revision away) is the only way forward.
            if (candidate.MalformedKeys.Count > 0)
            {
                invalidObservedDiskFingerprint = revisionAfter;
                r.Invalid = true;
                r.Note = "LIMISAW.ini has an invalid value — fix or save settings to rewrite it";
                return r;
            }

            if (candFp == Fingerprint())
            {
                // The disk agrees with the live object. The one interesting
                // sub-case: the live object is dirty and the file has since
                // been changed to match it — an external repair that landed
                // exactly on the user's choice. The FILE decided, which is the
                // explicit external acceptance the dirty conflict was waiting
                // for.
                if (Dirty && candFp != DurableFingerprint)
                {
                    AdoptBaseline(candidate);
                    AcceptDiskPresent(revisionAfter);
                    invalidObservedDiskFingerprint = null;
                    r.Accepted = true; r.Recovered = true;
                    r.Note = "LIMISAW.ini now matches the unsaved choice — conflict resolved";
                }
                return r;
            }

            if (Dirty && DurableFingerprint != null && candFp == DurableFingerprint)
            {
                // Same stale bytes as before the failed save: no external edit,
                // nothing to accept, the dirty choice outranks the file. Say
                // NOTHING — a refused reload must never wear a reload's clothes.
                r.DirtyRetained = true;
                return r;
            }

            bool wasDirty = Dirty;
            CopyPersistedFrom(candidate);
            AdoptBaseline(this);
            // CORE-001: an accepted external reload accepts the file's bytes
            // as the disk revision this process now stands behind.
            AcceptDiskPresent(revisionAfter);
            invalidObservedDiskFingerprint = null;
            r.Accepted = true; r.Changed = true; r.Recovered = wasDirty;
            r.Note = wasDirty
                ? "Repaired from the edited LIMISAW.ini — unsaved conflict resolved"
                : "Reloaded LIMISAW.ini";
            return r;
        }

        // W2-002: the identity of a file's BYTES, not its mtime or length — a
        // same-length edit inside the same timestamp tick is exactly the silent
        // lost update this guards against. SHA-256 of the content.
        internal static string FileFingerprint(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // Every persisted field in one string, so "did anything change" is one
        // comparison instead of twenty-four. Unit separator: no value may contain
        // it, so two different snapshots cannot collide.
        string Fingerprint()
        {
            return string.Join("\u001f", new[]
            {
                RefreshSeconds.ToString(), TrayMetric, TrayMode, TrayShow,
                TrayFill.ToString(), TrayMax.ToString(), TrayItems, TrayHidden,
                ThemeSlug, NotifyOnReset ? "1" : "0", ResetSound ? "1" : "0", ResetSoundFile,
                NotifyLow ? "1" : "0", LowSound ? "1" : "0", LowPct.ToString(), LowSoundFile,
                SoundVolume.ToString(), SoundDir, AutoStart ? "1" : "0", ShowUsed ? "1" : "0",
                ZcodeReadConfig ? "1" : "0", FreebuffReadConfig ? "1" : "0",
                AccountOrder, PreviewPct.ToString(),
                HiddenAccounts, HideSpentAccounts ? "1" : "0", OnlyWithFiveHour ? "1" : "0",
                AlwaysOnTop ? "1" : "0",
                WindowX.ToString(), WindowY.ToString(),
                WindowW.ToString(), WindowH.ToString(),
            });
        }
    }

    // One quota window as the probe resolved it. `Rem` is ALWAYS remaining
    // percent; the Used/Left switch flips only what is printed. `GatedBy` is
    // set when a longer window in the same pool is spent, which makes this
    // one unusable no matter what the vendor reports for it.
    class WindowData
    {
        public string Key = "", Base = "", Label = "", Group = "", GroupLabel = "", Reset, GatedBy;
        // CORE-013: the reset's absolute instant, carried through Flatten from
        // the probe. `Reset` is the local presentation of it and NEVER the
        // authority — quota state, cycle rollovers and notification identity
        // compare instants, not wall-clock renderings.
        public double? ResetEpoch;
        public bool Available, AssumedFull;
        public int Rem;
        public int DurationMinutes;
    }

    class AccountData
    {
        public string Provider = "", ProviderLabel = "", Name = "", Status = "", Plan, Error;
        // Stable account identity (CORE-001). For Codex this is a digest of the
        // exact canonical home, never the display name — two homes can both be
        // called "Codex", and a label is not authority for carry-forward,
        // notifications, tray persistence or an irreversible reset.
        public string SourceId = "";
        // Vendor-reported remote identity, hashed before it reaches the UI.
        // SourceId remains the local home key for routing and persistence.
        public string RemoteAccountIdentity = "";
        public bool RemoteIdentityChecked;
        // The exact canonical Codex home this account probes. The reset action
        // is routed by it so a banked credit is spent on the selected account.
        public string ResetHome = "";
        public bool Ok, Quiet;
        // Set when this account had no usable reading THIS sweep and the
        // previous sweep's windows were carried forward, so the card shows the
        // last known numbers (dimmed, with the reason) instead of going blank on
        // a transient CLI hiccup.
        public bool Carried; public string CarriedAt = "", CarriedNote = "";
        // Banked resets: a one-off credit that refills a spent window on demand.
        // Zero for every vendor that does not grant them. Not a window — there is
        // no percentage to draw — so it rides on the account, not in Windows.
        public int ResetCredits;
        public string ResetCreditTitle, ResetCreditExpires, ResetCreditId;
        // Absolute balances (FreeBucks). NOT windows: an amount with no
        // denominator rides beside them and is never a percentage, so it cannot
        // enter tray percentage modes or the low-quota alert.
        public List<BalanceData> Balances = new List<BalanceData>();
        // W2-003: the journal refusal's reset passed inside the grace — the
        // block ended, but nothing has measured the quota since. The reset
        // EVENT rides on this snapshot; the quota itself is unverified.
        public bool ResetUnverified;
        public List<WindowData> Windows = new List<WindowData>();
        public List<ReservePool> Reserves = new List<ReservePool>();
        public AccountAvailability Availability;
        // CORE-001: ANY provider that mints a stable source id is keyed by it.
        // This read `Provider == "codex"` while Codex was the only vendor with
        // more than one account; Claude homes are the same shape, and a label
        // ("Claude", "Claude") was never identity for either of them. A key
        // saved before this is still read through LegacyKey.
        public string Key { get { return Provider + "/" + (SourceId.Length > 0 ? SourceId : Name); } }

        // Pre-CORE-001 identity, for backward-compatible reads of saved
        // TrayItems/AccountOrder. Never written; only consulted so the tray and
        // cards survive an upgrade from the old single-"Codex" layout.
        public string LegacyKey { get { return Provider + "/" + Name; } }

        // "Did this sweep produce anything worth drawing?" A card with only
        // unavailable windows is just as blank as a card with none.
        public bool HasReading
        {
            get
            {
                foreach (WindowData w in Windows) if (w.Available) return true;
                // An absolute balance is a reading: a card with FreeBucks and
                // no window is not blank, so it must not be carried stale or
                // filtered out as spent.
                foreach (BalanceData b in Balances) if (b.Value.HasValue) return true;
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
        // W2-003: the block ended but nothing measured the quota since. The
        // balloon says "unverified", never a percentage.
        public bool Unverified;
    }

    // One selectable tray reading: "the lowest of everything", or one exact
    // account+window. Built from the live snapshot, so a new vendor account
    // appears in the menu without a code change.
    class Metric
    {
        public string Id = "", Label = "", Short = "";
        public int Value; public bool Available, IsShort;
        // The window's own reset: the countdown readout and the tooltip need
        // the same instant the alert logic keys on. CORE-013: the EPOCH is what
        // rides here, not a rendering of it.
        public double? ResetEpoch;
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

    // ── the tray's own bitmap alphabet ───────────────────────────────────────
    // A 3x5 pixel font for the only characters the tray icon ever draws. Verdana
    // rasterised through GDI at 5-8pt inside a 14x14 interior produced glyphs
    // that were not reliably readable — "34" could look like pixel debris — so
    // the tray numbers are now literal bitmaps: every lit pixel is plotted with
    // the exact palette colour at an integer scale, no font engine, no
    // antialiasing, no ClearType, no intermediate colours, and byte-identical
    // output on every machine. This belongs to the TRAY only; the window's UI
    // font is untouched.
    internal static class TrayGlyphs
    {
        // 3 wide x 5 tall, one string per row, '1' = lit.
        static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            { '0', new[] { "111", "101", "101", "101", "111" } },
            { '1', new[] { "010", "110", "010", "010", "111" } },
            { '2', new[] { "111", "001", "111", "100", "111" } },
            { '3', new[] { "111", "001", "111", "001", "111" } },
            { '4', new[] { "101", "101", "111", "001", "001" } },
            { '5', new[] { "111", "100", "111", "001", "111" } },
            { '6', new[] { "111", "100", "111", "101", "111" } },
            { '7', new[] { "111", "001", "010", "010", "010" } },
            { '8', new[] { "111", "101", "111", "101", "111" } },
            { '9', new[] { "111", "101", "111", "001", "111" } },
            { '-', new[] { "000", "000", "111", "000", "000" } },
            { '<', new[] { "001", "010", "100", "010", "001" } },
            { 'm', new[] { "101", "111", "101", "101", "101" } },
            { 'h', new[] { "100", "100", "111", "101", "101" } },
            { 'd', new[] { "001", "001", "111", "101", "111" } },
        };

        public const int GlyphW = 3, GlyphH = 5, Spacing = 1;

        public static bool Supported(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char ch in text) if (!Glyphs.ContainsKey(ch)) return false;
            return true;
        }

        // Rendered width of the whole string at a scale, including spacing.
        public static int Width(string text, int scale)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (text.Length * GlyphW + (text.Length - 1) * Spacing) * scale;
        }

        public static int Height(int scale) { return GlyphH * scale; }

        // The largest whole-pixel scale that fits the box, 0 when none does.
        public static int FitScale(string text, int boxW, int boxH)
        {
            if (!Supported(text)) return 0;
            for (int s = 4; s >= 1; s--)
                if (GlyphH * s <= boxH && Width(text, s) <= boxW) return s;
            return 0;
        }

        // Plot the string centred in the box. Only exact `color` pixels go down
        // — the caller owns the background, so nothing is ever blended.
        public static void Draw(Graphics g, string text, Rectangle box, Color color)
        {
            int scale = FitScale(text, box.Width, box.Height);
            if (scale <= 0) return;
            int w = Width(text, scale), h = GlyphH * scale;
            int x0 = box.X + (box.Width - w) / 2;
            int y0 = box.Y + (box.Height - h) / 2;
            using (var br = new SolidBrush(color))
            {
                int x = x0;
                foreach (char ch in text)
                {
                    string[] rows;
                    if (Glyphs.TryGetValue(ch, out rows))
                        for (int r = 0; r < GlyphH; r++)
                            for (int c = 0; c < GlyphW; c++)
                                if (rows[r][c] == '1')
                                    g.FillRectangle(br, x + c * scale, y0 + r * scale, scale, scale);
                    x += (GlyphW + Spacing) * scale;
                }
            }
        }
    }

    // What the single-number tray shows, resolved ONCE. The tooltip, the hover
    // panel title and the icon all read the same answer, so they can never pick
    // different readings, and a fallback is disclosed where the user looks.
    internal class TrayReading
    {
        public string Requested = "lowest";  // the saved pin, as saved
        public string MetricId = "";         // the reading that actually answered
        public string Label = "lowest";
        public int Value;
        public bool Available;
        // True when the pinned reading could not answer and another selected
        // reading did. The tooltip/popup carry `Note` so the fallback is
        // discoverable — 37% must never silently look like the pinned reading.
        public bool Fallback;
        public string Note = "";
        // The same fact in tooltip budget (the shell clamps the line at 63
        // characters): rides right behind the percentage.
        public string NoteShort = "";
        public double? ResetEpoch;
    }

    // One immutable snapshot of everything the tray renderers need. Building it
    // for the live tray and for the Settings preview replaces the old mutable
    // PreviewPct field: a render now receives its data instead of reading the
    // form's mutable state, so a preview can never leak into the real tray.
    internal class TrayModel
    {
        public List<Metric> All = new List<Metric>();    // every live reading
        public List<Metric> Items = new List<Metric>();  // selected, ordered, capped
        public string Pin = "lowest";
        public bool Stale;
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
    // ── audio: parser, cache, owner ──────────────────────────────────────────
    // SRC-004 R023/R029/R010/R022. Three layers, each narrow:
    //
    //   WavInfo      — a bounds-safe RIFF/WAVE reader. Scaling only ever
    //                  touches validated classic integer PCM (tag 1, 8/16/24/32
    //                  bits); anything else — IEEE float, A-law, mu-law,
    //                  WAVE_FORMAT_EXTENSIBLE, a malformed container — is
    //                  reported UNSUPPORTED and left byte-for-byte alone. An
    //                  unsupported file still PLAYS (the original is handed to
    //                  the player); it is just never transformed.
    //   the cache    — one artifact per canonical source path digest + 5%
    //                  quantised volume (the W2-007 identity), published
    //                  ATOMICALLY: the scaled bytes are written to a staging
    //                  file in the same directory and replace the final name
    //                  only when complete, so a failed replacement can never
    //                  damage the previous valid artifact. A cache hit is valid
    //                  only while the SOURCE still matches the length+mtime it
    //                  was built from, so an in-process edit of the user's WAV
    //                  rebuilds without anyone clearing the dictionary.
    //   the owner    — exactly one serialized worker thread owns the player and
    //                  all mutable cache state. Producers (UI preview, sweep
    //                  alerts) SUBMIT an immutable request and return
    //                  immediately: at most one cue runs, at most one waits,
    //                  and a newer pending request replaces an older one — the
    //                  product already treats a newer cue as cutting the older
    //                  sound, so an unbounded queue of stale chimes would be a
    //                  regression, not a feature.
    static class SoundCue
    {
        // ── WAV container inspection (R023) ──────────────────────────────────
        internal class WavInfo
        {
            public bool Valid;
            public int FormatTag, Channels, BitsPerSample, BlockAlign;
            public int DataOffset, DataLength;
            public string Reason = "";
        }

        static int LE16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
        static int LE32(byte[] b, int i)
        { return b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24); }

        // Walk the chunk list without ever indexing outside the buffer. A
        // negative or oversized chunk length fails closed; odd-length chunks
        // carry one padding byte that the traversal must skip.
        internal static WavInfo Inspect(byte[] b)
        {
            var w = new WavInfo();
            if (b == null || b.Length < 12) { w.Reason = "too small"; return w; }
            if (b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F')
            { w.Reason = "not RIFF"; return w; }
            if (b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            { w.Reason = "not WAVE"; return w; }
            int pos = 12;
            bool haveFmt = false, haveData = false;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = LE32(b, pos + 4);
                if (len < 0 || pos + 8 + (long)len > b.Length)
                { w.Reason = "chunk length out of bounds"; return w; }
                if (id == "fmt " && len >= 16)
                {
                    w.FormatTag = LE16(b, pos + 8);
                    w.Channels = LE16(b, pos + 10);
                    w.BitsPerSample = LE16(b, pos + 22);
                    w.BlockAlign = LE16(b, pos + 20);
                    haveFmt = true;
                }
                else if (id == "data")
                {
                    w.DataOffset = pos + 8;
                    w.DataLength = len;
                    haveData = true;
                    break;                      // samples end here; trailing chunks are metadata
                }
                pos += 8 + len + (len & 1);     // odd-length chunks are word-padded
            }
            if (!haveFmt) { w.Reason = "no fmt chunk"; return w; }
            if (!haveData) { w.Reason = "no data chunk"; return w; }
            if (w.DataOffset + (long)w.DataLength > b.Length)
            { w.Reason = "data out of bounds"; return w; }
            w.Valid = true;
            return w;
        }

        // Classic integer PCM only: tag 1 and a sane structure. The supported
        // widths are 8/16/24/32; IEEE float (3), A-law (6), mu-law (7) and
        // WAVE_FORMAT_EXTENSIBLE (0xFFFE) are UNSUPPORTED — which means "do
        // not transform this file", never "this file will not play".
        internal static bool IsSupportedPcm(WavInfo w)
        {
            if (!w.Valid) return false;
            if (w.FormatTag != 1) return false;
            if (w.Channels <= 0) return false;
            if (w.BitsPerSample != 8 && w.BitsPerSample != 16
                && w.BitsPerSample != 24 && w.BitsPerSample != 32) return false;
            if (w.BlockAlign != w.Channels * (w.BitsPerSample / 8)) return false;
            return true;
        }

        // ── the scaling outcome (R029/R023 contract) ─────────────────────────
        internal class ScaleResult
        {
            public bool Supported;              // validated integer PCM, transformed in place
            public byte[] Bytes;                // the transformed buffer (Supported only)
            public string Reason;               // why not, for the diagnostic path
        }

        // One owned buffer in, transformed in place. No clone before validation,
        // and no per-sample allocation: PCM32 writes its little-endian bytes
        // directly, exactly like the 16/24-bit branches always did. Rounding,
        // clamping and signed range keep their exact old semantics.
        internal static ScaleResult ScaleInPlace(byte[] b, double gain)
        {
            var r = new ScaleResult();
            WavInfo w = Inspect(b);
            if (!IsSupportedPcm(w)) { r.Reason = string.IsNullOrEmpty(w.Reason) ? "unsupported format" : w.Reason; return r; }
            r.Supported = true;
            if (gain >= 0.999999) { r.Bytes = b; return r; }   // nothing to do: full volume is not a transform
            int bps = w.BitsPerSample / 8;
            int end = Math.Min(b.Length, w.DataOffset + w.DataLength);
            for (int i = w.DataOffset; i + bps <= end; i += bps)
            {
                if (w.BitsPerSample == 8)
                {
                    int v = (int)Math.Round((b[i] - 128) * gain) + 128;
                    b[i] = (byte)Math.Max(0, Math.Min(255, v));
                }
                else if (w.BitsPerSample == 16)
                {
                    int n = (int)Math.Round(BitConverter.ToInt16(b, i) * gain);
                    n = Math.Max(short.MinValue, Math.Min(short.MaxValue, n));
                    b[i] = (byte)(n & 0xFF); b[i + 1] = (byte)((n >> 8) & 0xFF);
                }
                else if (w.BitsPerSample == 24)
                {
                    int raw = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
                    int v = (raw & 0x800000) != 0 ? raw - 0x1000000 : raw;
                    long n = Math.Max(-8388608L, Math.Min(8388607L, (long)Math.Round(v * gain)));
                    int u = (int)n & 0xFFFFFF;
                    b[i] = (byte)(u & 0xFF); b[i + 1] = (byte)((u >> 8) & 0xFF); b[i + 2] = (byte)((u >> 16) & 0xFF);
                }
                else // 32
                {
                    long n = (long)Math.Round((double)BitConverter.ToInt32(b, i) * gain);
                    n = Math.Max(int.MinValue, Math.Min((long)int.MaxValue, n));
                    int u = (int)n;
                    b[i] = (byte)(u & 0xFF);
                    b[i + 1] = (byte)((u >> 8) & 0xFF);
                    b[i + 2] = (byte)((u >> 16) & 0xFF);
                    b[i + 3] = (byte)((u >> 24) & 0xFF);
                }
            }
            r.Bytes = b;
            return r;
        }

        // One player for the process, owned exclusively by the audio worker. A
        // cue cutting the previous one is the right behaviour here (two reset
        // chimes overlapping is noise), and it keeps a single undisposed
        // SoundPlayer instead of one per event.
        static System.Media.SoundPlayer Player;

        // PERF-002 (SRC-006:R019): counting seams for the paint-path
        // regressions. A Settings paint must show ZERO calls to either seam
        // and ZERO shipped-WAV filesystem checks/extraction.
        internal static int LibraryCalls, ResolveCalls, ShippedFileChecks, SoundExtracts;
        internal static void ResetCountingForTests()
        {
            LibraryCalls = ResolveCalls = ShippedFileChecks = SoundExtracts = 0;
        }

        // PERF-004 seam: the real player is a shared SoundPlayer, which cannot be
        // asked whether two cues overlapped. tests\apply_thread.cs substitutes a
        // recorder that can.
        internal static Func<string, string> PlayBackend = null;

        // Publication seam (R010): the atomic commit step. Production is null
        // (the staging + replace below runs); a test injects a refusal that
        // fires AFTER staging is complete and BEFORE the final replace, so the
        // previous valid artifact can be proven intact.
        internal static Func<string, bool> PublishGate = null;

        // ── the cache (R010 / W2-007 identity) ───────────────────────────────
        // PERF-004 (SRC-006:R021): hard ceiling + LRU. The cache identity stays
        // source|quantized-volume; a distinct source path gives a distinct
        // artifact (Tag), and 5% quantization is preserved in Scaled. The
        // ceiling counts ENTRIES (source×volume combos), not files.
        class CacheEntry
        {
            public string Path;
            public long SourceLength;
            public DateTime SourceMtimeUtc;
            public long Seq;                        // LRU tick (strict ++SeqClock)
        }

        static readonly Dictionary<string, CacheEntry> Built = new Dictionary<string, CacheEntry>();
        // One small SeqClock per process, bumped under StateLock.
        static long SeqClock;
        internal static int CacheCap = 128;          // documented hard maximum
        internal static int DiskCap = 512;           // hard ceiling on owned generated artifacts, active included
        internal static int MaintenanceEvery = 32;   // coarse cadence: every Nth miss
        internal static int Misses;
        static string ActiveArtifact;                // path the active player owns

        // SRC-007 (repairs SRC-006:R021): positive ownership index. A file is
        // deletable by maintenance ONLY when it was created by THIS process's
        // PublishAtomic commit — filename grammar alone NEVER grants deletion
        // authority, so a user WAV named limisaw_user_abcdef00_v50.wav inside
        // the cache directory is FOREIGN and untouchable. Ownership entries
        // are keyed by full path and are deliberately tolerant of staleness:
        // a stale entry (file already gone / deleted by hand) is simply
        // skipped; a file present on disk without an entry is never deleted.
        // The index also owns staging lifecycle: PublishAtomic registers the
        // staging path BEFORE writing, transfers to the final path ONLY after
        // a successful atomic commit, and removes the staging entry in every
        // terminal path — so crash cleanup deletes only staging residue this
        // process family can prove it created.
        static readonly HashSet<string> OwnedFinals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> OwnedStaging = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static bool IsOwnedFinalForTests(string path) { lock (StateLock) return OwnedFinals.Contains(path); }

        static void TouchLocked(string key, CacheEntry e)
        {
            // PERF-004 LRU: evict the OLDEST entry once the cap would be
            // exceeded. Deterministic: Seq is strictly increasing.
            if (Built.Count >= CacheCap)
            {
                string victim = null; long oldest = long.MaxValue;
                foreach (var kv in Built) if (kv.Value.Seq < oldest) { oldest = kv.Value.Seq; victim = kv.Key; }
                if (victim != null) Built.Remove(victim);
            }
            e.Seq = ++SeqClock; Built[key] = e;
        }

        internal static void ResetCacheForTests()
        {
            lock (StateLock) { Built.Clear(); Misses = 0; SeqClock = 0; ActiveArtifact = null; OwnedFinals.Clear(); OwnedStaging.Clear(); }
        }

        // Where the picker looks. A folder the user pointed at wins, then a
        // Sounds folder next to the exe, then the WAVs embedded in the exe, and
        // the Windows media folder last so the list is never empty.
        public static string Library(string root, string setting)
        {
            LibraryCalls++;
            if (!string.IsNullOrEmpty(setting) && Directory.Exists(setting)) return setting;
            string local = Path.Combine(root ?? "", "Sounds");
            if (Directory.Exists(local)) return local;
            string shipped = Assets.SoundLibrary();
            ShippedFileChecks++;
            if (shipped != null) return shipped;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        }

        // A stored value is either a bare name inside the library or an absolute
        // path the user picked elsewhere. Null means "nothing to play", which the
        // caller must answer with silence rather than an exception at notify time.
        public static string Resolve(string root, string library, string file)
        {
            ResolveCalls++;
            if (string.IsNullOrEmpty(file)) return null;
            if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;
            string p = Path.Combine(Library(root, library), file);
            if (File.Exists(p)) return p;
            // The shipped defaults must keep working after the user points the
            // picker at a folder that does not contain them.
            string shipped = Assets.SoundLibrary();
            ShippedFileChecks++;
            if (shipped != null)
            {
                string s = Path.Combine(shipped, file);
                // Any File.Exists on a shipped WAV is recovery work — tracked
                // by ShippedFileChecks so a paint can be proven to do none.
                if (File.Exists(s)) return s;
            }
            return File.Exists(file) ? file : null;
        }

        // Play a cue, and say what stopped it. SYNCHRONOUS by design: only the
        // audio owner (or a single-threaded test) runs this. Producers use
        // Submit.
        //
        // `null` = it played. Anything else is a sentence for the user, because a
        // muted alert is indistinguishable from a quiet account: the balloon
        // still appears, so nothing about the app's behaviour suggests the chime
        // setting is the thing that is broken. The `Play` preview button already
        // reported "No such WAV" for the very same file — the alert path just
        // threw the reason away.
        public static string Play(string root, string library, string file, int volume)
        {
            if (string.IsNullOrEmpty(file)) return "no sound is set";
            if (volume <= 0) return null;   // muted on purpose is not a fault
            string path = Resolve(root, library, file);
            if (path == null) return "no such WAV: " + file;
            string name = Path.GetFileName(file);
            try
            {
                if (volume < 100)
                {
                    string scaled = Scaled(path, volume);
                    // The scaled copy is OUR artifact, not the user's file. If it
                    // will not play — an unsupported container survives untouched
                    // and can still be rejected by the player — fall back to the
                    // original once: loud beats silent, and the user's own file is
                    // the one thing here they did not derive.
                    if (scaled != null && PlayFile(scaled) == null) return null;
                    string why = PlayFile(path);
                    if (why == null)
                        return scaled == null
                            ? name + " played at full volume (it could not be scaled)"
                            : null;
                    return name + ": " + why;
                }
                string direct = PlayFile(path);
                return direct == null ? null : name + ": " + direct;
            }
            catch (Exception ex) { return name + ": " + ex.GetType().Name; }
        }

        // Null = playing. Otherwise the exception's type name; the CALLER owns
        // the display name, because the path here may be a cache artifact whose
        // name the user has never seen.
        static string PlayFile(string path)
        {
            try
            {
                // PERF-004 (SRC-006:R021): the artifact committed to the player
                // must not be pruned as stale until it is no longer the active one.
                lock (StateLock) ActiveArtifact = path;
                if (PlayBackend != null) return PlayBackend(path);
                if (Player == null) Player = new System.Media.SoundPlayer();
                else Player.Stop();
                Player.SoundLocation = path;
                // Load() on purpose: Play() alone starts an async load and can
                // miss the first play of a file this process has not heard yet.
                Player.Load();
                Player.Play();
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name; }
        }

        static string CacheDir() { return Path.Combine(Path.GetTempPath(), "limisaw_sounds"); }

        // Volume is quantised to 5% steps so dragging the slider cannot mint one
        // temp WAV per pixel — the exact orphan problem Problip's volume drag
        // had before it learned to reuse its cache. Returns the artifact path,
        // or null when there is nothing to hand the player (unsupported format,
        // unreadable source): the caller then plays the ORIGINAL file. No
        // derived artifact is ever minted for an unsupported file.
        static string Scaled(string src, int volume)
        {
            int q = Math.Max(5, Math.Min(100, volume / 5 * 5));
            string key = src + "|" + q;
            CacheEntry entry;
            lock (StateLock) entry = Built.ContainsKey(key) ? Built[key] : null;
            if (entry != null && ValidHit(entry, src))
            {
                // PERF-004: a hit proves the entry is still good — bump its LRU.
                lock (StateLock) if (Built.ContainsKey(key)) Built[key].Seq = ++SeqClock;
                return entry.Path;
            }
            try
            {
                string dir = CacheDir();
                Directory.CreateDirectory(dir);
                // PERF-004: the owned temp directory is bounded coarsely, not on
                // every Play. The first miss cleans a crash-staged leftover fast,
                // then every MaintenanceEvery-th miss prunes age + cardinality.
                int m; lock (StateLock) m = ++Misses;
                if (m == 1 || m % MaintenanceEvery == 0) MaintainDisk(dir);
                // W2-007: the artifact name carries a digest of the SOURCE PATH,
                // not just its basename, so two different files both called
                // alert.wav own distinct artifacts.
                string outPath = Path.Combine(dir,
                    "limisaw_" + Path.GetFileNameWithoutExtension(src) + "_" + Tag(src) + "_v" + q + ".wav");
                // R029: File.ReadAllBytes gives us a PRIVATE owned buffer; the
                // source file itself is never opened writable. R023: validate the
                // container first; only validated integer PCM is transformed.
                byte[] source = File.ReadAllBytes(src);
                ScaleResult r = ScaleInPlace(source, q / 100.0);
                if (!r.Supported) return null;      // no fake artifact for a float/A-law/extensible WAV
                // R010: never write the final artifact directly. Stage, then
                // atomically replace — a failure before the commit leaves the
                // previous valid artifact byte-for-byte intact.
                PublishAtomic(outPath, r.Bytes);
                var e = new CacheEntry
                {
                    Path = outPath,
                    SourceLength = new FileInfo(src).Length,
                    SourceMtimeUtc = File.GetLastWriteTimeUtc(src),
                };
                // PERF-004: deterministic LRU on insert, never exceeding CacheCap.
                lock (StateLock) TouchLocked(key, e);
                return outPath;
            }
            catch { return null; }
        }

        // A cache hit is only as good as its source: the artifact must still
        // exist AND the source must still be the exact file (length + mtime)
        // the artifact was derived from. An in-process edit of the user's WAV
        // therefore invalidates the entry on the next play — without anyone
        // clearing the dictionary, which is the defect the old
        // hit-and-return fast path hid for a whole process lifetime.
        static bool ValidHit(CacheEntry e, string src)
        {
            try
            {
                if (e == null || e.Path == null || !File.Exists(e.Path)) return false;
                if (!File.Exists(src)) return false;
                var info = new FileInfo(src);
                return info.Length == e.SourceLength
                    && info.LastWriteTimeUtc == e.SourceMtimeUtc;
            }
            catch { return false; }
        }

        // Staging + atomic same-volume publish. The staging file lives in the
        // SAME cache directory (same volume, so File.Replace is atomic), is
        // fully written and closed before the commit, and is deleted on every
        // failure path — no owned *.tmp junk survives. PublishGate is the test
        // seam: a refusal after staging is complete must leave the previous
        // final artifact untouched.
        //
        // SRC-007 (repairs SRC-006:R021): the staging file is POSITIVELY OWNED
        // before a single byte is written, and the final artifact becomes
        // owned ONLY after the atomic commit succeeds. Crash cleanup can then
        // prove which *.staging-* residue LIMISAW itself created: a foreign
        // file that merely imitates the staging grammar has no entry and is
        // never deleted.
        static void PublishAtomic(string finalPath, byte[] bytes)
        {
            string staging = finalPath + ".staging-" + Guid.NewGuid().ToString("N");
            lock (StateLock) OwnedStaging.Add(staging);
            try
            {
                File.WriteAllBytes(staging, bytes);
                if (PublishGate != null && !PublishGate(finalPath))
                    throw new IOException("publish refused by gate");
                if (File.Exists(finalPath)) File.Replace(staging, finalPath, null);
                else File.Move(staging, finalPath);
                // Ownership transfers to the final artifact ONLY after the
                // commit succeeded. A crash between the replace and this line
                // loses ownership of one valid artifact — that direction is
                // safe (the next rebuild re-registers it); the reverse
                // direction would claim foreign files.
                lock (StateLock) { OwnedFinals.Add(finalPath); OwnedStaging.Remove(staging); }
            }
            finally
            {
                try { if (File.Exists(staging)) File.Delete(staging); } catch { }
                lock (StateLock) OwnedStaging.Remove(staging);
            }
        }

        // A short digest of the canonical source path: distinct files get
        // distinct artifacts, the same file gets the same one on every run, and
        // the human-readable basename stays in the name so the cache folder is
        // still readable. Same rule as Probe.HomeId, for the same reason.
        static string Tag(string src)
        {
            string norm;
            try { norm = Path.GetFullPath(src).ToLowerInvariant(); }
            catch { norm = (src ?? "").ToLowerInvariant(); }
            byte[] bytes = System.Security.Cryptography.SHA256.Create()
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(norm));
            var sb = new System.Text.StringBuilder(8);
            for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        // SRC-007 (repairs SRC-006:R021): positive-ownership maintenance.
        // Deletion authority comes from the OWNERSHIP INDEX built by this
        // process's PublishAtomic commits — never from the filename grammar.
        // A foreign file that perfectly imitates the generated-artifact or
        // staging grammar has no index entry and is never deleted. Only
        // positively-owned generated finals and positively-owned abandoned
        // staging residue are eligible for age/cardinality cleanup. The
        // active artifact is protected from both. Stale ownership entries
        // (file already gone) are skipped and dropped. Total positively-owned
        // generated artifacts, active included, never exceeds DiskCap.
        static void MaintainDisk(string dir)
        {
            try
            {
                List<string> ownedStagingSnapshot;
                lock (StateLock) ownedStagingSnapshot = new List<string>(OwnedStaging);
                foreach (string f in ownedStagingSnapshot)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    lock (StateLock) if (!File.Exists(f)) OwnedStaging.Remove(f);
                }

                List<string> ownedFinalsSnapshot;
                lock (StateLock) ownedFinalsSnapshot = new List<string>(OwnedFinals);
                // Drop stale entries: an owned artifact the user (or a
                // previous maintenance run) already removed. Harmless and
                // keeps the cardinality count honest.
                var owned = new List<string>();
                foreach (string f in ownedFinalsSnapshot)
                {
                    if (File.Exists(f)) owned.Add(f);
                    else lock (StateLock) OwnedFinals.Remove(f);
                }

                DateTime cutoff = DateTime.UtcNow.AddDays(-3);
                string active; lock (StateLock) active = ActiveArtifact;
                for (int i = owned.Count - 1; i >= 0; i--)
                {
                    string f = owned[i];
                    bool isActive = !string.IsNullOrEmpty(active) && f.Equals(active, StringComparison.OrdinalIgnoreCase);
                    if (isActive) continue;
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        try { File.Delete(f); } catch { }
                        lock (StateLock) OwnedFinals.Remove(f);
                        owned.RemoveAt(i);
                    }
                }
                if (owned.Count > DiskCap)
                {
                    // The ceiling counts the ACTIVE owner too: evict exactly the
                    // surplus from the non-active set, oldest first. The active
                    // artifact is skipped and survives.
                    int toRemove = owned.Count - DiskCap;
                    owned.Sort((a, b) =>
                    {
                        int c = File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                        if (c != 0) return c;
                        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                    });
                    foreach (string f in owned)
                    {
                        if (toRemove <= 0) break;
                        if (!string.IsNullOrEmpty(active) && f.Equals(active, StringComparison.OrdinalIgnoreCase)) continue;
                        try { File.Delete(f); } catch { }
                        lock (StateLock) OwnedFinals.Remove(f);
                        toRemove--;
                    }
                }
            }
            catch { }
        }

        // ── the audio owner (R022) ───────────────────────────────────────────
        // One long-lived serialized worker. Producers submit immutable requests
        // and never touch the player or the cache; at most one cue is RUNNING
        // and at most one is PENDING, and a newer pending request replaces an
        // older one (latest wins) — an unbounded backlog of stale chimes is the
        // exact thing the cut-older-playback philosophy does not want.
        class CueRequest
        {
            public string Root, Library, File, Kind;
            public int Volume;
            public long Seq;
            public Action<string> Completion;
        }

        static readonly object StateLock = new object();
        static CueRequest Pending;
        // W2-004: an explicit "a request is executing right now" seam. The
        // owner thread is long-lived, so Worker==null was never a usable
        // drain signal; Running is what WaitIdle reads.
        static bool Running;
        static long SeqCounter;
        static System.Threading.Thread Worker;
        static System.Threading.AutoResetEvent Wake;
        static bool Stopping;

        // Submit a cue for the owner. Returns the request's sequence id, so a
        // completion callback can recognise that it is still the newest intent
        // (a superseded preview must not overwrite the newer note). Never
        // blocks on audio work.
        public static long Submit(string root, string library, string file, int volume,
            string kind, Action<string> completion)
        {
            lock (StateLock)
            {
                if (Stopping) return -1;
                if (Worker == null)
                {
                    Wake = new System.Threading.AutoResetEvent(false);
                    Worker = new System.Threading.Thread(OwnerLoop);
                    Worker.IsBackground = true;         // crash safety: never keep the process alive
                    Worker.Name = "LIMISAW-audio";
                    Worker.Start();
                }
                var req = new CueRequest
                {
                    Root = root, Library = library, File = file,
                    Volume = volume, Kind = kind,
                    Seq = ++SeqCounter,
                    Completion = completion,
                };
                // W2-004: exactly one pending slot; a newer request replaces
                // the older pending one (latest wins). No list, no batch.
                Pending = req;
                Wake.Set();
                return req.Seq;
            }
        }

        // The owner: take the newest pending request, play it to completion of
        // its SETUP (the cache and the player are touched by this thread only),
        // then deliver its result to the submitter's callback — off the UI
        // thread, which never waits for audio again.
        static void OwnerLoop()
        {
            while (true)
            {
                CueRequest run = null;
                lock (StateLock)
                {
                    if (Pending != null) { run = Pending; Pending = null; Running = true; }
                }
                if (run != null)
                {
                    Execute(run);
                    lock (StateLock) Running = false;
                    continue;
                }
                if (Stopping) break;
                Wake.WaitOne(1000);
            }
            // Shutdown: stop playback and drop the player; the thread exits.
            try { if (Player != null) { Player.Stop(); Player.Dispose(); } } catch { }
            Player = null;
        }

        static void Execute(CueRequest run)
        {
                // Shutdown landed between the handoff and playback: discard the
                // request instead of playing into a dying owner.
                if (Stopping) return;
                string why;
                try { why = Play(run.Root, run.Library, run.File, run.Volume); }
                catch (Exception ex) { why = ex.GetType().Name; }
                var done = run;
                if (done.Completion != null)
                {
                    try { done.Completion(why); }
                    catch { }                          // a dead UI must not kill the owner
                }
        }

        // Deterministic shutdown: stop accepting, discard the pending request,
        // stop playback, terminate the worker, release its wait handle. A later
        // Start (the first Submit after this) builds a fresh owner, so the test
        // lifecycle can start/stop cleanly without leaking threads.
        public static void Shutdown()
        {
            System.Threading.Thread w;
            lock (StateLock)
            {
                Stopping = true;
                Pending = null;
                w = Worker;
                if (Wake != null) Wake.Set();
            }
            try { if (w != null) w.Join(3000); } catch { }
            lock (StateLock)
            {
                if (Worker != null && Worker.IsAlive)
                { try { Worker.Abort(); } catch { } }   // a hung SoundPlayer.Load must not outlive shutdown
                Worker = null;
                try { if (Player != null) { Player.Stop(); Player.Dispose(); } } catch { }
                Player = null;
                if (Wake != null) { Wake.Dispose(); Wake = null; }
                Stopping = false;                       // the next Submit starts a fresh owner
                Running = false;
                ActiveArtifact = null;
                SeqCounter = 0;
            }
        }

        // Test seam: true when the owner has drained everything it was handed
        // (nothing running, nothing pending). A bounded wait, never a spin.
        internal static bool WaitIdle(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                lock (StateLock) if (Pending == null && !Running) return true;
                System.Threading.Thread.Sleep(15);
            }
            lock (StateLock) return Pending == null && !Running;
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

        // PERF-005: content update and position update are SEPARATE. Update
        // rebuilds text, measures once, repaints once. Move only revalidates
        // the anchor against the current cursor/monitor with the cached
        // measured size — no row rebuild, no Bitmap, no Graphics.
        public void UpdateContent(string title, List<Row> rows, Point cursor)
        {
            Title = title; Rows = rows;
            BackColor = Palette.BG;
            LastMeasured = Measure();
            Position(LastMeasured, cursor);
        }

        public void MoveTo(Point cursor)
        {
            Position(LastMeasured.IsEmpty ? Measure() : LastMeasured, cursor);
        }

        // PERF-005: the measured size of the current content. Invalidated by
        // UpdateContent; MoveTo reuses it. Handles are never retained across
        // contexts — only the numbers.
        Size LastMeasured;

        void Position(Size size, Point cursor)
        {
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

        // The legacy entry point: content + position in one call. The hover
        // path uses UpdateContent/MoveTo; tests and any other caller keep
        // working unchanged.
        public void Show(string title, List<Row> rows, Point cursor)
        {
            UpdateContent(title, rows, cursor);
        }

        // PERF-005: deterministic cost seam — every allocation-paying text
        // measurement lands here, so the harness can count them exactly.
        internal int MeasureCalls;

        Size Measure()
        {
            MeasureCalls++;
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
        List<string> DuplicateCodexHomes = new List<string>();
        List<string> UnverifiedCodexHomes = new List<string>();
        List<CliInfo> Clis = new List<CliInfo>();
        internal List<VendorConnection> Connections = new List<VendorConnection>();
        internal HashSet<string> ExpandedVendors = new HashSet<string>();
        internal ConnectionCoordinator ConnCoordinator = new ConnectionCoordinator();
        List<Theme> Themes;
        List<string> NotifiedResetKeys = new List<string>();
        // window key -> the reset stamp it was alerted for. See DetectLow.
        Dictionary<string, string> NotifiedLow = new Dictionary<string, string>();
        bool LowBaseline;
        string LastFetch = ""; string LastError = ""; string TrayError = ""; bool Stale = false;
        // CORE-004 (audit/7): the sweep-flight bookkeeping. Refreshing and
        // PendingRefresh are a compound state machine with two writers — the UI
        // thread (RefreshData's admission, CompleteSweep's normal drain) and,
        // exceptionally, the sweep worker (AbandonSweep, when a result could not
        // be marshalled to a live UI owner). SweepGate is the ONE primitive all
        // three paths share, so the worker's released-flight write can never
        // interleave with the UI thread's compound transition. The fields are
        // still plain bools: every read/write is inside lock(SweepGate), which
        // is what makes the ownership claim real instead of hopeful.
        readonly object SweepGate = new object();
        bool Refreshing = false;
        // W2-001: a refresh requested while one is in flight (the only
        // state-changing vendor action, a banked reset, always asks for one) is
        // COALESCED, never dropped: the gate remembers it and the sweep that
        // finishes next runs it, so the post-mutation re-read always happens.
        bool PendingRefresh = false;
        // The sweep body itself. Production reads the vendors; tests\refresh_coalesce.cs
        // substitutes a delegate so one sweep can be HELD open while a reset asks
        // for its mandatory re-read — the exact ordering the dropped-refresh
        // defect hid, and one that real vendor CLIs cannot be made to produce.
        // The argument is the Zcode credential permission the sweep is running
        // under, so a test can prove W2-003's reloaded value reaches the probe.
        Func<bool, ProbeResult> SweepSource = null;
        // Test seam (W2-002 B): pause between admission and the vendor
        // operation, so a harness can hold a queued worker immediately before
        // op.Verify and prove the shutdown recheck governs execution. Null in
        // production — assigned by tests only, the same convention as the
        // other seams (PlayBackend, PublishGate).
        internal static Action BeforeVendorWork = null;
        // W2-003: the application-lifecycle gate. Set by BeginShutdown before any
        // teardown begins; a late sweep that observes it must discard its result
        // rather than publish into a dying form. volatile because it is written
        // on the UI thread and read on the ThreadPool sweep thread.
        internal volatile bool ShuttingDown;
        // W2-003: the sweep generation. SweepGeneration is bumped only by
        // BeginShutdown; ActiveSweepGeneration is stamped when a sweep starts.
        // The predicate `ActiveSweepGeneration != SweepGeneration` (or the flag)
        // is the cancellation a late result must observe. Both are written only
        // on the UI thread, read by the worker.
        volatile int SweepGeneration;
        volatile int ActiveSweepGeneration;
        int Tab = TabAccounts; string Note = "";
        Timer RefreshTimer; NotifyIcon Tray;
        // A registration list the measure pass can run silently: the settings
        // height is measured by executing the REAL paint path against a
        // scratch bitmap, and a measure pass must never pollute the
        // interactive registry (or the two realities would drift — the exact
        // defect the offscreen-hit regression exists for).
        class RegenList<T> : System.Collections.IList, IEnumerable<T>
        {
            readonly List<T> inner = new List<T>();
            public bool Silent;
            public void Add(T v) { if (!Silent) inner.Add(v); }
            public void Clear() { inner.Clear(); }
            public int Count { get { return inner.Count; } }
            public T this[int i]
            {
                get { return inner[i]; }
                set { inner[i] = value; }
            }
            public void RemoveAt(int i) { inner.RemoveAt(i); }
            public IEnumerator<T> GetEnumerator() { return inner.GetEnumerator(); }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return inner.GetEnumerator(); }

            // The non-generic IList surface is what the reflection harnesses
            // enumerate through; Add stays silent-aware there too.
            int System.Collections.ICollection.Count { get { return inner.Count; } }
            bool System.Collections.ICollection.IsSynchronized { get { return false; } }
            object System.Collections.ICollection.SyncRoot { get { return ((System.Collections.ICollection)inner).SyncRoot; } }
            void System.Collections.ICollection.CopyTo(Array array, int index) { ((System.Collections.ICollection)inner).CopyTo(array, index); }
            bool System.Collections.IList.IsFixedSize { get { return false; } }
            bool System.Collections.IList.IsReadOnly { get { return false; } }
            object System.Collections.IList.this[int i]
            {
                get { return inner[i]; }
                set { if (!Silent) inner[i] = (T)value; }
            }
            int System.Collections.IList.Add(object value)
            {
                if (Silent) return inner.Count;
                inner.Add((T)value);
                return inner.Count - 1;
            }
            bool System.Collections.IList.Contains(object value) { return inner.Contains((T)value); }
            int System.Collections.IList.IndexOf(object value) { return inner.IndexOf((T)value); }
            void System.Collections.IList.Insert(int index, object value) { if (!Silent) inner.Insert(index, (T)value); }
            void System.Collections.IList.Remove(object value) { if (!Silent) inner.Remove((T)value); }
            void System.Collections.IList.RemoveAt(int index) { inner.RemoveAt(index); }
        }

        RegenList<Rectangle> Buttons = new RegenList<Rectangle>(); RegenList<Action> ButtonActions = new RegenList<Action>();
        // Where the last repaint actually put its measured content (gauges and
        // width-bounded text). Buttons must never land on top of these, and
        // tests\layout_fit.cs asserts exactly that - a limit bar sliding under a
        // button is invisible to a rectangle-only check.
        RegenList<Rectangle> Marks = new RegenList<Rectangle>();
        // Labels the last repaint could NOT fit in their button, even after
        // stepping the font down. Empty is the contract: a cropped label like
        // "Showing: Le" is a layout bug, not a rendering choice.
        RegenList<string> Cropped = new RegenList<string>();

        const int TabAccounts = 0, TabTray = 1, TabSettings = 2, TabCli = 3;
        static readonly string[] TabLabels = { "Accounts", "Tray", "Settings", "Connections" };

        const int CardHeight = 30, HeaderH = 22, RowH = 18, FooterH = 24, ToolbarH = 56, CliCardH = 58, Gap = 6;
        // T-42: the compact Settings metrics — 24px rows with 20px buttons, and
        // 16px section headings. Only the Settings blocks use these; the tray
        // picker keeps its own PickRowH.
        const int PickRowH = 22, SetRowH = 24, BtnH = 20, SectionH = 16, ThemeRowH = 20, BtnPad = 18;

        // ── T-42: the responsive shell constants ─────────────────────────────
        // The USER owns the window size; the content scrolls. The minimum is the
        // width everything must still reflow inside (no horizontal scrollbar
        // exists), the preferred size is the compact default a 1366x768 laptop
        // shows fully inside its working area.
        internal const int MinClientW = 360, MinClientH = 320, PrefW = 420, PrefH = 620;
        // Narrow resize band around the borderless form's edges; corners win
        // over straight edges, both win over the caption strip.
        const int ResizeEdge = 6;
        // The themed body scrollbar. Reserved from the body width whenever it is
        // shown, so it never covers a control; 0 width when content fits.
        const int ScrollbarW = 9, ThumbMinH = 20, WheelPx = 72;
        // Two-column Settings breakpoint (body width).
        internal const int SettingsTwoColW = 620;

        // Session-local scroll position per tab. Never persisted to the ini:
        // it is UI state, not a setting. The paint clamps it whenever content
        // or viewport geometry changes.
        internal readonly int[] TabScroll = new int[4];
        // The active tab's last painted maxScroll — the seam the scroll tests
        // drive and the reachability regression asserts against.
        internal int MaxScroll;
        // The virtual content bottom the last real paint reached. The parity
        // seam: PanelHeight() formulas must equal PaintedContentBottom-BodyTop.
        internal int PaintedContentBottom = -1;
        // True while the settings height is being measured through the SAME
        // paint path: registration helpers become no-ops so a measure pass can
        // never pollute the interactive registry.
        bool Measuring;

        // Drag-to-reorder state for the Tray tab. A press only becomes a drag
        // after DragSlop pixels, so a click on a row still behaves like a click.
        const int DragSlop = 4;
        RegenList<Rectangle> ItemRows = new RegenList<Rectangle>(); RegenList<string> ItemRowIds = new RegenList<string>();
        int ItemRowsTop = 0;
        // Account-card row tops, measured before the drag preview reorders
        // anything: DropIndex needs geometry that does not move under the
        // pointer (see DropIndex).
        List<int> CardTops = new List<int>();
        string DragId; int DragStartY, DragY; bool Dragging;
        // A tray-row press that has not yet become a drag. On release without a
        // drag it becomes a pin (T-013): the tray number was reachable only from
        // the context menu, which the README's "every setting is in the window"
        // promise never covered.
        string ClickPinCandidate;
        // Problip-style drag slider: one active at a time, armed by OnMouseDown
        // on the knob OR anywhere on the rail. Null = no slider in flight.
        // Both rails' rects persist across paints so OnMouseDown can hit-test
        // without repainting.
        string VolDrag; Rectangle VolRail;
        Rectangle VolRailVolume, VolKnobVolume, VolRailLow, VolKnobLow, VolRailPreview;
        Rectangle VolKnobPreview;
        // The scrollbar thumb drag: armed by a press inside the thumb, ended by
        // release or a lost capture. The grab offset keeps the thumb from
        // jumping under the pointer.
        bool ThumbDrag; int ThumbGrab;

        // ── hints ────────────────────────────────────────────────────────────
        // Four characters and a prayer is what "1/8" and "Off" amount to without
        // an explanation, so every control on a settings row registers the
        // sentence that says what it does. The footer shows it immediately; after
        // a short dwell the same sentence also appears as a themed popup near the
        // pointer (see the tooltip section below).
        RegenList<Rectangle> HintZones = new RegenList<Rectangle>();
        RegenList<string> HintTexts = new RegenList<string>();
        string Hover = "";

        void Hint(Rectangle area, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            HintZones.Add(area); HintTexts.Add(text);
        }

        // A labelled row: the label itself explains the row, so hovering anywhere
        // on it — including the dead space — answers the question.
        void HintRow(int x, int y, int w, string text) { Hint(new Rectangle(x, y, w, SetRowH), text); }

        // ONE registration for an interactive control: clickable rectangle, the
        // action it performs and the sentence that explains it, added together.
        // Separate Buttons/ButtonActions/HintZones lists made it possible to
        // register an action and forget its hint — that gap is now structural:
        // every clickable rectangle registered here carries its explanation by
        // construction, and the tooltip parity regression asserts it.
        internal int ActionsRegistered;
        internal int ActionsWithHint;
        internal void RegisterAction(Rectangle r, Action click, string hint)
        {
            Buttons.Add(r); ButtonActions.Add(click);
            if (!Measuring) { ActionsRegistered++; if (!string.IsNullOrEmpty(hint)) ActionsWithHint++; }
            Hint(r, hint);
        }

        // ── themed hover tooltip ─────────────────────────────────────────────
        // The footer keeps the immediate explanation; after a short dwell the
        // same sentence ALSO appears as a themed popup near the pointer, painted
        // as an overlay inside this form (no top-level window, no Alt-Tab entry,
        // no focus, never a white system tooltip that ignores the palette).
        // Geometry is recomputed at show time and clamped inside the client
        // rectangle, and never covers the pointer when avoidable.
        internal const int TooltipDelayMs = 400;
        internal const int TooltipMaxW = 300;
        Timer TooltipTimer;
        string TooltipPending;      // hint sentence under the pointer, if any
        Rectangle TooltipTarget;     // where the pointer dwells
        Point TooltipPointer;
        string TooltipShown = "";    // "" = hidden
        internal Rectangle TooltipRect;
        // The runtime seams the tooltip harness drives without a real timer.
        internal int TooltipShownCount, TooltipHiddenCount;
        // Dwell integrity: one pending hover starts the timer ONCE. Movement
        // inside the same target may update the pointer but must never restart
        // the dwell, or a pointer that keeps drifting within one control can
        // postpone its tooltip forever.
        internal int TooltipTimerStarts;

        void TooltipInit()
        {
            TooltipTimer = new Timer { Interval = TooltipDelayMs };
            TooltipTimer.Tick += (o, e) => { TooltipTimer.Stop(); TooltipFire(); };
        }

        // Hover targets are identified by the ZONE RECTANGLE, never by the hint
        // text: two different buttons may legitimately share one explanation,
        // and moving from one to the other must begin a new dwell and position
        // against the new control.
        void TooltipHover(string hint, Point p, Rectangle target)
        {
            if (hint.Length == 0) { TooltipDismiss(); return; }
            bool pending = !string.IsNullOrEmpty(TooltipPending);
            bool sameTarget = pending && target == TooltipTarget;
            if (sameTarget)
            {
                // Same target: a pending dwell keeps counting (no restart), a
                // shown popup stays shown. The pointer may move; the timer may not.
                TooltipPointer = p;
                return;
            }
            TooltipDismiss();
            TooltipPending = hint; TooltipTarget = target; TooltipPointer = p;
            if (TooltipTimer != null) { TooltipTimer.Stop(); TooltipTimer.Start(); TooltipTimerStarts++; }
        }

        // The timer seam: harnesses call this to fire the dwell without
        // waiting wall-clock time.
        internal void TooltipFire()
        {
            if (TooltipPending == null || TooltipPending.Length == 0) return;
            if (TooltipShown == TooltipPending) return;
            TooltipShown = TooltipPending;
            TooltipShownCount++;
            TooltipRect = TooltipPlace(TooltipShown, TooltipPointer, TooltipTarget);
            Refresh();
        }

        internal void TooltipDismiss()
        {
            TooltipTimerStop();
            TooltipPending = null;
            TooltipTarget = Rectangle.Empty;
            if (TooltipShown.Length > 0)
            {
                TooltipShown = "";
                TooltipHiddenCount++;
                Refresh();
            }
        }

        void TooltipTimerStop() { if (TooltipTimer != null) TooltipTimer.Stop(); }

        // Word-wrap a sentence into lines that fit the popup width, using the
        // same pixel font every other surface draws with.
        internal static List<string> TooltipWrap(Graphics g, string text, int maxW)
        {
            var lines = new List<string>();
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string para = raw;
                while (para.Length > 0)
                {
                    int cut = para.Length;
                    while (cut > 1 && Draw.Width(g, para.Substring(0, cut), 9) > maxW) cut = Math.Max(1, cut - 1);
                    if (cut >= para.Length || Draw.Width(g, para, 9) <= maxW) { lines.Add(para); break; }
                    int split = para.LastIndexOf(' ', Math.Max(1, cut - 1));
                    if (split <= 0) split = cut;
                    lines.Add(para.Substring(0, split).TrimEnd());
                    para = para.Substring(Math.Min(para.Length, split + (split < para.Length ? 1 : 0))).TrimStart();
                }
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        // Where the popup goes: near the target's top, left-biased to the
        // pointer, moved below the pointer when the box would otherwise sit on
        // it. The popup is an IN-CLIENT overlay painted last inside this form,
        // so ClientRectangle is the final renderable boundary — no screen work
        // area is consulted, and there is no second clamp that could fight the
        // client one.
        internal Rectangle TooltipPlace(string text, Point pointer, Rectangle target)
        {
            Rectangle client = ClientRectangle;
            int maxW = Math.Min(TooltipMaxW, Math.Max(60, client.Width - 12));
            int w = 0, h = 0;
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics mg = Graphics.FromImage(probe))
            {
                var lines = TooltipWrap(mg, text, maxW - 10);
                foreach (string ln in lines) w = Math.Max(w, Draw.Width(mg, ln, 9));
                h = lines.Count * 12 + 8;
            }
            w += 10;
            int x = pointer.X + 12;
            int y = target.Top - h - 4;
            if (y < 0) y = target.Bottom + 4;
            // Never cover the pointer when there is room to move away.
            if (x <= pointer.X + 4 && x + w > pointer.X && y <= pointer.Y + 4 && y + h > pointer.Y)
                x = Math.Max(0, pointer.X + 12);
            x = Math.Max(2, Math.Min(x, client.Width - w - 2));
            y = Math.Max(2, Math.Min(y, client.Height - h - 2));
            if (x < 0) x = 2; if (y < 0) y = 2;
            return new Rectangle(x, y, w, h);
        }

        // The overlay itself, painted LAST in OnPaint: raised box, bevel, the
        // wrapped sentence in palette colours. It is never part of the
        // interactive registry, so it can never answer a click.
        void PaintTooltip(Graphics g)
        {
            if (TooltipShown.Length == 0 || TooltipRect.Width <= 0) return;
            using (var bg = new SolidBrush(Palette.RAISED)) g.FillRectangle(bg, TooltipRect);
            DrawBevel(g, TooltipRect.X, TooltipRect.Y, TooltipRect.Width, TooltipRect.Height, true);
            var lines = TooltipWrap(g, TooltipShown, TooltipRect.Width - 10);
            for (int i = 0; i < lines.Count; i++)
                DrawText(g, lines[i], TooltipRect.X + 5, TooltipRect.Y + 4 + i * 12, Palette.TEXT, 9);
        }

        Rectangle TooltipZoneAt(Point p)
        {
            for (int i = HintZones.Count - 1; i >= 0; i--)
                if (HintZones[i].Contains(p)) return HintZones[i];
            return new Rectangle(p.X, p.Y, 0, 0);
        }

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
            // T-42: the saved geometry is the user's window. Position AND size
            // restore, both clamped to a current monitor's working area: a size
            // saved on a big monitor must not reopen mostly off-screen on a
            // small laptop, a position from a removed monitor moves to a live
            // one, and a below-minimum size rises to the minimum. A value the
            // clamps had to correct is NOT written back here — the next normal
            // geometry-save event persists what the user actually ends up with.
            MinimumSize = new Size(MinClientW, MinClientH);
            StartPosition = FormStartPosition.Manual;
            Rectangle work = Screen.PrimaryScreen == null ? new Rectangle(0, 0, 1280, 800) : Screen.PrimaryScreen.WorkingArea;
            foreach (Screen screen in Screen.AllScreens)
                if (screen.Primary) work = screen.WorkingArea;
            int w = Settings.WindowW > 0 ? Settings.WindowW : PrefW;
            int h = Settings.WindowH > 0 ? Settings.WindowH : PrefH;
            // No saved height means the user has never sized this window, so
            // its height follows the content until they do (see AutoFitHeight).
            AutoHeight = Settings.WindowH <= 0;
            // A saved size below the minimum rises to the minimum, never to
            // the preferred default: the user asked for a size, it is just too
            // small to be usable.
            w = Math.Max(MinClientW, Math.Min(w, Math.Max(MinClientW, work.Width - 24)));
            h = Math.Max(MinClientH, Math.Min(h, Math.Max(MinClientH, work.Height - 24)));
            ClientSize = new Size(w, h);
            if (Settings.WindowX != int.MinValue && Settings.WindowY != int.MinValue)
            {
                var saved = new Rectangle(Settings.WindowX, Settings.WindowY, w, h);
                Screen target = null;
                foreach (Screen screen in Screen.AllScreens)
                    if (screen.WorkingArea.IntersectsWith(saved)) { target = screen; break; }
                if (target == null && Screen.AllScreens.Length > 0) target = Screen.AllScreens[0];
                if (target != null)
                {
                    Rectangle wa = target.WorkingArea;
                    // Clamp enough of the window into the working area: never
                    // reopen with an inaccessible footer.
                    int x = Math.Max(wa.Left, Math.Min(saved.X, wa.Right - Math.Min(w, wa.Width)));
                    int y = Math.Max(wa.Top, Math.Min(saved.Y, wa.Bottom - Math.Min(h, wa.Height)));
                    Location = new Point(x, y);
                }
            }
            else StartPosition = FormStartPosition.CenterScreen;
            BackColor = Palette.BG; DoubleBuffered = true; TopMost = s.AlwaysOnTop;
            KeyPreview = true;
            try { Icon = Assets.AppIcon(root, SystemInformation.IconSize.Width) ?? Icon; } catch { }
            RefreshTimer = new Timer { Interval = Settings.RefreshSeconds * 1000 };
            RefreshTimer.Tick += (o, e) => RefreshData();
            // T-42: the geometry save is an END-OF-GESTURE fact, never a
            // per-WM_SIZE write. Windows raises ResizeEnd when the sizing or
            // moving modal loop finishes (the HT-edge resize, the caption
            // drag, the right-drag move all end here), so the ini sees one
            // final geometry per gesture instead of a write storm.
            ResizeEnd += (o, e) =>
            {
                TooltipDismiss();
                // A gesture that left the window at a height auto-fit did not
                // choose is the user sizing it themselves: from here the height
                // is theirs and content no longer moves it.
                if (AutoHeight && ClientSize.Height != AutoHeightApplied) AutoHeight = false;
                SaveWindowGeometry();
            };
            TooltipInit();
            FitWindow();
            // PERF-004: a sweep publishes its result by marshalling it to the UI
            // thread, and BeginInvoke needs a window handle to marshal to. The
            // handle is therefore created BEFORE the first sweep can start —
            // otherwise the start-up sweep would be the one publication that
            // still mutated the account list, NotifiedLow and the tray icon from
            // its own thread, while this constructor's caller is still wiring the
            // tray up on ours.
            ExecutableDiscovery.ZcodeAllowConfig = () => Settings.ZcodeReadConfig;
            ExecutableDiscovery.FreebuffAllowConfig = () => Settings.FreebuffReadConfig;
            try { IntPtr unused = Handle; } catch { }
            RefreshSoundDisplay();
            // The watcher's outcomes cross to this UI thread through the same
            // marshal the coordinator publishes use.
            ConnectionWatcher.OnAttempt = OnWatcherAttempt;
            ConnectionWatcher.OnExpire = OnWatcherExpire;
            ConnectionWatcher.OnRemoved = OnWatcherRemoved;
            BuildConnections();
            RefreshTimer.Start(); RefreshData();
        }

        public void ApplyTheme(string slug)
        {
            foreach (Theme t in Themes)
                if (string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase)) { Palette.T = t; break; }
            BackColor = Palette.BG;
            // PERF-005: a theme change repaints the popup's colours, so its
            // content rebuilds on the next hover.
            BumpPopupData();
        }

        // ── T-42: the responsive shell ───────────────────────────────────────
        // The old contract — measure the active tab, resize the whole window to
        // it, hope the monitor agrees — is gone. The window is the USER's size;
        // content that exceeds the body viewport SCROLLS. FitWindow keeps its
        // name (reflection tests and call sites depend on the symbol) but its
        // responsibility is now only: clamp the outer bounds to sane monitor
        // limits, clamp the active scroll, repaint. It must NOT normally change
        // ClientSize because content changed.
        // ── the default window fits its content ──────────────────────────────
        // T-42 stands: the window is the USER's size. But until the user has
        // ever given it one, there is no user size to defend — and opening with
        // a scrollbar over content that would have fitted is a worse default
        // than a taller window. So the HEIGHT follows the active tab's content
        // while AutoHeight holds, and the first real resize gesture ends it for
        // good. Width is never touched: wrapping and compactness depend on it,
        // so changing it would change what is being measured.
        bool AutoHeight;
        // The height auto-fit last applied. A ResizeEnd that finds a different
        // one is the user's own gesture, and that is what retires AutoHeight.
        int AutoHeightApplied = -1;

        // The client height at which a tab's content exactly fits — the value
        // AutoFitHeight applies. Pure arithmetic on the measured panel height:
        // no window is read or mutated, so the layout tests can assert the
        // no-scroll-by-default contract without showing a window.
        internal int FittedClientHeight(int tab, int clientW, int workHeight)
        {
            // Measured with NO scrollbar reserved: fitting is exactly the state
            // in which no bar is drawn, so reserving its strip would measure a
            // window that does not exist.
            int want = ContentHeightFor(tab, clientW) + FooterH;
            return Math.Max(MinClientH, Math.Min(want, Math.Max(MinClientH, workHeight - 24)));
        }

        void AutoFitHeight()
        {
            if (!AutoHeight || !IsHandleCreated || !Visible) return;
            if (WindowState != FormWindowState.Normal) return;
            Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
            int ch = FittedClientHeight(Tab, ClientSize.Width, work.Height);
            AutoHeightApplied = ch;
            if (ch == ClientSize.Height) return;
            ClientSize = new Size(ClientSize.Width, ch);
            // Growing downward must not push the footer off the monitor.
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
        }

        void FitWindow()
        {
            Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
            int cw = Math.Max(MinClientW, Math.Min(ClientSize.Width, Math.Max(MinClientW, work.Width - 16)));
            int ch = Math.Max(MinClientH, Math.Min(ClientSize.Height, Math.Max(MinClientH, work.Height - 16)));
            if (cw != ClientSize.Width || ch != ClientSize.Height) ClientSize = new Size(cw, ch);
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
            if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
            if (Top < work.Top) Top = work.Top;
            if (Left < work.Left) Left = work.Left;
        }

        // ── the one viewport contract ────────────────────────────────────────
        // Fixed chrome: header, action row, tab row, footer. Everything between
        // the tab row's bottom and the footer's top is the body viewport, and
        // only it scrolls. Every body rectangle — paint, hit-test, drag, hint —
        // comes from these helpers; no code may reconstruct a slightly
        // different body rectangle of its own.
        internal int BodyTop() { return HeaderH + 6 + 22 + 6 + 22 + 6; }   // header + action row + tab row
        int BodyBottom() { return Math.Max(BodyTop(), Height - FooterH); }
        internal int BodyHeight() { return Math.Max(0, BodyBottom() - BodyTop()); }
        internal Rectangle BodyViewport() { return new Rectangle(0, BodyTop(), Width, BodyHeight()); }

        // The virtual height of the active tab's content: chrome + panel +
        // breathing room below the last row. The PANEL heights are the single
        // source for measurement AND painting (each painter ends exactly at
        // BodyTop + its panel height; the parity seam PaintedContentBottom is
        // asserted against it in tests/layout_fit.cs).
        int ContentHeightFor(int tab, int bodyW)
        {
            int panel = PanelHeightFor(tab, bodyW);
            return BodyTop() + panel + Gap;
        }

        int PanelHeightFor(int tab, int bodyW)
        {
            return tab == TabCli ? ConnectionsPanelHeight(bodyW)
                : tab == TabTray ? TrayPanelHeight(bodyW)
                : tab == TabSettings ? SettingsPanelHeight(bodyW)
                : AccountsPanelHeight(bodyW);
        }

        // The scroll state for one paint: content measured, scrollbar decided,
        // scroll clamped. Width feeds back: a visible scrollbar reserves its
        // strip from the body width, which can only ever need MORE height, so
        // one resolution pass is stable.
        int ResolveScroll(int w, out Rectangle viewport, out bool showBar)
        {
            viewport = BodyViewport();
            int bodyH = viewport.Height;
            int contentFull = ContentHeightFor(Tab, w);
            showBar = contentFull > BodyBottom() + 1;
            int bodyW = showBar ? w - ScrollbarW : w;
            int content = showBar ? ContentHeightFor(Tab, bodyW) : contentFull;
            // Content height is measured from the client top (the painters
            // start at BodyTop), so the scroll maximum is measured against the
            // viewport's absolute bottom, never against the viewport height
            // alone — that would scroll every tab by BodyTop too far.
            MaxScroll = Math.Max(0, content - BodyBottom());
            if (TabScroll[Tab] > MaxScroll) TabScroll[Tab] = MaxScroll;
            if (TabScroll[Tab] < 0) TabScroll[Tab] = 0;
            if (showBar) viewport.Width = bodyW;
            return TabScroll[Tab];
        }

        // ── panel heights: the measure half of the measure==paint invariant ──
        // All four take the body width, because compactness and wrapping are
        // width-dependent. PaintAccounts/PaintInstallPanel/PaintTrayPanel/
        // PaintSettingsPanel must end exactly at BodyTop + the matching height
        // — PaintedContentBottom records the painted truth and layout_fit
        // asserts the two never drift.

        int AccountsPanelHeight(int w)
        {
            // PERF-004: inside a paint the snapshot owns the lists — the
            // measure half of measure==paint must not rebuild them.
            AccountsLayout layout = PaintLayout;
            List<AccountData> cards = layout != null ? layout.Cards : VisibleAccounts();
            if (cards.Count == 0) return 26;
            int total = 0;
            foreach (AccountData a in cards)
                total += CardHeight + CardLines(a, w) * RowH + Gap + Gap;
            return total;
        }

        // A CLI card grows a second command line when the PowerShell command
        // cannot fit one line at this width — measured with the same 10pt font
        // the painter draws with.
        int CliCardHeight(CliInfo cli, int w)
        {
            int cw = w - 16;
            int extra = 0;
            using (var probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
                if (TextWidth(g, cli.PowerShell, 10) > cw - 20) extra = RowH;
            return CliCardH + extra;
        }

        internal int ConnectionsPanelHeight(int w)
        {
            if (Connections.Count == 0) BuildConnections();
            int total = 20;
            foreach (var c in Connections) total += ConnectionCardHeight(c, w) + Gap;
            return total;
        }

        int InstallPanelHeight(int w) { return ConnectionsPanelHeight(w); }

        internal int ConnectionCardHeight(VendorConnection c, int w)
        {
            bool expanded = ExpandedVendors.Contains(c.VendorId);
            int baseH = 62;
            if (expanded)
            {
                // Discovery + Auth + Connectivity + Quota (4), plus Version
                // when the adapter read one, plus path and duplicates rows.
                baseH += 52;
                int lines = 3;   // auth + connectivity + quota beyond discovery
                if (!string.IsNullOrEmpty(c.CliVersion)) lines++;
                if (!string.IsNullOrEmpty(c.Plan)) lines++;
                if (!string.IsNullOrEmpty(c.ResolvedPath)) lines++;
                if (c.DuplicatePaths != null && c.DuplicatePaths.Count > 1) lines++;
                if (c.VendorId == "codex" && c.DuplicateRemoteHomeIds.Count > 0) lines++;
                if (c.VendorId == "codex" && c.UnverifiedRemoteHomeIds.Count > 0) lines++;
                lines += CodexDuplicateNotes(c).Count;
                baseH += lines * RowH;
                baseH += 28;
                // "Retry different account" gets its own row rather than being
                // squeezed onto the end of the first one, where a narrow panel
                // would clip it.
                if (ShowsRetryDifferentAccount(c)) baseH += 28;
            }
            return baseH;
        }

        // The duplicate state explained on the card itself. ONE list, consumed
        // by both the height measurement and the paint, so the card can never
        // be measured shorter than what it draws.
        internal static List<string> CodexDuplicateNotes(VendorConnection c)
        {
            var lines = new List<string>();
            if (!IsCodexDuplicate(c)) return lines;
            lines.Add("Signed in successfully — as an account already listed, so no extra Codex card was added.");
            lines.Add("Retry reuses that same Codex home; choose a DIFFERENT ChatGPT account when authorizing.");
            // Ephemeral: present only while a device authorization is live.
            string code = CodexDeviceCodePrompt.ActiveCode;
            if (code.Length > 0)
                lines.Add("Device code " + code + " — type it on the page that opened, signed in as the other account.");
            return lines;
        }

        static bool IsCodexDuplicate(VendorConnection c)
        {
            return c != null && c.VendorId == "codex"
                && (c.DuplicateRemoteHomeIds.Count > 0
                    || c.State == ConnectionState.DuplicateRemoteAccount);
        }

        static bool ShowsRetryDifferentAccount(VendorConnection c)
        {
            return IsCodexDuplicate(c)
                && ConnectionAdapterRegistry.RetryDifferentAccount.ContainsKey(c.VendorId);
        }

        int TrayPanelHeight(int w)
        {
            // PERF-004: inside a tray paint the snapshot owns the list.
            int rows = TrayPaintAll != null ? TrayPaintAll.Count : AllMetrics().Count;
            if (rows == 0) return 20 + 28 + PickRowH;
            return 20 + 28 + 16 + rows * PickRowH;
        }

        // Measured by RUNNING the real settings painter against a scratch
        // 1x1 bitmap with registration suppressed — the measure and the paint
        // are the same code, so they cannot disagree. Cached per width + the
        // few inputs the settings layout actually depends on; every other
        // paint reuses the cached number.
        int SettingsPanelHeight(int w)
        {
            int themesCount = Themes == null ? 0 : Themes.Count;
            int errors = Theme.LoadErrors.Count;
            // The Autostart label grows when the reconciler could not verify —
            // a wider button can wrap the controls row, so it is part of the
            // layout signature.
            int autoKey = AutostartNotApplied ? 1 : 0;
            if (w == SettingsMeasuredW && themesCount == SettingsMeasuredThemes
                && errors == SettingsMeasuredErrors && autoKey == SettingsMeasuredAuto
                && SettingsMeasuredH > 0)
                return SettingsMeasuredH;
            int h;
            using (var probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                bool saved = Measuring;
                Measuring = true;
                SetRegistrySilent(true);
                try { h = PaintSettingsBlocks(g, BodyTop(), w) - BodyTop(); }
                finally { Measuring = saved; SetRegistrySilent(false); }
            }
            SettingsMeasuredW = w; SettingsMeasuredThemes = themesCount;
            SettingsMeasuredErrors = errors; SettingsMeasuredAuto = autoKey;
            SettingsMeasuredH = h;
            return h;
        }
        int SettingsMeasuredW = -1, SettingsMeasuredThemes = -1, SettingsMeasuredErrors = -1,
            SettingsMeasuredAuto = -1, SettingsMeasuredH;

        // The measure pass runs the real painter, so the real registry lists
        // must be silent while it runs.
        void SetRegistrySilent(bool silent)
        {
            Buttons.Silent = ButtonActions.Silent = Marks.Silent = Cropped.Silent
                = HintZones.Silent = HintTexts.Silent = ItemRows.Silent = ItemRowIds.Silent = silent;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11,
                HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
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
                if (p.X < 0 || p.Y < 0 || p.X >= Width || p.Y >= Height) return;
                // T-42: the user resizes from all four edges and all four
                // corners. Corners win over straight edges; both win over the
                // caption strip. The close button must stay a button, so its
                // rectangle is exempt from the top-right corner zone.
                bool left = p.X < ResizeEdge, right = p.X >= Width - ResizeEdge;
                bool top = p.Y < ResizeEdge, bottom = p.Y >= Height - ResizeEdge;
                // The close button must stay a button, so its rectangle is
                // exempt from the top-right corner zone.
                var close = new Rectangle(Width - 22, 1, 20, 20);
                if (close.Contains(p)) { m.Result = (IntPtr)HTCLIENT; return; }
                if (top && left) { m.Result = (IntPtr)HTTOPLEFT; return; }
                if (top && right) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                if (bottom && left) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                if (bottom && right) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                if (left) { m.Result = (IntPtr)HTLEFT; return; }
                if (right) { m.Result = (IntPtr)HTRIGHT; return; }
                if (top) { m.Result = (IntPtr)HTTOP; return; }
                if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }
                if (p.Y < HeaderH && p.X < Width - 22) m.Result = (IntPtr)HTCAPTION;
            }
        }

        public void HideToTray()
        {
            // R020: an incidental geometry write must not decide a settings
            // conflict the user has not retried — the save persists geometry
            // against the durable baseline while a dirty conflict is open.
            SaveWindowGeometry();
            Hide();
        }

        // One geometry-save boundary: the end of a resize or move gesture.
        // Nothing writes the ini while the mouse moves — the write storm is
        // exactly what the ResizeEnd boundary exists to prevent.
        void SaveWindowGeometry()
        {
            Settings.WindowX = Location.X; Settings.WindowY = Location.Y;
            Settings.WindowW = ClientSize.Width;
            // Zero means "auto": persisting the fitted height would turn an
            // incidental save (hide to tray, a right-drag move) into the user
            // choosing a height they never chose.
            Settings.WindowH = AutoHeight ? 0 : ClientSize.Height;
            Settings.SavePosition();
        }

        // W2-003/R021: re-read LIMISAW.ini and re-apply whatever runtime state
        // the new values imply — every piece of derived state Settings owns,
        // through the ONE applier below. Exactly once per actual change: a file
        // that says what the running app already believes costs one read and
        // nothing else, so the periodic sweep does not repaint the window every
        // five minutes for no reason. A refused reload (dirty + unchanged stale
        // disk) says nothing and clobbers nothing.
        void ReloadSettings()
        {
            int lowBefore = Settings.LowPct;
            bool zcodeBefore = Settings.ZcodeReadConfig;
            SettingsReloadResult r;
            // A locked or vanished ini is not worth losing a sweep over: the
            // in-memory settings stay authoritative and the refresh continues.
            try { r = Settings.ReloadEx(); }
            catch { return; }
            // Note == null covers all three silent outcomes: identical, dirty
            // retained against the same stale disk, unreadable. None of them
            // may look like a reload.
            if (r.Note == null) return;
            bool lowMoved = Settings.LowPct != lowBefore;
            // R032/R044 + CORE-002 (audit/6): the config-access permission is
            // a CREDENTIAL-AUTHORITY change for the ZCode connection — the
            // old verdict is stale AND any verification still running under
            // the previous permission must lose its publication authority.
            // InvalidateOperation does all of it atomically; the matching
            // watcher (if any) is cancelled by generation. Unrelated settings
            // changes never touch it.
            if (Settings.ZcodeReadConfig != zcodeBefore)
            {
                int zg = ConnCoordinator.CurrentGeneration("zcode");
                ConnCoordinator.InvalidateOperation("zcode");
                ConnectionWatcher.CancelGeneration("zcode", zg);
            }
            Action apply = () =>
            {
                ApplySettingsSnapshot(lowMoved);
                Note = r.Note;
                Refresh();
            };
            try { if (InvokeRequired) BeginInvoke(apply); else apply(); }
            catch { }
        }

        // R021: the ONE place that turns an accepted settings snapshot into
        // live runtime state. Startup, external reload and any future
        // reconciliation path share it, so the tray, the window, the timer and
        // the registry can never disagree about which snapshot is current.
        // Deliberately NOT here: window relocation (WindowX/Y moving externally
        // must not teleport the window) and probe restarts (cosmetics never
        // justify re-running vendors). The caller keeps the ordering contract:
        // this runs BEFORE the provider sweep, because ZcodeReadConfig decides
        // what the upcoming sweep may ask for.
        void ApplySettingsSnapshot(bool lowMoved)
        {
            // CORE-001 (audit/7): this is where an accepted settings revision
            // becomes live runtime state (theme, tray, timer, autostart,
            // connections). It is form-owned, so the thread that runs it is
            // recorded for the harness. Reached only through the UI boundary.
            SettingsApplyThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            ApplyTheme(Settings.ThemeSlug);
            TopMost = Settings.AlwaysOnTop;
            RefreshTimer.Interval = Math.Max(1, Settings.RefreshSeconds) * 1000;
            if (lowMoved) NotifiedLow.Clear();
            ReconcileAutostartNow();
            BuildConnections();
            RefreshSoundDisplay();
            UpdateTray();
        }

        public void RefreshData()
        {
            // W2-003: once shutdown has begun no new sweep is ever scheduled —
            // the timer is stopped and every entry point refuses. The coalesced
            // follow-up is cleared by CompleteSweep and guarded there too.
            if (ShuttingDown) return;
            // CORE-001 (audit/7): which thread actually BEGAN this refresh. Set
            // before ReloadSettings so the harness proves the UI-ownership check
            // governs the whole transition, not merely the final repaint. A
            // connection-originated request that reached here from a worker would
            // record the worker's id.
            RefreshEntryThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            // W2-003: the documented workflow is "edit LIMISAW.ini, press
            // Refresh", so Refresh is where the file is re-read. Done before the
            // sweep starts, because ZcodeReadConfig decides what this very sweep
            // is allowed to ask for.
            ReloadSettings();
            // W2-001: coalesce, never drop. The reset action relies on this
            // call happening after it completes; a silent return here would
            // publish a pre-reset snapshot as final. CORE-004: this compound
            // admission runs under SweepGate so it cannot interleave with a
            // worker's abandoned-flight release; the shutdown gate is re-checked
            // inside the lock because BeginShutdown clears PendingRefresh.
            lock (SweepGate)
            {
                if (ShuttingDown) return;
                if (Refreshing) { PendingRefresh = true; return; }
                Refreshing = true;
            }
            ActiveSweepGeneration = SweepGeneration;
            // PERF-003 (SRC-006:R020): an explicit/new Refresh creates a NEW
            // discovery generation. The bump itself is free; the worker below
            // rebuilds the snapshot off this thread before the probes run.
            ExecutableDiscovery.BeginGeneration();
            LastError = ""; Refresh();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // PERF-003 (SRC-006:R020): the generation snapshot is built
                    // HERE — off the UI thread, before any vendor probe runs —
                    // so the probes, Cli.Status and the connection projection
                    // all reuse one secret-free discovery pass. Test sweeps
                    // (SweepSource) keep their own deterministic bodies.
                    if (SweepSource == null) ExecutableDiscovery.EnsureGeneration();
                    // The probe runs IN this process (Probe.cs / ProbeClaude.cs /
                    // ProbeAntigravity.cs / ProbeZcode.cs): no interpreter, no
                    // script folder, no child of our own to time out. Each vendor
                    // CLI still gets a hard deadline of its own, and Probe.Run's
                    // total budget is what bounds the sweep.
                    ProbeResult snapshot = SweepSource != null
                        ? SweepSource(Settings.ZcodeReadConfig)
                        : Probe.Run(Settings.ZcodeReadConfig, Settings.FreebuffReadConfig);
                    // W2-003: the result of a sweep the application has since
                    // invalidated (shutdown, or a generation bump) is discarded
                    // here, before it can reach Publish, the tray or the audio
                    // owner. Apply re-checks on the UI thread for the marshal
                    // window. CORE-004: an invalidated result is an ABANDONED
                    // flight — releasing it must not become a worker-side
                    // shortcut into UI completion (AutoFitHeight/Refresh/
                    // UpdateTray/CompleteSweep) or an undeliverable follow-up.
                    if (ShuttingDown || ActiveSweepGeneration != SweepGeneration) { AbandonSweep(); return; }
                    Apply(snapshot);
                }
                catch (Exception ex)
                {
                    if (ShuttingDown || ActiveSweepGeneration != SweepGeneration) AbandonSweep();
                    else SetError(ex.Message);
                }
            });
        }

        // CORE-001 (audit/7): the ONE UI-dispatch boundary for form-owned
        // refresh requests. The connection layer runs its vendor/network work on
        // ThreadPool workers, and a successful completion asks for a follow-up
        // quota sweep. That request used to call RefreshData directly, so the
        // whole state-owner transition (ReloadSettings, Refreshing,
        // PendingRefresh, ActiveSweepGeneration, ExecutableDiscovery.BeginGeneration,
        // LastError, Refresh) ran on the worker — a direct WinForms call from a
        // non-UI thread and a race on state the rest of the form treats as
        // UI-owned. The gate order mirrors MarshalConnectionRepaint exactly:
        //   1. any teardown state (shutdown gate, disposed, disposing) -> drop;
        //   2. no handle -> drop;
        //   3. handle live: marshal to the UI thread when needed, executing
        //      directly ONLY on the UI thread; if the handle vanishes before
        //      BeginInvoke, the request is dropped, never run on the worker.
        // The ownership check runs BEFORE ReloadSettings or any RefreshData
        // state mutation, because the whole transition belongs to the UI thread.
        void RequestRefresh()
        {
            if (ShuttingDown || IsDisposed || Disposing) return;
            if (!IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke((Action)RefreshData);
                else RefreshData();
            }
            catch { } // handle disappeared between the check and BeginInvoke: drop
        }

        // CORE-004 (audit/7): the ONE UI-dispatch boundary for a sweep's RESULT.
        // Apply/SetError used to attempt BeginInvoke and, on failure, fall
        // through into Publish(snapshot)/fail() ON THE SWEEP WORKER — the exact
        // worker-side publication model PERF-004 removed, resurrected by a
        // handle-loss race. The policy mirrors RequestRefresh /
        // MarshalConnectionRepaint: teardown state, no handle, or a vanished
        // handle all mean DROP; inline execution happens ONLY on the live UI
        // thread; a failed worker dispatch reports back to the caller so it can
        // release the abandoned flight instead of pretending it published.
        // Returns true when the effect was accepted (queued or run inline),
        // false when there is no valid UI owner.
        //
        // VerifyDispatchHook is a production-null seam executed immediately
        // before the real BeginInvoke attempt (and, on the UI-thread path,
        // before the direct execution) so a harness can destroy/recreate the
        // handle or force the dispatch itself to throw. Null in production,
        // assigned by tests only — same convention as PlayBackend/PublishGate.
        internal static Action VerifyDispatchHook = null;

        bool RunOnUiThread(Action work)
        {
            if (ShuttingDown || IsDisposed || Disposing) return false;
            if (!IsHandleCreated) return false;
            try
            {
                if (InvokeRequired)
                {
                    if (VerifyDispatchHook != null) VerifyDispatchHook();
                    BeginInvoke(work);
                }
                else
                {
                    if (VerifyDispatchHook != null) VerifyDispatchHook();
                    work();
                }
                return true;
            }
            catch { return false; } // handle vanished before/during dispatch: no owner
        }

        // W2-003: the single shutdown authority. Called from the real exit path
        // (Application.Exit -> FormClosing) BEFORE Application.Run returns and
        // before any teardown; idempotent. It stops scheduling, invalidates the
        // in-flight sweep generation, and shuts down the connection layer —
        // the watcher FIRST (W2-002), so no queued or captured watcher work can
        // begin vendor I/O after the gate wins — so no late result can touch
        // form state, the tray or the audio owner. Close-to-tray is deliberately
        // NOT this path: UserClosing is cancelled and background refresh keeps
        // running there.
        internal void BeginShutdown()
        {
            if (ShuttingDown) return;
            ShuttingDown = true;
            // Invalidate every sweep that is already in flight.
            SweepGeneration++;
            PendingRefresh = false;
            try { if (RefreshTimer != null) RefreshTimer.Stop(); } catch { }
            // W2-002/R010: BeginShutdown OWNS the watcher lifetime. Kill it at
            // the boundary, so no due operation survives into teardown and no
            // vendor verification may begin after the gate is set.
            try { ConnectionWatcher.Shutdown(); } catch { }
            // A managed vendor login that is still pending belongs to an
            // operation that just died with the watcher. It is released inside
            // the same bounded teardown: only sessions that are ALREADY live
            // are told (no app-server child is started to cancel a dead
            // attempt). Whatever cannot be told is fenced diagnostically; the
            // coordinator shutdown gate rejects a browser completion afterward.
            try { CodexManagedLogin.ShutdownPending(Stamp.Now + 2); } catch { }
            // W2-003/R011: BeginShutdown also owns the exit-observation
            // resources. Detaching/disposing the observers drops ONLY LIMISAW's
            // wrappers and subscriptions — a user-owned interactive login
            // child is never touched, and the normal bounded watcher cadence
            // (already stopped) is the only thing observation accelerated.
            try { ConnectionProcessLauncher.ShutdownObservers(); } catch { }
            // CORE-004's coordinator is shut down by the same lifecycle owner, so
            // an in-flight ZCode verification is invalidated with the sweeps.
            try { ConnCoordinator.Shutdown(); } catch { }
        }

        void SetError(string msg)
        {
            // PERF-004: raised from the sweep's own thread, so the state it
            // publishes crosses to the UI thread with the repaint it needs.
            Action fail = () =>
            {
                // CORE-004: the same observable ownership seam PublishThread is
                // for a result, so a regression that lets a failed worker
                // dispatch mutate Stale/LastError on the worker is caught by
                // value (it stays 0) rather than by source shape.
                ErrorPublishThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                Stale = true; LastError = msg;
                // W2-002: the sweep is ended through the SAME completion as a
                // successful Publish, so a refresh queued while the sweep was
                // throwing is retried instead of stranded.
                CompleteSweep();
            };
            // CORE-004: the same single UI-dispatch boundary every other
            // publication uses. A failed worker dispatch NEVER falls through to
            // fail() — that was the worker-side Stale/LastError mutation and
            // worker-side CompleteSweep the audit found. An undeliverable error
            // releases only the abandoned flight.
            if (!RunOnUiThread(fail)) AbandonSweep();
        }

        // PERF-004: the sweep runs on a ThreadPool thread, but everything it
        // PUBLISHES is UI-owned state — the account list the paint walks, the
        // NotifiedLow suppression dictionary, the tray icon, the balloon. Those
        // were mutated straight from the worker while the UI thread could be
        // clearing the same dictionary from a slider release, which is a torn
        // Dictionary (an enumeration in RearmLowAlerts throwing
        // InvalidOperationException at best, a lost entry at worst). The result
        // is the ONLY thing that crosses the thread boundary now.
        void Apply(ProbeResult snapshot)
        {
            // W2-003: a result invalidated by shutdown (or a generation bump)
            // must not publish. Re-checked here because the sweep thread may
            // have passed its own check just before shutdown began. CORE-004:
            // the discard is an ABANDONED flight, not a UI completion — an
            // invalidated worker must not repaint the tray or start a follow-up.
            if (ShuttingDown || ActiveSweepGeneration != SweepGeneration) { AbandonSweep(); return; }
            // CORE-004: one UI-dispatch boundary. If it cannot hand the result
            // to a live UI owner the snapshot is DISCARDED — never published on
            // the worker — and only the minimum sweep-flight bookkeeping is
            // released so Refreshing cannot wedge.
            if (!RunOnUiThread(() => Publish(snapshot))) AbandonSweep();
        }

        // Which thread the last publication actually ran on. Set here rather than
        // asserted in a comment: tests\apply_thread.cs compares it with the UI
        // thread, because a regression to the old worker-thread mutation is
        // invisible until a Dictionary tears in front of a user.
        int PublishThread;

        // CORE-004: which thread the last ERROR publication actually ran on.
        // Same contract as PublishThread: it must be the UI thread, and a
        // failed worker dispatch must leave it unset (0), never the worker.
        int ErrorPublishThread;

        // CORE-001 (audit/7): observable ownership seams for the connection
        // refresh boundary. Each records the thread that actually ran the
        // form-owned transition, so a regression that re-introduces worker-side
        // execution of any of them is caught by value, not by source shape.
        //   RefreshEntryThread   — the thread that began RefreshData (before
        //                          ReloadSettings or any state mutation);
        //   SettingsApplyThread  — the thread that applied an accepted settings
        //                          snapshot (ApplySettingsSnapshot);
        //   RepaintThread        — the thread that ran the connection repaint
        //                          (BuildConnections/Refresh/UpdateTray).
        int RefreshEntryThread;
        int SettingsApplyThread;
        int RepaintThread;

        void Publish(ProbeResult snapshot)
        {
            // W2-003: the final gate. BeginShutdown may have run between the
            // worker's check and this message being pumped, so the UI-thread
            // entry point re-checks before touching any form state, the tray or
            // the audio owner.
            if (ShuttingDown || ActiveSweepGeneration != SweepGeneration) { CompleteSweep(); return; }
            PublishThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            try
            {
                if (snapshot.Clis.Count > 0) Clis = snapshot.Clis;
                PrevAccounts = Accounts; Accounts = CarryForward(snapshot.Accounts, PrevAccounts);
                DuplicateCodexHomes = snapshot.DuplicateCodexHomes ?? new List<string>();
                UnverifiedCodexHomes = snapshot.UnverifiedCodexHomes ?? new List<string>();
                // PERF-005: a fresh publication is the popup's main content
                // change — the next hover rebuilds once.
                BumpPopupData();
                // CORE-001: project the fresh probe truth into the coordinator
                // BEFORE the projection step reads it, so a successful read
                // converges the card to Connected.
                ProjectProbeConnections();
                BuildConnections();
                Stale = false; LastError = ""; LastFetch = DateTime.Now.ToString("HH:mm:ss");
                DetectResets();
                RearmLowAlerts();
                DetectLow();
            }
            catch (Exception ex) { Stale = true; LastError = ex.Message; }
            finally
            {
                CompleteSweep();
            }
        }

        // The one end-of-sweep owner, reached from Publish's finally and from
        // SetError. Clears the in-flight flag, then runs exactly one coalesced
        // follow-up if any request arrived while the sweep was running — the
        // reset re-read and the manual button must survive a throwing sweep,
        // not just a clean one.
        void CompleteSweep()
        {
            bool runAgain;
            lock (SweepGate)
            {
                // CORE-004: a UI completion that races a worker's AbandonSweep
                // must not double-release or revive a dead flight. If the flight
                // was already abandoned (or shutdown cleared it), there is
                // nothing to complete here.
                if (!Refreshing) return;
                Refreshing = false;
                // W2-001: the coalesced request runs after this sweep's state is
                // fully published or the failure recorded. Multiple requests
                // during one sweep collapse into ONE follow-up, never a queue.
                runAgain = PendingRefresh;
                PendingRefresh = false;
            }
            // W2-003: after shutdown there is no follow-up and no repaint/tray
            // touch — the form is dying and the pending request is dropped.
            if (ShuttingDown) return;
            // T-42: a sweep never resizes a window the USER has sized — an
            // account appearing or disappearing changes the tab's scrollable
            // content, not their window. The paint clamps the active scroll to
            // the new maximum. A window still on the auto default follows its
            // content instead, so the first sweep opens at the right height.
            if (IsHandleCreated) { try { AutoFitHeight(); Refresh(); UpdateTray(); } catch { } }
            if (runAgain) RefreshData();
        }

        // CORE-004 (audit/7): the abandoned-sweep completion. Reached ONLY when
        // a sweep's result can no longer be handed to a live UI owner — the
        // handle vanished before dispatch, the form is disposing, or shutdown
        // invalidated the generation. It performs NO UI work whatsoever: no
        // Publish, no Stale/LastError mutation, no AutoFitHeight/Refresh/
        // UpdateTray, no follow-up sweep. Its single job is to release the
        // in-flight bookkeeping under SweepGate so Refreshing cannot remain
        // permanently true, and to discard any coalesced PendingRefresh that
        // could otherwise be run by a worker. Idempotent and safe against
        // BeginShutdown racing the release.
        void AbandonSweep()
        {
            lock (SweepGate)
            {
                if (!Refreshing) return;
                Refreshing = false;
                PendingRefresh = false;
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
                    // W2-003: an unverified reset is THIS sweep's own fact —
                    // carrying the previous blocked card would both hide the
                    // event and keep drawing numbers the clock disproved.
                    if (a.ResetUnverified) break;
                    a.Windows = old.Windows;
                    // An absolute balance is a READING too: carrying windows
                    // without it would drop FreeBucks from a hiccuping card.
                    if (a.Balances.Count == 0 && old.Balances.Count > 0) a.Balances = old.Balances;
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
                // W2-003: an Antigravity refusal whose reset passed inside the
                // grace never produces a readable window, so the ordinary
                // percentage comparison below cannot see it. The EVENT is the
                // transition: previous sweep blocked at 0% on this exact reset
                // stamp, this sweep says the stamp has passed. That the quota
                // is now UNVERIFIED is the honest announcement — the balloon
                // must not promise a full refill nothing measured.
                if (cur.ResetUnverified)
                {
                    foreach (WindowData w in cur.Windows)
                    {
                        // CORE-013: the event is keyed by the reset INSTANT.
                        // A missing epoch stays unknown — no timestamp is
                        // invented from the display string.
                        if (!w.ResetEpoch.HasValue) continue;
                        WindowData pw = prev.Find(w.Key);
                        if (pw == null || !pw.ResetEpoch.HasValue
                            || pw.ResetEpoch.Value != w.ResetEpoch.Value) continue;
                        if (pw.Available && pw.Rem > 0) continue;   // not a blocked card
                        CheckUnverifiedReset(cur, w);
                    }
                    continue;
                }
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
            // CORE-013: identity is the absolute instant, not its rendering.
            if (!cur.ResetEpoch.HasValue || !prev.ResetEpoch.HasValue) return;
            if (cur.ResetEpoch.Value == prev.ResetEpoch.Value
                && cur.Rem <= prev.Rem + 25) return; // no meaningful change
            if (cur.Rem <= prev.Rem + 25) return; // dropped or negligible
            string key = acc.Key + "_" + cur.Key + "_" + Stamp.Token(cur.ResetEpoch);
            if (NotifiedResetKeys.Contains(key)) return;
            NotifiedResetKeys.Add(key);
            if (NotifiedResetKeys.Count > 100) NotifiedResetKeys.RemoveRange(0, 50);
            Notify(new ResetEvent
            {
                AccountLabel = AccountTitle(acc), LimitLabel = WindowTitle(cur),
                NewRemaining = cur.Rem, ResetAt = cur.Reset, LockedByWeekly = locked,
            });
        }

        // W2-003: the unverified-reset branch of DetectResets. Same once-per-
        // event suppression as CheckReset, but the announcement says the truth:
        // the block ended, the new quota is unknown until the next refusal or
        // a CLI that reports numbers.
        void CheckUnverifiedReset(AccountData acc, WindowData w)
        {
            string key = acc.Key + "_" + w.Key + "_" + Stamp.Token(w.ResetEpoch) + "_unverified";
            if (NotifiedResetKeys.Contains(key)) return;
            NotifiedResetKeys.Add(key);
            if (NotifiedResetKeys.Count > 100) NotifiedResetKeys.RemoveRange(0, 50);
            Notify(new ResetEvent
            {
                AccountLabel = AccountTitle(acc), LimitLabel = WindowTitle(w),
                ResetAt = w.Reset, Unverified = true,
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
                string text;
                if (ev.Unverified)
                    text = ev.LimitLabel + " limit reset — quota unverified until the next report";
                else if (ev.LockedByWeekly)
                    text = ev.LimitLabel + " limit reset — still 0% usable, locked by a longer window";
                else
                    text = ev.LimitLabel + " limit reset — " + ev.NewRemaining + "% remaining";
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

        // A cue that could not be played leaves its reason in the footer. The
        // alert itself already happened (the balloon is up), so this is a note
        // about the sound and not an error about the quota — but it has to be
        // visible, or a muted chime looks exactly like a quiet account.
        //
        // T-40/R022: the alert SUBMITS and continues — DetectResets/DetectLow
        // run inside the UI-thread state commit and must never block on
        // File.ReadAllBytes, scaling, the cache build or SoundPlayer.Load. The
        // completion delivers the outcome on the UI thread; an alert failure
        // stays visible unless a NEWER cue has already replaced the note.
        long LatestCueSeq;

        void Play(string file)
        {
            long seq = -1;
            seq = SoundCue.Submit(RootPath, Settings.SoundDir, file, Settings.SoundVolume, "alert",
                why => BeginInvoke((Action)(() =>
                {
                    if (seq != LatestCueSeq) return;    // superseded: a newer cue owns the note
                    SoundNote = why == null ? "" : "Alert sound: " + why;
                    if (why == null) return;
                    try { Note = SoundNote; Refresh(); } catch { }
                })));
            LatestCueSeq = seq;
        }

        // Last sound failure, kept so the footer can restate it after a repaint
        // clears Note. Cleared by the next cue that plays.
        string SoundNote = "";

        // "You are down to the last few percent" is a different alert from a
        // reset: it fires once per quota window per cycle, not once per refresh.
        //
        // CORE-005: the EVENT is armed when either channel is on, because the
        // suppression record is per event and not per channel — deciding here
        // that a chime-only user has no low alert is what made the two switches
        // one switch.
        void DetectLow()
        {
            if (!Settings.NotifyLow && !Settings.LowSound) return;
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
                    // CORE-013: the record is the invariant epoch token, not a
                    // local rendering. A window with no epoch stays unknown;
                    // the Reset-string branch exists only for fabricated
                    // WindowData that never crossed Flatten (Flatten leaves
                    // Reset null whenever the epoch is absent).
                    string stamp = w.ResetEpoch.HasValue
                        ? Stamp.Token(w.ResetEpoch) : (w.Reset ?? "");
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
        //
        // CORE-013: the movement is measured on the epoch token, never by
        // reparsing an offset-less wall-clock string — a DST fall-back renders
        // two distinct instants to the same text and a rolled hour would shift
        // the difference either way.
        static bool NewCycle(WindowData w, string alertedFor, string now)
        {
            if (alertedFor == now) return false;
            if (w.DurationMinutes <= 0) return false;
            double? before = Stamp.FromToken(alertedFor);
            if (!before.HasValue)
            {
                // Fabricated WindowData (tests, previews) may still hold an Iso
                // stamp; a genuine Flatten product never lands here, because its
                // stamp is a token. This is compatibility, not authority.
                before = Stamp.Epoch(alertedFor);
                if (!before.HasValue) return false;
            }
            double? after = Stamp.FromToken(now);
            if (!after.HasValue) { after = Stamp.Epoch(now); }
            if (!after.HasValue) return false;
            return after.Value - before.Value >= w.DurationMinutes * 30.0;   // half, in seconds
        }

        void NotifyLowAlert(AccountData acc, WindowData w)
        {
            try
            {
                string title = "LIMISAW — " + AccountTitle(acc);
                string text = WindowTitle(w) + " — only " + w.Rem + "% left"
                    + (w.ResetEpoch.HasValue ? ", resets " + FriendlyTime(w.ResetEpoch) : "");
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
                // CORE-005: each half asks its own switch, the same way the
                // refill alert does. A balloon with the chime muted and a chime
                // with no balloon are both reachable.
                if (Settings.NotifyLow) { if (InvokeRequired) BeginInvoke(show); else show(); }
                if (Settings.LowSound) Play(Settings.LowSoundFile);
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
        // The one place a tray reading's id is minted. CORE-001: it is built
        // from the account's STABLE key, never its display label — two Codex
        // homes can both be called "Codex", and the label of one changes the
        // moment the other is discovered (DisambiguateLabels), so a saved pin,
        // order or hide keyed by the label pointed at a different reading after
        // a restart, or at nothing at all. It also has to be the same rule the
        // migration resolves to (MigrateMetricId -> AccountData.Key), or a
        // migrated id could never match a live one.
        public static string MetricId(AccountData a, WindowData w)
        {
            return a.Key + "/" + w.Key;
        }

        // Every account+window pair is a selectable tray reading, plus the
        // synthetic "lowest". Ids are account-key/window so a saved choice
        // survives a restart and new vendors need no new code.
        public List<Metric> AllMetrics()
        {
            AllMetricsCalls++;             // PERF-004 cost seam
            var list = new List<Metric>();
            foreach (AccountData a in Accounts)
                foreach (WindowData w in a.Windows)
                    list.Add(new Metric
                    {
                        Id = MetricId(a, w),
                        Label = AccountTitle(a) + " · " + WindowTitle(w),
                        Short = ShortName(a) + "·" + w.Label,
                        // A carried-forward reading still counts. Dropping it
                        // would make the tray jump UP when a vendor CLI hiccups,
                        // i.e. report more quota than the user has — the one
                        // direction that must never happen silently.
                        Value = w.Rem, Available = (a.Ok || a.Carried) && w.Available,
                        IsShort = w.DurationMinutes > 0 ? w.DurationMinutes <= 300 : w.Base == "five_hour",
                        ResetEpoch = w.ResetEpoch,
                    });
            return list;
        }

        // What the TRAY actually draws: the user's explicit order first, then
        // any newly discovered reading, minus the hidden ones, capped at
        // TrayMax. Four vendors' worth of windows cannot fit in 16 pixels, so
        // the cap is the whole point - the alternative is unreadable mush.
        public List<Metric> TrayMetrics()
        {
            return BuildModel().Items;
        }

        // One immutable snapshot of the live state.
        TrayModel BuildModel()
        {
            TraySnapshots++;
            // PERF-004: the snapshot reuse. When a paint pass is building its
            // per-paint model, AllMetrics was already taken ONCE by the paint
            // — derive from that same collection instead of walking the fleet
            // again. Outside a paint this stays the plain live build.
            List<Metric> all = TrayPaintAll ?? AllMetrics();
            return new TrayModel
            {
                All = all,
                Items = SelectedMetrics(all),
                Pin = Settings.TrayMetric,
                Stale = Stale,
            };
        }

        // The Settings preview's model: the same selection shape, every reading
        // reporting the pretend level. Nothing here mutates live state — the old
        // code set a PreviewPct field and restored it in a finally, which made
        // the real tray's data depend on who was rendering at the time.
        internal TrayModel PreviewModel(int pct)
        {
            return PreviewModel(pct, SelectedMetrics());
        }

        // PERF-001 (SRC-006:R018): the Settings preview derived from the metric
        // snapshot the paint already owns, so a Settings paint never
        // materializes a second AllMetrics just for the preview row.
        internal TrayModel PreviewModel(int pct, List<Metric> all)
        {
            List<Metric> picked = SelectedMetrics(all);
            if (picked.Count == 0)
            {
                // Nothing discovered yet (first launch, no vendor logged in)
                // still deserves a preview, so the shapes are inventable.
                int n = Settings.TrayMode == "single" || Settings.TrayMode == "dual" ? 2 : 4;
                for (int i = 0; i < n; i++)
                    picked.Add(new Metric
                    {
                        Id = "preview/" + i, Label = "preview", Short = "pv",
                        Value = pct, Available = true, IsShort = i % 2 == 0,
                        ResetEpoch = PreviewResetEpoch(pct),
                    });
                return new TrayModel { All = picked, Items = picked, Pin = "lowest", Stale = Stale };
            }
            var faked = new List<Metric>();
            foreach (Metric m in picked)
                faked.Add(new Metric
                {
                    Id = m.Id, Label = m.Label, Short = m.Short,
                    Value = pct, Available = true, IsShort = m.IsShort,
                    ResetEpoch = PreviewResetEpoch(pct),
                });
            return new TrayModel { All = faked, Items = faked, Pin = Settings.TrayMetric, Stale = Stale };
        }

        // CORE-001 backward compatibility: a saved tray id or card key from
        // before stable identity named the account by its DISPLAY label
        // (provider/Name). It is read as the live account whose LegacyKey it
        // matches — but only when exactly ONE account matches, because two homes
        // sharing a label is precisely the case the old shape could not express.
        // An ambiguous saved key is dropped and the reading rediscovers at the
        // bottom, which is the honest fallback: a label was never identity.
        string MigrateAccountKey(string key)
        {
            AccountData found = null;
            foreach (AccountData a in Accounts)
            {
                if (a.Key == key) return key;       // already a stable key
                if (a.LegacyKey != key) continue;
                if (found != null) return key;      // ambiguous label
                found = a;
            }
            return found != null ? found.Key : key;
        }

        // A tray id is <account key>/<window key>; window keys never contain a
        // separator, so the LAST one splits the two halves whichever identity
        // shape the saved id was written with.
        string MigrateMetricId(string id)
        {
            int cut = id.LastIndexOf('/');
            if (cut <= 0) return id;
            string account = id.Substring(0, cut);
            string live = MigrateAccountKey(account);
            return live == account ? id : live + id.Substring(cut);
        }

        string MigrateCardKey(string key)
        {
            return MigrateAccountKey(key);
        }

        // The hide list read through the same migration as the order and the
        // pin: a hide saved under the old label-derived id must keep hiding the
        // same reading, or an upgrade silently un-hides everything.
        List<string> HiddenMetricIds()
        {
            List<string> raw = Settings.HiddenItems();
            var live = new List<string>(raw.Count);
            foreach (string id in raw) live.Add(MigrateMetricId(id));
            return live;
        }

        List<Metric> SelectedMetrics()
        {
            return SelectedMetrics(AllMetrics());
        }

        // PERF-004: the derivation from an ALL list the caller already owns —
        // the only allocation here is the picked list. One HashSet owns the
        // hidden/picked membership instead of the old nested Contains scans.
        List<Metric> SelectedMetrics(List<Metric> all)
        {
            List<string> hidden = HiddenMetricIds();
            var hiddenSet = new HashSet<string>(hidden);
            var pickedSet = new HashSet<Metric>();
            var byId = new Dictionary<string, Metric>();
            foreach (Metric m in all) if (!byId.ContainsKey(m.Id)) byId[m.Id] = m;
            var picked = new List<Metric>();
            foreach (string id in Settings.ItemOrder())
            {
                string want = MigrateMetricId(id);
                if (hiddenSet.Contains(want)) continue;
                Metric m;
                if (byId.TryGetValue(want, out m) && pickedSet.Add(m)) picked.Add(m);
            }
            // A reading the user has never seen is shown by default: silently
            // hiding a brand-new account would look like the vendor broke.
            foreach (Metric m in all)
                if (!hiddenSet.Contains(m.Id) && pickedSet.Add(m)) picked.Add(m);
            if (picked.Count > Settings.TrayMax) picked.RemoveRange(Settings.TrayMax, picked.Count - Settings.TrayMax);
            return picked;
        }

        // ── the one tray reading resolution ──────────────────────────────────
        // The single answer to "what does the number show", shared by the icon,
        // the tooltip and the hover panel title so the three surfaces can never
        // pick different readings:
        //
        //   PINNED + READABLE   -> the pinned reading, exactly as saved;
        //   PINNED + UNAVAILABLE-> a deterministic fallback to the same
        //                          lowest-available-selected policy the
        //                          unpinned tray uses, with the fallback
        //                          DISCLOSED — "--" is the case where no
        //                          ELIGIBLE selected reading is readable;
        //   PINNED + VANISHED   -> the same fallback;
        //   LOWEST              -> the lowest selected reading with quota left,
        //                          else the lowest available one;
        //   NOTHING READABLE    -> available=false: "--" is finally correct.
        //
        // CORE-013: the reset rides as the window's own epoch.
        internal TrayReading ResolveReading(TrayModel model)
        {
            TrayResolutions++;
            var reading = new TrayReading { Requested = model.Pin ?? "lowest" };
            string pin = MigrateMetricId(model.Pin ?? "lowest");
            Metric pinned = null;
            if (model.Pin != "lowest")
            {
                // A pin is an explicit choice, so it is looked up in the FULL
                // list: hiding a reading from a bars/grid picture must not
                // silently retarget the single number somewhere else.
                foreach (Metric m in model.All)
                    if (m.Id == pin) { pinned = m; break; }
                if (pinned != null && pinned.Available)
                {
                    reading.MetricId = pinned.Id;
                    reading.Label = pinned.Label;
                    reading.Value = pinned.Value;
                    reading.Available = true;
                    reading.ResetEpoch = pinned.ResetEpoch;
                    return reading;
                }
                // The pin could not answer: it is temporarily unreadable (a
                // vendor hiccup, a locked window) or it vanished (logout,
                // uninstall). Falling back beats showing "--" forever — but the
                // fallback says so.
                reading.Fallback = true;
                reading.Note = pinned != null
                    ? "pinned reading unavailable — showing the lowest selected"
                    : "pinned reading gone — showing the lowest selected";
                reading.NoteShort = "pin unavailable";
            }
            // The lowest policy over the SELECTED readings — the same set the
            // tray picture draws. "What stops me first" answers over positive
            // readings first, so a spent-but-reported window does not mask a
            // healthy one; with none positive, the lowest available one. The
            // scope is ONLY model.Items: a reading the user hid from the tray
            // is hidden from every tray surface, so an emptied selection
            // answers "--" rather than resurrecting a hidden metric. ("--"
            // here means "no readable eligible tray metric", not "no provider
            // anywhere in the application".)
            List<Metric> scope = model.Items;
            int minAny = int.MaxValue, minPos = int.MaxValue;
            Metric pickAny = null, pickPos = null;
            foreach (Metric m in scope)
            {
                if (!m.Available) continue;
                if (m.Value < minAny) { minAny = m.Value; pickAny = m; }
                if (m.Value > 0 && m.Value < minPos) { minPos = m.Value; pickPos = m; }
            }
            Metric pick = pickPos ?? pickAny;
            if (pick != null)
            {
                reading.MetricId = pick.Id;
                reading.Label = pick.Label;
                reading.Value = pick.Value;
                reading.Available = true;
                reading.ResetEpoch = pick.ResetEpoch;
            }
            else if (reading.Fallback)
            {
                // The pin fell back and nothing readable exists either: the
                // note explains both facts.
                reading.Note = "pinned reading unavailable — no readable reading at all";
                reading.NoteShort = "pin unavailable";
            }
            return reading;
        }

        void GetTrayMetric(out int value, out bool available, out string label, out double? resetEpoch)
        {
            TrayReading r = ResolveReading(BuildModel());
            value = r.Value; available = r.Available; label = r.Label; resetEpoch = r.ResetEpoch;
        }

        void GetTrayMetric(out int value, out bool available, out string label)
        {
            double? resetEpoch;
            GetTrayMetric(out value, out available, out label, out resetEpoch);
        }

        // "How long until I can work again" in the fewest characters that stay
        // true: minutes under an hour ("12m"), whole hours under a day ("3h"),
        // days above that ("2d"). A past/unknown stamp is "--", never "0m" —
        // zero minutes reads as "right now" and lies when the probe is stale.
        //
        // CORE-013: callers pass the epoch TOKEN (the absolute instant in
        // invariant form) — the ISO branch answers only for legacy shapes that
        // never crossed Flatten (previews, fabricated test windows). Either way
        // the local DateTime exists solely for this final readout.
        static string CountdownText(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "--";
            DateTime reset;
            double? epoch = Stamp.FromToken(iso);
            if (epoch.HasValue) reset = Stamp.Local(epoch.Value);
            else if (!DateTime.TryParse(iso, out reset)) return "--";
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
            BumpPopupData();
            Refresh(); UpdateTray();
        }

        // The order as the Tray tab PAINTS it: the saved order, restricted to
        // readings that still exist, then anything newly discovered. Both the
        // arrows and the drag commit against this list, so an id left over from
        // a logged-out account cannot shift a row by one.
        // PERF-004: dictionary/HashSet membership instead of nested scans,
        // and the tray paint's own `all` list instead of a second AllMetrics.
        List<string> PaintedOrder()
        {
            List<Metric> all = TrayPaintAll ?? AllMetrics();
            var byId = new Dictionary<string, Metric>(all.Count);
            foreach (Metric m in all) if (!byId.ContainsKey(m.Id)) byId[m.Id] = m;
            var order = new List<string>();
            var seen = new HashSet<string>();
            foreach (string id in Settings.ItemOrder())
            {
                string want = MigrateMetricId(id);
                if (!byId.ContainsKey(want) || !seen.Add(want)) continue;
                order.Add(want);
            }
            foreach (Metric m in all) if (seen.Add(m.Id)) order.Add(m.Id);
            return order;
        }

        // The same rule for account cards: the saved order first, then anything
        // newly discovered. A vendor that logs in mid-session lands at the
        // bottom rather than shuffling the cards the user arranged.
        List<string> PaintedCardOrder()
        {
            var byKey = new Dictionary<string, AccountData>(Accounts.Count);
            foreach (AccountData a in Accounts) if (!byKey.ContainsKey(a.Key)) byKey[a.Key] = a;
            var order = new List<string>();
            var seen = new HashSet<string>();
            foreach (string key in Settings.CardOrder())
            {
                string want = MigrateCardKey(key);
                if (!byKey.ContainsKey(want) || !seen.Add(want)) continue;
                order.Add(want);
            }
            foreach (AccountData a in Accounts) if (seen.Add(a.Key)) order.Add(a.Key);
            return order;
        }

        // Cards in the order they are drawn, which is the order OnMouseUp
        // commits against — one list, so the insertion marker cannot lie.
        List<AccountData> OrderedAccounts()
        {
            var byKey = new Dictionary<string, AccountData>(Accounts.Count);
            foreach (AccountData a in Accounts) if (!byKey.ContainsKey(a.Key)) byKey[a.Key] = a;
            var cards = new List<AccountData>();
            foreach (string key in PaintedCardOrder()) cards.Add(byKey[key]);
            return cards;
        }

        // The account has at least one window with quota actually left. An
        // all-unavailable card and an all-zero card are equally "nothing to
        // work with"; a carried-forward card keeps its last good numbers, so a
        // vendor hiccup never counts as spent.
        static bool AccountHasQuota(AccountData a)
        {
            if (a.Availability != null && (a.Availability.RegularAvailable || a.Availability.GenericReserveAvailable || a.Availability.LunaReserveAvailable))
                return true;
            foreach (WindowData w in a.Windows)
                if (w.Available && w.Rem > 0) return true;
            return false;
        }

        // The account's 5h window is usable right now: a reading the vendor
        // sent, above zero, not locked by a longer window in its pool.
        static bool AccountHasFiveHour(AccountData a)
        {
            foreach (WindowData w in a.Windows)
                if (w.Base == Model.FIVE_HOUR && w.Available && w.Rem > 0 && w.GatedBy == null) return true;
            return false;
        }

        // What the Accounts tab DRAWS. The sweep always probes everything; the
        // filters decide only what is shown, so a hidden account reappears by
        // itself the moment its quota returns (or the filter goes off).
        List<AccountData> VisibleAccounts()
        {
            return VisibleAccounts(OrderedAccounts());
        }

        // PERF-004: the filter over an ordered list the caller already owns.
        List<AccountData> VisibleAccounts(List<AccountData> cards)
        {
            List<string> hidden = Settings.HiddenAccountList();
            var shown = new List<AccountData>();
            foreach (AccountData a in cards)
            {
                if (hidden.Contains(a.Key)) continue;
                if (Settings.HideSpentAccounts && !AccountHasQuota(a)) continue;
                if (Settings.OnlyWithFiveHour && !AccountHasFiveHour(a)) continue;
                shown.Add(a);
            }
            return shown;
        }

        void ToggleAccountHidden(string key)
        {
            List<string> hidden = Settings.HiddenAccountList();
            if (hidden.Contains(key)) hidden.Remove(key); else hidden.Add(key);
            Settings.SetHiddenAccounts(hidden); Settings.Save();
            Note = hidden.Contains(key) ? "Account hidden — it still refreshes" : "Account shown";
            // PERF-005: the popup rows list every account, hidden or not.
            BumpPopupData();
            FitWindow(); AutoFitHeight(); Refresh();
        }

        void ToggleItem(string id)
        {
            // The stored list is rewritten in LIVE ids, so a hide saved under
            // the old label-derived shape is replaced rather than accumulated
            // beside its own migration.
            List<string> hidden = HiddenMetricIds();
            if (hidden.Contains(id)) hidden.Remove(id); else hidden.Add(id);
            Settings.SetHiddenItems(hidden);
            if (Settings.ItemOrder().Count == 0) Settings.SetItemOrder(PaintedOrder());
            Settings.Save();
            BumpPopupData();
            AutoFitHeight(); Refresh(); UpdateTray();
        }

        void SetTrayMax(int delta)
        {
            int next = Settings.TrayMax + delta;
            if (next < 1 || next > LimisawSettings.MaxTrayItems) return;
            Settings.TrayMax = next; Settings.Save();
            BumpPopupData();
            AutoFitHeight(); Refresh(); UpdateTray();
        }

        void ResetTrayItems()
        {
            Settings.TrayItems = ""; Settings.TrayHidden = ""; Settings.Save();
            Note = "Tray items reset to discovery order";
            BumpPopupData();
            FitWindow(); AutoFitHeight(); Refresh(); UpdateTray();
        }

        // ── painting ─────────────────────────────────────────────────────────
        // Fixed chrome (header, action row, tab row, footer) never moves; the
        // body between the tabs and the footer is ONE viewport that scrolls
        // its virtual content. The painters keep painting in virtual content
        // coordinates under a translate transform; after they return, every
        // body-registered rectangle is translated into client space and
        // clipped to the viewport, so the interactive registry holds only
        // what the user can actually see and click.
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.SmoothingMode = SmoothingMode.None; g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.Clear(Palette.BG); Buttons.Clear(); ButtonActions.Clear(); Marks.Clear(); Cropped.Clear();
            HintZones.Clear(); HintTexts.Clear();
            bool silent = Measuring;
            Buttons.Silent = ButtonActions.Silent = Marks.Silent = Cropped.Silent
                = HintZones.Silent = HintTexts.Silent = ItemRows.Silent = ItemRowIds.Silent = silent;
            int w = Width, h = Height;
            using (var b = new SolidBrush(Palette.SURFACE)) g.FillRectangle(b, 0, 0, w, HeaderH);
            DrawText(g, "LIMISAW", 6, 3, Palette.TEXT, 12, true);
            string st = Refreshing ? "..." : (Stale ? "STALE" : "OK");
            DrawText(g, st, w - 62, 3, Stale ? Palette.DANGERTXT : (Accounts.Count > 0 ? Palette.LINK : Palette.MUTED), 12, true);
            var xr = new Rectangle(w - 22, 1, 20, 20); RegisterAction(xr, () => HideToTray(), "hide to the notification area — the tray icon and menu stay");
            DrawText(g, "X", w - 18, 3, Palette.TEXT2, 12);

            int y = HeaderH + 6;
            // Buttons are laid out RIGHT to LEFT from their measured labels, and
            // the status text gets whatever is left: a fixed-width box cropped
            // "Showing: Left" to "Showing: Le" as soon as a theme's font was
            // wider than the guess.
            string usedLabel = Settings.ShowUsed ? "Showing: Used" : "Showing: Left";
            int usedW = ButtonWidth(g, usedLabel), refreshW = ButtonWidth(g, "Refresh (F5)");
            var ur = new Rectangle(w - 8 - usedW, y, usedW, 22);
            RegisterAction(ur, () => ToggleShowUsed(),
                Settings.ShowUsed
                    ? "showing how much each window has SPENT — click to show what is LEFT"
                    : "showing how much each window has LEFT — click to show what is SPENT");
            DrawButton(g, ur, usedLabel, Settings.ShowUsed);
            var rr = new Rectangle(ur.Left - Gap - refreshW, y, refreshW, 22);
            RegisterAction(rr, () => RefreshData(), "re-probe every vendor now (same as F5)");
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
                RegisterAction(tr, () => { Tab = pick; TooltipDismiss(); Refresh(); },
                    "the " + TabLabels[i] + " tab" + (Tab == i ? " (open)" : ""));
                DrawButton(g, tr, TabLabels[i], Tab == i);
            }

            // ── the body viewport ───────────────────────────────────────────
            // PERF-004: ONE layout snapshot for this paint transaction. It is
            // built before ResolveScroll (which may measure the height more
            // than once while the scrollbar decides the width) and reused by
            // the painter — never across paints, never across state changes.
            LayoutSnapshots++;
            SnapPopulation += Accounts.Count;
            if (Tab == TabAccounts)
                PaintLayout = new AccountsLayout(OrderedAccounts());
            else if (Tab == TabTray || Tab == TabSettings) TrayPaintAll = AllMetrics();
            try
            {
                if (Tab == TabAccounts)
                    PaintLayout.Cards = VisibleAccounts(PaintLayout.All);
                int top = BodyTop();
                Rectangle viewport;
                bool showBar;
                int scrollY = ResolveScroll(w, out viewport, out showBar);
                // PERF-004: the virtual band of content the viewport can see,
                // with overscan. Expensive painters consult it; geometry,
                // heights, ItemRows and drag targets stay FULL-population.
                BandTop = viewport.Y + scrollY - BandOverscanPx;
                BandBottom = viewport.Bottom + scrollY + BandOverscanPx;
                PaintedBodyW = viewport.Width;
                g.SetClip(viewport);
                g.TranslateTransform(0, -scrollY);
                ItemRows.Clear(); ItemRowIds.Clear();
                try
                {
                    if (Tab == TabCli) PaintConnectionsPanel(g, top, viewport.Width);
                    else if (Tab == TabTray) PaintTrayPanel(g, top, viewport.Width);
                    else if (Tab == TabSettings) PaintSettingsPanel(g, top, viewport.Width);
                    else PaintAccounts(g, top, viewport.Width);
                }
                catch (Exception ex)
                {
                    // An expected rendering fault (GDI+ exhaustion, a degenerate
                    // rectangle, a disposed bitmap) degrades to a line and a
                    // diagnostic instead of an unhandled UI-thread exception —
                    // that class of throw was what could take the whole app down
                    // while the user clicked through tray settings. Programming
                    // errors (a null deref, an index bug) still crash loudly.
                    if (!IsRenderingFault(ex)) throw;
                    g.ResetTransform();
                    DrawText(g, "render fault: " + ex.GetType().Name + " — press Refresh", 14, top + 4, Palette.DANGERTXT, 10);
                    Note = "Panel paint failed — " + ex.GetType().Name + " — press Refresh to retry";
                }
                g.ResetTransform();
                // The interactive registry becomes the CLIENT truth: virtual
                // rectangles shift by the scroll offset and anything outside the
                // viewport is unregistered, so an off-screen button can never eat
                // a click aimed at visible content (and a visible control is
                // never blocked by stale geometry).
                ClipBodyRegistrations(viewport, scrollY);
                g.ResetClip();
                PaintBodyScrollbar(g, viewport, showBar, scrollY);
            }
            finally
            {
                PaintLayout = null;
                TrayPaintAll = null;
                BandTop = BandBottom = 0;
            }

            // A fetch error outranks a tray-render error: without data the icon
            // has nothing to draw anyway. A save failure outranks both hints:
            // every change the user makes in this session is being thrown away,
            // and they cannot know that from behaviour alone — the settings work
            // until the app restarts and then revert, the worst possible moment.
            // R020: a DIRTY conflict (live choices the disk never accepted,
            // after a failed save) is the same class of problem and stays
            // visible until an explicit retry or an accepted external reload
            // resolves it — it is deliberately NOT the same line as a write
            // that just failed, because the two mean different things.
            string problem = Settings.LastSaveFailed
                ? "Settings NOT saved — LIMISAW.ini could not be written"
                : Settings.Dirty
                ? "Settings changed — LIMISAW.ini still holds older values"
                : LastError.Length > 0 ? "Error: " + LastError
                : TrayError.Length > 0 ? "Tray icon failed: " + TrayError
                // A cue that could not play is a real fault the user cannot
                // otherwise notice, so it outlives one repaint — but it yields to
                // anything about the quota itself.
                : SoundNote.Length > 0 ? SoundNote : "";
            // Hover explanation beats the keyboard cheatsheet but yields to a
            // real problem and to the note about what just changed: an
            // explanation of a control is only useful while nothing is wrong.
            string footer = problem.Length > 0 ? problem
                : Note.Length > 0 ? Note
                : Hover.Length > 0 ? Hover
                : "F5 refresh · wheel scroll · PgDn/PgUp · drag edges resize · right-drag move · Esc hide";
            DrawTextFit(g, footer, 8, h - 20, w - 16,
                problem.Length > 0 ? Palette.DANGERTXT
                    : Hover.Length > 0 && Note.Length == 0 ? Palette.TEXT2 : Palette.MUTED, 10);
            // The themed tooltip overlay goes on top of everything, last.
            PaintTooltip(g);
        }

        // Body width of the last real paint — the parity seam's other half.
        internal int PaintedBodyW;

        // ── PERF-004: the per-paint layout snapshot ──────────────────────────
        // One immutable-ish layout per paint transaction. The ordered/visible
        // account lists and the tray metric list are built ONCE per paint and
        // every helper downstream (panel height, scroll resolution, painter)
        // reads the snapshot instead of rebuilding equivalent lists through
        // nested scans. The counters are the regression seam: a paint must
        // build one snapshot, populate the full fleet once, and render only
        // the cards that intersect the viewport band.
            internal int LayoutSnapshots, SnapPopulation, ExpensiveCardPaints,
            ExpensiveRowPaints, AllMetricsCalls, PreviewRenders;

        internal class AccountsLayout
        {
            public List<AccountData> All;     // full ordered population
            public List<AccountData> Cards;  // visible, ordered
            public AccountsLayout(List<AccountData> all) { All = all; }
        }

        AccountsLayout PaintLayout;        // non-null only inside one paint
        List<Metric> TrayPaintAll;         // non-null only inside one paint
        int BandTop, BandBottom;           // virtual content band, 0 = disabled
        // Overscan: one extra card's worth of rows rendered above and below
        // the viewport, so partial cards at the edges always draw.
        const int BandOverscanPx = 160;

        // Virtual → client: the body registry the painters built is shifted by
        // the scroll offset and clipped to the viewport. Pairs stay aligned
        // because the rectangle list and its value list are filtered in ONE
        // pass. Chrome rectangles (header/action/tabs, all above the body) are
        // already client coordinates and pass through untouched.
        void ClipBodyRegistrations(Rectangle viewport, int scrollY)
        {
            ClipTranslate(Buttons, ButtonActions, viewport, scrollY);
            ClipTranslate<Rectangle>(Marks, null, viewport, scrollY);
            ClipTranslate(HintZones, HintTexts, viewport, scrollY);
            ClipTranslate(ItemRows, ItemRowIds, viewport, scrollY);
            ClipRail(ref VolRailVolume, ref VolKnobVolume, viewport, scrollY);
            ClipRail(ref VolRailLow, ref VolKnobLow, viewport, scrollY);
            ClipRail(ref VolRailPreview, ref VolKnobPreview, viewport, scrollY);
        }

        void ClipTranslate<TVal>(RegenList<Rectangle> rects, RegenList<TVal> vals, Rectangle viewport, int scrollY)
        {
            for (int i = rects.Count - 1; i >= 0; i--)
            {
                Rectangle r = rects[i];
                if (r.Y < BodyTop()) continue;                  // chrome: already client
                Rectangle shifted = new Rectangle(r.X, r.Y - scrollY, r.Width, r.Height);
                if (!shifted.IntersectsWith(viewport))
                {
                    rects.RemoveAt(i);
                    if (vals != null && vals.Count == rects.Count + 1) vals.RemoveAt(i);
                    continue;
                }
                rects[i] = Rectangle.Intersect(shifted, viewport);
            }
        }

        // A slider rail keeps its FULL x-span (the value math needs it), but a
        // rail whose row is entirely outside the viewport must not answer a
        // press — stale geometry is exactly the invisible-clickable defect.
        void ClipRail(ref Rectangle rail, ref Rectangle knob, Rectangle viewport, int scrollY)
        {
            if (rail == Rectangle.Empty || rail.Y < BodyTop()) return;
            rail = new Rectangle(rail.X, rail.Y - scrollY, rail.Width, rail.Height);
            if (knob != Rectangle.Empty) knob = new Rectangle(knob.X, knob.Y - scrollY, knob.Width, knob.Height);
            if (!rail.IntersectsWith(viewport) && !knob.IntersectsWith(viewport))
            { rail = Rectangle.Empty; knob = Rectangle.Empty; }
        }

        // ── the themed body scrollbar ────────────────────────────────────────
        // Track = BDARK on BG, thumb = RAISED with a bevel; every colour from
        // the active Palette, so a theme change repaints it correctly. Shown
        // only when content exceeds the viewport; its strip is reserved from
        // the body width, never covering a control.
        Rectangle ScrollTrack(Rectangle viewport)
        {
            if (MaxScroll <= 0) return Rectangle.Empty;
            return new Rectangle(viewport.Right + 1, viewport.Y + 1, ScrollbarW - 2, Math.Max(0, viewport.Height - 2));
        }

        Rectangle ScrollThumb(Rectangle track, int scroll)
        {
            int viewH = BodyHeight(), contentH = viewH + MaxScroll;
            int th = Math.Max(ThumbMinH, track.Height * viewH / Math.Max(1, contentH));
            th = Math.Min(th, track.Height);
            int travel = track.Height - th;
            int ty = MaxScroll > 0 ? track.Y + (int)((long)travel * scroll / MaxScroll) : track.Y;
            return new Rectangle(track.X, ty, track.Width, th);
        }

        void PaintBodyScrollbar(Graphics g, Rectangle viewport, bool show, int scroll)
        {
            if (!show) return;
            Rectangle track = ScrollTrack(viewport);
            if (track.Width <= 0 || track.Height < ThumbMinH + 4) return;
            using (var bg = new SolidBrush(Palette.BG)) g.FillRectangle(bg, track);
            using (var p = new Pen(Palette.BDARK)) g.DrawRectangle(p, track.X, track.Y, track.Width - 1, track.Height - 1);
            // The TRACK is interactive too: a click above/below the thumb pages
            // a viewport, so it needs its own hover target. Registered BEFORE
            // the thumb so the thumb's own sentence wins where they overlap.
            Hint(track, "click above or below the thumb to page; drag the thumb to scroll");
            Rectangle thumb = ScrollThumb(track, scroll);
            using (var bg2 = new SolidBrush(Palette.RAISED)) g.FillRectangle(bg2, thumb.X + 1, thumb.Y + 1, thumb.Width - 2, thumb.Height - 2);
            Draw.Bevel(g, thumb.X, thumb.Y, thumb.Width, thumb.Height, false);
            Hint(thumb, "drag to scroll — click the track above or below the thumb to page");
        }

        // Card geometry is shared with AccountsPanelHeight through the same
        // constants, so the measured virtual height and the painted content
        // cannot drift apart and clip a row. Columns are derived from the
        // window width, never hardcoded: fixed x-offsets are what let the
        // gauge slide under the reset time. Below the name floor the row
        // reflows into its two-line narrow shape instead of cropping.
        void CardColumns(int w, out int nameX, out int nameW, out int pctX,
            out int gaugeX, out int gaugeW, out int whenX, out int whenW)
        {
            int cw = w - 16;
            whenW = 96; gaugeW = 78;
            int pctW = 40;
            whenX = 8 + cw - 8 - whenW;
            gaugeX = whenX - Gap - gaugeW;
            pctX = gaugeX - Gap - pctW;
            nameX = 18; nameW = pctX - nameX - Gap;
        }

        void PaintAccounts(Graphics g, int cursor, int w)
        {
            int nameX, nameW, pctX, gaugeX, gaugeW, whenX, whenW;
            CardColumns(w, out nameX, out nameW, out pctX, out gaugeX, out gaugeW, out whenX, out whenW);
            // PERF-004: the paint snapshot owns both lists — no rebuild here.
            AccountsLayout layout = PaintLayout;
            List<AccountData> allOrdered = layout != null && layout.All != null ? layout.All : OrderedAccounts();
            List<AccountData> visible = layout != null && layout.Cards != null ? layout.Cards : VisibleAccounts(allOrdered);
            if (Accounts.Count > 0 && visible.Count == 0)
            {
                DrawText(g, "Every account is hidden or filtered out — see Settings.", 14, cursor + 4, Palette.MUTED, 10);
                PaintedContentBottom = cursor + 26;
                return;
            }
            if (visible.Count == 0)
            {
                DrawText(g, Refreshing ? "Probing vendors..." : "No accounts reported yet.", 14, cursor + 4, Palette.TEXT2, 11);
                PaintedContentBottom = cursor + 26;
                return;
            }
            bool narrow = NarrowCards(w);
            int cw = w - 16;

            // Cards are reordered by dragging, same gesture as the Tray tab: the
            // vendor that matters most belongs at the top, and which one that is
            // is the user's call, not the sweep order's. Filters and hides are
            // applied first, so a drag commits against what is actually drawn.
            List<AccountData> cards = visible;
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
                    probe += CardHeight + CardLines(a, w) * RowH + Gap + Gap;
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

            // PERF-004: the drag preview mutates the snapshot's card list; the
            // next paint rebuilds it from live state, so the mutation dies with
            // the transaction.
            if (layout != null) layout.Cards = cards;

            foreach (AccountData a in cards)
            {
                int cardTopFull = cursor;
                int cardH = CardHeight + CardLines(a, w) * RowH + Gap;
                // PERF-004: viewport work virtualization. Cheap geometry runs
                // for EVERY card (so ItemRows/drag targets and the measured
                // height still represent the full population); the EXPENSIVE
                // card render — text measurement, brushes, buttons, detailed
                // draws — runs only for cards intersecting the translated
                // viewport band. Offscreen rows stay non-interactive by
                // construction instead of being painted then clipped away.
                if (BandBottom > BandTop &&
                    (cardTopFull + cardH < BandTop || cardTopFull > BandBottom))
                { cursor += cardH + Gap; continue; }
                ExpensiveCardPaints++;
                bool bad;
                string lead = CardNote(a, out bad);
                bool held = Dragging && a.Key == DragId;
                ItemRows.Add(new Rectangle(8, cursor, cw, cardH + Gap));
                ItemRowIds.Add(a.Key);
                using (var bg = new SolidBrush(held ? Palette.ALT : Palette.RAISED)) g.FillRectangle(bg, 8, cursor, cw, cardH);
                DrawBevel(g, 8, cursor, cw, cardH, false);
                Hint(new Rectangle(8, cursor, cw, cardH), "drag a card to reorder the accounts");
                string tag = a.Carried ? "last good" + (a.CarriedAt.Length > 0 ? " " + a.CarriedAt : "")
                    : a.Plan != null ? a.Plan : (a.Ok ? "" : a.Status.ToLowerInvariant());
                // The hide affordance sits where the tag would be when there is
                // no tag: the card's own top-right corner. A click there hides
                // this account from the tab — unhide lives in Settings.
                const int HideBtnW = 20;
                var hideBtn = new Rectangle(8 + cw - 8 - HideBtnW, cursor + 3, HideBtnW, 16);
                AccountData hideTarget = a;
                Buttons.Add(hideBtn); ButtonActions.Add(() => ToggleAccountHidden(hideTarget.Key));
                Hint(hideBtn, "hide this account from the Accounts tab (unhide in Settings — it keeps refreshing either way)");
                DrawButton(g, hideBtn, "x", false);
                int tagRoom = (hideBtn.Left - Gap) - 14;
                int tagW = tag.Length > 0 ? TextWidth(g, tag, 10) : 0;
                DrawTextFit(g, AccountTitle(a), 14, cursor + 5, tagRoom - (tagW > 0 ? tagW + Gap : 0), Palette.LINK, 11, true, AccountTitle(a));
                if (tagW > 0) DrawText(g, tag, hideBtn.Left - Gap - tagW, cursor + 6,
                    a.Carried ? Palette.WARNING : Palette.TEXT2, 10);
                int ry = cursor + CardHeight - 4;
                if (lead != null)
                {
                    // The reason goes ABOVE the rows, whether or not there are
                    // any: a failed sweep still leaves unavailable windows
                    // behind, and drawing this only for an empty list is what
                    // hid every provider's error message (see T-006).
                    DrawTextFit(g, lead, 14, ry, cw - 20,
                        a.Carried ? Palette.WARNING : bad ? Palette.DANGERTXT : Palette.MUTED, 10, false, lead);
                    ry += RowH;
                }
                foreach (WindowData win in a.Windows)
                {
                    string name = win.Label + (win.GroupLabel.Length > 0 ? " · " + win.GroupLabel : "");
                    string when = win.GatedBy != null ? "locked by " + win.GatedBy
                        : win.AssumedFull ? "refilled" : FriendlyTime(win.ResetEpoch);
                    if (narrow)
                    {
                        // The narrow shape: label + percentage + gauge on line
                        // one, the reset state on its own line below — nothing
                        // is cropped into nonsense, the card just grows.
                        DrawTextFit(g, name, nameX, ry, nameW, a.Carried ? Palette.MUTED : Palette.TEXT2, 10, false, name);
                        DrawText(g, FormatPct(win.Available, win.Rem), pctX, ry,
                            a.Carried ? Palette.MUTED : PctColor(win.Rem), 11, true);
                        DrawGauge(g, gaugeX, ry + 4, gaugeW, 8, win.Available ? win.Rem : 100, win.Available, a.Carried);
                        ry += RowH;
                        DrawTextFit(g, when, 18, ry, cw - 20, Palette.MUTED, 10);
                        ry += RowH;
                        continue;
                    }
                    DrawTextFit(g, name, nameX, ry, nameW, a.Carried ? Palette.MUTED : Palette.TEXT2, 10, false, name);
                    DrawText(g, FormatPct(win.Available, win.Rem), pctX, ry,
                        a.Carried ? Palette.MUTED : PctColor(win.Rem), 11, true);
                    DrawGauge(g, gaugeX, ry + 4, gaugeW, 8, win.Available ? win.Rem : 100, win.Available, a.Carried);
                    DrawTextFit(g, when, whenX, ry, whenW, Palette.MUTED, 10);
                    ry += RowH;
                }
                // A banked reset goes BELOW the windows, because it is what to do
                // about them. The button says exactly what it will run; nothing
                // happens without the confirmation behind it.
                string banked = CreditNote(a);
                if (banked != null)
                {
                    AccountData target = a;
                    // Only Codex exposes a command for this. Another vendor's
                    // credit is still reported — it is real — but without a
                    // button LIMISAW has no way to honour.
                    bool actionable = a.Provider == "codex" && !Redeeming;
                    string useLabel = Redeeming ? "working..." : "Use reset";
                    int useW = ButtonWidth(g, "Use reset");
                    var useBtn = new Rectangle(8 + cw - 8 - useW, ry - 2, useW, 20);
                    if (a.Provider == "codex")
                    {
                        if (actionable)
                            RegisterAction(useBtn, () => RedeemCredit(target),
                                "Spend one banked Codex reset. LIMISAW asks for confirmation first; this cannot be undone.");
                        else
                            Hint(useBtn, "Spend one banked Codex reset. LIMISAW asks for confirmation first; this cannot be undone.");
                        DrawButton(g, useBtn, useLabel, false, actionable);
                    }
                    string expiry = string.IsNullOrEmpty(a.ResetCreditExpires)
                        ? "" : "  expires " + FriendlyTime(a.ResetCreditExpires);
                    int textRight = a.Provider == "codex" ? useBtn.Left - Gap : 8 + cw - 8;
                    // The full sentence in the tooltip when the visible line is
                    // cropped: how many credits, what they refill, how long.
                    string bankedFull = banked + expiry
                        + " — the Use reset button asks for confirmation first, and this cannot be undone.";
                    DrawTextFit(g, banked + expiry, 18, ry, textRight - 18, Palette.LINK, 10, false, bankedFull);
                    ry += RowH;
                }
                // An absolute balance (FreeBucks) is its own line below the
                // windows. It is NOT a percentage, so it never shows as a gauge
                // or a % — just the vendor's own amount, with the reset when the
                // vendor states one. The safe breakdown rides the tooltip only.
                string balance = BalanceNote(a);
                if (balance != null)
                {
                    string bd = BalanceBreakdown(a);
                    string when2 = "";
                    foreach (BalanceData b in a.Balances)
                        if (b.ResetEpoch.HasValue) { when2 = "  resets " + FriendlyTime(b.ResetEpoch); break; }
                    DrawTextFit(g, balance + when2, 18, ry, cw - 20, Palette.TEXT2, 10, false,
                        bd != null ? bd : balance);
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
            PaintedContentBottom = cursor;
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
            if (a.Provider == "codex" && a.RemoteIdentityChecked
                && string.IsNullOrEmpty(a.RemoteAccountIdentity) && a.Ok)
                return "Remote account identity unverified — refresh to check this home";
            if (a.Carried)
                return a.CarriedNote.Length > 0 ? "stale: " + a.CarriedNote : null;
            if (a.Ok) return null;
            bad = !a.Quiet;
            return (a.Quiet ? "idle: " : "ERROR: ") + (a.Error ?? a.Status);
        }

        // An ABSOLUTE balance (FreeBucks) is not a window and not a percentage:
        // it earns its own line, formatted as the vendor's own amount. A missing
        // amount reads "--", never 0. The safe breakdown rides the tooltip only.
        public static string BalanceNote(AccountData a)
        {
            foreach (BalanceData b in a.Balances)
            {
                if (!b.Value.HasValue) return b.Label + " --";
                return b.Label + " " + b.Value.Value.ToString("0.#")
                    + (b.Unit.Length > 0 ? " " + b.Unit : "");
            }
            return null;
        }

        // The tooltip-safe breakdown of the first balance, or null. Never a
        // token, never a raw response: only the vendor's own named amounts.
        public static string BalanceBreakdown(AccountData a)
        {
            foreach (BalanceData b in a.Balances)
            {
                if (b.Breakdown == null || b.Breakdown.Count == 0) continue;
                var parts = new List<string>();
                foreach (var kv in b.Breakdown) parts.Add(kv.Key + " " + kv.Value.ToString("0.#"));
                return b.Label + ": " + string.Join(", ", parts.ToArray());
            }
            return null;
        }

        // A banked reset is the one thing that can get a blocked user working
        // again, so it earns its own line rather than a corner tag. The vendor's
        // own title says what it refills ("Full reset (Weekly + 5 hr)"), which is
        // more than LIMISAW could infer.
        public static string CreditNote(AccountData a)
        {
            if (a.ResetCredits <= 0) return null;
            string what = string.IsNullOrEmpty(a.ResetCreditTitle)
                ? (a.ResetCredits == 1 ? "1 reset available" : a.ResetCredits + " resets available")
                : (a.ResetCredits > 1 ? a.ResetCredits + "x " : "") + a.ResetCreditTitle;
            return "banked: " + what;
        }

        // Height and paint read the SAME count, so a card can never be measured
        // shorter than it draws and clip its last row. At narrow width every
        // window's reset state drops to its own line, one line per window.
        static int CardNameW(int w)
        {
            int cw = w - 16;
            int whenW = 96, gaugeW = 78;
            int gaugeX = 8 + cw - 8 - whenW - Gap - gaugeW;
            int pctX = gaugeX - Gap - 40;
            return pctX - 18 - Gap;
        }

        static bool NarrowCards(int w) { return CardNameW(w) < 90; }

        public static int CardLines(AccountData a) { return CardLines(a, 420); }
        public static int CardLines(AccountData a, int w)
        {
            bool bad;
            int lines = a.Windows.Count
                + (CardNote(a, out bad) != null ? 1 : 0)
                + (CreditNote(a) != null ? 1 : 0)
                + (BalanceNote(a) != null ? 1 : 0);
            if (NarrowCards(w)) lines += a.Windows.Count;
            return Math.Max(1, lines);
        }
        // CORE-001: the coordinator is the single authoritative per-vendor
        // connection state. A successful periodic quota read is fresh Level-1
        // evidence — the CLI ran, authenticated and answered — so it must
        // promote the card to Connected/ConnectedQuotaUnavailable even though no
        // interactive verification ran. The projection is POSITIVE-only: a
        // failed read never downgrades a terminal result, and an in-flight
        // verification is never clobbered (Observe refuses). The existing
        // discovery facts (path, version, duplicates, credential category) ride
        // along so the expanded card stays honest.
        void ProjectProbeConnections()
        {
            foreach (var vd in VendorRegistry.Present())
            {
                bool anyOk = false, anyReading = false;
                foreach (AccountData a in Accounts)
                {
                    if (a.Provider != vd.Id || !a.Ok) continue;
                    anyOk = true;
                    if (a.HasReading) anyReading = true;
                }
                if (!anyOk) continue;

                VendorConnection baseConn = ConnCoordinator.Latest(vd.Id);
                if (baseConn == null)
                {
                    // PERF-003 (SRC-006:R020): projection consumes the generation
                    // snapshot too — ZERO registry/PATH/config-body I/O on this
                    // thread. When no generation exists yet a deterministic
                    // non-authoritative placeholder carries the vendor identity;
                    // the successful probe truth below overrides the state.
                    baseConn = ExecutableDiscovery.SnapshotLevel0(vd.Id);
                    if (baseConn == null)
                        baseConn = new VendorConnection { VendorId = vd.Id, State = ConnectionState.Discovering, Stage = ConnectionStage.Discovery };
                }
                var vc = baseConn.Clone();
                vc.VendorId = vd.Id;
                vc.Installed = true;
                vc.State = anyReading ? ConnectionState.Connected : ConnectionState.ConnectedQuotaUnavailable;
                vc.ErrorCode = ConnectionErrorCode.None;
                vc.RecommendedAction = ConnectionAction.None;
                vc.AuthKnown = true;
                vc.Authenticated = true;
                vc.ConnectivityKnown = true;
                vc.ConnectivityOk = true;
                vc.VerificationOk = true;
                vc.Monitorable = anyReading;
                vc.UserActionRequired = false;
                vc.Stage = ConnectionStage.Quota;
                vc.LastVerifiedUtc = Stamp.Now;
                vc.Reason = anyReading
                    ? "Quota read succeeded"
                    : "Authenticated, but the vendor reported no quota window";
                if (vd.Id == "codex")
                {
                    vc.DuplicateRemoteHomeIds = new List<string>(DuplicateCodexHomes);
                    vc.UnverifiedRemoteHomeIds = new List<string>(UnverifiedCodexHomes);
                    if (DuplicateCodexHomes.Count > 0)
                    {
                        vc.State = ConnectionState.DuplicateRemoteAccount;
                        vc.Reason = "Signed in, but as an account already listed — no extra Codex card was added. Retry different account reuses that same home.";
                    }
                }
                ConnCoordinator.Observe(vd.Id, vc);
            }
        }

        internal void BuildConnections()
        {
            Connections.Clear();
            foreach (var vd in VendorRegistry.Present())
            {
                // A published result is AUTHORITY until explicitly superseded
                // (R044): repaints rebuild Level 0 for vendors with no verdict,
                // but a terminal failure (CredentialRejected, NetworkTimeout,
                // TlsFailed, ProtocolChanged, ResponseTooLarge,
                // ServiceUnavailable) or a good verdict from the coordinator is
                // never thrown away just because the panel repainted.
                VendorConnection last = ConnCoordinator.Latest(vd.Id);
                if (last != null) { Connections.Add(last.Clone()); continue; }
                // PERF-003 (SRC-006:R020): the UI consumes the refresh
                // generation's secret-free snapshot — ZERO registry/PATH/
                // config-body I/O on this thread once the sweep has built it.
                VendorConnection vc = ExecutableDiscovery.SnapshotLevel0(vd.Id);
                if (vc == null)
                {
                    // R020: before the first worker generation exists the
                    // ordinary projection performs ZERO discovery I/O on the UI
                    // thread. A deterministic, non-authoritative placeholder row
                    // keeps every supported vendor represented; the first Refresh
                    // worker builds the generation and Publish replaces it with
                    // the real Level-0 projection. User-triggered actions that
                    // explicitly need fresh discovery still call the explicit
                    // fresh-discovery path (ExecutableDiscovery.BuildConnection).
                    vc = new VendorConnection
                    {
                        VendorId = vd.Id,
                        State = ConnectionState.Discovering,
                        Stage = ConnectionStage.Discovery,
                        Reason = "Checking…",
                    };
                }
                Connections.Add(vc);
            }
        }

        void PaintInstallPanel(Graphics g, int cursor, int w) { PaintConnectionsPanel(g, cursor, w); }

        void PaintConnectionsPanel(Graphics g, int cursor, int w)
        {
            if (Connections.Count == 0) BuildConnections();
            DrawTextFit(g, "Connections — every supported vendor, always visible. Expand for details.",
                14, cursor, w - 24, Palette.TEXT2, 10);
            cursor += 20;
            int cw = w - 16;
            foreach (var conn in Connections)
            {
                var pres = ConnectionPresentation.From(conn);
                int cardH = ConnectionCardHeight(conn, w);
                using (var bg = new SolidBrush(Palette.RAISED)) g.FillRectangle(bg, 8, cursor, cw, cardH);
                DrawBevel(g, 8, cursor, cw, cardH, false);
                bool expanded = ExpandedVendors.Contains(conn.VendorId);
                string primaryLabel = pres.PrimaryActionText;
                int actW = primaryLabel.Length > 0 ? Math.Max(84, ButtonWidth(g, primaryLabel)) : 0;
                int detailsW = ButtonWidth(g, expanded ? "Hide" : "Details");
                var detailsBtn = new Rectangle(8 + cw - 8 - detailsW, cursor + 6, detailsW, 22);
                string vid = conn.VendorId;
                RegisterAction(detailsBtn,
                    () => { if (ExpandedVendors.Contains(vid)) ExpandedVendors.Remove(vid); else ExpandedVendors.Add(vid); TooltipDismiss(); Refresh(); },
                    expanded ? "collapse this card" : "show this vendor's diagnostics and deeper actions");
                DrawButton(g, detailsBtn, expanded ? "Hide" : "Details", false);
                if (actW > 0)
                {
                    var br = new Rectangle(detailsBtn.Left - Gap - actW, cursor + 6, actW, 22);
                    string actionVid = conn.VendorId;
                    ConnectionAction act = conn.RecommendedAction;
                    RegisterAction(br, () => HandleConnectionAction(actionVid, act),
                        ConnectionActionHint(act, actionVid));
                    DrawButton(g, br, primaryLabel, false);
                }
                Color stateCol = pres.Severity == ConnectionSeverity.Error ? Palette.DANGERTXT : pres.Severity == ConnectionSeverity.Warning ? Palette.TEXT2 : Palette.SUCCESS;
                if (conn.State == ConnectionState.Connected || conn.State == ConnectionState.ConnectedQuotaUnavailable) stateCol = Palette.SUCCESS;
                DrawTextFit(g, pres.Title, 14, cursor + 5, detailsBtn.Left - (actW > 0 ? actW + Gap * 2 : Gap) - 14, Palette.LINK, 11, true, pres.Title);
                DrawText(g, pres.StateText, 14, cursor + 24, stateCol, 10);
                string reason = pres.ReasonText;
                if (!string.IsNullOrEmpty(reason))
                {
                int stateW = TextWidth(g, pres.StateText, 10);
                string reasonFull = reason.Length > 0 ? pres.Title + ": " + reason : null;
                DrawTextFit(g, reason, 14 + stateW + Gap * 2, cursor + 24, cw - (14 + stateW + Gap * 2) - (actW > 0 ? actW + detailsW + Gap * 3 : detailsW + Gap * 2) - 10, Palette.MUTED, 10, false, reasonFull);
                }
                int innerY = cursor + 44;
                if (expanded)
                {
                    // True dimensions from the snapshot (R103): a row the
                    // adapter actually ran states what it found; one it has
                    // not run says so instead of a permanent "unknown".
                    DrawText(g, "Discovery: " + ConnectionPresentation.DiscoveryLabel(conn), 14, innerY, Palette.TEXT2, 10);
                    innerY += RowH;
                    if (!string.IsNullOrEmpty(conn.CliVersion))
                    {
                        DrawText(g, "Version: " + conn.CliVersion, 14, innerY, Palette.TEXT2, 10);
                        innerY += RowH;
                    }
                    if (!string.IsNullOrEmpty(conn.Plan))
                    {
                        DrawText(g, "Plan: " + conn.Plan, 14, innerY, Palette.TEXT2, 10);
                        innerY += RowH;
                    }
                    DrawText(g, "Authentication: " + (conn.AuthKnown ? (conn.Authenticated ? "authenticated" : "not authenticated") : "not checked"), 14, innerY, Palette.TEXT2, 10);
                    innerY += RowH;
                    DrawText(g, "Connectivity: " + (conn.ConnectivityKnown ? (conn.ConnectivityOk ? "ok" : "failed") : "not checked"), 14, innerY, Palette.TEXT2, 10);
                    innerY += RowH;
                    DrawText(g, "Quota: " + (conn.Monitorable ? "verified" : conn.VerificationOk ? "ok" : "not verified"), 14, innerY, Palette.TEXT2, 10);
                    innerY += RowH;
                    if (!string.IsNullOrEmpty(conn.ResolvedPath))
                    {
                        DrawTextFit(g, conn.ResolvedPath, 14, innerY, cw - 20, Palette.MUTED, 10, false, conn.ResolvedPath);
                        innerY += RowH;
                    }
                    if (conn.DuplicatePaths != null && conn.DuplicatePaths.Count > 1)
                    {
                        string dup = "Duplicates: " + string.Join("; ", conn.DuplicatePaths.ToArray());
                        DrawTextFit(g, dup, 14, innerY, cw - 20, Palette.MUTED, 10, false, dup);
                        innerY += RowH;
                    }                    if (conn.LastVerifiedUtc.HasValue)
                    {
                        DrawText(g, "Verified: " + FriendlyTime(conn.LastVerifiedUtc), 14, innerY, Palette.MUTED, 10);
                        innerY += RowH;
                    }
                    if (conn.VendorId == "codex" && conn.DuplicateRemoteHomeIds.Count > 0)
                    {
                        string message = "DUPLICATE_REMOTE_ACCOUNT: " + conn.DuplicateRemoteHomeIds.Count + " Codex home(s) share a remote account";
                        DrawTextFit(g, message, 14, innerY, cw - 20, Palette.WARNING, 10, false, message);
                        innerY += RowH;
                    }
                    if (conn.VendorId == "codex" && conn.UnverifiedRemoteHomeIds.Count > 0)
                    {
                        string message = "Remote identity unverified: " + conn.UnverifiedRemoteHomeIds.Count + " Codex home(s)";
                        DrawTextFit(g, message, 14, innerY, cw - 20, Palette.WARNING, 10, false, message);
                        innerY += RowH;
                    }
                    // Why there is no new card, and what the retry will do. No
                    // remote identity, no account id, no email, no credential.
                    foreach (string note in CodexDuplicateNotes(conn))
                    {
                        DrawTextFit(g, note, 14, innerY, cw - 20, Palette.TEXT2, 10, false, note);
                        innerY += RowH;
                    }
                    var copyBtn = new Rectangle(14, innerY, ButtonWidth(g, "Copy diagnostics"), 22);
                    string copyVid = conn.VendorId;
                    RegisterAction(copyBtn, () => CopyConnectionDiagnostics(copyVid),
                        "copy a sanitized " + conn.VendorId + " diagnostic report to the clipboard (no credentials, no keys)");
                    DrawButton(g, copyBtn, "Copy diagnostics", false);
                    var checkBtn = new Rectangle(copyBtn.Right + Gap, innerY, ButtonWidth(g, "Check again"), 22);
                    string checkVid = conn.VendorId;
                    RegisterAction(checkBtn, () => CheckAgain(checkVid),
                        "re-run discovery and verification for " + conn.VendorId + " now");
                    DrawButton(g, checkBtn, "Check again", false);
                    // A second account is a second config home, and nothing on
                    // screen used to say so: the only way to reach one was an
                    // environment variable nobody guesses, and signing in again
                    // the obvious way REPLACED the account already there.
                    if (ConnectionAdapterRegistry.AddAccount.ContainsKey(conn.VendorId))
                    {
                        var addBtn = new Rectangle(checkBtn.Right + Gap, innerY, ButtonWidth(g, "Add account"), 22);
                        string addVid = conn.VendorId;
                        RegisterAction(addBtn, () => BeginAddAccount(addVid),
                            "sign in to ANOTHER " + conn.VendorId + " account, in its own config directory — the account you already use stays exactly as it is");
                        DrawButton(g, addBtn, "Add account", false);
                    }
                    // The way out of a duplicate: the SAME secondary home,
                    // authorized by device code so the account is the user's
                    // choice instead of whatever the browser already held.
                    if (ShowsRetryDifferentAccount(conn))
                    {
                        innerY += 28;
                        var retryBtn = new Rectangle(14, innerY,
                            ButtonWidth(g, "Retry different account"), 22);
                        string retryVid = conn.VendorId;
                        RegisterAction(retryBtn, () => BeginRetryDifferentAccount(retryVid),
                            "authorize the SAME second Codex home again with a one-time device code — sign in to a different ChatGPT account there; no third home is created");
                        DrawButton(g, retryBtn, "Retry different account", false);
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(conn.ResolvedPath))
                        DrawTextFit(g, conn.ResolvedPath, 14, innerY, cw - 20, Palette.MUTED, 10, false, conn.ResolvedPath);
                    else if (conn.VendorId == "zcode" && conn.State == ConnectionState.PermissionRequired)
                        DrawTextFit(g, "Enable in Settings or allow here", 14, innerY, cw - 20, Palette.MUTED, 10);
                }
                cursor += cardH + Gap;
            }
            PaintedContentBottom = cursor;
        }

        // Vendor actions route as vendorId+action (R041): the switch picks the
        // coordinator operation, the adapter performs the work, and the panel
        // repaints structured snapshots. Vendor-specific rules live in the
        // adapters, not here.
        void HandleConnectionAction(string vendorId, ConnectionAction act)
        {
            switch (act)
            {
                case ConnectionAction.CheckAgain: CheckAgain(vendorId); return;
                case ConnectionAction.AllowAndConnect: AllowCredentialAndConnect(vendorId); return;
                case ConnectionAction.OpenVendor: OpenVendor(vendorId); return;
                case ConnectionAction.Connect: BeginSignIn(vendorId); return;
                case ConnectionAction.Install: BeginInstall(vendorId); return;
                case ConnectionAction.Verify: BeginVerify(vendorId); return;
                case ConnectionAction.Troubleshoot: BeginTroubleshoot(vendorId); return;
                default: return;
            }
        }

        // The sentence behind each vendor action. One place, so the button label
        // and its explanation cannot drift apart the way a separate hint list
        // lets them.
        static string ConnectionActionHint(ConnectionAction act, string vendorId)
        {
            switch (act)
            {
                case ConnectionAction.CheckAgain: return "re-run discovery and verification for " + vendorId + " now";
                case ConnectionAction.AllowAndConnect: return vendorId == "freebuff"
                    ? "let LIMISAW read FreeBuff's stored credential to fetch your FreeBucks balance — asks first, read-only, disable any time in Settings"
                    : "let LIMISAW read the ZCode quota credential from its config — asks first, disable any time in Settings";
                case ConnectionAction.OpenVendor: return "open " + vendorId + " itself, so you can manage the account where it lives";
                case ConnectionAction.Connect: return "launch the vendor's own visible sign-in — LIMISAW never sees the credential, and checks automatically when you finish";
                case ConnectionAction.Install: return "run the vendor's published installer in a visible PowerShell window — you confirm first";
                case ConnectionAction.Verify: return "verify " + vendorId + " now (read-only, no quota spent)";
                case ConnectionAction.Troubleshoot: return "run the vendor's own diagnostics and classify the failure";
                default: return "";
            }
        }

        // The credential-read permission keeps its explicit confirmation: it is
        // LIMISAW asking to read another application's credential (R053). One
        // dialog per vendor, with the vendor's own facts.
        void AllowCredentialAndConnect(string vendorId)
        {
            if (vendorId == "freebuff")
            {
                AllowFreebuffAndConnect();
                return;
            }
            string body = "Allow LIMISAW to read the ZCode quota credential?\n\n"
                + "LIMISAW will read only the credential needed for the quota endpoint.\n"
                + "It will not display or log the credential.\n"
                + "Requests go only to fixed supported Z.ai / BigModel quota hosts.\n"
                + "Permission can be disabled again in Settings.\n\nContinue?";
            if (MessageBox.Show(this, body, "LIMISAW — allow ZCode",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            string note;
            if (!ZcodeConnectionAdapter.TryAllowAndConnect(Settings, true, out note))
            { Note = note; Refresh(); return; }
            Note = note;
            BeginZcodeVerify();
        }

        // T-50: the FreeBuff permission dialog. T-51 P1-3: its body is DERIVED
        // by FreebuffSource.CredentialPermissionText from the production origin
        // set, so the destinations the user consents to are exactly the
        // destinations the transport can contact.
        void AllowFreebuffAndConnect()
        {
            string body = FreebuffSource.CredentialPermissionText();
            if (MessageBox.Show(this, body, "LIMISAW — allow FreeBuff",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            string note;
            if (!FreebuffConnectionAdapter.TryAllowAndConnect(Settings, true, out note))
            { Note = note; Refresh(); return; }
            Note = note;
            CheckAgain("freebuff");
        }

        void OpenVendor(string vendorId)
        {
            string note;
            if (vendorId == "zcode") ZcodeConnectionAdapter.TryOpenVendor(out note);
            else if (vendorId == "freebuff") FreebuffConnectionAdapter.TryOpenVendor(out note);
            else note = "No open action for " + vendorId;
            Note = note; Refresh();
        }

        // The explicit sign-in. One coordinator operation per vendor means a
        // second click while the flow is running cannot spawn a second login.
        void BeginSignIn(string vendorId)
        {
            Func<bool> signIn;
            if (!ConnectionAdapterRegistry.SignIn.TryGetValue(vendorId, out signIn) || signIn == null)
            { Note = "No sign-in flow for " + vendorId; Refresh(); return; }
            int gen = ConnCoordinator.Begin(vendorId);
            if (gen < 0) { Note = "A " + vendorId + " operation is already running"; Refresh(); return; }
            if (!signIn())
            {
                ConnCoordinator.Cancel(vendorId);
                Note = "Could not start the " + vendorId + " sign-in — is the CLI installed?";
                Refresh();
                return;
            }
            ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.WaitingForUser, "Waiting for sign-in — LIMISAW verifies automatically.");
            Note = "Sign-in started — LIMISAW checks automatically when you finish.";
            Refresh();
            StartWatcher(vendorId, gen, null);
        }

        // Sign in to an ADDITIONAL account: make it a named, one-click thing
        // instead of folklore about an environment variable. Same coordinator
        // discipline as BeginSignIn — one operation per vendor, so a second
        // click cannot make a third home.
        void BeginAddAccount(string vendorId)
        {
            Func<int, AddAccountOutcome> add;
            if (!ConnectionAdapterRegistry.AddAccount.TryGetValue(vendorId, out add) || add == null)
            { Note = "No second-account flow for " + vendorId; Refresh(); return; }
            if (vendorId == "codex" && MessageBox.Show(this,
                "Your browser may reuse the current ChatGPT login. Select or switch to the intended ChatGPT account in the browser. LIMISAW will add a Codex card only after it verifies a distinct account.",
                "Add Codex account", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;
            int gen = ConnCoordinator.Begin(vendorId);
            if (gen < 0) { Note = "A " + vendorId + " operation is already running"; Refresh(); return; }
            // The generation is minted BEFORE the vendor flow starts, so the
            // live vendor state that flow creates is owned by this generation
            // from its first instant.
            AddAccountOutcome outcome = add(gen);
            if (outcome == null || !outcome.Ok)
            {
                ConnCoordinator.Cancel(vendorId);
                string why = outcome == null ? "unknown" : (outcome.Error ?? "unknown");
                Note = why == "cli_missing"
                    ? "Could not start the sign-in — the " + vendorId + " CLI was not found"
                    : "Could not add a " + vendorId + " account (" + why + ")";
                Refresh();
                return;
            }
            ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.WaitingForUser,
                vendorId == "codex" ? "WAITING_FOR_DISTINCT_ACCOUNT — verifying the browser login" : "Waiting for sign-in — the new account gets its own card.");
            Note = vendorId == "codex"
                ? "Sign in to the intended ChatGPT account. LIMISAW is waiting to verify a distinct account in " + ShortHome(outcome.Home) + "."
                : "Sign in to the new account in the window that opened — it lives in "
                  + ShortHome(outcome.Home) + " and becomes its own card.";
            Refresh();
            if (vendorId == "codex")
            {
                string addedHome = outcome.Home;
                StartWatcher(vendorId, gen, () =>
                {
                    var verified = CodexConnectionAdapter.VerifyAddedHome(addedHome,
                        Stamp.Now + CodexConnectionAdapter.VerifyDeadlineS);
                    verified.Detail = CodexHomeDiscovery.HomeId(addedHome);
                    return ConnectionVerifyToConnection(vendorId, verified);
                });
            }
            else StartWatcher(vendorId, gen, null);
        }

        // The retry after an add that landed on an account already present. It
        // reuses the EXISTING duplicate home — nothing is created, nothing is
        // deleted, and the primary account is not touched. Device-code
        // authorization is what makes "different account" actually reachable:
        // the browser cannot decide it for the user any more.
        void BeginRetryDifferentAccount(string vendorId)
        {
            Func<int, AddAccountOutcome> retry;
            if (!ConnectionAdapterRegistry.RetryDifferentAccount.TryGetValue(vendorId, out retry) || retry == null)
            { Note = "No retry flow for " + vendorId; Refresh(); return; }
            if (MessageBox.Show(this,
                "LIMISAW will authorize the SAME second Codex home again — it does not create another one.\n\n"
                + "A one-time device code appears on the Codex card and a ChatGPT page opens.\n"
                + "If that page is already signed in as the account you added by mistake, switch account "
                + "there first, then approve the code.\n\nContinue?",
                "Retry with a different ChatGPT account",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;
            int gen = ConnCoordinator.Begin(vendorId);
            if (gen < 0) { Note = "A " + vendorId + " operation is already running"; Refresh(); return; }
            AddAccountOutcome outcome = retry(gen);
            if (outcome == null || !outcome.Ok)
            {
                ConnCoordinator.Cancel(vendorId);
                string why = outcome == null ? "unknown" : (outcome.Error ?? "unknown");
                Note = why == "cli_missing"
                    ? "Could not start the retry — the " + vendorId + " CLI was not found"
                    : why == "no_duplicate_home"
                      ? "No duplicate " + vendorId + " home to retry — nothing was created"
                      : "Could not start device authorization (" + why + ")";
                Refresh();
                return;
            }
            ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.WaitingForUser,
                "WAITING_FOR_DISTINCT_ACCOUNT — device authorization in " + ShortHome(outcome.Home));
            // The code itself is on the card, from the ephemeral holder, and is
            // never written anywhere that outlives the attempt.
            Note = "Device code shown on the Codex card — approve it as a DIFFERENT ChatGPT account. Same home: "
                + ShortHome(outcome.Home) + ".";
            Refresh();
            string retriedHome = outcome.Home;
            StartWatcher(vendorId, gen, () =>
            {
                var verified = CodexConnectionAdapter.VerifyAddedHome(retriedHome,
                    Stamp.Now + CodexConnectionAdapter.VerifyDeadlineS);
                verified.Detail = CodexHomeDiscovery.HomeId(retriedHome);
                return ConnectionVerifyToConnection(vendorId, verified);
            });
        }

        // "~\.claude-account2" reads; the full profile path does not.
        static string ShortHome(string path)
        {
            if (string.IsNullOrEmpty(path)) return "its own directory";
            string profile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(profile)
                && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                return "~" + path.Substring(profile.Length);
            return path;
        }

        // Install keeps its explicit confirmation; after the visible installer
        // starts, verification is AUTOMATIC (T-46): the watcher rediscovers
        // until the binary appears, then verifies. "Press Refresh" is gone.
        void BeginInstall(string vendorId)
        {
            string key = vendorId;
            var tool = Cli.Find(key);
            if (tool == null) return;
            var cli = new CliInfo { Key = tool.Key, Label = tool.Label, Command = tool.Command, PowerShell = tool.Command, Source = tool.Source, Target = tool.Target };
            var m = System.Text.RegularExpressions.Regex.Match(tool.Command, "-c(?:ommand)?\\s+\"(.+)\"\\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) cli.PowerShell = m.Groups[1].Value;
            int gen = ConnCoordinator.Begin(vendorId);
            if (gen < 0) { Note = "A " + vendorId + " operation is already running"; Refresh(); return; }
            string body = cli.Label + "\n\nThis runs the vendor's published installer:\n\n"
                + cli.Command + "\n\nPublisher: " + cli.Source
                + "\nInstalls to: " + cli.Target
                + "\n\nA PowerShell window will open so you can watch it. Continue?";
            if (MessageBox.Show(this, body, "LIMISAW — install " + cli.Label,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                ConnCoordinator.Cancel(vendorId);
                return;
            }
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoLogo -ExecutionPolicy Bypass -NoExit -Command \"" + cli.PowerShell.Replace("\"", "`\"") + "\"")
                { UseShellExecute = true };
                Process.Start(psi);
                Note = cli.Label + ": installer started — LIMISAW checks automatically when it finishes.";
            }
            catch (Exception ex)
            {
                ConnCoordinator.Cancel(vendorId);
                Note = cli.Label + ": could not start installer (" + ex.GetType().Name + ")";
                Refresh();
                return;
            }
            ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.WaitingForUser, "Waiting for the installer — LIMISAW checks automatically.");
            Refresh();
            StartWatcher(vendorId, gen, () =>
            {
                // Level 0 first: while the installer has not produced a binary,
                // keep waiting (null = no attempt result yet).
                ExecutableDiscovery.ZcodeAllowConfig = () => Settings.ZcodeReadConfig;
            ExecutableDiscovery.FreebuffAllowConfig = () => Settings.FreebuffReadConfig;
                // PERF-003 (SRC-006:R020): a targeted FRESH generation — an
                // install action must see the binary the moment it appears,
                // without waiting for the next Refresh to rebuild the snapshot.
                var vc = ExecutableDiscovery.BuildConnectionFresh(vendorId);
                if (!vc.Installed) return null;
                Func<ConnectionVerifyResult> verify;
                return ConnectionAdapterRegistry.Verify.TryGetValue(vendorId, out verify) && verify != null
                    ? ConnectionVerifyToConnection(vendorId, verify())
                    : vc;
            });
        }

        void BeginVerify(string vendorId)
        {
            int gen = ConnCoordinator.Begin(vendorId);
            if (gen < 0) { Note = "A " + vendorId + " operation is already running"; Refresh(); return; }
            ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.Verifying, "Verifying...");
            Refresh();
            RunConnectionAttempt(vendorId, gen, null);
        }

        void BeginTroubleshoot(string vendorId)
        {
            Func<VendorConnection, VendorConnection> doctor;
            if (!ConnectionAdapterRegistry.Doctor.TryGetValue(vendorId, out doctor) || doctor == null)
            { Note = "No deeper diagnostics for " + vendorId; Refresh(); return; }
            int gen = ConnCoordinator.Begin(vendorId);
            if (gen < 0) { Note = "A " + vendorId + " operation is already running"; Refresh(); return; }
            ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.Verifying, "Running vendor diagnostics...");
            Refresh();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                VendorConnection baseConn = ConnCoordinator.Latest(vendorId) ?? ExecutableDiscovery.BuildConnection(vendorId);
                VendorConnection result;
                try { result = doctor(baseConn); }
                catch (Exception ex)
                { result = new VendorConnection { VendorId = vendorId, State = ConnectionState.Failed, ErrorCode = ConnectionErrorCode.UnknownFailure, Reason = ex.GetType().Name }; }
                if (!ConnCoordinator.TryPublish(vendorId, gen, result)) return;
                MarshalConnectionRepaint();
            });
        }

        // One bounded verification attempt for the given vendor, published
        // under the given generation. Runs on a worker thread.
        void RunConnectionAttempt(string vendorId, int gen, Func<VendorConnection> customVerify)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                VendorConnection result = null;
                Func<ConnectionVerifyResult> verify;
                if (customVerify != null)
                {
                    try { result = customVerify(); }
                    catch (Exception ex)
                    { result = new VendorConnection { VendorId = vendorId, State = ConnectionState.Failed, ErrorCode = ConnectionErrorCode.UnknownFailure, Reason = ex.GetType().Name }; }
                }
                else if (vendorId == "zcode")
                {
                    var baseConn = ZcodeConnectionAdapter.Discover();
                    result = ZcodeConnectionAdapter.Verify(baseConn, Settings.ZcodeReadConfig);
                }
                else if (vendorId == "freebuff")
                {
                    // T-50: FreeBuff verification resolves its own Level-0 so the
                    // permission and credential category are read under the same
                    // owner the sweep uses.
                    bool found = FreebuffDiscovery.HasExecutable();
                    var baseConn = FreebuffConnectionAdapter.BuildLevel0(Settings.FreebuffReadConfig, found);
                    result = FreebuffConnectionAdapter.Verify(baseConn, Settings.FreebuffReadConfig, found);
                }
                else if (ConnectionAdapterRegistry.Verify.TryGetValue(vendorId, out verify) && verify != null)
                {
                    try { result = ConnectionVerifyToConnection(vendorId, verify()); }
                    catch (Exception ex)
                    { result = new VendorConnection { VendorId = vendorId, State = ConnectionState.Failed, ErrorCode = ConnectionErrorCode.UnknownFailure, Reason = ex.GetType().Name }; }
                }
                if (result == null) return;
                // CORE-002 (audit/6): a manual Verify / Check again / doctor
                // completion is terminal by construction — one attempt, one
                // result. TryPublish now also requires the operation to still
                // be active, so a late duplicate worker cannot overwrite the
                // authoritative result.
                if (!ConnCoordinator.TryPublish(vendorId, gen, result)) return;
                if (result.State == ConnectionState.Connected
                    || result.State == ConnectionState.ConnectedQuotaUnavailable
                    || result.State == ConnectionState.SignInRequired
                    || result.State == ConnectionState.Failed
                    || result.State == ConnectionState.UnsupportedConfiguration)
                {
                    ConnectionWatcher.CancelGeneration(vendorId, gen);
                    // R084: the same one-follow-up contract as the watcher path
                    // — a terminal success from a manual Verify/Check again asks
                    // for its single quota refresh through the coalescing gate.
                    if (result.State == ConnectionState.Connected
                        || result.State == ConnectionState.ConnectedQuotaUnavailable)
                        RequestFollowUpQuotaRefresh(vendorId, gen);
                }
                MarshalConnectionRepaint();
            });
        }

        static VendorConnection ConnectionVerifyToConnection(string vendorId, ConnectionVerifyResult r)
        {
            var vc = new VendorConnection
            {
                VendorId = vendorId,
                State = r.State,
                ErrorCode = r.Error,
                Reason = r.Reason,
                Authenticated = r.Authenticated,
                AuthKnown = r.Authenticated || r.State == ConnectionState.SignInRequired,
                Monitorable = r.Monitorable,
                VerificationOk = r.State == ConnectionState.Connected,
                ConnectivityKnown = r.State == ConnectionState.Connected || r.State == ConnectionState.Failed,
                ConnectivityOk = r.State == ConnectionState.Connected,
                CliVersion = r.CliVersion,
                Plan = r.Plan,
                Stage = r.State == ConnectionState.Connected || r.State == ConnectionState.ConnectedQuotaUnavailable
                    ? ConnectionStage.Quota : ConnectionStage.Authentication,
            };
            if (r.Error != ConnectionErrorCode.None)
                vc.RecommendedAction = ConnectionErrorPriority.RecommendedAction(r.Error, r.State);
            if (vendorId == "codex" && !string.IsNullOrEmpty(r.Detail)) vc.SelectedHomeId = r.Detail;
            return vc;
        }

        // The watcher owns nothing but its own schedule: each tick asks the
        // adapter for one read-only attempt under the SAME generation the
        // original action minted, so a newer action invalidates the older
        // watcher and a stale completion cannot publish.
        void StartWatcher(string vendorId, int gen, Func<VendorConnection> customVerify)
        {
            var op = new ConnectionWatcher.Operation
            {
                VendorId = vendorId,
                Generation = gen,
                Verify = () =>
                {
                    Func<ConnectionVerifyResult> verify;
                    if (customVerify != null) return RunAttemptInline(vendorId, gen, customVerify);
                    if (vendorId == "zcode")
                    {
                        var baseConn = ZcodeConnectionAdapter.Discover();
                        return ZcodeConnectionAdapter.Verify(baseConn, Settings.ZcodeReadConfig);
                    }
                    if (vendorId == "freebuff")
                    {
                        bool found = FreebuffDiscovery.HasExecutable();
                        // T-51 P1-2: an ACTIVE sign-in generation keeps waiting
                        // while the vendor login has not written a credential
                        // yet; null means "still waiting", never a terminal
                        // SignInRequired that would stop the watcher early.
                        return FreebuffConnectionAdapter.VerifySignIn(
                            Settings.FreebuffReadConfig, found);
                    }
                    if (ConnectionAdapterRegistry.Verify.TryGetValue(vendorId, out verify) && verify != null)
                        return RunAttemptInline(vendorId, gen, null);
                    return null;
                },
            };
            ConnectionWatcher.Start(op);
        }

        VendorConnection RunAttemptInline(string vendorId, int gen, Func<VendorConnection> customVerify)
        {
            Func<ConnectionVerifyResult> verify;
            if (customVerify != null) return customVerify();
            if (vendorId == "zcode")
            {
                var baseConn = ZcodeConnectionAdapter.Discover();
                return ZcodeConnectionAdapter.Verify(baseConn, Settings.ZcodeReadConfig);
            }
            if (vendorId == "freebuff")
            {
                bool found = FreebuffDiscovery.HasExecutable();
                // T-51 P1-2: inline attempts (StartWatcher's own Verify) obey
                // the same first-time sign-in contract as the async path.
                return FreebuffConnectionAdapter.VerifySignIn(
                    Settings.FreebuffReadConfig, found);
            }
            if (ConnectionAdapterRegistry.Verify.TryGetValue(vendorId, out verify) && verify != null)
                return ConnectionVerifyToConnection(vendorId, verify());
            return null;
        }

        void OnWatcherAttempt(ConnectionWatcher.Operation op)
        {
            // W2-002/R010: the shutdown gate owns the watcher. A timer tick
            // already captured before ConnectionWatcher.Shutdown ran must not
            // begin ANY vendor work — not even the progress publication.
            if (ShuttingDown) return;
            string vendorId = op.VendorId;
            int gen = op.Generation;
            ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.Verifying, "Verifying...");
            // CORE-001 (audit/7): TryProgress is coordinator-thread-safe, but the
            // repaint it needs is form-owned. This handler runs on the watcher's
            // ThreadPool worker, so the direct WinForms Refresh() that used to
            // sit here crossed the UI-thread boundary illegally. Marshal through
            // the one canonical connection repaint boundary instead: it drops
            // during shutdown/disposal or with no handle and never falls back to
            // worker-side UI execution.
            MarshalConnectionRepaint();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                VendorConnection result = null;
                // Test seam (W2-002 B): hold here between admission and the
                // gate recheck below, so a harness can run BeginShutdown
                // inside the gap and prove the recheck governs execution.
                Action beforeWork = BeforeVendorWork;
                if (beforeWork != null) beforeWork();
                // W2-002: recheck the gate immediately before the vendor
                // operation — the queue may have released this work after
                // shutdown began. No vendor process/network verification may
                // start after the shutdown gate wins.
                if (ShuttingDown) { ConnectionWatcher.AttemptFinished(vendorId, gen); return; }
                try { result = op.Verify(); }
                catch (Exception ex)
                { result = new VendorConnection { VendorId = vendorId, State = ConnectionState.Failed, ErrorCode = ConnectionErrorCode.UnknownFailure, Reason = ex.GetType().Name }; }
                // W2-002: the attempt may have run across BeginShutdown; its
                // result is teardown-era state and must not be published.
                if (ShuttingDown) { ConnectionWatcher.AttemptFinished(vendorId, gen); return; }
                // CORE-002 (audit/6): the watcher's own attempt slot is freed
                // only AFTER the result is classified. AttemptFinished before
                // classification let a slow attempt's next scheduled tick
                // queue a second same-generation verification whose later
                // publication overwrote the first result.
                if (result == null)
                {
                    // Still waiting (installer not finished, sign-in not done):
                    // a progress observation — ownership stays active, the
                    // cadence re-arms.
                    // CORE-002 (audit/7): publish the still-waiting observation
                    // WHILE this generation N still owns an ACTIVE operation,
                    // and only THEN re-arm the watcher. AttemptFinished first
                    // released the slot, so a PokePending-accelerated attempt
                    // B could complete terminally (Connected, active=false)
                    // and this late WaitingForUser could overwrite it. The
                    // coordinator's active-ownership guard now also refuses
                    // that stale same-generation publication; the ordering
                    // keeps N's single ownership window authoritative.
                    if (!ShuttingDown)
                        ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.WaitingForUser,
                            "Still waiting — LIMISAW keeps checking.");
                    ConnectionWatcher.AttemptFinished(vendorId, gen);
                    MarshalConnectionRepaint();
                    return;
                }
                // CORE-002 (audit/6): classify BEFORE touching the watcher.
                // Terminal for the watcher: the user finished (success), the
                // auth flow is over (SignInRequired), or the configuration is
                // fundamentally incompatible. Transient results keep the
                // bounded cadence running until the window expires.
                if (IsTerminalWatcherState(result.State))
                {
                    // Remove/complete the watcher FIRST so no tick can re-arm
                    // an attempt for a finished operation (a terminal result
                    // plus PokePending must never schedule another verify).
                    ConnectionWatcher.CancelGeneration(vendorId, gen);
                    if (ConnCoordinator.TryPublish(vendorId, gen, result)
                        && (result.State == ConnectionState.Connected
                            || result.State == ConnectionState.ConnectedQuotaUnavailable
                            || result.State == ConnectionState.DuplicateRemoteAccount))
                    {
                        // R084: ONE follow-up global quota refresh per
                        // successful onboarding operation — requested through
                        // the existing single-flight/PendingRefresh coalescing.
                        RequestFollowUpQuotaRefresh(vendorId, gen);
                    }
                    MarshalConnectionRepaint();
                    return;
                }
                // Non-terminal: publish the progress observation while
                // ownership stays ACTIVE, and only then re-arm the watcher.
                ConnCoordinator.TryProgressResult(vendorId, gen, result);
                ConnectionWatcher.AttemptFinished(vendorId, gen);
                MarshalConnectionRepaint();
            });
        }

        // CORE-002 (audit/6): the watcher-terminal classification. Connected
        // states end onboarding, SignInRequired means the auth flow is over,
        // UnsupportedConfiguration cannot improve by waiting. Failed /
        // NetworkTimeout / everything else is transient by contract: the
        // bounded cadence keeps retrying until the window expires.
        static bool IsTerminalWatcherState(ConnectionState s)
        {
            return s == ConnectionState.Connected
                || s == ConnectionState.ConnectedQuotaUnavailable
                || s == ConnectionState.DuplicateRemoteAccount
                || s == ConnectionState.SignInRequired
                || s == ConnectionState.UnsupportedConfiguration;
        }

        // R084/R031: exactly one follow-up request per (vendor, generation).
        // Called only from terminal-success watcher completions and their
        // equivalents in RunConnectionAttempt; repeated/duplicate signals for
        // the same operation collapse to the single recorded request.
        readonly HashSet<string> FollowUpRefreshDone = new HashSet<string>();
        void RequestFollowUpQuotaRefresh(string vendorId, int gen)
        {
            string key = vendorId + "#" + gen;
            lock (FollowUpRefreshDone)
            {
                if (!FollowUpRefreshDone.Add(key)) return;   // duplicate signal: still one
            }
            // The existing coalescing owns the rest: if a sweep is running,
            // RefreshData sets PendingRefresh and CompleteSweep runs exactly
            // one follow-up; if not, one sweep starts now. Never a parallel sweep.
            // CORE-001 (audit/7): the callers are terminal-success paths on the
            // connection worker, so the request crosses the UI boundary first —
            // RequestRefresh marshals to the form's thread and otherwise drops,
            // exactly like MarshalConnectionRepaint. The FollowUpRefreshDone gate
            // above still makes the request exactly-once per (vendor, generation).
            RequestRefresh();
        }

        // A watcher operation left the schedule without finishing — the window
        // expired, an explicit cancel removed it, or a newer generation
        // superseded it. Whatever vendor-side state that generation owned is
        // released HERE, exactly once, for that generation only.
        //
        // A terminal success/duplicate has already dropped its own ownership
        // before this runs, so the completed vendor login is never cancelled.
        void OnWatcherRemoved(ConnectionWatcher.Operation op)
        {
            if (op == null || op.VendorId != "codex") return;
            if (!CodexManagedLogin.PendingGeneration(op.Generation)) return;
            int gen = op.Generation;
            // During shutdown the bounded teardown path owns the cleanup and
            // must not start vendor work from a timer/UI thread.
            if (ShuttingDown) { CodexManagedLogin.CancelGeneration(gen, Stamp.Now + 2); return; }
            // Off the caller's thread: this is a bounded vendor round trip and
            // the caller may be the watcher timer or the UI.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { CodexManagedLogin.CancelGeneration(gen, Stamp.Now + 5); } catch { }
            });
        }

        // Bounded window expired: leave the user with the honest state and the
        // manual escape hatches — never a stuck "Verifying".
        void OnWatcherExpire(ConnectionWatcher.Operation op)
        {
            // W2-002/R010: an expiry that lands after the shutdown gate wins
            // mutates nothing — no Degraded downgrade, no repaint into a
            // dying form.
            if (ShuttingDown) return;
            // TIMEOUT: the operation that owned the vendor login is over, so
            // that login is cancelled — this generation's loginId, once. The
            // expiry path removes the operation itself, so OnRemoved does not
            // also fire for it; this is the single cleanup for an expiry.
            OnWatcherRemoved(op);
            // R082: Degraded with the escape hatches, never a stuck Verifying.
            ConnCoordinator.TryProgress(op.VendorId, op.Generation, ConnectionState.Degraded,
                "Still waiting for sign-in — use Check again after you finish.");
            // CORE-002 (audit/6): generation-SCOPED cancel. This expiry may
            // belong to a watcher that was already superseded by a newer
            // operation; the unscoped Cancel would have invalidated the
            // NEWER generation. A no-op when this generation is stale.
            ConnCoordinator.TryCancel(op.VendorId, op.Generation);
            MarshalConnectionRepaint();
        }

        // W2-002/R010: the one connection repaint marshal. A no-handle worker
        // execution used to land in the `else` and run BuildConnections/
        // Refresh/UpdateTray ON THE WORKER THREAD — the exact teardown race.
        // The gate order is explicit:
        //   1. any teardown state (shutdown gate, disposed, disposing) -> drop;
        //   2. no handle -> drop;
        //   3. handle live: marshal to the UI thread when needed, executing
        //      directly ONLY on the UI thread; if the handle vanishes before
        //      BeginInvoke, the repaint is dropped.
        void MarshalConnectionRepaint()
        {
            if (ShuttingDown || IsDisposed || Disposing) return;
            if (!IsHandleCreated) return;
            Action done = () => { RepaintThread = System.Threading.Thread.CurrentThread.ManagedThreadId; BuildConnections(); Refresh(); UpdateTray(); };
            try
            {
                if (InvokeRequired) BeginInvoke(done);
                else done();
            }
            catch { } // handle disappeared between the check and BeginInvoke: drop the repaint
        }

        void CheckAgain(string vendorId)
        {
            // An explicit Check again is manual rediscovery + verify, bounded,
            // no global Refresh, no F5.
            ExecutableDiscovery.ZcodeAllowConfig = () => Settings.ZcodeReadConfig;
            ExecutableDiscovery.FreebuffAllowConfig = () => Settings.FreebuffReadConfig;
            int gen = ConnCoordinator.Begin(vendorId);
            if (gen < 0) { Note = "A " + vendorId + " operation is already running"; Refresh(); return; }
            ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.Verifying, "Checking...");
            Refresh();
            // Antigravity rides the same generic adapter path as every other
            // CLI vendor: no second-class exception branch remains.
            RunConnectionAttempt(vendorId, gen, null);
        }

        void BeginZcodeVerify()
        {
            CheckAgain("zcode");
        }

        void CopyConnectionDiagnostics(string vendorId)
        {
            VendorConnection c = null;
            foreach (var v in Connections) if (v.VendorId == vendorId) c = v;
            if (c == null) return;
            string ver = "0.0.8";
            try { ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); } catch { }
            string report;
            string note;
            try { report = ConnectionDiagnostics.BuildReport(c, ver); }
            catch (Exception ex)
            {
                // Report generation fails safe too: a concise safe failure, no
                // crash, no secret in the exception text.
                note = "Diagnostics unavailable (" + ex.GetType().Name + ")";
                Note = note; Refresh(); return;
            }
            ConnectionDiagnostics.TryCopyToClipboard(report, out note);
            Note = note; Refresh();
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
        // The class of throw a paint path may legitimately hit at runtime.
        // Deliberately narrow: it buys a degraded panel, not a hidden bug.
        static bool IsRenderingFault(Exception ex)
        {
            return ex is OutOfMemoryException        // GDI+'s usual "the operation failed"
                || ex is ArgumentException
                || ex is InvalidOperationException
                || ex is System.Runtime.InteropServices.ExternalException
                || ex is ArithmeticException;
        }

        void PaintTrayPanel(Graphics g, int cursor, int w)
        {
            // PERF-004: ONE AllMetrics per paint transaction — taken by
            // OnPaint before the height was measured; TrayMetrics and every
            // helper derive from this same collection.
            List<Metric> all = TrayPaintAll ?? AllMetrics();
            List<string> hidden = HiddenMetricIds();
            int shown = SelectedMetrics(all).Count;
            DrawTextFit(g, "Tray shows " + shown + " of " + all.Count + " readings — drag a row or use the arrows:",
                14, cursor, w - 24, Palette.TEXT2, 10);
            cursor += 20;

            // Row 2 is laid out from measured labels, left to right, so the
            // "Reset order" button can never land on top of the counter.
            string resetLabel = "Reset order";
            int resetW = ButtonWidth(g, resetLabel);
            var reset = new Rectangle(w - 8 - resetW, cursor, resetW, 22);
            RegisterAction(reset, () => ResetTrayItems(), "forget the saved order, hidden rows and cap — discovery order shows again");
            DrawButton(g, reset, resetLabel, false);

            int labelW = TextWidth(g, "Max in tray", 10);
            DrawText(g, "Max in tray", 14, cursor + 5, Palette.TEXT2, 10);
            int x = 14 + labelW + Gap;
            var minus = new Rectangle(x, cursor, 24, 22); RegisterAction(minus, () => SetTrayMax(-1), "one fewer reading in the tray icon");
            DrawButton(g, minus, "-", false);
            x += 24 + Gap;
            string maxText = Settings.TrayMax.ToString();
            int maxW = Math.Max(14, TextWidth(g, maxText, 11));
            DrawText(g, maxText, x, cursor + 4, Palette.LINK, 11, true);
            x += maxW + Gap;
            var plus = new Rectangle(x, cursor, 24, 22); RegisterAction(plus, () => SetTrayMax(1), "one more reading in the tray icon (up to " + LimisawSettings.MaxTrayItems + ")");
            DrawButton(g, plus, "+", false);
            cursor += 28;

            if (all.Count == 0)
            {
                DrawText(g, Refreshing ? "Probing vendors..." : "No readings yet — press Refresh.", 14, cursor + 2, Palette.MUTED, 10);
                PaintedContentBottom = cursor + PickRowH;
                return;
            }

            // Column geometry, derived once and reused by both the header and
            // every row, so a header can never sit over a different column. The
            // reading name is the flexible column; gauge and percentage keep
            // visible minimums and the arrows/eye stay clickable at any width.
            int rightEdge = w - 8;
            bool narrow = w < 400;
            string eyeOn = narrow ? "off" : "shown", eyeOff = narrow ? "on" : "hidden";
            int eyeW = Math.Max(ButtonWidth(g, eyeOn), ButtonWidth(g, eyeOff)), arrowW = 22;
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

            // Ordered = the tray order, then the hidden ones so they stay
            // reachable (a hidden row is the only way back).
            // PERF-004: dictionary/HashSet membership, no nested scans. The
            // rows stay OBJECTS: two windows can share one metric id shape
            // only via their own account key, and every row in `all` keeps
            // its slot exactly as the old Contains-on-objects walk did.
            var firstById = new Dictionary<string, Metric>(all.Count);
            foreach (Metric m in all) if (!firstById.ContainsKey(m.Id)) firstById[m.Id] = m;
            var ordered = new List<Metric>(all.Count);
            var inOrder = new HashSet<Metric>();
            foreach (string id in PaintedOrder())
            {
                Metric m;
                if (firstById.TryGetValue(id, out m) && inOrder.Add(m)) ordered.Add(m);
            }
            foreach (Metric m in all) if (inOrder.Add(m)) ordered.Add(m);
            var hiddenSet = new HashSet<string>(hidden);

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
                bool off = hiddenSet.Contains(m.Id);
                bool held = Dragging && m.Id == DragId;
                if (!off) rank++;
                bool overflow = !off && rank > Settings.TrayMax;
                // PERF-004: cheap row geometry advances for EVERY row (rank
                // parity, cursor, PaintedContentBottom); the EXPENSIVE row
                // render — labels, gauges, three buttons, hints — runs only
                // for rows intersecting the viewport band. Offscreen rows stay
                // non-interactive by construction. A row may legitimately be
                // outside the tray's own list (off/overflow rows keep their
                // full geometry too, ItemRows excluded as before).
                if (BandBottom > BandTop &&
                    (cursor + PickRowH < BandTop || cursor > BandBottom))
                { cursor += PickRowH; continue; }
                ExpensiveRowPaints++;
                var row = new Rectangle(8, cursor, w - 16, PickRowH);
                ItemRows.Add(row); ItemRowIds.Add(m.Id);
                // The held row is highlighted, and the readings that DO reach
                // the tray get a faint plate so the cap is visible as a group.
                if (held) using (var bg = new SolidBrush(Palette.ALT)) g.FillRectangle(bg, row.X, row.Y, row.Width, row.Height);
                else if (!off && !overflow) using (var bg = new SolidBrush(Palette.SURFACE)) g.FillRectangle(bg, row.X, row.Y, row.Width, row.Height);

                Color name = off ? Palette.MUTED : overflow ? Palette.TEXT2 : Palette.TEXT;
                // The pinned reading for the single-number layout is marked here,
                // in the list that decides what the tray can draw at all.
                bool pinned = MigrateMetricId(Settings.TrayMetric) == m.Id;
                DrawText(g, off ? "-" : (overflow ? "·" : rank.ToString()), 16, cursor + 4, name, 10);
                DrawTextFit(g, (pinned ? "▸ " : "") + m.Label, nameX, cursor + 4, nameW, name, 10, false,
                    m.Label + (pinned ? " (pinned)" : "") + (off ? " — hidden" : overflow ? " — past the tray cap" : ""));
                Hint(row, pinned
                    ? "pinned: this reading IS the tray number (Number layout) — click Pin lowest to unpin"
                    : "click to pin this reading as the tray number, or drag to reorder");
                DrawText(g, m.Available ? ShownRem(m.Value) + "%" : "--", pctX, cursor + 4,
                    off || overflow ? Palette.MUTED : PctColor(m.Value), 10, true);
                DrawGauge(g, gaugeX, cursor + 7, gaugeW, 8, m.Available ? m.Value : 100, m.Available, off || overflow);

                string id2 = m.Id;
                var up = new Rectangle(upX, cursor + 2, arrowW, PickRowH - 4);
                RegisterAction(up, () => MoveItem(id2, -1), "move " + m.Short + " one row up in the tray order");
                DrawButton(g, up, "^", false);
                var down = new Rectangle(downX, cursor + 2, arrowW, PickRowH - 4);
                RegisterAction(down, () => MoveItem(id2, 1), "move " + m.Short + " one row down in the tray order");
                DrawButton(g, down, "v", false);
                var eye = new Rectangle(eyeX, cursor + 2, eyeW, PickRowH - 4);
                RegisterAction(eye, () => ToggleItem(id2),
                    off ? "show " + m.Short + " in the tray again (it stays refreshed while hidden)"
                        : "hide " + m.Short + " from the tray (it stays refreshed while hidden)");
                DrawButton(g, eye, off ? eyeOff : eyeOn, off);
                cursor += PickRowH;
            }

            // Insertion marker: where the held row would land on release.
            if (Dragging && dragFrom >= 0)
            {
                int slot = Math.Min(DropIndex(ordered.Count), ordered.Count);
                int lineY = ItemRowsTop + slot * PickRowH;
                using (var p = new Pen(Palette.LINK)) g.DrawLine(p, 10, lineY, w - 10, lineY);
            }
            PaintedContentBottom = cursor;
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
            // DragY is a CLIENT coordinate; CardTops and ItemRowsTop are VIRTUAL
            // content coordinates. The scroll offset translates between them —
            // and it is frozen during a drag (the wheel refuses while the
            // pointer is captured), so one translation is stable.
            int dragY = DragY + TabScroll[Tab];
            if (Tab == TabAccounts)
            {
                for (int i = 0; i < CardTops.Count && i < count; i++)
                    if (dragY < CardTops[i]) return Math.Max(0, i - 1);
                return Math.Max(0, count - 1);
            }
            int rel = dragY - ItemRowsTop;
            int slot = (rel + PickRowH / 2) / PickRowH;
            if (slot < 0) slot = 0;
            if (slot > count - 1) slot = count - 1;
            return slot;
        }

        // ── Zcode settings row ───────────────────────────────────────────────
        // What is actually true about Zcode right now, one line. The env key is
        // explicit authority and always wins; the config file is the permission
        // this row controls; the live account card carries the probe's own
        // answer (plan read / nothing readable / endpoint refused).
        string ZcodeRowStatus()
        {
            string env = ZcodeSource.EnvKey();
            if (env != null) return "using " + env + " — config not needed";
            if (!ZcodeSource.ConfigExists()) return "not detected";
            if (!Settings.ZcodeReadConfig) return "detected · config access off";
            foreach (AccountData a in Accounts)
            {
                if (a.Provider != "zcode") continue;
                if (a.Ok) return "config allowed · reading" + (a.Plan != null ? " · " + a.Plan : "");
                if (a.Carried) return "config allowed · last good reading";
                if (!string.IsNullOrEmpty(a.Error)) return a.Error;
                return "config access on · " + a.Status.ToLowerInvariant();
            }
            return Refreshing ? "config access on · probing" : "detected · config access on";
        }

        // ── FreeBuff settings row ────────────────────────────────────────────
        // What is true about FreeBuff right now, one line. FreeBuff is optional,
        // so the row only exists when it is positively detected. T-51 P1-1: the
        // row and its presence gate read the PUBLISHED refresh-generation
        // snapshot — no PATH/registry/Start-Menu discovery and no config
        // filesystem query runs on this projection path. The live card carries
        // the probe's own answer (balance read / nothing readable / endpoint
        // refused).
        string FreebuffRowStatus()
        {
            VendorConnection snap = ExecutableDiscovery.SnapshotLevel0("freebuff");
            bool present = ExecutableDiscovery.PublishedPresence("freebuff") == true;
            if (!present) return "not detected";
            bool configPresent = snap != null && snap.ConfigPresent;
            if (!configPresent) return "detected · not signed in";
            if (!Settings.FreebuffReadConfig) return "detected · credential access off";
            foreach (AccountData a in Accounts)
            {
                if (a.Provider != "freebuff") continue;
                if (a.Ok)
                {
                    foreach (BalanceData b in a.Balances)
                        if (b.Value.HasValue) return "credential allowed · " + b.Label + " " + b.Value.Value.ToString("0.#");
                    return "credential allowed · balance unavailable";
                }
                if (a.Carried) return "credential allowed · last good reading";
                if (!string.IsNullOrEmpty(a.Error)) return a.Error;
                return "credential access on · " + a.Status.ToLowerInvariant();
            }
            return Refreshing ? "credential access on · probing" : "detected · credential access on";
        }

        // Durable-before-authority, exactly like the Zcode toggle: a save that
        // failed leaves the access OFF again and the row says so.
        void ToggleFreebuffConfigAccess()
        {
            bool turningOn = !Settings.FreebuffReadConfig;
            Settings.FreebuffReadConfig = turningOn;
            Settings.Save();
            if (Settings.LastSaveFailed)
            {
                Settings.FreebuffReadConfig = !turningOn;
                Note = "FreeBuff credential access NOT saved — LIMISAW.ini not writable";
                Refresh();
                return;
            }
            int fg = ConnCoordinator.CurrentGeneration("freebuff");
            ConnCoordinator.InvalidateOperation("freebuff");
            ConnectionWatcher.CancelGeneration("freebuff", fg);
            Note = turningOn
                ? "FreeBuff credential access on — the next sweep reads your FreeBucks balance"
                : "FreeBuff credential access off — nothing is read";
            RefreshData();
        }

        // The grant becomes authoritative only when it is durable: a save that
        // failed leaves the access OFF again, never a silent half-state where
        // LIMISAW reads a key under a permission the ini does not record.
        void ToggleZcodeConfigAccess()
        {
            bool turningOn = !Settings.ZcodeReadConfig;
            Settings.ZcodeReadConfig = turningOn;
            Settings.Save();
            if (Settings.LastSaveFailed)
            {
                Settings.ZcodeReadConfig = !turningOn;
                Note = "Zcode config access NOT saved — LIMISAW.ini not writable";
                Refresh();
                return;
            }
            // One truth + CORE-002 (audit/6): the saved permission change is a
            // credential-authority change — the stale verdict is dropped AND
            // an in-flight verify under the old permission loses publication
            // authority. The generic coordinator invalidation, not a bare
            // Forget.
            int zg = ConnCoordinator.CurrentGeneration("zcode");
            ConnCoordinator.InvalidateOperation("zcode");
            ConnectionWatcher.CancelGeneration("zcode", zg);
            Note = turningOn
                ? "Zcode config access on — the next sweep reads Zcode's own key"
                : "Zcode config access off — env " + ZcodeSource.EnvPrimary + " still works";
            RefreshData();
        }

        // ── Settings tab: four compact blocks, one or two responsive columns ──
        // The old page was one ~936px column: every account as a permanent row
        // and every theme as a permanent button wall. It is now four scannable
        // blocks — TRAY, ACCOUNTS, ALERTS, APP — stacked below the two-column
        // breakpoint and side by side above it (TRAY+ACCOUNTS | ALERTS+APP).
        // The height is measured by running THIS code against a scratch bitmap
        // with registration suppressed, so measure and paint are the same
        // reality by construction, and the painted bottom is asserted against
        // the measured height in tests/layout_fit.cs.
        void PaintSettingsPanel(Graphics g, int cursor, int w)
        {
            int end = PaintSettingsBlocks(g, cursor, w);
            if (!Measuring) PaintedContentBottom = end;
        }

        int PaintSettingsBlocks(Graphics g, int cursor, int w)
        {
            if (w >= SettingsTwoColW)
            {
                int gap = 12;
                int colW = (w - 8 * 2 - gap) / 2;
                int lx = 8, rx = 8 + colW + gap;
                int left = PaintAccountsBlock(g, lx, PaintTrayBlock(g, lx, cursor, colW), colW);
                int right = PaintAppBlock(g, rx, PaintAlertsBlock(g, rx, cursor, colW), colW);
                return Math.Max(left, right);
            }
            int y = PaintTrayBlock(g, 8, cursor, w - 16);
            y = PaintAccountsBlock(g, 8, y + Gap, w - 16);
            y = PaintAlertsBlock(g, 8, y + Gap, w - 16);
            y = PaintAppBlock(g, 8, y + Gap, w - 16);
            return y;
        }

        // Widest label of one block, so each block's rows line up without
        // inheriting the widest label of the whole page.
        int BlockLabelW(Graphics g, params string[] labels)
        {
            int labelW = 0;
            foreach (string s in labels) labelW = Math.Max(labelW, TextWidth(g, s, 10));
            return labelW;
        }

        // A group heading with a rule to its right, at the block's own x.
        int SectionAt(Graphics g, int x, int y, int right, string title)
        {
            DrawText(g, title, x, y, Palette.MUTED, 10, true);
            int tw = TextWidth(g, title, 10);
            using (var p = new Pen(Palette.BDARK))
                g.DrawLine(p, x + tw + Gap, y + 6, right, y + 6);
            return y + SectionH;
        }

        // A row of equal-width buttons that WRAPS instead of cropping: when the
        // column is too narrow for every label at its own measured width, the
        // row splits into more sub-rows. Returns the number of sub-rows used —
        // the caller advances by rows * SetRowH, so measure and paint agree.
        int ButtonRow(Graphics g, int optX, int y, int right, string[] labels,
            Action<int> click, Func<int, string> hint, Func<int, bool> selected, bool enabled)
        {
            int widest = 0;
            foreach (string s in labels) widest = Math.Max(widest, ButtonWidth(g, s) + 2);
            int perRow = Math.Max(1, Math.Min(labels.Length, (right - optX + 2) / Math.Max(1, widest)));
            int rows = (labels.Length + perRow - 1) / perRow;
            int bw = Math.Max(widest, (right - optX) / perRow);
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var r = new Rectangle(optX + (i % perRow) * bw, y + 1 + (i / perRow) * SetRowH, bw - 2, BtnH);
                if (enabled) { Buttons.Add(r); ButtonActions.Add(() => click(idx)); Hint(r, hint(idx)); }
                DrawButton(g, r, labels[i], selected(i), enabled);
            }
            return rows;
        }

        // ── TRAY block ───────────────────────────────────────────────────────
        int PaintTrayBlock(Graphics g, int x, int y, int w)
        {
            int right = x + w;
            y = SectionAt(g, x, y, right, "TRAY");
            LimisawSettings.TrayModeDefinition curMode = LimisawSettings.ModeDef(Settings.TrayMode);
            bool drawsNumber = curMode.UsesNumberReadout;
            bool drawsFill = curMode.UsesFillDetail;
            bool multiReading = curMode.MultiReading;
            int labelW = BlockLabelW(g, "Tray layout", "Tray shows", "Fill detail", "Preview", "Readings");
            int optX = x + labelW + Gap * 2;

            // Which controls actually do anything depends on the layout: a fill
            // granularity means nothing when the icon draws a bare number, and a
            // number readout means nothing when the icon draws bars. A control
            // that is visibly dead is honest; one that silently ignores you is
            // what makes settings feel broken.
            HintRow(x, y, w, "what the 16x16 tray icon draws");
            DrawText(g, "Tray layout", x, y + 3, Palette.TEXT2, 10);
            var modeLabels = new List<string>();
            foreach (LimisawSettings.TrayModeDefinition d in LimisawSettings.TrayModes) modeLabels.Add(d.ShortLabel);
            LimisawSettings.TrayModeDefinition cur = LimisawSettings.ModeDef(Settings.TrayMode);
            y += ButtonRow(g, optX, y, right, modeLabels.ToArray(), i =>
            {
                LimisawSettings.TrayModeDefinition d = LimisawSettings.TrayModes[i];
                Settings.TrayMode = d.Id; Settings.Save();
                NoteAfterSave("Layout: " + d.ShortLabel);
                Refresh(); UpdateTray();
            }, i => LimisawSettings.TrayModes[i].Hint, i => Settings.TrayMode == LimisawSettings.TrayModes[i].Id, true) * SetRowH;

            // The readout belongs to the number, so it sits directly under the
            // layout that produces one and greys out for the ones that do not.
            HintRow(x, y, w, drawsNumber
                ? "what the number in the icon reads"
                : "only a number layout has a readout — pick Number or Two above");
            DrawText(g, "Tray shows", x, y + 3, drawsNumber ? Palette.TEXT2 : Palette.MUTED, 10);
            y += ButtonRow(g, optX, y, right, new[] { "Off", "%", "Time" }, i =>
            {
                string sv = new[] { "off", "pct", "time" }[i];
                string sl = new[] { "Off", "%", "Time" }[i];
                Settings.TrayShow = sv; Settings.Save();
                NoteAfterSave("Tray shows: " + sl); Refresh(); UpdateTray();
            }, i => new[] {
                "no number at all — the icon is the picture",
                "percent left (or used, see Numbers below)",
                "time until this window's own reset: 12m, 3h, 2d",
            }[i], i => Settings.TrayShow == new[] { "off", "pct", "time" }[i], drawsNumber) * SetRowH;

            HintRow(x, y, w, drawsFill
                ? "how coarsely a bar, row, gauge or cell fills — coarse reads faster at 16px"
                : "only Gauge, Bars, Rows and Cells fill — pick one of those above");
            DrawText(g, "Fill detail", x, y + 3, drawsFill ? Palette.TEXT2 : Palette.MUTED, 10);
            var fillShort = new List<string>();
            foreach (LimisawSettings.TrayFillDefinition fd in LimisawSettings.TrayFills) fillShort.Add(fd.ShortLabel);
            y += ButtonRow(g, optX, y, right, fillShort.ToArray(), i =>
            {
                LimisawSettings.TrayFillDefinition fd = LimisawSettings.TrayFills[i];
                Settings.TrayFill = fd.Value; Settings.Save();
                NoteAfterSave("Fill: " + fd.ShortLabel); Refresh(); UpdateTray();
            }, i => LimisawSettings.TrayFills[i].Hint, i => Settings.TrayFill == LimisawSettings.TrayFills[i].Value, drawsFill) * SetRowH;

            // ── the compact preview row ──────────────────────────────────────
            // One horizontal strip: the ACTUAL production bitmap 1:1, the 16x16
            // master art at 4x, the pretend slider and its percent. The picture
            // is drawn from a PRETEND quota — the question is "what does 5% look
            // like in this mode", which a healthy account can never show.
            HintRow(x, y, w, "drag to see any quota level in the layout above — nothing real changes");
            DrawText(g, "Preview", x, y + 3, Palette.TEXT2, 10);
            int pvX = optX;
            // PERF-001 (SRC-006:R018): the preview row advances a FIXED 70px and
            // the bitmap cannot change any height, so the measurement pass
            // performs zero preview/model/bitmap work. The painter renders the
            // preview once from the per-paint snapshot (zero extra AllMetrics).
            if (!Measuring)
            {
            using (Bitmap prev = RenderPreviewBitmap(TrayPaintAll))
            {
                int pw = prev.Width, ph = prev.Height;              // actual size, 1:1
                int zoom = 4, zw = 16 * zoom, zh = 16 * zoom;       // 4x of the 16x16 master art
                // The master art sits centred inside prev by the same rule
                // RenderBitmap uses; crop it so the zoom always magnifies the
                // 16x16 artwork, not the shell's padding.
                int scale = Math.Max(1, pw / 16);
                int art = 16 * scale, pad = (pw - art) / 2;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                g.DrawImage(prev, pvX, y + 2, pw, ph);
                using (var edge = new Pen(Palette.BEVEL)) g.DrawRectangle(edge, pvX, y + 2, pw - 1, ph - 1);
                var smallBox = new Rectangle(pvX, y + 2, pw, ph);
                Marks.Add(smallBox);
                Hint(smallBox, "the production tray bitmap at actual size (" + pw + " x " + ph + ")");
                g.DrawImage(prev, new Rectangle(pvX + pw + Gap * 2, y + 2, zw, zh),
                    new Rectangle(pad, pad, art, art), GraphicsUnit.Pixel);
                using (var edge = new Pen(Palette.BEVEL)) g.DrawRectangle(edge, pvX + pw + Gap * 2, y + 2, zw - 1, zh - 1);
                var box = new Rectangle(pvX + pw + Gap * 2, y + 2, zw, zh);
                Marks.Add(box);
                Hint(box, "the 16x16 master art at 4x, pixel for pixel — pretend " + Settings.PreviewPct + "% remaining");
                pvX += pw + Gap * 2 + zw + Gap * 2;
            }
            string pvText = ShownRem(Settings.PreviewPct) + "%";
            int pvNumW = TextWidth(g, pvText, 11);
            int pvSliderW = Math.Max(60, right - pvX - pvNumW - Gap * 2);
            PaintVolSlider(g, pvX, y, pvSliderW, 0, 100, Settings.PreviewPct, "preview");
            Hint(new Rectangle(pvX, y + 1, pvSliderW, BtnH), "pretend quota for the preview only");
            pvX += pvSliderW + Gap;
            DrawText(g, pvText, pvX, y + 2, PctColor(Settings.PreviewPct), 11, true);
            Marks.Add(new Rectangle(pvX, y + 2, pvNumW, 15));
            }
            y += 70;

            HintRow(x, y, w, multiReading
                ? "how many readings reach the icon, and in what order"
                : "one number shows one reading — the Tray tab picks which");
            DrawText(g, "Readings", x, y + 3, Palette.TEXT2, 10);
            // PERF-001 (SRC-006:R018): the count label cannot change the row's
            // fixed height, so the measurement pass skips both metric walks;
            // the painter counts from the per-paint snapshot.
            int shownCount = Measuring ? 0 : TrayMetrics().Count;
            int totalCount = Measuring ? 0 : (TrayPaintAll ?? AllMetrics()).Count;
            string pickLabel = multiReading
                ? shownCount + " of " + totalCount + " — pick and reorder"
                : "pick which reading";
            int pickW = Math.Min(right - optX, ButtonWidth(g, pickLabel));
            var pickBtn = new Rectangle(optX, y + 1, pickW, BtnH);
            Buttons.Add(pickBtn); ButtonActions.Add(() => ShowTab(TabTray));
            Hint(pickBtn, "opens the Tray tab, where readings are ordered, hidden and capped");
            DrawButton(g, pickBtn, pickLabel, false);
            y += SetRowH;
            return y;
        }

        // ── ACCOUNTS block ───────────────────────────────────────────────────
        // Zcode's credential permission, the two display filters, and the
        // compact visibility manager — one row that reports the shown/hidden
        // balance and opens every discovered account through a themed menu.
        // The full always-expanded list is gone; every account stays
        // recoverable from the menu.
        int PaintAccountsBlock(Graphics g, int x, int y, int w)
        {
            int right = x + w;
            y = SectionAt(g, x, y, right, "ACCOUNTS");
            int labelW = BlockLabelW(g, "Zcode", "Hide spent", "Only 5h", "Accounts");
            int optX = x + labelW + Gap * 2;

            // ── Zcode: the one credential that needs explicit permission ────
            // The state text says what is actually true (env key in use /
            // detected but closed / allowed and reading / allowed but nothing
            // readable / not detected), and the toggle persists BEFORE the
            // access it grants becomes authoritative.
            HintRow(x, y, w, Settings.ZcodeReadConfig
                ? "LIMISAW may read the Z.ai key out of Zcode's config — toggle off to stop"
                : "let LIMISAW read the Z.ai key from Zcode's own config — nothing is read while off");
            DrawText(g, "Zcode", x, y + 3, Palette.TEXT2, 10);
            string zcState = ZcodeRowStatus();
            int zcBtnW = ButtonWidth(g, Settings.ZcodeReadConfig ? "Config access: on" : "Config access: off");
            var zcBtn = new Rectangle(right - zcBtnW, y + 1, zcBtnW, BtnH);
            Buttons.Add(zcBtn); ButtonActions.Add(ToggleZcodeConfigAccess);
            Hint(new Rectangle(optX, y + 1, right - optX - zcBtnW - Gap, BtnH),
                "the credential permission itself — env ZAI_API_KEY needs no switch");
            DrawTextFit(g, zcState, optX, y + 3, right - optX - zcBtnW - Gap * 2, Palette.TEXT2, 10);
            DrawButton(g, zcBtn, Settings.ZcodeReadConfig ? "Config access: on" : "Config access: off",
                Settings.ZcodeReadConfig);
            y += SetRowH;

            // ── FreeBuff: the OPTIONAL vendor's credential permission ────────
            // The row appears only when FreeBuff is positively detected, so the
            // four core vendors' settings surface is unchanged when it is not.
            // T-51 P1-1: the gate reads the PUBLISHED generation fact; this
            // paint path performs no discovery I/O.
            if (ExecutableDiscovery.PublishedPresence("freebuff") == true)
            {
                HintRow(x, y, w, Settings.FreebuffReadConfig
                    ? "LIMISAW may read FreeBuff's stored token to fetch your FreeBucks balance — toggle off to stop"
                    : "let LIMISAW read FreeBuff's stored token for your FreeBucks balance — nothing is read while off");
                DrawText(g, "FreeBuff", x, y + 3, Palette.TEXT2, 10);
                string fbState = FreebuffRowStatus();
                int fbBtnW = ButtonWidth(g, Settings.FreebuffReadConfig ? "Credential access: on" : "Credential access: off");
                var fbBtn = new Rectangle(right - fbBtnW, y + 1, fbBtnW, BtnH);
                Buttons.Add(fbBtn); ButtonActions.Add(ToggleFreebuffConfigAccess);
                Hint(new Rectangle(optX, y + 1, right - optX - fbBtnW - Gap, BtnH),
                    "the credential permission itself — read-only, used only to fetch your FreeBucks balance");
                DrawTextFit(g, fbState, optX, y + 3, right - optX - fbBtnW - Gap * 2, Palette.TEXT2, 10);
                DrawButton(g, fbBtn, Settings.FreebuffReadConfig ? "Credential access: on" : "Credential access: off",
                    Settings.FreebuffReadConfig);
                y += SetRowH;
            }

            // Both filters are DISPLAY-only: the sweep keeps probing every
            // account, so a filtered-out account reappears the moment its quota
            // returns. Hiding it here would be a monitor that stops monitoring.
            HintRow(x, y, w, Settings.HideSpentAccounts
                ? "accounts with nothing left are hidden — they still refresh, and come back when quota does"
                : "hide accounts whose every window reads 0% — they still refresh");
            DrawText(g, "Hide spent", x, y + 3, Palette.TEXT2, 10);
            int hideW = ButtonWidth(g, "on");
            var hsBtn = new Rectangle(optX, y + 1, hideW, BtnH);
            RegisterAction(hsBtn, () =>
            {
                Settings.HideSpentAccounts = !Settings.HideSpentAccounts; Settings.Save();
                NoteAfterSave("Hide spent " + (Settings.HideSpentAccounts ? "on" : "off"));
                Refresh();
            }, "filter the Accounts tab by what has quota left — display only, nothing stops refreshing");
            DrawButton(g, hsBtn, Settings.HideSpentAccounts ? "on" : "off", Settings.HideSpentAccounts);
            y += SetRowH;

            HintRow(x, y, w, Settings.OnlyWithFiveHour
                ? "only accounts with a usable 5h window are shown — the rest still refresh"
                : "keep only accounts whose 5h window is actually usable right now");
            DrawText(g, "Only 5h", x, y + 3, Palette.TEXT2, 10);
            var ofBtn = new Rectangle(optX, y + 1, hideW, BtnH);
            RegisterAction(ofBtn, () =>
            {
                Settings.OnlyWithFiveHour = !Settings.OnlyWithFiveHour; Settings.Save();
                NoteAfterSave("Only 5h " + (Settings.OnlyWithFiveHour ? "on" : "off"));
                Refresh();
            }, "keep only accounts whose 5-hour window is usable now — display only, nothing stops refreshing");
            DrawButton(g, ofBtn, Settings.OnlyWithFiveHour ? "on" : "off", Settings.OnlyWithFiveHour);
            y += SetRowH;

            // The compact visibility manager: one row reports the balance, the
            // menu exposes every discovered account with its live shown/hidden
            // check. Same HiddenAccounts state as before — no new persistence,
            // just six permanent rows off the page.
            HintRow(x, y, w, "which accounts the Accounts tab draws — hidden ones keep refreshing either way");
            DrawText(g, "Accounts", x, y + 3, Palette.TEXT2, 10);
            List<string> accHidden = Settings.HiddenAccountList();
            int total = OrderedAccounts().Count;
            int shownN = total - accHidden.Count;
            DrawTextFit(g, total == 0 ? "none discovered yet" : shownN + " shown · " + accHidden.Count + " hidden",
                optX, y + 3, Math.Max(10, right - optX - ButtonWidth(g, "Visibility...") - Gap * 2),
                accHidden.Count > 0 ? Palette.TEXT2 : Palette.MUTED, 10);
            int visW = ButtonWidth(g, "Visibility...");
            var visBtn = new Rectangle(right - visW, y + 1, visW, BtnH);
            Buttons.Add(visBtn); ButtonActions.Add(() => ShowMenuBelow(BuildVisibilityMenu(), visBtn));
            Hint(visBtn, "every discovered account, shown or hidden — an account hidden on its card is recovered here");
            DrawButton(g, visBtn, "Visibility...", false);
            y += SetRowH;
            return y;
        }

        // ── ALERTS block ─────────────────────────────────────────────────────
        // One volume, two alert rows (balloon + chime + WAV + preview, built by
        // the shared AlertRow), one threshold row under the alert it arms.
        int PaintAlertsBlock(Graphics g, int x, int y, int w)
        {
            int right = x + w;
            y = SectionAt(g, x, y, right, "ALERTS");
            int labelW = BlockLabelW(g, "Volume", "On refill", "Low alert", "Low at");
            int optX = x + labelW + Gap * 2;

            // CORE-005: the volume belongs to the CHIMES, so it is the two chime
            // switches that decide whether it can matter — NotifyLow is a
            // balloon switch and was never evidence that a sound would play.
            bool anySound = Settings.ResetSound || Settings.LowSound;
            HintRow(x, y, w, anySound
                ? "one volume for every alert — Windows has no per-sound volume, so the WAV itself is scaled"
                : "both alert sounds are off, so there is nothing to set a volume for");
            DrawText(g, "Volume", x, y + 3, anySound ? Palette.TEXT2 : Palette.MUTED, 10);
            int cx = optX;
            string volText = Settings.SoundVolume + "%";
            int volNumW = TextWidth(g, volText, 11);
            int dirW = ButtonWidth(g, "Folder");
            int volSliderW = Math.Max(60, Math.Min(160, right - cx - volNumW - dirW - Gap * 4));
            PaintVolSlider(g, cx, y, volSliderW, 0, 100, Settings.SoundVolume, "volume", anySound);
            if (anySound) Hint(new Rectangle(cx, y + 1, volSliderW, BtnH), "alert volume, 0 = silent");
            cx += volSliderW + Gap;
            DrawText(g, volText, cx, y + 2, anySound ? Palette.LINK : Palette.MUTED, 11, true);
            Marks.Add(new Rectangle(cx, y + 2, volNumW, 15));
            cx += volNumW + Gap;
            var dirBtn = new Rectangle(cx, y + 1, dirW, BtnH);
            Buttons.Add(dirBtn); ButtonActions.Add(() => PickSoundDir());
            Hint(dirBtn, "where the WAV pickers look — the shipped sounds keep working either way");
            DrawButton(g, dirBtn, "Folder", false);
            cx += dirW + Gap;
            // PERF-002 (SRC-006:R019): the displayed library path is the
            // snapshot resolved off the paint path — one read, no I/O.
            string libDisplay = SoundDisplayPath;
            DrawTextFit(g, libDisplay, cx, y + 3, right - cx, Palette.MUTED, 10, false, libDisplay);
            y += SetRowH;

            // Both alerts have the same shape — a balloon switch, a chime switch,
            // the WAV and a preview — so they are drawn by one row builder. The
            // old panel had the reset chime, the low threshold and the two sounds
            // in four unrelated shapes, which is what made it unreadable.
            y = AlertRow(g, x, y, right, optX,
                "On refill", "a quota window refilled",
                Settings.NotifyOnReset, () =>
                {
                    Settings.NotifyOnReset = !Settings.NotifyOnReset; Settings.Save();
                    NoteAfterSave("Refill balloon " + (Settings.NotifyOnReset ? "on" : "off")); Refresh();
                },
                "balloon: a Windows notification when a 5h or weekly window resets",
                Settings.ResetSound, () =>
                {
                    Settings.ResetSound = !Settings.ResetSound; Settings.Save();
                    NoteAfterSave("Refill chime " + (Settings.ResetSound ? "on" : "off")); Refresh();
                },
                "chime: play a sound when a window resets",
                Settings.ResetSoundFile,
                () => { string picked = PickSound(Settings.ResetSoundFile); if (picked == null) return;
                        Settings.ResetSoundFile = picked; Settings.Save(); NoteAfterSave("Refill sound: " + picked); Refresh(); },
                () => Preview(Settings.ResetSoundFile));

            // CORE-005: the low alert is the SAME two-switch row as the refill
            // one — one row builder, one shape, both channels reachable.
            y = AlertRow(g, x, y, right, optX,
                "Low alert", "a window drops to the threshold below",
                Settings.NotifyLow, () =>
                {
                    Settings.NotifyLow = !Settings.NotifyLow; Settings.Save();
                    NoteAfterSave("Low balloon " + (Settings.NotifyLow ? "on" : "off")); Refresh();
                },
                "balloon: a Windows notification when a window drops to the threshold below",
                Settings.LowSound, () =>
                {
                    Settings.LowSound = !Settings.LowSound; Settings.Save();
                    NoteAfterSave("Low chime " + (Settings.LowSound ? "on" : "off")); Refresh();
                },
                "chime: play a sound when a window drops to the threshold below",
                Settings.LowSoundFile,
                () => { string picked = PickSound(Settings.LowSoundFile); if (picked == null) return;
                        Settings.LowSoundFile = picked; Settings.Save(); NoteAfterSave("Low sound: " + picked); Refresh(); },
                () => Preview(Settings.LowSoundFile));

            // The threshold owns its own row, directly under the alert it arms:
            // a slider three rows away from the switch it feeds is a guess. It is
            // the EVENT's level, so either channel keeps it live.
            bool lowArmed = Settings.NotifyLow || Settings.LowSound;
            HintRow(x, y, w, lowArmed
                ? "the level the low alert fires at, once per window per reset cycle"
                : "both low channels are off, so there is no level to fire at");
            DrawText(g, "Low at", x, y + 3, lowArmed ? Palette.TEXT2 : Palette.MUTED, 10);
            cx = optX;
            string pctText = "at " + Settings.LowPct + "%";
            int pctNumW = TextWidth(g, pctText, 11);
            int lowSliderW = Math.Max(60, Math.Min(160, right - cx - pctNumW - Gap * 3));
            PaintVolSlider(g, cx, y, lowSliderW, 5, 95, Settings.LowPct, "lowpct", lowArmed);
            if (lowArmed)
                Hint(new Rectangle(cx, y + 1, lowSliderW, BtnH), "fire the low alert when a window drops to this much left");
            cx += lowSliderW + Gap;
            DrawText(g, pctText, cx, y + 2, lowArmed ? Palette.LINK : Palette.MUTED, 11, true);
            Marks.Add(new Rectangle(cx, y + 2, pctNumW, 15));
            y += SetRowH;
            return y;
        }

        // ── APP block ────────────────────────────────────────────────────────
        // Numbers toggle, refresh cadence, the always-reachable controls row
        // (Autostart / Always on top / Open the ini, wrapped — never dropped on
        // a narrow width), and the compact theme selector: [<] name [>] plus
        // All... for the full list. Every theme stays reachable.
        int PaintAppBlock(Graphics g, int x, int y, int w)
        {
            int right = x + w;
            y = SectionAt(g, x, y, right, "APP");
            int labelW = BlockLabelW(g, "Numbers", "Refresh", "Controls", "Theme");
            int optX = x + labelW + Gap * 2;

            HintRow(x, y, w, "whether every percentage counts what is LEFT or what is SPENT");
            DrawText(g, "Numbers", x, y + 3, Palette.TEXT2, 10);
            int cx = optX;
            string[] usedLabels = { "Left", "Used" };
            string[] usedHints = {
                "count down: 20% means 20% of the quota is still yours",
                "count up: 80% means 80% is spent — bars fill as you work",
            };
            int uw = Math.Max(ButtonWidth(g, "Left"), ButtonWidth(g, "Used"));
            for (int i = 0; i < usedLabels.Length; i++)
            {
                bool wantUsed = i == 1;
                var r = new Rectangle(cx, y + 1, uw, BtnH);
                Buttons.Add(r); ButtonActions.Add(() =>
                { if (Settings.ShowUsed != wantUsed) ToggleShowUsed(); });
                Hint(r, usedHints[i]);
                DrawButton(g, r, usedLabels[i], wantUsed ? Settings.ShowUsed : !Settings.ShowUsed);
                cx += uw + Gap;
            }
            cx += Gap;
            DrawTextFit(g, "colours always warn on what is left", cx, y + 3, right - cx, Palette.MUTED, 10);
            y += SetRowH;

            HintRow(x, y, w, "how often LIMISAW asks the vendors — one sweep runs every vendor CLI");
            DrawText(g, "Refresh", x, y + 3, Palette.TEXT2, 10);
            cx = optX;
            var slower = new Rectangle(cx, y + 1, 24, BtnH);
            Buttons.Add(slower); ButtonActions.Add(() => SetRefresh(60));
            Hint(slower, "ask less often");
            DrawButton(g, slower, "-", false);
            cx += 24 + Gap;
            string mins = (Settings.RefreshSeconds / 60) + " min";
            int minsW = Math.Max(TextWidth(g, mins, 11), TextWidth(g, "60 min", 11));
            DrawText(g, mins, cx, y + 2, Palette.LINK, 11, true);
            Marks.Add(new Rectangle(cx, y + 2, minsW, 15));
            cx += minsW + Gap;
            var faster = new Rectangle(cx, y + 1, 24, BtnH);
            Buttons.Add(faster); ButtonActions.Add(() => SetRefresh(-60));
            Hint(faster, "ask more often");
            DrawButton(g, faster, "+", false);
            cx += 24 + Gap * 2;
            // The version is read from the exe's own metadata, which build.ps1
            // stamps from VERSION — one source of truth, and a bug report can
            // finally name the build it came from.
            string version = "";
            try { version = GetType().Assembly.GetName().Version.ToString(3); } catch { }
            if (version.Length > 0)
                DrawTextFit(g, "v" + version, cx, y + 2, right - cx, Palette.MUTED, 11);
            y += SetRowH;

            // R012: the Autostart button shows the DESIRED state; a registry
            // mismatch the reconciler could not verify is appended, so
            // "Autostart: on" never silently claims a startup entry Windows
            // does not actually hold. FlowButtons WRAPS: a control that does
            // not fit this width moves to the next sub-row — it never
            // disappears the way the old width gates dropped it.
            HintRow(x, y, w, "how LIMISAW lives on this machine — every control here stays reachable at any width");
            DrawText(g, "Controls", x, y + 3, Palette.TEXT2, 10);
            string autoLabel = (Settings.AutoStart ? "Autostart: on" : "Autostart: off")
                + (AutostartNotApplied ? " (not applied)" : "");
            string topLabel = Settings.AlwaysOnTop ? "On top: on" : "On top: off";
            y = FlowButtons(g, optX, y, right, new[] { autoLabel, topLabel, "Open the ini" },
                new[] { Settings.AutoStart, Settings.AlwaysOnTop, false }, i =>
                {
                    if (i == 0) ToggleAutostart();
                    else if (i == 1) ToggleTopMost();
                    else OpenIni();
                }, i => new[] {
                    "start LIMISAW with Windows, silently in the tray",
                    "keep the window above every other window (Alt+A)",
                    Settings.IniPath,
                }[i]);
            y += Gap;

            // The compact theme selector: previous / current name / next, plus
            // All... for every loaded theme. No theme is unreachable, no
            // native white ComboBox, and the wall of nine theme rows is gone.
            HintRow(x, y, w, "colours for the window, the tray icon and the hover panel — T cycles from anywhere");
            DrawText(g, "Theme", x, y + 3, Palette.TEXT2, 10);
            // A palette that failed to parse is named HERE, where the user is
            // looking for themes — not in some log.
            if (Theme.LoadErrors.Count > 0)
            {
                string why = Theme.LoadErrors[0];
                DrawTextFit(g, why, optX, y + 3, right - optX, Palette.DANGERTXT, 10, false,
                    why + (Theme.LoadErrors.Count > 1 ? "  (+" + (Theme.LoadErrors.Count - 1) + " more)" : ""));
                Hint(new Rectangle(optX - 4, y, right - optX + 4, SetRowH),
                    why + (Theme.LoadErrors.Count > 1
                        ? "  (+" + (Theme.LoadErrors.Count - 1) + " more broken palette file(s))"
                        : "  — fix the file and it will load without a restart"));
                y += SetRowH;
                HintRow(x, y, w, "colours for the window, the tray icon and the hover panel — T cycles from anywhere");
            }
            cx = optX;
            int at = ThemeIndex();
            string curLabel = at >= 0 && at < Themes.Count ? Themes[at].Label : "(no themes)";
            int arrowW2 = 24;
            var prevBtn = new Rectangle(cx, y + 1, arrowW2, BtnH);
            Buttons.Add(prevBtn); ButtonActions.Add(() => CycleThemeTo(-1));
            Hint(prevBtn, "the previous theme");
            DrawButton(g, prevBtn, "<", false);
            cx += arrowW2 + Gap;
            int nameW2 = Math.Min(right - cx - ButtonWidth(g, "All...") - arrowW2 - Gap * 3, ButtonWidth(g, curLabel));
            if (nameW2 < 40) nameW2 = Math.Max(40, Math.Min(nameW2, right - cx - ButtonWidth(g, "All...") - arrowW2 - Gap * 2));
            var curBtn = new Rectangle(cx, y + 1, nameW2, BtnH);
            Buttons.Add(curBtn); ButtonActions.Add(() => ShowMenuBelow(BuildThemeMenu(), curBtn));
            Hint(curBtn, "the current theme — click for every loaded theme");
            DrawButton(g, curBtn, curLabel, true);
            cx += nameW2 + Gap;
            var nextBtn = new Rectangle(cx, y + 1, arrowW2, BtnH);
            Buttons.Add(nextBtn); ButtonActions.Add(() => CycleThemeTo(1));
            Hint(nextBtn, "the next theme");
            DrawButton(g, nextBtn, ">", false);
            cx += arrowW2 + Gap;
            var allBtn = new Rectangle(right - ButtonWidth(g, "All..."), y + 1, ButtonWidth(g, "All..."), BtnH);
            Buttons.Add(allBtn); ButtonActions.Add(() => ShowMenuBelow(BuildThemeMenu(), allBtn));
            Hint(allBtn, "every loaded theme — nothing is unreachable");
            DrawButton(g, allBtn, "All...", false);
            y += SetRowH;
            return y;
        }

        int ThemeIndex()
        {
            for (int i = 0; i < Themes.Count; i++)
                if (string.Equals(Themes[i].Slug, Settings.ThemeSlug, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        void SetTheme(string slug)
        {
            foreach (Theme t in Themes)
                if (string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase))
                {
                    Settings.ThemeSlug = t.Slug; Settings.Save(); ApplyTheme(t.Slug);
                    NoteAfterSave("Theme: " + t.Label); Refresh(); UpdateTray();
                    return;
                }
        }

        // Direction -1 / +1 from the current theme, wrapping; the T shortcut
        // keeps cycling forward through everything.
        void CycleThemeTo(int direction)
        {
            if (Themes.Count == 0) return;
            int at = ThemeIndex();
            if (at < 0) at = 0;
            int next = ((at + direction) % Themes.Count + Themes.Count) % Themes.Count;
            SetTheme(Themes[next].Slug);
        }

        // The theme list as a themed context menu — the same renderer the tray
        // menu uses, no native white ComboBox. Built fresh per open so a newly
        // dropped palette file appears without a restart.
        internal ContextMenuStrip BuildThemeMenu()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new MenuRenderer();
            m.BackColor = Palette.SURFACE; m.ForeColor = Palette.TEXT;
            m.ShowImageMargin = false;
            m.Font = Pix.Get(11);
            int active = ThemeIndex();
            for (int i = 0; i < Themes.Count; i++)
            {
                Theme choice = Themes[i];
                var item = new ToolStripMenuItem(choice.Label, null, (o, e) => SetTheme(choice.Slug))
                { Checked = i == active,
                  ToolTipText = "switch the window and tray to " + choice.Label };
                m.Items.Add(item);
            }
            return m;
        }

        // Every discovered account, checked = shown. Show all recovers a fleet
        // hidden card by card. Same HiddenAccounts state the card "x" button
        // and the old always-expanded rows used — no new persistence.
        internal ContextMenuStrip BuildVisibilityMenu()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new MenuRenderer();
            m.BackColor = Palette.SURFACE; m.ForeColor = Palette.TEXT;
            m.ShowImageMargin = false;
            m.Font = Pix.Get(11);
            List<string> hidden = Settings.HiddenAccountList();
            foreach (AccountData a in OrderedAccounts())
            {
                AccountData target = a;
                var item = new ToolStripMenuItem(AccountTitle(a), null,
                    (o, e) => ToggleAccountHidden(target.Key))
                { Checked = !hidden.Contains(a.Key),
                  ToolTipText = hidden.Contains(a.Key)
                      ? "show this account on the Accounts tab again (it stays refreshed either way)"
                      : "hide this account from the Accounts tab (unhide here or in Settings)" };
                m.Items.Add(item);
            }
            m.Items.Add(new ToolStripSeparator());
            var showAll = m.Items.Add("Show all", null, (o, e) =>
            {
                Settings.SetHiddenAccounts(new List<string>()); Settings.Save();
                Note = "All accounts shown"; Refresh();
            }) as ToolStripMenuItem;
            if (showAll != null) showAll.ToolTipText = "unhide every account at once";
            return m;
        }

        void ShowMenuBelow(ContextMenuStrip menu, Rectangle anchor)
        {
            try { menu.Show(this, new Point(Math.Max(0, anchor.Left - 4), anchor.Bottom + 2)); }
            catch (Exception ex) { Note = "Could not open the menu (" + ex.GetType().Name + ")"; Refresh(); }
        }

        // Left-to-right buttons that WRAP to the next sub-row when the width
        // runs out. The old width gates (`if (x + w <= right)`) dropped their
        // control silently; a wrapped control is still on the page. `right`
        // is the ABSOLUTE right edge the row may fill.
        int FlowButtons(Graphics g, int x, int y, int right, string[] labels, bool[] states,
            Action<int> click, Func<int, string> hint)
        {
            int cx = x;
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                int bw = ButtonWidth(g, labels[i]);
                if (cx > x && cx + bw > right) { cx = x; y += SetRowH; }
                var r = new Rectangle(cx, y + 1, Math.Min(bw, Math.Max(20, right - cx)), BtnH);
                Buttons.Add(r); ButtonActions.Add(() => click(idx));
                Hint(r, hint(idx));
                DrawButton(g, r, labels[i], i < states.Length && states[i]);
                cx += r.Width + Gap;
            }
            return y + SetRowH;
        }

        void SetRefresh(int delta)
        {
            int next = Settings.RefreshSeconds + delta;
            if (next < 60 || next > 3600) return;
            Settings.RefreshSeconds = next; Settings.Save();
            RefreshTimer.Interval = next * 1000;
            NoteAfterSave("Refresh every " + (next / 60) + " min");
            Refresh();
        }

        void OpenIni()
        {
            try
            {
                // Incidental, same as the position saves: creating a missing
                // ini before opening it must not silently persist a choice
                // whose own save failed.
                if (!File.Exists(Settings.IniPath)) Settings.SavePosition();
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
            // start on a control that does nothing in this mode. The measure
            // pass writes nothing: the interactive rails stay what the last
            // REAL paint produced.
            if (!Measuring)
            {
                if (id == "volume") { VolRailVolume = enabled ? rail : Rectangle.Empty; VolKnobVolume = enabled ? knob0 : Rectangle.Empty; }
                else if (id == "preview") { VolRailPreview = enabled ? rail : Rectangle.Empty; VolKnobPreview = enabled ? knob0 : Rectangle.Empty; }
                else { VolRailLow = enabled ? rail : Rectangle.Empty; VolKnobLow = enabled ? knob0 : Rectangle.Empty; }
                if (id == VolDrag) VolRail = rail;
            }
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

        // PERF-003: the drag is one gesture. The pointer gets live values on
        // every move, but the INI commit happens ONCE, when the gesture ends —
        // a drag across the rail used to persist per MouseMove, writing the
        // settings file dozens of times for one user action.
        bool VolDragDirty;

        void SetVolumeFromX(int px)
        {
            int next = Math.Max(0, Math.Min(100,
                (int)Math.Round((double)(px - VolRail.X) / Math.Max(1, VolRail.Width) * 100)));
            if (next == Settings.SoundVolume) return;
            Settings.SoundVolume = next;
            VolDragDirty = true;
            Note = "Volume " + next + "%";
            Refresh();
        }

        void SetLowPctFromX(int px)
        {
            int next = 5 + (int)Math.Round((double)(px - VolRail.X) / Math.Max(1, VolRail.Width) * 90);
            next = Math.Max(5, Math.Min(95, next));
            if (next == Settings.LowPct) return;
            Settings.LowPct = next;
            VolDragDirty = true;
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
            Settings.PreviewPct = next;
            VolDragDirty = true;
            Note = "Preview at " + next + "% left";
            Refresh();
        }

        // The gesture's single durable commit: one Save when the drag ends,
        // and the low-alert re-arm (NotifiedLow.Clear) happens HERE — once per
        // gesture — not once per pointer move. A press that never moved
        // anything commits nothing.
        void EndVolDrag()
        {
            if (VolDrag == null) return;
            string id = VolDrag;
            VolDrag = null; Capture = false;
            if (VolDragDirty)
            {
                VolDragDirty = false;
                Settings.Save();
                if (id == "lowpct") NotifiedLow.Clear();
            }
            Refresh(); UpdateTray();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // Capture can be lost without a MouseUp (a popup, an alt-tab): the
            // gesture must still end, with its one commit, instead of leaving
            // the slider stuck armed and the value unpersisted.
            if (VolDrag != null && !Capture) EndVolDrag();
            if (ThumbDrag && !Capture) ThumbDrag = false;
        }

        // The wheel scrolls the body the pointer is over — never the chrome,
        // never while a slider or a row drag owns the capture (scrolling under
        // a captured gesture would move the surface the pointer is holding).
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (VolDrag != null || DragId != null || ThumbDrag) return;
            var vp = BodyViewport();
            if (e.Y < vp.Y || e.Y >= Height - FooterH) return;
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);
            if (notches == 0) return;
            // WinForms reports a POSITIVE Delta for a wheel rolled AWAY from
            // the user (up) and a negative one for down, while a positive
            // ScrollBy moves toward later content. The sign is therefore
            // inverted here: wheel down = forward, wheel up = back — which is
            // what every other scrolling surface on the desktop does.
            ScrollBy(-notches * WheelPx);
            Refresh();
        }

        // One wheel notch is about three compact setting rows. The clamp uses
        // the last painted maximum; a paint that changes the content clamps
        // again, so no input can leave the offset past 0 or maxScroll.
        internal void ScrollBy(int delta)
        {
            int at = Math.Max(0, TabScroll[Tab] + delta);
            TabScroll[Tab] = MaxScroll > 0 ? Math.Min(at, MaxScroll) : at;
            // Scrolling moves the control out from under the pointer (or the
            // pointer out from the control): the old geometry is stale.
            TooltipDismiss();
        }

        // One alert = one row: two switches (balloon, chime) then the WAV and a
        // preview. The sound half is only drawn when the chime is on, because a
        // WAV picker for a muted alert is a control with no effect. In a
        // narrow column the tail WRAPS to its own sub-row instead of
        // colliding with the switches — nothing is dropped.
        int AlertRow(Graphics g, int x, int y, int right, int optX,
            string label, string what,
            bool balloonOn, Action toggleBalloon, string balloonHint,
            bool chimeOn, Action toggleChime, string chimeHint,
            string wav, Action pickWav, Action previewWav)
        {
            HintRow(x, y, right - x, "when " + what);
            DrawText(g, label, x, y + 3, Palette.TEXT2, 10);
            int cx = optX;
            int bw = Math.Max(ButtonWidth(g, "balloon"), ButtonWidth(g, "chime"));
            var balloon = new Rectangle(cx, y + 1, bw, BtnH);
            Buttons.Add(balloon); ButtonActions.Add(toggleBalloon);
            Hint(balloon, balloonHint);
            DrawButton(g, balloon, "balloon", balloonOn);
            cx += bw + Gap;
            var chime = new Rectangle(cx, y + 1, bw, BtnH);
            Buttons.Add(chime); ButtonActions.Add(toggleChime);
            Hint(chime, chimeHint);
            DrawButton(g, chime, "chime", chimeOn);
            cx += bw + Gap;
            int wavW = ButtonWidth(g, "WAV"), playW = ButtonWidth(g, "Play");
            bool tailFits = !chimeOn || cx + wavW + playW + Gap * 3 <= right;
            if (chimeOn && tailFits) PaintSoundTail(g, cx, y, right, wav, pickWav, previewWav);
            else if (!chimeOn) DrawTextFit(g, balloonOn ? "silent, balloon only" : "off", cx, y + 3,
                right - cx, Palette.MUTED, 10);
            if (chimeOn && !tailFits)
            {
                // The wrapped tail row: the WAV name gets the full width.
                y += SetRowH;
                HintRow(x, y, right - x, "the WAV this chime plays and a preview button");
                PaintSoundTail(g, x, y, right, wav, pickWav, previewWav);
                return y + SetRowH;
            }
            return y + SetRowH;
        }

        // The tail of an alert row: the sound that is set, then the two buttons
        // that change it. Laid out from the right so a WAV with a long name
        // yields with an ellipsis instead of pushing Play off the window.
        void PaintSoundTail(Graphics g, int x, int y, int right, string file, Action pick, Action preview)
        {
            int wavW = ButtonWidth(g, "WAV"), playW = ButtonWidth(g, "Play");
            // PERF-002 (SRC-006:R019): the display name comes from the off-paint
            // snapshot, never from Resolve/File.Exists during paint.
            string label = SoundDisplayLabel(file);
            bool missing = label == "(no sound: pick one)";
            int nameW = right - x - wavW - playW - Gap * 3;
            if (nameW > 12)
                DrawTextFit(g, label, x, y + 3, nameW, missing ? Palette.DANGERTXT : Palette.TEXT2, 10);
            var wav = new Rectangle(right - wavW - playW - Gap, y + 1, wavW, BtnH);
            RegisterAction(wav, pick, "choose the WAV file this chime plays");
            DrawButton(g, wav, "WAV", false);
            var play = new Rectangle(right - playW, y + 1, playW, BtnH);
            RegisterAction(play, preview, "play the chosen sound once, at the current volume");
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
                    string stored = picked.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? picked.Substring(prefix.Length) : picked;
                    RefreshSoundDisplay();
                    return stored;
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
                    RefreshSoundDisplay();
                    NoteAfterSave("Sounds from " + dlg.SelectedPath);
                }
            }
            catch (Exception ex) { Note = "Could not open the folder dialog (" + ex.GetType().Name + ")"; }
            Refresh();
        }

        // A preview is heard even when the alert is switched off, but ALWAYS
        // at the real volume: the request is "let the test button reflect the
        // volume", so flooring to 25% was the exact lie being removed.
        //
        // T-40/R022: the preview SUBMITS to the serialized audio owner and
        // returns immediately — a slow ReadAllBytes, first-time scale, atomic
        // cache build or SoundPlayer.Load must never block paint, resize,
        // scrolling or a refresh. The queued note says so at once; the
        // completion callback delivers the real outcome (success, mute note,
        // failure) on the UI thread, and only while it is still the newest
        // preview intent — a superseded request never overwrites a newer note.
        long LatestPreviewSeq;

        // ── PERF-002 (SRC-006:R019): the off-paint sound snapshot ───────────
        // Ordinary Settings paint and slider traffic performs ZERO shipped-WAV
        // filesystem work: no Assets.SoundLibrary() call (manifest check +
        // per-name File.Exists + extraction), no SoundCue.Resolve, no
        // Directory.Exists cascade. The display strings are resolved at real
        // sound-use boundaries (form construction, an accepted settings
        // change, the WAV/folder pickers) and the painters only READ them.
        string SoundDisplayPath = "";
        readonly Dictionary<string, string> SoundDisplayNames = new Dictionary<string, string>();

        void RefreshSoundDisplay()
        {
            SoundDisplayPath = SoundCue.Library(RootPath, Settings.SoundDir) ?? "";
            SoundDisplayNames.Clear();
            foreach (string f in new[] { Settings.ResetSoundFile, Settings.LowSoundFile })
            {
                if (string.IsNullOrEmpty(f)) { SoundDisplayNames[f] = null; continue; }
                string p = SoundCue.Resolve(RootPath, Settings.SoundDir, f);
                SoundDisplayNames[f] = p == null ? null : Path.GetFileName(p);
            }
        }

        // The tail label for one stored WAV value: the display name from the
        // snapshot, "(no sound: pick one)" when nothing is set or the snapshot
        // resolved it missing, and the bare filename as the snapshot-free
        // fallback (an absolute path needs no library to name itself).
        string SoundDisplayLabel(string file)
        {
            string name;
            if (SoundDisplayNames.TryGetValue(file, out name))
                return name ?? "(no sound: pick one)";
            if (string.IsNullOrEmpty(file)) return "(no sound: pick one)";
            return Path.GetFileName(file);
        }

        void Preview(string file)
        {
            int volume = Settings.SoundVolume;
            long seq = -1;
            seq = SoundCue.Submit(RootPath, Settings.SoundDir, file, volume, "preview",
                why => BeginInvoke((Action)(() =>
                {
                    if (seq != LatestPreviewSeq) return;    // superseded: a newer preview owns the note
                    SoundNote = why == null ? "" : "Alert sound: " + why;
                    Note = why != null ? SoundNote
                        : Settings.SoundVolume <= 0 ? "Volume is 0% — nothing to hear"
                        : "Preview at " + Settings.SoundVolume + "%";
                    Refresh();
                })));
            LatestPreviewSeq = seq;
            Note = volume <= 0 ? "Volume is 0% — nothing to hear"
                : "Preview queued at " + volume + "%";
            Refresh();
        }

        // W2-006/R020: one honest line for every user-visible settings
        // mutation. `what` is the note the change shows when it reached the
        // ini; when the save failed the LIVE value still stands, so the same
        // line must say so — "Theme: OLED — not saved" — instead of letting a
        // runtime-only change read as durable. The dirty state stays visible
        // in the footer until an explicit retry or an accepted external
        // reload resolves it.
        void NoteAfterSave(string what)
        {
            Note = Settings.LastSaveFailed ? what + " — not saved" : what;
        }

        // R012: the form asks Program to touch the registry through this seam,
        // and gets a VERIFIED answer back — the reconciler is the only writer.
        public Func<AutostartResult> AutostartReconciler;

        // True while the desired AutoStart could not be proven applied: set by
        // every reconcile, cleared only by a verified one. The Settings button,
        // the tray menu and the status row all present from it, so a registry
        // failure can never dress up as "Autostart on".
        public bool AutostartNotApplied;

        AutostartResult ReconcileAutostartNow()
        {
            if (AutostartReconciler == null) return null;
            AutostartResult r = AutostartReconciler();
            AutostartNotApplied = r != null && !r.Verified;
            return r;
        }

        // Startup reconciliation runs before the user has touched anything; if
        // the desired state could not be applied, that is a first-screen fact,
        // not a swallowed exception.
        public void ReportAutostart(AutostartResult r)
        {
            if (r == null || r.Verified) return;
            AutostartNotApplied = true;
            Note = "Autostart wanted: " + (r.Desired ? "on" : "off") + " — " + r.Reason;
        }

        void ToggleAutostart()
        {
            bool want = !Settings.AutoStart;
            Settings.AutoStart = want;
            // W2-004: the registry value outlives this process, so it is applied
            // only if the ini really recorded the choice. Otherwise the two
            // disagree after a restart — Windows launching an app whose settings
            // say autostart is off, with nothing on screen to explain it.
            // R020: the desired flag reverts too, so INI desire, live flag and
            // registry all stay on the same page while the disk is unwritable.
            if (!Settings.SaveSettings().Saved)
            {
                Settings.AutoStart = !want;
                Note = "Autostart unchanged — LIMISAW.ini is not writable";
                Refresh();
                return;
            }
            // The INI is durable; now reconcile the registry and let the
            // READBACK decide what may be claimed. A verified mismatch keeps
            // the desired INI state (B4: desired-but-not-applied, no fragile
            // compensation dance of INI rewrites) and says so.
            AutostartResult r = ReconcileAutostartNow();
            if (r != null && r.Verified) Note = "Autostart " + (want ? "on" : "off");
            else Note = "Autostart wanted: " + (want ? "on" : "off")
                + " — " + (r != null ? r.Reason : "startup entry could not be verified");
            Refresh();
        }

        // Piping a remote script into a shell is exactly the kind of action
        // that must never be implicit: the exact command and its publisher are
        // shown, and only an explicit Yes runs it, in a VISIBLE console.
        // The Connections card routes through BeginInstall (T-46); this entry
        // stays for the tray menu path.
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
                Note = cli.Label + ": installer started — LIMISAW checks automatically when it finishes.";
                // The tray-installed CLI gets the same automatic post-install
                // verification as the Connections card.
                string vendorId = cli.Key;
                int gen = ConnCoordinator.Begin(vendorId);
                if (gen >= 0)
                {
                    ConnCoordinator.TryProgress(vendorId, gen, ConnectionState.WaitingForUser,
                        "Waiting for the installer — LIMISAW checks automatically.");
                    StartWatcher(vendorId, gen, () =>
                    {
                        ExecutableDiscovery.ZcodeAllowConfig = () => Settings.ZcodeReadConfig;
            ExecutableDiscovery.FreebuffAllowConfig = () => Settings.FreebuffReadConfig;
                        var vc = ExecutableDiscovery.BuildConnection(vendorId);
                        if (!vc.Installed) return null;
                        Func<ConnectionVerifyResult> verify;
                        return ConnectionAdapterRegistry.Verify.TryGetValue(vendorId, out verify) && verify != null
                            ? ConnectionVerifyToConnection(vendorId, verify())
                            : vc;
                    });
                }
            }
            catch (Exception ex) { Note = cli.Label + ": could not start installer (" + ex.GetType().Name + ")"; }
            Refresh();
        }

        // Spending a banked reset. The FIRST thing in this app that changes
        // state at a vendor rather than reading it, so it gets the same treatment
        // as Install CLIs: name the exact operation, say it cannot be undone,
        // default the dialog to No, and never act implicitly. The credit is
        // one-off — there is no second chance to reconsider.
        void RedeemCredit(AccountData a)
        {
            if (a.ResetCredits <= 0) { Note = "No banked reset on this account"; Refresh(); return; }
            // Codex is the only vendor that grants these. A button drawn from a
            // count is not authority to call a Codex method on someone else's
            // account, so the provider is checked at the point of ACTION and not
            // only where the button was painted.
            if (a.Provider != "codex")
            { Note = a.ProviderLabel + " has no reset command LIMISAW can run"; Refresh(); return; }
            if (Redeeming) { Note = "A reset is already in flight"; Refresh(); return; }
            string what = string.IsNullOrEmpty(a.ResetCreditTitle) ? "a usage limit reset" : a.ResetCreditTitle;
            string body = AccountTitle(a) + "\n\nThis spends one banked reset:\n\n  " + what
                + (string.IsNullOrEmpty(a.ResetCreditExpires) ? "" : "\n  expires " + FriendlyTime(a.ResetCreditExpires))
                + "\n\nLIMISAW will run the vendor's own command:\n\n  codex app-server"
                + "\n  account/rateLimitResetCredit/consume"
                + "\n\nYou have " + a.ResetCredits + " left. Spending one CANNOT be undone."
                + "\n\nUse it now?";
            if (MessageBox.Show(this, body, "LIMISAW — use a banked reset",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                Note = "Banked reset left alone";
                Refresh();
                return;
            }
            // An irreversible action is routed by the EXACT canonical home, never
            // by display name — two "Codex" cards must not make the credit land
            // on whichever sorts first. Missing exact identity refuses early.
            if (string.IsNullOrEmpty(a.ResetHome))
            { Note = "No exact Codex home for this account — refresh and try again"; Refresh(); return; }
            Redeeming = true;
            Note = "Using the banked reset...";
            Refresh();
            AccountData target = a;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string outcome;
                try { outcome = CodexSource.ConsumeResetCredit(target.ResetHome, Stamp.Now + 30); }
                catch (Exception ex) { outcome = ex.GetType().Name; }
                try
                {
                    Action done = () => ResetCompleted(outcome);
                    if (InvokeRequired) BeginInvoke(done); else done();
                }
                catch { Redeeming = false; }
            });
        }

        // What happens once the vendor has answered, kept whole and separate so
        // the ordering it depends on can be driven without a modal dialog or a
        // real credit. Whatever the outcome, the windows on screen are now wrong:
        // a redeemed credit refills them and a refused one means the count was
        // stale. Re-reading is the only way to show the truth — and W2-001's gate
        // is what keeps that re-read from being dropped when a scheduled sweep
        // happens to be in flight.
        void ResetCompleted(string outcome)
        {
            Redeeming = false;
            Note = "Reset: " + outcome;
            Refresh();
            RefreshData();
        }

        // One redemption at a time. Double-clicking the button would otherwise
        // spend two credits for one intention, and there is no way to give one
        // back.
        bool Redeeming;

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
            return FriendlyTime(t);
        }

        // CORE-013: the epoch-first presentation path. Converting to local
        // happens once, HERE, at the last moment — the caller's authority was
        // the instant all along.
        static string FriendlyTime(double? epoch)
        {
            if (!epoch.HasValue || epoch.Value <= 0) return "--";
            DateTime t;
            try { t = Stamp.Local(epoch.Value); }
            catch { return "--"; }
            return FriendlyTime(t);
        }

        static string FriendlyTime(DateTime t)
        {
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
        // DrawTextFit with an optional full-text tooltip: when the visible text
        // is genuinely elided, the complete string becomes the hover tooltip
        // (after the connection redaction boundary where applicable). Paths,
        // labels and reasons on the 360px window are the reason this exists.
        void DrawTextFit(Graphics g, string s, int x, int y, int maxWidth, Color c, int pt, bool bold = false, string fullText = null)
        {
            if (string.IsNullOrEmpty(s) || maxWidth <= 0) return;
            string text = Elide(g, s, maxWidth, pt);
            if (text.Length == 0) return;
            if (fullText != null && text != s && !string.IsNullOrEmpty(fullText))
            {
                var zone = new Rectangle(x, y, Math.Min(TextWidth(g, text, pt), maxWidth), pt + 4);
                Hint(zone, ConnectionDiagnostics.Redact(fullText, null));
            }
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
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var br = new SolidBrush(!enabled ? Palette.MUTED : selected ? Palette.LINK : Palette.TEXT))
                g.DrawString(text, Cached(pt), br, new RectangleF(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), fmt);
        }

        // Width a button needs for its label at 10pt, so a row can be laid out
        // from its content instead of from a hardcoded guess.
        int ButtonWidth(Graphics g, string label) { return TextWidth(g, label, 10) + BtnPad; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // A click is a decision, not a dwell: any press takes the popup
            // down before whatever the click does happens.
            TooltipDismiss();
            // Right-hold = move the window, on any tab, over any control.
            if (e.Button == MouseButtons.Right) { BeginRightDrag(e); return; }
            // A slider rail grabs the press before any button: the rail spans the
            // row, so without this the knob press would fall through to whatever
            // sits behind it. Must run BEFORE Buttons, and the rail rect is the
            // one painted last frame. While the page is being scrolled the rails
            // are client-coordinate truth, so a press lands on the rail the user
            // sees.
            if (Tab == TabSettings && e.Button == MouseButtons.Left && VolDrag == null && !ThumbDrag)
            {
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
            // The scrollbar thumb: drag to scroll. A click on the track above or
            // below the thumb pages a viewport at a time. Registered before the
            // buttons because the track strip is body-reserved space the
            // buttons never reach.
            if (e.Button == MouseButtons.Left && ThumbDrag == false)
            {
                Rectangle track = CurrentTrack();
                if (track != Rectangle.Empty)
                {
                    if (track.Contains(e.Location))
                    {
                        Rectangle thumb = ScrollThumb(track, TabScroll[Tab]);
                        if (thumb.Contains(e.Location))
                        {
                            ThumbDrag = true; ThumbGrab = e.Y - thumb.Y; Capture = true;
                            return;
                        }
                        ScrollBy(e.Y < thumb.Y ? -BodyHeight() : BodyHeight());
                        return;
                    }
                }
            }
            for (int i = 0; i < Buttons.Count; i++) { if (Buttons[i].Contains(e.Location)) { Note = ""; ButtonActions[i](); return; } }
            // A press on a tray row arms a drag but does not start one: the
            // pointer must travel DragSlop pixels first, so a plain click on a
            // row (which pins that reading) never reorders anything by accident.
            if (Tab == TabTray && e.Button == MouseButtons.Left)
            {
                for (int i = 0; i < ItemRows.Count; i++)
                    if (ItemRows[i].Contains(e.Location))
                    {
                        // A plain CLICK pins the row as the tray number; a DRAG
                        // reorders. The two live on the same row because they are
                        // both about that reading, and the disambiguation is the
                        // gesture: click = pin, drag = move.
                        string clicked = ItemRowIds[i];
                        DragId = clicked; DragStartY = e.Y; DragY = e.Y;
                        Dragging = false; Capture = true;
                        ClickPinCandidate = clicked;
                        return;
                    }
            }
            if (Tab == TabAccounts && e.Button == MouseButtons.Left)
                for (int i = 0; i < ItemRows.Count; i++)
                    if (ItemRows[i].Contains(e.Location))
                    { DragId = ItemRowIds[i]; DragStartY = e.Y; DragY = e.Y; Dragging = false; Capture = true; return; }
        }

        // The scrollbar track in CLIENT coordinates for the pointer handlers —
        // the same geometry the last paint drew.
        Rectangle CurrentTrack()
        {
            if (MaxScroll <= 0) return Rectangle.Empty;
            var vp = BodyViewport();
            vp.Width = Math.Max(0, vp.Width - ScrollbarW);
            return new Rectangle(vp.Right + 1, vp.Y + 1, ScrollbarW - 2, Math.Max(0, vp.Height - 2));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (RightDragging) { MoveRightDrag(e); return; }
            // A volume slider in flight owns the pointer until release: every
            // move re-sets from the x position, live, like Problip's slider.
            if (VolDrag != null && e.Button == MouseButtons.Left)
            {
                if (VolDrag == "volume") SetVolumeFromX(e.X);
                else if (VolDrag == "preview") SetPreviewFromX(e.X);
                else SetLowPctFromX(e.X);
                return;
            }
            // The thumb follows the pointer 1:1 while the drag lasts.
            if (ThumbDrag && e.Button == MouseButtons.Left)
            {
                Rectangle track = CurrentTrack();
                if (track != Rectangle.Empty)
                {
                    int th = ScrollThumb(track, TabScroll[Tab]).Height;
                    int travel = track.Height - th;
                    if (travel <= 0) TabScroll[Tab] = 0;
                    else
                        TabScroll[Tab] = Math.Max(0, Math.Min(MaxScroll,
                            (int)((long)MaxScroll * (e.Y - ThumbGrab - track.Y) / travel)));
                    Refresh();
                }
                return;
            }
            if (DragId == null)
            {
                // Hover explanation. Only repaint when the sentence actually
                // changes: a repaint per mouse-move over a panel of gauges is
                // visible churn for nothing.
                string next = HintAt(e.Location);
                if (next != Hover) { Hover = next; Refresh(); }
                // The themed popup follows the same sentence after a dwell.
                Rectangle zone = TooltipZoneAt(e.Location);
                TooltipHover(next, e.Location, zone);
                return;
            }
            // A drag cancels any dwell.
            TooltipDismiss();
            DragY = e.Y;
            if (!Dragging && Math.Abs(e.Y - DragStartY) >= DragSlop) Dragging = true;
            if (Dragging) Refresh();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            TooltipDismiss();
            if (Hover.Length > 0) { Hover = ""; Refresh(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { EndRightDrag(); return; }
            if (VolDrag != null) { EndVolDrag(); return; }
            if (ThumbDrag) { ThumbDrag = false; Capture = false; return; }
            if (DragId == null) return;
            string id = DragId; bool dragged = Dragging;
            DragId = null; Dragging = false; Capture = false;
            if (!dragged)
            {
                // A click, not a drag: on the Tray tab that PINS the reading as
                // the tray number (or unpins, back to lowest). The pin used to
                // live only in the context menu.
                if (Tab == TabTray && ClickPinCandidate == id)
                {
                    if (MigrateMetricId(Settings.TrayMetric) == id)
                    {
                        Settings.TrayMetric = "lowest"; Settings.Save();
                        NoteAfterSave("Tray number: lowest remaining (recommended)");
                    }
                    else
                    {
                        Settings.TrayMetric = id; Settings.Save();
                        string label = id;
                        foreach (Metric m in AllMetrics())
                            if (m.Id == id) { label = m.Label; break; }
                        NoteAfterSave("Tray number: " + label);
                    }
                    // PERF-005: the pin decides the popup title's reading.
                    BumpPopupData();
                    Refresh(); UpdateTray();
                }
                ClickPinCandidate = null;
                Refresh();
                return;
            }
            ClickPinCandidate = null;
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
            // PERF-005: ShowUsed flips the popup title's own wording.
            BumpPopupData();
            Refresh(); UpdateTray();
        }

        // T-42: a tab switch changes which content the viewport shows — never
        // the window's size, never the other tabs' scroll positions.
        public void ShowTab(int tab) { Tab = tab; TooltipDismiss(); AutoFitHeight(); Refresh(); }

        // Alt+A: keep the window above every other window. Persisted, so a
        // monitor someone floats over their work area is still floating after
        // the next start.
        public void ToggleTopMost()
        {
            Settings.AlwaysOnTop = !Settings.AlwaysOnTop;
            TopMost = Settings.AlwaysOnTop;
            Settings.Save();
            NoteAfterSave("Always on top " + (Settings.AlwaysOnTop ? "on" : "off") + " (Alt+A)");
            Refresh();
        }

        // Right-button hold anywhere moves the window: the left button owns
        // every control (buttons, sliders, cards), so the drag gesture lives
        // on the button the UI does not use. Left-drag on the header strip
        // still works through WM_NCHITTEST.
        bool RightDragging; Point RightDragPos;

        void BeginRightDrag(MouseEventArgs e)
        {
            RightDragging = true;
            RightDragPos = e.Location;
            Capture = true;
        }

        void MoveRightDrag(MouseEventArgs e)
        {
            if (!RightDragging) return;
            Left += e.X - RightDragPos.X;
            Top += e.Y - RightDragPos.Y;
        }

        void EndRightDrag()
        {
            if (!RightDragging) return;
            RightDragging = false; Capture = false;
            // Same incidental contract as HideToTray: geometry only, never a
            // silent resolution of an open dirty settings conflict.
            SaveWindowGeometry();
        }

        public void CycleTheme()
        {
            if (Themes.Count == 0) return;
            int at = 0;
            for (int i = 0; i < Themes.Count; i++)
                if (string.Equals(Themes[i].Slug, Settings.ThemeSlug, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            Theme next = Themes[(at + 1) % Themes.Count];
            Settings.ThemeSlug = next.Slug; Settings.Save();
            ApplyTheme(next.Slug);
            NoteAfterSave("Theme: " + next.Label);
            Refresh(); UpdateTray();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { RefreshData(); e.Handled = true; return; }
            if (e.KeyCode == Keys.U) { ToggleShowUsed(); e.Handled = true; return; }
            if (e.KeyCode == Keys.T) { CycleTheme(); e.Handled = true; return; }
            if (e.Alt && e.KeyCode == Keys.A) { ToggleTopMost(); e.Handled = true; return; }
            if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D4)
            { ShowTab(e.KeyCode - Keys.D1); e.Handled = true; return; }
            if (e.KeyCode == Keys.Escape) { HideToTray(); e.Handled = true; return; }
            // Body navigation: a page at a time, Home/End to the ends, arrows
            // one row. Without a modifier none of these were bound before, and
            // the paint clamps every offset.
            if (e.Modifiers == Keys.None)
            {
                if (e.KeyCode == Keys.PageDown) { ScrollBy(BodyHeight()); Refresh(); e.Handled = true; return; }
                if (e.KeyCode == Keys.PageUp) { ScrollBy(-BodyHeight()); Refresh(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Home) { TabScroll[Tab] = 0; Refresh(); e.Handled = true; return; }
                if (e.KeyCode == Keys.End) { TabScroll[Tab] = MaxScroll; Refresh(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Down) { ScrollBy(RowH); Refresh(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Up) { ScrollBy(-RowH); Refresh(); e.Handled = true; return; }
            }
            base.OnKeyDown(e);
        }

        // ── tray ─────────────────────────────────────────────────────────────
        // The shell tooltip is one line of 63 plain characters — unreadable for
        // ten readings across three vendors. A themed panel is shown on hover
        // instead, with the same gauges and colours as the window; the OS
        // tooltip is left empty so only one thing appears.
        // ── PERF-005: the popup content generation key ───────────────────────
        // A cheap counter pair that changes exactly when the popup's CONTENT
        // can change. MouseMove frequency must never translate into model-
        // building frequency, so the hover handler rebuilds only when this
        // key moved:
        //   PopupDataGen    — bumped by real mutations of the popup's inputs:
        //                     a published probe snapshot, tray settings
        //                     (order/hidden/cap/pin/mode/show), the theme,
        //                     stale flag and the shown-window refresh clock.
        //   TimeBucket      — a coarse minute bucket covering FriendlyTime/
        //                     CountdownText, so relative text refreshes on a
        //                     bounded cadence instead of freezing or riding
        //                     the mouse.
        int PopupDataGen;
        internal int PopupDataVersion { get { return PopupDataGen; } }
        void BumpPopupData() { PopupDataGen++; }
        static int TimeBucket()
        {
            return (int)(Stamp.Now / 60.0);   // minute-level bucket
        }

        // The hover cache the MouseMove handler consults: the content built
        // for `key` is reused until the key changes. One build per key, one
        // Measure per key.
        int PopupCacheKey = int.MinValue;
        string PopupCacheTitle;
        List<TrayPopup.Row> PopupCacheRows;

        // ── PERF-005: the one hover entry point ─────────────────────────────
        // Called on every NotifyIcon.MouseMove. An unchanged content key does
        // NOT rebuild title/rows and does NOT re-measure — the popup only
        // revalidates its position against the current cursor/monitor.
        public void TrayHover(TrayPopup panel, Point at)
        {
            int key = PopupDataGen * 1000000 + TimeBucket();
            if (key != PopupCacheKey || PopupCacheRows == null)
            {
                PopupCacheKey = key;
                PopupCacheTitle = PopupTitle();
                PopupCacheRows = PopupRows();
                HoverContentBuilds++;
                panel.UpdateContent(PopupCacheTitle, PopupCacheRows, at);
            }
            else
            {
                HoverMoves++;
                panel.MoveTo(at);
            }
        }

        // Test seam: the per-move cost counters the harness asserts against
        // (measures live on TrayPopup.MeasureCalls). Initialized inline so the
        // release build carries no CS0649 seam warning.
        internal int HoverContentBuilds = 0, HoverMoves = 0;

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
                    string id = MetricId(a, w);
                    string label = w.Label + (w.GroupLabel.Length > 0 ? " · " + w.GroupLabel : "");
                    // A reading the tray does not draw is dimmed rather than
                    // dropped: the panel is also how the user sees what the cap
                    // and the hide list are doing. Carried-forward numbers are
                    // dimmed for the same reason - they are real, but not fresh.
                    bool drawn = inTray.Contains(id) && !a.Carried;
                    rows.Add(new TrayPopup.Row
                    {
                        Left = (drawn ? "● " : "  ") + label,
                        Right = w.GatedBy != null ? "locked" : w.AssumedFull ? "refilled" : FriendlyTime(w.ResetEpoch),
                        Pct = ShownRem(w.Rem), Rem = w.Rem,
                        Available = w.Available, Gauge = true, Dim = !drawn,
                    });
                }
            }
            return rows;
        }

        public string PopupTitle()
        {
            TrayReading r = ResolveReading(BuildModel());
            string head = "LIMISAW — " + (Settings.ShowUsed ? "highest used " : "lowest remaining ");
            head += r.Available ? ShownRem(r.Value) + "% (" + r.Label + ")" : "no reading";
            // A fallback must be discoverable where the number is shown: 37%
            // next to a Zcode pin must never read as "Zcode is at 37%".
            if (r.Fallback && r.Note.Length > 0) head += " · " + r.Note;
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
        //
        // ONE snapshot, ONE resolution, ONE icon/text result: the tooltip and
        // the bitmap are drawn from the same model and the same resolved
        // reading, never from two independent builds of the live state.
        // TraySnapshots/TrayResolutions are the regression seam that proves
        // that per update (tests/tray_render_modes.cs).
        internal int TraySnapshots, TrayResolutions;

        public void UpdateTray()
        {
            try
            {
                TrayModel model = BuildModel();
                TrayReading reading = ResolveReading(model);
                Tray.Text = PopupOwnsTooltip ? "" : BuildTip(reading);

                using (Bitmap bmp = RenderBitmap(model, reading))
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
        Bitmap RenderTrayBitmap()
        {
            return RenderModel(BuildModel());
        }

        // The whole render for one immutable model: resolve the reading, draw
        // the layout. Both the real tray and the Settings preview come through
        // here, so they cannot drift — and a preview can never touch live
        // state, because the model carries its own data.
        Bitmap RenderModel(TrayModel model)
        {
            TrayReading reading = ResolveReading(model);
            return RenderBitmap(model, reading);
        }

        Bitmap RenderBitmap(TrayModel model, TrayReading reading)
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
                    if (Settings.TrayMode == "grid") DrawGrid(mg, model.Items);
                    else if (Settings.TrayMode == "bars") DrawBars(mg, model.Items);
                    else if (Settings.TrayMode == "rows") DrawRows(mg, model.Items);
                    else if (Settings.TrayMode == "gauge") DrawGauge(mg, reading);
                    else if (Settings.TrayMode == "dual") DrawDual(mg, model.Items);
                    else DrawSingle(mg, model, reading);
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

        // The Settings preview: the SAME renderer the shell gets, driven by the
        // pretend model. Going through the real path is the point — a
        // hand-drawn mock-up can agree with the icon today and drift tomorrow.
        Bitmap RenderPreviewBitmap()
        {
            PreviewRenders++;
            // Off-paint callers (tests) own no snapshot, so they build one;
            // the Settings painter itself always passes its TrayPaintAll in.
            return RenderModel(PreviewModel(Settings.PreviewPct, TrayPaintAll ?? AllMetrics()));
        }

        // PERF-001 (SRC-006:R018): the paint path hands the already-owned
        // snapshot in, so rendering the preview adds zero materializations.
        Bitmap RenderPreviewBitmap(List<Metric> all)
        {
            PreviewRenders++;
            return RenderModel(PreviewModel(Settings.PreviewPct, all));
        }

        // A plausible reset for the Time readout, derived from the pretend level
        // so it moves with the slider instead of sitting at a constant.
        double? PreviewResetEpoch(int pct)
        {
            double hours = 0.25 + 4.75 * Math.Max(0, Math.Min(100, pct)) / 100.0;
            return Stamp.Of(DateTime.UtcNow.AddHours(hours));
        }

        Bitmap RenderTrayBitmap(int value, bool available)
        {
            return RenderTrayBitmap(value, available, (double?)null);
        }

        // Compatibility path: draws the single number from explicit values the
        // way tests and callers hand them over, while the multi-reading layouts
        // still read the live selection.
        Bitmap RenderTrayBitmap(int value, bool available, double? resetEpoch)
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
                    if (Settings.TrayMode == "grid") DrawGrid(mg, TrayMetrics());
                    else if (Settings.TrayMode == "bars") DrawBars(mg, TrayMetrics());
                    else if (Settings.TrayMode == "rows") DrawRows(mg, TrayMetrics());
                    else if (Settings.TrayMode == "gauge") DrawGauge(mg, ResolveReading(BuildModel()));
                    else if (Settings.TrayMode == "dual") DrawDual(mg, TrayMetrics());
                    else DrawSingle(mg, value, available, resetEpoch);
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

        string BuildTip(TrayReading reading)
        {
            // The metric is always picked on REMAINING, so the same window is the
            // lowest-left one and the most-used one: only the wording flips.
            string head = "LIMISAW | " + (Settings.ShowUsed ? "highest used " : "lowest remaining ");
            string tip;
            if (reading.Fallback && reading.NoteShort.Length > 0)
            {
                // The fallback rides INSIDE the budget, right behind the
                // percentage, and the label yields first: the one fact the
                // tooltip must carry is that this is not the pinned number.
                tip = head + (reading.Available ? ShownRem(reading.Value) + "%" : "--")
                    + " [" + reading.NoteShort + "]"
                    + " (" + ShortText(reading.Label, 14) + ")";
            }
            else
            {
                tip = head + (reading.Available
                    ? ShownRem(reading.Value) + "% (" + ShortText(reading.Label, 28) + ")"
                    : "--");
            }
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
            DrawSingle(g, value, available, (double?)null);
        }

        void DrawSingle(Graphics g, int value, bool available, double? resetEpoch)
        {
            DrawSingle(g, value, available, resetEpoch.HasValue ? Stamp.Token(resetEpoch) : null);
        }

        // `reset` is the invariant epoch token at runtime; the ISO branch
        // answers for the Settings-preview shape (see CountdownText).
        void DrawSingle(Graphics g, int value, bool available, string reset)
        {
            // TrayShow picks the TEXT: off = icon only (no number at all), pct =
            // the old behaviour, time = CountdownText of this window's reset.
            // Colour keeps reading the REMAINING percent in both modes: red
            // still means "almost out", never "barely used".
            string text;
            if (!available) text = "--";
            else if (Settings.TrayShow == "off") text = "";
            else if (Settings.TrayShow == "time") text = CountdownText(reset);
            else text = ShownRem(value).ToString();
            if (text.Length == 0) return;
            Color col = Stale ? Palette.MUTED : (available ? PctColor(value) : Palette.MUTED);
            DrawTrayText(g, text, new Rectangle(1, 1, 14, 14), col);
        }

        // The model-aware single draw: the resolved reading decides both the
        // number and its reset, so icon, tooltip and popup agree by
        // construction.
        void DrawSingle(Graphics g, TrayModel model, TrayReading reading)
        {
            string text;
            if (!reading.Available) text = "--";
            else if (Settings.TrayShow == "off") text = "";
            else if (Settings.TrayShow == "time") text = CountdownText(
                reading.ResetEpoch.HasValue ? Stamp.Token(reading.ResetEpoch) : null);
            else text = ShownRem(reading.Value).ToString();
            if (text.Length == 0) return;
            Color col = model.Stale ? Palette.MUTED : PctColor(reading.Value);
            DrawTrayText(g, text, new Rectangle(1, 1, 14, 14), col);
        }

        // The tray's one text primitive: the bitmap alphabet, exact palette
        // colours, largest whole-pixel scale that fits. Nothing else may draw
        // tray digits — GDI at this size is the renderer that produced
        // unreadable glyphs.
        static void DrawTrayText(Graphics g, string text, Rectangle box, Color color)
        {
            if (!TrayGlyphs.Supported(text)) return;
            TrayGlyphs.Draw(g, text, box, color);
        }

        // Two stacked numbers: the lowest SHORT window (5h-class) over the
        // lowest LONG one (weekly/monthly), across the SELECTED readings only.
        // One glance answers both "can I work now" and "will I last the week".
        // Each half may independently read "--": faking a long value into the
        // short slot would lie about a real window.
        void DrawDual(Graphics g, List<Metric> items)
        {
            int shortRem = int.MaxValue, longRem = int.MaxValue;
            foreach (Metric m in items)
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
            DrawTrayText(g, text, new Rectangle(1, top, 14, 7), col);
        }

        // Fill granularity is a setting, not a constant: 1/8 quantises the
        // percentage into eight steps, Exact fills by the pixel. The old 1/2
        // and 1/4 steps are gone; legacy 2/4 values load as 8 (see Read).
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
            FillRows(g, cell, rem, bottomUp);
        }

        // The fill without the clear: the caller owns the track colour under
        // it. Bars and Rows 2px slots sit on a BEVEL track the fill draws
        // over bottom-up, because a 2px rectangle outline has no interior —
        // its border would paint over every fill pixel and a 90% slot would
        // look exactly like an empty one. AREA fill only: Bars/Cells.
        void FillRows(Graphics g, Rectangle cell, int rem, bool bottomUp)
        {
            int shown = ShownRem(rem);
            int cellArea = cell.Width * cell.Height;
            int steps = FillSteps(shown);
            int fillArea = steps < 0 ? cellArea * Math.Min(100, shown) / 100 : cellArea * steps / Settings.TrayFill;
            if (fillArea <= 0) return;
            using (var brush = new SolidBrush(FillColor(rem, true)))
            {
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
        }

        // The horizontal fill for Gauge/Rows: the unit is WIDTH, never pixel
        // area. The old shared routine computed fillArea = width*height*pct
        // and then reused that AREA as a WIDTH on the partial final column,
        // painting extra full-height columns past the real boundary (a 14x4
        // gauge at 47% grew past its 6 columns). 47% of a 14px row is 6px:
        // fillWidth = width * shownPct / 100 (Exact) or the eighth-quantised
        // step mapped back onto the row's own width.
        void FillHorizontal(Graphics g, Rectangle row, int rem)
        {
            int shown = ShownRem(rem);
            int steps = FillSteps(shown);
            int fillW = steps < 0
                ? row.Width * Math.Min(100, shown) / 100
                : row.Width * steps / Math.Max(1, Settings.TrayFill);
            if (fillW <= 0) return;
            using (var brush = new SolidBrush(FillColor(rem, true)))
                g.FillRectangle(brush, row.X, row.Y, fillW, row.Height);
        }

        // One cell per SELECTED reading, in the user's order, laid out as
        // 1x1 / 2x2 / 3x3. Supports every reading up to TrayMax: nine cells is
        // the practical floor, and a reading past the layout's own arithmetic
        // would be a silently dropped selection.
        void DrawGrid(Graphics g, List<Metric> items)
        {
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
                // Left and Used are OPPOSITE fill directions in Cells: Left
                // fills top-down with the remaining amount, Used fills
                // bottom-up with the used amount. Only the amount (ShownRem)
                // and the direction express the toggle; the colour still reads
                // the REMAINING percent, so red always means "almost out".
                FillArea(g, cell, value, available, Settings.ShowUsed);
                if (have && !available) MarkUnavailable(g, cell);
                using (var p = new Pen(Palette.BEVEL)) g.DrawRectangle(p, cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
            }
        }

        // BARS ARE COLUMNS, NOT PARTITIONS OF THE WHOLE INTERIOR.
        //
        // The old renderer divided the 14 interior columns by the reading
        // count, so a single selected reading owned all 14 pixels and looked
        // exactly like the one-cell Cells view. A bar is a NARROW vertical
        // column that keeps its own width no matter how few there are, and the
        // whole group is centred: one reading is one ~4px column with 10px of
        // empty track beside it, never a full block.
        //
        // Widths are a fixed table, one entry per legal count 1..9, so the
        // geometry is the same in the icon and in the preview and a 9-reading
        // configuration still gives every reading its own visible slot.
        static readonly int[] BarWidths = { 0, 4, 4, 2, 2, 1, 1, 1, 1, 1 };
        static readonly int[] BarGaps   = { 0, 0, 3, 1, 1, 1, 1, 1, 1, 0 };

        internal void DrawBars(Graphics g, List<Metric> items)
        {
            int n = Math.Max(1, Math.Min(LimisawSettings.MaxTrayItems, items.Count));
            int bw = BarWidths[n], gap = BarGaps[n];
            int group = n * bw + (n - 1) * gap;
            if (group > 14) { gap = 0; group = n * bw; }
            int x = 1 + (14 - group) / 2;
            for (int i = 0; i < n; i++)
            {
                bool have = i < items.Count;
                bool available = have && items[i].Available;
                int value = have ? items[i].Value : 100;
                var bar = new Rectangle(x, 1, bw, 14);
                if (bw >= 3)
                {
                    FillArea(g, bar, value, available, true);
                    if (have && !available) MarkUnavailable(g, bar);
                    using (var p = new Pen(Palette.BEVEL)) g.DrawRectangle(p, bar.X, bar.Y, bar.Width - 1, bar.Height - 1);
                }
                else
                {
                    // 1px and 2px columns have no interior a rectangle border
                    // could frame, so the BEVEL colour IS the track: the fill
                    // rows draw over it, bottom-up, with the same step maths
                    // as FillArea. Every bar stays a visible slot instead of
                    // dissolving into its neighbour — and a 90% bar keeps
                    // looking different from a 5% one, which the outline-on-
                    // 2px shape silently destroyed.
                    using (var track = new SolidBrush(Palette.BEVEL)) g.FillRectangle(track, x, 1, bw, 14);
                    if (!available)
                    {
                        using (var br = new SolidBrush(Palette.MUTED))
                            for (int r = 0; r < bw; r++)
                                g.FillRectangle(br, x + r, 1 + 14 / 2, 1, 1);
                    }
                    else
                    {
                        int shown = ShownRem(value);
                        int steps = FillSteps(shown);
                        int fillRows = steps < 0 ? 14 * Math.Min(100, shown) / 100
                                                 : 14 * steps / Settings.TrayFill;
                        using (var b = new SolidBrush(FillColor(value, true)))
                            for (int r = 0; r < fillRows; r++)
                                g.FillRectangle(b, x, 14 - r, bw, 1);
                    }
                }
                x += bw + gap;
            }
        }

        // ROWS: the horizontal counterpart of Bars. One mini-bar per selected
        // reading, top to bottom in the selected order, filling left to right.
        // Thick rows with spacing for small counts, one pixel at nine — every
        // reading still owns a distinct row at every legal count.
        static readonly int[] RowHeights = { 0, 4, 4, 3, 3, 2, 2, 2, 2, 1 };
        static readonly int[] RowGaps = { 0, 0, 3, 1, 1, 1, 1, 0, 0, 0 };

        internal void DrawRows(Graphics g, List<Metric> items)
        {
            int n = Math.Max(1, Math.Min(LimisawSettings.MaxTrayItems, items.Count));
            int rh = RowHeights[n], gap = RowGaps[n];
            int group = n * rh + (n - 1) * gap;
            if (group > 14) { gap = 0; group = n * rh; }
            int y = 1 + (14 - group) / 2;
            for (int i = 0; i < n; i++)
            {
                bool have = i < items.Count;
                bool available = have && items[i].Available;
                int value = have ? items[i].Value : 100;
                var row = new Rectangle(1, y, 14, rh);
                if (rh >= 3)
                {
                    using (var under = new SolidBrush(Palette.BG)) g.FillRectangle(under, row);
                    if (available) FillHorizontal(g, row, value);
                    if (have && !available) MarkUnavailable(g, row);
                    using (var p = new Pen(Palette.BEVEL)) g.DrawRectangle(p, row.X, row.Y, row.Width - 1, row.Height - 1);
                }
                else
                {
                    // 1px and 2px rows: BEVEL track, fill columns drawn over
                    // left-to-right (same reason as the bar comment above).
                    using (var track = new SolidBrush(Palette.BEVEL)) g.FillRectangle(track, 1, y, 14, rh);
                    if (!available)
                    {
                        using (var br = new SolidBrush(Palette.MUTED))
                            for (int r = 0; r < rh; r++)
                                g.FillRectangle(br, 1 + 14 / 2, y + r, 1, 1);
                    }
                    else
                        FillHorizontal(g, new Rectangle(1, y, 14, rh), value);
                }
                y += rh + gap;
            }
        }

        // GAUGE: one thin horizontal bar for the ONE resolved reading, no
        // digits. It reads the same truth Number does — the readable pin
        // first, an eligible fallback second, "--" when nothing can answer.
        // GAUGE IS THE MINIMAL SINGLE-READING METER: its own 3px band, distinct
        // from Rows (the multi-reading layout, whose one-reading row keeps the
        // family's 4px row height). One-reading Rows and Gauge would otherwise
        // be pixel-identical and one of the two layouts would be decoration.
        internal void DrawGauge(Graphics g, TrayReading reading)
        {
            const int gh = 3;
            var box = new Rectangle(1, 1 + (14 - gh) / 2, 14, gh);
            if (!reading.Available)
            {
                using (var t = new SolidBrush(Palette.BG)) g.FillRectangle(t, box);
                using (var p = new Pen(Palette.BEVEL)) g.DrawRectangle(p, box.X, box.Y, box.Width - 1, box.Height - 1);
                MarkUnavailable(g, box);
                return;
            }
            using (var under = new SolidBrush(Palette.BG)) g.FillRectangle(under, box);
            FillHorizontal(g, box, reading.Value);
            using (var p = new Pen(Palette.BEVEL)) g.DrawRectangle(p, box.X, box.Y, box.Width - 1, box.Height - 1);
        }

        // A reading that exists but cannot answer is marked, not blanked: an
        // empty track would read as a genuine 0%, and a missing slot would read
        // as an undropped selection. One muted pixel at the centre, restrained
        // on purpose — the hover panel carries the explanation.
        void MarkUnavailable(Graphics g, Rectangle cell)
        {
            using (var br = new SolidBrush(Palette.MUTED))
                g.FillRectangle(br, cell.X + cell.Width / 2, cell.Y + cell.Height / 2, 1, 1);
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
        public void ApplyChoice() { BumpPopupData(); Refresh(); UpdateTray(); }
    }

    // ── single instance ──────────────────────────────────────────────────────
    // CORE-011: the launch protocol. The old shape made the SECOND launch a
    // one-shot guess — mutex says an owner exists, so try the activation
    // channel once and return — which meant a launch inside the publication
    // gap (owner has the mutex, but the channel or its consumer does not exist
    // yet) or during teardown (channel closed while ownership still stands)
    // silently vanished. The contract now: every launch resolves to a READY
    // primary receiving activation, or to owning the singleton itself and
    // continuing as the replacement primary, or — only when the handoff bound
    // expires with a demonstrably live owner that never published — a designed
    // timeout. The two named events carry the protocol:
    //
    //   LimisawShow  AutoReset — the activation signal. Consumed by the
    //                primary's registered waiter; a Set that lands while the
    //                waiter is not yet registered stays pending as a signaled
    //                state and is consumed at registration.
    //   LimisawReady ManualReset — the license to deliver. A primary holds it
    //                SET only while the waiter and the forced form handle are
    //                live; a secondary Sets the show event ONLY against a set
    //                gate, so "gate is set" means "activation can be delivered
    //                safely".
    //
    // Publication order on the primary: channels exist right after ownership
    // (before anything can look for them), the gate is SET only once the
    // callback path is usable. Teardown order: the gate is RESET first (no new
    // delivery is licensed), then the waiter is unregistered and the channel
    // closed, and singleton ownership is relinquished LAST — the world never
    // observes a contactable primary that is on its way out, and a secondary
    // arriving at any instant finds either a licensed channel or a free mutex.
    internal static class Singleton
    {
        // Static (not const) only so the regression harness can run the real
        // protocol against throwaway namespace prefixes; Program.Main always
        // uses these defaults and never touches the fields.
        public static string MutexName = "Local\\LimisawApp";
        public static string ShowName = "Local\\LimisawShow";
        public static string ReadyName = "Local\\LimisawReady";

        public enum Role { Primary, Activated, Timeout, PublicationFailed }

        public sealed class Ownership : IDisposable
        {
            internal readonly System.Threading.Mutex Mutex;
            internal readonly System.Threading.EventWaitHandle Show;
            internal readonly System.Threading.EventWaitHandle Ready;
            bool released;

            internal Ownership(System.Threading.Mutex mutex, System.Threading.EventWaitHandle show, System.Threading.EventWaitHandle ready)
            { Mutex = mutex; Show = show; Ready = ready; }

            // Show and Ready are the caller's to order (the waiter must be
            // unregistered before the channel closes); this closes them and
            // relinquishes ownership LAST.
            public void Dispose()
            {
                try { Ready.Reset(); } catch { }
                try { Show.Dispose(); } catch { }
                try { Ready.Dispose(); } catch { }
                Release();
            }

            // Explicit release on the owning thread — never left to accidental
            // process/thread teardown ordering.
            public void Release()
            {
                if (released) return;
                released = true;
                try { Mutex.ReleaseMutex(); } catch { }
                try { Mutex.Dispose(); } catch { }
            }
        }

        // Returns ownership for a PRIMARY (fresh or takeover), or null when the
        // launch resolved as a secondary (role says Activated or Timeout) — or
        // when publication FAILED (role says PublicationFailed: the mutex was
        // released, so a later healthy instance can become Primary).
        public static Ownership Acquire(TimeSpan handoff, out Role role)
        {
            bool createdNew;
            // initiallyOwned matters only on creation: the winner of the create
            // race owns immediately; an existing mutex is acquired below.
            var mutex = new System.Threading.Mutex(true, MutexName, out createdNew);
            bool own = createdNew;
            if (!own)
            {
                try { own = mutex.WaitOne(TimeSpan.Zero); }
                catch (System.Threading.AbandonedMutexException) { own = true; }  // the owner died; the wait granted us the handle
            }
            if (own) return Publish(mutex, out role);
            Ownership takeover = Handoff(mutex, handoff, out role);
            if (takeover == null) { try { mutex.Dispose(); } catch { } }
            return takeover;
        }

        // W2-004/R012: the OpenChannel seam. Production opens real named
        // events; tests inject failures to prove the atomic publication
        // contract — a primary exists ONLY when BOTH channels exist.
        internal static Func<string, System.Threading.EventResetMode, System.Threading.EventWaitHandle> OpenChannelImpl = OpenChannel;

        static System.Threading.EventWaitHandle TryOpenChannel(string name, System.Threading.EventResetMode mode)
        {
            return OpenChannelImpl(name, mode);
        }

        // W2-004/R012: ATOMIC singleton publication. The old shape assumed
        // both channels existed and immediately called ready.Reset() — a null
        // from OpenChannel was a NullReferenceException, and a Show-succeeded/
        // Ready-failed launch left a PARTIAL primary holding the mutex. Now:
        // Primary exists only when BOTH channels exist; any failure disposes
        // the channel(s) that DID open, releases the mutex, and reports
        // PublicationFailed so a later healthy instance can become Primary.
        static Ownership Publish(System.Threading.Mutex mutex, out Role role)
        {
            System.Threading.EventWaitHandle show = TryOpenChannel(ShowName, System.Threading.EventResetMode.AutoReset);
            if (show == null)
            {
                // Show failed: no partial primary. Unwind and hand the
                // singleton back to the world.
                role = Role.PublicationFailed;
                try { mutex.ReleaseMutex(); } catch { }
                try { mutex.Dispose(); } catch { }
                return null;
            }
            System.Threading.EventWaitHandle ready = TryOpenChannel(ReadyName, System.Threading.EventResetMode.ManualReset);
            if (ready == null)
            {
                // Ready failed: dispose Show, release the mutex, report.
                role = Role.PublicationFailed;
                try { show.Dispose(); } catch { }
                try { mutex.ReleaseMutex(); } catch { }
                try { mutex.Dispose(); } catch { }
                return null;
            }
            role = Role.Primary;
            // A crashed predecessor leaves the gate signaled in the kernel; a
            // fresh primary is not contactable until IT sets the gate.
            try { ready.Reset(); } catch { }
            return new Ownership(mutex, show, ready);
        }

        // The bounded handoff. One failed activation attempt is never final:
        // the loop exits only when a delivery lands, when ownership becomes
        // obtainable, or when the bound expires against a live owner that never
        // published. Waits are on kernel handles — no busy spin.
        static Ownership Handoff(System.Threading.Mutex mutex, TimeSpan handoff, out Role role)
        {
            DateTime deadline = DateTime.UtcNow + handoff;
            bool activated = false;
            while (true)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                System.Threading.EventWaitHandle ready = TryOpen(ReadyName);
                if (ready == null)
                {
                    // The owner has not published the gate yet (startup instant)
                    // or predates the gate entirely (an older exe still running).
                    // One window probe, then wait for the mutex in short
                    // bounded slices — readiness publication is watched on the
                    // next slice's gate open.
                    if (ActivateWindow()) { activated = true; break; }
                    bool took = false;
                    try { took = mutex.WaitOne(TimeSpan.FromMilliseconds(50)); }
                    catch (System.Threading.AbandonedMutexException) { took = true; }
                    if (took) return Publish(mutex, out role);   // takeover
                    continue;
                }
                using (ready)
                {
                    if (ready.WaitOne(TimeSpan.Zero) && Deliver()) { activated = true; break; }
                    // Either the gate flips on (deliver next pass) or the owner
                    // exits (the mutex wakes first in the array — takeover).
                    int which;
                    try { which = System.Threading.WaitHandle.WaitAny(new System.Threading.WaitHandle[] { mutex, ready }, left); }
                    catch (System.Threading.AbandonedMutexException) { which = 0; }   // the abandoned mutex is ours now
                    if (which == 0) return Publish(mutex, out role); // takeover: exactly one waiter can win this
                }
            }
            if (!activated && (Deliver() || ActivateWindow())) activated = true;
            role = activated ? Role.Activated : Role.Timeout;
            return null;
        }

        // Activation is licensed by the gate: open it, confirm it is SET, then
        // signal the show event.
        static bool Deliver()
        {
            try
            {
                using (var ready = System.Threading.EventWaitHandle.OpenExisting(ReadyName))
                {
                    if (!ready.WaitOne(TimeSpan.Zero)) return false;
                    using (var show = System.Threading.EventWaitHandle.OpenExisting(ShowName))
                    { show.Set(); return true; }
                }
            }
            catch { return false; }
        }

        // For an owner that predates the gate: the window itself is the only
        // contact surface (the pre-CORE-011 shape's fallback, kept for the
        // upgrade case).
        static bool ActivateWindow()
        {
            IntPtr existing = Native.FindWindow(null, "LIMISAW");
            if (existing == IntPtr.Zero) return false;
            Native.ShowWindow(existing, 5);
            Native.SetForegroundWindow(existing);
            return true;
        }

        static System.Threading.EventWaitHandle OpenChannel(string name, System.Threading.EventResetMode mode)
        {
            try { return new System.Threading.EventWaitHandle(false, mode, name); }
            catch (UnauthorizedAccessException) { try { return System.Threading.EventWaitHandle.OpenExisting(name); } catch { return null; } }
        }

        static System.Threading.EventWaitHandle TryOpen(string name)
        {
            try { return System.Threading.EventWaitHandle.OpenExisting(name); }
            catch (System.Threading.WaitHandleCannotBeOpenedException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (TimeoutException) { return null; }
        }
    }

    // ── R012: the verified autostart contract ────────────────────────────────
    // The Run-value operations, cut narrow so each one can fail alone. The
    // harness substitutes a store that refuses exactly one operation; a UI
    // that reports success must get it from a READBACK, never from the fact
    // that a call was made. Production always binds HKCU Run; nothing else
    // about the registry is reachable through this seam.
    internal abstract class AutostartStore : IDisposable
    {
        // Open the Run key. `writable` false is the read-only look the tray
        // menu uses on Opening, where the contract is projection only. True
        // means the key is open for writing; false = open failure.
        public abstract bool Open(bool writable);
        // Returns false only when an EXISTING value could not be removed; an
        // already-absent value is success (disable must be idempotent).
        public abstract bool Delete(string name);
        public abstract bool SetValue(string name, string value);
        // Distinguishes the three reads the contract needs: failure (false),
        // absent (true, null), present (true, text).
        public abstract bool TryRead(string name, out string value);
        public abstract void Dispose();
    }

    internal sealed class RegistryAutostartStore : AutostartStore
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        RegistryKey rk;

        public override bool Open(bool writable)
        {
            try { rk = Registry.CurrentUser.OpenSubKey(RunKey, writable); }
            catch { rk = null; if (writable) return false; return true; }
            // A missing key can still be READ (nothing in it: the value is
            // absent, which is exactly what disable wants to prove) but cannot
            // be written through.
            if (rk == null) return !writable;
            return true;
        }

        public override bool SetValue(string name, string value)
        {
            if (rk == null) return false;
            try { rk.SetValue(name, value); }
            catch { return false; }
            return true;
        }

        public override bool Delete(string name)
        {
            if (rk == null) return true;
            try { if (rk.GetValue(name) != null) rk.DeleteValue(name, false); }
            catch { return false; }
            return true;
        }

        public override bool TryRead(string name, out string value)
        {
            value = null;
            if (rk == null) return true;
            try
            {
                object o = rk.GetValue(name);
                if (o != null) value = o.ToString();
            }
            catch { return false; }
            return true;
        }

        public override void Dispose()
        {
            if (rk != null) rk.Dispose();
            rk = null;
        }
    }

    // One autostart reconciliation outcome. `Actual` is the VERIFIED state of
    // the Run value — present with the exact expected command, absent, or null
    // when nothing readable proved anything. `Verified` is the only thing UI
    // success text may key on: invocation is not success, and a mismatch
    // (wanted on, entry absent; wanted off, entry still there; wrong command)
    // is a failure with a reason, never a success.
    internal class AutostartResult
    {
        public bool Desired;
        public bool? Actual;
        public bool Verified;
        public string Reason;
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // CORE-011: the launch resolves through the bounded singleton
            // protocol — own it (fresh or takeover), deliver activation to a
            // ready primary, or expire the bound against a live unpublished
            // owner. Never a one-shot guess that can vanish in a gap.
            Singleton.Role role;
            Singleton.Ownership owned = Singleton.Acquire(TimeSpan.FromSeconds(10), out role);
            if (owned == null) return;
            using (owned)
            {
                var showEvent = owned.Show;

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
                // it — through the verified R012 reconciler, never a bare write.
                form.AutostartReconciler = () => ReconcileAutostart(s);
                // BeginInvoke from the single-instance signal needs a handle even
                // when Windows starts the application hidden in the tray.
                IntPtr hiddenHandle = form.Handle;
                Action show = () => { form.Show(); form.Activate(); };
                var showWait = System.Threading.ThreadPool.RegisterWaitForSingleObject(showEvent, (state, timedOut) =>
                {
                    if (form.IsDisposed || !form.IsHandleCreated) return;
                    try { form.BeginInvoke(show); } catch { }
                }, null, System.Threading.Timeout.Infinite, false);
                // CORE-011: published. From here the gate licenses delivery, so
                // a secondary's Set reaches a live waiter on a forced handle.
                owned.Ready.Set();
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
                        try { form.TrayHover(panel, at); } catch { }
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
                form.FormClosing += (o, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; form.HideToTray(); } else form.BeginShutdown(); };
                form.UpdateTray();
                // R012: startup reconciliation is now verified and honest — a
                // failure is surfaced on screen instead of swallowed.
                form.ReportAutostart(ReconcileAutostart(s));
                bool startHidden = Array.Exists(args, a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
                if (!startHidden) form.Show();
                Application.Run();
                // W2-003: the lifecycle gate closes before any teardown. It is
                // also set from FormClosing on the ApplicationExitCall path;
                // this call is the belt-and-suspenders for any exit that skips
                // that handler. Idempotent.
                form.BeginShutdown();
                // CORE-011 teardown: the gate stops advertising FIRST — no
                // delivery is licensed into a dying app — then the waiter is
                // unregistered and the channel closes in Dispose, and
                // singleton ownership is relinquished last.
                owned.Ready.Reset();
                showWait.Unregister(null);
                if (hoverGuard != null) { hoverGuard.Stop(); hoverGuard.Dispose(); }
                if (popup != null) popup.Dispose();
                // T-40/R022: the audio owner is stopped deterministically —
                // no background worker outlives the application lifecycle.
                SoundCue.Shutdown();
                // The connection layer stops with the app: watcher tasks end
                // and every in-flight generation is invalidated so a late
                // completion can never publish into a dying process. The
                // user's interactive vendor children are untouched.
                ConnectionWatcher.Shutdown();
                form.ConnCoordinator.Shutdown();
                tray.Dispose();
            }
        }

        // R012: the registry boundary is a seam, so a harness can fail each
        // operation alone; production binds the real HKCU Run key and nothing
        // else about the registry is reachable through it.
        internal static Func<AutostartStore> AutostartStoreSource = () => new RegistryAutostartStore();
        internal const string AutostartValueName = "LimisawApp";

        internal static string ExpectedAutostartCommand()
        {
            return "\"" + Application.ExecutablePath + "\" --minimized";
        }

        // Apply the desired state and PROVE it. Enable is successful only when
        // the exact quoted command reads back; disable only when the value is
        // proven absent. Invocation is not success; wrong readback is a
        // mismatch with a reason, never a success. Nothing here throws into
        // the UI.
        internal static AutostartResult ReconcileAutostart(LimisawSettings s)
        {
            using (AutostartStore store = AutostartStoreSource())
                return ReconcileAutostart(s, store);
        }

        internal static AutostartResult ReconcileAutostart(LimisawSettings s, AutostartStore store)
        {
            var r = new AutostartResult { Desired = s.AutoStart, Reason = "" };
            string want = ExpectedAutostartCommand();
            if (!store.Open(true))
            { r.Reason = "Windows startup entry could not be opened"; return r; }
            if (s.AutoStart)
            {
                if (!store.SetValue(AutostartValueName, want))
                { r.Reason = "Windows startup entry could not be written"; return r; }
            }
            else if (!store.Delete(AutostartValueName))
            { r.Reason = "Windows startup entry could not be removed"; return r; }
            // Readback is the authority — the write or delete above is only an
            // attempt until a read proves what the key actually holds.
            string got;
            if (!store.TryRead(AutostartValueName, out got))
            { r.Reason = "Windows startup entry could not be verified"; return r; }
            r.Actual = got != null && got == want;
            if (s.AutoStart && !(bool)r.Actual)
                r.Reason = got == null
                    ? "Windows startup entry was not applied"
                    : "Windows startup entry does not hold the expected command";
            else if (!s.AutoStart && (bool)r.Actual)
                r.Reason = "Windows startup entry is still present";
            else
                r.Verified = true;
            return r;
        }

        // The tray menu's Opening look: READ-ONLY projection. It must never
        // mutate the registry merely because the user opened the menu — only
        // ReconcileAutostart writes, and only from a real apply/retry path.
        internal static AutostartResult InspectAutostart(LimisawSettings s)
        {
            var r = new AutostartResult { Desired = s.AutoStart, Reason = "" };
            using (AutostartStore store = AutostartStoreSource())
            {
                if (!store.Open(false))
                { r.Reason = "Windows startup entry state is unreadable"; return r; }
                string got;
                if (!store.TryRead(AutostartValueName, out got))
                { r.Reason = "Windows startup entry state is unreadable"; return r; }
                r.Actual = got != null && got == ExpectedAutostartCommand();
                r.Verified = r.Actual == s.AutoStart;
                if (!r.Verified)
                    r.Reason = s.AutoStart
                        ? "Windows startup entry is not applied"
                        : "Windows startup entry is still present";
            }
            return r;
        }

        // R021: every menu piece whose check/text projects a SETTING, held so
        // the Opening handler can rebind all of them from the live settings —
        // not just the ones that happened to be convenient. Constructor-time
        // checks are stale the moment a setting changes from the window or an
        // external reload; the menu is built ONCE and rebound on every open.
        internal class TrayMenuState
        {
            public LimisawSettings S;
            public Func<LimisawForm> GetForm;
            public Action Show;
            public ToolStripMenuItem StatusRows, SettingsState, MetricMenu, InstallMenu;
            public ToolStripMenuItem ModeMenu, FillMenu, ThemeMenu;
            public ToolStripMenuItem Used, Notify, Chime, LowBalloon, LowChime, Auto;
            // Read-only registry look for the autostart projection; may be null
            // in which case only the desired state is shown.
            public Func<AutostartResult> Inspect;
        }

        // The whole Opening body, extracted so the contract is drivable without
        // a real popup: rebinds every static settings entry from the live
        // settings and rebuilds the runtime submenus. PROJECTION ONLY — zero
        // Settings writes and zero registry mutations may happen here; the
        // autostart look is the read-only Inspect, never the reconciler.
        internal static void MenuOpening(TrayMenuState t)
        {
            LimisawForm f = t.GetForm();
            // Accounts, metrics and CLIs are all discovered at runtime, so
            // these three submenus are rebuilt from the live snapshot.
            t.StatusRows.DropDownItems.Clear();
            int count = f.AccountCount;
            t.StatusRows.Text = count == 0 ? "No accounts yet" : count + " account(s)";
            t.StatusRows.Enabled = count > 0;
            for (int i = 0; i < count; i++)
            {
                string line = f.AccountSummary(i, t.S.ShowUsed);
                if (line != null) t.StatusRows.DropDownItems.Add(new ToolStripMenuItem(line) { Enabled = false });
            }

            // R020: the one-line settings ledger, so the menu can answer "is
            // what I see live also on disk?" without opening the window.
            t.SettingsState.Text = t.S.LastSaveFailed
                ? "Settings: save failed — not writable"
                : t.S.Dirty ? "Settings: unsaved changes pending" : "Settings: saved";

            t.MetricMenu.DropDownItems.Clear();
            var lowest = new ToolStripMenuItem("Lowest remaining (recommended)", null, (o2, e2) =>
            { t.S.TrayMetric = "lowest"; t.S.Save(); f.ApplyChoice(); })
            { Tag = "lowest", Checked = t.S.TrayMetric == "lowest",
              ToolTipText = "always show the worst window there is right now — never stale, never a guess" };
            t.MetricMenu.DropDownItems.Add(lowest);
            foreach (Metric metric in f.AllMetrics())
            {
                Metric pick = metric;
                t.MetricMenu.DropDownItems.Add(new ToolStripMenuItem(metric.Label, null, (o2, e2) =>
                { t.S.TrayMetric = pick.Id; t.S.Save(); f.ApplyChoice(); })
                { Tag = metric.Id, Checked = t.S.TrayMetric == metric.Id,
                  ToolTipText = "pin this reading as the tray number (the Number layout shows it)" });
            }

            t.InstallMenu.Text = "Connections";
            t.InstallMenu.DropDownItems.Clear();
            var mi = new ToolStripMenuItem("Manage connections...", null, (o2, e2) => { t.Show(); f.ShowTab(3); })
            { ToolTipText = "open the Connections tab: install, sign-in and diagnostics for every vendor CLI" };
            t.InstallMenu.DropDownItems.Add(mi);
            t.InstallMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (CliInfo cli in f.CliList)
            {
                CliInfo pick = cli;
                t.InstallMenu.DropDownItems.Add(new ToolStripMenuItem(
                    cli.Label + (cli.Installed ? " (installed)" : " — install"), null,
                    (o2, e2) => { t.Show(); f.ShowTab(3); f.RunInstall(pick); })
                { ToolTipText = cli.Installed
                    ? "open Connections and re-check this CLI"
                    : "open Connections and run the guided install for this CLI" });
            }
            if (t.InstallMenu.DropDownItems.Count == 2)
                t.InstallMenu.DropDownItems.Add(new ToolStripMenuItem("Not probed yet") { Enabled = false });

            // ── the static settings entries, rebound from CURRENT state ──
            foreach (ToolStripItem child in t.ModeMenu.DropDownItems)
            {
                var c = child as ToolStripMenuItem;
                if (c != null) c.Checked = (string)c.Tag == t.S.TrayMode;
            }
            foreach (ToolStripItem child in t.FillMenu.DropDownItems)
            {
                var c = child as ToolStripMenuItem;
                if (c != null) c.Checked = (int)c.Tag == t.S.TrayFill;
            }
            foreach (ToolStripItem child in t.ThemeMenu.DropDownItems)
            {
                var c = child as ToolStripMenuItem;
                if (c != null) c.Checked = string.Equals((string)c.Tag, t.S.ThemeSlug, StringComparison.OrdinalIgnoreCase);
            }
            t.Used.Checked = t.S.ShowUsed;
            t.Used.Text = t.S.ShowUsed ? "Show Used %" : "Show Left %";
            t.Notify.Checked = t.S.NotifyOnReset;
            t.Chime.Checked = t.S.ResetSound;
            t.LowBalloon.Checked = t.S.NotifyLow;
            t.LowChime.Checked = t.S.LowSound;

            // R012: the tick shows the desired state; a mismatch the read-only
            // inspection could not verify is named in the text instead of being
            // ticked over. Opening never reconciles — that is a write.
            AutostartResult look = t.Inspect != null ? t.Inspect() : null;
            bool verified = look != null && look.Verified;
            t.Auto.Checked = t.S.AutoStart;
            t.Auto.Text = verified ? "Start with Windows"
                : look != null && look.Reason != null && look.Reason.Length > 0
                ? "Start with Windows — " + look.Reason
                : "Start with Windows";
        }

        internal static ContextMenuStrip BuildMenu(LimisawSettings s, NotifyIcon tray, Func<LimisawForm> getForm,
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
            m.Items[0].ToolTipText = "show the LIMISAW window (it keeps watching from the tray either way)";
            m.Items[1].ToolTipText = "re-probe every vendor account now, without opening the window";
            m.ShowItemToolTips = true;
            m.Items.Add(new ToolStripSeparator());
            // Status rows are rebuilt on every open: the account list is
            // discovered, so its length is not known at build time.
            var statusRows = new ToolStripMenuItem("No accounts yet") { Enabled = false };
            statusRows.ToolTipText = "the accounts discovered right now — open LIMISAW for the full view";
            m.Items.Add(statusRows);
            // R020: the one-line settings ledger, refreshed on every open alongside.
            var settingsState = new ToolStripMenuItem("Settings: saved") { Enabled = false };
            settingsState.ToolTipText = "whether the last settings write reached LIMISAW.ini";
            m.Items.Add(settingsState);
            m.Items.Add(new ToolStripSeparator());

            // Every entry below is a shortcut to a tab in the window, which is
            // where the same setting lives with room to explain itself. The menu
            // stays for one-click changes; ordering a list in a context menu is
            // exactly what the Tray tab exists to avoid.
            m.Items.Add("Tray items, order and count...", null, (o, e) => { show(); getForm().ShowTab(1); });
            m.Items.Add("All settings...", null, (o, e) => { show(); getForm().ShowTab(2); });
            m.Items[m.Items.Count - 2].ToolTipText = "open the Tray tab: which readings the icon shows, their order and the cap";
            m.Items[m.Items.Count - 1].ToolTipText = "open the Settings tab: layout, fill, alerts, sounds and themes";
            m.Items.Add(new ToolStripSeparator());

            var metricMenu = new ToolStripMenuItem("Tray number");
            metricMenu.ToolTipText = "which single reading the Number layout displays";
            m.Items.Add(metricMenu);

            var modeMenu = new ToolStripMenuItem("Tray layout");
            modeMenu.ToolTipText = "the overall shape of the 16x16 tray icon";
            foreach (LimisawSettings.TrayModeDefinition def in LimisawSettings.TrayModes)
            {
                string value = def.Id;
                var item = new ToolStripMenuItem(def.LongLabel, null, (o, e) =>
                {
                    s.TrayMode = value; s.Save();
                    getForm().UpdateTray();
                }) { Tag = value, Checked = s.TrayMode == value, ToolTipText = def.Hint };
                modeMenu.DropDownItems.Add(item);
            }
            m.Items.Add(modeMenu);
            var fillMenu = new ToolStripMenuItem("Fill detail");
            fillMenu.ToolTipText = "how coarsely Gauge, Bars, Rows and Cells fill";
            foreach (LimisawSettings.TrayFillDefinition fd in LimisawSettings.TrayFills)
            {
                int value = fd.Value;
                var item = new ToolStripMenuItem(fd.LongLabel, null, (o, e) =>
                {
                    s.TrayFill = value; s.Save();
                    getForm().UpdateTray();
                }) { Tag = value, Checked = s.TrayFill == value, ToolTipText = fd.Hint };
                fillMenu.DropDownItems.Add(item);
            }
            m.Items.Add(fillMenu);

            var themeMenu = new ToolStripMenuItem("Theme");
            themeMenu.ToolTipText = "the colours for the window, the tray icon and the hover panel";
            foreach (Theme t in themes)
            {
                Theme choice = t;
                var item = new ToolStripMenuItem(t.Label, null, (o, e) =>
                {
                    s.ThemeSlug = choice.Slug; s.Save();
                    getForm().ApplyTheme(choice.Slug);
                    getForm().Refresh(); getForm().UpdateTray();
                }) { Tag = t.Slug, Checked = string.Equals(t.Slug, s.ThemeSlug, StringComparison.OrdinalIgnoreCase),
                     ToolTipText = "switch the whole window and tray to the " + t.Label + " colours" };
                themeMenu.DropDownItems.Add(item);
            }
            m.Items.Add(themeMenu);

            var installMenu = new ToolStripMenuItem("Connections");
            installMenu.ToolTipText = "the supported vendor CLIs, their install and sign-in state";
            m.Items.Add(installMenu);

            m.Items.Add(new ToolStripSeparator());
            var usedItem = new ToolStripMenuItem(s.ShowUsed ? "Show Used %" : "Show Left %") { Checked = s.ShowUsed };
            usedItem.ToolTipText = s.ShowUsed
                ? "currently showing what each window has SPENT — click to show what is LEFT"
                : "currently showing what each window has LEFT — click to show what is SPENT";
            usedItem.Click += (o, e) => { getForm().ToggleShowUsed(); };
            m.Items.Add(usedItem);
            var notifyItem = new ToolStripMenuItem("Notify on reset") { Checked = s.NotifyOnReset };
            notifyItem.ToolTipText = "a Windows balloon when a quota window refills";
            notifyItem.Click += (o, e) => { s.NotifyOnReset = !s.NotifyOnReset; s.Save(); };
            m.Items.Add(notifyItem);
            // The two sounds next to the balloon: each alert is a switch here and
            // a sound here, so muting one does not mute the other.
            var chimeItem = new ToolStripMenuItem("Chime on reset") { Checked = s.ResetSound };
            chimeItem.ToolTipText = "play a sound when a quota window refills";
            chimeItem.Click += (o, e) => { s.ResetSound = !s.ResetSound; s.Save(); };
            m.Items.Add(chimeItem);
            var lowItem = new ToolStripMenuItem("Balloon when quota is low") { Checked = s.NotifyLow };
            lowItem.ToolTipText = "a Windows balloon when a window drops to the low threshold";
            lowItem.Click += (o, e) => { s.NotifyLow = !s.NotifyLow; s.Save(); };
            m.Items.Add(lowItem);
            // CORE-005: the low alert has the same two channels here as the
            // refill one, or the menu could only ever mute both at once.
            var lowChime = new ToolStripMenuItem("Chime when quota is low") { Checked = s.LowSound };
            lowChime.ToolTipText = "play a sound when a window drops to the low threshold";
            lowChime.Click += (o, e) => { s.LowSound = !s.LowSound; s.Save(); };
            m.Items.Add(lowChime);
            var autoItem = new ToolStripMenuItem("Start with Windows") { Checked = s.AutoStart };
            // W2-004/R012: same gate as the Settings button — the registry value
            // is applied only if the ini recorded the choice, and only a verified
            // readback counts. The item's own projection is rebuilt from the
            // verified look on the next Opening.
            autoItem.Click += (o, e) =>
            {
                bool want = !s.AutoStart;
                s.AutoStart = want;
                if (!s.SaveApplied()) { s.AutoStart = !want; return; }
                LimisawForm f = getForm();
                if (f != null) f.ReportAutostart(ReconcileAutostart(s));
                else ReconcileAutostart(s);
            };
            autoItem.ToolTipText = "launch LIMISAW silently with Windows — the registry write follows the ini choice";
            m.Items.Add(autoItem);
            m.Items.Add(new ToolStripSeparator());
            var exitItem = m.Items.Add("Exit", null, (o, e) => { var f = getForm(); f.Close(); try { tray.Visible = false; } catch { } Application.Exit(); });
            exitItem.ToolTipText = "close LIMISAW completely — the tray icon goes too";

            var state = new TrayMenuState
            {
                S = s, GetForm = getForm, Show = show,
                StatusRows = statusRows, SettingsState = settingsState,
                MetricMenu = metricMenu, InstallMenu = installMenu,
                ModeMenu = modeMenu, FillMenu = fillMenu, ThemeMenu = themeMenu,
                Used = usedItem, Notify = notifyItem, Chime = chimeItem,
                LowBalloon = lowItem, LowChime = lowChime, Auto = autoItem,
                Inspect = () => InspectAutostart(s),
            };
            m.Opening += (o, e) => MenuOpening(state);
            // The rebind state rides on the strip so the contract is drivable
            // (tests construct the menu once and project repeatedly).
            m.Tag = state;
            return m;
        }
    }
}
