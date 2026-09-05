using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// PERF-003: the three Settings sliders (alert volume, low threshold, preview
// quota) are ONE gesture each — press, move, release. Every MouseMove used to
// call Settings.Save(), so a single drag across the rail rewrote LIMISAW.ini
// dozens of times: twenty-four WritePrivateProfileString calls per pixel step,
// for one user action. The low slider was worse: it also cleared NotifiedLow on
// every move, re-arming the low alert over and over mid-drag.
//
// The contract this harness holds:
//
//   * the pointer still gets LIVE values on every move (the number and the fill
//     follow the cursor — nothing is deferred visually);
//   * nothing is persisted while the gesture is in flight;
//   * exactly ONE commit happens when the gesture ends;
//   * losing capture without a MouseUp (a popup, an alt-tab) still ends the
//     gesture with that one commit, instead of leaving the value unpersisted;
//   * the value that lands on disk is the LAST pointer value;
//   * NotifiedLow is re-armed once per gesture, not once per pointer move;
//   * and a press that never moved anything commits nothing at all.
//
// Commits are counted through the production write seam (LimisawSettings
// .WriteHook): one Save() is one pass over the key list, so counting a single
// key's writes counts saves. The hook still writes for real, so the reload check
// at the end reads bytes the drag actually produced.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class SliderCommit
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags NPI = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;

    static Type formType, settingsType;
    static object form, settings;
    static Writer writer;

    // The write seam, counting. RefreshSeconds is the first key Save() writes,
    // so its tally is the number of saves; Last holds what each key last
    // received, which is what the disk ends up with.
    class Writer
    {
        public readonly List<string> Keys = new List<string>();
        public readonly Dictionary<string, string> Last = new Dictionary<string, string>();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WritePrivateProfileString(string section, string key, string val, string file);

        public int Saves
        {
            get { int n = 0; foreach (string k in Keys) if (k == "RefreshSeconds") n++; return n; }
        }

        public void Zero() { Keys.Clear(); }

        public bool Write(string key, string val, string file)
        {
            Keys.Add(key); Last[key] = val;
            return WritePrivateProfileString("limisaw", key, val, file);
        }
    }

    static object SField(string name) { return settingsType.GetField(name).GetValue(settings); }
    static void SSet(string name, object v) { settingsType.GetField(name).SetValue(settings, v); }
    static object FField(string name) { return formType.GetField(name, NP).GetValue(form); }
    static Rectangle Rail(string name) { return (Rectangle)FField(name); }
    static IDictionary Notified() { return (IDictionary)FField("NotifiedLow"); }

    static void Down(int x, int y) { Mouse("OnMouseDown", x, y); }
    static void Move(int x, int y) { Mouse("OnMouseMove", x, y); }
    static void Up(int x, int y) { Mouse("OnMouseUp", x, y); }

    static void Mouse(string handler, int x, int y)
    {
        formType.GetMethod(handler, NP).Invoke(form,
            new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
    }

    // A repaint is what publishes the rail rectangles OnMouseDown hit-tests
    // against, so every scenario paints first.
    static void Paint(int width)
    {
        var f = (Form)form;
        f.ClientSize = new Size(width, f.ClientSize.Height);
        formType.GetMethod("FitWindow", NP).Invoke(form, null);
        using (var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
        using (Graphics g = Graphics.FromImage(bmp))
            formType.GetMethod("OnPaint", NP).Invoke(form,
                new object[] { new PaintEventArgs(g, new Rectangle(0, 0, bmp.Width, bmp.Height)) });
    }

    static bool Wait(Func<bool> condition, int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Application.DoEvents();
            Thread.Sleep(15);
        }
        return condition();
    }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_slider_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // The constructor probes for real. An empty environment makes that
            // start-up sweep find nothing and finish at once, so no worker is
            // publishing state underneath the gestures.
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(temp, "no-codex"));
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string key in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(key, "");

            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            Assembly asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            formType = asm.GetType("Limisaw.LimisawForm");
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            Type themeType = asm.GetType("Limisaw.Theme");

            settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            object themes = themeType.GetMethod("Load", PS).Invoke(null, new object[] { root });

            using (var tray = new NotifyIcon())
            using (Form f = (Form)Activator.CreateInstance(formType,
                new object[] { temp, settings, tray, themes }))
            {
                form = f;
                object timer = formType.GetField("RefreshTimer", NP).GetValue(form);
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                Wait(() => !(bool)FField("Refreshing"), 60000);
                // Held down for the rest of the run: a sweep starting mid-drag
                // would repaint and re-publish underneath the gesture.
                formType.GetField("Refreshing", NP).SetValue(form, true);
                formType.GetMethod("ShowTab").Invoke(form, new object[] { 2 });

                writer = new Writer();
                FieldInfo hook = settingsType.GetField("WriteHook", NPI);
                hook.SetValue(settings, Delegate.CreateDelegate(hook.FieldType, writer,
                    typeof(Writer).GetMethod("Write")));

                Volume(temp, asm);
                Low();
                Preview();
                CaptureLoss();
                DeadPress();
                ((Control)form).Capture = false;
            }

            Source(root);
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    // ── the alert volume: the drag the audit measured ───────────────────────
    static void Volume(string temp, Assembly asm)
    {
        Console.WriteLine("== one volume drag, one commit ==");
        // Both alert sounds on, or the row is drawn dead and registers no rail
        // at all (which is its own rule, checked by settings_ux).
        SSet("ResetSound", true);
        SSet("NotifyLow", true);
        SSet("SoundVolume", 0);
        Paint(700);

        Rectangle rail = Rail("VolRailVolume");
        Check("the volume rail is live and wide enough to drag across",
            rail.Width >= 120, "rail " + rail.Width + "px at a 700px window");

        int mid = rail.Y + 6;
        writer.Zero();
        Down(rail.X, mid);
        Check("the press arms the gesture",
            (string)FField("VolDrag") == "volume", "VolDrag=" + FField("VolDrag"));

        var seen = new List<int>();
        int last = -1, lastX = rail.X;
        for (int px = rail.X; px <= rail.Right; px++)
        {
            Move(px, mid);
            last = (int)SField("SoundVolume"); lastX = px;
            if (!seen.Contains(last)) seen.Add(last);
        }
        // Live is the whole point of a slider: the pointer moved through the
        // range and the setting followed it, value by value.
        Check("the pointer gets live values all the way across the rail",
            seen.Count >= 100, seen.Count + " distinct values in one drag");
        Check("...and the last one is where the pointer stopped",
            last == 100, "SoundVolume=" + last);
        Check("...and the footer says so while dragging",
            ((string)FField("Note")).IndexOf(last + "%", StringComparison.Ordinal) >= 0,
            "Note=" + FField("Note"));

        Check("NOTHING is persisted while the gesture is in flight",
            writer.Saves == 0, writer.Saves + " saves after " + seen.Count + " live values");

        Up(lastX, mid);
        Check("the release commits exactly once",
            writer.Saves == 1, writer.Saves + " saves");
        Check("...and the gesture is over",
            FField("VolDrag") == null && !(bool)FField("VolDragDirty"),
            "VolDrag=" + FField("VolDrag") + ", dirty=" + FField("VolDragDirty"));
        Check("...and the committed value is the LAST pointer value",
            writer.Last.ContainsKey("SoundVolume") && writer.Last["SoundVolume"] == last.ToString(),
            "wrote SoundVolume=" + (writer.Last.ContainsKey("SoundVolume") ? writer.Last["SoundVolume"] : "(never)"));

        // End to end: a fresh settings object reading the file the drag left
        // behind must see that same value, or the one commit did not count.
        object reread = Activator.CreateInstance(settingsType, new object[] { temp });
        settingsType.GetMethod("Load").Invoke(reread, null);
        Check("...and it survives a restart",
            (int)settingsType.GetField("SoundVolume").GetValue(reread) == last,
            "reloaded SoundVolume=" + settingsType.GetField("SoundVolume").GetValue(reread));
        Check("...and the save is not reported as failed",
            !(bool)settingsType.GetField("LastSaveFailed").GetValue(settings), "");
    }

    // ── the low threshold: the same rule, plus the alert re-arm ─────────────
    static void Low()
    {
        Console.WriteLine();
        Console.WriteLine("== the low threshold re-arms once per gesture, not per move ==");
        SSet("NotifyLow", true);
        SSet("LowPct", 20);
        Paint(700);

        Rectangle rail = Rail("VolRailLow");
        Check("the low rail is live while the alert is on", rail.Width >= 120, "rail " + rail.Width + "px");

        IDictionary notified = Notified();
        notified.Clear();
        notified["codex/five_hour"] = "2026-09-05T10:00:00";
        notified["zcode/weekly"] = "2026-09-05T10:00:00";

        int mid = rail.Y + 6;
        writer.Zero();
        Down(rail.X + rail.Width / 2, mid);
        int moves = 0, lastX = rail.X + rail.Width / 2, last = (int)SField("LowPct");
        bool armedThroughout = true;
        for (int px = rail.X + rail.Width / 2; px <= rail.Right; px += 2)
        {
            Move(px, mid);
            moves++; lastX = px; last = (int)SField("LowPct");
            if (Notified().Count != 2) armedThroughout = false;
        }
        Check("the threshold follows the pointer", last == 95 && moves > 10,
            "LowPct=" + last + " after " + moves + " moves");
        Check("the already-fired alerts are NOT re-armed on every move", armedThroughout,
            "NotifiedLow=" + Notified().Count + " entries mid-drag");
        Check("and nothing is persisted mid-drag", writer.Saves == 0, writer.Saves + " saves");

        Up(lastX, mid);
        Check("the release commits once", writer.Saves == 1, writer.Saves + " saves");
        Check("...and re-arms the low alert exactly then",
            Notified().Count == 0, "NotifiedLow=" + Notified().Count + " entries");
        Check("...and persists the threshold the pointer stopped at",
            writer.Last["LowPct"] == last.ToString(), "wrote LowPct=" + writer.Last["LowPct"]);
    }

    // ── the preview quota: nothing real changes, so nothing real is re-armed ─
    static void Preview()
    {
        Console.WriteLine();
        Console.WriteLine("== the preview slider commits once and touches no alert state ==");
        SSet("PreviewPct", 65);
        Paint(700);

        Rectangle rail = Rail("VolRailPreview");
        Check("the preview rail is live", rail.Width >= 120, "rail " + rail.Width + "px");

        IDictionary notified = Notified();
        notified.Clear();
        notified["codex/five_hour"] = "2026-09-05T10:00:00";

        int mid = rail.Y + 6;
        writer.Zero();
        Down(rail.X + 10, mid);
        int lastX = rail.X + 10, last = (int)SField("PreviewPct");
        for (int px = rail.X + 10; px <= rail.Right; px += 3)
        { Move(px, mid); lastX = px; last = (int)SField("PreviewPct"); }
        Check("the pretend quota follows the pointer", last == 100, "PreviewPct=" + last);
        Check("nothing is persisted mid-drag", writer.Saves == 0, writer.Saves + " saves");

        Up(lastX, mid);
        Check("the release commits once", writer.Saves == 1, writer.Saves + " saves");
        Check("...and the preview never re-arms a real alert",
            Notified().Count == 1, "NotifiedLow=" + Notified().Count + " entries");
        Check("...and the pretend quota is what landed",
            writer.Last["PreviewPct"] == last.ToString(), "wrote PreviewPct=" + writer.Last["PreviewPct"]);
    }

    // ── capture lost without a MouseUp ─────────────────────────────────────
    static void CaptureLoss()
    {
        Console.WriteLine();
        Console.WriteLine("== a gesture cut short by lost capture still commits, once ==");
        SSet("SoundVolume", 0);
        Paint(700);

        Rectangle rail = Rail("VolRailVolume");
        int mid = rail.Y + 6;
        writer.Zero();
        Down(rail.X, mid);
        Move(rail.X + rail.Width / 2, mid);
        int held = (int)SField("SoundVolume");
        Check("the drag is in flight with an uncommitted value",
            (string)FField("VolDrag") == "volume" && (bool)FField("VolDragDirty") && writer.Saves == 0,
            "SoundVolume=" + held + ", saves=" + writer.Saves);

        // What Windows does: it releases the capture, then tells the control.
        ((Control)form).Capture = false;
        formType.GetMethod("OnMouseCaptureChanged", NP).Invoke(form, new object[] { EventArgs.Empty });
        Check("losing capture ends the gesture", FField("VolDrag") == null,
            "VolDrag=" + FField("VolDrag"));
        Check("...with exactly one commit", writer.Saves == 1, writer.Saves + " saves");
        Check("...of the value the pointer had reached",
            writer.Last["SoundVolume"] == held.ToString(), "wrote " + writer.Last["SoundVolume"]);

        formType.GetMethod("OnMouseCaptureChanged", NP).Invoke(form, new object[] { EventArgs.Empty });
        Up(rail.X + rail.Width / 2, mid);
        Check("...and a second notification, or a late MouseUp, commits nothing more",
            writer.Saves == 1, writer.Saves + " saves");
    }

    // ── a press that changed nothing ───────────────────────────────────────
    static void DeadPress()
    {
        Console.WriteLine();
        Console.WriteLine("== a press that moves nothing writes nothing ==");
        SSet("NotifyLow", true);
        Paint(700);

        Rectangle rail = Rail("VolRailLow");
        int mid = rail.Y + 6;
        // Land the pointer, commit that, then press the very same pixel again:
        // the second gesture has nothing to change.
        Down(rail.X + rail.Width / 3, mid);
        Move(rail.X + rail.Width / 3, mid);
        Up(rail.X + rail.Width / 3, mid);
        int settled = (int)SField("LowPct");

        IDictionary notified = Notified();
        notified.Clear();
        notified["codex/five_hour"] = "2026-09-05T10:00:00";
        writer.Zero();
        Down(rail.X + rail.Width / 3, mid);
        Move(rail.X + rail.Width / 3, mid);
        Up(rail.X + rail.Width / 3, mid);
        Check("a press on the knob's own position commits nothing",
            writer.Saves == 0, writer.Saves + " saves, LowPct=" + SField("LowPct"));
        Check("...and does not re-arm the low alert either",
            Notified().Count == 1 && (int)SField("LowPct") == settled,
            "NotifiedLow=" + Notified().Count + ", LowPct=" + SField("LowPct"));
    }

    // The shape of the fix, so a future edit cannot quietly put the per-move
    // save back: the setters mark the gesture dirty, and the ONE commit plus the
    // ONE re-arm live in EndVolDrag.
    static void Source(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== the per-move commit cannot come back ==");
        string ui = File.ReadAllText(Path.Combine(SourceRoot(root), "LIMISAW.cs"));

        Check("no setter saves on the way past",
            ui.IndexOf("Settings.SoundVolume = next; Settings.Save();", StringComparison.Ordinal) < 0
            && ui.IndexOf("Settings.LowPct = next; Settings.Save();", StringComparison.Ordinal) < 0
            && ui.IndexOf("Settings.PreviewPct = next; Settings.Save();", StringComparison.Ordinal) < 0, "");
        Check("all three setters mark the gesture instead",
            Count(ui, "VolDragDirty = true;") == 3, Count(ui, "VolDragDirty = true;") + " marks");
        Check("the single commit lives in EndVolDrag",
            ui.IndexOf("void EndVolDrag()", StringComparison.Ordinal) >= 0
            && ui.IndexOf("if (VolDragDirty)", StringComparison.Ordinal) >= 0, "");
        Check("the low-alert re-arm is once per gesture, not once per move",
            ui.IndexOf("if (id == \"lowpct\") NotifiedLow.Clear();", StringComparison.Ordinal) >= 0
            && Count(ui, "NotifiedLow.Clear();") == 2,
            Count(ui, "NotifiedLow.Clear();") + " re-arm sites (EndVolDrag + the ini reload)");
        Check("a lost capture is still routed into the same ending",
            ui.IndexOf("if (VolDrag != null && !Capture) EndVolDrag();", StringComparison.Ordinal) >= 0, "");
    }

    static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static string SourceRoot(string start)
    {
        string dir = start;
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) return dir;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        return start;
    }
}
