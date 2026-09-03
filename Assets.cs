using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;

// Everything LIMISAW ships — every palette, every sound, its icon — is compiled
// INTO the exe, so one file dropped in an empty folder is a complete install.
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
        const string IconName = "Limisaw.heh.ico";

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

        // The size the shell asked for, from the exe's own icon resource. An
        // external heh.ico still wins so the icon can be swapped without a
        // rebuild; plain `new Icon(path)` is never used because it returns the
        // largest frame (128x128 here) and the shell would blur it into 16x16.
        public static Icon AppIcon(string root, int size)
        {
            string external = Path.Combine(root ?? "", "heh.ico");
            try { if (File.Exists(external)) return new Icon(external, size, size); }
            catch { }
            try
            {
                using (Stream s = Self.GetManifestResourceStream(IconName))
                    if (s != null) return new Icon(s, size, size);
            }
            catch { }
            return null;
        }
    }
}
