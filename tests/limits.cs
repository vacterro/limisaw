using System;
using System.Collections.Generic;
using Limisaw;

// The quota engine's rules, where a wrong answer is invisible but wrong on
// screen. Every one of these was a real defect at some point:
//
//  1. Antigravity bills two INDEPENDENT model pools, each with its own weekly
//     AND 5-hour limit. A `disabled` 5-hour bucket is a window the pool really
//     has — dropping it hid the Claude/GPT 5-hour limit entirely, and trusting
//     its `remaining_fraction: 1` showed "100% free" on a pool that refuses work.
//  2. Gating is per pool: a spent Claude/GPT weekly must not zero a Gemini window.
//  3. A window whose own reset time has passed is full without a new probe, and
//     that pass must run BEFORE gating or the gate's zero survives the refill.
//  4. Codex varies its window set by plan; a window is mapped by its reported
//     duration, never by primary/secondary position.
//  5. Claude's /usage text is parsed, never guessed: a line that does not parse
//     is dropped, and a per-model weekly keeps its own key.
//
// Build + run: pwsh .\build.ps1 -Tests   (or see build.ps1 for the csc line)
public static class LimitsTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    // Verbatim shape of `agy -p "/usage" --output-format json`, including the
    // disabled Claude/GPT 5-hour bucket that still claims a full fraction.
    const string AgyJson = @"{""status"":""SUCCESS"",""command"":{""name"":""usage"",""data"":{""groups"":[
      {""name"":""Gemini Models"",""buckets"":[
        {""id"":""gemini-weekly"",""window"":""weekly"",""remaining_fraction"":0.6389,""reset_time"":""2126-09-08T20:06:36Z""},
        {""id"":""gemini-5h"",""window"":""5h"",""remaining_fraction"":0.8411,""reset_time"":""2126-09-03T12:41:31Z""}]},
      {""name"":""Claude and GPT models"",""buckets"":[
        {""id"":""3p-weekly"",""window"":""weekly"",""remaining_fraction"":0,""reset_time"":""2126-09-04T16:16:03Z""},
        {""id"":""3p-5h"",""window"":""5h"",""disabled"":true,""remaining_fraction"":1}]}]}}}";

    static AntigravitySource.Row Row(List<AntigravitySource.Row> rows, string key, string group)
    {
        foreach (AntigravitySource.Row r in rows)
            if (r.Key == key && r.Group == group) return r;
        return null;
    }

    static ProbeWindow Win(List<ProbeWindow> windows, string key)
    {
        foreach (ProbeWindow w in windows) if (w.Key == key) return w;
        return null;
    }

    static WindowData Flat(AccountData acc, string key)
    {
        foreach (WindowData w in acc.Windows) if (w.Key == key) return w;
        return null;
    }

    static string Keys(List<ProbeWindow> windows)
    {
        var names = new List<string>();
        foreach (ProbeWindow w in windows) names.Add(w.Key);
        names.Sort(StringComparer.Ordinal);
        return string.Join(", ", names.ToArray());
    }

    public static int Main()
    {
        try
        {
            Antigravity();
            Gating();
            Resets();
            Codex();
            Claude();
            Clis();
            Reasons();
            Console.WriteLine("---");
            Console.WriteLine(checks + " checks");
            Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
            return fails == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  harness");
            Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // A probe failure the user cannot read is a probe failure the user cannot
    // fix. Two halves, both of which were broken (T-006): the vendor's own
    // reason has to survive the provider, and the card has to draw it.
    static void Reasons()
    {
        Console.WriteLine("== a failure says WHY, in the vendor's own words ==");

        // The CLI is the primary source and the only one that can say "Not
        // logged in". "has not supplied rate limits yet" reads as "nothing has
        // run yet" and sends the user to wait instead of to re-auth.
        Check("the Claude CLI's own reason outranks the generic wording",
            ClaudeSource.Summary("claude /usage: Not logged in", null)
                == "claude /usage: Not logged in", ClaudeSource.Summary("claude /usage: Not logged in", null));
        Check("...and outranks the optional status-line cache's complaint",
            ClaudeSource.Summary("claude /usage: Not logged in", "connect Claude Code in Clock settings")
                == "claude /usage: Not logged in", "");
        Check("the bridge speaks only when the CLI had nothing to say",
            ClaudeSource.Summary(null, "connect Claude Code in Clock settings")
                == "connect Claude Code in Clock settings", "");
        Check("with no source at all the wording stays honest, not blank",
            ClaudeSource.Summary(null, null) == "Claude has not supplied rate limits yet",
            ClaudeSource.Summary(null, null));
        Check("an empty reason is treated as no reason, never printed as one",
            ClaudeSource.Summary("", "") == "Claude has not supplied rate limits yet", "");

        // Telling a user to install what they already have is worse than saying
        // nothing: it hides the actionable failure behind advice already taken.
        Check("a refusing Antigravity CLI is not told to install itself",
            AntigravitySource.NoQuotaSummary("agy /usage: not authenticated")
                == "agy /usage: not authenticated", "");
        Check("a missing Antigravity CLI still gets the install advice",
            AntigravitySource.NoQuotaSummary(null).IndexOf("install the Antigravity CLI",
                StringComparison.Ordinal) >= 0, AntigravitySource.NoQuotaSummary(null));
        // Quiet draws muted "idle:", which is right for a vendor that is idle BY
        // DESIGN and wrong for one that just refused work.
        Check("a refusing CLI is loud, not idle",
            !Model.Quiet(AntigravitySource.NoQuotaCode("agy /usage: not authenticated")),
            AntigravitySource.NoQuotaCode("agy /usage: not authenticated"));
        Check("no CLI at all stays quiet — nothing to report is Antigravity's resting state",
            Model.Quiet(AntigravitySource.NoQuotaCode(null)),
            AntigravitySource.NoQuotaCode(null));

        // End-to-end: a real child process that exits nonzero, and its first
        // stderr line arriving as the reason. Without this the string plumbing
        // above could be perfect over a value nothing ever produces.
        string cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        Cli.Result bad = Cli.Run(cmd,
            new[] { "/c", "echo Not logged in 1>&2 & exit /b 1" }, Stamp.Now + 20, null);
        Check("a CLI that exits nonzero yields its first stderr line",
            !bad.Ok && bad.Error == "Not logged in", "ok=" + bad.Ok + " error=" + bad.Error);
        Cli.Result good = Cli.Run(cmd, new[] { "/c", "echo fine" }, Stamp.Now + 20, null);
        Check("a CLI that succeeds reports no error (the gate is not stuck red)",
            good.Ok && good.Error.Length == 0, "ok=" + good.Ok + " error=" + good.Error);

        // The surface half. A failed sweep leaves unavailable windows behind, so
        // a reason drawn only for an EMPTY window list is a reason no user ever
        // sees — measured on 1762f03: codex/Codex ERROR, 2 windows, reason
        // "Codex CLI not found on PATH", nothing on the card.
        var failed = new AccountData
        {
            Provider = "codex", ProviderLabel = "Codex", Name = "Codex",
            Status = Model.ERROR, Ok = false, Error = "Codex CLI not found on PATH",
        };
        failed.Windows.Add(new WindowData { Key = Model.FIVE_HOUR, Available = false });
        failed.Windows.Add(new WindowData { Key = Model.WEEKLY, Available = false });
        bool loud;
        string note = LimisawForm.CardNote(failed, out loud);
        Check("a card with unavailable windows still shows the reason",
            note == "ERROR: Codex CLI not found on PATH", note ?? "null");
        Check("...and it is drawn as a fault", loud, "bad=" + loud);
        Check("the card reserves a line for it, so the last window cannot clip",
            LimisawForm.CardLines(failed) == 3, LimisawForm.CardLines(failed) + " lines");

        var idle = new AccountData
        {
            Provider = "antigravity", ProviderLabel = "Antigravity", Name = "Antigravity",
            Status = Model.UNAVAILABLE, Ok = false, Quiet = true,
            Error = "install the Antigravity CLI for exact quota",
        };
        idle.Windows.Add(new WindowData { Key = "quota", Available = false });
        string idleNote = LimisawForm.CardNote(idle, out loud);
        Check("an idle-by-design provider reads idle, not ERROR",
            idleNote != null && idleNote.StartsWith("idle: ", StringComparison.Ordinal) && !loud,
            (idleNote ?? "null") + " bad=" + loud);

        // A carried card's note already holds the failure text (CarryForward
        // copies Error into CarriedNote), so printing both would say it twice.
        var carried = new AccountData
        {
            Provider = "claude", ProviderLabel = "Claude Code", Name = "Claude",
            Status = Model.ERROR, Ok = false, Carried = true,
            CarriedAt = "12:00:00", CarriedNote = "claude /usage: timeout",
            Error = "claude /usage: timeout",
        };
        carried.Windows.Add(new WindowData { Key = Model.FIVE_HOUR, Available = true, Rem = 40 });
        string carriedNote = LimisawForm.CardNote(carried, out loud);
        Check("a carried card says stale once, not stale AND error",
            carriedNote == "stale: claude /usage: timeout" && !loud, carriedNote ?? "null");

        var healthy = new AccountData
        {
            Provider = "codex", ProviderLabel = "Codex", Name = "Codex",
            Status = Model.OK, Ok = true,
        };
        healthy.Windows.Add(new WindowData { Key = Model.FIVE_HOUR, Available = true, Rem = 80 });
        string none = LimisawForm.CardNote(healthy, out loud);
        Check("a healthy card carries no line at all",
            none == null && LimisawForm.CardLines(healthy) == 1, none ?? "null");
    }

    static void Antigravity()
    {
        Console.WriteLine("== Antigravity: two pools, four windows ==");
        List<AntigravitySource.Row> rows =
            AntigravitySource.ParseUsagePayload(J.Parse(AgyJson));
        Check("every pool reports both of its windows", rows.Count == 4,
            rows.Count + " rows");

        AntigravitySource.Row gem5 = Row(rows, "five_hour", "gemini_models");
        AntigravitySource.Row gemWeek = Row(rows, "weekly", "gemini_models");
        AntigravitySource.Row tp5 = Row(rows, "five_hour", "claude_and_gpt_models");
        AntigravitySource.Row tpWeek = Row(rows, "weekly", "claude_and_gpt_models");

        Check("the Claude/GPT pool has a 5-hour window at all", tp5 != null, "");
        Check("a disabled bucket is flagged, not trusted",
            tp5 != null && tp5.Disabled && !tp5.Remaining.HasValue,
            tp5 == null ? "missing" : "disabled=" + tp5.Disabled + " remaining=" + tp5.Remaining);
        Check("a live bucket keeps the vendor number",
            gem5 != null && gem5.Remaining.HasValue && Math.Abs(gem5.Remaining.Value - 84.11) < 0.01,
            gem5 == null ? "missing" : "gemini 5h remaining=" + gem5.Remaining);
        Check("a spent bucket is zero, not missing",
            tpWeek != null && tpWeek.Remaining.HasValue && tpWeek.Remaining.Value == 0.0, "");
        Check("the reset stamp is parsed, not dropped",
            gemWeek != null && gemWeek.ResetEpoch.HasValue, "");
        Check("a disabled bucket carries no reset it does not have",
            tp5 != null && !tp5.ResetEpoch.HasValue, "");

        List<ProbeWindow> windows = AntigravitySource.WindowsFrom(rows, "test");
        Check("the snapshot carries all four windows, pool-qualified",
            Keys(windows) == "five_hour@claude_and_gpt_models, five_hour@gemini_models, "
                + "weekly@claude_and_gpt_models, weekly@gemini_models", Keys(windows));

        // now is BEFORE every reset stamp above, so nothing is refilled here.
        double now = 4.5e9;
        List<ProbeWindow> resolved = Model.Resolve(windows, now);
        ProbeWindow r3p5 = Win(resolved, "five_hour@claude_and_gpt_models");
        ProbeWindow rGem5 = Win(resolved, "five_hour@gemini_models");
        ProbeWindow rGemWeek = Win(resolved, "weekly@gemini_models");

        Check("a disabled 5h window whose pool weekly is spent reads 0%, not \"--\"",
            r3p5 != null && r3p5.Available && r3p5.Remaining == 0.0,
            r3p5 == null ? "missing" : "available=" + r3p5.Available + " remaining=" + r3p5.Remaining);
        Check("and it says WHICH window locked it",
            r3p5 != null && r3p5.GatedBy == "weekly@claude_and_gpt_models",
            r3p5 == null ? "missing" : "gated_by=" + r3p5.GatedBy);
        Check("a spent pool does not zero the other pool",
            rGem5 != null && Math.Abs((rGem5.Remaining ?? -1) - 84.11) < 0.01 && rGem5.GatedBy == null,
            rGem5 == null ? "missing" : "gemini 5h remaining=" + rGem5.Remaining
                + " gated_by=" + (rGem5.GatedBy ?? "none"));
        Check("the untouched pool keeps its weekly number too",
            rGemWeek != null && Math.Abs((rGemWeek.Remaining ?? -1) - 63.89) < 0.01,
            rGemWeek == null ? "missing" : "gemini weekly=" + rGemWeek.Remaining);

        // Same payload with the Claude/GPT weekly NOT spent: there is nothing to
        // infer for the disabled bucket, so it must stay unavailable.
        tpWeek.Remaining = 55.0;
        List<ProbeWindow> lively = Model.Resolve(AntigravitySource.WindowsFrom(rows, "test"), now);
        ProbeWindow stillDisabled = Win(lively, "five_hour@claude_and_gpt_models");
        Check("a disabled window with quota left in its pool stays \"--\", not 0%",
            stillDisabled != null && !stillDisabled.Available && !stillDisabled.Remaining.HasValue,
            stillDisabled == null ? "missing"
                : "available=" + stillDisabled.Available + " remaining=" + stillDisabled.Remaining);
    }

    static ProbeWindow Make(string key, string group, double? remaining,
                            double? reset, int? minutes)
    {
        return new ProbeWindow
        {
            Key = Model.Qualified(key, group), Group = group ?? "",
            Available = remaining.HasValue, Remaining = remaining,
            ResetEpoch = reset, DurationMinutes = minutes,
        };
    }

    static void Gating()
    {
        Console.WriteLine("== gating: longer over shorter, inside one pool ==");
        double now = 4.5e9;

        var single = new List<ProbeWindow> {
            Make("five_hour", "", 90.0, now + 3600, 300),
            Make("weekly", "", 0.0, now + 86400, 10080),
        };
        List<ProbeWindow> gated = Model.Resolve(single, now);
        Check("a spent weekly zeroes the 5h window in the same account",
            Win(gated, "five_hour").Remaining == 0.0
            && Win(gated, "five_hour").GatedBy == "weekly", "");
        Check("the spent weekly is not gated by itself",
            Win(gated, "weekly").GatedBy == null, "");

        var reversed = new List<ProbeWindow> {
            Make("five_hour", "", 0.0, now + 3600, 300),
            Make("weekly", "", 80.0, now + 86400, 10080),
        };
        List<ProbeWindow> notGated = Model.Resolve(reversed, now);
        Check("a spent 5h window never gates the weekly above it",
            Win(notGated, "weekly").Remaining == 80.0
            && Win(notGated, "weekly").GatedBy == null, "");

        var unavailable = new List<ProbeWindow> {
            ProbeWindow.Unavailable("five_hour"),
            Make("weekly", "", 0.0, now + 86400, 10080),
        };
        Check("an unavailable window is not turned into a gated zero",
            !Win(Model.Resolve(unavailable, now), "five_hour").Available, "");

        // 0.4% left is spent; 0.6% is not. The threshold is what keeps an
        // almost-empty window from reading as usable quota.
        var almost = new List<ProbeWindow> {
            Make("five_hour", "", 50.0, now + 3600, 300),
            Make("weekly", "", 0.4, now + 86400, 10080),
        };
        Check("0.4% left counts as exhausted and gates",
            Win(Model.Resolve(almost, now), "five_hour").GatedBy == "weekly", "");
        almost[1] = Make("weekly", "", 0.6, now + 86400, 10080);
        Check("0.6% left does not gate",
            Win(Model.Resolve(almost, now), "five_hour").GatedBy == null, "");
    }

    static void Resets()
    {
        Console.WriteLine("== elapsed resets run before gating ==");
        double now = 4.5e9;

        var elapsed = new List<ProbeWindow> {
            Make("five_hour", "", 3.0, now - 60, 300),
            Make("weekly", "", 40.0, now + 86400, 10080),
        };
        ProbeWindow refilled = Win(Model.Resolve(elapsed, now), "five_hour");
        Check("a window whose own reset passed is full without a new probe",
            refilled.Remaining == 100.0 && refilled.AssumedFull, "remaining=" + refilled.Remaining);
        Check("and its dead reset stamp is dropped, never rendered as negative",
            !refilled.ResetEpoch.HasValue, "");

        // The order is load-bearing: a weekly whose reset just passed lifts its
        // own gate in the same pass, so the 5h window keeps its real number.
        var lifted = new List<ProbeWindow> {
            Make("five_hour", "", 61.0, now + 3600, 300),
            Make("weekly", "", 0.0, now - 5, 10080),
        };
        List<ProbeWindow> after = Model.Resolve(lifted, now);
        Check("a weekly reset lifts its gate in the same pass",
            Win(after, "five_hour").Remaining == 61.0
            && Win(after, "five_hour").GatedBy == null,
            "5h=" + Win(after, "five_hour").Remaining
            + " gated_by=" + (Win(after, "five_hour").GatedBy ?? "none"));
        Check("the weekly itself reads refilled",
            Win(after, "weekly").Remaining == 100.0, "");

        var future = new List<ProbeWindow> { Make("five_hour", "", 12.0, now + 1, 300) };
        Check("a reset one second away is not treated as passed",
            Win(Model.Resolve(future, now), "five_hour").Remaining == 12.0, "");
    }

    // Verbatim `account/rateLimits/read` result shapes.
    const string CodexPlus = @"{""rateLimits"":{""planType"":""plus"",
        ""primary"":{""usedPercent"":65.0,""windowDurationMins"":300,""resetsAt"":4500003600},
        ""secondary"":{""usedPercent"":10.5,""windowDurationMins"":10080,""resetsAt"":4500086400}}}";
    const string CodexFree = @"{""rateLimits"":{""planType"":""free"",
        ""primary"":{""usedPercent"":20.0,""windowDurationMins"":43200,""resetsAt"":4502592000}}}";
    const string CodexOdd = @"{""rateLimits"":{""planType"":""enterprise"",
        ""primary"":{""usedPercent"":5.0,""windowDurationMins"":1440,""resetsAt"":4500086400}}}";

    static void Codex()
    {
        Console.WriteLine("== Codex: windows mapped by duration, never by position ==");
        string plan;
        List<ProbeWindow> plus = CodexSource.ParseWindows(J.Parse(CodexPlus), out plan);
        Check("a Plus plan reports 5h + weekly", Keys(plus) == "five_hour, weekly", Keys(plus));
        Check("the plan type is carried through", plan == "plus", plan ?? "null");
        Check("usedPercent becomes remaining, not used",
            Win(plus, "five_hour").Remaining == 35.0, "remaining=" + Win(plus, "five_hour").Remaining);
        Check("the shortest window sorts first",
            plus[0].Key == "five_hour", plus[0].Key);

        List<ProbeWindow> free = CodexSource.ParseWindows(J.Parse(CodexFree), out plan);
        Check("a Free plan reports its single 30-day window and no dead pair",
            Keys(free) == "monthly", Keys(free));

        List<ProbeWindow> odd = CodexSource.ParseWindows(J.Parse(CodexOdd), out plan);
        Check("an unrecognised duration keeps a generic key instead of vanishing",
            Keys(odd) == "window_1440m", Keys(odd));

        Check("a payload with no rateLimits yields no invented window",
            CodexSource.ParseWindows(J.Parse("{}"), out plan).Count == 0, "");
        Check("an unusable duration is dropped rather than keyed",
            CodexSource.DurationLabel(0) == null && CodexSource.DurationLabel(null) == null, "");
    }

    // Verbatim `claude -p "/usage"` text (2.1.259).
    const string ClaudeUsage =
        "You are currently using your subscription to power your Claude Code usage\n" +
        "\n" +
        "Current session: 65% used \u00b7 resets Sep 3, 4:49pm (Europe/Tallinn)\n" +
        "Current week (all models): 64% used \u00b7 resets Sep 8, 4:59pm (Europe/Tallinn)\n" +
        "Current week (Sonnet): 12% used \u00b7 resets Sep 8, 4:59pm (Europe/Tallinn)\n" +
        "Some unrelated prose that must not become a window\n";

    static void Claude()
    {
        Console.WriteLine("== Claude: /usage text parsed, never guessed ==");
        double now = Stamp.Now;
        Dictionary<string, ClaudeSource.Slot> windows =
            ClaudeSource.ParseUsageText(ClaudeUsage, now);

        Check("the session line is the 5-hour window",
            windows.ContainsKey("five_hour") && windows["five_hour"].Used == 65.0,
            windows.ContainsKey("five_hour") ? "used=" + windows["five_hour"].Used : "missing");
        Check("the all-models week is the weekly window",
            windows.ContainsKey("weekly") && windows["weekly"].Used == 64.0, "");
        Check("a per-model week keeps its OWN key, never overwriting the weekly",
            windows.ContainsKey("weekly_sonnet") && windows["weekly_sonnet"].Used == 12.0,
            string.Join(", ", new List<string>(windows.Keys).ToArray()));
        Check("prose is not a window", windows.Count == 3, windows.Count + " windows");
        Check("the reset stamp is parsed off the same line",
            windows["five_hour"].ResetEpoch.HasValue, "");

        Check("an unparseable reset leaves the percentage usable",
            ClaudeSource.ParseUsageText("Current session: 5% used \u00b7 resets whenever", now)["five_hour"]
                .ResetEpoch.HasValue == false, "");
        Check("a line with no percentage is dropped",
            ClaudeSource.ParseUsageText("Current session: resets Sep 3, 4:49pm", now).Count == 0, "");

        // "Sep 8, 4:59pm" is local time with the zone printed for the reader.
        double? reset = ClaudeSource.ParseReset("Sep 8, 4:59pm", now);
        Check("a bare month/day/time resolves to a real instant", reset.HasValue,
            reset.HasValue ? Stamp.Iso(reset) : "null");
        Check("an explicit year is honoured",
            Stamp.Iso(ClaudeSource.ParseReset("Sep 3, 2127, 4:49pm", now)) == "2127-09-03T16:49:00",
            Stamp.Iso(ClaudeSource.ParseReset("Sep 3, 2127, 4:49pm", now)) ?? "null");
        Check("midnight-hour am/pm maps correctly",
            Stamp.Iso(ClaudeSource.ParseReset("Sep 3, 2127, 12:30am", now)) == "2127-09-03T00:30:00",
            Stamp.Iso(ClaudeSource.ParseReset("Sep 3, 2127, 12:30am", now)) ?? "null");
        Check("nonsense is refused rather than guessed",
            !ClaudeSource.ParseReset("tomorrow-ish", now).HasValue, "");
    }

    static void Clis()
    {
        Console.WriteLine("== CLI descriptions are data, never actions ==");
        List<CliInfo> clis = Cli.Status();
        Check("all three vendors are described", clis.Count == 3, clis.Count + " tools");
        bool everyOne = true;
        foreach (CliInfo c in clis)
            if (c.Label.Length == 0 || c.Command.Length == 0
                || c.Source.Length == 0 || c.PowerShell.Length == 0) everyOne = false;
        Check("each carries a command, a publisher and a target", everyOne, "");

        CliInfo codex = null;
        foreach (CliInfo c in clis) if (c.Key == "codex") codex = c;
        // OpenAI publishes its command already wrapped in `powershell -c "..."`;
        // the UI must run the inner pipeline, not nest one shell in another.
        Check("a wrapped install command is unwrapped for the shell",
            codex != null && codex.PowerShell == "irm https://chatgpt.com/codex/install.ps1 | iex",
            codex == null ? "missing" : codex.PowerShell);

        Check("epoch parsing accepts seconds, milliseconds and ISO-8601",
            Stamp.Epoch(4500000000.0) == 4500000000.0
            && Stamp.Epoch(4500000000000.0) == 4500000000.0
            && Stamp.Epoch("2126-09-08T20:06:36Z").HasValue
            && !Stamp.Epoch("not a time").HasValue
            && !Stamp.Epoch(true).HasValue, "");
        Check("nine fractional digits parse (the vendors write them)",
            Stamp.Epoch("2126-09-08T20:06:36.123456789Z").HasValue, "");

        var flat = new ProbeAccount { Provider = "x", Name = "y", Ok = true, Status = Model.OK };
        flat.Windows.Add(Make("five_hour", "", 33.4, 4.5e9 + 60, 300));
        AccountData acc = Model.Flatten(flat, 4.5e9);
        Check("a flattened window rounds remaining and keeps its own label",
            Flat(acc, "five_hour").Rem == 33 && Flat(acc, "five_hour").Label == "5h",
            "rem=" + Flat(acc, "five_hour").Rem);
        Check("a pool-qualified key still reports its base kind",
            Model.Base("weekly@gemini_models") == "weekly"
            && Model.Label("weekly@gemini_models") == "week", "");
    }
}
