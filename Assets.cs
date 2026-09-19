using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

// Everything LIMISAW ships — every palette, every sound, its icon — is inside
// the exe, so one file dropped in an empty folder is a complete install.
//
// A file of the same name next to the exe still wins. Customisation must never
// require rebuilding the program: `Themes\mine.json` adds a palette, and
// `Themes\goldendefault.json` replaces the built-in one.
namespace Limisaw
{
    static class Assets
    {
        const string ThemePrefix = "Limisaw.Themes.";
        const string SoundPrefix = "Limisaw.Sounds.";

        static Assembly Self { get { return typeof(Assets).Assembly; } }

        static string[] Names(string prefix)
        {
            // PERF-002 (SRC-006:R019): the embedded manifest names are immutable
            // for the running process, so enumerate them once per process rather
            // than on every paint. A counting seam proves the single enumeration.
            if (prefix == SoundPrefix && CachedSoundNames != null) return CachedSoundNames;
            if (prefix == ThemePrefix && CachedThemeNames != null) return CachedThemeNames;
            var found = new List<string>();
            try
            {
                ManifestCalls++;
                foreach (string name in Self.GetManifestResourceNames())
                    if (name.StartsWith(prefix, StringComparison.Ordinal)) found.Add(name);
            }
            catch { }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            string[] result = found.ToArray();
            if (prefix == SoundPrefix) CachedSoundNames = result;
            else if (prefix == ThemePrefix) CachedThemeNames = result;
            return result;
        }

        internal static int ManifestCalls;
        static string[] CachedSoundNames, CachedThemeNames;
        internal static void ResetCacheForTests()
        {
            CachedSoundNames = null; CachedThemeNames = null;
            ManifestCalls = 0;
        }

        // (fallback slug, JSON text) for every embedded palette.
        public static List<KeyValuePair<string, string>> ThemeFiles()
        {
            var outList = new List<KeyValuePair<string, string>>();
            foreach (string name in Names(ThemePrefix))
            {
                string text = Text(name);
                if (text == null) continue;
                string slug = name.Substring(ThemePrefix.Length);
                if (slug.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    slug = slug.Substring(0, slug.Length - 5);
                outList.Add(new KeyValuePair<string, string>(slug, text));
            }
            return outList;
        }

        static string Text(string name)
        {
            try
            {
                using (Stream s = Self.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    using (var reader = new StreamReader(s)) return reader.ReadToEnd();
                }
            }
            catch { return null; }
        }

        static string SoundDir;

        // The shipped WAVs, unpacked so the picker can list them and SoundPlayer
        // can open them by path (it has no stream+volume path that survives the
        // scaling step).
        //
        // Every call re-checks the FILES, not just the folder. Windows Disk
        // Cleanup and Storage Sense delete temp files by age and leave the
        // directory behind, so a "does the folder exist" shortcut left the
        // shipped chime pointing at a file that was gone — silently, since
        // nothing else looks at it until an alert fires. Re-extraction is
        // File.Exists per name, which costs two stat calls on the hot path.
        //
        // PERF-002 (SRC-006:R019): the DISK-EXISTS + extraction work is kept
        // as the recovery path: Resolve/Play (real sound use) still call
        // Ensure. The Library() seam itself is never invoked from Settings
        // paint any more — the paint's DisplayPath is the cached
        // SoundCue.Library() result resolved off-paint, with a counting seam
        // that must show Library-calls == 0 on the paint path.
        public static string SoundLibrary()
        {
            string[] names = Names(SoundPrefix);
            if (names.Length == 0) return null;
            try
            {
                string dir = SoundDir ?? Path.Combine(Path.GetTempPath(), "limisaw_sounds", "lib");
                Directory.CreateDirectory(dir);
                foreach (string name in names)
                {
                    string file = Path.Combine(dir, name.Substring(SoundPrefix.Length));
                    if (File.Exists(file)) continue;
                    Limisaw.SoundCue.SoundExtracts++;
                    using (Stream s = Self.GetManifestResourceStream(name))
                    {
                        if (s == null) continue;
                        using (var target = new FileStream(file, FileMode.Create, FileAccess.Write))
                            s.CopyTo(target);
                    }
                }
                SoundDir = dir;
                return dir;
            }
            catch { return SoundDir; }
        }

        // The app icon, at the exact size the shell asked for.
        //
        // Loaded through the shell's own `LoadImage`, not `new Icon(path, w, h)`:
        // System.Drawing.Icon misparses a PNG-compressed frame when it picks a
        // size out of a multi-frame .ico (it takes the BITMAPINFOHEADER path and
        // reads past the end of the frame). Going through the OS is what lets the
        // icon ship as PNG frames instead of raw DIBs, and it is the same loader
        // the shell uses for the taskbar and Alt-Tab, so the window icon cannot
        // disagree with them.
        //
        // The icon inside the exe is its win32 icon group (`-win32icon:`), which
        // Windows already needs for Explorer — so there is no second embedded
        // copy. Precedence: an external LIMISAW.ico (the current override name)
        // wins, then the legacy external heh.ico older installs may still carry,
        // then the embedded group — so the icon can be swapped without a
        // rebuild either way.
        public static Icon AppIcon(string root, int size)
        {
            Icon file = ExternalIcon(root, "LIMISAW.ico", size);
            if (file == null) file = ExternalIcon(root, "heh.ico", size);
            if (file != null) return file;
            // GetHINSTANCE of THIS module, not GetModuleHandle(null): under a
            // test harness or any other host, the process module is the host and
            // carries a different icon (or none).
            IntPtr self;
            try { self = Marshal.GetHINSTANCE(typeof(Assets).Module); }
            catch { return null; }
            if (self == IntPtr.Zero || self == new IntPtr(-1)) return null;
            return FromHandle(Native.LoadImage(self, GroupIconId,
                Native.IMAGE_ICON, size, size, 0));
        }

        static Icon ExternalIcon(string root, string name, int size)
        {
            string external = Path.Combine(root ?? "", name);
            if (!File.Exists(external)) return null;
            return FromHandle(Native.LoadImage(IntPtr.Zero, external,
                Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE));
        }

        // The first icon group csc emits for -win32icon:, by convention.
        const string GroupIconId = "#32512";

        // Icon.FromHandle does NOT own the handle, so the managed object dies
        // with it; Clone() copies the bits out and lets the handle go.
        static Icon FromHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            try
            {
                using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone();
            }
            catch { return null; }
            finally { Native.DestroyIcon(handle); }
        }
    }
}
