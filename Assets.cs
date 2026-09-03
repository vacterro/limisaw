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
            var found = new List<string>();
            try
            {
                foreach (string name in Self.GetManifestResourceNames())
                    if (name.StartsWith(prefix, StringComparison.Ordinal)) found.Add(name);
            }
            catch { }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found.ToArray();
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

        // The shipped WAVs unpacked once, so the sound picker can list them and
        // SoundPlayer can open them by path (it has no stream+volume path that
        // also survives the scaling step). Re-extracts whatever is missing, so
        // a cleaned temp folder heals itself on the next alert.
        public static string SoundLibrary()
        {
            string[] names = Names(SoundPrefix);
            if (names.Length == 0) return null;
            if (SoundDir != null && Directory.Exists(SoundDir)) return SoundDir;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "limisaw_sounds", "lib");
                Directory.CreateDirectory(dir);
                foreach (string name in names)
                {
                    string file = Path.Combine(dir, name.Substring(SoundPrefix.Length));
                    if (File.Exists(file)) continue;
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
            catch { return null; }
        }

        // The app icon, at the exact size the shell asked for.
        //
        // Loaded through the shell's own `LoadImage`, not `new Icon(path, w, h)`:
        // System.Drawing.Icon misparses a PNG-compressed frame when it picks a
        // size out of a multi-frame .ico (it takes the BITMAPINFOHEADER path and
        // reads past the end of the frame). Going through the OS is what lets the
        // icon ship as ~4 KB of PNG frames instead of ~99 KB of raw DIBs, and it
        // is the same loader the shell uses for the taskbar and Alt-Tab, so the
        // window icon cannot disagree with them.
        //
        // The icon inside the exe is its win32 icon group (`-win32icon:`), which
        // Windows already needs for Explorer — so there is no second embedded
        // copy. An external heh.ico still wins, so the icon can be swapped
        // without a rebuild.
        public static Icon AppIcon(string root, int size)
        {
            string external = Path.Combine(root ?? "", "heh.ico");
            if (File.Exists(external))
            {
                Icon file = FromHandle(Native.LoadImage(IntPtr.Zero, external,
                    Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE));
                if (file != null) return file;
            }
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
