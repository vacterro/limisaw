using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// The low-quota alert (T-103) has three rules that are easy to get wrong and
// impossible to see from the outside until they annoy somebody:
//
//   * the FIRST sweep must not alert. Everything the user already has is news
//     to nobody, and a launch that pops five balloons is a reason to switch the
//     feature off again;
//   * a window alerts ONCE per reset cycle, not once per refresh;
//   * it re-arms when that window's reset stamp changes (new cycle) or when the
//     number climbs back above the threshold plus the hysteresis band.
//
// The balloon text on the (never shown) NotifyIcon is what the assertions read:
// it is the observable half of NotifyLowAlert, and a hidden NotifyIcon discards
// ShowBalloonTip instead of pestering the desktop. Sound is silenced with
// volume 0, which SoundCue.Play answers by playing nothing.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:notify_alerts.exe -r:System.dll -r:System.Drawing.dll ^
//     -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll tests\notify_alerts.cs
//   notify_alerts.exe            (exit 0 = all PASS)
public static class NotifyAlertsTest
{
    static int fails = 0, checks = 0;
    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Assembly Load()
    {
        string root = Directory.GetCurrentDirectory();
        string exe = Path.Combine(root, "LIMISAW.exe");
        if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
        return Assembly.LoadFrom(Path.GetFullPath(exe));
    }

    static readonly BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    static object MakeWindow(Assembly asm, string key, string label, int rem, string reset)
    {
        Type t = asm.GetType("Limisaw.WindowData");
        object w = Activator.CreateInstance(t);
        t.GetField("Key").SetValue(w, key);
        t.GetField("Base").SetValue(w, key.Split('@')[0]);
        t.GetField("Label").SetValue(w, label);
        t.GetField("Group").SetValue(w, "");
        t.GetField("GroupLabel").SetValue(w, "");
        t.GetField("Available").SetValue(w, true);
        t.GetField("Rem").SetValue(w, rem);
        t.GetField("Reset").SetValue(w, reset);
        t.GetField("GatedBy").SetValue(w, null);
        t.GetField("AssumedFull").SetValue(w, false);
        t.GetField("DurationMinutes").SetValue(w, 300);
        return w;
    }

    static object MakeAccount(Assembly asm, string name, params object[] windows)
    {
        Type t = asm.GetType("Limisaw.AccountData");
        object a = Activator.CreateInstance(t);
        t.GetField("Provider").SetValue(a, "codex");
        t.GetField("ProviderLabel").SetValue(a, "Codex");
        t.GetField("Name").SetValue(a, name);
        t.GetField("Status").SetValue(a, "OK");
        t.GetField("Ok").SetValue(a, true);
        t.GetField("Plan").SetValue(a, "plus");
        IList list = (IList)t.GetField("Windows").GetValue(a);
        foreach (object w in windows) list.Add(w);
        return a;
    }


    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_alerts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assembly asm = Load();
            Type settingsType = asm.GetType("Limisaw.LimisawSettings");
            Type themeType = asm.GetType("Limisaw.Theme");
            Type formType = asm.GetType("Limisaw.LimisawForm");

            object settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            // Silent: volume 0 makes SoundCue.Play a no-op, and the balloon goes
            // to a NotifyIcon that was never made visible.
            settingsType.GetField("SoundVolume").SetValue(settings, 0);
            settingsType.GetField("NotifyLow").SetValue(settings, true);
            settingsType.GetField("LowPct").SetValue(settings, 20);
            object themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { Directory.GetCurrentDirectory() });

            using (NotifyIcon tray = new NotifyIcon())
            {
                object form = Activator.CreateInstance(formType,
                    new object[] { temp, settings, tray, themes });
                using ((IDisposable)form)
                {
                    // Block the refresh worker: a real sweep would replace the
                    // injected fleet mid-test and make every assertion a race.
                    formType.GetField("Refreshing", NP).SetValue(form, true);

                    IList accounts = (IList)formType.GetField("Accounts", NP).GetValue(form);
                    MethodInfo sweep = formType.GetMethod("RearmLowAlerts", NP);
                    MethodInfo detect = formType.GetMethod("DetectLow", NP);
                    IDictionary fired = (IDictionary)formType.GetField("NotifiedLow", NP).GetValue(form);
                    // Local functions are C# 7 and this builds with the .NET 4
                    // compiler, so the two helpers are delegates.
                    Func<string> Tip = () => tray.BalloonTipText ?? "";
                    Action Sweep = () => { sweep.Invoke(form, null); detect.Invoke(form, null); };

                    string soon = DateTime.Now.AddHours(2).ToString("yyyy-MM-ddTHH:mm:ss");
                    string later = DateTime.Now.AddDays(2).ToString("yyyy-MM-ddTHH:mm:ss");
                    accounts.Add(MakeAccount(asm, "one",
                        MakeWindow(asm, "five_hour", "5h", 12, soon),
                        MakeWindow(asm, "weekly", "week", 90, later)));
                    accounts.Add(MakeAccount(asm, "two",
                        MakeWindow(asm, "five_hour", "5h", 3, soon)));

                    // 1) first sweep: baseline only
                    tray.BalloonTipText = "";
                    Sweep();
                    Check("the first sweep records the already-low windows without alerting",
                        Tip().Length == 0 && fired.Count == 2,
                        "tip=\"" + Tip() + "\", tracked=" + fired.Count);

                    // 2) same numbers, next sweep: nothing new
                    Sweep();
                    Sweep();
                    Check("a window that is still low does not alert again each refresh",
                        Tip().Length == 0 && fired.Count == 2,
                        "tip=\"" + Tip() + "\", tracked=" + fired.Count);

                    // 3) a fresh drop below the threshold alerts once
                    accounts.Clear();
                    accounts.Add(MakeAccount(asm, "one",
                        MakeWindow(asm, "five_hour", "5h", 12, soon),
                        MakeWindow(asm, "weekly", "week", 18, later)));
                    accounts.Add(MakeAccount(asm, "two",
                        MakeWindow(asm, "five_hour", "5h", 3, soon)));
                    tray.BalloonTipText = "";
                    Sweep();
                    string first = Tip();
                    Check("crossing the threshold alerts once, with the number and the reset",
                        first.Contains("only 18% left") && first.Contains("week"),
                        "tip=\"" + first + "\"");
                    Sweep();
                    Check("that window stays quiet until its cycle rolls over",
                        Tip() == first, "tip=\"" + Tip() + "\"");

                    // 4) the same window, new reset stamp = new cycle -> may alert again
                    accounts.Clear();
                    accounts.Add(MakeAccount(asm, "one",
                        MakeWindow(asm, "five_hour", "5h", 12, soon),
                        MakeWindow(asm, "weekly", "week", 9, DateTime.Now.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ss"))));
                    accounts.Add(MakeAccount(asm, "two",
                        MakeWindow(asm, "five_hour", "5h", 3, soon)));
                    tray.BalloonTipText = "";
                    Sweep();
                    Check("a reset window in the NEW cycle alerts again",
                        Tip().Contains("only 9% left"), "tip=\"" + Tip() + "\"");

                    // 5) climbing back above threshold + band drops the record, so
                    //    falling low again is a real new alert
                    accounts.Clear();
                    accounts.Add(MakeAccount(asm, "one",
                        MakeWindow(asm, "five_hour", "5h", 80, soon),
                        MakeWindow(asm, "weekly", "week", 90, later)));
                    accounts.Add(MakeAccount(asm, "two",
                        MakeWindow(asm, "five_hour", "5h", 75, soon)));
                    tray.BalloonTipText = "";
                    Sweep();
                    Check("recovered windows are forgotten so the next drop can alert",
                        fired.Count == 0, "tracked=" + fired.Count);
                    accounts.Clear();
                    accounts.Add(MakeAccount(asm, "one",
                        MakeWindow(asm, "five_hour", "5h", 5, soon),
                        MakeWindow(asm, "weekly", "week", 90, later)));
                    accounts.Add(MakeAccount(asm, "two",
                        MakeWindow(asm, "five_hour", "5h", 3, soon)));
                    tray.BalloonTipText = "";
                    Sweep();
                    // Every window under the threshold alerts, so the tip holds
                    // the LAST one of this sweep - what the tracked set proves is
                    // that the recovered window fired again, not which text won.
                    Check("a window that dropped low again alerts again",
                        fired.Contains("codex/one_five_hour") && Tip().Length > 0,
                        "tip=\"" + Tip() + "\", tracked=" + fired.Count);

                    // 5b) REPLAY: the same low window three sweeps in a row fires
                    // exactly once. This is the "not every 3 minutes" rule: the
                    // stamp key must hold across identical sweeps.
                    accounts.Clear();
                    accounts.Add(MakeAccount(asm, "one",
                        MakeWindow(asm, "five_hour", "5h", 4, soon)));
                    tray.BalloonTipText = "";
                    Sweep();
                    string replayFirst = Tip();
                    Sweep(); Sweep(); Sweep();
                    Check("three identical sweeps fire exactly once, no replay",
                        Tip() == replayFirst && fired.Count == 1,
                        "tip=\"" + Tip() + "\", tracked=" + fired.Count);

                    // 6) the switch is honoured. The tracked set is cleared first,
                    //    so the only thing that can keep this sweep silent is the
                    //    switch itself and not a leftover record.
                    settingsType.GetField("NotifyLow").SetValue(settings, false);
                    fired.Clear();
                    accounts.Clear();
                    accounts.Add(MakeAccount(asm, "one",
                        MakeWindow(asm, "five_hour", "5h", 1, soon)));
                    tray.BalloonTipText = "";
                    Sweep();
                    Check("the low alert switch switches it off",
                        Tip().Length == 0 && fired.Count == 0,
                        "tip=\"" + Tip() + "\", tracked=" + fired.Count);

                    Console.WriteLine("---");
                    Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
                    return fails == 0 ? 0 : 1;
                }
            }
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

