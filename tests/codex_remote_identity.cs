using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Limisaw;
using System.Windows.Forms;

// Fake app-server responses drive the real per-home sweep, projection filter,
// pooled-session invalidation, reset route and settings persistence. No live
// browser, credential, auth.json body or remote service is read.
public static class CodexRemoteIdentityTest
{
    static int checks, fails;
    static string profile, oldProfile, oldHome, oldUser;
    // Every static seam this harness overwrites, captured once so the finally
    // can put the process back exactly as it found it.
    static Func<string, string> oldResolveExe;
    static Func<string, string, CodexSource.RpcLink> oldStartSession;
    static Func<string, bool> oldOpenAuthUrl;
    static Func<CodexConnectionAdapter.LoginLaunch, bool> oldLaunch;
    static readonly Dictionary<string, string> Remote = new Dictionary<string, string>();
    static readonly Dictionary<string, int> Quota = new Dictionary<string, int>();
    static readonly List<string> Consumed = new List<string>();
    // What the fake app-server saw, per exact session key: which home ran
    // account/login/start, and which loginIds were cancelled where.
    static readonly List<string> LoginStarts = new List<string>();
    // Which login TYPE each start asked for, per exact session key: the
    // device-code retry must be provably a different vendor login type, in the
    // same home, not a rewritten browser URL.
    static readonly List<string> LoginStartTypes = new List<string>();
    static readonly List<string> LoginCancels = new List<string>();
    static readonly List<string> AppServerStarts = new List<string>();
    static readonly List<string> Opened = new List<string>();
    static readonly List<CodexConnectionAdapter.LoginLaunch> Launches =
        new List<CodexConnectionAdapter.LoginLaunch>();
    static int loginSeq;
    static string authUrl = "https://chatgpt.com/codex/device?x=1";
    static bool emitLoginId = true;
    static bool emitUserCode = true;
    static string deviceUrl = "https://chatgpt.com/codex/device";
    static string deviceUserCode = "WXYZ-4242";

    static void Check(string name, bool ok)
    {
        checks++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        if (!ok) fails++;
    }

    static void Check(string name, bool ok, string detail)
    {
        Check(name, ok);
    }

    static string Home(string name) { return Path.Combine(profile, name); }

    static void Add(string name, string account, int used)
    {
        string path = Home(name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "auth.json"), "{}");
        Remote[CodexSource.SessionPool.Key(path)] = account;
        Quota[CodexSource.SessionPool.Key(path)] = used;
    }

    static void Factory()
    {
        CodexSource.ResolveExe = _ => "fake-codex";
        CodexSource.StartSession = (exe, home) =>
        {
            lock (AppServerStarts) AppServerStarts.Add(CodexSource.SessionPool.Key(home));
            return new CodexSource.RpcLink
            {
            Call = (method, parameters, deadline) =>
            {
                string key = CodexSource.SessionPool.Key(home);
                if (method == "initialize") return J.Parse("{\"result\":{}}");
                if (method == "account/read") return J.Parse("{\"result\":{\"account\":null}}");
                if (method == "account/rateLimitResetCredit/consume")
                {
                    Consumed.Add(key);
                    return J.Parse("{\"result\":{\"outcome\":\"reset\"}}");
                }
                if (method == "account/login/start")
                {
                    // The REAL production Add-account path: a managed browser
                    // login started inside this exact session. No CLI child,
                    // no browser, no credential.
                    string newLoginId = "login-" + (++loginSeq);
                    string loginType = J.Str(J.Get(parameters, "type"));
                    lock (LoginStarts)
                    {
                        LoginStarts.Add(key + "|" + newLoginId);
                        LoginStartTypes.Add(key + "|" + loginType + "|" + newLoginId);
                    }
                    string idField = emitLoginId ? ",\"loginId\":\"" + newLoginId + "\"" : "";
                    if (loginType == "chatgptDeviceCode")
                    {
                        // The provider-supported device-code shape: a code the
                        // user types, and the page that accepts it.
                        string codeField = emitUserCode ? ",\"userCode\":\"" + deviceUserCode + "\"" : "";
                        return J.Parse("{\"result\":{\"type\":\"chatgptDeviceCode\"" + idField
                            + codeField + ",\"verificationUrl\":\"" + deviceUrl + "\"}}");
                    }
                    return J.Parse("{\"result\":{\"type\":\"chatgpt\"" + idField
                        + ",\"authUrl\":\"" + authUrl + "\"}}");
                }
                if (method == "account/login/cancel")
                {
                    object p = parameters;
                    lock (LoginCancels) LoginCancels.Add(key + "|" + J.Str(J.Get(p, "loginId")));
                    return J.Parse("{\"result\":{}}");
                }
                if (method != "account/rateLimits/read") return J.Parse("{\"result\":{}}");
                string remote = Remote[key];
                int used = Quota[key];
                string id = remote.Length == 0 ? "null" : "\"" + remote + "\"";
                return J.Parse("{\"result\":{\"accountId\":" + id
                    + ",\"rateLimits\":{\"planType\":\"plus\",\"primary\":{\"usedPercent\":"
                    + used + ",\"windowDurationMins\":300,\"resetsAt\":4500003600}}}}");
            },
            Notify = (method, parameters) => { },
            Alive = () => true,
            Drop = () => { },
            };
        };
    }

    static List<ProbeAccount> Sweep(out List<string> duplicate, out List<string> unverified)
    {
        duplicate = new List<string>(); unverified = new List<string>();
        var probed = CodexSource.Sweep(Stamp.Now + 60, 30);
        return CodexSource.DistinctRemoteAccounts(probed, duplicate, unverified);
    }

    // Primary A + secondary A, swept the way production sweeps, so the duplicate
    // set the retry path reads is populated by the real classifier.
    static string DuplicateSetup()
    {
        List<string> dup, unknown;
        Add(".codex", "A", 25);
        Add(".codex-account2", "A", 90);
        Sweep(out dup, out unknown);
        return Home(".codex-account2");
    }

    static long AuthSize(string home)
    {
        var file = new FileInfo(Path.Combine(home, "auth.json"));
        return file.Exists ? file.Length : -1;
    }

    static void Fresh()
    {
        CodexSource.Pool.Reset();
        CodexSource.ResetScheduling();
        Remote.Clear(); Quota.Clear(); Consumed.Clear();
        LoginStarts.Clear(); LoginStartTypes.Clear();
        LoginCancels.Clear(); AppServerStarts.Clear(); Opened.Clear(); Launches.Clear();
        loginSeq = 0; emitLoginId = true; emitUserCode = true;
        authUrl = "https://chatgpt.com/codex/device?x=1";
        deviceUrl = "https://chatgpt.com/codex/device";
        deviceUserCode = "WXYZ-4242";
        CodexManagedLogin.Reset();
        // No test may ever reach a real browser or a real `codex login`.
        CodexConnectionAdapter.OpenAuthUrl = u => { Opened.Add(u); return true; };
        CodexConnectionAdapter.Launch = l => { lock (Launches) Launches.Add(l); return true; };
        profile = Path.Combine(Path.GetTempPath(), "limisaw_remote_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Environment.SetEnvironmentVariable("USERPROFILE", profile);
        Environment.SetEnvironmentVariable("HOME", profile);
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        Factory();
    }

    static void Clear()
    {
        CodexSource.Pool.Reset();
        try { Directory.Delete(profile, true); } catch { }
    }

    static int Live()
    {
        var duplicate = new List<string>();
        var unverified = new List<string>();
        try
        {
            var probed = CodexSource.Sweep(Stamp.Now + 105, 35);
            var usable = CodexSource.DistinctRemoteAccounts(probed, duplicate, unverified);
            string profilePath = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            string secondary = CodexHomeDiscovery.HomeId(Path.Combine(profilePath, ".codex-account2"));
            Console.WriteLine("Live Codex homes: " + probed.Count);
            Console.WriteLine("Independent cards: " + usable.Count);
            Console.WriteLine("Duplicate homes: " + duplicate.Count);
            Console.WriteLine("Unverified homes: " + unverified.Count);
            Console.WriteLine("Account2 duplicate: " + (duplicate.Contains(secondary) ? "yes" : "not verified as duplicate"));
            foreach (var home in probed)
            {
                string reason = home.Error ?? "";
                string category = home.Ok ? "verified"
                    : reason.Contains("not found") ? "cli missing"
                    : reason.Contains("did not start") ? "app-server start failed"
                    : reason.Contains("initialize") ? "initialize failed"
                    : reason.Contains("rateLimits") ? "rate-limit read failed"
                    : reason.Contains("sweep time") || reason.Contains("cold start") ? "budget deferred"
                    : "other probe failure";
                string source = home.RemoteAccountIdentity.StartsWith("account:") ? "backend accountId"
                    : home.RemoteAccountIdentity.StartsWith("email:") ? "account/read email"
                    : "no remote identity";
                Console.WriteLine(Path.GetFileName(home.ResetHome) + ": " + category + " (" + source + ")");
            }
            return 0;
        }
        finally { CodexSource.Pool.Reset(); }
    }

    static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    static string CancelFor(string home, string loginId)
    {
        string expected = CodexSource.SessionPool.Key(home) + "|" + loginId;
        lock (LoginCancels)
            return LoginCancels.Contains(expected) ? expected : "";
    }

    static LimisawForm MakeProductionForm(out NotifyIcon tray, out string root)
    {
        root = Path.Combine(profile, "form_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new LimisawSettings(root);
        settings.Load();
        settings.SoundVolume = 0;
        settings.RefreshSeconds = 60;
        tray = new NotifyIcon();
        return new LimisawForm(root, settings, tray, new List<Theme> { new Theme() });
    }

    static void StartProductionWatcher(LimisawForm form, int generation)
    {
        MethodInfo start = typeof(LimisawForm).GetMethod("StartWatcher",
            BindingFlags.Instance | BindingFlags.NonPublic);
        start.Invoke(form, new object[]
        {
            "codex", generation,
            new Func<VendorConnection>(() => new VendorConnection
            {
                VendorId = "codex", State = ConnectionState.Failed,
                ErrorCode = ConnectionErrorCode.NetworkTimeout, Reason = "test watcher"
            })
        });
    }

    static void InvokeWatcherTick()
    {
        MethodInfo tick = typeof(ConnectionWatcher).GetMethod("Tick",
            BindingFlags.Static | BindingFlags.NonPublic);
        tick.Invoke(null, new object[] { null });
    }

    static void DisposeProductionForm(LimisawForm form, NotifyIcon tray, string root)
    {
        try { if (form != null) form.BeginShutdown(); } catch { }
        try { if (form != null) form.Dispose(); } catch { }
        try { if (tray != null) tray.Dispose(); } catch { }
        try { Directory.Delete(root, true); } catch { }
    }

    // These are production lifecycle calls: the form binds the real watcher
    // handlers, StartWatcher registers the real operation, and BeginShutdown
    // owns teardown. No managed-login cleanup API is called by these regressions.
    static void ProductionLifecycle()
    {
        Func<double> oldNow = ConnectionWatcher.Now;
        int oldTickPeriod = ConnectionWatcher.TickPeriodMs;
        Action<ConnectionWatcher.Operation> oldAttempt = ConnectionWatcher.OnAttempt;
        Action<ConnectionWatcher.Operation> oldExpire = ConnectionWatcher.OnExpire;
        Action<ConnectionWatcher.Operation> oldRemoved = ConnectionWatcher.OnRemoved;
        try
        {
            ConnectionWatcher.TickPeriodMs = Int32.MaxValue;
            string connectionSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Connections.cs"));
            Check("managed-login cancellation is structurally no-start",
                connectionSource.IndexOf("allowStart", StringComparison.Ordinal) < 0
                && connectionSource.IndexOf("internal static bool Cancel(Attempt a, double deadline)", StringComparison.Ordinal) >= 0
                && connectionSource.IndexOf("CheckoutExisting(a.HomePath, deadline)", StringComparison.Ordinal) >= 0);

            Console.WriteLine("== production watcher timeout -> exact Codex login cleanup ==");
            Fresh();
            ConnectionWatcher.Shutdown();
            double now = Stamp.Now;
            ConnectionWatcher.Now = () => now;
            NotifyIcon tray; string root;
            LimisawForm form = MakeProductionForm(out tray, out root);
            try
            {
                int generation = form.ConnCoordinator.Begin("codex");
                form.ConnCoordinator.TryProgress("codex", generation,
                    ConnectionState.WaitingForUser, "waiting");
                string home, error;
                Check("timeout setup starts managed login", CodexConnectionAdapter.LaunchAddAccount(generation, out home, out error), error ?? "");
                string loginId = CodexManagedLogin.Peek(CodexHomeDiscovery.HomeId(home)).LoginId;
                StartProductionWatcher(form, generation);
                int startsBefore = AppServerStarts.Count;
                Check("real production watcher owns generation N", ConnectionWatcher.Pending("codex", generation)
                    && CodexManagedLogin.PendingGeneration(generation), "");

                now += ConnectionWatcher.WindowS + 1;
                InvokeWatcherTick();
                bool canceled = WaitUntil(() => CancelFor(home, loginId).Length > 0, 3000);
                Check("real watcher timeout removes generation N", !ConnectionWatcher.Pending("codex", generation), "");
                Check("timeout cancels N's exact loginId exactly once", canceled
                    && LoginCancels.Count == 1 && CancelFor(home, loginId).Length > 0, "");
                Check("timeout cleanup starts no replacement app-server", AppServerStarts.Count == startsBefore, "");
                Check("timeout retires the coordinator generation", !form.ConnCoordinator.IsActive("codex")
                    && form.ConnCoordinator.CurrentGeneration("codex") != generation, "");

                int newer = form.ConnCoordinator.Begin("codex");
                string newerHome, newerError;
                bool newerStarted = CodexConnectionAdapter.LaunchAddAccount(newer, out newerHome, out newerError);
                Check("newer generation remains startable", newerStarted && form.ConnCoordinator.IsActive("codex")
                    && CodexManagedLogin.PendingGeneration(newer), newerError ?? "");
                bool stalePublished = form.ConnCoordinator.TryPublish("codex", generation,
                    new VendorConnection { VendorId = "codex", State = ConnectionState.Connected, Reason = "late N" });
                Check("late generation-N completion cannot publish", !stalePublished
                    && form.ConnCoordinator.IsActive("codex") && LoginCancels.Count == 1, "");
                Check("timeout leaves CODEX_HOME data intact", Directory.Exists(home), home);
            }
            finally { DisposeProductionForm(form, tray, root); Clear(); }

            Console.WriteLine("== production watcher supersession -> N retired, N+1 alive ==");
            Fresh();
            ConnectionWatcher.Shutdown();
            now = Stamp.Now;
            ConnectionWatcher.Now = () => now;
            form = MakeProductionForm(out tray, out root);
            try
            {
                int generation = form.ConnCoordinator.Begin("codex");
                form.ConnCoordinator.TryProgress("codex", generation,
                    ConnectionState.WaitingForUser, "waiting");
                string homeN, errorN;
                Check("supersession setup starts generation N login",
                    CodexConnectionAdapter.LaunchAddAccount(generation, out homeN, out errorN), errorN ?? "");
                string loginN = CodexManagedLogin.Peek(CodexHomeDiscovery.HomeId(homeN)).LoginId;
                string authN = Path.Combine(homeN, "auth.json");
                StartProductionWatcher(form, generation);

                // The coordinator retires N before the next action; the normal
                // StartWatcher call below then supersedes N's real watcher.
                Check("coordinator retires N before N+1", form.ConnCoordinator.TryCancel("codex", generation), "");
                int newer = form.ConnCoordinator.Begin("codex");
                File.WriteAllText(authN, "{}");
                string homeNext, errorNext;
                Check("production starts generation N+1", CodexConnectionAdapter.LaunchAddAccount(newer, out homeNext, out errorNext), errorNext ?? "");
                string loginNext = CodexManagedLogin.Peek(CodexHomeDiscovery.HomeId(homeNext)).LoginId;
                int startsBeforeCleanup = AppServerStarts.Count;
                StartProductionWatcher(form, newer);
                bool canceled = WaitUntil(() => CancelFor(homeN, loginN).Length > 0, 3000);
                Check("real watcher supersession retires N", canceled
                    && !ConnectionWatcher.Pending("codex", generation)
                    && !CodexManagedLogin.PendingGeneration(generation), "");
                Check("supersession cancels only N's exact loginId once", LoginCancels.Count == 1
                    && CancelFor(homeN, loginN).Length > 0 && CancelFor(homeNext, loginNext).Length == 0, "");
                Check("N+1 watcher and login remain active", ConnectionWatcher.Pending("codex", newer)
                    && CodexManagedLogin.PendingGeneration(newer) && form.ConnCoordinator.IsActive("codex")
                    && form.ConnCoordinator.CurrentGeneration("codex") == newer, "");
                Check("late N result cannot publish into N+1", !form.ConnCoordinator.TryPublish("codex", generation,
                    new VendorConnection { VendorId = "codex", State = ConnectionState.Failed, Reason = "late N" })
                    && form.ConnCoordinator.IsActive("codex"), "");
                Check("supersession cleanup starts no app-server", AppServerStarts.Count == startsBeforeCleanup, "");
                Check("supersession preserves credentials and CODEX_HOME directories",
                    File.Exists(authN) && Directory.Exists(homeN) && Directory.Exists(homeNext), "");
            }
            finally { DisposeProductionForm(form, tray, root); Clear(); }

            Console.WriteLine("== production BeginShutdown -> existing-session cleanup only ==");
            Fresh();
            ConnectionWatcher.Shutdown();
            now = Stamp.Now;
            ConnectionWatcher.Now = () => now;
            form = MakeProductionForm(out tray, out root);
            try
            {
                int generation = form.ConnCoordinator.Begin("codex");
                form.ConnCoordinator.TryProgress("codex", generation,
                    ConnectionState.WaitingForUser, "waiting");
                string home, error;
                Check("shutdown setup has a managed login", CodexConnectionAdapter.LaunchAddAccount(generation, out home, out error), error ?? "");
                string loginId = CodexManagedLogin.Peek(CodexHomeDiscovery.HomeId(home)).LoginId;
                StartProductionWatcher(form, generation);
                int startsBefore = AppServerStarts.Count;
                form.BeginShutdown();
                bool canceled = WaitUntil(() => CancelFor(home, loginId).Length > 0, 3000);
                Check("BeginShutdown gate is authoritative", form.ShuttingDown && form.ConnCoordinator.IsShutdown, "");
                Check("BeginShutdown stops the real watcher", ConnectionWatcher.PendingCount == 0, "");
                Check("BeginShutdown cleans the pending managed login", canceled
                    && CodexManagedLogin.PendingCount == 0 && LoginCancels.Count == 1, "");
                Check("BeginShutdown uses only the existing app-server", AppServerStarts.Count == startsBefore, "");
                bool stale = form.ConnCoordinator.TryPublish("codex", generation,
                    new VendorConnection { VendorId = "codex", State = ConnectionState.Connected, Reason = "late shutdown" });
                Check("shutdown rejects stale publication", !stale, "");
                int cancelsAfter = LoginCancels.Count;
                form.BeginShutdown();
                Check("repeated BeginShutdown is idempotent", ConnectionWatcher.PendingCount == 0
                    && CodexManagedLogin.PendingCount == 0 && LoginCancels.Count == cancelsAfter
                    && AppServerStarts.Count == startsBefore, "");
                Check("shutdown leaves CODEX_HOME data intact", Directory.Exists(home), home);
            }
            finally { DisposeProductionForm(form, tray, root); Clear(); }

            Console.WriteLine("== production BeginShutdown with missing original session ==");
            Fresh();
            ConnectionWatcher.Shutdown();
            now = Stamp.Now;
            ConnectionWatcher.Now = () => now;
            form = MakeProductionForm(out tray, out root);
            try
            {
                int generation = form.ConnCoordinator.Begin("codex");
                string home, error;
                Check("missing-session setup has a managed login", CodexConnectionAdapter.LaunchAddAccount(generation, out home, out error), error ?? "");
                string loginId = CodexManagedLogin.Peek(CodexHomeDiscovery.HomeId(home)).LoginId;
                StartProductionWatcher(form, generation);
                int startsBefore = AppServerStarts.Count;
                CodexSource.Pool.Reset();
                form.BeginShutdown();
                Check("missing-session shutdown retires local ownership", ConnectionWatcher.PendingCount == 0
                    && CodexManagedLogin.PendingCount == 0 && CodexManagedLogin.IsFenced(generation), "");
                Check("missing-session shutdown sends no cancel and starts no replacement", LoginCancels.Count == 0
                    && AppServerStarts.Count == startsBefore, "");
                Check("missing-session late completion is fenced by the coordinator gate", !form.ConnCoordinator.TryPublish("codex", generation,
                    new VendorConnection { VendorId = "codex", State = ConnectionState.Connected, Reason = "late missing session" }), "");
                int fences = CodexManagedLogin.FenceCount;
                form.BeginShutdown();
                Check("missing-session repeated shutdown is idempotent", LoginCancels.Count == 0
                    && AppServerStarts.Count == startsBefore && CodexManagedLogin.FenceCount == fences, "");
                Check("missing-session shutdown preserves CODEX_HOME data", Directory.Exists(home), home);
            }
            finally { DisposeProductionForm(form, tray, root); Clear(); }
        }
        finally
        {
            ConnectionWatcher.Shutdown();
            ConnectionWatcher.Now = oldNow;
            ConnectionWatcher.TickPeriodMs = oldTickPeriod;
            ConnectionWatcher.OnAttempt = oldAttempt;
            ConnectionWatcher.OnExpire = oldExpire;
            ConnectionWatcher.OnRemoved = oldRemoved;
            CodexManagedLogin.Reset();
            CodexSource.Pool.Reset();
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--live") return Live();
        oldUser = Environment.GetEnvironmentVariable("USERPROFILE");
        oldProfile = Environment.GetEnvironmentVariable("HOME");
        oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        oldResolveExe = CodexSource.ResolveExe;
        oldStartSession = CodexSource.StartSession;
        oldOpenAuthUrl = CodexConnectionAdapter.OpenAuthUrl;
        oldLaunch = CodexConnectionAdapter.Launch;
        try
        {
            List<string> dup, unknown;
            Fresh();
            Add(".codex", "A", 25);
            var a = Sweep(out dup, out unknown);
            Check("baseline: one home, one remote ID => one card", a.Count == 1 && dup.Count == 0);
            Clear();

            Fresh();
            Add(".codex", "A", 25); Add(".codex-account2", "B", 25);
            var b = Sweep(out dup, out unknown);
            Check("B: primary A + secondary B => two usable accounts", b.Count == 2 && dup.Count == 0);
            Check("C: different remote IDs with identical quota percentages => two accounts", b.Count == 2 && b[0].Windows[0].Remaining == b[1].Windows[0].Remaining);
            Clear();

            Fresh();
            Add(".codex", "A", 25); Add(".codex-account2", "A", 90);
            var c = Sweep(out dup, out unknown);
            string primary = Home(".codex"), secondary = Home(".codex-account2");
            Check("A: primary A + secondary A => one usable account, secondary duplicate", c.Count == 1 && dup.Count == 1);
            Check("D: same remote ID with different quota percentages => duplicate", c.Count == 1 && Quota[CodexSource.SessionPool.Key(primary)] != Quota[CodexSource.SessionPool.Key(secondary)]);
            Check("duplicate home is reused for retry", CodexConnectionAdapter.NextAccountHome() == secondary);
            Check("duplicate keeps local auth and directory", File.Exists(Path.Combine(secondary, "auth.json")));
            // ── the REAL Add-account path: a managed app-server login ─────
            string processHomeBefore = Environment.GetEnvironmentVariable("CODEX_HOME");
            string secondaryKey = CodexSource.SessionPool.Key(secondary);
            string added, addError;
            bool started = CodexConnectionAdapter.LaunchAddAccount(7, out added, out addError);
            Check("F: add reuses the intended secondary CODEX_HOME", started && added == secondary);
            Check("account/login/start ran in the exact session for that home",
                LoginStarts.Count == 1 && LoginStarts[0] == secondaryKey + "|login-1");
            Check("trusted authUrl opened exactly once",
                Opened.Count == 1 && Opened[0] == "https://chatgpt.com/codex/device?x=1");
            Check("M: no CLI `codex login` process is launched", Launches.Count == 0
                && CodexConnectionAdapter.LaunchCount == 0);
            Check("LIMISAW process CODEX_HOME is unchanged",
                Environment.GetEnvironmentVariable("CODEX_HOME") == processHomeBefore);
            Check("duplicate-home auth data remains intact",
                File.Exists(Path.Combine(secondary, "auth.json")));
            // B: the loginId is OWNED for the whole operation, not discarded
            // after the browser opened.
            var owned = CodexManagedLogin.Peek(CodexHomeDiscovery.HomeId(secondary));
            Check("loginId is owned with home, generation and session bound",
                owned != null && owned.LoginId == "login-1" && owned.Generation == 7
                && owned.HomePath == secondary && owned.Session != null && !owned.Terminal);
            Check("no cancel is sent for a login that opened", LoginCancels.Count == 0);
            var beforeBrowser = CodexConnectionAdapter.VerifyAddedHome(secondary, Stamp.Now + 10);
            Check("add waits for new browser authentication before verification",
                beforeBrowser.State == ConnectionState.WaitingForUser);
            File.AppendAllText(Path.Combine(secondary, "auth.json"), " ");
            var duplicateVerify = CodexConnectionAdapter.VerifyAddedHome(secondary, Stamp.Now + 10);
            Check("A: browser reused A never verifies as a new account",
                duplicateVerify.State == ConnectionState.DuplicateRemoteAccount);
            // C-duplicate terminal: ownership released, nothing cancelled, the
            // home and its credentials preserved for a retry.
            Check("duplicate clears managed-login ownership without cancelling",
                !CodexManagedLogin.Pending(CodexHomeDiscovery.HomeId(secondary))
                && LoginCancels.Count == 0 && File.Exists(Path.Combine(secondary, "auth.json")));
            string oldSource = CodexHomeDiscovery.HomeId(secondary);
            string firstKey = c[0].SourceId;
            Remote[CodexSource.SessionPool.Key(secondary)] = "B";
            File.AppendAllText(Path.Combine(secondary, "auth.json"), " ");
            var g = Sweep(out dup, out unknown);
            Check("G: secondary authenticates as B => second account becomes usable", g.Count == 2 && dup.Count == 0);
            Check("success does not cancel a completed vendor login", LoginCancels.Count == 0);
            Check("G: local SourceId stays stable across the remote account change", g[0].SourceId == firstKey && g[1].SourceId == oldSource);
            string reset = CodexSource.ConsumeResetCredit(g[1].ResetHome, Stamp.Now + 10);
            Check("H: reset-credit is routed to the exact selected home", reset.Contains("done") && Consumed.Count == 1 && Consumed[0] == CodexSource.SessionPool.Key(secondary));
            string item = "codex/" + g[1].SourceId + "/five_hour";
            var settings = new LimisawSettings(profile);
            settings.SetItemOrder(new List<string> { item });
            settings.Save();
            var read = new LimisawSettings(profile);
            read.Load();
            Check("tray item persists by local home key", read.ItemOrder().Count == 1 && read.ItemOrder()[0] == item);
            Clear();

            Fresh();
            Add(".codex", "A", 20); Add(".codex-account2", "", 20);
            var f = Sweep(out dup, out unknown);
            Check("E: remote identity unavailable => unverified, never guessed from quota", f.Count == 1 && dup.Count == 0 && unknown.Count == 1);
            Clear();

            // ── I: the browser opener fails → the exact loginId is cancelled ──
            Fresh();
            Add(".codex", "A", 25);
            CodexConnectionAdapter.OpenAuthUrl = u => { Opened.Add(u); return false; };
            string iHome, iErr;
            bool iOk = CodexConnectionAdapter.LaunchAddAccount(11, out iHome, out iErr);
            string iKey = CodexSource.SessionPool.Key(iHome);
            Check("I: browser-open failure fails Add account cleanly",
                !iOk && iErr == "managed_login_failed");
            Check("I: the exact loginId is cancelled on its own session",
                LoginCancels.Count == 1 && LoginCancels[0] == iKey + "|login-1");
            Check("I: the attempt is cleared", !CodexManagedLogin.Pending(CodexHomeDiscovery.HomeId(iHome))
                && CodexManagedLogin.PendingCount == 0);
            Clear();

            // ── untrusted authUrl is rejected and never opened ────────────
            Fresh();
            Add(".codex", "A", 25);
            authUrl = "https://chatgpt.com.evil.test/steal";
            string uHome, uErr;
            bool uOk = CodexConnectionAdapter.LaunchAddAccount(12, out uHome, out uErr);
            Check("untrusted authUrl is rejected and never opened",
                !uOk && Opened.Count == 0 && uErr == "managed_login_failed");
            Check("untrusted authUrl releases the vendor login it refused",
                LoginCancels.Count == 1 && LoginCancels[0].EndsWith("|login-1")
                && CodexManagedLogin.PendingCount == 0);
            Clear();

            // ── a response with no loginId is not a login this process owns ──
            Fresh();
            Add(".codex", "A", 25);
            emitLoginId = false;
            string nHome, nErr;
            bool nOk = CodexConnectionAdapter.LaunchAddAccount(13, out nHome, out nErr);
            Check("a loginId-less response is refused and owns nothing",
                !nOk && Opened.Count == 0 && CodexManagedLogin.PendingCount == 0);
            Clear();

            // ── J: watcher timeout cancels this generation's loginId ONCE ───
            Fresh();
            Add(".codex", "A", 25);
            string jHome, jErr;
            CodexConnectionAdapter.LaunchAddAccount(21, out jHome, out jErr);
            string jKey = CodexSource.SessionPool.Key(jHome);
            bool firstCancel = CodexManagedLogin.CancelGeneration(21, Stamp.Now + 10);
            bool secondCancel = CodexManagedLogin.CancelGeneration(21, Stamp.Now + 10);
            Check("J: timeout cancels the exact loginId exactly once",
                firstCancel && !secondCancel
                && LoginCancels.Count == 1 && LoginCancels[0] == jKey + "|login-1");
            Check("J: a cancel for another generation is a no-op",
                !CodexManagedLogin.CancelGeneration(22, Stamp.Now + 10)
                && LoginCancels.Count == 1);
                Check("J: manager ownership is retired after cancellation",
                    !CodexManagedLogin.PendingGeneration(21) && CodexManagedLogin.PendingCount == 0);
            Clear();

            // ── K: generation N superseded by N+1 ─────────────────────
            Fresh();
            Add(".codex", "A", 25);
            string kHome, kErr;
            CodexConnectionAdapter.LaunchAddAccount(31, out kHome, out kErr);
            string kKey = CodexSource.SessionPool.Key(kHome);
            CodexConnectionAdapter.LaunchAddAccount(32, out kHome, out kErr);
            var kOwned = CodexManagedLogin.Peek(CodexHomeDiscovery.HomeId(kHome));
            Check("K: N+1 owns the home and N is retired",
                kOwned != null && kOwned.Generation == 32 && kOwned.LoginId == "login-2"
                && !CodexManagedLogin.PendingGeneration(31));
            Check("K: N's exact login attempt is cancelled, N+1's is not",
                LoginCancels.Count == 1 && LoginCancels[0] == kKey + "|login-1");
            Check("K: N diagnostic fence is recorded and N+1 is not",
                CodexManagedLogin.IsFenced(31) && !CodexManagedLogin.IsFenced(32));
            Check("K: cancelling N cannot touch N+1 state",
                !CodexManagedLogin.CancelGeneration(31, Stamp.Now + 10)
                && CodexManagedLogin.Peek(CodexHomeDiscovery.HomeId(kHome)) != null
                && CodexManagedLogin.Peek(CodexHomeDiscovery.HomeId(kHome)).Generation == 32);
            Clear();

            // ── L: shutdown with a pending login ────────────────────
            Fresh();
            Add(".codex", "A", 25);
            string lHome, lErr;
            CodexConnectionAdapter.LaunchAddAccount(41, out lHome, out lErr);
            string lKey = CodexSource.SessionPool.Key(lHome);
            int cleaned = CodexManagedLogin.ShutdownPending(Stamp.Now + 5);
            Check("L: shutdown releases the pending login on its live session",
                cleaned == 1 && LoginCancels.Count == 1 && LoginCancels[0] == lKey + "|login-1");
            Check("L: nothing is left pending and no watcher can resurrect it",
                CodexManagedLogin.PendingCount == 0 && !CodexManagedLogin.PendingGeneration(41));
            Clear();

            // ── L/shutdown with the exact session already gone ───────────
            Fresh();
            Add(".codex", "A", 25);
            string sHome, sErr;
            CodexConnectionAdapter.LaunchAddAccount(51, out sHome, out sErr);
            int startsBefore = LoginStarts.Count;
            CodexSource.Pool.Reset();          // the exact app-server is gone
            int cleaned2 = CodexManagedLogin.ShutdownPending(Stamp.Now + 5);
            Check("L: a dead session starts no new vendor work",
                cleaned2 == 1 && LoginCancels.Count == 0 && LoginStarts.Count == startsBefore);
            Check("L: the local operation is retired and its diagnostic fence recorded",
                CodexManagedLogin.PendingCount == 0 && CodexManagedLogin.IsFenced(51));
            Clear();

            // -- a throw after registration leaks no ownership ------------
            Fresh();
            Add(".codex", "A", 25);
            CodexConnectionAdapter.OpenAuthUrl = u => { throw new InvalidOperationException("opener exploded"); };
            string tHome, tErr;
            bool tOk = CodexConnectionAdapter.LaunchAddAccount(61, out tHome, out tErr);
            Check("a throwing browser opener leaves no owned attempt behind",
                !tOk && CodexManagedLogin.PendingCount == 0 && CodexManagedLogin.IsFenced(61));
            Clear();

            // ── Retry different account: device-code in the SAME home ──────
            Console.WriteLine("== retry different account: device-code authorization ==");
            Fresh();
            string dupHome = DuplicateSetup();
            string dupId = CodexHomeDiscovery.HomeId(dupHome);
            string dupKey = CodexSource.SessionPool.Key(dupHome);
            long dupAuthBefore = AuthSize(dupHome);
            string rHome, rCode, rErr;
            bool rOk = CodexConnectionAdapter.LaunchRetryDifferentAccount(71, out rHome, out rCode, out rErr);
            Check("D: retry targets the existing .codex-account2", rOk && rHome == dupHome, rErr ?? "");
            Check("D: retry never allocates a .codex-account3",
                !Directory.Exists(Home(".codex-account3"))
                && CodexConnectionAdapter.DuplicateAccountHome() == dupHome);
            Check("E: retry asks for type=chatgptDeviceCode in the exact secondary session",
                LoginStartTypes.Count == 1 && LoginStartTypes[0] == dupKey + "|chatgptDeviceCode|login-1");
            var rOwned = CodexManagedLogin.Peek(dupId);
            Check("F: the device loginId is owned by the exact home, generation and session",
                rOwned != null && rOwned.LoginId == "login-1" && rOwned.Generation == 71
                && rOwned.HomePath == dupHome && rOwned.HomeId == dupId
                && rOwned.Session != null && !rOwned.Terminal);
            Check("the trusted verification page is opened exactly once",
                Opened.Count == 1 && Opened[0] == "https://chatgpt.com/codex/device");
            Check("the one-time code reaches the caller and the screen holder only",
                rCode == "WXYZ-4242" && CodexDeviceCodePrompt.ActiveCode == "WXYZ-4242"
                && CodexDeviceCodePrompt.CodeFor(dupId) == "WXYZ-4242");
            Check("N: the retry neither deletes nor truncates the secondary auth.json",
                AuthSize(dupHome) == dupAuthBefore && dupAuthBefore > 0);
            Check("the retry launches no CLI `codex login` child",
                Launches.Count == 0 && CodexConnectionAdapter.LaunchCount == 0);
            var dupConn = new VendorConnection
            {
                VendorId = "codex", State = ConnectionState.DuplicateRemoteAccount,
                Reason = "duplicate remote account", SelectedHomePath = dupHome, SelectedHomeId = dupId,
            };
            dupConn.DuplicateRemoteHomeIds.Add(dupId);
            string dupReport = ConnectionDiagnostics.BuildReport(dupConn, "test");
            Check("Q: the device userCode never appears in generated diagnostics",
                !dupReport.Contains("WXYZ-4242") && !dupReport.Contains("userCode"));
            // K/L: the code is approved while signed in as a DIFFERENT account.
            Remote[dupKey] = "B";
            File.AppendAllText(Path.Combine(dupHome, "auth.json"), " ");
            var promoted = CodexConnectionAdapter.VerifyAddedHome(dupHome, Stamp.Now + 10);
            Check("K: a device retry completing as remote B verifies as a real account",
                promoted.State == ConnectionState.Connected
                || promoted.State == ConnectionState.ConnectedQuotaUnavailable);
            Check("the one-time code stops existing when the attempt terminates",
                CodexDeviceCodePrompt.ActiveCode.Length == 0
                && CodexDeviceCodePrompt.CodeFor(dupId).Length == 0
                && !CodexDeviceCodePrompt.Active);
            Check("the completed device login is never cancelled",
                LoginCancels.Count == 0 && !CodexManagedLogin.Pending(dupId));
            Check("the promoted home stops being a known duplicate",
                !CodexSource.IsKnownDuplicateHome(dupId)
                && CodexConnectionAdapter.DuplicateAccountHome() == "");
            var after = Sweep(out dup, out unknown);
            Check("K: two usable Codex accounts and no duplicate remain",
                after.Count == 2 && dup.Count == 0 && unknown.Count == 0);
            Check("L: the promoted account keeps its original local SourceId and home",
                after[1].SourceId == dupId && after[1].ResetHome == dupHome);
            Check("L: the primary Codex account is untouched",
                after[0].SourceId == CodexHomeDiscovery.HomeId(Home(".codex"))
                && Remote[CodexSource.SessionPool.Key(Home(".codex"))] == "A");
            Clear();

            // ── M: the retry lands on the SAME account again ───────────────
            Console.WriteLine("== retry that authorizes the same account again ==");
            Fresh();
            string mHome = DuplicateSetup();
            string mId = CodexHomeDiscovery.HomeId(mHome);
            long mAuthBefore = AuthSize(mHome);
            string m1h, m1c, m1e;
            Check("M: the first retry starts in the duplicate home",
                CodexConnectionAdapter.LaunchRetryDifferentAccount(81, out m1h, out m1c, out m1e)
                && m1h == mHome, m1e ?? "");
            File.AppendAllText(Path.Combine(mHome, "auth.json"), " ");
            var stillDup = CodexConnectionAdapter.VerifyAddedHome(mHome, Stamp.Now + 10);
            Check("M: authorizing the same account again stays DUPLICATE_REMOTE_ACCOUNT",
                stillDup.State == ConnectionState.DuplicateRemoteAccount && !stillDup.Monitorable);
            Check("N: a duplicate retry never deletes or truncates the secondary auth.json",
                AuthSize(mHome) > mAuthBefore && mAuthBefore > 0);
            string m2h, m2c, m2e;
            Check("M: the next retry reuses .codex-account2 and creates no third home",
                CodexConnectionAdapter.LaunchRetryDifferentAccount(82, out m2h, out m2c, out m2e)
                && m2h == mHome && !Directory.Exists(Home(".codex-account3")), m2e ?? "");
            Check("M: the second device attempt is owned by its own generation",
                CodexManagedLogin.Peek(mId) != null && CodexManagedLogin.Peek(mId).Generation == 82
                && LoginStartTypes.Count == 2
                && LoginStartTypes[1].Contains("|chatgptDeviceCode|"));
            Clear();

            // ── retry with nothing to retry ────────────────────────────────
            Fresh();
            Add(".codex", "A", 25);
            Sweep(out dup, out unknown);
            string zh, zc, ze;
            bool zok = CodexConnectionAdapter.LaunchRetryDifferentAccount(151, out zh, out zc, out ze);
            Check("retry with no duplicate home refuses instead of creating one",
                !zok && ze == "no_duplicate_home" && CodexConnectionAdapter.DuplicateAccountHome() == ""
                && !Directory.Exists(Home(".codex-account2")) && LoginStartTypes.Count == 0);
            Clear();

            // ── G/H: an unusable device-code answer owns nothing ───────────
            Fresh();
            string ghHome = DuplicateSetup();
            emitLoginId = false;
            string gh, gc, ge;
            bool gok = CodexConnectionAdapter.LaunchRetryDifferentAccount(91, out gh, out gc, out ge);
            Check("G: a device response with no loginId fails safely and owns nothing",
                !gok && ge == "device_login_failed" && gc == "" && Opened.Count == 0
                && CodexManagedLogin.PendingCount == 0 && LoginCancels.Count == 0
                && !CodexDeviceCodePrompt.Active);
            emitLoginId = true; emitUserCode = false;
            string hh, hc, he;
            bool hok = CodexConnectionAdapter.LaunchRetryDifferentAccount(92, out hh, out hc, out he);
            Check("H: a device response with no userCode fails safely and owns nothing",
                !hok && he == "device_login_failed" && hc == "" && Opened.Count == 0
                && CodexManagedLogin.PendingCount == 0 && LoginCancels.Count == 0
                && !CodexDeviceCodePrompt.Active);
            Check("G/H: a refused retry leaves the duplicate home and its auth intact",
                Directory.Exists(ghHome) && AuthSize(ghHome) > 0);
            Clear();

            // ── I: an untrusted verification URL is never opened ───────────
            Fresh();
            string iiHome = DuplicateSetup();
            string iiKey = CodexSource.SessionPool.Key(iiHome);
            deviceUrl = "http://chatgpt.com/codex/device";          // not HTTPS
            string i1h, i1c, i1e;
            bool i1 = CodexConnectionAdapter.LaunchRetryDifferentAccount(101, out i1h, out i1c, out i1e);
            Check("I: a non-HTTPS verificationUrl is never opened",
                !i1 && Opened.Count == 0 && i1e == "device_login_failed");
            Check("I: its exact device loginId is cancelled on its own session and cleared",
                LoginCancels.Count == 1 && LoginCancels[0] == iiKey + "|login-1"
                && CodexManagedLogin.PendingCount == 0 && !CodexDeviceCodePrompt.Active);
            deviceUrl = "https://chatgpt.com.evil.test/device";      // untrusted host
            string i2h, i2c, i2e;
            bool i2 = CodexConnectionAdapter.LaunchRetryDifferentAccount(102, out i2h, out i2c, out i2e);
            Check("I: an untrusted HTTPS host is refused, cancelled and never opened",
                !i2 && Opened.Count == 0 && LoginCancels.Count == 2
                && LoginCancels[1] == iiKey + "|login-2"
                && CodexManagedLogin.PendingCount == 0 && !CodexDeviceCodePrompt.Active);
            Clear();

            // ── J: the verification page cannot be opened ──────────────────
            Fresh();
            string jjHome = DuplicateSetup();
            string jjKey = CodexSource.SessionPool.Key(jjHome);
            CodexConnectionAdapter.OpenAuthUrl = u => { Opened.Add(u); return false; };
            string j1h, j1c, j1e;
            bool j1 = CodexConnectionAdapter.LaunchRetryDifferentAccount(111, out j1h, out j1c, out j1e);
            Check("J: a verification page that will not open fails the retry cleanly",
                !j1 && j1e == "device_login_failed" && j1c == "");
            Check("J: no managed-login ownership and no code are orphaned",
                CodexManagedLogin.PendingCount == 0 && LoginCancels.Count == 1
                && LoginCancels[0] == jjKey + "|login-1" && !CodexDeviceCodePrompt.Active);
            Check("J: the duplicate home and its auth survive the failure",
                Directory.Exists(jjHome) && AuthSize(jjHome) > 0);
            Clear();

            // ── O: exact loginId + generation ownership for device logins ──
            Fresh();
            string oHome = DuplicateSetup();
            string oId = CodexHomeDiscovery.HomeId(oHome);
            string oKey = CodexSource.SessionPool.Key(oHome);
            string o1h, o1c, o1e;
            CodexConnectionAdapter.LaunchRetryDifferentAccount(121, out o1h, out o1c, out o1e);
            Check("O: a cancel for another generation never touches the device attempt",
                !CodexManagedLogin.CancelGeneration(122, Stamp.Now + 10)
                && LoginCancels.Count == 0 && CodexManagedLogin.PendingGeneration(121));
            Check("O: the owning generation cancels the exact device loginId exactly once",
                CodexManagedLogin.CancelGeneration(121, Stamp.Now + 10)
                && !CodexManagedLogin.CancelGeneration(121, Stamp.Now + 10)
                && LoginCancels.Count == 1 && LoginCancels[0] == oKey + "|login-1");
            Check("O: cancellation clears the ephemeral code and keeps the home",
                !CodexDeviceCodePrompt.Active && CodexManagedLogin.PendingCount == 0
                && AuthSize(oHome) > 0);
            string o2h, o2c, o2e;
            CodexConnectionAdapter.LaunchRetryDifferentAccount(131, out o2h, out o2c, out o2e);
            string o3h, o3c, o3e;
            CodexConnectionAdapter.LaunchRetryDifferentAccount(132, out o3h, out o3c, out o3e);
            var oOwned = CodexManagedLogin.Peek(oId);
            Check("O: supersession retires only the older device attempt",
                oOwned != null && oOwned.Generation == 132 && oOwned.LoginId == "login-3"
                && !CodexManagedLogin.PendingGeneration(131)
                && LoginCancels.Count == 2 && LoginCancels[1] == oKey + "|login-2");
            int oCleaned = CodexManagedLogin.ShutdownPending(Stamp.Now + 5);
            Check("O: shutdown releases the surviving device attempt and its code",
                oCleaned == 1 && LoginCancels.Count == 3 && LoginCancels[2] == oKey + "|login-3"
                && CodexManagedLogin.PendingCount == 0 && !CodexDeviceCodePrompt.Active);
            Clear();

            // ── P: an unreadable remote identity is never guessed ──────────
            Fresh();
            string pHome = DuplicateSetup();
            string pKey = CodexSource.SessionPool.Key(pHome);
            string p1h, p1c, p1e;
            CodexConnectionAdapter.LaunchRetryDifferentAccount(141, out p1h, out p1c, out p1e);
            Remote[pKey] = "";                                        // no account id exposed
            Quota[pKey] = Quota[CodexSource.SessionPool.Key(Home(".codex"))];   // identical percentages
            File.AppendAllText(Path.Combine(pHome, "auth.json"), " ");
            var pRes = CodexConnectionAdapter.VerifyAddedHome(pHome, Stamp.Now + 10);
            Check("P: an unreadable remote identity stays waiting, never distinct, never duplicate",
                pRes.State == ConnectionState.WaitingForUser && !pRes.Monitorable);
            var pSweep = Sweep(out dup, out unknown);
            Check("P: identical quota percentages never promote an unverified home",
                pSweep.Count == 1 && dup.Count == 0 && unknown.Count == 1);
            Quota[pKey] = 5;                                          // now different percentages
            var pSweep2 = Sweep(out dup, out unknown);
            Check("P: different quota percentages never promote it either",
                pSweep2.Count == 1 && dup.Count == 0 && unknown.Count == 1);
            Clear();

            ProductionLifecycle();

            string email = "private.person@example.test";
            string identity = CodexSource.RemoteIdentity(J.Parse("{}"),
                J.Parse("{\"account\":{\"type\":\"chatgpt\",\"email\":\"" + email + "\"}}"));
            var diagnostic = new VendorConnection
            {
                VendorId = "codex",
                Reason = "Duplicate remote account " + email
                    + " access_token:\"ACCESS_SENTINEL\" refresh_token:\"REFRESH_SENTINEL\"",
            };
            diagnostic.DuplicateRemoteHomeIds.Add("0123456789abcdef");
            string report = ConnectionDiagnostics.BuildReport(diagnostic, "test");
            Check("email fallback is opaque and diagnostics contain no raw email or credentials",
                identity.Length > 32 && !identity.Contains(email) && !report.Contains(email)
                && !report.Contains("ACCESS_SENTINEL") && !report.Contains("REFRESH_SENTINEL")
                && !report.Contains("auth.json"));
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw: " + ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", oldUser);
            Environment.SetEnvironmentVariable("HOME", oldProfile);
            Environment.SetEnvironmentVariable("CODEX_HOME", oldHome);
            CodexSource.ResolveExe = oldResolveExe;
            CodexSource.StartSession = oldStartSession;
            CodexConnectionAdapter.OpenAuthUrl = oldOpenAuthUrl;
            CodexConnectionAdapter.Launch = oldLaunch;
            CodexManagedLogin.Reset();
            CodexSource.Pool.Reset();
        }
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
