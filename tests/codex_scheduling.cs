using System;
using System.Collections.Generic;
using System.IO;
using Limisaw;

// PERF-001 (SRC-005:R012): the Codex cold-start budget.
//
// The measured first `codex app-server` cold start is 14-19 s. The old
// scheduler divided whatever provider time remained equally across the
// discovered homes (`remaining / homes.Count`), so a normal multi-provider,
// multi-home machine granted every cold home 4-8 s — below the floor a cold
// initialization can even theoretically answer in — and every home failed
// despite there having been enough global time to initialize at least some of
// them. That is a liveness defect: more accounts made the provider LESS
// likely to work.
//
// The scheduler now knows which homes are warm (SessionPool.IsWarm — pure
// metadata, no process started), serves warm homes first with their cheap
// read, and gives each cold home a FULL viable slice only while the
// provider's own deadline still affords one. A cold home that cannot receive
// a viable slice is returned as a stale slot (never a doomed launch), and a
// bounded round-robin cursor rotates the cold queue across sweeps so the same
// homes do not win forever.
//
// This harness drives the real Sweep with a VIRTUAL clock: the fake sessions
// consume seconds exactly like a 15 s cold start would, deterministically —
// no sleeping. The fake session fails `initialize` whenever the granted slice
// is below the documented floor, which is what the vendor itself does.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main CodexSchedulingTest)
public static class CodexSchedulingTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    // ── the virtual clock ────────────────────────────────────────────────────
    static double VNow;
    static double Clock() { return VNow; }
    static void Advance(double s) { VNow += s; }

    // ── the scripted cold/warm app-server ────────────────────────────────────
    class HomeScript
    {
        public string Home;
        public bool Alive = true;
        public int InitCalls, ReadCalls, Drops;
        // The smallest slice any initialize call of this home was granted.
        public double MinGranted = double.MaxValue;
        public bool AnyGrant;
    }

    static readonly Dictionary<string, HomeScript> Scripts = new Dictionary<string, HomeScript>();
    static int Starts;
    static readonly List<double> ColdGrants = new List<double>();

    static HomeScript Script(string home)
    {
        HomeScript s;
        string key = CodexSource.SessionPool.Key(home);
        if (!Scripts.TryGetValue(key, out s)) { s = new HomeScript { Home = key }; Scripts[key] = s; }
        return s;
    }

    static void InstallFakeFactory()
    {
        CodexSource.ResolveExe = exe => "fake-codex";
        CodexSource.StartSession = (exe, home) =>
        {
            Starts++;
            HomeScript s = Script(home);
            return new CodexSource.RpcLink
            {
                Call = (method, parameters, deadline) =>
                {
                    if (method == "initialize")
                    {
                        s.InitCalls++;
                        double granted = deadline - VNow;
                        if (granted < s.MinGranted) s.MinGranted = granted;
                        s.AnyGrant = true;
                        ColdGrants.Add(granted);
                        Advance(15.0);   // the measured cold start, virtually
                        // A real app-server granted a sub-floor slice cannot
                        // answer: this is the measured behavior the scheduler
                        // must respect instead of gambling on it.
                        return granted >= CodexSource.ColdStartFloorSeconds
                            ? J.Parse("{\"result\":{}}") : null;
                    }
                    if (method == "account/rateLimits/read") { s.ReadCalls++; Advance(0.5); return J.Parse("{\"result\":{}}"); }
                    return J.Parse("{\"result\":{}}");
                },
                Notify = (method, parameters) => { },
                Alive = () => s.Alive,
                Drop = () => s.Drops++,
            };
        };
    }

    // ── scratch profile with discoverable Codex homes ───────────────────────
    static string Scratch;

    static void MakeHome(string profile, string dir)
    {
        string full = Path.Combine(profile, dir);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "auth.json"), "{}");
    }

    static void UseScratchProfile(int siblings)
    {
        Scratch = Path.Combine(Path.GetTempPath(), "limisaw_codexsched_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        Environment.SetEnvironmentVariable("USERPROFILE", Scratch);
        Environment.SetEnvironmentVariable("HOME", Scratch);
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        MakeHome(Scratch, ".codex");
        for (int i = 0; i < siblings; i++)
            MakeHome(Scratch, ".codex-account" + (i + 1));
    }

    static string HomePath(string dir) { return Path.Combine(Scratch, dir); }

    static List<ProbeAccount> Sweep(double budget, double perAccount)
    {
        return CodexSource.Sweep(VNow + budget, perAccount);
    }

    static void FreshCase(int siblings)
    {
        UseScratchProfile(siblings);
        InstallFakeFactory();
        CodexSource.Pool.Reset();
        CodexSource.ResetScheduling();
        CodexSource.Clock = Clock;
        Scripts.Clear(); ColdGrants.Clear(); Starts = 0;
        VNow = 1000.0;
    }

    public static int Main()
    {
        Console.WriteLine("== A: one cold home, enough budget -> a viable slice and success ==");
        FreshCase(0);
        List<ProbeAccount> one = Sweep(30.0, 26.0);
        HomeScript a = Script(HomePath(".codex"));
        Check("the cold home was probed", one.Count == 1 && one[0].Ok, one.Count + " accounts");
        Check("...with a grant at or above the documented floor",
            a.AnyGrant && a.MinGranted >= CodexSource.ColdStartFloorSeconds,
            "granted=" + (a.AnyGrant ? a.MinGranted.ToString("0.0") : "none"));
        Check("...and it initialized successfully", a.InitCalls == 1 && a.ReadCalls == 1, "");
        Check("...never exceeding the provider deadline",
            ColdGrants.Count > 0 && VNow <= 1000.0 + 30.0 + 0.001, "vnow drift " + (VNow - 1030));

        Console.WriteLine();
        Console.WriteLine("== B: three cold homes, 30 s -> useful attempts, no doomed launches ==");
        FreshCase(2);
        Scripts.Clear(); ColdGrants.Clear(); Starts = 0;
        List<ProbeAccount> three = Sweep(30.0, 26.0);
        int doomed = 0, viable = 0;
        foreach (double g in ColdGrants) if (g < CodexSource.ColdStartFloorSeconds) doomed++; else viable++;
        Check("no cold attempt was launched below the viable floor", doomed == 0,
            doomed + " sub-floor launches");
        Check("the budget produced exactly the two viable starts it could carry",
            viable == 2, viable + " viable of " + ColdGrants.Count + " launched");
        Check("...with exactly two process starts", Starts == 2, "starts=" + Starts);
        int staleSlots = 0;
        foreach (ProbeAccount acc in three)
            if (acc.Status == Model.STALE && !acc.Ok) staleSlots++;
        Check("the home that could not receive a viable slice is a stale slot",
            staleSlots == 1, staleSlots + " stale slots of " + three.Count);
        Check("...its error says the budget, not the vendor, failed",
            three.Exists(acc => (acc.Error ?? "").IndexOf("cold start", StringComparison.Ordinal) >= 0), "");

        Console.WriteLine();
        Console.WriteLine("== C: the underfunded home stays listed (never disappears) ==");
        bool listed = false;
        foreach (ProbeAccount acc in three)
            if (acc.SourceId != null && acc.SourceId.Length > 0 && acc.Status == Model.STALE) listed = true;
        Check("every discovered home is still represented", three.Count == 3 && listed,
            three.Count + " accounts");

        Console.WriteLine();
        Console.WriteLine("== D: a warm pooled home is cheap and takes no cold reservation ==");
        FreshCase(1);   // .codex + .codex-account1, both cold
        Scripts.Clear(); ColdGrants.Clear(); Starts = 0;
        Sweep(66.0, 16.5);           // warms both (16.5 >= 14 viable)
        HomeScript w = Script(HomePath(".codex"));
        Check("the first sweep warmed the home", w.InitCalls == 1, "inits=" + w.InitCalls);
        Script(HomePath(".codex-account1")).Alive = false;   // its child died -> cold
        Starts = 0; ColdGrants.Clear();
        // A budget that could not carry any cold start: the warm home must
        // still read, and the cold home must NOT be handed a doomed launch.
        List<ProbeAccount> tight = Sweep(4.0, 26.0);
        Check("the warm home needed no new start", Starts == 0, "starts=" + Starts);
        Check("...and no re-initialization", Script(HomePath(".codex")).InitCalls == 1,
            "inits=" + Script(HomePath(".codex")).InitCalls);
        Check("...its read fit inside the tiny budget", tight.Count == 2 && tight[0].Ok,
            "ok=" + (tight.Count > 0 && tight[0].Ok));
        Check("the cold home received no doomed launch on the tight budget",
            ColdGrants.Count == 0, ColdGrants.Count + " launches");
        Check("...and is reported stale, not failed",
            tight.Count == 2 && !tight[1].Ok && tight[1].Status == Model.STALE, "");

        Console.WriteLine();
        Console.WriteLine("== E: mixed warm+cold — warm responsive, cold viable ==");
        FreshCase(1);
        Sweep(66.0, 16.5);           // both warm
        Script(HomePath(".codex-account1")).Alive = false;   // its child died -> cold again
        Starts = 0; ColdGrants.Clear();
        List<ProbeAccount> mixed = Sweep(30.0, 16.5);
        Check("the warm home still read", mixed.Count == 2 && mixed[0].Ok, "ok=" + (mixed.Count > 0 && mixed[0].Ok));
        Check("the dead home's replacement got a viable grant",
            ColdGrants.Count == 1 && ColdGrants[0] >= CodexSource.ColdStartFloorSeconds,
            "granted=" + (ColdGrants.Count > 0 ? ColdGrants[0].ToString("0.0") : "none"));
        Check("...both accounts answered", mixed.TrueForAll(acc => acc.Ok), "");

        Console.WriteLine();
        Console.WriteLine("== F: repeated sweeps rotate every persistent cold home in ==");
        FreshCase(4);   // .codex + 4 siblings = 5 cold homes
        Scripts.Clear(); ColdGrants.Clear(); Starts = 0;
        // Budget carries exactly two viable starts per sweep (2 x 15.5 = 31).
        for (int sweep = 0; sweep < 3; sweep++) Sweep(33.0, 26.0);
        bool every = true;
        foreach (string dir in new[] { ".codex", ".codex-account1", ".codex-account2", ".codex-account3", ".codex-account4" })
            if (Script(HomePath(dir)).InitCalls < 1) every = false;
        Check("every persistent cold home received a viable attempt within three sweeps", every, "");
        Check("...with no sub-floor launch anywhere", ColdGrants.TrueForAll(g => g >= CodexSource.ColdStartFloorSeconds),
            ColdGrants.Count + " launches");

        Console.WriteLine();
        Console.WriteLine("== G: later providers keep their global opportunity ==");
        string agy = File.ReadAllText(Path.Combine(SourceRoot(), "ProbeAntigravity.cs"));
        Check("the provider schedule is untouched (66 s budget, cumulative slots)",
            agy.IndexOf("double claudeEnd = codexEnd + (claudeOn ? share : 0);", StringComparison.Ordinal) >= 0
            && Probe.TotalBudgetS == 66.0, "budget=" + Probe.TotalBudgetS);
        FreshCase(2);
        Scripts.Clear(); ColdGrants.Clear(); Starts = 0;
        double providerEnd = VNow + 16.5;   // the 4-provider Codex share
        CodexSource.Sweep(providerEnd, 16.5);
        Check("a cold start was capped at the provider deadline, never past it",
            ColdGrants.TrueForAll(g => g <= 16.5 + 0.001),
            ColdGrants.Count > 0 ? "max grant " + ColdGrants[0].ToString("0.0") : "none");
        Check("...the sweep never ran past the provider's own slot",
            VNow <= providerEnd + 0.001, "vnow=" + VNow.ToString("0.0") + " end=" + providerEnd.ToString("0.0"));

        Console.WriteLine();
        Console.WriteLine("== H: five active providers still afford a viable Codex cold start ==");
        // The starvation this pins: 66 / 5 = 13.2 s is BELOW the 14 s cold-start
        // floor, so every cold Codex home was returned `cold_start_underfunded`
        // on every sweep and could never warm. The slot floor keeps it viable.
        Check("the per-provider slot never drops below the cold-start floor",
            Probe.ProviderShare(5) >= CodexSource.ColdStartFloorSeconds
            && Probe.ProviderShare(6) >= CodexSource.ColdStartFloorSeconds,
            "5 providers -> " + Probe.ProviderShare(5).ToString("0.0") + "s, 6 -> "
            + Probe.ProviderShare(6).ToString("0.0") + "s, floor=" + CodexSource.ColdStartFloorSeconds.ToString("0.0") + "s");
        Check("...and four or fewer providers keep the original 16.5 s share",
            Math.Abs(Probe.ProviderShare(4) - 16.5) < 1e-9
            && Math.Abs(Probe.ProviderShare(3) - 22.0) < 1e-9,
            "4 -> " + Probe.ProviderShare(4).ToString("0.0") + "s, 3 -> " + Probe.ProviderShare(3).ToString("0.0") + "s");
        // Drive the live schedule for a cold home under the widened 5-provider
        // slot: Codex owns the first slot and must be granted at or above the
        // floor instead of starving.
        FreshCase(0);
        Scripts.Clear(); ColdGrants.Clear(); Starts = 0;
        double fiveShare = Probe.ProviderShare(5);
        List<ProbeAccount> five = CodexSource.Sweep(VNow + fiveShare, fiveShare);
        Check("the first cold home gets a viable grant under the widened slot",
            ColdGrants.Count > 0 && ColdGrants.TrueForAll(g => g >= CodexSource.ColdStartFloorSeconds),
            ColdGrants.Count > 0 ? "granted " + ColdGrants[0].ToString("0.0") : "no launch");
        Check("...and it initializes instead of starving",
            Script(HomePath(".codex")).InitCalls == 1 && five.Count == 1 && five[0].Ok,
            "inits=" + Script(HomePath(".codex")).InitCalls);

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    static string SourceRoot()
    {
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) return dir;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
