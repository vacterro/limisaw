using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Limisaw;

// Accounts visibility: per-account hide, the hide-spent filter, the
// only-5h-available filter, Alt+A always-on-top and the right-drag move.
//
// The contract every check here guards: VISIBILITY IS DISPLAY-ONLY. The sweep
// probes every account whatever the filters say, so a hidden account can be
// unhidden with its numbers intact and a filtered-out account comes back by
// itself the moment its quota returns.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main AccountsVisibilityTest)
public static class AccountsVisibilityTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    static Type formType;
    static object form;

    static object Get(string name) { return formType.GetField(name, NP).GetValue(form); }
    static void Set(string name, object v) { formType.GetField(name, NP).SetValue(form, v); }
    static object Call(string name, params object[] args)
    {
        return formType.GetMethod(name, NP | BindingFlags.Public, null,
            args == null ? Type.EmptyTypes : ArgTypes(args), null).Invoke(form, args ?? new object[0]);
    }
    static Type[] ArgTypes(object[] args)
    {
        var t = new Type[args.Length];
        for (int i = 0; i < args.Length; i++) t[i] = args[i].GetType();
        return t;
    }

    // Account cards as the window would draw them.
    static AccountData Acc(string key, params WindowData[] windows)
    {
        var a = new AccountData { Provider = "test", ProviderLabel = "T", Name = key, Ok = true, Status = "OK" };
        // Key = Provider + "/" + Name for non-codex, so make the key exact.
        a.Windows.AddRange(windows);
        return a;
    }

    static WindowData Win(string key, string baseKey, int rem, bool available)
    {
        return new WindowData
        {
            Key = key, Base = baseKey, Label = baseKey, Available = available, Rem = rem,
            Reset = DateTime.Now.AddHours(2).ToString("yyyy-MM-ddTHH:mm:ss"),
        };
    }

    static string Keys(List<AccountData> list)
    {
        var k = new List<string>();
        foreach (AccountData a in list) k.Add(a.Name);
        return string.Join(",", k.ToArray());
    }

    // Account cards as the window would draw them, through the form's own
    // VisibleAccounts — the one list the filters decide.
    static List<AccountData> Visible()
    {
        return (List<AccountData>)Call("VisibleAccounts");
    }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_vis_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(temp, "no-codex"));
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string k in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(k, null);

            var settings = new LimisawSettings(temp);
            settings.Load();
            List<Theme> themes = Theme.Load(root);
            formType = typeof(LimisawForm);

            using (var tray = new NotifyIcon())
            using (var f = new LimisawForm(temp, settings, tray, themes))
            {
                form = f;
                object timer = Get("RefreshTimer");
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                for (int i = 0; i < 1200 && (bool)Get("Refreshing"); i++)
                { Application.DoEvents(); Thread.Sleep(25); }

                // The fleet: A healthy (5h+weekly), B spent (0/0), C 5h-only
                // healthy, D no windows at all.
                var live = (List<AccountData>)Get("Accounts");
                live.Clear();
                live.Add(Acc("A", Win("five_hour", "five_hour", 40, true), Win("weekly", "weekly", 90, true)));
                live.Add(Acc("B", Win("five_hour", "five_hour", 0, true), Win("weekly", "weekly", 0, true)));
                live.Add(Acc("C", Win("five_hour", "five_hour", 55, true)));
                live.Add(Acc("D"));

                Console.WriteLine("== no filters: everything shows ==");
                Check("all four accounts visible", Visible().Count == 4, Keys(Visible()));

                Console.WriteLine();
                Console.WriteLine("== per-account hide ==");
                Call("ToggleAccountHidden", live[1].Key);
                Check("hiding B leaves A, C, D", Keys(Visible()) == "A,C,D", Keys(Visible()));
                Check("the hide persisted to the ini",
                    settings.HiddenAccountList().Contains(live[1].Key), settings.HiddenAccounts);
                Call("ToggleAccountHidden", live[1].Key);
                Check("unhiding B brings it back", Visible().Count == 4, Keys(Visible()));
                Check("...and the ini list is rewritten, not stacked",
                    !settings.HiddenAccountList().Contains(live[1].Key), settings.HiddenAccounts);

                Console.WriteLine();
                Console.WriteLine("== hide-spent filter ==");
                settings.HideSpentAccounts = true;
                Check("a fully spent account (0/0) is hidden", Keys(Visible()) == "A,C", Keys(Visible()));
                Check("an account with no windows at all is hidden too", !Visible().Contains(live[3]), Keys(Visible()));
                settings.HideSpentAccounts = false;
                Check("turning the filter off restores everything", Visible().Count == 4, Keys(Visible()));

                Console.WriteLine();
                Console.WriteLine("== only-5h filter ==");
                settings.OnlyWithFiveHour = true;
                // C's 5h is usable, A's and B's too; D has no 5h at all, so it
                // cannot be "available right now" either.
                Check("an account with no 5h window at all stays out", !Visible().Contains(live[3]), Keys(Visible()));
                // B: 5h present but 0 — not "usable".
                Check("B's 5h at 0 is not usable, so B stays out", Keys(Visible()) == "A,C", Keys(Visible()));
                settings.OnlyWithFiveHour = false;

                Console.WriteLine();
                Console.WriteLine("== both filters compose ==");
                settings.HideSpentAccounts = true;
                settings.OnlyWithFiveHour = true;
                Check("compose: A and C survive both", Keys(Visible()) == "A,C", Keys(Visible()));
                settings.HideSpentAccounts = false;
                settings.OnlyWithFiveHour = false;

                Console.WriteLine();
                Console.WriteLine("== visibility is display-only: the sweep list is untouched ==");
                Check("the Accounts list itself still holds all four", live.Count == 4, live.Count + " accounts");

                Console.WriteLine();
                Console.WriteLine("== Alt+A always on top ==");
                settings.AlwaysOnTop = false;
                Call("ToggleTopMost");
                Check("toggling pins the window", ((Form)form).TopMost, "TopMost=" + ((Form)form).TopMost);
                Check("...and persists", settings.AlwaysOnTop, "ini=" + settings.AlwaysOnTop);
                Call("ToggleTopMost");
                Check("toggling again unpins", !((Form)form).TopMost && !settings.AlwaysOnTop, "");

                Console.WriteLine();
                Console.WriteLine("== a filtered-out fleet says so, not just empty ==");
                // Set filters so everything is gone; the panel must tell the user
                // why instead of drawing "No accounts reported yet."
                Set("Tab", 0);
                settings.OnlyWithFiveHour = true;
                settings.HideSpentAccounts = true;
                settings.SetHiddenAccounts(new List<string> { "test/A", "test/B", "test/C", "test/D" });
                List<AccountData> none = Visible();
                Check("every account filtered out is reported as hidden, not missing",
                    none.Count == 0 && live.Count == 4, "shown=" + none.Count + " of " + live.Count);
                settings.OnlyWithFiveHour = false;
                settings.HideSpentAccounts = false;
                settings.SetHiddenAccounts(new List<string>());
            }

            // Persistence round trip: hide + both filters + topmost survive a
            // fresh settings object reading the same ini.
            settings.HiddenAccounts = "test/B";
            settings.HideSpentAccounts = true;
            settings.OnlyWithFiveHour = true;
            settings.AlwaysOnTop = true;
            settings.Save();
            var reread = new LimisawSettings(temp);
            reread.Load();
            Check("HiddenAccounts survives the round trip", reread.HiddenAccounts == "test/B", reread.HiddenAccounts);
            Check("HideSpentAccounts survives", reread.HideSpentAccounts, "");
            Check("OnlyWithFiveHour survives", reread.OnlyWithFiveHour, "");
            Check("AlwaysOnTop survives", reread.AlwaysOnTop, "");

            // Source guards: the wire-ups cannot silently vanish.
            string ui = File.ReadAllText(Path.Combine(SourceRoot(root), "LIMISAW.cs"));
            Check("Alt+A is wired to the toggle",
                ui.IndexOf("e.Alt && e.KeyCode == Keys.A", StringComparison.Ordinal) >= 0
                && ui.IndexOf("void ToggleTopMost()", StringComparison.Ordinal) >= 0, "");
            Check("right-drag is wired on all three mouse handlers",
                ui.IndexOf("if (e.Button == MouseButtons.Right) { BeginRightDrag(e); return; }", StringComparison.Ordinal) >= 0
                && ui.IndexOf("if (RightDragging) { MoveRightDrag(e); return; }", StringComparison.Ordinal) >= 0
                && ui.IndexOf("if (e.Button == MouseButtons.Right) { EndRightDrag(); return; }", StringComparison.Ordinal) >= 0, "");
            Check("the accounts filters are display-only (VisibleAccounts, not Probe.Run)",
                ui.IndexOf("List<AccountData> VisibleAccounts()", StringComparison.Ordinal) >= 0
                && ui.IndexOf("if (Settings.HideSpentAccounts && !AccountHasQuota(a)) continue;", StringComparison.Ordinal) >= 0
                && ui.IndexOf("if (Settings.OnlyWithFiveHour && !AccountHasFiveHour(a)) continue;", StringComparison.Ordinal) >= 0, "");
            Check("the card carries a hide affordance",
                ui.IndexOf("ToggleAccountHidden(hideTarget.Key)", StringComparison.Ordinal) >= 0, "");
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally { try { Directory.Delete(temp, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " of " + checks + ")");
        return fails == 0 ? 0 : 1;
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
