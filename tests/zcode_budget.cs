using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Limisaw;

// W2-002: Zcode's two hosts share ONE account deadline, and the fallback host
// must survive a first host that hangs.
//
// The defect: the loop handed the ENTIRE unchanged account deadline to the
// current host and only checked the clock BEFORE each host. So an unreachable
// api.z.ai could legally consume all 26s of the Zcode budget, and control
// returned to the loop at or past the deadline — the second constant host was
// never contacted. `open.bigmodel.cn` was a fallback for FAST failures only,
// while the slow case that actually needs a fallback skipped it.
//
// These checks drive the real ZcodeSource.Probe through its request seam
// (`ZcodeSource.Transport`), so the host order, the per-host slice and the
// account deadline are observed as production computes them — no socket, no
// live vendor. Deadlines here are seconds, not the production 26, so the suite
// stays fast; the arithmetic under test is the same.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class ZcodeBudgetTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const string Quota = @"{""code"":200,""data"":{""level"":""lite"",""limits"":[
      {""type"":""CREDIT_LIMIT"",""unit"":3,""number"":5,""usage"":2000,""remaining"":1000,""nextResetTime"":4102444800000},
      {""type"":""CREDIT_LIMIT"",""unit"":6,""number"":1,""usage"":10000,""remaining"":9000,""nextResetTime"":4102444800000}]}}";

    // One recorded attempt: which host, how much time it was given, and how
    // much it actually took.
    class Attempt
    {
        public string Url;
        public double Granted;   // seconds the transport was allowed
        public double Spent;     // seconds it really used
    }

    // A scripted transport. `plan` maps a host to what it does: "ok" answers
    // quota, "hang" blocks until its own granted deadline, "401" fails fast,
    // "envelope" returns HTTP 200 carrying a vendor-level error.
    class Fake
    {
        public readonly List<Attempt> Calls = new List<Attempt>();
        readonly Dictionary<string, string> Plan;

        public Fake(Dictionary<string, string> plan) { Plan = plan; }

        public string Fetch(string url, string key, double deadline, out string error)
        {
            error = null;
            double start = Stamp.Now;
            var call = new Attempt { Url = url, Granted = deadline - start };
            Calls.Add(call);
            string behaviour = "hang";
            foreach (var pair in Plan)
                if (url.StartsWith(pair.Key, StringComparison.Ordinal)) behaviour = pair.Value;
            try
            {
                if (behaviour == "ok") return Quota;
                if (behaviour == "401") { error = "HTTP 401"; return null; }
                if (behaviour == "envelope") return @"{""code"":401,""msg"":""token expired""}";
                // hang: burn exactly the slice it was granted, the way a dead
                // host does, then report the timeout production would report.
                while (Stamp.Now < deadline) Thread.Sleep(5);
                error = "Timeout";
                return null;
            }
            finally { call.Spent = Stamp.Now - start; }
        }

        public string FirstHost { get { return Calls.Count > 0 ? Host(Calls[0].Url) : "(none)"; } }
        public bool Touched(string host)
        {
            foreach (Attempt call in Calls)
                if (call.Url.StartsWith(host, StringComparison.Ordinal)) return true;
            return false;
        }
        public static string Host(string url)
        {
            int at = url.IndexOf(ZcodeSource.QuotaPath, StringComparison.Ordinal);
            return at > 0 ? url.Substring(0, at) : url;
        }
    }

    static Fake Install(Dictionary<string, string> plan)
    {
        var fake = new Fake(plan);
        ZcodeSource.Transport = fake.Fetch;
        return fake;
    }

    static Dictionary<string, string> Plan(string zai, string bigmodel)
    {
        return new Dictionary<string, string>
        {
            { ZcodeSource.HostZai, zai },
            { ZcodeSource.HostBigModel, bigmodel },
        };
    }

    public static int Main()
    {
        string savedPrimary = Environment.GetEnvironmentVariable(ZcodeSource.EnvPrimary);
        string savedAlternate = Environment.GetEnvironmentVariable(ZcodeSource.EnvAlternate);
        string savedProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            Environment.SetEnvironmentVariable(ZcodeSource.EnvAlternate, null);
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, "env-key-budget");
            Budget();
            HostHint();
            NoKey();
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, savedPrimary);
            Environment.SetEnvironmentVariable(ZcodeSource.EnvAlternate, savedAlternate);
            Environment.SetEnvironmentVariable("USERPROFILE", savedProfile);
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 1 - 1 : 1;
    }

    static void Budget()
    {
        Console.WriteLine("== a hanging first host must not spend the fallback's budget ==");

        // The defect's exact shape: host 1 unreachable, host 2 perfectly fine.
        Fake fake = Install(Plan("hang", "ok"));
        double budget = 2.0;
        double start = Stamp.Now;
        ProbeAccount acc = ZcodeSource.Probe(start + budget, false);
        double elapsed = Stamp.Now - start;

        Check("a dead first host does not stop the second from being asked",
            fake.Touched(ZcodeSource.HostBigModel), "hosts asked: " + fake.Calls.Count);
        Check("...and the quota comes back from the fallback host",
            acc.Ok && acc.Status == Model.OK, acc.Status + " " + (acc.Error ?? ""));
        Check("...with both windows parsed",
            acc.Windows.Count == 2 && acc.Windows[0].Key == Model.FIVE_HOUR
            && acc.Windows[1].Key == Model.WEEKLY, acc.Windows.Count + " windows");
        Check("...and the plan tag survives", acc.Plan == "Lite", acc.Plan ?? "null");
        Check("the hanging host was granted only its SHARE, not the whole budget",
            fake.Calls[0].Granted > 0.2 && fake.Calls[0].Granted <= budget * 0.6,
            fake.Calls[0].Granted.ToString("0.00") + "s of " + budget.ToString("0.00") + "s");
        Check("...and it really did burn that share (the slice is enforced, not advisory)",
            fake.Calls[0].Spent >= fake.Calls[0].Granted - 0.15,
            "spent " + fake.Calls[0].Spent.ToString("0.00") + "s");
        Check("the whole probe still finished inside the account deadline",
            elapsed <= budget + 0.6, elapsed.ToString("0.00") + "s of " + budget.ToString("0.00") + "s");

        // A first host that answers must not cost a second request: the key
        // would travel to a host that never needed to see it.
        fake = Install(Plan("ok", "hang"));
        acc = ZcodeSource.Probe(Stamp.Now + 2.0, false);
        Check("a first host that answers means the second is never contacted",
            acc.Ok && fake.Calls.Count == 1 && !fake.Touched(ZcodeSource.HostBigModel),
            fake.Calls.Count + " request(s)");

        // Fast failure: the fallback should inherit essentially the whole
        // remainder, not a pre-carved half.
        fake = Install(Plan("401", "ok"));
        budget = 2.0;
        start = Stamp.Now;
        acc = ZcodeSource.Probe(start + budget, false);
        Check("a fast 401 on the first host still ends in success on the second",
            acc.Ok && fake.Calls.Count == 2, acc.Status + ", " + fake.Calls.Count + " request(s)");
        Check("...and the fallback inherits the whole remaining budget, not a half",
            fake.Calls[1].Granted > budget * 0.8,
            fake.Calls[1].Granted.ToString("0.00") + "s of " + budget.ToString("0.00") + "s");

        // A 200 carrying `code: 401` is a vendor-level failure, and the
        // fallback must apply there too.
        fake = Install(Plan("envelope", "ok"));
        acc = ZcodeSource.Probe(Stamp.Now + 2.0, false);
        Check("a 200 with an error envelope also falls through to the second host",
            acc.Ok && fake.Calls.Count == 2, acc.Status + ", " + fake.Calls.Count + " request(s)");

        // Both dead: one bounded error, inside the budget, and never the key.
        fake = Install(Plan("hang", "hang"));
        budget = 2.0;
        start = Stamp.Now;
        acc = ZcodeSource.Probe(start + budget, false);
        elapsed = Stamp.Now - start;
        Check("both hosts hanging: both were still attempted",
            fake.Calls.Count == 2, fake.Calls.Count + " request(s)");
        Check("...and the account budget is not exceeded",
            elapsed <= budget + 0.6, elapsed.ToString("0.00") + "s of " + budget.ToString("0.00") + "s");
        Check("...and the card carries ONE bounded error, not a hang",
            acc.Status == Model.ERROR && !acc.Ok
            && !string.IsNullOrEmpty(acc.Error), acc.Status + ": " + (acc.Error ?? "null"));
        Check("...with both windows marked unavailable",
            acc.Windows.Count == 2 && !acc.Windows[0].Available && !acc.Windows[1].Available,
            acc.Windows.Count + " windows");
        Check("...and the key never appears in the error text",
            acc.Error.IndexOf("env-key-budget", StringComparison.Ordinal) < 0, acc.Error);

        // A deadline already gone must not open a socket at all.
        fake = Install(Plan("ok", "ok"));
        acc = ZcodeSource.Probe(Stamp.Now - 1.0, false);
        Check("an expired deadline asks no host at all",
            fake.Calls.Count == 0 && acc.Status == Model.ERROR, fake.Calls.Count + " request(s)");
    }

    static void HostHint()
    {
        Console.WriteLine();
        Console.WriteLine("== the config's provider id decides which host is tried first ==");

        // No env key: the key comes from a scratch config, whose provider id is
        // the only clue about which host owns it.
        Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, null);
        Environment.SetEnvironmentVariable(ZcodeSource.EnvAlternate, null);
        string profile = Path.Combine(Path.GetTempPath(), "zcode_budget_" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(profile, ".zcode", "v2");
        Directory.CreateDirectory(dir);
        string config = Path.Combine(dir, "config.json");
        Environment.SetEnvironmentVariable("USERPROFILE", profile);
        try
        {
            File.WriteAllText(config,
                @"{""provider"":{""builtin:bigmodel-coding-plan"":{""options"":{""apiKey"":""bm-key""}}}}");
            string providerId;
            Check("the config reports WHICH provider the key came from",
                ZcodeSource.FromConfig(config, out providerId) == "bm-key"
                && providerId == "builtin:bigmodel-coding-plan", providerId ?? "null");
            Check("...and the old single-argument reader still answers the same key",
                ZcodeSource.FromConfig(config) == "bm-key", ZcodeSource.FromConfig(config) ?? "null");
            Check("a BigModel key is reported with its own host",
                ZcodeSource.Resolve(true).Host == ZcodeSource.HostBigModel,
                ZcodeSource.Resolve(true).Host ?? "null");

            Fake fake = Install(Plan("ok", "ok"));
            ProbeAccount acc = ZcodeSource.Probe(Stamp.Now + 2.0, true);
            Check("...and that host is asked FIRST, so the budget starts where the key lives",
                acc.Ok && fake.Calls.Count == 1 && fake.FirstHost == ZcodeSource.HostBigModel,
                fake.FirstHost);

            // The hint is order only. A wrong guess must still fall back.
            fake = Install(Plan("ok", "hang"));
            double budget = 2.0;
            double start = Stamp.Now;
            acc = ZcodeSource.Probe(start + budget, true);
            Check("a hinted host that hangs still falls back to the other one",
                acc.Ok && fake.Calls.Count == 2 && fake.FirstHost == ZcodeSource.HostBigModel,
                fake.Calls.Count + " request(s), first " + fake.FirstHost);
            Check("...within the account budget",
                Stamp.Now - start <= budget + 0.6, (Stamp.Now - start).ToString("0.00") + "s");

            File.WriteAllText(config,
                @"{""provider"":{""builtin:zai"":{""options"":{""apiKey"":""zai-key""}}}}");
            Check("a Z.ai key hints the Z.ai host",
                ZcodeSource.Resolve(true).Host == ZcodeSource.HostZai,
                ZcodeSource.Resolve(true).Host ?? "null");
            fake = Install(Plan("ok", "ok"));
            acc = ZcodeSource.Probe(Stamp.Now + 2.0, true);
            Check("...and it is asked first",
                acc.Ok && fake.FirstHost == ZcodeSource.HostZai, fake.FirstHost);

            // An env key says nothing about the host, so the default order
            // stands rather than a guess.
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, "env-key-budget");
            Check("an environment key claims no host",
                ZcodeSource.Resolve(true).Host == null,
                ZcodeSource.Resolve(true).Host ?? "null");
            fake = Install(Plan("ok", "ok"));
            acc = ZcodeSource.Probe(Stamp.Now + 2.0, true);
            Check("...so the default order is used",
                acc.Ok && fake.FirstHost == ZcodeSource.HostZai, fake.FirstHost);
        }
        finally
        {
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    static void NoKey()
    {
        Console.WriteLine();
        Console.WriteLine("== no credential: nothing is sent, and the card stays quiet ==");
        Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, null);
        Environment.SetEnvironmentVariable(ZcodeSource.EnvAlternate, null);
        string profile = Path.Combine(Path.GetTempPath(), "zcode_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Environment.SetEnvironmentVariable("USERPROFILE", profile);
        try
        {
            Fake fake = Install(Plan("ok", "ok"));
            ProbeAccount acc = ZcodeSource.Probe(Stamp.Now + 2.0, false);
            Check("without a key no host is contacted at all",
                fake.Calls.Count == 0, fake.Calls.Count + " request(s)");
            Check("...and the card is a quiet UNAVAILABLE, not a red error",
                acc.Status == Model.UNAVAILABLE && acc.Quiet && !acc.Ok, acc.Status);
        }
        finally
        {
            try { Directory.Delete(profile, true); } catch { }
        }
    }
}
