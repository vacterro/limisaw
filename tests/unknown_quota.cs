using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Limisaw;

// CORE-002: a window that cannot state a number is NOT a window at 0%.
//
// `Model.Flatten` turned `Remaining = null` into `Rem = 0` and copied
// `Available` through unchanged, so a vendor that answers with a window but no
// percentage produced a reading that claims to be readable and claims to be
// empty. Three separate consequences, all silent:
//
//   * the card and the tray draw a confident 0% for a number no vendor sent;
//   * the account counts as having a reading, so CarryForward does NOT restore
//     the last good numbers — the fabricated zero replaces them;
//   * 0 is below any low threshold, so it arms a low-quota alert (balloon +
//     chime) on invented data, and the alert is recorded so the REAL crossing
//     later is suppressed as already-notified.
//
// The vendors really do this: Codex omits `usedPercent` on a window it still
// lists, Zcode can send a `limits` row with neither `remaining`/`usage` nor
// `percentage`, and Antigravity's disabled bucket is deliberately kept with no
// number at all (its reported fraction is a lie by design). The engine already
// had the right shape for it — `ProbeWindow.Unavailable`, drawn as "--" — so
// unknown must flatten to exactly that.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class UnknownQuotaTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    const double Now = 4.5e9;

    static Type formType;
    static object form;

    static object Get(string name) { return formType.GetField(name, NP).GetValue(form); }
    static void Set(string name, object v) { formType.GetField(name, NP).SetValue(form, v); }
    static void Call(string name) { formType.GetMethod(name, NP).Invoke(form, null); }

    static ProbeWindow Known(string key, double remaining, int minutes)
    {
        return new ProbeWindow
        {
            Key = key, Available = true, Remaining = remaining,
            ResetEpoch = Now + 3600, DurationMinutes = minutes,
        };
    }

    // The defect's shape: the vendor listed the window (so `Available` is true)
    // but sent no readable percentage.
    static ProbeWindow Unknown(string key, int minutes)
    {
        return new ProbeWindow
        {
            Key = key, Available = true, Remaining = null,
            ResetEpoch = Now + 3600, DurationMinutes = minutes,
        };
    }

    static AccountData Flat(params ProbeWindow[] windows)
    {
        var acc = new ProbeAccount
        {
            Provider = "codex", ProviderLabel = "Codex", Name = "Codex",
            Status = Model.OK, Ok = true, SourceId = "aaaa1111",
        };
        acc.Windows.AddRange(windows);
        return Model.Flatten(acc, Now);
    }

    static WindowData Win(AccountData acc, string key)
    {
        foreach (WindowData w in acc.Windows) if (w.Key == key) return w;
        return null;
    }

    static ProbeWindow Pick(List<ProbeWindow> windows, string key)
    {
        foreach (ProbeWindow w in windows) if (w.Key == key) return w;
        return null;
    }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_unknown_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Flatten();
            Vendors();
            Engine();
            Consequences(root, temp);
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
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " of " + checks + ")");
        return fails == 0 ? 0 : 1;
    }

    // ── the flatten boundary ────────────────────────────────────────────────
    static void Flatten()
    {
        Console.WriteLine("== an unreadable window flattens to \"--\", never to 0% ==");
        AccountData acc = Flat(Unknown(Model.FIVE_HOUR, 300), Known(Model.WEEKLY, 62.0, 10080));

        WindowData unknown = Win(acc, Model.FIVE_HOUR);
        Check("a window with no percentage is not available",
            unknown != null && !unknown.Available,
            unknown == null ? "missing" : "available=" + unknown.Available + " rem=" + unknown.Rem);
        // The window must still EXIST: dropping it would hide a limit the
        // account really has, which is the mirror defect.
        Check("but the window is still listed, so the limit is not hidden",
            acc.Windows.Count == 2, acc.Windows.Count + " window(s)");
        Check("a readable sibling in the same account keeps its number",
            Win(acc, Model.WEEKLY).Available && Win(acc, Model.WEEKLY).Rem == 62,
            "weekly=" + Win(acc, Model.WEEKLY).Rem + "%");

        // `HasReading` is what CarryForward and the blank-card logic ask.
        Check("an account whose only window is unreadable has no reading",
            !Flat(Unknown(Model.FIVE_HOUR, 300)).HasReading, "");
        Check("the abbreviation says -- rather than 0%",
            Flat(Unknown(Model.FIVE_HOUR, 300)).Abbrev() == "5h --",
            Flat(Unknown(Model.FIVE_HOUR, 300)).Abbrev());

        // An explicit zero is a real reading and must survive untouched: the fix
        // must not turn "spent" into "unknown".
        AccountData spent = Flat(Known(Model.FIVE_HOUR, 0.0, 300));
        Check("a vendor's real 0% is still a reading",
            Win(spent, Model.FIVE_HOUR).Available && Win(spent, Model.FIVE_HOUR).Rem == 0, "");
        Check("an already-unavailable window is unchanged",
            !Win(Flat(ProbeWindow.Unavailable(Model.FIVE_HOUR)), Model.FIVE_HOUR).Available, "");
    }

    // ── the three vendors that actually send it ─────────────────────────────
    static void Vendors()
    {
        Console.WriteLine();
        Console.WriteLine("== the payloads that carry an unreadable window ==");

        // Codex: the window is listed with its duration and reset, no usedPercent.
        string plan;
        List<ProbeWindow> codex = CodexSource.ParseWindows(J.Parse(
            @"{""rateLimits"":{""planType"":""plus"",
               ""primary"":{""windowDurationMins"":300,""resetsAt"":4500003600},
               ""secondary"":{""usedPercent"":10.5,""windowDurationMins"":10080,""resetsAt"":4500086400}}}"),
            out plan);
        Check("Codex: a window with no usedPercent parses with no number",
            Pick(codex, Model.FIVE_HOUR) != null && !Pick(codex, Model.FIVE_HOUR).Remaining.HasValue,
            Pick(codex, Model.FIVE_HOUR) == null ? "dropped" : "remaining=null");
        AccountData codexCard = Flat(codex.ToArray());
        Check("Codex: and reaches the card as -- while the weekly reads 90%",
            !Win(codexCard, Model.FIVE_HOUR).Available
            && Win(codexCard, Model.WEEKLY).Rem == 90,
            "5h=" + (Win(codexCard, Model.FIVE_HOUR).Available ? "?" : "--")
            + " week=" + Win(codexCard, Model.WEEKLY).Rem + "%");

        // Zcode: a limits row with neither remaining/usage nor percentage.
        string zplan, zerr;
        List<ProbeWindow> zcode = ZcodeSource.ParseQuota(
            @"{""code"":200,""data"":{""limits"":[
               {""type"":""CREDIT_LIMIT"",""unit"":3,""number"":5,""nextResetTime"":1788498595214},
               {""type"":""CREDIT_LIMIT"",""unit"":6,""number"":1,""usage"":10000,
                ""remaining"":9829,""percentage"":1,""nextResetTime"":1789085298997}],
               ""level"":""lite""},""success"":true}", out zplan, out zerr);
        Check("Zcode: a limits row with no numbers at all parses with no number",
            Pick(zcode, Model.FIVE_HOUR) != null && !Pick(zcode, Model.FIVE_HOUR).Remaining.HasValue,
            Pick(zcode, Model.FIVE_HOUR) == null ? "dropped" : "remaining=null");
        Check("Zcode: and its readable weekly is unaffected",
            Pick(zcode, Model.WEEKLY).Remaining.HasValue
            && Math.Abs(Pick(zcode, Model.WEEKLY).Remaining.Value - 98.29) < 0.01,
            "weekly=" + Pick(zcode, Model.WEEKLY).Remaining);

        // Antigravity: the disabled bucket is kept deliberately numberless. Its
        // pool's weekly is HEALTHY here, so gating must not invent a zero either.
        var rows = AntigravitySource.ParseUsagePayload(J.Parse(
            @"{""status"":""SUCCESS"",""command"":{""name"":""usage"",""data"":{""groups"":[
               {""name"":""Claude and GPT models"",""buckets"":[
                 {""id"":""3p-weekly"",""window"":""weekly"",""remaining_fraction"":0.5},
                 {""id"":""3p-5h"",""window"":""5h"",""disabled"":true,""remaining_fraction"":1}]}]}}}"));
        List<ProbeWindow> agy = Model.Resolve(AntigravitySource.WindowsFrom(rows, "test"), Now);
        string fiveKey = Model.Qualified(Model.FIVE_HOUR, "claude_and_gpt_models");
        Check("Antigravity: a disabled bucket under a healthy weekly stays unreadable",
            Pick(agy, fiveKey) != null && !Pick(agy, fiveKey).Available,
            Pick(agy, fiveKey) == null ? "dropped" : "available="
                + Pick(agy, fiveKey).Available);
    }

    // ── the rules that read the flattened window ────────────────────────────
    static void Engine()
    {
        Console.WriteLine();
        Console.WriteLine("== an unreadable window neither gates nor is gated into a lie ==");

        // Exhausted() requires a number, so an unknown window must not spend the
        // pool for its shorter siblings.
        var pool = new List<ProbeWindow> {
            Known(Model.FIVE_HOUR, 70.0, 300),
            Unknown(Model.WEEKLY, 10080),
        };
        List<ProbeWindow> resolved = Model.Resolve(pool, Now);
        Check("an unreadable weekly does not gate the 5h window below it",
            Pick(resolved, Model.FIVE_HOUR).Remaining == 70.0
            && Pick(resolved, Model.FIVE_HOUR).GatedBy == null,
            "5h=" + Pick(resolved, Model.FIVE_HOUR).Remaining
            + " gated_by=" + (Pick(resolved, Model.FIVE_HOUR).GatedBy ?? "none"));

        // The inverse: a spent weekly DOES gate, and gating supplies the number
        // the vendor withheld, so that window becomes readable — a gated zero is
        // a real answer ("locked"), not a fabricated one.
        var gated = new List<ProbeWindow> {
            Unknown(Model.FIVE_HOUR, 300),
            Known(Model.WEEKLY, 0.0, 10080),
        };
        AccountData gatedCard = Flat(gated.ToArray());
        Check("a spent weekly still locks an unreadable 5h window at 0%",
            Win(gatedCard, Model.FIVE_HOUR).Available
            && Win(gatedCard, Model.FIVE_HOUR).Rem == 0
            && Win(gatedCard, Model.FIVE_HOUR).GatedBy == Model.WEEKLY,
            "gated_by=" + (Win(gatedCard, Model.FIVE_HOUR).GatedBy ?? "none"));

        // A window whose own reset has passed is full by the clock, whatever the
        // vendor failed to say.
        var elapsed = new List<ProbeWindow> { new ProbeWindow {
            Key = Model.FIVE_HOUR, Available = true, Remaining = null,
            ResetEpoch = Now - 60, DurationMinutes = 300 } };
        AccountData refilled = Flat(elapsed.ToArray());
        Check("an unreadable window whose own reset passed is full by the clock",
            Win(refilled, Model.FIVE_HOUR).Available
            && Win(refilled, Model.FIVE_HOUR).Rem == 100
            && Win(refilled, Model.FIVE_HOUR).AssumedFull,
            "rem=" + Win(refilled, Model.FIVE_HOUR).Rem + "%");
    }

    // ── what the fabricated zero actually did ───────────────────────────────
    static void Consequences(string root, string temp)
    {
        Console.WriteLine();
        Console.WriteLine("== the three things a fabricated 0% caused ==");

        // Nothing discoverable: the constructor runs a real sweep, and this
        // harness owns every account it asserts on.
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
            Set("Refreshing", true);

            // 1. CarryForward: the last good numbers must survive a sweep that
            //    produced only unreadable windows.
            var previous = new List<AccountData> { Flat(Known(Model.FIVE_HOUR, 44.0, 300)) };
            var fresh = new List<AccountData> { Flat(Unknown(Model.FIVE_HOUR, 300)) };
            formType.GetMethod("CarryForward", NP).Invoke(form, new object[] { fresh, previous });
            Check("a sweep of unreadable windows carries the last good numbers",
                fresh[0].Carried && Win(fresh[0], Model.FIVE_HOUR).Rem == 44,
                "carried=" + fresh[0].Carried + " rem=" + Win(fresh[0], Model.FIVE_HOUR).Rem + "%");

            // 2. The tray must not draw or pick a number no vendor sent.
            var live = (List<AccountData>)Get("Accounts");
            live.Clear();
            live.Add(Flat(Unknown(Model.FIVE_HOUR, 300), Known(Model.WEEKLY, 55.0, 10080)));
            List<Metric> metrics = f.AllMetrics();
            int available = 0, lowest = 101;
            foreach (Metric m in metrics)
            {
                if (!m.Available) continue;
                available++;
                lowest = Math.Min(lowest, m.Value);
            }
            Check("the tray sees one reading, not a 0% that outranks it",
                available == 1 && lowest == 55, available + " available, lowest=" + lowest + "%");

            settings.TrayMetric = "lowest";
            object[] args = new object[] { 0, false, "" };
            MethodInfo pinned = null;
            foreach (MethodInfo cand in formType.GetMethods(NP))
                if (cand.Name == "GetTrayMetric" && cand.GetParameters().Length == 3) pinned = cand;
            pinned.Invoke(form, args);
            Check("\"lowest\" resolves to the readable window",
                (bool)args[1] && (int)args[0] == 55, "value=" + args[0] + "% label=" + args[2]);

            // 3. The low alert must not fire on invented data — and must not
            //    burn its once-per-cycle record, which would suppress the real
            //    crossing later.
            settings.NotifyLow = true; settings.LowSound = false; settings.LowPct = 20;
            var notified = (Dictionary<string, string>)Get("NotifiedLow");
            notified.Clear();
            Set("LowBaseline", false);
            Call("DetectLow");
            Call("DetectLow");
            Check("an unreadable window arms no low alert",
                notified.Count == 0, notified.Count + " armed key(s)");

            // The control: the same account with a REAL 8% does arm, so the
            // check above is not passing because the alert path is inert.
            live.Clear();
            live.Add(Flat(Known(Model.FIVE_HOUR, 8.0, 300)));
            notified.Clear();
            Set("LowBaseline", false);
            Call("DetectLow");
            Check("...while a real 8% still arms one",
                notified.Count == 1, notified.Count + " armed key(s)");

            // 4. Unknown -> a real number is a first reading, not a refill: a
            //    reset balloon there would announce a reset that never happened.
            settings.NotifyOnReset = true;
            var prevAccounts = new List<AccountData> { Flat(Unknown(Model.FIVE_HOUR, 300)) };
            Set("PrevAccounts", prevAccounts);
            live.Clear();
            live.Add(Flat(Known(Model.FIVE_HOUR, 100.0, 300)));
            var keys = (List<string>)Get("NotifiedResetKeys");
            keys.Clear();
            Call("DetectResets");
            Check("an unreadable window becoming readable raises no reset balloon",
                keys.Count == 0, keys.Count + " balloon key(s)");
        }
    }

    // ── the fabricated zero cannot come back ────────────────────────────────
    static void Source(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== the fabricated zero cannot come back ==");
        string probe = File.ReadAllText(Path.Combine(SourceRoot(root), "Probe.cs"));
        Check("Flatten gates Available on a readable number",
            probe.IndexOf("Available = w.Available && readable,", StringComparison.Ordinal) >= 0, "");
        Check("and no longer copies Available through unchanged",
            probe.IndexOf("Available = w.Available,", StringComparison.Ordinal) < 0, "");
    }

    static string SourceRoot(string start)
    {
        string dir = start;
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Probe.cs"))) return dir;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        return start;
    }
}
