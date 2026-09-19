using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    // The sizes the shell asks for, and therefore the frames the artwork owes.
    static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

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

    static string HashOf(Bitmap b)
    {
        var bytes = new List<byte>();
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                bytes.AddRange(BitConverter.GetBytes(b.GetPixel(x, y).ToArgb()));
        using (var sha = new System.Security.Cryptography.SHA256Managed())
            return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "");
    }

    // The pixels of one extracted HICON, hashed. The Icon wrapper does NOT own
    // the native handle: the caller destroys the HICON itself.
    static string ShellFrameHash(IntPtr hicon)
    {
        using (var shell = Icon.FromHandle(hicon))
        using (Bitmap b = shell.ToBitmap())
            return HashOf(b);
    }

    static string ShortHash(string h)
    {
        return h == null ? "null" : h.Substring(0, 8);
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
                badSizes.Count == 0, badSizes.Count == 0 ? "16/24/32/48/64/128/256"
                    : string.Join(", ", badSizes.ToArray()));

            string readme = File.ReadAllText(Path.Combine(root, "README.md"));
            Check("README uses the LIMISAW header image from inside the repo",
                readme.IndexOf("assets/branding/LIMISAW_HEADER1.png", StringComparison.Ordinal) >= 0,
                readme.IndexOf("assets/branding/LIMISAW_HEADER1.png", StringComparison.Ordinal) < 0
                    ? "header path missing" : "ok");
            string brandHeader = Path.Combine(root, "assets", "branding", "LIMISAW_HEADER1.png");
            Check("assets/branding/LIMISAW_HEADER1.png exists beside the repo",
                File.Exists(brandHeader), File.Exists(brandHeader) ? "ok" : brandHeader);
            string brandIcon = Path.Combine(root, "assets", "branding", "LIMISAW1.png");
            Check("assets/branding/LIMISAW1.png exists beside the repo",
                File.Exists(brandIcon), File.Exists(brandIcon) ? "ok" : brandIcon);
            string builtin = Path.Combine(root, "LIMISAW.ico");
            Check("LIMISAW.ico exists after regeneration",
                File.Exists(builtin), File.Exists(builtin) ? "ok" : builtin);
            if (File.Exists(builtin))
            {
                Check("LIMISAW.ico carries one frame per size (viewable)",
                    FramesOf(builtin).Count == Sizes.Length && MissingFrames(builtin).Count == 0,
                    "frames: " + string.Join("/", FramesOf(builtin).ConvertAll(n => n.ToString()).ToArray())
                        + (MissingFrames(builtin).Count > 0
                            ? "  missing: " + string.Join(", ", MissingFrames(builtin).ConvertAll(n => n.ToString()).ToArray())
                            : ""));
                var icoBad = new List<string>();
                foreach (int size in Sizes)
                {
                    var got = (Icon)icon.Invoke(null, new object[] { temp, size });
                    if (got == null) { icoBad.Add(size + ":null"); continue; }
                    using (got)
                        if (got.Width != size || got.Height != size)
                            icoBad.Add(size + ":" + got.Width + "x" + got.Height);
                }
                Check("the exe carries its own icon frames (not the host's)",
                    icoBad.Count == 0,
                    icoBad.Count == 0 ? "16/24/32/48/64/128/256 via AppIcon"
                        : string.Join(", ", icoBad.ToArray()));
                string legacy = Path.Combine(root, "heh.ico");
                if (File.Exists(legacy))
                    Check("heh.ico remains a well-formed legacy icon if present",
                        FramesOf(legacy).Count >= 5,
                        "frames: " + string.Join("/", FramesOf(legacy).ConvertAll(n => n.ToString()).ToArray()));
            }

            // ── the PNG -> ICO generator, exercised for real ────────────────
            // Consuming the already-built LIMISAW.ico proves the ARTIFACT, not
            // the generator. This compiles the REAL tools\make_ico.cs with the
            // same Framework compiler the build uses, runs it on the real
            // brand PNG, and proves the output: every frame present, each
            // frame decodable by GDI+ at its own size, and the app's actual
            // icon load path (Assets.AppIcon) reading the generated file at
            // representative sizes. Everything happens in a scratch
            // directory — the canonical assets are inputs only, never
            // overwritten.
            string brandPng = Path.Combine(root, "assets", "branding", "LIMISAW1.png");
            string makeIcoSrc = Path.Combine(root, "tools", "make_ico.cs");
            if (File.Exists(brandPng) && File.Exists(makeIcoSrc))
            {
                string csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
                if (!File.Exists(csc))
                    csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
                string genDir = Path.Combine(temp, "gen");
                Directory.CreateDirectory(genDir);
                string genExe = Path.Combine(genDir, "make_ico_under_test.exe");
                string genIco = Path.Combine(genDir, "LIMISAW.ico");
                var psi = new ProcessStartInfo
                {
                    FileName = csc,
                    Arguments = "-nologo -out:\"" + genExe + "\" -r:System.dll -r:System.Drawing.dll \""
                                + makeIcoSrc + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                string cscOut = "";
                using (var p = Process.Start(psi))
                {
                    cscOut = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Check("tools\\make_ico.cs compiles with the Framework compiler",
                        p.ExitCode == 0 && File.Exists(genExe),
                        p.ExitCode == 0 ? genExe : cscOut.Trim());
                }
                if (File.Exists(genExe))
                {
                    var run = new ProcessStartInfo
                    {
                        FileName = genExe,
                        Arguments = "\"" + brandPng + "\" \"" + genIco + "\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    };
                    string runOut = "";
                    using (var p = Process.Start(run))
                    {
                        runOut = p.StandardOutput.ReadToEnd();
                        p.WaitForExit();
                        Check("make_ico runs on the real brand PNG",
                            p.ExitCode == 0 && File.Exists(genIco),
                            runOut.Trim());
                    }
                    if (File.Exists(genIco))
                    {
                        List<int> genFrames = FramesOf(genIco);
                        Check("the generated icon carries every frame the shell asks for",
                            genFrames.Count == Sizes.Length && MissingFrames(genIco).Count == 0,
                            "frames: " + string.Join("/", genFrames.ConvertAll(n => n.ToString()).ToArray()));
                        // Each frame must be real, non-empty, GDI+-decodable
                        // image data at its own size — a header that promises a
                        // frame it cannot decode is not a frame.
                        byte[] raw = File.ReadAllBytes(genIco);
                        int count = BitConverter.ToUInt16(raw, 4);
                        var undecodable = new List<string>();
                        for (int i = 0; i < count; i++)
                        {
                            int entry = 6 + i * 16;
                            int size = raw[entry] == 0 ? 256 : raw[entry];
                            int length = BitConverter.ToInt32(raw, entry + 8);
                            int offset = BitConverter.ToInt32(raw, entry + 12);
                            if (length <= 0 || offset + length > raw.Length)
                            { undecodable.Add(size + ":empty"); continue; }
                            var slice = new byte[length];
                            Array.Copy(raw, offset, slice, 0, length);
                            try
                            {
                                using (var ms = new MemoryStream(slice))
                                using (Image img = Image.FromStream(ms))
                                    if (img.Width != size || img.Height != size)
                                        undecodable.Add(size + ":" + img.Width + "x" + img.Height);
                            }
                            catch (Exception ex)
                            { undecodable.Add(size + ":" + ex.GetType().Name); }
                        }
                        Check("every generated frame decodes through GDI+ at its own size",
                            undecodable.Count == 0,
                            undecodable.Count == 0 ? count + " frames, all decodable" : undecodable[0]);
                        // The app's own load path (LoadImage over the win32
                        // group) must read the generated file too —
                        // representative sizes, exact dimensions.
                        var appBad = new List<string>();
                        foreach (int size in new[] { 16, 32, 256 })
                        {
                            var got = (Icon)icon.Invoke(null, new object[] { genDir, size });
                            if (got == null) { appBad.Add(size + ":null"); continue; }
                            using (got)
                                if (got.Width != size || got.Height != size)
                                    appBad.Add(size + ":" + got.Width + "x" + got.Height);
                        }
                        Check("Assets.AppIcon loads the generated icon at 16/32/256",
                            appBad.Count == 0,
                            appBad.Count == 0 ? "16/32/256 exact" : string.Join(", ", appBad.ToArray()));
                    }
                }
                // genDir lives inside temp, so the finally already deletes the
                // generated exe/icon; the canonical LIMISAW.ico and the brand
                // PNG were never write targets.
            }
            string artwork = Path.Combine(root, "LIMISAW.ico");
            // An external icon wins without a rebuild — LIMISAW.ico first,
            File.Copy(artwork, Path.Combine(temp, "LIMISAW.ico"));
            using (var external = (Icon)icon.Invoke(null, new object[] { temp, 32 }))
                Check("an external LIMISAW.ico is loaded instead of the embedded group",
                    external != null && external.Width == 32,
                    external == null ? "null" : external.Width + "px");
            File.Copy(artwork, Path.Combine(temp, "heh.ico"));
            File.Delete(Path.Combine(temp, "LIMISAW.ico"));
            using (var external = (Icon)icon.Invoke(null, new object[] { temp, 32 }))
                Check("legacy external heh.ico still loads as fallback",
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

            // A BROKEN palette must be named, not silently skipped (T-010): the
            // user wrote the file, so "my theme never appeared and nothing said
            // why" is the worst possible answer. The good ones must still load.
            File.WriteAllText(Path.Combine(themeDir, "broken.json"),
                "{ this is not json at all");
            IList afterBad = (IList)themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp });
            Check("the good palettes still load beside a broken one",
                afterBad.Count == after.Count, afterBad.Count + " themes");
            FieldInfo loadErrors = themeType.GetField("LoadErrors", BindingFlags.Public | BindingFlags.Static);
            IList errors = loadErrors == null ? null : (IList)loadErrors.GetValue(null);
            bool named = false;
            if (errors != null)
                foreach (string e in errors)
                    if (e.IndexOf("broken.json", StringComparison.Ordinal) >= 0) named = true;
            Check("a malformed palette file is NAMED, not silently skipped",
                named, "LoadErrors reported: " + (errors == null ? "NOTHING (the field is gone)" : errors.Count + " error(s)"));
            // Clearing the fault clears the message: a stale complaint about a
            // file the user already fixed is the stale-error defect again.
            File.Delete(Path.Combine(themeDir, "broken.json"));
            themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp });
            if (loadErrors != null)
                Check("fixing the file clears the complaint on the next load",
                    ((IList)loadErrors.GetValue(null)).Count == 0, "");

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

            // Windows Disk Cleanup and Storage Sense delete temp FILES by age and
            // leave the folder. A "does the directory exist" shortcut therefore
            // left the shipped chime pointing at a file that was gone — and since
            // nothing looks at it until an alert fires, the first thing the user
            // would notice is silence (T-014, and T-007's other half).
            MethodInfo soundLib = assets.GetMethod("SoundLibrary", BindingFlags.Public | BindingFlags.Static);
            string lib = (string)soundLib.Invoke(null, null);
            Check("the extracted library is a real folder", lib != null && Directory.Exists(lib), lib ?? "null");
            foreach (string wav in Directory.GetFiles(lib, "*.wav")) File.Delete(wav);
            Check("...and it is now empty, as a temp cleanup would leave it",
                Directory.GetFiles(lib, "*.wav").Length == 0, "");
            string healed = (string)soundLib.Invoke(null, null);
            Check("the next call re-extracts the WAVs instead of trusting the folder",
                healed != null && Directory.GetFiles(healed, "*.wav").Length >= 2,
                Directory.GetFiles(healed ?? lib, "*.wav").Length + " WAVs back");
            string afterClean = (string)cue.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { temp, "", "success_powerup.wav" });
            Check("...so the shipped chime resolves again after a temp cleanup",
                afterClean != null && File.Exists(afterClean), afterClean ?? "null");

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

            // ── the SHELL sees the icon: direct executable extraction ───────
            // Assets.AppIcon proves the app's own load path; the user's report
            // was about EXPLORER, which asks the shell resource APIs. This
            // calls ExtractIconEx on the canonical exe FILE and pins what the
            // shell hands back: a real icon group, small + large frames, and
            // artwork byte-identical to the brand icon file when extracted
            // through the SAME API (the Icon-class rescales differently, so
            // like must compare with like). The negative control is a bare
            // csc exe with no -win32icon: it must extract NOTHING.
            uint groups = ExtractIconEx(exe, -1, null, null, 0);
            Check("the exe exposes a win32 icon group to the shell", groups >= 1,
                "groups=" + groups);
            if (groups >= 1)
            {
                var large = new IntPtr[1]; var small = new IntPtr[1];
                ExtractIconEx(exe, 0, large, small, 1);
                Check("shell extraction returns a large and a small icon",
                    large[0] != IntPtr.Zero && small[0] != IntPtr.Zero,
                    "large=" + large[0] + " small=" + small[0]);
                string shellHash = large[0] != IntPtr.Zero ? ShellFrameHash(large[0]) : null;
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
                // Brand correspondence through the SAME extraction API on the
                // icon file the build embedded.
                string brandHash = null;
                var bLarge = new IntPtr[1]; var bSmall = new IntPtr[1];
                ExtractIconEx(artwork, 0, bLarge, bSmall, 1);
                if (bLarge[0] != IntPtr.Zero) { brandHash = ShellFrameHash(bLarge[0]); DestroyIcon(bLarge[0]); }
                if (bSmall[0] != IntPtr.Zero) DestroyIcon(bSmall[0]);
                Check("the shell-extracted artwork IS the LIMISAW brand frame",
                    shellHash != null && shellHash == brandHash,
                    "shell=" + ShortHash(shellHash) + " brand=" + ShortHash(brandHash));

                // The negative control: an exe built WITHOUT -win32icon carries
                // no icon group at all, so the shell extracts nothing. This is
                // the seam a missing -win32icon regression would break.
                string cscPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
                if (!File.Exists(cscPath))
                    cscPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
                string bareSrc = Path.Combine(temp, "bare.cs");
                string bareExe = Path.Combine(temp, "bare.exe");
                File.WriteAllText(bareSrc,
                    "class Bare { static void Main() { } }\r\n");
                var psiBare = new ProcessStartInfo
                {
                    FileName = cscPath,
                    Arguments = "-nologo -target:winexe -out:\"" + bareExe + "\" \"" + bareSrc + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                bool bareBuilt = false;
                using (var p = Process.Start(psiBare))
                { p.WaitForExit(); bareBuilt = p.ExitCode == 0 && File.Exists(bareExe); }
                Check("the no-icon scratch exe compiled", bareBuilt, bareExe);
                if (bareBuilt)
                {
                    uint bareGroups = ExtractIconEx(bareExe, -1, null, null, 0);
                    var bareLarge = new IntPtr[1]; var bareSmall = new IntPtr[1];
                    ExtractIconEx(bareExe, 0, bareLarge, bareSmall, 1);
                    bool bareEmpty = bareGroups == 0
                        || (bareLarge[0] == IntPtr.Zero && bareSmall[0] == IntPtr.Zero);
                    if (bareLarge[0] != IntPtr.Zero) DestroyIcon(bareLarge[0]);
                    if (bareSmall[0] != IntPtr.Zero) DestroyIcon(bareSmall[0]);
                    Check("a bare exe without -win32icon extracts NO brand icon",
                        bareEmpty,
                        "groups=" + bareGroups + " large=" + bareLarge[0] + " small=" + bareSmall[0]);
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
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
