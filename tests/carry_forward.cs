using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// A vendor CLI that fails ONE sweep is not a vendor without quota. The Codex
// app-server pays a cold start (measured at 14-19s for the first account of a
// session), `agy -p "/usage"` occasionally takes 15s, and any of them can lose
// a race with a laptop waking up. Before this, one such sweep replaced the
// account's card with a bare "ERROR" — which looked permanent, dropped the
// numbers the app already had, and made the tray jump UP (reporting more quota
// than the user actually has).
//
// This harness drives LIMISAW's own carry-forward against the real failure
// shapes and pins:
//
//   * a failed account keeps its last good windows, flagged Carried;
//   * the flag names WHEN those numbers were fresh;
//   * a still-failing account keeps the ORIGINAL timestamp, not a rolling one;
//   * a recovered account drops the flag and takes the new numbers;
//   * an account that has never succeeded stays a real error (nothing to carry);
//   * carried readings still count as available, so the tray never rounds up;
//   * a carried account raises no reset balloon (its windows are the old ones).
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:carry_forward.exe -r:System.dll -r:System.Drawing.dll ^
//     -r:System.Windows.Forms.dll tests\carry_forward.cs
//   carry_forward.exe          (exit 0 = all PASS)
public static class CarryForward
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Assembly asm;
    static Type accType, winType, formType;

    static object Window(string key, string label, int rem, bool available, string reset)
    {
        object w = Activator.CreateInstance(winType);
        winType.GetField("Key").SetValue(w, key);
        winType.GetField("Base").SetValue(w, key);
        winType.GetField("Label").SetValue(w, label);
        winType.GetField("Available").SetValue(w, available);
        winType.GetField("Rem").SetValue(w, rem);
        winType.GetField("Reset").SetValue(w, reset);
        winType.GetField("DurationMinutes").SetValue(w, key == "five_hour" ? 300 : 10080);
        return w;
    }

    static object Account(string provider, string name, bool ok, string error, object[] windows)
    {
        object a = Activator.CreateInstance(accType);
        accType.GetField("Provider").SetValue(a, provider);
        accType.GetField("ProviderLabel").SetValue(a, provider == "codex" ? "Codex" : "Claude Code");
        accType.GetField("Name").SetValue(a, name);
        accType.GetField("Status").SetValue(a, ok ? "OK" : "ERROR");
        accType.GetField("Ok").SetValue(a, ok);
        accType.GetField("Error").SetValue(a, error);
        IList list = (IList)accType.GetField("Windows").GetValue(a);
        foreach (object w in windows) list.Add(w);
        return a;
    }

    static object Carry(object form, IList fresh, IList previous)
    {
        MethodInfo m = formType.GetMethod("CarryForward", BindingFlags.NonPublic | BindingFlags.Instance);
        return m.Invoke(form, new object[] { fresh, previous });
    }

    static IList Accounts(params object[] items)
    {
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(accType));
        foreach (object o in items) list.Add(o);
        return list;
    }

    static bool Flag(object a, string field) { return (bool)accType.GetField(field).GetValue(a); }
    static string Text(object a, string field) { return (string)accType.GetField(field).GetValue(a); }
    static int WindowCount(object a) { return ((IList)accType.GetField("Windows").GetValue(a)).Count; }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_carry_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string root = Directory.GetCurrentDirectory();
            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            accType = asm.GetType("Limisaw.AccountData");
            winType = asm.GetType("Limisaw.WindowData");
            formType = asm.GetType("Limisaw.LimisawForm");
            Type settingsType = asm.GetType("Limisaw.LimisawSettings");
            Type themeType = asm.GetType("Limisaw.Theme");

            object settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            object themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { root });

            using (var tray = new NotifyIcon())
            using (Form form = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            {
                FieldInfo lastFetch = formType.GetField("LastFetch", BindingFlags.NonPublic | BindingFlags.Instance);
                lastFetch.SetValue(form, "12:00:00");

                // A good sweep, then the same account failing.
                IList good = Accounts(
                    Account("codex", "Codex", true, null, new object[] {
                        Window("five_hour", "5h", 42, true, "2026-09-03T18:00:00"),
                        Window("weekly", "week", 77, true, "2026-09-07T05:00:00") }),
                    Account("claude", "Claude", true, null, new object[] {
                        Window("five_hour", "5h", 7, true, "2026-09-03T18:00:00") }));
                IList failed = Accounts(
                    Account("codex", "Codex", false,
                        "c:\\users\\x\\.codex: timed out waiting for account/rateLimits/read", new object[0]),
                    Account("claude", "Claude", true, null, new object[] {
                        Window("five_hour", "5h", 6, true, "2026-09-03T18:00:00") }));

                Carry(form, failed, good);
                object codex = failed[0];
                Check("a failed account keeps its last good windows",
                    WindowCount(codex) == 2 && Flag(codex, "Carried"),
                    WindowCount(codex) + " windows, Carried=" + Flag(codex, "Carried"));
                Check("the card can say when those numbers were fresh",
                    Text(codex, "CarriedAt") == "12:00:00", "CarriedAt=" + Text(codex, "CarriedAt"));
                Check("a healthy account in the same sweep is untouched",
                    !Flag(failed[1], "Carried") && WindowCount(failed[1]) == 1,
                    "Carried=" + Flag(failed[1], "Carried"));

                // Still failing on the next sweep: the timestamp must not roll
                // forward, or "last good" would slowly become a lie.
                lastFetch.SetValue(form, "12:05:00");
                IList failedAgain = Accounts(
                    Account("codex", "Codex", false, "timed out", new object[0]));
                Carry(form, failedAgain, failed);
                Check("a still-failing account keeps the ORIGINAL timestamp",
                    Text(failedAgain[0], "CarriedAt") == "12:00:00",
                    "CarriedAt=" + Text(failedAgain[0], "CarriedAt"));
                Check("and it still has the numbers", WindowCount(failedAgain[0]) == 2,
                    WindowCount(failedAgain[0]) + " windows");

                // Recovery drops the flag and takes the fresh numbers.
                IList recovered = Accounts(
                    Account("codex", "Codex", true, null, new object[] {
                        Window("five_hour", "5h", 99, true, "2026-09-03T23:00:00") }));
                Carry(form, recovered, failedAgain);
                Check("a recovered account drops the carried flag",
                    !Flag(recovered[0], "Carried") && WindowCount(recovered[0]) == 1,
                    "Carried=" + Flag(recovered[0], "Carried") + ", " + WindowCount(recovered[0]) + " window");

                // Nothing to carry: a first-run failure must stay a real error.
                IList firstRun = Accounts(
                    Account("codex", "Ghost", false, "CODEX_HOME missing", new object[0]));
                Carry(form, firstRun, Accounts());
                Check("an account that never succeeded stays a real error",
                    !Flag(firstRun[0], "Carried") && WindowCount(firstRun[0]) == 0,
                    "Carried=" + Flag(firstRun[0], "Carried"));

                // An account reporting an OK-but-blank snapshot is just as blank
                // as a failed one, so it is carried too; and an all-unavailable
                // window list counts as blank.
                IList emptyOk = Accounts(Account("codex", "Codex", true, null, new object[0]));
                Carry(form, emptyOk, good);
                Check("an ok-but-empty snapshot is still carried (no windows to show)",
                    Flag(emptyOk[0], "Carried") && WindowCount(emptyOk[0]) == 2,
                    "Carried=" + Flag(emptyOk[0], "Carried") + ", " + WindowCount(emptyOk[0]) + " windows");

                IList allDead = Accounts(
                    Account("codex", "Codex", true, null, new object[] {
                        Window("five_hour", "5h", 0, false, null) }));
                Carry(form, allDead, good);
                Check("an all-unavailable snapshot is carried, not shown as 0%",
                    Flag(allDead[0], "Carried") && WindowCount(allDead[0]) == 2,
                    "Carried=" + Flag(allDead[0], "Carried") + ", " + WindowCount(allDead[0]) + " windows");
                Check("the carried card explains WHY the numbers are stale",
                    Text(failed[0], "CarriedNote").Length > 0,
                    "CarriedNote=\"" + Text(failed[0], "CarriedNote") + "\"");

                // The tray must not round UP when a vendor hiccups: a carried
                // reading still counts as an available metric.
                FieldInfo accountsField = formType.GetField("Accounts", BindingFlags.NonPublic | BindingFlags.Instance);
                IList live = (IList)accountsField.GetValue(form);
                live.Clear();
                live.Add(failed[0]);            // carried, 5h=42 week=77
                MethodInfo allMetrics = formType.GetMethod("AllMetrics");
                IList metrics = (IList)allMetrics.Invoke(form, null);
                Type metricType = asm.GetType("Limisaw.Metric");
                int availableCount = 0, lowest = 101;
                foreach (object m in metrics)
                {
                    if (!(bool)metricType.GetField("Available").GetValue(m)) continue;
                    availableCount++;
                    lowest = Math.Min(lowest, (int)metricType.GetField("Value").GetValue(m));
                }
                Check("carried readings still count, so the tray never rounds up",
                    availableCount == 2 && lowest == 42,
                    availableCount + " available, lowest=" + lowest + "%");

                string summary = (string)formType.GetMethod("AccountSummary")
                    .Invoke(form, new object[] { 0, false });
                Check("the menu row says the numbers are the last good ones",
                    summary != null && summary.Contains("last good"), summary);

                // A carried account must raise no reset balloon: its windows ARE
                // the previous windows, so any comparison is with itself.
                FieldInfo prevField = formType.GetField("PrevAccounts", BindingFlags.NonPublic | BindingFlags.Instance);
                IList prevList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(accType));
                object before = Account("codex", "Codex", true, null, new object[] {
                    Window("five_hour", "5h", 0, true, "2026-09-03T13:00:00") });
                prevList.Add(before);
                prevField.SetValue(form, prevList);
                object after = Account("codex", "Codex", false, "timed out", new object[0]);
                IList afterList = Accounts(after);
                Carry(form, afterList, prevList);
                accountsField.SetValue(form, afterList);
                FieldInfo notified = formType.GetField("NotifiedResetKeys", BindingFlags.NonPublic | BindingFlags.Instance);
                ((IList)notified.GetValue(form)).Clear();
                formType.GetMethod("DetectResets", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(form, null);
                Check("a carried account raises no reset balloon",
                    ((IList)notified.GetValue(form)).Count == 0,
                    ((IList)notified.GetValue(form)).Count + " balloon key(s)");
            }
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.GetType().Name + ": "
                + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
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
