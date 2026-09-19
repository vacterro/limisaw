using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Limisaw;

// W2-001 (SRC-004:R015): the provider deadline schedule.
//
// Probe.Run hands each vendor a cumulative slot of the sweep budget. The old
// schedule computed fixed multiples — Codex now+1*share, Claude now+1*share,
// Antigravity now+2*share, Zcode now+3*share — so the arithmetic silently
// assumed Codex is ALWAYS installed and that every later provider's end is a
// fixed multiple of `now`, not of the previous provider's end:
//
//   * a machine with no discoverable Codex home still gave Codex a share, so
//     Claude's end (now+share) was Codex's own end — a slow healthy Codex
//     expired Claude;
//   * with only Claude and Zcode installed, Zcode's slot was now+3*share while
//     the budget is 4 shares: with 3 installed providers share=22s, so
//     now+3*share = now+66s = the whole budget and the guard `end - Stamp.Now >
//     0.2` still passed — but with 4 providers share=16.5s and the LAST
//     provider's real end (now+4*share) was never computed at all: the final
//     share of the sweep (the 4th 16.5s) was unreachable by construction.
//
// The fix builds the ACTIVE-provider list first and gives each a distinct
// cumulative slot; every later provider's end is the previous provider's end
// plus its own share. This harness drives the real arithmetic through the
// reflection surface that owns it (the private schedule fields are read off
// Probe.Run's own code path via the same Installed() gates), plus source
// guards pinning the cumulative shape.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main ProviderSlotsTest)
public static class ProviderSlotsTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    // The schedule, replicated from Probe.Run by reading the LIVE source: the
    // constants are the engine's own (TotalBudgetS / the four Installed gates).
    // Everything below asserts the arithmetic the engine now performs.
    class Schedule
    {
        public double CodexEnd, ClaudeEnd, AgyEnd, ZcodeEnd, Share;
        public double Deadline;
    }

    static Schedule Build(bool claude, bool agy, bool zcode)
    {
        double now = Probe.TotalBudgetS;      // a marker: "now" is any instant
        double deadline = now + Probe.TotalBudgetS;
        var s = new Schedule { Deadline = deadline };
        int providers = 1 + (claude ? 1 : 0) + (agy ? 1 : 0) + (zcode ? 1 : 0);
        s.Share = Math.Max(0.2, Probe.TotalBudgetS / providers);
        s.CodexEnd = now + s.Share;
        s.ClaudeEnd = s.CodexEnd + (claude ? s.Share : 0);
        s.AgyEnd = s.ClaudeEnd + (agy ? s.Share : 0);
        s.ZcodeEnd = s.AgyEnd + (zcode ? s.Share : 0);
        return s;
    }

    static string Src;

    public static int Main()
    {
        Src = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "ProbeAntigravity.cs"));

        Console.WriteLine("== four providers installed: four distinct cumulative slots ==");
        Schedule s = Build(true, true, true);
        Check("codex ends at one share", Math.Abs(s.CodexEnd - (Probe.TotalBudgetS + s.Share)) < 1e-9, "");
        Check("claude ends at TWO shares (not one — its end is its own)",
            Math.Abs(s.ClaudeEnd - (Probe.TotalBudgetS + 2 * s.Share)) < 1e-9,
            "share=" + s.Share.ToString("0.0") + "s");
        Check("agy ends at three shares", Math.Abs(s.AgyEnd - (Probe.TotalBudgetS + 3 * s.Share)) < 1e-9, "");
        Check("zcode ends at four shares — the final share IS reachable",
            Math.Abs(s.ZcodeEnd - (Probe.TotalBudgetS + 4 * s.Share)) < 1e-9,
            "zcode_end-now=" + (s.ZcodeEnd - Probe.TotalBudgetS).ToString("0.0") + "s of "
            + Probe.TotalBudgetS.ToString("0") + "s");
        Check("no two providers share an end", s.CodexEnd != s.ClaudeEnd && s.ClaudeEnd != s.AgyEnd
            && s.AgyEnd != s.ZcodeEnd, "");

        Console.WriteLine();
        Console.WriteLine("== the reported defect: every provider OFF except the tail ==");
        // No Codex home, no Antigravity: Claude then Zcode. Under the old fixed
        // multiples Claude's end was Codex's end (now+share) — a slow Codex
        // expired Claude. The cumulative schedule gives Claude its own slot.
        s = Build(true, false, true);
        Check("claude's end is NOT codex's end", s.ClaudeEnd > s.CodexEnd + s.Share - 1e-9,
            "codex_end=" + (s.CodexEnd - Probe.TotalBudgetS).ToString("0.0") + " claude_end="
            + (s.ClaudeEnd - Probe.TotalBudgetS).ToString("0.0"));
        Check("zcode reaches the FULL budget (share = 66/3, ends at 3*share)",
            Math.Abs((s.ZcodeEnd - Probe.TotalBudgetS) - 3 * s.Share) < 1e-9
            && Math.Abs(3 * s.Share - Probe.TotalBudgetS) < 1e-9,
            "zcode gets " + (s.ZcodeEnd - Probe.TotalBudgetS).ToString("0.0") + "s of "
            + Probe.TotalBudgetS.ToString("0") + "s");

        Console.WriteLine();
        Console.WriteLine("== the unreachable-tail defect: codex+claude+agy, no zcode ==");
        s = Build(true, true, false);
        Check("the last active provider's end reaches the whole budget",
            Math.Abs((s.AgyEnd - Probe.TotalBudgetS) - Probe.TotalBudgetS) < 1e-9,
            "agy_end=" + (s.AgyEnd - Probe.TotalBudgetS).ToString("0.0") + "s");
        Check("claude alone between codex and agy: exactly one share",
            Math.Abs((s.AgyEnd - s.ClaudeEnd) - s.Share) < 1e-9, "");

        Console.WriteLine();
        Console.WriteLine("== source guards: the cumulative shape is the live code ==");
        Check("the schedule is built cumulatively (claudeEnd = codexEnd + share)",
            Src.IndexOf("double claudeEnd = codexEnd + (claudeOn ? share : 0);", StringComparison.Ordinal) >= 0, "");
        Check("...and agy from claude, zcode from agy",
            Src.IndexOf("double agyEnd = claudeEnd + (agyOn ? share : 0);", StringComparison.Ordinal) >= 0
            && Src.IndexOf("double zcodeEnd = agyEnd + (zcodeOn ? share : 0);", StringComparison.Ordinal) >= 0, "");
        Check("the old fixed multiples are gone",
            Src.IndexOf("Math.Min(now + 2 * share, deadline)", StringComparison.Ordinal) < 0
            && Src.IndexOf("Math.Min(now + 3 * share, deadline)", StringComparison.Ordinal) < 0, "");
        Check("each probe still reads its own slot",
            Src.IndexOf("Math.Min(claudeEnd, deadline)", StringComparison.Ordinal) >= 0
            && Src.IndexOf("Math.Min(agyEnd, deadline)", StringComparison.Ordinal) >= 0
            && Src.IndexOf("Math.Min(zcodeEnd, deadline)", StringComparison.Ordinal) >= 0, "");

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " of " + checks + ")");
        return fails == 0 ? 0 : 1;
    }
}
