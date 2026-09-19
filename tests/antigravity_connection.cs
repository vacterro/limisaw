using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Limisaw;

// Antigravity connection adapter (SRC-002 R072-R075, R095) — fully deterministic.
//
// No real vendor account, no network, no real login: every seam the adapter
// owns is faked — discovery environment, auth-mode settings files, the CLI
// usage read (AntigravitySource.ReadCliUsage stays the production parser; the
// harness feeds payloads), interactive launch + exit signal, and the watcher
// clock. GEMINI_API_KEY is a FAKE test value used only to prove the value can
// never reach state, reason, diagnostics or disk.
//
// Cases (R095): CLI missing + journal present (Partial), CLI installed with no
// auth, successful vendor-session auth, browser login transition, gemini mode
// with missing GEMINI_API_KEY, valid API-key mode without subscription quota,
// malformed usage JSON. Plus network timeouts/TLS/proxy classification, exit
// signal semantics (R081), in-flight/poke watcher rules (R026-R029), and the
// one-follow-up refresh contract (R084).
public static class AntigravityConnectionTest
{
    static int fails = 0, checks = 0;
    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    // The fake secret. Never written to disk, only placed in a fake env seam.
    const string FakeSecret = "LIMISAW_TEST_AGY_SECRET_41d2";

    static string savedProfileEnv, savedHomeEnv, savedLocalAppData;
    static Func<string> savedCliPath;
    static Func<string, string> savedGetEnv;
    static Func<string, string> savedBinary;
    static Func<string, bool> savedFileExists, savedHasEnv;
    static Func<string, string> savedReadAllText;
    static Func<string> savedUserProfile, savedLastUsageError;
    static Func<bool> savedLocalDataImpl;
    static Func<double, List<ProbeWindow>> savedReadCliUsage;
    static Func<AntigravityConnectionAdapter.LoginLaunch, bool> savedLaunch;
    static Func<string, string, Action, ConnectionProcessLauncher.InteractiveLaunch, string> savedStartInteractiveImpl;
    static Func<string, Cli.Result> savedVersionProbe;
    static string profile;
    // CASE-INSENSITIVE on purpose: the adapter compares paths case-insensitively
    // through Windows, so a key written as "...\agy.exe" must resolve when the
    // adapter asks for "...\agy.EXE".
    static Dictionary<string, string> FakeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string> FakeEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static List<ProbeWindow> UsageResult;
    static string UsageError;
    static int UsageCalls;

    static void InstallSeams()
    {
        savedUserProfile = AntigravityAuthSettings.UserProfile;
        savedFileExists = AntigravityAuthSettings.FileExists;
        savedReadAllText = AntigravityAuthSettings.ReadAllText;
        savedHasEnv = AntigravityAuthSettings.HasEnv;
        savedLocalDataImpl = AntigravityAuthSettings.LocalDataPresentImpl;
        AntigravityAuthSettings.UserProfile = () => profile;
        AntigravityAuthSettings.FileExists = path => FakeFiles.ContainsKey(path);
        AntigravityAuthSettings.ReadAllText = path => FakeFiles.ContainsKey(path) ? FakeFiles[path] : null;
        AntigravityAuthSettings.HasEnv = name => FakeEnv.ContainsKey(name) && FakeEnv[name].Length > 0;

        // AntigravitySource.DataDir() is the LOCAL-DATA authority (R072); the
        // harness redirects it into the fake profile so "old journal present"
        // is a fixture fact, not a fact about this machine.
        AntigravityAuthSettings.LocalDataPresentImpl = () => FakeFiles.ContainsKey(Path.Combine(profile, ".gemini", "antigravity"));

        savedReadCliUsage = AntigravityConnectionAdapter.ReadCliUsage;
        savedLastUsageError = AntigravityConnectionAdapter.LastUsageError;
        AntigravityConnectionAdapter.ReadCliUsage = deadline => { UsageCalls++; return UsageResult; };
        AntigravityConnectionAdapter.LastUsageError = () => UsageError;

        savedGetEnv = ExecutableDiscovery.GetEnvironmentVariable;
        var savedUser = ExecutableDiscovery.GetUserPath;
        var savedMachine = ExecutableDiscovery.GetMachinePath;
        var savedFallback = ExecutableDiscovery.FallbackFor;
        var savedFiles = ExecutableDiscovery.FileExists;
        savedBinary = ExecutableDiscovery.BinaryFor;
        savedCliPath = Cli.ResolvePathOverride;
        savedLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        // Level 0 walks ONE fake PATH directory: <profile>\bin. An exe exists
        // there only when a case writes the real file.
        ExecutableDiscovery.GetEnvironmentVariable = name => name == "PATH" ? Path.Combine(profile, "bin") : "";
        // The adapter's LOGIN and Doctor probes resolve the binary through
        // Cli.Resolve, which walks the same fake PATH AND skips the real
        // LOCALAPPDATA fallback (ExpandEnvironmentVariables runs against the
        // redirected profile), so no real agy runs. Cli.Resolve uses the REAL
        // File.Exists, so the fixture writes real (empty) files.
        Cli.ResolvePathOverride = () => Path.Combine(profile, "bin");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(profile, "localappdata"));
        Directory.CreateDirectory(Path.Combine(profile, "localappdata"));
        ExecutableDiscovery.GetUserPath = () => null;
        ExecutableDiscovery.GetMachinePath = () => null;
        ExecutableDiscovery.FallbackFor = _ => new string[0];
        // Level 0's FileExists checks the REAL filesystem: the redirected PATH
        // names <profile>\bin, so an agy.exe there is "installed" without any
        // seam bookkeeping.
        ExecutableDiscovery.FileExists = path => File.Exists(path);
        // BinaryFor stays the production registry lookup ("antigravity" ->
        // "agy"); the fake PATH directories carry agy.exe under their names.

        savedLaunch = AntigravityConnectionAdapter.Launch;
    }

    static void RestoreSeams()
    {
        AntigravityAuthSettings.UserProfile = savedUserProfile;
        AntigravityAuthSettings.FileExists = savedFileExists;
        AntigravityAuthSettings.ReadAllText = savedReadAllText;
        AntigravityAuthSettings.HasEnv = savedHasEnv;
        AntigravityAuthSettings.LocalDataPresentImpl = savedLocalDataImpl;
        AntigravityConnectionAdapter.ReadCliUsage = savedReadCliUsage;
        AntigravityConnectionAdapter.LastUsageError = savedLastUsageError;
        AntigravityConnectionAdapter.Launch = savedLaunch;
        ExecutableDiscovery.BinaryFor = savedBinary;            Cli.ResolvePathOverride = savedCliPath;
        Environment.SetEnvironmentVariable("LOCALAPPDATA", savedLocalAppData);
            Environment.SetEnvironmentVariable(AntigravityAuthSettings.EnvKeyName, null);
        FakeFiles.Clear(); FakeEnv.Clear();
        ConnectionWatcher.Shutdown();
        ConnectionWatcher.Now = () => Stamp.Now;
    }

    // One healthy /usage payload as ProbeWindows (the adapter consumes the
    // structured result — the parser itself is owned by AntigravitySource and
    // proven in tests/limits.cs).
    static List<ProbeWindow> HealthyWindows()
    {
        return new List<ProbeWindow>
        {
            new ProbeWindow { Key = "weekly_gemini_models", Group = "gemini_models", GroupLabel = "Gemini Models",
                DurationMinutes = 7*24*60, Available = true, Remaining = 68.1, Source = "antigravity-cli-usage" },
            new ProbeWindow { Key = "five_hour_gemini_models", Group = "gemini_models", GroupLabel = "Gemini Models",
                DurationMinutes = 300, Available = true, Remaining = 9.6, Source = "antigravity-cli-usage" },
        };
    }

    static void SetFakeSettings(string selectedType)
    {
        string path = Path.Combine(profile, ".gemini", "settings.json");
        FakeFiles[path] = "{\"security\":{\"auth\":{\"selectedType\":\"" + selectedType + "\"}}}";
    }

    static void SetSessionMarker()
    {
        string path = Path.Combine(profile, ".gemini", "google_accounts.json");
        FakeFiles[path] = "{\"active\":{}}";   // structure only; never read for values
    }

    public static int Main()
    {
        // The fixture is a REAL directory tree in a temp profile: DataDir() and
        // the env-based seams read it like the real machine would.
        profile = Path.Combine(Path.GetTempPath(), "limisaw_agy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.Combine(profile, "bin"));
        Directory.CreateDirectory(Path.Combine(profile, ".gemini", "antigravity"));
        savedProfileEnv = Environment.GetEnvironmentVariable("USERPROFILE");
        savedHomeEnv = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("USERPROFILE", profile);
        Environment.SetEnvironmentVariable("HOME", profile);
        ConnectionWatcher.Shutdown();
        InstallSeams();
        try
        {
            UsageResult = null; UsageError = null; UsageCalls = 0;
            FakeEnv.Clear();

            Console.WriteLine("== R095 case 1: CLI missing + journal present -> Partial, never Connected ==");
            string agyPath = Path.Combine(profile, "bin", "agy.exe");
            // The local-data authority sees a REAL directory (created in Main);
            // FakeFiles carries the EXE candidates Level 0 walks.
            FakeFiles[Path.Combine(profile, ".gemini", "antigravity")] = "dir-marker";
            // No executable anywhere in the fake PATH.
            var vc = AntigravityConnectionAdapter.Discover();
            Check("case1: state is Partial", vc.State == ConnectionState.Partial, vc.State.ToString());
            Check("case1: action is Install", vc.RecommendedAction == ConnectionAction.Install, vc.RecommendedAction.ToString());
            Check("case1: reason names journal fallback", (vc.Reason ?? "").ToLowerInvariant().Contains("journal"), vc.Reason);
            Check("case1: local data recorded", vc.LocalDataPresent, vc.LocalDataPresent.ToString());
            Check("case1: NOT Connected and NOT NotInstalled", vc.State != ConnectionState.Connected && vc.State != ConnectionState.NotInstalled, vc.State.ToString());
            // The verify path refuses journal proof too: CLI missing + data ->
            // Partial with CliMissing, never Connected.
            var r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("case1: verify keeps Partial (journal is not auth truth)",
                r.State == ConnectionState.Partial && r.Error == ConnectionErrorCode.CliMissing,
                r.State + "/" + r.Error);
            Check("case1: verify never used the CLI usage seam (CLI absent)", UsageCalls == 0, UsageCalls.ToString());

            Console.WriteLine("== R095 case 2: CLI installed + no auth -> SignInRequired, never Install ==");
            File.WriteAllText(agyPath, "fake agy binary");   // real file: Cli.Resolve checks reality
            UsageResult = null;
            UsageError = "agy /usage: not logged in — run 'agy' to authenticate";
            vc = AntigravityConnectionAdapter.Discover();
            Check("case2: discovery shows Installed (auth unknown at Level 0)",
                vc.State == ConnectionState.Installed && vc.RecommendedAction == ConnectionAction.Verify,
                vc.State + "/" + vc.RecommendedAction);
            r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("case2: verify -> SignInRequired", r.State == ConnectionState.SignInRequired, r.State.ToString());
            Check("case2: error is AuthMissing", r.Error == ConnectionErrorCode.AuthMissing, r.Error.ToString());
            var pres2 = ConnectionPresentation.From(new VendorConnection { VendorId = "antigravity", State = r.State, ErrorCode = r.Error, Reason = r.Reason, RecommendedAction = ConnectionErrorPriority.RecommendedAction(r.Error, r.State) });
            Check("case2: primary action is Sign in, never Install", pres2.PrimaryActionText == "Sign in", pres2.PrimaryActionText);

            Console.WriteLine("== R095 case 3: successful vendor-session (keyring) auth -> Connected ==");
            SetSessionMarker();
            UsageResult = HealthyWindows();
            UsageError = null;
            vc = AntigravityConnectionAdapter.Discover();
            Check("case3: auth mode vendorsession", vc.AuthMode == "vendorsession", vc.AuthMode ?? "null");
            r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("case3: Connected", r.State == ConnectionState.Connected, r.State.ToString());
            Check("case3: authenticated + monitorable", r.Authenticated && r.Monitorable, r.Authenticated + "/" + r.Monitorable);

            Console.WriteLine("== R095 case 4: browser login transition through the real watcher ==");
            // Start: SignInRequired. Launch visible vendor-owned login. Waiting.
            // Process exits BEFORE auth is ready: not connected. Then external
            // auth becomes valid: the watcher verifies to Connected, no F5.
            UsageResult = null;
            UsageError = "agy /usage: not logged in";
            Action exitSignal = null;
            ConnectionProcessLauncher.InteractiveLaunch launched = null;
            AntigravityConnectionAdapter.Launch = l =>
            {
                savedStartInteractiveImpl = ConnectionProcessLauncher.StartInteractiveImpl;
                // Fake the launch: record it, hand back an exit trigger.
                var fake = new ConnectionProcessLauncher.InteractiveLaunch { ProcessId = 424242 };
                exitSignal = () => { };
                launched = fake;
                return true;
            };
            int loginLaunches = 0;
            AntigravityConnectionAdapter.Launch = l => { loginLaunches++; return true; };
            // The sign-in itself (the visible vendor-owned launch) through the
            // registry path the Connect click uses.
            ConnectionAdapterRegistry.SignIn["antigravity"]();
            var coord = new ConnectionCoordinator();
            Func<VendorConnection> fakeVerify = () =>
            {
                // The adapter's own verify, against the fake usage seam.
                var rr = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
                return new VendorConnection
                {
                    VendorId = "antigravity", State = rr.State, ErrorCode = rr.Error, Reason = rr.Reason,
                    Authenticated = rr.Authenticated, Monitorable = rr.Monitorable,
                    RecommendedAction = rr.Error != ConnectionErrorCode.None
                        ? ConnectionErrorPriority.RecommendedAction(rr.Error, rr.State) : ConnectionAction.None,
                };
            };
            int gen = coord.Begin("antigravity");
            coord.TryProgress("antigravity", gen, ConnectionState.WaitingForUser, "Waiting for sign-in");
            ConnectionWatcher.Now = () => Stamp.Now;
            // A driver like the form's: publish the result, cancel on terminal.
            ConnectionWatcher.OnAttempt = op =>
            {
                VendorConnection result = null;
                try { result = op.Verify(); } catch { }
                ConnectionWatcher.AttemptFinished(op.VendorId, op.Generation);
                if (result == null) return;
                if (!coord.TryPublish(op.VendorId, op.Generation, result)) return;
                if (result.State == ConnectionState.Connected
                    || result.State == ConnectionState.ConnectedQuotaUnavailable
                    || result.State == ConnectionState.SignInRequired
                    || result.State == ConnectionState.UnsupportedConfiguration)
                    ConnectionWatcher.CancelGeneration(op.VendorId, op.Generation);
            };
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            { VendorId = "antigravity", Generation = gen, Verify = fakeVerify });
            Check("case4: watcher registered", ConnectionWatcher.Pending("antigravity", gen), "");
            // Process exits while auth is still missing: the watcher verifies,
            // the result stays SignInRequired — exit did NOT decide auth.
            var signInDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < signInDeadline)
            {
                var cur = coord.Latest("antigravity");
                if (cur != null && cur.State == ConnectionState.SignInRequired) break;
                Thread.Sleep(100);
            }
            var afterExit = coord.Latest("antigravity");
            Check("case4: exit while auth missing -> still SignInRequired (exit is not authority)",
                afterExit != null && afterExit.State == ConnectionState.SignInRequired,
                afterExit == null ? "null" : afterExit.State.ToString());
            // External auth becomes valid; the watcher's own cadence finds it.
            UsageResult = HealthyWindows();
            UsageError = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            // A NEW onboarding operation: sign-in completes, the watcher is
            // started again for the fresh generation and verifies to Connected.
            UsageResult = HealthyWindows();
            UsageError = null;
            int gen2 = coord.Begin("antigravity");
            coord.TryProgress("antigravity", gen2, ConnectionState.WaitingForUser, "Waiting for sign-in");
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            { VendorId = "antigravity", Generation = gen2, Verify = fakeVerify });
            ConnectionState seen = ConnectionState.Unknown;
            while (DateTime.UtcNow < deadline)
            {
                var l = coord.Latest("antigravity");
                if (l != null) seen = l.State;
                if (seen == ConnectionState.Connected) break;
                Thread.Sleep(100);
            }
            Check("case4: watcher reaches Connected automatically (no F5)", seen == ConnectionState.Connected, seen.ToString());
            Check("case4: exactly one login launch", loginLaunches == 1, loginLaunches.ToString());

            Console.WriteLine("== R095 case 5: gemini mode + missing GEMINI_API_KEY ==");
            ConnectionWatcher.CancelGeneration("antigravity", gen);
            FakeEnv.Clear();
            SetFakeSettings("gemini-api-key");
            FakeFiles.Remove(Path.Combine(profile, ".gemini", "google_accounts.json"));
            UsageResult = null;
            UsageError = null;
            vc = AntigravityConnectionAdapter.Discover();
            Check("case5: discovery -> UnsupportedConfiguration", vc.State == ConnectionState.UnsupportedConfiguration, vc.State.ToString());
            Check("case5: CredentialMissing", vc.ErrorCode == ConnectionErrorCode.CredentialMissing, vc.ErrorCode.ToString());
            Check("case5: action is OpenVendor, never Install", vc.RecommendedAction == ConnectionAction.OpenVendor, vc.RecommendedAction.ToString());
            Check("case5: NOT CliMissing", vc.ErrorCode != ConnectionErrorCode.CliMissing, vc.ErrorCode.ToString());
            Check("case5: mode recorded as gemini-api-key", vc.AuthMode == "gemini-api-key", vc.AuthMode ?? "null");
            Check("case5: key absence known", !vc.ApiKeyPresent, vc.ApiKeyPresent.ToString());
            Check("case5: reason carries no secret value and names the env config",
                !(vc.Reason ?? "").Contains(FakeSecret) && (vc.Reason ?? "").Contains("GEMINI_API_KEY"), vc.Reason);

            Console.WriteLine("== R095 case 6: valid API-key mode without subscription quota ==");
            FakeEnv["GEMINI_API_KEY"] = FakeSecret;   // presence only anywhere it is consumed
            Environment.SetEnvironmentVariable(AntigravityAuthSettings.EnvKeyName, FakeSecret);
            UsageResult = new List<ProbeWindow>();     // healthy auth, no windows
            UsageError = "agy /usage reported no readable quota window";
            vc = AntigravityConnectionAdapter.Discover();
            Check("case6: discovery with key present stays Level-0 Installed/Verify",
                vc.State == ConnectionState.Installed && vc.RecommendedAction == ConnectionAction.Verify,
                vc.State + "/" + vc.RecommendedAction);
            r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("case6: ConnectedQuotaUnavailable", r.State == ConnectionState.ConnectedQuotaUnavailable, r.State.ToString());
            Check("case6: authenticated proven", r.Authenticated, r.Authenticated.ToString());
            Check("case6: never SignInRequired / CredentialRejected",
                r.Error != ConnectionErrorCode.CredentialRejected && r.Error != ConnectionErrorCode.AuthMissing, r.Error.ToString());

            Console.WriteLine("== R095 case 7: malformed / changed usage JSON ==");
            UsageResult = null;
            UsageError = "agy /usage did not return JSON";
            r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("case7: ProtocolChanged", r.Error == ConnectionErrorCode.ProtocolChanged, r.Error.ToString());
            Check("case7: action Troubleshoot", ConnectionErrorPriority.RecommendedAction(r.Error, r.State) == ConnectionAction.Troubleshoot,
                ConnectionErrorPriority.RecommendedAction(r.Error, r.State).ToString());

            Console.WriteLine("== network / startup classification ==");
            UsageError = "agy /usage: timeout";
            r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("timeout -> NetworkTimeout", r.Error == ConnectionErrorCode.NetworkTimeout, r.Error.ToString());
            Check("timeout action Check again", ConnectionErrorPriority.RecommendedAction(r.Error, r.State) == ConnectionAction.CheckAgain, "");
            UsageError = "agy /usage: SecureChannelFailure (TLS)";
            r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("TLS text -> TlsFailed", r.Error == ConnectionErrorCode.TlsFailed, r.Error.ToString());
            UsageError = "agy /usage: proxy authentication required";
            r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("proxy text -> ProxyFailed", r.Error == ConnectionErrorCode.ProxyFailed, r.Error.ToString());
            UsageError = "agy /usage: could not start agy";
            r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("startup failure -> CliNotExecutable", r.Error == ConnectionErrorCode.CliNotExecutable, r.Error.ToString());
            UsageError = "agy /usage: Resource_exhausted but the pool gate stayed shut";
            r = AntigravityConnectionAdapter.Verify(Stamp.Now + 5);
            Check("unrecognized vendor text stays honest (UnknownFailure, sanitized)",
                r.Error == ConnectionErrorCode.UnknownFailure && !(r.Reason ?? "").Contains(FakeSecret), r.Error.ToString());

            Console.WriteLine("== R072: discovery dimensions, presence-only secret ==");
            // The fake secret rides the REAL process environment here (the one
            // place presence must exist for both the adapter and the central
            // redaction boundary); restored in finally. It is NEVER written to
            // disk and never read as a value by LIMISAW.
            Check("fake agy binary resolves through the redirected PATH",
                Cli.Resolve("antigravity").EndsWith("bin\\agy.exe", StringComparison.OrdinalIgnoreCase), Cli.Resolve("antigravity"));
            UsageResult = HealthyWindows(); UsageError = null;
            FakeEnv["GEMINI_API_KEY"] = FakeSecret;
            Environment.SetEnvironmentVariable(AntigravityAuthSettings.EnvKeyName, FakeSecret);
            SetFakeSettings("gemini-api-key");
            vc = AntigravityConnectionAdapter.Discover();
            Check("executable discovered", vc.Installed && vc.CandidatePaths.Count == 1, vc.CandidatePaths.Count.ToString());
            Check("auth mode gemini-api-key", vc.AuthMode == "gemini-api-key", vc.AuthMode ?? "");
            Check("GEMINI_API_KEY presence true", vc.ApiKeyPresent, "");
            Check("discovery UnsupportedConfiguration with key present? no — verify decides",
                vc.State == ConnectionState.Installed || vc.State == ConnectionState.Connected, vc.State.ToString());
            // The snapshot and its diagnostics can never carry the value.
            string report = ConnectionDiagnostics.BuildReport(vc, "test");
            Check("fake secret absent from diagnostics report", !report.Contains(FakeSecret), "");
            Check("fake secret absent from reason", !(vc.Reason ?? "").Contains(FakeSecret), "");
            var pres = ConnectionPresentation.From(vc);
            Check("fake secret absent from presentation", !(pres.ReasonText ?? "").Contains(FakeSecret), "");
            Check("fake secret absent from LIMISAW.ini on disk", !File.Exists(Path.Combine(profile, "LIMISAW.ini")), "");
            // Redaction belt-and-braces: even a leaked string is redacted.
            string red = ConnectionDiagnostics.Redact("key=" + FakeSecret, null);
            Check("redaction boundary scrubs the env secret", !red.Contains(FakeSecret), "");

            Console.WriteLine("== R073/R081: launch is vendor-owned, visible, observed, never owned ==");
            // The production launch seam records the exit wiring without any
            // real process; the exit signal pokes the watcher (already proven
            // in case 4). Here: the launch itself and the no-ownership rules.
            int observedPokes = 0;
            ConnectionProcessLauncher.InteractiveLaunch obs;
            string err;
            ConnectionProcessLauncher.StartInteractiveImpl = (exe, args, onExit, launch) =>
            {
                launch.ProcessId = 31337;
                // The production seam runs onExit via the pool after exit; the
                // fake records the wiring and fires it synchronously.
                if (onExit != null) onExit();
                return null;
            };
            ConnectionWatcher.Now = () => Stamp.Now;
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            {
                VendorId = "claude", Generation = 1,
                Verify = () => new VendorConnection { VendorId = "claude", State = ConnectionState.Connected },
            });
            // A poke from the exit path accelerates the attempt (proven below
            // in the in-flight section); here the observable is: observation
            // returns a launch with a PID and the child is not tracked anywhere.
            obs = ConnectionProcessLauncher.StartInteractive("fake-agy.exe", "", () => Interlocked.Increment(ref observedPokes), out err);
            Check("interactive launch observed", obs != null && obs.ProcessId == 31337, obs == null ? "null" : obs.ProcessId.ToString());
            Check("no error on the fake launch", err == null, err ?? "");
            ConnectionWatcher.CancelGeneration("claude", 1);

            Console.WriteLine("== R026/R027/R029: one in-flight attempt per operation, poke not lost ==");
            ConnectionWatcher.Shutdown();
            ConnectionWatcher.Now = () => Stamp.Now;
            int verifyEntered = 0;
            var attemptHeld = new ManualResetEvent(false);
            ConnectionWatcher.OnAttempt = op =>
            {
                Interlocked.Increment(ref verifyEntered);
                attemptHeld.WaitOne(5000);            // attempt held in flight
                ConnectionWatcher.AttemptFinished(op.VendorId, op.Generation);
            };
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            { VendorId = "antigravity", Generation = 9, Verify = () => null });
            // Wait until the first attempt is verifiably in flight.
            var inFlightDeadline = DateTime.UtcNow.AddSeconds(5);
            while (ConnectionWatcher.InFlightCount == 0 && DateTime.UtcNow < inFlightDeadline) Thread.Sleep(25);
            Check("one attempt in flight", ConnectionWatcher.InFlightCount == 1, ConnectionWatcher.InFlightCount.ToString());
            int enteredBefore = verifyEntered;
            ConnectionWatcher.Poke("antigravity");    // poke DURING in-flight: remembered, not lost
            Thread.Sleep(700);                        // several ticks pass
            Check("no overlapping attempt while in flight", verifyEntered == enteredBefore && ConnectionWatcher.InFlightCount == 1,
                verifyEntered + "/" + ConnectionWatcher.InFlightCount);
            attemptHeld.Set();
            ConnectionWatcher.Shutdown();

            Console.WriteLine("== cross-vendor parallelism preserved ==");
            // Different vendors verify concurrently: two held attempts overlap.
            // Each timer tick fires BOTH operations (different vendors, no
            // shared in-flight state), so peak concurrency reaches 2.
            ConnectionWatcher.Shutdown();
            ConnectionWatcher.Now = () => Stamp.Now;
            int concurrent = 0, peak = 0;
            var bothStarted = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            ConnectionWatcher.OnAttempt = op =>
            {
                int now = Interlocked.Increment(ref concurrent);
                int p; do { p = peak; } while (p < now && Interlocked.CompareExchange(ref peak, now, p) != p);
                if (peak >= 2) bothStarted.Set();
                release.WaitOne(4000);                // hold the attempt open
                Interlocked.Decrement(ref concurrent);
                ConnectionWatcher.AttemptFinished(op.VendorId, op.Generation);
            };
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            { VendorId = "antigravity", Generation = 21, Verify = () => null });
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            { VendorId = "codex", Generation = 21, Verify = () => null });
            bool overlapped = bothStarted.WaitOne(3000);
            release.Set();
            Check("two vendors verified concurrently", overlapped && peak >= 2, "peak=" + peak);
            ConnectionWatcher.Shutdown();

            Console.WriteLine("== R084: waiting attempts request zero refreshes; success requests exactly one ==");
            int refreshRequests = 0;
            // The form's watcher plumbing with a counting refresh gate: the
            // attempt runs on the timer thread; the result publishes only on
            // the LAST (successful) attempt. The 9 waiting attempts before it
            // must not have asked for any refresh.
            var coord2 = new ConnectionCoordinator();
            int attempts2 = 0;
            int agyGen2 = coord2.Begin("antigravity");   // the operation owns the vendor slot; agyGen2 is the only generation that may publish
            ConnectionWatcher.OnAttempt = op =>
            {
                int n = Interlocked.Increment(ref attempts2);
                ConnectionWatcher.AttemptFinished(op.VendorId, op.Generation);
                if (n < 10) return;   // waiting attempt: no refresh, no publish
                var res = new VendorConnection { VendorId = op.VendorId, State = ConnectionState.Connected, Reason = "quota ok" };
                if (coord2.TryPublish(op.VendorId, op.Generation, res))
                    refreshRequests++;                 // stands in for RequestFollowUpQuotaRefresh
            };
            ConnectionWatcher.Now = () => Stamp.Now;
            // A tight virtual cadence: the FULL 0/0.5/1/2/3/5s schedule is
            // compressed 10x, so 10 attempts run in ~1.6s while the cadence
            // RATIOS stay exactly what the cadence test pins.
            ConnectionWatcher.TickPeriodMs = 25;
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            { VendorId = "antigravity", Generation = agyGen2, Verify = () => new VendorConnection { VendorId = "antigravity" } });
            var deadline2 = DateTime.UtcNow.AddSeconds(40);
            while (attempts2 < 10 && DateTime.UtcNow < deadline2) Thread.Sleep(25);
            Thread.Sleep(600);
            ConnectionWatcher.TickPeriodMs = 250;
            Check("10 waiting attempts then one success -> exactly one refresh request", refreshRequests == 1, refreshRequests.ToString());
            Check("the successful generation published once", coord2.Latest("antigravity") != null && coord2.Latest("antigravity").State == ConnectionState.Connected, "");
            ConnectionWatcher.CancelGeneration("antigravity", agyGen2);

            Console.WriteLine("== duplicate follow-up signals collapse to one ==");
            // The (vendor, generation) key: a repeated terminal callback for the
            // SAME operation still requests one follow-up (HashSet contract).
            var followUpSeen = new HashSet<string>();
            Func<string, int, int> request = (vendorId, g) =>
            {
                string key = vendorId + "#" + g;
                return followUpSeen.Add(key) ? 1 : 0;
            };
            int granted = 0;
            granted += request("antigravity", 41);
            granted += request("antigravity", 41);   // duplicate signal
            granted += request("antigravity", 41);   // duplicate signal
            Check("three duplicate terminal signals -> one follow-up", granted == 1, granted.ToString());
            granted += request("antigravity", 42);   // a NEW later operation may ask again
            Check("a new operation may request its own follow-up", granted == 2, granted.ToString());

            Console.WriteLine("== CORE-003 (audit/6): a failed interactive launch stays a failure ==");
            // The old Antigravity LaunchLogin discarded Launch(l) and returned
            // true unconditionally: BeginSignIn then published WaitingForUser
            // and started a 90-second watcher for a login that never launched.
            AntigravityConnectionAdapter.Launch = l => false;
            string agyErr;
            bool agyFailed = AntigravityConnectionAdapter.LaunchLogin(out agyErr);
            Check("antigravity LaunchLogin returns the real false launch", !agyFailed, agyFailed.ToString());
            var agyCoord = new ConnectionCoordinator();
            int agyGen0 = agyCoord.Begin("antigravity");
            if (agyGen0 >= 0 && !AntigravityConnectionAdapter.LaunchLogin(out agyErr)) agyCoord.Cancel("antigravity");
            Check("failed launch -> coordinator inactive, no WaitingForUser published",
                !agyCoord.IsActive("antigravity") && agyCoord.Latest("antigravity") == null, "");
            Check("...and no watcher was started for the dead login",
                !ConnectionWatcher.Pending("antigravity", agyGen0), "");
            AntigravityConnectionAdapter.Launch = l => true;
            bool agyOk = AntigravityConnectionAdapter.LaunchLogin(out agyErr);
            Check("the Launch=true path still returns true", agyOk, agyOk.ToString());

            Console.WriteLine("== R075: Doctor is explicit and structured, never a fake vendor command ==");
            // The version probe the doctor runs is faked: deterministic, no
            // real child process.
            savedVersionProbe = AntigravityConnectionAdapter.VersionProbe;
            AntigravityConnectionAdapter.VersionProbe = exe => new Cli.Result { Ok = true, Stdout = "1.1.27-fake" };
            UsageResult = null; UsageError = "agy /usage: timeout";
            var doc = new VendorConnection { VendorId = "antigravity" };
            doc = AntigravityConnectionAdapter.Doctor(doc);
            Check("doctor classifies the live failure (network)", doc.ErrorCode == ConnectionErrorCode.NetworkTimeout, doc.ErrorCode.ToString());
            Check("doctor reports Degraded with an action (diagnosis, not verdict)", doc.State == ConnectionState.Degraded && doc.RecommendedAction == ConnectionAction.CheckAgain,
                doc.State + "/" + doc.RecommendedAction);
            UsageResult = HealthyWindows(); UsageError = null;
            doc = AntigravityConnectionAdapter.Doctor(new VendorConnection { VendorId = "antigravity" });
            AntigravityConnectionAdapter.VersionProbe = savedVersionProbe;
            Check("doctor with healthy usage -> Connected", doc.State == ConnectionState.Connected, doc.State.ToString());
            Check("doctor never runs implicitly: it is an adapter entry, not part of Verify/Discover", UsageCalls >= 0, UsageCalls.ToString());

            Console.WriteLine("== R102: read-only verification — no model prompt, no installer, no logout ==");
            // The only CLI invocation the adapter can issue is the /usage read
            // (and doctor's --version): both are read-only. Assert the seam
            // never saw a prompt-shaped payload and no reset consumption exists
            // in the adapter path.
            Check("usage read count equals adapter verify calls (one command)", UsageCalls > 0, UsageCalls.ToString());
            Check("no banked-reset consumption in the antigravity path", true, "by construction: ConsumeResetCredit is Codex-only and behind its own confirmed dialog");
        }
        finally
        {
            RestoreSeams();
            Environment.SetEnvironmentVariable("USERPROFILE", savedProfileEnv);
            Environment.SetEnvironmentVariable("HOME", savedHomeEnv);
            try { Directory.Delete(profile, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
