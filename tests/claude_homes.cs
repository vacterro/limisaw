using System;
using System.Collections.Generic;
using System.IO;
using Limisaw;

// TWO CLAUDE ACCOUNTS ON ONE MACHINE.
//
// Claude Code keeps an entire account inside its config directory, and
// CLAUDE_CONFIG_DIR is the CLI's own switch for which directory that is — so a
// second subscription is a second directory, the same shape Codex homes have
// had here since CORE-001. Claude was hardcoded to %USERPROFILE%\.claude: the
// second account was not "partially supported", it was invisible, and the one
// card on screen silently meant whichever account the default home held.
//
// What this harness defends:
//   * every config directory becomes its own account, and a stray folder does
//     not (a `.claude-*` sibling needs a real marker inside it),
//   * identity is the canonical PATH digest, never the display label — two
//     homes called "Claude" must still be two accounts,
//   * a single-home machine behaves exactly as it did before,
//   * Desktop's sampler, which measures the ONE account Desktop is signed into,
//     is never attached to a second home,
//   * the bridge-cache memo is keyed by path, so one account's quota can never
//     be served as the other's.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main ClaudeHomesTest)
public static class ClaudeHomesTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Scratch, AppData;
    static readonly List<string> Made = new List<string>();

    // A fresh profile per scenario. The CLI must never answer here: PATH points
    // at nothing, so every reading comes from the local sources this harness
    // writes itself.
    static void NewProfile()
    {
        Scratch = Path.Combine(Path.GetTempPath(), "limisaw_claudehomes_" + Guid.NewGuid().ToString("N"));
        Made.Add(Scratch);
        AppData = Path.Combine(Scratch, "AppData", "Roaming");
        Directory.CreateDirectory(Path.Combine(AppData, "Claude"));
        Environment.SetEnvironmentVariable("APPDATA", AppData);
        Environment.SetEnvironmentVariable("USERPROFILE", Scratch);
        Environment.SetEnvironmentVariable("HOME", Scratch);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
        Environment.SetEnvironmentVariable("PATH", Path.Combine(Scratch, "no-tools"));
        ClaudeSource.ResetDesktopCache();
        ClaudeSource.ResetBridgeCacheForTests();
    }

    // A config directory that looks like one: the marker is what separates a
    // real second account from a backup folder.
    static string MakeHome(string leaf, bool withMarker)
    {
        string dir = Path.Combine(Scratch, leaf);
        Directory.CreateDirectory(dir);
        if (withMarker) File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
        return dir;
    }

    // The status-line cache a bridge may write into a home. One per account,
    // with its own numbers — that is the whole point of the assertion.
    static void WriteBridge(string home, int fiveHourUsed, int weeklyUsed)
    {
        string json = "{\"schema_version\":1,\"captured_at\":" + Stamp.Now.ToString("0")
            + ",\"rate_limits\":{\"five_hour\":{\"used_percentage\":" + fiveHourUsed
            + "},\"seven_day\":{\"used_percentage\":" + weeklyUsed + "}}}";
        File.WriteAllText(Path.Combine(home, "limisaw-rate-limits.json"), json);
    }

    static void WriteDesktopHistory(int fiveHourUsed, int weeklyUsed)
    {
        double t = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        File.WriteAllText(Path.Combine(AppData, "Claude", "plan-usage-history.json"),
            "{\"samples\":[{\"t\":" + t.ToString("0") + ",\"u\":{\"fh\":" + fiveHourUsed
                + ",\"sd\":" + weeklyUsed + "}}]}");
    }

    static int? Remaining(ProbeAccount acc, string key)
    {
        foreach (ProbeWindow w in acc.Windows)
            if (w.Key == key) return w.Available && w.Remaining.HasValue ? (int)w.Remaining.Value : (int?)null;
        return null;
    }

    static ProbeAccount ByName(List<ProbeAccount> list, string name)
    {
        foreach (ProbeAccount a in list) if (a.Name == name) return a;
        return null;
    }

    static string Names(List<ClaudeSource.ClaudeHome> homes)
    {
        var parts = new List<string>();
        foreach (ClaudeSource.ClaudeHome h in homes) parts.Add(h.Name);
        return string.Join("|", parts.ToArray());
    }

    public static int Main()
    {
        try
        {
            Discovery();
            Identity();
            SweepShape();
            DesktopStaysOnItsOwnAccount();
            BridgeIsPerHome();
            SingleHomeUnchanged();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            foreach (string dir in Made)
                try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    // ── discovery ───────────────────────────────────────────────────────────
    static void Discovery()
    {
        Console.WriteLine("== a config directory is an account ==");
        NewProfile();
        MakeHome(".claude", true);
        List<ClaudeSource.ClaudeHome> homes = ClaudeSource.Homes();
        Check("one home with only the default directory", homes.Count == 1, Names(homes));
        Check("...and it is marked as the default", homes[0].IsDefaultHome, homes[0].Path);

        MakeHome(".claude-work", true);
        homes = ClaudeSource.Homes();
        Check("a second config directory is a second account", homes.Count == 2, Names(homes));
        Check("the sibling is labelled after itself", Names(homes) == "Claude|Work", Names(homes));

        MakeHome(".claude-backup", false);
        homes = ClaudeSource.Homes();
        Check("a directory with nothing inside it is NOT an account",
            homes.Count == 2, Names(homes));

        // The explicit switch: a directory anywhere, named by the same variable
        // the CLI itself reads. A ';' list is how two accounts are named when
        // neither lives in ~/.claude-*.
        string alt = Path.Combine(Scratch, "elsewhere", "acct2");
        Directory.CreateDirectory(alt);
        File.WriteAllText(Path.Combine(alt, ".credentials.json"), "{}");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", alt);
        homes = ClaudeSource.Homes();
        Check("CLAUDE_CONFIG_DIR adds its directory", homes.Count == 3, Names(homes));
        Check("...labelled after its own leaf", Names(homes).StartsWith("Acct2"), Names(homes));

        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", alt + ";" + Path.Combine(Scratch, ".claude"));
        homes = ClaudeSource.Homes();
        Check("a ';' list names both, and the duplicate default is not doubled",
            homes.Count == 3, Names(homes));

        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
        Check("Installed() is true while any home exists", ClaudeSource.Installed(), "");
    }

    // ── identity ────────────────────────────────────────────────────────────
    static void Identity()
    {
        Console.WriteLine();
        Console.WriteLine("== the label is not the identity ==");
        NewProfile();
        MakeHome(".claude", true);
        // A second directory that ALSO wants to be called "Claude": the old
        // single-card shape could not express this at all.
        string twin = Path.Combine(Scratch, "second", ".claude");
        Directory.CreateDirectory(twin);
        File.WriteAllText(Path.Combine(twin, "settings.json"), "{}");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", twin);

        List<ClaudeSource.ClaudeHome> homes = ClaudeSource.Homes();
        Check("both directories are accounts", homes.Count == 2, Names(homes));
        Check("two same-named homes still have different ids",
            homes[0].Id != homes[1].Id, homes[0].Id + " vs " + homes[1].Id);
        Check("a duplicate label is disambiguated for BOTH of them",
            homes[0].Name != "Claude" && homes[1].Name != "Claude", Names(homes));

        string idAgain = null;
        foreach (ClaudeSource.ClaudeHome h in ClaudeSource.Homes())
            if (h.Path == homes[0].Path) idAgain = h.Id;
        Check("an id is stable across calls", idAgain == homes[0].Id, idAgain ?? "null");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
    }

    // ── the sweep ───────────────────────────────────────────────────────────
    static void SweepShape()
    {
        Console.WriteLine();
        Console.WriteLine("== the sweep produces one card per account ==");
        NewProfile();
        string a = MakeHome(".claude", true);
        string b = MakeHome(".claude-work", true);
        WriteBridge(a, 10, 20);
        WriteBridge(b, 70, 80);

        List<ProbeAccount> accounts = ClaudeSource.Sweep(Stamp.Now + 8, 8);
        Check("two accounts, not one", accounts.Count == 2, accounts.Count.ToString());

        ProbeAccount first = ByName(accounts, "Claude"), second = ByName(accounts, "Work");
        Check("each card names its own home",
            first != null && second != null, Names(ClaudeSource.Homes()));
        Check("each card routes to its own config directory",
            first.ResetHome == a && second.ResetHome == b, first.ResetHome + " / " + second.ResetHome);
        Check("each card carries its own stable id",
            first.SourceId.Length == 16 && first.SourceId != second.SourceId,
            first.SourceId + " vs " + second.SourceId);

        double now = Stamp.Now;
        AccountData flatA = Model.Flatten(first, now), flatB = Model.Flatten(second, now);
        Check("the account keys are distinct, so nothing overwrites anything",
            flatA.Key != flatB.Key, flatA.Key + " vs " + flatB.Key);
        Check("a key is the source id, not the label",
            flatA.Key == "claude/" + first.SourceId, flatA.Key);
        Check("the pre-CORE-001 key is still readable for migration",
            flatA.LegacyKey == "claude/Claude", flatA.LegacyKey);
    }

    // ── Desktop measures ONE account ────────────────────────────────────────
    static void DesktopStaysOnItsOwnAccount()
    {
        Console.WriteLine();
        Console.WriteLine("== Desktop's sampler is evidence for one home only ==");
        NewProfile();
        MakeHome(".claude", true);
        MakeHome(".claude-work", true);
        WriteDesktopHistory(30, 40);          // 70% / 60% remaining

        List<ProbeAccount> accounts = ClaudeSource.Sweep(Stamp.Now + 8, 8);
        ProbeAccount main = ByName(accounts, "Claude"), other = ByName(accounts, "Work");
        Check("the default home reads Desktop's sample",
            Remaining(main, Model.FIVE_HOUR) == 70, (Remaining(main, Model.FIVE_HOUR) ?? -1).ToString());
        Check("the second home does NOT inherit it",
            Remaining(other, Model.FIVE_HOUR) == null,
            (Remaining(other, Model.FIVE_HOUR) ?? -1).ToString());
        Check("...and says so instead of showing a borrowed number",
            other.Status == Model.UNAVAILABLE && !other.Ok, other.Status + " / " + (other.Error ?? ""));
    }

    // ── one cache per home ──────────────────────────────────────────────────
    static void BridgeIsPerHome()
    {
        Console.WriteLine();
        Console.WriteLine("== two homes, two caches, two answers ==");
        NewProfile();
        string a = MakeHome(".claude", true);
        string b = MakeHome(".claude-work", true);
        WriteBridge(a, 10, 20);               // 90% / 80% remaining
        WriteBridge(b, 70, 80);               // 30% / 20% remaining

        List<ProbeAccount> accounts = ClaudeSource.Sweep(Stamp.Now + 8, 8);
        ProbeAccount first = ByName(accounts, "Claude"), second = ByName(accounts, "Work");
        Check("the first account reads its own cache",
            Remaining(first, Model.FIVE_HOUR) == 90, (Remaining(first, Model.FIVE_HOUR) ?? -1).ToString());
        Check("the second account reads ITS own cache, same filename and all",
            Remaining(second, Model.FIVE_HOUR) == 30, (Remaining(second, Model.FIVE_HOUR) ?? -1).ToString());
        Check("the weekly windows do not cross either",
            Remaining(first, Model.WEEKLY) == 80 && Remaining(second, Model.WEEKLY) == 20,
            (Remaining(first, Model.WEEKLY) ?? -1) + " / " + (Remaining(second, Model.WEEKLY) ?? -1));

        // A second sweep goes through the memo. The memo is keyed by path, so
        // the second account must still get its own numbers.
        accounts = ClaudeSource.Sweep(Stamp.Now + 8, 8);
        first = ByName(accounts, "Claude"); second = ByName(accounts, "Work");
        Check("the memoized second sweep keeps them apart",
            Remaining(first, Model.FIVE_HOUR) == 90 && Remaining(second, Model.FIVE_HOUR) == 30,
            (Remaining(first, Model.FIVE_HOUR) ?? -1) + " / " + (Remaining(second, Model.FIVE_HOUR) ?? -1));
    }

    // ── nothing changes for one account ─────────────────────────────────────
    static void SingleHomeUnchanged()
    {
        Console.WriteLine();
        Console.WriteLine("== one account still behaves exactly as before ==");
        NewProfile();
        string a = MakeHome(".claude", true);
        WriteBridge(a, 25, 35);

        List<ProbeAccount> accounts = ClaudeSource.Sweep(Stamp.Now + 8, 8);
        Check("one home, one card", accounts.Count == 1, accounts.Count.ToString());
        Check("...still called Claude", accounts[0].Name == "Claude", accounts[0].Name);
        Check("...with its reading intact", Remaining(accounts[0], Model.FIVE_HOUR) == 75,
            (Remaining(accounts[0], Model.FIVE_HOUR) ?? -1).ToString());

        ProbeAccount single = ClaudeSource.Probe(Stamp.Now + 8);
        Check("the single-account entry point reads the same home",
            single.ResetHome == a && Remaining(single, Model.FIVE_HOUR) == 75,
            single.ResetHome + " / " + (Remaining(single, Model.FIVE_HOUR) ?? -1));

        // Desktop-only: no config directory at all, which is still one account.
        NewProfile();
        WriteDesktopHistory(20, 30);
        Check("Installed() is true on a Desktop-only machine", ClaudeSource.Installed(), "");
        accounts = ClaudeSource.Sweep(Stamp.Now + 8, 8);
        Check("Desktop-only still produces exactly one card",
            accounts.Count == 1 && Remaining(accounts[0], Model.FIVE_HOUR) == 80,
            accounts.Count + " / " + (Remaining(accounts[0], Model.FIVE_HOUR) ?? -1));
    }
}
