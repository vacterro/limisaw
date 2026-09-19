using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// PERF-002 (SRC-006:R019): a pure repaint performed synchronous filesystem
// work. PaintAlertsBlock evaluated SoundCue.Library TWICE inside one
// DrawTextFit expression — and Library is not a formatter: it stat-walks the
// user folder, enumerates the assembly manifest (GetManifestResourceNames +
// sort + allocate, per call), runs Directory.CreateDirectory and stats every
// shipped WAV for extraction recovery. With the live sliders repainting per
// pointer pixel that was hundreds of stats and manifest walks for ONE drag.
//
// The contract this harness holds:
//
//   * ordinary Settings paints (and the >=100-value drag patterns) perform
//     ZERO Library calls, ZERO manifest enumerations, ZERO shipped-WAV
//     existence checks and ZERO extractions — the painter reads the
//     off-paint display snapshot;
//   * deleting an extracted shipped WAV mid-session is still recovered —
//     at the REAL sound-use boundary (Play), the missing WAV is re-extracted
//     and plays;
//   * a custom SoundDir appearing or disappearing is observed at the
//     documented boundary (the display refresh), not by per-frame stats.
//
// Deterministic seams: SoundCue.LibraryCalls / ResolveCalls /
// ShippedFileChecks / SoundExtracts and Assets.ManifestCalls. No wall-clock
// benchmarking, no real audio output (the PlayBackend seam records).
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   csc -out:sound_paint_cost.exe -r:System.dll -r:System.Drawing.dll
//       -r:System.Windows.Forms.dll tests\sound_paint_cost.cs
public static class SoundPaintCostTest
{
    static int fails = 0, checks = 0;
    const BindingFlags NS = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags PS = BindingFlags.Static | BindingFlags.Public;
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Type formType, settingsType, themeType;
    static object settings, themes, form_var;
    static System.Windows.Forms.Form F;
    static MethodInfo RefreshDisplay;

    static int CueInt(string field)
    { return (int)formType.Assembly.GetType("Limisaw.SoundCue").GetField(field, NS).GetValue(null); }
    static void CueReset()
    { formType.Assembly.GetType("Limisaw.SoundCue").GetMethod("ResetCountingForTests", NS).Invoke(null, null); }
    static int ManifestCalls()
    { return (int)formType.Assembly.GetType("Limisaw.Assets").GetField("ManifestCalls", NS).GetValue(null); }
    static void ManifestReset()
    { formType.Assembly.GetType("Limisaw.Assets").GetMethod("ResetCacheForTests", NS).Invoke(null, null); }
    static int FInt(string field) { return (int)formType.GetField(field, NP).GetValue(form_var); }
    static string FString(string field) { return (string)formType.GetField(field, NP).GetValue(form_var); }
    static void SSet(string name, object v) { settingsType.GetField(name).SetValue(settings, v); }
    static object SField(string name) { return settingsType.GetField(name).GetValue(settings); }
    static object Call(string method, params object[] args)
    { return formType.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form_var, args); }
    static object PrivCall(string method, params object[] args)
    { return formType.GetMethod(method, NP).Invoke(form_var, args); }

    static readonly List<string> Played = new List<string>();
    public static string Backend(string path) { lock (Played) Played.Add(path); return null; }

    // 16-bit mono PCM at a constant amplitude — a real, playable, tiny WAV.
    static byte[] Tone(short amp)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        short[] s = new short[64];
        for (int i = 0; i < s.Length; i++) s[i] = amp;
        byte[] data = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, data, 0, data.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + data.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(8000); w.Write(16000); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    static void PaintSettings(int w, int h)
    {
        F.ClientSize = new System.Drawing.Size(w, h);
        Call("FitWindow");
        using (var bmp = new System.Drawing.Bitmap(Math.Max(1, F.Width), Math.Max(1, F.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
        {
            var args = new System.Windows.Forms.PaintEventArgs(g,
                new System.Drawing.Rectangle(System.Drawing.Point.Empty, F.ClientSize));
            formType.GetMethod("OnPaint", NP).Invoke(form_var, new object[] { args });
        }
    }

    static void Mouse(string handler, int x, int y)
    {
        formType.GetMethod(handler, NP).Invoke(form_var,
            new object[] { new System.Windows.Forms.MouseEventArgs(
                System.Windows.Forms.MouseButtons.Left, 1, x, y, 0) });
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_soundpaint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // The constructor probes for real. An empty environment makes the
            // start-up sweep find nothing and settle at once.
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(temp, "no-codex"));
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string key in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(key, "");

            Assembly asm = Assembly.LoadFrom(Path.Combine(Directory.GetCurrentDirectory(), "LIMISAW.exe"));
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            themeType = asm.GetType("Limisaw.Theme");
            formType = asm.GetType("Limisaw.LimisawForm");
            RefreshDisplay = formType.GetMethod("RefreshSoundDisplay", NP);

            settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            themes = themeType.GetMethod("Load", PS).Invoke(null, new object[] { Directory.GetCurrentDirectory() });

            // The shipped library the form would display: the ROOT path is the
            // temp dir, so no Sounds folder sits beside the exe and the
            // embedded fallback is the real code path.
            string shipped = (string)asm.GetType("Limisaw.Assets")
                .GetMethod("SoundLibrary", PS).Invoke(null, null);
            Check("the embedded shipped library extracted",
                shipped != null && Directory.Exists(shipped) && File.Exists(Path.Combine(shipped, "pop_cartoon_pop.wav")),
                shipped ?? "(null)");

            using (var tray = new System.Windows.Forms.NotifyIcon())
            using (Form form = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            {
                form_var = form;
                F = form;
                var timer = formType.GetField("RefreshTimer", NP).GetValue(form_var);
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                System.Threading.Thread.Sleep(100);
                formType.GetField("Refreshing", NP).SetValue(form_var, true);
                Call("ShowTab", 2);   // Settings

                // ── warm the display boundary ──────────────────────────────
                // The display snapshot resolves at real sound-use boundaries:
                // construction already refreshed it; do it once more here so
                // every lazily-cached list (manifest names) is populated.
                RefreshDisplay.Invoke(form_var, null);

                // From here on, PAINTS must be free of all sound I/O. Reset
                // the counters AFTER the boundary work, then paint.
                CueReset(); ManifestReset();
                int manifest0 = ManifestCalls();
                for (int i = 0; i < 100; i++) PaintSettings(700, 620);
                Check("100 Settings paints perform ZERO Library calls",
                    CueInt("LibraryCalls") == 0, CueInt("LibraryCalls") + " Library calls");
                Check("100 Settings paints perform ZERO manifest enumerations",
                    ManifestCalls() - manifest0 == 0, (ManifestCalls() - manifest0) + " manifest walks");
                Check("100 Settings paints perform ZERO shipped-WAV existence checks",
                    CueInt("ShippedFileChecks") == 0, CueInt("ShippedFileChecks") + " shipped stats");
                Check("100 Settings paints perform ZERO extractions",
                    CueInt("SoundExtracts") == 0, CueInt("SoundExtracts") + " extractions");

                // ── the audit's drag pattern: >=100 live values ────────────
                SSet("ResetSound", true);
                SSet("NotifyLow", true);
                SSet("SoundVolume", 0);
                PaintSettings(700, 620);   // publish the rail rectangles
                var rail = (System.Drawing.Rectangle)formType.GetField("VolRailVolume", NP).GetValue(form_var);
                Check("the volume rail is live", rail.Width >= 60, "rail " + rail.Width + "px");
                int mid = rail.Y + 6;
                CueReset(); ManifestReset();
                manifest0 = ManifestCalls();
                Mouse("OnMouseDown", rail.X, mid);
                int distinct = 0, lastVal = -1, lastX = rail.X;
                for (int px = rail.X; px <= rail.Right; px++)
                {
                    Mouse("OnMouseMove", px, mid);
                    int v = (int)SField("SoundVolume");
                    if (v != lastVal) { distinct++; lastVal = v; }
                    lastX = px;
                }
                Mouse("OnMouseUp", lastX, mid);
                Check("the drag moved through >=100 live values", distinct >= 100, distinct + " values");
                Check("the >=100-value volume drag performs ZERO Library calls",
                    CueInt("LibraryCalls") == 0, CueInt("LibraryCalls") + " Library calls");
                Check("the drag performs ZERO manifest enumerations",
                    ManifestCalls() - manifest0 == 0, (ManifestCalls() - manifest0) + " manifest walks");
                Check("the drag performs ZERO shipped stats and ZERO extractions",
                    CueInt("ShippedFileChecks") == 0 && CueInt("SoundExtracts") == 0,
                    "stats=" + CueInt("ShippedFileChecks") + ", extracts=" + CueInt("SoundExtracts"));

                // ── the Storage Sense recovery contract, at the boundary ───
                // Delete ONE extracted shipped WAV mid-session, then PLAY:
                // the missing WAV must be re-extracted (SoundExtracts >= 1)
                // and the cue must reach the player.
                string victim = Path.Combine(shipped, "pop_cartoon_pop.wav");
                byte[] before = File.ReadAllBytes(victim);
                File.Delete(victim);
                Check("the extracted shipped WAV is really gone", !File.Exists(victim), victim);
                var cue = asm.GetType("Limisaw.SoundCue");
                var backend = cue.GetField("PlayBackend", NS);
                backend.SetValue(null, Delegate.CreateDelegate(backend.FieldType,
                    typeof(SoundPaintCostTest).GetMethod("Backend", PS)));
                try
                {
                    lock (Played) Played.Clear();
                    CueReset(); ManifestReset();
                    object why = cue.GetMethod("Play", PS).Invoke(null,
                        new object[] { temp, "", "pop_cartoon_pop.wav", 50 });
                    Check("the deleted shipped WAV plays without complaint", why == null, (why ?? "").ToString());
                    Check("...and was re-extracted at the play boundary",
                        CueInt("SoundExtracts") >= 1, CueInt("SoundExtracts") + " extractions");
                    Check("...and the re-extracted bytes are the shipped bytes",
                        File.ReadAllBytes(victim).Length == before.Length, "");
                    Check("...and the player received exactly one real path",
                        Played.Count == 1 && File.Exists(Played[0]),
                        Played.Count + " paths");
                    Check("the shipped recovery cost ZERO manifest re-enumerations beyond the play",
                        ManifestCalls() <= 1, (ManifestCalls()) + " manifest walks (cached names reused)");
                }
                finally { backend.SetValue(null, null); }

                // ── the SoundDir boundary: appear / disappear ──────────────
                string custom = Path.Combine(temp, "my-sounds");
                Directory.CreateDirectory(custom);
                File.WriteAllBytes(Path.Combine(custom, "pop_cartoon_pop.wav"), Tone(6000));
                SSet("SoundDir", custom);
                CueReset();
                RefreshDisplay.Invoke(form_var, null);
                Check("a custom SoundDir appears at the display boundary",
                    FString("SoundDisplayPath") == custom, FString("SoundDisplayPath"));
                Check("...resolved through the Library seam",
                    CueInt("LibraryCalls") >= 1, CueInt("LibraryCalls") + " calls");
                Directory.Delete(custom, true);
                CueReset();
                RefreshDisplay.Invoke(form_var, null);
                Check("a vanished SoundDir is observed at the boundary too (no restart)",
                    FString("SoundDisplayPath") != custom && FString("SoundDisplayPath").Length > 0,
                    FString("SoundDisplayPath"));
                Check("...again resolved through the Library seam",
                    CueInt("LibraryCalls") >= 1, CueInt("LibraryCalls") + " calls");
                SSet("SoundDir", "");
                RefreshDisplay.Invoke(form_var, null);
            }

            // The painter must never have re-extracted on its own: prove the
            // shipped library still holds both shipped WAVs at the end.
            string libEnd = (string)formType.Assembly.GetType("Limisaw.Assets")
                .GetMethod("SoundLibrary", PS).Invoke(null, null);
            Check("the shipped library is intact after the whole run",
                File.Exists(Path.Combine(libEnd, "pop_cartoon_pop.wav"))
                && File.Exists(Path.Combine(libEnd, "success_powerup.wav")), libEnd);
        }
        catch (Exception ex)
        {
            string detail = ex.GetType().Name + ": "
                + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            Exception walk = ex;
            while (walk.InnerException != null) walk = walk.InnerException;
            if (walk.StackTrace != null)
            {
                string[] frames = walk.StackTrace.Split('\n');
                for (int i = 0; i < frames.Length && i < 5; i++) detail += " @ " + frames[i].Trim();
            }
            Check("harness", false, detail);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
