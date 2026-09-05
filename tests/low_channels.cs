using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// CORE-005: every alert in this app is TWO switches — the balloon and the chime —
// because muting one and keeping the other is a real preference. The refill alert
// was built that way (NotifyOnReset + ResetSound); the low-quota alert had a
// single NotifyLow that meant event-enable, balloon-enable and sound-enable at
// once. A balloon with the chime muted was impossible, a chime with no balloon
// was unreachable, and the volume row treated NotifyLow as evidence a sound
// would play.
//
// The contract this harness holds:
//
//   * NotifyLow (balloon) and LowSound (chime) persist independently, all four
//     combinations;
//   * an ini written before the split keeps doing exactly what it did — a
//     previously audible low alert stays audible, a previously muted one stays
//     muted;
//   * the EVENT is armed when either channel is on, and each half then asks its
//     own switch: off/off silent, balloon-only balloon and no WAV, sound-only WAV
//     and no balloon, both both;
//   * the once-per-cycle, silent-baseline, drift-is-not-a-rollover and
//     re-arm-on-recovery rules still hold in a channel combination that has no
//     balloon at all;
//   * the panel draws the low alert with the same two-switch row as the refill
//     alert, and the volume is dead only when both CHIMES are off.
//
// The player is replaced (SoundCue.PlayBackend) because a shared SoundPlayer
// cannot be asked how many cues it was handed; the balloon is read off a hidden
// NotifyIcon, which discards ShowBalloonTip instead of pestering the desktop.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class LowChannels
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags NS = BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;

    static Type accType, winType, formType, settingsType, cueType;
    static object form, settings;
    static NotifyIcon Tray;

    static int plays;
    static string Backend(string path) { Interlocked.Increment(ref plays); return null; }

    // ── reflection helpers ──────────────────────────────────────────────────
    static object SGet(string name) { return settingsType.GetField(name).GetValue(settings); }
    static void SSet(string name, object v) { settingsType.GetField(name).SetValue(settings, v); }
    static object Get(string name) { return formType.GetField(name, NP).GetValue(form); }
    static IDictionary Notified() { return (IDictionary)Get("NotifiedLow"); }
    static void Call(string name) { formType.GetMethod(name, NP).Invoke(form, null); }

    static object NewSettings(string dir)
    {
        object s = Activator.CreateInstance(settingsType, new object[] { dir });
        settingsType.GetMethod("Load").Invoke(s, null);
        return s;
    }

    static bool Bool(object s, string name) { return (bool)settingsType.GetField(name).GetValue(s); }

    static object Window(string key, int rem, string reset)
    {
        object w = Activator.CreateInstance(winType);
        winType.GetField("Key").SetValue(w, key);
        winType.GetField("Base").SetValue(w, key);
        winType.GetField("Label").SetValue(w, key);
        winType.GetField("Group").SetValue(w, "");
        winType.GetField("GroupLabel").SetValue(w, "");
        winType.GetField("Available").SetValue(w, true);
        winType.GetField("Rem").SetValue(w, rem);
        winType.GetField("Reset").SetValue(w, reset);
        winType.GetField("DurationMinutes").SetValue(w, 300);
        return w;
    }

    static void Fleet(int rem, string reset)
    {
        IList accounts = (IList)Get("Accounts");
        accounts.Clear();
        object a = Activator.CreateInstance(accType);
        accType.GetField("Provider").SetValue(a, "codex");
        accType.GetField("ProviderLabel").SetValue(a, "Codex");
        accType.GetField("Name").SetValue(a, "one");
        accType.GetField("Status").SetValue(a, "OK");
        accType.GetField("Ok").SetValue(a, true);
        ((IList)accType.GetField("Windows").GetValue(a)).Add(Window("five_hour", rem, reset));
        accounts.Add(a);
    }

    // One sweep's worth of alert processing, exactly the order Publish uses.
    static void Sweep() { Call("RearmLowAlerts"); Call("DetectLow"); }

    static string Tip() { return Tray.BalloonTipText ?? ""; }

    static void Channels(bool balloon, bool chime)
    {
        SSet("NotifyLow", balloon);
        SSet("LowSound", chime);
    }

    // Forget everything the previous scenario recorded, so each one starts from
    // a launched-just-now app.
    static void Reset()
    {
        Notified().Clear();
        formType.GetField("LowBaseline", NP).SetValue(form, false);
        Tray.BalloonTipText = "";
        plays = 0;
    }

    static string Soon = DateTime.Now.AddHours(2).ToString("yyyy-MM-ddTHH:mm:ss");

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_lowch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            Assembly asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            accType = asm.GetType("Limisaw.AccountData");
            winType = asm.GetType("Limisaw.WindowData");
            formType = asm.GetType("Limisaw.LimisawForm");
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            cueType = asm.GetType("Limisaw.SoundCue");
            Type themeType = asm.GetType("Limisaw.Theme");

            Persistence(temp);
            Migration(temp);

            // No vendor home, no CLI on PATH, no Zcode key: the constructor's own
            // sweep finds nothing, so every alert measured below is one this
            // harness raised.
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(temp, "no-codex"));
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string key in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(key, null);

            // A real WAV in a real folder: the chime path resolves, scales and
            // reaches the player seam, so "did a sound play" is measured and not
            // assumed.
            string sounds = Path.Combine(temp, "Sounds");
            Directory.CreateDirectory(sounds);
            File.WriteAllBytes(Path.Combine(sounds, "low.wav"), Tone(9000));

            settings = NewSettings(temp);
            SSet("SoundDir", sounds);
            SSet("LowSoundFile", "low.wav");
            SSet("SoundVolume", 50);
            SSet("LowPct", 20);
            object themes = themeType.GetMethod("Load", PS).Invoke(null, new object[] { root });

            FieldInfo backend = cueType.GetField("PlayBackend", NS);
            backend.SetValue(null, Delegate.CreateDelegate(backend.FieldType,
                typeof(LowChannels).GetMethod("Backend", NS)));
            try
            {
                using (var tray = new NotifyIcon())
                using (Form f = (Form)Activator.CreateInstance(formType,
                    new object[] { temp, settings, tray, themes }))
                {
                    form = f; Tray = tray;
                    Settle();

                    Matrix();
                    CycleRules();
                    Panel();
                }
            }
            finally { backend.SetValue(null, null); }

            Source(root);
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally { try { Directory.Delete(temp, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    // The constructor starts a real sweep. Let it finish (it has nothing to
    // find), then hold the gate shut: a sweep landing mid-test would replace the
    // injected fleet and make every assertion a race.
    static void Settle()
    {
        object timer = Get("RefreshTimer");
        timer.GetType().GetMethod("Stop").Invoke(timer, null);
        for (int i = 0; i < 1200 && (bool)Get("Refreshing"); i++)
        { Application.DoEvents(); Thread.Sleep(25); }
        formType.GetField("Refreshing", NP).SetValue(form, true);
    }

    // ── the two switches persist independently ──────────────────────────────
    static void Persistence(string temp)
    {
        Console.WriteLine("== balloon and chime are two settings, not one ==");
        string dir = Path.Combine(temp, "persist");
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "LIMISAW.ini");

        foreach (bool balloon in new[] { false, true })
            foreach (bool chime in new[] { false, true })
            {
                object s = NewSettings(dir);
                settingsType.GetField("NotifyLow").SetValue(s, balloon);
                settingsType.GetField("LowSound").SetValue(s, chime);
                settingsType.GetMethod("Save").Invoke(s, null);
                object back = NewSettings(dir);
                Check("balloon=" + balloon + " chime=" + chime + " survives a save/load",
                    Bool(back, "NotifyLow") == balloon && Bool(back, "LowSound") == chime,
                    "NotifyLow=" + Bool(back, "NotifyLow") + ", LowSound=" + Bool(back, "LowSound"));
            }

        string text = File.ReadAllText(ini);
        Check("the chime has a key of its own on disk",
            text.IndexOf("LowSound=", StringComparison.Ordinal) >= 0, "");
    }

    // ── an existing installation keeps doing what it did ────────────────────
    static void Migration(string temp)
    {
        Console.WriteLine();
        Console.WriteLine("== an ini written before the split is not reinterpreted ==");
        Func<string, object> Load = body =>
        {
            string dir = Path.Combine(temp, "mig_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "LIMISAW.ini"), "[limisaw]\r\n" + body);
            return NewSettings(dir);
        };

        object audible = Load("NotifyLow=1\r\nLowPct=20\r\n");
        Check("an old ini whose low alert was ON stays audible",
            Bool(audible, "NotifyLow") && Bool(audible, "LowSound"),
            "NotifyLow=" + Bool(audible, "NotifyLow") + ", LowSound=" + Bool(audible, "LowSound"));

        object muted = Load("NotifyLow=0\r\nLowPct=20\r\n");
        Check("...and an old ini whose low alert was OFF stays silent, not half-armed",
            !Bool(muted, "NotifyLow") && !Bool(muted, "LowSound"),
            "NotifyLow=" + Bool(muted, "NotifyLow") + ", LowSound=" + Bool(muted, "LowSound"));

        object balloonOnly = Load("NotifyLow=1\r\nLowSound=0\r\n");
        Check("an explicit LowSound=0 beats the migration default",
            Bool(balloonOnly, "NotifyLow") && !Bool(balloonOnly, "LowSound"), "");

        object soundOnly = Load("NotifyLow=0\r\nLowSound=1\r\n");
        Check("...and a chime with no balloon is a state the file can express",
            !Bool(soundOnly, "NotifyLow") && Bool(soundOnly, "LowSound"), "");
    }

    // ── all four combinations, driven through the real alert path ────────────
    static void Matrix()
    {
        Console.WriteLine();
        Console.WriteLine("== each half asks its own switch ==");

        Reset(); Channels(false, false);
        Fleet(50, Soon); Sweep();              // baseline sweep, nothing low
        Fleet(5, Soon); Sweep();
        Check("off/off: no balloon, no chime",
            Tip().Length == 0 && plays == 0, "tip=\"" + Tip() + "\", plays=" + plays);
        Check("...and the event is not even recorded, so nothing is suppressed later",
            Notified().Count == 0, Notified().Count + " tracked");

        Reset(); Channels(true, false);
        Fleet(50, Soon); Sweep();
        Fleet(5, Soon); Sweep();
        Check("balloon-only: the balloon appears",
            Tip().IndexOf("only 5% left", StringComparison.Ordinal) >= 0, "tip=\"" + Tip() + "\"");
        Check("...and the WAV is never handed to the player",
            plays == 0, plays + " cues played");

        Reset(); Channels(false, true);
        Fleet(50, Soon); Sweep();
        Fleet(5, Soon); Sweep();
        Check("sound-only: the chime plays",
            plays == 1, plays + " cues played");
        Check("...with no balloon at all",
            Tip().Length == 0, "tip=\"" + Tip() + "\"");

        Reset(); Channels(true, true);
        Fleet(50, Soon); Sweep();
        Fleet(5, Soon); Sweep();
        Check("both: balloon and chime",
            Tip().IndexOf("only 5% left", StringComparison.Ordinal) >= 0 && plays == 1,
            "tip=\"" + Tip() + "\", plays=" + plays);
    }

    // ── the timing rules survive a combination with no balloon ──────────────
    // These are the reason the EVENT is armed by either channel rather than by
    // the balloon: the suppression record is per event, so a chime-only user must
    // get the same once-per-cycle behaviour a balloon user gets.
    static void CycleRules()
    {
        Console.WriteLine();
        Console.WriteLine("== once per window per cycle, chime-only ==");
        Reset(); Channels(false, true);

        Fleet(4, Soon);
        Sweep();
        Check("the first sweep after launch records what is already low, silently",
            plays == 0 && Notified().Count == 1, "plays=" + plays + ", tracked=" + Notified().Count);

        Sweep(); Sweep(); Sweep();
        Check("three identical sweeps play nothing more",
            plays == 0, plays + " cues played");

        // A rolling window pushes its reset out a few minutes on every spend.
        Fleet(3, DateTime.Now.AddHours(2).AddMinutes(7).ToString("yyyy-MM-ddTHH:mm:ss"));
        Sweep();
        Check("consumption drift on a rolling window is not a new cycle",
            plays == 0, plays + " cues played");

        Fleet(3, DateTime.Now.AddHours(7).ToString("yyyy-MM-ddTHH:mm:ss"));
        Sweep();
        Check("a real rollover alerts again, chime and no balloon",
            plays == 1 && Tip().Length == 0, "plays=" + plays + ", tip=\"" + Tip() + "\"");

        Fleet(80, Soon);
        Sweep();
        Check("recovering above the threshold drops the record",
            Notified().Count == 0, Notified().Count + " tracked");

        plays = 0;
        Fleet(6, Soon);
        Sweep();
        Check("...so the next drop is a real new alert",
            plays == 1 && Notified().Count == 1, "plays=" + plays + ", tracked=" + Notified().Count);
    }

    // ── the panel ───────────────────────────────────────────────────────────
    // Buttons/HintTexts/Cropped are the panel's own live lists, cleared on every
    // paint: a Frame that kept the references would silently describe the LAST
    // paint, so each one is snapshotted.
    class Frame
    {
        public List<string> Hints = new List<string>();
        public List<string> Cropped = new List<string>();
        public int Height;
        public Rectangle LowRail;
    }

    static Frame Paint(int width)
    {
        var f = (Form)form;
        f.ClientSize = new Size(width, f.ClientSize.Height);
        formType.GetMethod("FitWindow", NP).Invoke(form, null);
        using (var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            var args = new PaintEventArgs(g, new Rectangle(0, 0, bmp.Width, bmp.Height));
            formType.GetMethod("OnPaint", NP).Invoke(form, new object[] { args });
        }
        var frame = new Frame { Height = f.Height, LowRail = (Rectangle)Get("VolRailLow") };
        foreach (string s in (IList)Get("HintTexts")) frame.Hints.Add(s);
        foreach (string s in (IList)Get("Cropped")) frame.Cropped.Add(s);
        return frame;
    }

    static bool Says(Frame f, string needle)
    {
        foreach (string s in f.Hints)
            if (s != null && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    static void Panel()
    {
        Console.WriteLine();
        Console.WriteLine("== the low alert is drawn as the two-switch row it now is ==");
        formType.GetMethod("ShowTab").Invoke(form, new object[] { 2 });
        // The refill chime is off throughout, so the volume row can only be live
        // because of the LOW chime — which is the coupling being tested.
        SSet("ResetSound", false);

        Channels(true, true);
        Frame both = Paint(560);
        Check("the low row explains both of its switches",
            Says(both, "balloon: a Windows notification when a window drops")
            && Says(both, "chime: play a sound when a window drops"), "");
        Check("...and its threshold slider is live",
            both.LowRail != Rectangle.Empty, both.LowRail.ToString());
        Check("a low chime alone makes the volume live",
            Says(both, "one volume for every alert"), "");

        Channels(true, false);
        Frame balloonOnly = Paint(560);
        Check("muting the low chime kills the volume, and says why",
            Says(balloonOnly, "nothing to set a volume for"), "");
        Check("...while the threshold stays live, because the balloon still fires",
            balloonOnly.LowRail != Rectangle.Empty, balloonOnly.LowRail.ToString());

        Channels(false, true);
        Frame soundOnly = Paint(560);
        Check("a chime with no balloon keeps the threshold live",
            soundOnly.LowRail != Rectangle.Empty, soundOnly.LowRail.ToString());
        Check("...and the volume is live again, on the chime alone",
            Says(soundOnly, "one volume for every alert"), "");

        Channels(false, false);
        Frame off = Paint(560);
        Check("both low channels off: the threshold is dead and says why",
            off.LowRail == Rectangle.Empty && Says(off, "both low channels are off"),
            off.LowRail.ToString());

        // The alert rows are fixed now — the WAV picker collapses INSIDE its row
        // instead of adding one — so no combination can grow the panel past a
        // window height that was already chosen.
        Check("the panel is the same height in every combination",
            both.Height == balloonOnly.Height && both.Height == soundOnly.Height
            && both.Height == off.Height,
            both.Height + "/" + balloonOnly.Height + "/" + soundOnly.Height + "/" + off.Height);

        var bad = new List<string>();
        foreach (int width in new[] { 420, 480, 560, 700, 900 })
            foreach (bool balloon in new[] { false, true })
                foreach (bool chime in new[] { false, true })
                {
                    Channels(balloon, chime);
                    Frame f = Paint(width);
                    if (f.Cropped.Count > 0)
                        bad.Add(width + "px " + balloon + "/" + chime + " cropped \"" + f.Cropped[0] + "\"");
                }
        Check("no width and no combination crops a label",
            bad.Count == 0, bad.Count == 0 ? "5 widths x 4 combinations" : bad[0]);
    }

    // The shape of the fix, so a future edit cannot quietly fuse the two
    // switches back into one.
    static void Source(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== the shape of the fix ==");
        string dir = root;
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) break;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        string ui = File.ReadAllText(Path.Combine(dir ?? root, "LIMISAW.cs"));

        Check("the event is armed by either channel",
            ui.IndexOf("if (!Settings.NotifyLow && !Settings.LowSound) return;", StringComparison.Ordinal) >= 0, "");
        Check("the alert asks each switch for its own half",
            ui.IndexOf("if (Settings.NotifyLow) { if (InvokeRequired) BeginInvoke(show); else show(); }", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (Settings.LowSound) Play(Settings.LowSoundFile);", StringComparison.Ordinal) >= 0, "");
        Check("the volume asks the CHIMES, never the balloon",
            ui.IndexOf("bool anySound = Settings.ResetSound || Settings.LowSound;", StringComparison.Ordinal) >= 0
            && ui.IndexOf("Settings.ResetSound || Settings.NotifyLow", StringComparison.Ordinal) < 0, "");
    }

    // A tiny 16-bit mono WAV, so the chime path has real samples to scale.
    static byte[] Tone(int amp)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        short[] s = new short[64];
        for (int i = 0; i < s.Length; i++) s[i] = (short)amp;
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
}
