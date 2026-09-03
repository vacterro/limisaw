using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;

// The claim that makes LIMISAW 0.0.1 what it is: ONE file, no runtime, no
// folder. This copies LIMISAW.exe alone into an empty directory and proves it
// still has every palette, every sound and its icon, and that an external file
// of the same name still overrides the embedded one.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class Standalone
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
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
            var sixteen = (Icon)icon.Invoke(null, new object[] { temp, 16 });
            var thirtytwo = (Icon)icon.Invoke(null, new object[] { temp, 32 });
            Check("the icon comes out of the exe at the size the shell asks for",
                sixteen != null && sixteen.Width == 16 && thirtytwo != null && thirtytwo.Width == 32,
                sixteen == null ? "null" : sixteen.Width + "px / " + (thirtytwo == null ? "null" : thirtytwo.Width + "px"));

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
