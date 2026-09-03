using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Drives the REAL LIMISAW window through every tab, theme and account shape,
// then asserts its layout is sane. This is the harness for a class of defect
// that only shows up on screen: a label cropped to "Showing: Le", a limit bar
// sliding under a button, two buttons sharing pixels.
//
// It loads LIMISAW.exe by reflection, injects synthetic accounts (so the check
// never depends on which vendors are logged in), paints onto a bitmap and
// inspects the hit-test rectangles the paint pass produced:
//
//   * no two clickable rectangles overlap  (a click would be ambiguous);
//   * every rectangle stays inside the window and clear of the footer;
//   * every button is big enough for its own label at the smallest font it is
//     allowed to use, so DrawButton never has to truncate;
//   * the window's measured height fits the content it just painted.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:layout_fit.exe -r:System.dll -r:System.Drawing.dll ^
//     -r:System.Windows.Forms.dll tests\layout_fit.cs
//   layout_fit.exe            (exit 0 = all PASS)
public static class LayoutFit
{
    static int fails = 0, checks = 0;

    static void Fail(string name, string detail)
    {
        fails++;
        Console.WriteLine("FAIL  " + name + "  -> " + detail);
    }

    static void Pass(string name, string detail)
    {
        Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
    }

    static Assembly Load()
    {
        string root = Directory.GetCurrentDirectory();
        string exe = Path.Combine(root, "LIMISAW.exe");
        if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
        return Assembly.LoadFrom(Path.GetFullPath(exe));
    }

    static object NewWindow(Assembly asm, string tempDir, out Type formType, out NotifyIcon tray)
    {
        Type settingsType = asm.GetType("Limisaw.LimisawSettings");
        Type themeType = asm.GetType("Limisaw.Theme");
        formType = asm.GetType("Limisaw.LimisawForm");

        object settings = Activator.CreateInstance(settingsType, new object[] { tempDir });
        settingsType.GetMethod("Load").Invoke(settings, null);

        // Themes come from the repo so a broken palette file fails here too.
        object themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { Directory.GetCurrentDirectory() });

        tray = new NotifyIcon();
        return Activator.CreateInstance(formType, new object[] { tempDir, settings, tray, themes });
    }

    // ── synthetic fleet: 3 Codex accounts + Claude + Antigravity's two pools ──
    static object MakeWindow(Assembly asm, string key, string label, string group,
                             int rem, string reset, string gated, int minutes)
    {
        Type t = asm.GetType("Limisaw.WindowData");
        object w = Activator.CreateInstance(t);
        t.GetField("Key").SetValue(w, key);
        t.GetField("Base").SetValue(w, key.Split('@')[0]);
        t.GetField("Label").SetValue(w, label);
        t.GetField("Group").SetValue(w, group);
        t.GetField("GroupLabel").SetValue(w, group);
        t.GetField("Available").SetValue(w, true);
        t.GetField("Rem").SetValue(w, rem);
        t.GetField("Reset").SetValue(w, reset);
        t.GetField("GatedBy").SetValue(w, gated);
        t.GetField("DurationMinutes").SetValue(w, minutes);
        return w;
    }

    static object MakeAccount(Assembly asm, string provider, string providerLabel,
                              string name, string plan, string error, object[] windows)
    {
        Type t = asm.GetType("Limisaw.AccountData");
        object a = Activator.CreateInstance(t);
        t.GetField("Provider").SetValue(a, provider);
        t.GetField("ProviderLabel").SetValue(a, providerLabel);
        t.GetField("Name").SetValue(a, name);
        t.GetField("Status").SetValue(a, error == null ? "OK" : "ERROR");
        t.GetField("Plan").SetValue(a, plan);
        t.GetField("Error").SetValue(a, error);
        t.GetField("Ok").SetValue(a, error == null);
        IList list = (IList)t.GetField("Windows").GetValue(a);
        foreach (object w in windows) list.Add(w);
        return a;
    }

    static object MakeCli(Assembly asm, string key, string label, bool installed,
                          string path, string command, string source, string target)
    {
        Type t = asm.GetType("Limisaw.CliInfo");
        object c = Activator.CreateInstance(t);
        t.GetField("Key").SetValue(c, key);
        t.GetField("Label").SetValue(c, label);
        t.GetField("Installed").SetValue(c, installed);
        t.GetField("Path").SetValue(c, path);
        t.GetField("Command").SetValue(c, command);
        t.GetField("PowerShell").SetValue(c, command);
        t.GetField("Source").SetValue(c, source);
        t.GetField("Target").SetValue(c, target);
        return c;
    }

    static void Inject(Assembly asm, Type formType, object form, bool withAccounts)
    {
        IList accounts = (IList)formType.GetField("Accounts", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
        IList clis = (IList)formType.GetField("Clis", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
        accounts.Clear(); clis.Clear();
        if (!withAccounts) return;

        string soon = DateTime.Now.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ss");
        string later = DateTime.Now.AddDays(3).ToString("yyyy-MM-ddTHH:mm:ss");
        accounts.Add(MakeAccount(asm, "codex", "Codex", "Codex", "plus", null, new object[] {
            MakeWindow(asm, "five_hour", "5h", "", 0, soon, "weekly", 300),
            MakeWindow(asm, "weekly", "week", "", 0, later, null, 10080) }));
        accounts.Add(MakeAccount(asm, "codex", "Codex", "Account2", "plus", null, new object[] {
            MakeWindow(asm, "five_hour", "5h", "", 63, soon, null, 300),
            MakeWindow(asm, "weekly", "week", "", 91, later, null, 10080) }));
        accounts.Add(MakeAccount(asm, "codex", "Codex", "Account3Free", "free", null, new object[] {
            MakeWindow(asm, "monthly", "month", "", 100, later, null, 43200) }));
        accounts.Add(MakeAccount(asm, "claude", "Claude Code", "Claude", null, null, new object[] {
            MakeWindow(asm, "five_hour", "5h", "", 7, soon, null, 300),
            MakeWindow(asm, "weekly", "week", "", 33, later, null, 10080) }));
        // The worst realistic labels: two pools, long pool names.
        accounts.Add(MakeAccount(asm, "antigravity", "Antigravity", "Antigravity", null, null, new object[] {
            MakeWindow(asm, "five_hour@gemini_models", "5h", "Gemini Models", 10, soon, null, 300),
            MakeWindow(asm, "weekly@gemini_models", "week", "Gemini Models", 68, later, null, 10080),
            MakeWindow(asm, "weekly@claude_and_gpt", "week", "Claude and GPT models", 0, later, null, 10080) }));
        // An account that only has an error to report.
        accounts.Add(MakeAccount(asm, "claude", "Claude Code", "Claude2", null,
            "AUTH_REQUIRED: not logged in, run `claude login` in a terminal first", new object[0]));

        clis.Add(MakeCli(asm, "claude", "Claude Code CLI", true,
            @"C:\Users\someone\.local\bin\claude.exe", "irm https://claude.ai/install.ps1 | iex",
            "claude.ai (Anthropic)", @"%USERPROFILE%\.local\bin\claude.exe"));
        clis.Add(MakeCli(asm, "antigravity", "Antigravity CLI", false, "",
            "irm https://antigravity.google/cli/install.ps1 | iex",
            "antigravity.google (Google)", @"%LOCALAPPDATA%\agy\bin\agy.exe"));
        clis.Add(MakeCli(asm, "codex", "Codex CLI", true, @"c:\nodejs\codex.CMD",
            "irm https://chatgpt.com/codex/install.ps1 | iex",
            "chatgpt.com/codex (OpenAI)", "on PATH (installer-chosen directory)"));
    }

    static List<Rectangle> Grab(Type formType, object form, string field)
    {
        var found = (IList)formType.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
        var list = new List<Rectangle>();
        foreach (object r in found) list.Add((Rectangle)r);
        return list;
    }

    static void Paint(Type formType, object form)
    {
        formType.GetMethod("FitWindow", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
        Size size = ((Form)form).ClientSize;
        using (var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            var args = new PaintEventArgs(g, new Rectangle(Point.Empty, size));
            formType.GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(form, new object[] { args });
        }
    }

    static void CheckLayout(string scenario, Type formType, object form)
    {
        checks++;
        Paint(formType, form);
        List<Rectangle> buttons = Grab(formType, form, "Buttons");
        // Measured content: gauges and width-bounded text. A button landing on
        // one of these is the "limit bar under the button" defect, which a
        // button-vs-button check cannot see.
        List<Rectangle> marks = Grab(formType, form, "Marks");
        Form f = (Form)form;
        int w = f.ClientSize.Width, h = f.ClientSize.Height;
        var problems = new List<string>();

        for (int i = 0; i < buttons.Count; i++)
        {
            Rectangle a = buttons[i];
            if (a.Width <= 0 || a.Height <= 0) { problems.Add("empty rect #" + i); continue; }
            if (a.Left < 0 || a.Top < 0 || a.Right > w || a.Bottom > h)
                problems.Add("rect #" + i + " " + a + " escapes the " + w + "x" + h + " client area");
            for (int j = i + 1; j < buttons.Count; j++)
            {
                Rectangle b = buttons[j];
                Rectangle hit = Rectangle.Intersect(a, b);
                if (hit.Width > 0 && hit.Height > 0)
                    problems.Add("rect #" + i + " " + a + " overlaps #" + j + " " + b);
            }
            foreach (Rectangle mark in marks)
            {
                Rectangle hit = Rectangle.Intersect(a, mark);
                if (hit.Width > 1 && hit.Height > 1)
                    problems.Add("button " + a + " covers content " + mark);
            }
        }
        if (buttons.Count == 0) problems.Add("no clickable rectangles at all");
        foreach (Rectangle mark in marks)
            if (mark.Right > w - 2)
                problems.Add("content " + mark + " runs past the right edge (" + w + ")");
        // "Showing: Le" was a label the button could not hold. Any cropped
        // label is a layout bug: the row must measure its own text.
        var cropped = (IList)formType.GetField("Cropped", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
        foreach (object label in cropped) problems.Add("label cropped: \"" + label + "\"");

        if (problems.Count == 0) Pass(scenario, buttons.Count + " controls, " + marks.Count + " content marks, " + w + "x" + h);
        else Fail(scenario, problems.Count + " problem(s): " + string.Join("; ", problems.ToArray()));
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_layout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assembly asm = Load();
            Type formType; NotifyIcon tray;
            object form = NewWindow(asm, temp, out formType, out tray);
            using ((Form)form)
            using (tray)
            {
                Type settingsType = asm.GetType("Limisaw.LimisawSettings");
                object settings = formType.GetField("Settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                MethodInfo showTab = formType.GetMethod("ShowTab");
                FieldInfo tabField = formType.GetField("Tab", BindingFlags.NonPublic | BindingFlags.Instance);

                string[] tabNames = { "Accounts", "Tray", "Settings", "CLIs" };
                foreach (bool populated in new[] { true, false })
                {
                    Inject(asm, formType, form, populated);
                    for (int tab = 0; tab < 4; tab++)
                    {
                        tabField.SetValue(form, tab);
                        // Both toggle states: the label is what used to be cropped.
                        foreach (bool used in new[] { false, true })
                        {
                            settingsType.GetField("ShowUsed").SetValue(settings, used);
                            CheckLayout(tabNames[tab] + " tab, " + (populated ? "6 accounts" : "empty")
                                + ", " + (used ? "Used" : "Left"), formType, form);
                        }
                    }
                }

                // A theme swap changes font metrics, which is exactly what broke
                // the fixed-width labels: every theme must still lay out clean.
                Inject(asm, formType, form, true);
                var themes = (IList)formType.GetField("Themes", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                Type themeType = asm.GetType("Limisaw.Theme");
                MethodInfo applyTheme = formType.GetMethod("ApplyTheme");
                foreach (object t in themes)
                {
                    string slug = (string)themeType.GetField("Slug").GetValue(t);
                    applyTheme.Invoke(form, new object[] { slug });
                    tabField.SetValue(form, 2);
                    CheckLayout("Settings tab, theme " + slug, formType, form);
                    tabField.SetValue(form, 1);
                    CheckLayout("Tray tab, theme " + slug, formType, form);
                }

                // The cap must not change the geometry of the rows it dims.
                foreach (int max in new[] { 1, 4, 9 })
                {
                    settingsType.GetField("TrayMax").SetValue(settings, max);
                    tabField.SetValue(form, 1);
                    CheckLayout("Tray tab, cap " + max, formType, form);
                }
            }
        }
        catch (Exception ex)
        {
            Fail("harness", ex.GetType().Name + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " layouts checked");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
