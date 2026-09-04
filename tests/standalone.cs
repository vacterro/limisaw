using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;

// The claim that makes LIMISAW 0.0.1 what it is: ONE file, no runtime, no
// folder. This copies LIMISAW.exe alone into an empty directory and proves it
// still has every palette, every sound and its icon, and that an external file
// of the same name still overrides the embedded one.
//
// The icon checks run against the real exe from a HOST process, which is where
// a module-vs-process mixup would show: the harness has its own icon, so a
// loader reading the process module would hand back the wrong one.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class Standalone
{
    static int fails = 0, checks = 0;

    // The sizes the shell asks for, and therefore the frames the artwork owes.
    static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128 };

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    // The widths in an .ico's own directory. Reading the file is the only way to
    // tell a real per-size frame from one the loader reduced for us.
    static List<int> FramesOf(string path)
    {
        var widths = new List<int>();
        try
        {
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length < 6) return widths;
            int count = BitConverter.ToUInt16(raw, 4);
            for (int i = 0; i < count && 6 + i * 16 + 16 <= raw.Length; i++)
            {
                byte w = raw[6 + i * 16];
                widths.Add(w == 0 ? 256 : w);
            }
        }
        catch { }
        widths.Sort();
        return widths;
    }

    static List<int> MissingFrames(string path)
    {
        List<int> have = FramesOf(path);
        var missing = new List<int>();
        foreach (int size in Sizes) if (!have.Contains(size)) missing.Add(size);
        return missing;
    }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string source = Path.Combine(root, "LIMISAW.exe");
        if (!File.Exists(source)) source = Path.Combine(root, "..", "LIMISAW.exe");
        source = Path.GetFullPath(source);

        string temp = Path.Combine(Path.GetTempPath(), "limisaw_alone_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            string exe = Path.Combine(temp, "LIMISAW.exe");
            File.Copy(source, exe);
            Check("the folder holds nothing but the exe",
                Directory.GetFileSystemEntries(temp).Length == 1,
                Directory.GetFileSystemEntries(temp).Length + " entries");

            Assembly asm = Assembly.LoadFrom(exe);
            Type themeType = asm.GetType("Limisaw.Theme");
            Type assets = asm.GetType("Limisaw.Assets");
            Type cue = asm.GetType("Limisaw.SoundCue");

            IList themes = (IList)themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp });
            Check("every palette is inside the exe", themes.Count >= 16, themes.Count + " themes");

            // Golden Default is the built-in fallback and must survive as the
            // first entry even with no Themes folder in sight.
            FieldInfo slug = themeType.GetField("Slug");
            bool hasGolden = false;
            foreach (object t in themes) if ((string)slug.GetValue(t) == "goldendefault") hasGolden = true;
            Check("Golden Default is present without a Themes folder", hasGolden, "");

            string library = (string)cue.GetMethod("Library", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp, "" });
            Check("the sound library resolves with no Sounds folder",
                library != null && Directory.Exists(library), library ?? "null");
            string reset = (string)cue.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp, "", "success_powerup.wav" });
            string low = (string)cue.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp, "", "pop_cartoon_pop.wav" });
            Check("the shipped reset chime is playable", reset != null && File.Exists(reset), reset ?? "null");
            Check("the shipped low-quota alert is playable", low != null && File.Exists(low), low ?? "null");

            MethodInfo icon = assets.GetMethod("AppIcon", BindingFlags.Public | BindingFlags.Static);

            // Every size the shell actually asks for must come back at exactly
            // that size.
            //
            // This runs in a HOST process (the harness loaded LIMISAW.exe as an
            // assembly, and the harness has its own icon), so it also proves
            // AppIcon reads ITS OWN module's icon group rather than the process
            // module's — the bug that would silently ship the wrong icon
            // anywhere LIMISAW is not the entry assembly.
            var badSizes = new List<string>();
            foreach (int size in Sizes)
            {
                var got = (Icon)icon.Invoke(null, new object[] { temp, size });
                if (got == null) { badSizes.Add(size + ":null"); continue; }
                using (got)
                    if (got.Width != size || got.Height != size)
                        badSizes.Add(size + ":" + got.Width + "x" + got.Height);
            }
            Check("the icon comes out of the exe at every size the shell asks for",
                badSizes.Count == 0, badSizes.Count == 0 ? "16/24/32/48/64/128"
                    : string.Join(", ", badSizes.ToArray()));

            // ...and it must be a REAL frame per size, not one oversized frame
            // the loader reduces on the fly. That is a property of the artwork
            // file, not of what LoadImage hands back: this artwork is four flat
            // colours, so the shell's own reduction of a single 128x128 frame
            // comes back nearly pixel-identical (measured: 0 differing pixels at
            // 16 and 32, 10 at 24). Reading the icon directory is therefore the
            // only check that can actually tell the two apart — which is the
            // whole regression, since the single-frame file is what LIMISAW
            // shipped with.
            string artwork = Path.Combine(Directory.GetCurrentDirectory(), "heh.ico");
            Check("heh.ico carries one real frame per size, not one oversized frame",
                FramesOf(artwork).Count == Sizes.Length && MissingFrames(artwork).Count == 0,
                "frames: " + string.Join("/", FramesOf(artwork).ConvertAll(n => n.ToString()).ToArray())
                    + (MissingFrames(artwork).Count > 0
                        ? "  missing: " + string.Join(", ", MissingFrames(artwork).ConvertAll(n => n.ToString()).ToArray())
                        : ""));

            // Swapping the artwork must not need a rebuild either.
            File.Copy(artwork, Path.Combine(temp, "heh.ico"));
            using (var external = (Icon)icon.Invoke(null, new object[] { temp, 32 }))
                Check("an external heh.ico is loaded instead of the embedded group",
                    external != null && external.Width == 32,
                    external == null ? "null" : external.Width + "px");
            File.Delete(Path.Combine(temp, "heh.ico"));

            // Customisation must not require a rebuild: a file next to the exe
            // wins over the embedded copy with the same slug.
            string themeDir = Path.Combine(temp, "Themes");
            Directory.CreateDirectory(themeDir);
            File.WriteAllText(Path.Combine(themeDir, "goldendefault.json"),
                "{\"slug\":\"goldendefault\",\"label\":\"Overridden\",\"order\":1,"
                + "\"tokens\":{\"background\":\"#010203\",\"surface\":\"#101010\","
                + "\"surfaceRaised\":\"#111111\",\"textPrimary\":\"#FFFFFF\","
                + "\"dangerText\":\"#FF0000\",\"link\":\"#00FF00\"}}");
            File.WriteAllText(Path.Combine(themeDir, "mine.json"),
                "{\"slug\":\"mine\",\"label\":\"Mine\",\"order\":99,"
                + "\"tokens\":{\"background\":\"#000000\",\"textPrimary\":\"#CCCCCC\","
                + "\"dangerText\":\"#FF0000\",\"link\":\"#00FF00\"}}");

            IList after = (IList)themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp });
            Check("an added palette file shows up", after.Count == themes.Count + 1,
                after.Count + " themes (was " + themes.Count + ")");
            object golden = null;
            foreach (object t in after) if ((string)slug.GetValue(t) == "goldendefault") golden = t;
            Check("a palette file REPLACES the embedded one of the same slug, never duplicates it",
                golden != null && (string)themeType.GetField("Label").GetValue(golden) == "Overridden",
                golden == null ? "missing" : (string)themeType.GetField("Label").GetValue(golden));
            Check("the override's colours are the ones that load",
                golden != null && ((Color)themeType.GetField("BG").GetValue(golden)) == Color.FromArgb(1, 2, 3),
                golden == null ? "missing" : themeType.GetField("BG").GetValue(golden).ToString());

            // A Sounds folder next to the exe likewise wins the picker.
            string soundDir = Path.Combine(temp, "Sounds");
            Directory.CreateDirectory(soundDir);
            string picked = (string)cue.GetMethod("Library", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp, "" });
            Check("a Sounds folder next to the exe wins the picker",
                string.Equals(Path.GetFullPath(picked), Path.GetFullPath(soundDir),
                    StringComparison.OrdinalIgnoreCase), picked);
            string stillThere = (string)cue.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp, "", "success_powerup.wav" });
            Check("...and the shipped default still plays from inside the exe",
                stillThere != null && File.Exists(stillThere), stillThere ?? "null");

            // A folder the ini cannot be written in is the "drop it in Program
            // Files" install, and losing every setting to it without a word was
            // T-011: the theme, volume, tray order and window position all work
            // until restart and then revert, which the user can only discover
            // after the fact. Save() must say it could not save.
            Type settingsType = asm.GetType("Limisaw.LimisawSettings");
            FieldInfo failed = settingsType.GetField("LastSaveFailed");
            string ini = Path.Combine(temp, "LIMISAW.ini");
            File.WriteAllText(ini, "[limisaw]\r\nTheme=oled\r\n");
            new FileInfo(ini).IsReadOnly = true;
            object ro = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(ro, null);
            settingsType.GetField("ThemeSlug").SetValue(ro, "nord");
            settingsType.GetMethod("Save").Invoke(ro, null);
            Check("Save() against a read-only ini reports the failure",
                (bool)failed.GetValue(ro), "LastSaveFailed=" + failed.GetValue(ro));
            object reload = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(reload, null);
            Check("...and the theme really did not land",
                (string)settingsType.GetField("ThemeSlug").GetValue(reload) == "oled",
                (string)settingsType.GetField("ThemeSlug").GetValue(reload) + " (still the disk value)");
            new FileInfo(ini).IsReadOnly = false;
            settingsType.GetMethod("Save").Invoke(ro, null);
            Check("a later successful Save clears the flag",
                !(bool)failed.GetValue(ro), "LastSaveFailed=" + failed.GetValue(ro));
            object reload2 = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(reload2, null);
            Check("...and this time the theme lands",
                (string)settingsType.GetField("ThemeSlug").GetValue(reload2) == "nord",
                (string)settingsType.GetField("ThemeSlug").GetValue(reload2));
            File.Delete(ini);

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
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
