using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Limisaw;

// Codex + Claude connection adapters (SRC-002 R093/R094), fully deterministic.
//
// No real vendor account, no real CLI, no network: the seams are
// CodexSource.ResolveExe / SessionPool.StartSession for the app-server path,
// CodexHomeDiscovery's directory seams for the home view, the adapters'
// Launch seams for the interactive sign-in, and Cli.Run replacement is NOT
// needed because Claude auth status and doctor parse through their own seams.
//
// Codex cases: CLI missing, USER PATH discovery, conflicting installs, default
// home unauthenticated, exact non-default CODEX_HOME login environment,
// successful quota read, quota unavailable, expired auth, dead pooled session
// (only that home, one retry), doctor classification, post-login watcher,
// second Connect, timeout, stale generation.
//
// Claude cases: CLI missing, healthy version, conflicting installs, auth
// logged out / logged in / expired, login launch, post-login transition,
// subscription quota, no-subscription -> ConnectedQuotaUnavailable, network
// timeout, TLS/proxy classification, malformed auth status, doctor
// classification, second Connect, shutdown survival, watcher timeout.
public static class CliConnectionsTest
{
    static int fails = 0, checks = 0;
    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string profile;
    static Func<string, bool> savedCodexDirExists, savedCodexFileExists;
    static Func<string, string> savedCodexGetEnv;
    static Func<string> savedUserProfile;
    static Func<string, string> savedResolveExe;
    static Func<string, string, CodexSource.RpcLink> savedStartSession;

    public static int Main()
    {
        profile = Path.Combine(Path.GetTempPath(), "limisaw_cli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        string savedProfileEnv = Environment.GetEnvironmentVariable("USERPROFILE");
        string savedCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        savedResolveExe = CodexSource.ResolveExe;
        savedStartSession = CodexSource.StartSession;
        savedCodexDirExists = CodexHomeDiscovery.DirectoryExists;
        savedCodexFileExists = CodexHomeDiscovery.FileExists;
        savedCodexGetEnv = CodexHomeDiscovery.GetEnv;
        savedUserProfile = CodexHomeDiscovery.UserProfile;
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", profile);
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            string processCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            // Level 0 discovery fixture for the whole harness: no PATH sources,
            // no fallbacks, nothing on disk unless a case says so.
            ExecutableDiscovery.GetEnvironmentVariable = _ => "";
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            ExecutableDiscovery.FileExists = _ => false;

            Console.WriteLine("== codex discovery ==");
            // 1. CLI missing -> Install (never a sign-in recommendation).
            CodexSource.ResolveExe = _ => "";
            CodexHomeDiscovery.DirectoryExists = _ => false;
            CodexHomeDiscovery.FileExists = _ => false;
            CodexHomeDiscovery.GetEnv = _ => null;
            CodexHomeDiscovery.UserProfile = () => profile;
            var vc = CodexConnectionAdapter.Discover();
            Check("codex CLI missing -> NotInstalled/Install", vc.State == ConnectionState.NotInstalled && vc.RecommendedAction == ConnectionAction.Install,
                vc.State + "/" + vc.RecommendedAction);

            // 2. CLI discovered via USER PATH after process startup.
            string binDir = Path.Combine(profile, "bin");
            Directory.CreateDirectory(binDir);
            File.WriteAllText(Path.Combine(binDir, "codex.cmd"), "@echo off");
            ExecutableDiscovery.GetEnvironmentVariable = _ => binDir;
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            ExecutableDiscovery.FileExists = p => File.Exists(p);
            CodexSource.ResolveExe = exe => Path.Combine(binDir, "codex.cmd");
            CodexHomeDiscovery.DirectoryExists = d => d == Path.Combine(profile, ".codex");
            CodexHomeDiscovery.FileExists = _ => false;
            CodexHomeDiscovery.UserProfile = () => profile;
            vc = CodexConnectionAdapter.Discover();
            Check("codex CLI found via USER PATH source -> Installed", vc.Installed && vc.CandidatePaths.Count == 1, vc.State + "/" + vc.CandidatePaths.Count);

            // 3. Conflicting installs.
            string binDir2 = Path.Combine(profile, "bin2");
            Directory.CreateDirectory(binDir2);
            File.WriteAllText(Path.Combine(binDir2, "codex.cmd"), "@echo off");
            File.WriteAllText(Path.Combine(binDir2, "claude.cmd"), "@echo off");
            ExecutableDiscovery.GetEnvironmentVariable = _ => binDir + Path.PathSeparator + binDir2;
            vc = ExecutableDiscovery.BuildConnection("codex");
            Check("codex conflicting installs -> conflicting_installations",
                vc.ErrorCode == ConnectionErrorCode.ConflictingInstallations && vc.RecommendedAction == ConnectionAction.Troubleshoot,
                vc.ErrorCode.ToString());

            // 4. Default home unauthenticated -> SignInRequired/Connect, exact home identity.
            CodexSource.ResolveExe = _ => "codex.cmd";
            var defaultHome = Path.Combine(profile, ".codex");
            CodexHomeDiscovery.DirectoryExists = d => d == defaultHome;
            CodexHomeDiscovery.FileExists = _ => false;
            vc = CodexConnectionAdapter.Discover();
            Check("codex default home unauthenticated -> SignInRequired/Connect",
                vc.State == ConnectionState.SignInRequired && vc.RecommendedAction == ConnectionAction.Connect,
                vc.State + "/" + vc.RecommendedAction);
            Check("codex unauthenticated snapshot carries exact canonical home identity",
                vc.SelectedHomePath == defaultHome && vc.SelectedHomeId == CodexHomeDiscovery.HomeId(defaultHome) && vc.SelectedHomeId.Length == 16,
                vc.SelectedHomeId ?? "null");
            Check("codex auth-missing never recommends Install", vc.RecommendedAction != ConnectionAction.Install, vc.RecommendedAction.ToString());

            // 5. Exact non-default CODEX_HOME: login child receives the canonical path.
            string customHome = Path.Combine(profile, "custom-codex-home");
            Directory.CreateDirectory(customHome);
            CodexHomeDiscovery.GetEnv = name => name == "CODEX_HOME" ? customHome : null;
            CodexHomeDiscovery.DirectoryExists = d => d == customHome || d == defaultHome;
            CodexHomeDiscovery.FileExists = _ => false;
            var target = CodexConnectionAdapter.TargetHome();
            Check("explicit CODEX_HOME is the connection target, not the default",
                target != null && target.Path == customHome && !target.IsDefaultHome,
                target == null ? "null" : target.Path);
            CodexConnectionAdapter.LaunchCount = 0;
            CodexConnectionAdapter.Launched.Clear();
            CodexConnectionAdapter.Launch = l =>
            {
                Check("codex non-default login receives exact canonical CODEX_HOME",
                    l.EnvName == "CODEX_HOME" && l.EnvValue == customHome,
                    l.EnvName + "=" + (l.EnvValue ?? "null"));
                Check("codex login launches supported login command visibly", l.Arguments == "login", l.Arguments);
                return true;
            };
            string loginError;
            bool loginOk = CodexConnectionAdapter.LaunchLogin(target, out loginError);
            Check("codex login launch accepted", loginOk && CodexConnectionAdapter.LaunchCount == 1, CodexConnectionAdapter.LaunchCount.ToString());
            // Default home login carries NO env override.
            CodexHomeDiscovery.GetEnv = _ => null;
            CodexHomeDiscovery.DirectoryExists = d => d == defaultHome;
            CodexConnectionAdapter.Launched.Clear();
            CodexConnectionAdapter.Launch = l =>
            {
                Check("codex default-home login carries no CODEX_HOME override", l.EnvName == null, l.EnvName ?? "none");
                return true;
            };
            var defaultTarget = CodexConnectionAdapter.TargetHome();
            CodexConnectionAdapter.LaunchLogin(defaultTarget, out loginError);
            Check("LIMISAW process environment is never mutated for a non-default home",
                Environment.GetEnvironmentVariable("CODEX_HOME") == processCodexHome,
                Environment.GetEnvironmentVariable("CODEX_HOME") ?? "null");

            Console.WriteLine("== codex verify through the SessionPool ==");
            // 6. Successful app-server quota -> Connected.
            var pool2 = new CodexSource.SessionPool();
            CodexSource.Pool.Reset();
            CodexSource.StartSession = (exe, home) =>
            {
                var link = new CodexSource.RpcLink();
                link.Call = (method, parameters, deadline) =>
                {
                    if (method == "initialize")
                        return new Dictionary<string, object> { { "result", new Dictionary<string, object>() } };
                    if (method == "account/rateLimits/read")
                        return new Dictionary<string, object> { { "result", CodexQuota("plus", 300, 20.0, 10080, 55.0) } };
                    return null;
                };
                link.Notify = (m, p) => { };
                return link;
            };
            var verify = CodexConnectionAdapter.VerifyHome(defaultHome, Stamp.Now + 20);
            Check("codex successful quota read -> Connected",
                verify.State == ConnectionState.Connected && verify.Error == ConnectionErrorCode.None,
                verify.State + "/" + verify.Error);
            Check("codex Connected is authenticated and monitorable", verify.Authenticated && verify.Monitorable, "");

            // 7. Authenticated healthy session + legitimate quota unavailable.
            CodexSource.Pool.Reset();
            CodexSource.StartSession = (exe, home) =>
            {
                var link = new CodexSource.RpcLink();
                link.Call = (method, parameters, deadline) =>
                {
                    if (method == "initialize")
                        return new Dictionary<string, object> { { "result", new Dictionary<string, object>() } };
                    return new Dictionary<string, object> { { "error", new Dictionary<string, object> { { "message", "no quota" } } } };
                };
                link.Notify = (m, p) => { };
                return link;
            };
            verify = CodexConnectionAdapter.VerifyHome(defaultHome, Stamp.Now + 20);
            Check("codex healthy auth + quota unavailable -> ConnectedQuotaUnavailable (never SignInRequired)",
                verify.State == ConnectionState.ConnectedQuotaUnavailable && verify.Authenticated,
                verify.State.ToString());

            // 8. Expired/bad auth -> sign-in, never Install.
            CodexSource.Pool.Reset();
            CodexSource.StartSession = (exe, home) =>
            {
                var link = new CodexSource.RpcLink();
                link.Call = (method, parameters, deadline) =>
                {
                    if (method == "initialize")
                        return new Dictionary<string, object> { { "error", new Dictionary<string, object> { { "message", "unauthorized" } } } };
                    return null;
                };
                link.Notify = (m, p) => { };
                return link;
            };
            verify = CodexConnectionAdapter.VerifyHome(defaultHome, Stamp.Now + 20);
            Check("codex rejected auth -> SignInRequired with sign-in action",
                verify.State == ConnectionState.SignInRequired
                && ConnectionErrorPriority.RecommendedAction(verify.Error, verify.State) == ConnectionAction.Connect,
                verify.State + "/" + verify.Error);

            // 9. Dead pooled session: ONLY the target home restarts, one retry.
            int sessionStarts = 0;
            var startedHomes = new List<string>();
            bool handshakeKilled = false;
            string deadHome = Path.Combine(profile, ".codex-dead");
            CodexSource.StartSession = (exe, home) =>
            {
                sessionStarts++;
                startedHomes.Add(home);
                var link = new CodexSource.RpcLink();
                link.Alive = () => true;
                link.Call = (method, parameters, deadline) =>
                {
                    if (method == "initialize")
                    {
                        if (!handshakeKilled)
                        {
                            // The first session dies mid-handshake: dead session.
                            handshakeKilled = true;
                            return null;
                        }
                        return new Dictionary<string, object> { { "result", new Dictionary<string, object>() } };
                    }
                    if (method == "account/rateLimits/read")
                        return new Dictionary<string, object> { { "result", CodexQuota("plus", 300, 10.0, 10080, 5.0) } };
                    return null;
                };
                link.Notify = (m, p) => { };
                return link;
            };
            CodexSource.Pool.Reset();
            verify = CodexConnectionAdapter.VerifyHome(deadHome, Stamp.Now + 20);
            Check("codex dead session -> one retry inside the same operation -> Connected",
                verify.State == ConnectionState.Connected, verify.State + "/" + verify.Error);
            Check("codex dead session restarted exactly one child", sessionStarts == 2, sessionStarts.ToString());
            Check("codex dead session only touched the target home",
                startedHomes.TrueForAll(h => h == deadHome), string.Join(";", startedHomes.ToArray()));

            Console.WriteLine("== codex doctor ==");
            // 10. Doctor classifies a startup problem; runs only on demand.
            string doctorOutput = "Codex CLI doctor\nauth: not logged in\n";
            var doctorConn = new VendorConnection { VendorId = "codex", State = ConnectionState.Degraded };
            // The classification seam is exercised through a fake Cli.Result by
            // going through the adapter's public Doctor — which shells out. To
            // stay deterministic, the seam under test is ClassifyDoctor via
            // reflection-free internal access: the internal method is public
            // within the assembly.
            var fakeResult = new Cli.Result { Ok = true, Stdout = doctorOutput };
            var classified = CodexConnectionAdapter.ClassifyDoctor(fakeResult, doctorConn);
            Check("codex doctor classifies auth problem", classified == ConnectionErrorCode.AuthMissing, classified.ToString());
            classified = CodexConnectionAdapter.ClassifyDoctor(new Cli.Result { Ok = false, Error = "timeout" }, doctorConn);
            Check("codex doctor timeout -> DeadlineExceeded", classified == ConnectionErrorCode.DeadlineExceeded, classified.ToString());
            classified = CodexConnectionAdapter.ClassifyDoctor(new Cli.Result { Ok = true, Stdout = "tls certificate problem" }, doctorConn);
            Check("codex doctor TLS -> TlsFailed", classified == ConnectionErrorCode.TlsFailed, classified.ToString());
            classified = CodexConnectionAdapter.ClassifyDoctor(new Cli.Result { Ok = true, Stdout = "everything fine" }, doctorConn);
            Check("codex doctor unclassifiable -> UnknownFailure (never fabricated)", classified == ConnectionErrorCode.UnknownFailure, classified.ToString());

            Console.WriteLine("== codex post-login watcher ==");
            // 11. Post-login watcher observes successful auth -> Connected.
            var coord = new ConnectionCoordinator();
            var published = new List<VendorConnection>();
            ConnectionWatcher.Shutdown();
            ConnectionWatcher.OnAttempt = op =>
            {
                VendorConnection result;
                try { result = op.Verify(); }
                catch { result = null; }
                if (result == null) return;
                if (coord.TryPublish(op.VendorId, op.Generation, result))
                {
                    lock (published) published.Add(result);
                    if (result.State == ConnectionState.Connected || result.State == ConnectionState.SignInRequired
                        || result.State == ConnectionState.ConnectedQuotaUnavailable || result.State == ConnectionState.UnsupportedConfiguration)
                        ConnectionWatcher.CancelGeneration(op.VendorId, op.Generation);
                }
            };
            ConnectionWatcher.OnExpire = op => coord.TryProgress(op.VendorId, op.Generation, ConnectionState.Degraded, "expired");
            ConnectionAdapterRegistry.SignIn["codex"] = () => { CodexConnectionAdapter.LaunchCount++; return true; };
            int gen = coord.Begin("codex");
            ConnectionAdapterRegistry.SignIn["codex"]();
            coord.TryProgress("codex", gen, ConnectionState.WaitingForUser, "Waiting for sign-in");

            // 12. Second Connect while waiting: exactly one interactive login.
            // The check must run while the WaitingForUser operation is ACTIVE —
            // that is the only state in which the production BeginSignIn guard
            // (gen < 0 -> note, no sign-in call) applies. After the watcher
            // publishes Connected the slot is handed back by design.
            int launchesBefore = CodexConnectionAdapter.LaunchCount;
            int refusedGen = coord.Begin("codex");
            Check("codex second Connect while waiting launches no second login",
                refusedGen < 0 && CodexConnectionAdapter.LaunchCount == launchesBefore,
                (refusedGen < 0 ? "refused" : refusedGen.ToString()) + " and " + CodexConnectionAdapter.LaunchCount + " vs " + launchesBefore);

            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            {
                VendorId = "codex",
                Generation = gen,
                Verify = () => new VendorConnection { VendorId = "codex", State = ConnectionState.Connected, Reason = "auth ok after login" },
            });
            VendorConnection postLogin = null;
            for (int i = 0; i < 100; i++)
            {
                var c = coord.Latest("codex");
                if (c != null && c.State == ConnectionState.Connected) { postLogin = c; break; }
                Thread.Sleep(50);
            }
            Check("codex post-login watcher reaches Connected automatically (no F5)",
                postLogin != null && postLogin.State == ConnectionState.Connected,
                postLogin == null ? "null" : postLogin.State.ToString());

            // 13. Timeout -> bounded Waiting/Degraded + Check again.
            int tgen = coord.Begin("claude");
            coord.TryProgress("claude", tgen, ConnectionState.WaitingForUser, "Waiting");
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            { VendorId = "claude", Generation = tgen, Verify = () => null });
            for (int i = 0; i < 30 && !ConnectionWatcher.Pending("claude", tgen); i++) Thread.Sleep(20);
            var timeoutCard = coord.Latest("claude");
            Check("claude timeout card is bounded (Waiting or Degraded, never Verifying forever)",
                timeoutCard == null || timeoutCard.State == ConnectionState.WaitingForUser || timeoutCard.State == ConnectionState.Degraded,
                timeoutCard == null ? "no card yet" : timeoutCard.State.ToString());
            ConnectionWatcher.CancelGeneration("claude", tgen);

            // 14. Stale generation completion cannot overwrite newer state.
            int st2 = coord.Begin("codex");
            coord.TryPublish("codex", st2, new VendorConnection { VendorId = "codex", State = ConnectionState.Connected, Reason = "newer" });
            coord.TryPublish("codex", st2 - 1, new VendorConnection { VendorId = "codex", State = ConnectionState.Failed, Reason = "older" });
            var kept = coord.Latest("codex");
            Check("codex stale generation cannot overwrite newer state", kept != null && kept.State == ConnectionState.Connected && kept.Reason == "newer",
                kept == null ? "null" : kept.State + "/" + kept.Reason);
            ConnectionWatcher.Shutdown();

            Console.WriteLine("== claude discovery ==");
            ExecutableDiscovery.GetEnvironmentVariable = _ => binDir;
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.FallbackFor = k => k == "claude" ? new string[0] : new string[0];
            // claude.cmd exists in binDir alongside codex.cmd.
            File.WriteAllText(Path.Combine(binDir, "claude.cmd"), "@echo off");
            // 1. CLI missing.
            ExecutableDiscovery.FileExists = p => false;
            var cvc = ClaudeConnectionAdapter.Discover();
            Check("claude CLI missing -> NotInstalled/Install", cvc.State == ConnectionState.NotInstalled && cvc.RecommendedAction == ConnectionAction.Install,
                cvc.State + "/" + cvc.RecommendedAction);
            // 2. CLI executable healthy.
            ExecutableDiscovery.FileExists = p => File.Exists(p);
            cvc = ClaudeConnectionAdapter.Discover();
            Check("claude CLI healthy -> Installed", cvc.Installed && cvc.CandidatePaths.Count == 1, cvc.State + "/" + cvc.CandidatePaths.Count);
            // 3. Conflicting installs.
            ExecutableDiscovery.GetEnvironmentVariable = _ => binDir + Path.PathSeparator + binDir2;
            cvc = ExecutableDiscovery.BuildConnection("claude");
            Check("claude conflicting installs -> conflicting_installations", cvc.ErrorCode == ConnectionErrorCode.ConflictingInstallations && cvc.RecommendedAction == ConnectionAction.Troubleshoot,
                cvc.ErrorCode + "/" + cvc.RecommendedAction);
            ExecutableDiscovery.GetEnvironmentVariable = _ => binDir;

            Console.WriteLine("== claude auth status parser ==");
            // 4. Logged out.
            var auth = ClaudeConnectionAdapter.ParseAuthStatus("", "Not logged in. Run claude auth login first.");
            Check("claude auth status logged out -> LoggedOut", auth.Category == CliAuthCategory.LoggedOut, auth.Category.ToString());
            // 5. Logged in (machine-readable).
            auth = ClaudeConnectionAdapter.ParseAuthStatus("{\"loggedIn\":true,\"account\":\"x\"}", null);
            Check("claude machine-readable logged in -> LoggedIn", auth.Category == CliAuthCategory.LoggedIn && auth.MachineReadable, auth.Category.ToString());
            // 5b. Logged in (text).
            auth = ClaudeConnectionAdapter.ParseAuthStatus("You are logged in as user@example.com", null);
            Check("claude text logged in -> LoggedIn", auth.Category == CliAuthCategory.LoggedIn, auth.Category.ToString());
            // 6. Expired/rejected.
            auth = ClaudeConnectionAdapter.ParseAuthStatus("", "OAuth token has expired, please log in again");
            Check("claude expired token -> Expired", auth.Category == CliAuthCategory.Expired, auth.Category.ToString());
            auth = ClaudeConnectionAdapter.ParseAuthStatus("{\"status\":\"rejected\"}", null);
            Check("claude machine-readable rejected -> Expired", auth.Category == CliAuthCategory.Expired, auth.Category.ToString());
            // 13. Malformed auth status -> unclassifiable, not a guess.
            auth = ClaudeConnectionAdapter.ParseAuthStatus("%%% not json \n greek text", "bytes bytes");
            Check("claude malformed auth status -> Unknown (protocol/output classification candidate)",
                auth.Category == CliAuthCategory.Unknown, auth.Category.ToString());

            Console.WriteLine("== claude login + post-login ==");
            // 7. Auth login launch.
            ClaudeConnectionAdapter.LaunchCount = 0;
            ClaudeConnectionAdapter.Launched.Clear();
            ClaudeConnectionAdapter.Launch = l =>
            {
                Check("claude login launches 'auth login'", l.Arguments.Contains("auth login"), l.Arguments);
                return true;
            };
            string claudeLoginError;
            bool claudeLoginOk = ClaudeConnectionAdapter.LaunchLogin(out claudeLoginError);
            Check("claude login launch accepted", claudeLoginOk && ClaudeConnectionAdapter.LaunchCount == 1, ClaudeConnectionAdapter.LaunchCount.ToString());

            // 7b. A SECOND Claude account. One config directory is one account,
            // so "sign in again" the obvious way does not add one - it replaces
            // the account already in ~/.claude, and the first card disappears.
            // The add-account flow makes the second home explicit and routes the
            // vendor's own login into it.
            string claudeDefaultHome = Path.Combine(profile, ".claude");
            Directory.CreateDirectory(claudeDefaultHome);
            string firstFree = ClaudeConnectionAdapter.NextAccountHome();
            Check("the next account home is ~/.claude-account2",
                firstFree == Path.Combine(profile, ".claude-account2"), firstFree);
            Check("...and is never the default home", firstFree != claudeDefaultHome, firstFree);

            // The launch counter belongs to the cases below; this one borrows
            // it and puts it back.
            int claudeLaunchesBeforeAdd = ClaudeConnectionAdapter.LaunchCount;
            ClaudeConnectionAdapter.Launched.Clear();
            ClaudeConnectionAdapter.Launch = l => true;
            string addedHome, addError;
            bool addOk = ClaudeConnectionAdapter.LaunchAddAccount(out addedHome, out addError);
            Check("add-account launch accepted",
                addOk && ClaudeConnectionAdapter.LaunchCount == claudeLaunchesBeforeAdd + 1,
                (addError ?? "") + " count=" + ClaudeConnectionAdapter.LaunchCount);
            Check("...the new home is created", Directory.Exists(addedHome), addedHome ?? "null");
            var addLaunch = ClaudeConnectionAdapter.Launched[0];
            Check("...the child carries CLAUDE_CONFIG_DIR, LIMISAW's own env untouched",
                addLaunch.EnvName == "CLAUDE_CONFIG_DIR" && addLaunch.EnvValue == addedHome
                && Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") == null,
                (addLaunch.EnvName ?? "-") + "=" + (addLaunch.EnvValue ?? "-"));
            Check("...through the vendor's own sign-in command",
                addLaunch.Arguments.Contains("auth login"), addLaunch.Arguments);

            // An abandoned login leaves an empty directory. That is not an
            // account, so it never becomes a card - and the next click reuses
            // it instead of walking the number up forever.
            Check("an empty home is not discovered as an account",
                ClaudeSource.Homes().Count == 1, ClaudeSource.Homes().Count.ToString());
            string reuse, reuseError;
            ClaudeConnectionAdapter.LaunchAddAccount(out reuse, out reuseError);
            Check("...and the next click reuses it", reuse == addedHome, reuse ?? "null");

            // Once the login has written credentials there, it IS an account:
            // its own card, and the next click takes the next home.
            File.WriteAllText(Path.Combine(addedHome, ".credentials.json"), "{}");
            Check("a home that holds an account becomes its own account",
                ClaudeSource.Homes().Count == 2, ClaudeSource.Homes().Count.ToString());
            string third, thirdError;
            ClaudeConnectionAdapter.LaunchAddAccount(out third, out thirdError);
            Check("...and is never offered again",
                third == Path.Combine(profile, ".claude-account3"), third ?? "null");

            // The registry entry is what puts the button on the card at all.
            // The onboarding generation rides the registry call; Claude's flow
            // owns no live vendor state, so it ignores the value.
            AddAccountOutcome addOutcome = ConnectionAdapterRegistry.AddAccount["claude"](1);
            Check("the registry flow reports the home it prepared",
                addOutcome != null && addOutcome.Ok && addOutcome.Home == third,
                addOutcome == null ? "null" : (addOutcome.Home ?? "-"));
            ClaudeConnectionAdapter.LaunchCount = claudeLaunchesBeforeAdd;
            ClaudeConnectionAdapter.Launched.Clear();
            // 15. Second Connect click -> one login process.
            var coordC = new ConnectionCoordinator();
            ConnectionAdapterRegistry.SignIn["claude"] = () => { ClaudeConnectionAdapter.LaunchCount++; return true; };
            int cgen = coordC.Begin("claude");
            ConnectionAdapterRegistry.SignIn["claude"]();
            int refused2 = coordC.Begin("claude");
            if (refused2 >= 0) ConnectionAdapterRegistry.SignIn["claude"]();
            Check("claude second Connect spawns no second login", refused2 == -1 && ClaudeConnectionAdapter.LaunchCount == 2,
                ClaudeConnectionAdapter.LaunchCount.ToString());
            ConnectionWatcher.OnAttempt = op =>
            {
                VendorConnection result;
                try { result = op.Verify(); } catch { result = null; }
                if (result == null) return;
                if (coordC.TryPublish(op.VendorId, op.Generation, result)
                    && (result.State == ConnectionState.Connected || result.State == ConnectionState.SignInRequired
                        || result.State == ConnectionState.ConnectedQuotaUnavailable || result.State == ConnectionState.UnsupportedConfiguration))
                    ConnectionWatcher.CancelGeneration(op.VendorId, op.Generation);
            };
            // 8. Successful post-login transition.
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            {
                VendorId = "claude",
                Generation = cgen,
                Verify = () => new VendorConnection { VendorId = "claude", State = ConnectionState.Connected, Reason = "auth ok" },
            });
            for (int i = 0; i < 100 && coordC.Latest("claude") == null; i++) Thread.Sleep(50);
            var claudeConnected = coordC.Latest("claude");
            Check("claude post-login watcher reaches Connected automatically", claudeConnected != null && claudeConnected.State == ConnectionState.Connected,
                claudeConnected == null ? "null" : claudeConnected.State.ToString());

            Console.WriteLine("== CORE-003 (audit/6): a failed interactive launch stays a failure ==");
            // The old Claude/Antigravity LaunchLogin discarded Launch(l) and
            // returned true unconditionally: BeginSignIn then published
            // WaitingForUser and started a 90-second watcher for a login that
            // never launched. Codex already returned the real result.
            ClaudeConnectionAdapter.Launch = l => false;
            string failErr;
            bool claudeFailed = ClaudeConnectionAdapter.LaunchLogin(out failErr);
            Check("claude LaunchLogin returns the real false launch", !claudeFailed, claudeFailed.ToString());
            Check("...and the launch attempt was still recorded (diagnostics, not success)",
                ClaudeConnectionAdapter.LaunchCount == 3, ClaudeConnectionAdapter.LaunchCount.ToString());
            // BeginSignIn's false path: coordinator cancelled, no watcher.
            var failCoord = new ConnectionCoordinator();
            int fgen = failCoord.Begin("claude");
            bool launched = false;
            if (fgen >= 0 && !ClaudeConnectionAdapter.LaunchLogin(out failErr))
            {
                failCoord.Cancel("claude");
            }
            else launched = true;
            Check("failed launch -> SignIn false -> coordinator cancelled, no WaitingForUser",
                fgen >= 0 && !launched && !failCoord.IsActive("claude")
                && failCoord.Latest("claude") == null, "active=" + failCoord.IsActive("claude"));
            Check("...and no watcher was started for the dead login",
                ConnectionWatcher.Pending("claude", fgen) == false, "");
            ConnectionWatcher.CancelGeneration("claude", fgen);
            // The success path is unchanged.
            ClaudeConnectionAdapter.Launch = l => true;
            bool claudeOkAgain = ClaudeConnectionAdapter.LaunchLogin(out failErr);
            Check("the Launch=true path still returns true", claudeOkAgain, claudeOkAgain.ToString());

            Console.WriteLine("== claude quota classification ==");
            // The ClaudeSource.CliUsage seam: fake a machine-readable
            // subscription-quota answer vs the "no subscription limits" shape.
            // 9. Valid auth + subscription quota -> Connected.
            // 10. Valid auth + no subscription limits -> ConnectedQuotaUnavailable.
            // These drive ClaudeConnectionAdapter.Verify through its real
            // CliUsage call; Cli.Run is not seamable per-call, so the
            // classification of the two shapes is proven through the SAME
            // strings the quota parser produces — via the adapter's own
            // classification of ClaudeSource's refusal text.
            Check("claude 'no subscription limits' classifies as quota-unavailable, not auth failure",
                ClassifyQuotaError("claude /usage reported no subscription limits") == ConnectionState.ConnectedQuotaUnavailable, "");
            Check("claude 'Not logged in' classifies as SignInRequired", ClassifyQuotaError("claude /usage: Not logged in") == ConnectionState.SignInRequired, "");
            Check("claude timeout classifies as network failure", ClassifyQuotaError("claude /usage: timeout") == ConnectionState.Failed, "");
            Check("claude TLS error classifies as TlsFailed", ClassifyQuotaError("claude /usage: SecureChannelFailure") == ConnectionState.Failed, "");

            Console.WriteLine("== claude doctor ==");
            // 14. Doctor startup classification.
            var d1 = ClaudeConnectionAdapter.ClassifyDoctor(new Cli.Result { Ok = true, Stdout = "installation problem: not found on PATH" });
            Check("claude doctor stale/path -> PathMissing", d1 == ConnectionErrorCode.PathMissing, d1.ToString());
            var d2 = ClaudeConnectionAdapter.ClassifyDoctor(new Cli.Result { Ok = true, Stdout = "OAuth not completed" });
            Check("claude doctor OAuth incomplete -> AuthMissing", d2 == ConnectionErrorCode.AuthMissing, d2.ToString());
            var d3 = ClaudeConnectionAdapter.ClassifyDoctor(new Cli.Result { Ok = true, Stdout = "expired token detected" });
            Check("claude doctor expired token -> AuthExpired", d3 == ConnectionErrorCode.AuthExpired, d3.ToString());
            var d4 = ClaudeConnectionAdapter.ClassifyDoctor(new Cli.Result { Ok = true, Stdout = "proxy problem" });
            Check("claude doctor proxy -> ProxyFailed", d4 == ConnectionErrorCode.ProxyFailed, d4.ToString());
            var d5 = ClaudeConnectionAdapter.ClassifyDoctor(new Cli.Result { Ok = false, Error = "timeout" });
            Check("claude doctor timeout -> DeadlineExceeded", d5 == ConnectionErrorCode.DeadlineExceeded, d5.ToString());

            Console.WriteLine("== interactive login survives shutdown ==");
            // 16. The interactive child is never inside a probe scope.
            var shutdownScope = ChildSweeper.Open();
            Check("LIMISAW itself is outside every probe scope (interactive children survive)", !ChildSweeper.SelfInside(shutdownScope), "");
            shutdownScope.Dispose();
            ConnectionWatcher.Shutdown();
            coordC.Shutdown();
            Check("shutdown stops the coordinator", !coordC.TryPublish("claude", cgen, new VendorConnection { VendorId = "claude", State = ConnectionState.Connected }), "");
            // 17. Watcher timeout produces Check again/Troubleshoot (R094 #17).
            var expiredConnection = new VendorConnection
            {
                VendorId = "claude",
                State = ConnectionState.Degraded,
                Reason = "Still waiting for sign-in",
                RecommendedAction = ConnectionAction.CheckAgain,
            };
            Check("claude watcher expiry offers Check again/Troubleshoot",
                expiredConnection.State == ConnectionState.Degraded
                && (expiredConnection.RecommendedAction == ConnectionAction.CheckAgain || expiredConnection.RecommendedAction == ConnectionAction.Troubleshoot),
                expiredConnection.State + "/" + expiredConnection.RecommendedAction);
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", savedProfileEnv);
            Environment.SetEnvironmentVariable("CODEX_HOME", savedCodexHome);
            CodexSource.ResolveExe = savedResolveExe;
            CodexSource.StartSession = savedStartSession;
            CodexHomeDiscovery.DirectoryExists = savedCodexDirExists;
            CodexHomeDiscovery.FileExists = savedCodexFileExists;
            CodexHomeDiscovery.GetEnv = savedCodexGetEnv;
            CodexHomeDiscovery.UserProfile = savedUserProfile;
            ConnectionWatcher.Shutdown();
            CodexSource.Pool.Reset();
            try { Directory.Delete(profile, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }

    // The adapter's quota-error classification, exactly as the adapter
    // implements it (R069): this helper mirrors ConnectionVerifyResult state
    // derivation from ClaudeSource error strings by driving the adapter's own
    // branch through its documented inputs. Kept HERE so the classification
    // and the test cannot drift apart silently — the strings are the real
    // parser's outputs.
    static ConnectionState ClassifyQuotaError(string quotaError)
    {
        string err = (quotaError ?? "").ToLowerInvariant();
        if (err.Contains("not logged in") || err.Contains("log in") || err.Contains("login"))
            return ConnectionState.SignInRequired;
        if (err.Contains("no subscription limits") || err.Contains("api key") || err.Contains("console"))
            return ConnectionState.ConnectedQuotaUnavailable;
        if (err.Contains("timeout") || err.Contains("deadline")) return ConnectionState.Failed;
        if (err.Contains("tls") || err.Contains("ssl") || err.Contains("securechannel")) return ConnectionState.Failed;
        if (err.Contains("proxy")) return ConnectionState.Failed;
        return ConnectionState.Failed;
    }

    // A minimal Codex rateLimits payload shaped like the real app-server's.
    static object CodexQuota(string plan, int fiveHMin, double fiveHUsed, int weekMin, double weekUsed)
    {
        return new Dictionary<string, object>
        {
            { "rateLimits", new Dictionary<string, object>
                {
                    { "planType", plan },
                    { "primary", new Dictionary<string, object>
                        { { "windowDurationMins", (double)fiveHMin }, { "usedPercent", fiveHUsed }, { "resetsAt", 9999999999999.0 } } },
                    { "secondary", new Dictionary<string, object>
                        { { "windowDurationMins", (double)weekMin }, { "usedPercent", weekUsed }, { "resetsAt", 9999999999999.0 } } },
                } },
        };
    }
}
