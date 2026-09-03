using System;
using System.Collections.Generic;

// Replays LIMISAW's reset-notification path verbatim to prove a gated window
// reaches the balloon text as "locked", and that detection of a genuine
// turnover is NOT suppressed by the gate.
//
// The gate itself is decided by the probe (Scripts/limisaw_limits/model.py
// gate_windows) and arrives as WindowData.GatedBy; this harness pins what the
// app does with it.
//
// Build + run:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:reset_lock.exe -r:System.dll tests\reset_lock.cs
//   reset_lock.exe          (exit 0 = all PASS)
public static class ResetLock
{
    class WindowData
    {
        public string Key = "", Label = "", GroupLabel = "", Reset, GatedBy;
        public bool Available = true;
        public int Rem;
    }

    class AccountData
    {
        public string Provider = "codex", Name = "Codex";
        public bool Ok = true;
        public List<WindowData> Windows = new List<WindowData>();
        public string Key { get { return Provider + "/" + Name; } }
        public WindowData Find(string key)
        {
            foreach (WindowData w in Windows) if (w.Key == key) return w;
            return null;
        }
    }

    class ResetEvent
    {
        public string AccountLabel = "", LimitLabel = "";
        public int NewRemaining; public string ResetAt;
        public bool LockedByWeekly;
    }

    static List<string> NotifiedResetKeys = new List<string>();
    static List<string> Fired = new List<string>();

    // ── verbatim from LIMISAW.cs ──
    static string WindowTitle(WindowData w)
    {
        return string.IsNullOrEmpty(w.GroupLabel) ? w.Label : w.Label + " (" + w.GroupLabel + ")";
    }

    static void CheckReset(AccountData acc, WindowData cur, WindowData prev, bool locked)
    {
        if (!cur.Available || !prev.Available) return;
        if (string.IsNullOrEmpty(cur.Reset) || string.IsNullOrEmpty(prev.Reset)) return;
        if (cur.Reset == prev.Reset && cur.Rem <= prev.Rem + 25) return;
        if (cur.Rem <= prev.Rem + 25) return;
        string key = acc.Key + "_" + cur.Key + "_" + cur.Reset;
        if (NotifiedResetKeys.Contains(key)) return;
        NotifiedResetKeys.Add(key);
        if (NotifiedResetKeys.Count > 100) NotifiedResetKeys.RemoveRange(0, 50);
        Fired.Add(NotifyText(new ResetEvent
        {
            AccountLabel = acc.Name, LimitLabel = WindowTitle(cur),
            NewRemaining = cur.Rem, ResetAt = cur.Reset, LockedByWeekly = locked,
        }));
    }

    static string NotifyText(ResetEvent ev)
    {
        return ev.LockedByWeekly
            ? ev.LimitLabel + " limit reset - still 0% usable, locked by a longer window"
            : ev.LimitLabel + " limit reset - " + ev.NewRemaining + "% remaining";
    }

    static void DetectResets(AccountData cur, AccountData prev)
    {
        if (!cur.Ok || prev == null || !prev.Ok) return;
        foreach (WindowData w in cur.Windows)
        {
            WindowData pw = prev.Find(w.Key);
            if (pw == null) continue;
            CheckReset(cur, w, pw, w.GatedBy != null);
        }
    }

    static int fails = 0;

    static AccountData Acc(WindowData[] windows)
    {
        var a = new AccountData();
        a.Windows.AddRange(windows);
        return a;
    }

    static WindowData W(string key, string label, int rem, string reset, string gatedBy)
    {
        return new WindowData { Key = key, Label = label, Rem = rem, Reset = reset, GatedBy = gatedBy };
    }

    static void Case(string name, AccountData prev, AccountData cur, string[] want)
    {
        NotifiedResetKeys.Clear();
        Fired.Clear();
        DetectResets(cur, prev);
        bool ok = Fired.Count == want.Length;
        if (ok)
            for (int i = 0; i < want.Length; i++)
                if (Fired[i] != want[i]) ok = false;
        if (ok) Console.WriteLine("PASS  " + name + (Fired.Count == 0 ? "  (silent)" : "  -> " + string.Join(" | ", Fired.ToArray())));
        else
        {
            fails++;
            Console.WriteLine("FAIL  " + name);
            Console.WriteLine("        got  " + (Fired.Count == 0 ? "(silent)" : string.Join(" | ", Fired.ToArray())));
            Console.WriteLine("        want " + (want.Length == 0 ? "(silent)" : string.Join(" | ", want)));
        }
    }

    public static int Main()
    {
        // the defect: 5h refills to 100% while the weekly window is spent, so
        // the probe gates it and the balloon must not promise free quota
        Case("5h refill while gated -> must not promise 100%",
             Acc(new[] { W("five_hour", "5h", 0, "T1", "weekly"), W("weekly", "week", 0, "W1", null) }),
             Acc(new[] { W("five_hour", "5h", 100, "T2", "weekly"), W("weekly", "week", 0, "W1", null) }),
             new[] { "5h limit reset - still 0% usable, locked by a longer window" });

        // weekly has budget: the real percentage is honest
        Case("5h refill, weekly 55% -> plain percentage",
             Acc(new[] { W("five_hour", "5h", 0, "T1", null), W("weekly", "week", 55, "W1", null) }),
             Acc(new[] { W("five_hour", "5h", 100, "T2", null), W("weekly", "week", 55, "W1", null) }),
             new[] { "5h limit reset - 100% remaining" });

        // weekly itself resets: never marked locked
        Case("weekly refill announces its own percentage",
             Acc(new[] { W("five_hour", "5h", 10, "T1", null), W("weekly", "week", 0, "W1", null) }),
             Acc(new[] { W("five_hour", "5h", 10, "T1", null), W("weekly", "week", 90, "W2", null) }),
             new[] { "week limit reset - 90% remaining" });

        Case("both windows refill",
             Acc(new[] { W("five_hour", "5h", 0, "T1", null), W("weekly", "week", 0, "W1", null) }),
             Acc(new[] { W("five_hour", "5h", 100, "T2", null), W("weekly", "week", 90, "W2", null) }),
             new[] { "5h limit reset - 100% remaining", "week limit reset - 90% remaining" });

        // noise stays silent, gate or not
        Case("negligible rise stays silent",
             Acc(new[] { W("five_hour", "5h", 50, "T1", "weekly") }),
             Acc(new[] { W("five_hour", "5h", 60, "T1", "weekly") }),
             new string[] { });

        Case("drop stays silent",
             Acc(new[] { W("five_hour", "5h", 80, "T1", null) }),
             Acc(new[] { W("five_hour", "5h", 20, "T2", null) }),
             new string[] { });

        // an ungated window is never announced as locked
        Case("no gate -> plain percentage",
             Acc(new[] { W("five_hour", "5h", 0, "T1", null) }),
             Acc(new[] { W("five_hour", "5h", 100, "T2", null) }),
             new[] { "5h limit reset - 100% remaining" });

        // Antigravity keeps two independent pools: each window is announced with
        // its pool name, so two "week" resets can never read as one event.
        Case("pooled windows are announced separately",
             Acc(new[] { W("weekly@gemini_models", "week", 0, "W1", null),
                         W("weekly@claude_and_gpt", "week", 0, "X1", null) }),
             Acc(new[] { W("weekly@gemini_models", "week", 68, "W2", null),
                         W("weekly@claude_and_gpt", "week", 90, "X2", null) }),
             new[] { "week limit reset - 68% remaining", "week limit reset - 90% remaining" });

        // a window that vanished from the snapshot cannot fire
        Case("window missing in the previous snapshot stays silent",
             Acc(new[] { W("weekly", "week", 0, "W1", null) }),
             Acc(new[] { W("five_hour", "5h", 100, "T2", null) }),
             new string[] { });

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
