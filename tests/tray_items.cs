using System;
using System.Collections.Generic;

// Replays LIMISAW's tray-item selection verbatim: which readings the tray
// draws, in which order, and how many. 16 tray pixels cannot carry every
// window three vendors expose, so the user picks — and the picking rules are
// where a wrong answer silently shows the wrong account's quota.
//
// Rules pinned here:
//   * the saved order wins, then newly discovered readings follow;
//   * a reading the user has never seen is shown by DEFAULT (silently hiding a
//     brand-new account would look like the vendor broke);
//   * hidden readings never reach the tray, at any position;
//   * the count cap truncates AFTER ordering and hiding;
//   * an id in the saved order that no longer exists is skipped, not fatal;
//   * a move is a swap with the neighbour and is clamped at both ends.
//
// Build + run:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:tray_items.exe -r:System.dll tests\tray_items.cs
//   tray_items.exe          (exit 0 = all PASS)
public static class TrayItems
{
    const int MaxTrayItems = 9;

    class Metric
    {
        public string Id = "";
        public int Value; public bool Available = true; public bool IsShort;
    }

    // ── verbatim from LimisawSettings ──
    static List<string> Split(string value)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(value)) return list;
        foreach (string part in value.Split('|'))
        { string t = part.Trim(); if (t.Length > 0 && !list.Contains(t)) list.Add(t); }
        return list;
    }

    static string Join(List<string> ids) { return string.Join("|", ids.ToArray()); }

    // ── verbatim from LimisawForm.TrayMetrics ──
    static List<Metric> TrayMetrics(List<Metric> all, string order, string hiddenCsv, int trayMax)
    {
        List<string> hidden = Split(hiddenCsv);
        var picked = new List<Metric>();
        foreach (string id in Split(order))
        {
            if (hidden.Contains(id)) continue;
            foreach (Metric m in all)
                if (m.Id == id && !picked.Contains(m)) { picked.Add(m); break; }
        }
        foreach (Metric m in all)
            if (!hidden.Contains(m.Id) && !picked.Contains(m)) picked.Add(m);
        if (picked.Count > trayMax) picked.RemoveRange(trayMax, picked.Count - trayMax);
        return picked;
    }

    // ── verbatim from LimisawForm.CurrentOrder / MoveItem ──
    static List<string> CurrentOrder(List<Metric> all, string order)
    {
        List<string> list = Split(order);
        foreach (Metric m in all) if (!list.Contains(m.Id)) list.Add(m.Id);
        return list;
    }

    static string MoveItem(List<Metric> all, string order, string id, int delta)
    {
        List<string> list = CurrentOrder(all, order);
        int at = list.IndexOf(id);
        int to = at + delta;
        if (at < 0 || to < 0 || to >= list.Count) return Join(list);
        list.RemoveAt(at); list.Insert(to, id);
        return Join(list);
    }

    static string ToggleItem(string hiddenCsv, string id)
    {
        List<string> hidden = Split(hiddenCsv);
        if (hidden.Contains(id)) hidden.Remove(id); else hidden.Add(id);
        return Join(hidden);
    }

    static int ClampMax(int value)
    {
        if (value < 1) return 1;
        if (value > MaxTrayItems) return MaxTrayItems;
        return value;
    }

    // ── the realistic fleet: 3 Codex accounts + Claude + Antigravity's 2 pools ──
    static readonly string[] Fleet = {
        "codex/Codex/five_hour", "codex/Codex/weekly",
        "codex/Account2/five_hour", "codex/Account2/weekly",
        "codex/Account3Free/monthly",
        "claude/Claude/five_hour", "claude/Claude/weekly",
        "antigravity/Antigravity/five_hour@gemini_models",
        "antigravity/Antigravity/weekly@gemini_models",
        "antigravity/Antigravity/weekly@claude_and_gpt",
    };

    static List<Metric> All()
    {
        var list = new List<Metric>();
        for (int i = 0; i < Fleet.Length; i++)
            list.Add(new Metric { Id = Fleet[i], Value = (i * 11) % 101, IsShort = Fleet[i].Contains("five_hour") });
        return list;
    }

    static int fails = 0;
    static void Check(string name, string got, string want)
    {
        if (got == want) Console.WriteLine("PASS  " + name + "  -> " + (got.Length == 0 ? "(none)" : got));
        else
        {
            fails++;
            Console.WriteLine("FAIL  " + name);
            Console.WriteLine("        got  " + got);
            Console.WriteLine("        want " + want);
        }
    }

    static string Ids(List<Metric> items)
    {
        var parts = new List<string>();
        foreach (Metric m in items) parts.Add(m.Id);
        return Join(parts);
    }

    static string Shown(string order, string hidden, int max)
    {
        return Ids(TrayMetrics(All(), order, hidden, max));
    }

    public static int Main()
    {
        // 1. no choice yet: discovery order, capped
        Check("default = discovery order, capped at 4",
            Shown("", "", 4),
            "codex/Codex/five_hour|codex/Codex/weekly|codex/Account2/five_hour|codex/Account2/weekly");

        // 2. the cap is the whole point: ten readings do not fit 16 pixels
        Check("cap of 1 keeps exactly the first", Shown("", "", 1), "codex/Codex/five_hour");
        Check("cap truncates after ordering, keeping the first 9 of 10",
            Shown("", "", MaxTrayItems),
            Join(Split(string.Join("|", Fleet))).Replace("|" + Fleet[Fleet.Length - 1], ""));

        // 3. explicit order wins, and the rest follows behind it
        Check("explicit order is honoured",
            Shown("claude/Claude/weekly|antigravity/Antigravity/weekly@claude_and_gpt", "", 3),
            "claude/Claude/weekly|antigravity/Antigravity/weekly@claude_and_gpt|codex/Codex/five_hour");

        // 4. hiding removes a reading at ANY position, including a pinned one
        Check("hidden reading never reaches the tray",
            Shown("claude/Claude/weekly|codex/Codex/five_hour", "claude/Claude/weekly", 2),
            "codex/Codex/five_hour|codex/Codex/weekly");
        Check("hiding the first discovery slot promotes the next",
            Shown("", "codex/Codex/five_hour", 2),
            "codex/Codex/weekly|codex/Account2/five_hour");

        // 5. everything hidden is an empty tray set, not a crash
        Check("all hidden -> nothing selected", Shown("", string.Join("|", Fleet), 4), "");

        // 6. a stale id (logged-out account) is skipped, not fatal
        Check("unknown id in the saved order is skipped",
            Shown("codex/Ghost/five_hour|claude/Claude/weekly", "", 2),
            "claude/Claude/weekly|codex/Codex/five_hour");

        // 7. a duplicate id cannot double-book a slot
        Check("duplicate id is collapsed",
            Shown("claude/Claude/weekly|claude/Claude/weekly", "", 2),
            "claude/Claude/weekly|codex/Codex/five_hour");

        // 8. moving: swap with the neighbour, clamped at both ends
        Check("move up swaps with the previous row",
            MoveItem(All(), "", "codex/Codex/weekly", -1).Split('|')[0],
            "codex/Codex/weekly");
        Check("move up at the top is a no-op",
            MoveItem(All(), "", "codex/Codex/five_hour", -1),
            Join(CurrentOrder(All(), "")));
        Check("move down at the bottom is a no-op",
            MoveItem(All(), "", Fleet[Fleet.Length - 1], 1),
            Join(CurrentOrder(All(), "")));
        Check("move down swaps with the next row",
            MoveItem(All(), "", "codex/Codex/five_hour", 1).Split('|')[1],
            "codex/Codex/five_hour");

        // 9. toggling is symmetric — a hidden row is the only way back
        string hidden1 = ToggleItem("", "claude/Claude/weekly");
        Check("toggle hides", hidden1, "claude/Claude/weekly");
        Check("toggle again shows", ToggleItem(hidden1, "claude/Claude/weekly"), "");

        // 10. the count is clamped to something a 16px icon can render
        Check("count clamp low", ClampMax(0).ToString(), "1");
        Check("count clamp high", ClampMax(99).ToString(), MaxTrayItems.ToString());

        // 11. a NEW reading (vendor just logged in) shows up by default even
        //     when an explicit order exists — the opposite would look broken
        var withNew = All();
        withNew.Add(new Metric { Id = "codex/Account4/five_hour", Value = 50 });
        Check("a newly discovered reading is visible by default",
            Ids(TrayMetrics(withNew, "codex/Account4/five_hour", "", 1)),
            "codex/Account4/five_hour");
        Check("a new reading appended after an explicit order still fits the cap",
            Ids(TrayMetrics(withNew, "claude/Claude/weekly", "", 11)).EndsWith("codex/Account4/five_hour")
                ? "appended" : "missing",
            "appended");

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
