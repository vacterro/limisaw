using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Limisaw
{
    enum ConnectionState
    {
        Unknown = 0,
        Discovering,
        NotInstalled,
        Installed,
        Partial,
        SignInRequired,
        WaitingForUser,
        PermissionRequired,
        Verifying,
        Connected,
        ConnectedQuotaUnavailable,
        DuplicateRemoteAccount,
        Degraded,
        RepairAvailable,
        VendorUnavailable,
        UnsupportedConfiguration,
        Failed
    }

    enum ConnectionErrorCode
    {
        None = 0,
        CliMissing,
        CliNotExecutable,
        PathMissing,
        ConflictingInstallations,
        AuthMissing,
        AuthExpired,
        AuthRejected,
        PermissionRequired,
        CredentialMissing,
        CredentialRejected,
        DnsFailed,
        TlsFailed,
        ProxyFailed,
        NetworkTimeout,
        ServiceUnavailable,
        QuotaNotSupported,
        QuotaNotAvailable,
        ProtocolChanged,
        OutputInvalid,
        DeadlineExceeded,
        ResponseTooLarge,
        UnknownFailure
    }

    enum ConnectionAction
    {
        None = 0,
        Install,
        Verify,
        Connect,
        AllowAndConnect,
        OpenVendor,
        CheckAgain,
        Troubleshoot
    }

    enum ConnectionSeverity { Info = 0, Warning, Error }

    // Partial: real evidence exists (the Antigravity journal knows the vendor
    // has been here) but the component that can actually verify — the CLI — is
    // missing. Not NotInstalled (something IS present), never Connected (the
    // journal is not authentication truth): the card says exactly that.
    // R072: the auth mode the VENDOR's own configuration declares. Presence
    // and category only — no credential value ever rides this enum.
    internal enum AntigravityAuthMode { Unknown = 0, VendorSession, GeminiApiKey }

    // R072: one read-only view over the vendor's own settings surface.
    // GeminiSettingsPath/GeminiApiKeyPresent are injected seams (the harness
    // feeds fake files and a fake environment); production reads the real
    // %USERPROFILE%\.gemini\settings.json and the process environment.
    // NOTHING here returns a secret: only presence booleans and a mode name.
    internal static class AntigravityAuthSettings
    {
        // Proven on the installed CLI's machine: agy 1.1.27 writes its auth
        // selection to security.auth.selectedType in %USERPROFILE%\.gemini\settings.json
        // (observed value "gemini-api-key"); the vendor-session mode leaves
        // its account marker in %USERPROFILE%\.gemini\google_accounts.json.
        internal static Func<string> UserProfile = () =>
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME");
        internal static Func<string, bool> FileExists = path => File.Exists(path);
        internal static Func<string, string> ReadAllText = path => File.ReadAllText(path);
        internal static Func<string, bool> HasEnv = name =>
        {
            string v = Environment.GetEnvironmentVariable(name);
            return v != null && v.Trim().Length > 0;
        };

        internal const string EnvKeyName = "GEMINI_API_KEY";
        internal const string SessionMarkerFile = "google_accounts.json";

        // The local-data authority is AntigravitySource.DataDir() — the same
        // canonical directory the journal fallback reads. Tests override this
        // seam; production asks the real directory.
        internal static Func<bool> LocalDataPresentImpl = DefaultLocalDataPresent;
        static bool DefaultLocalDataPresent()
        {
            string dir = AntigravitySource.DataDir();
            return dir.Length > 0 && Directory.Exists(dir);
        }

        public static bool LocalDataPresent()
        {
            return LocalDataPresentImpl();
        }

        public static string SettingsPath()
        {
            string profile = UserProfile();
            if (string.IsNullOrEmpty(profile)) return null;
            return Path.Combine(profile, ".gemini", "settings.json");
        }

        public static string SessionMarkerPath()
        {
            string profile = UserProfile();
            if (string.IsNullOrEmpty(profile)) return null;
            return Path.Combine(profile, ".gemini", SessionMarkerFile);
        }

        public static bool GeminiApiKeyPresent()
        {
            return HasEnv(EnvKeyName);
        }

        // The declared auth mode from the vendor's own settings file. A settings
        // file that names gemini/api-key in any of the shapes the CLI has
        // shipped selects GeminiApiKey; a session marker with no api-key
        // selection is VendorSession; anything else is honestly Unknown and
        // VERIFY/TROUBLESHOOT classify from the live CLI output instead.
        public static AntigravityAuthMode ReadMode(out string declared)
        {
            declared = null;
            string path = SettingsPath();
            string raw = null;
            if (path != null && FileExists(path))
            {
                try { raw = ReadAllText(path); } catch { raw = null; }
            }
            if (!string.IsNullOrEmpty(raw))
            {
                object doc = J.Parse(raw);
                object sel = doc != null ? J.Get(doc, "security") : null;
                object auth = sel != null ? J.Get(sel, "auth") : null;
                string val = auth != null ? J.Str(J.Get(auth, "selectedType")) : null;
                if (string.IsNullOrEmpty(val))
                {
                    // Legacy flat shape: selectedType at the top level.
                    val = doc != null ? J.Str(J.Get(doc, "selectedType")) : null;
                }
                if (!string.IsNullOrEmpty(val))
                {
                    declared = val;
                    string norm = val.ToLowerInvariant();
                    if (norm.Contains("api-key") || norm.Contains("api_key") || norm.Contains("apikey")
                        || norm.Contains("gemini") || norm == "modelprovider" || norm.Contains("key"))
                        return AntigravityAuthMode.GeminiApiKey;
                    return AntigravityAuthMode.VendorSession;
                }
            }
            // No declared selection: a vendor session marker still proves the
            // vendor-session shape exists.
            string marker = SessionMarkerPath();
            if (marker != null && FileExists(marker)) return AntigravityAuthMode.VendorSession;
            return AntigravityAuthMode.Unknown;
        }
    }

    enum ConnectionStage { Discovery = 0, Authentication, Connectivity, Quota }

    class VendorDefinition
    {
        public string Id;
        public string DisplayName;
        public string Kind;
        public bool SupportsInstall;
        public bool SupportsInteractiveLogin;
        public bool SupportsQuota;
        public bool RequiresCredentialPermission;
        public string ExecutableName;
        public string InstallerCommand;
        public string InstallerSource;
        public string InstallerTarget;

        public bool IsCli { get { return Kind == "cli"; } }
    }

    static class VendorRegistry
    {
        public static readonly VendorDefinition[] All = new[]
        {
            new VendorDefinition { Id="codex", DisplayName="Codex", Kind="cli", SupportsInstall=true, SupportsInteractiveLogin=true, SupportsQuota=true, RequiresCredentialPermission=false, ExecutableName="codex", InstallerCommand="powershell -ExecutionPolicy ByPass -c \"irm https://chatgpt.com/codex/install.ps1 | iex\"", InstallerSource="chatgpt.com/codex (OpenAI)", InstallerTarget="on PATH (installer-chosen directory)" },
            new VendorDefinition { Id="claude", DisplayName="Claude Code", Kind="cli", SupportsInstall=true, SupportsInteractiveLogin=true, SupportsQuota=true, RequiresCredentialPermission=false, ExecutableName="claude", InstallerCommand="irm https://claude.ai/install.ps1 | iex", InstallerSource="claude.ai (Anthropic)", InstallerTarget=@"%USERPROFILE%\.local\bin\claude.exe" },
            new VendorDefinition { Id="antigravity", DisplayName="Antigravity", Kind="cli", SupportsInstall=true, SupportsInteractiveLogin=true, SupportsQuota=true, RequiresCredentialPermission=false, ExecutableName="agy", InstallerCommand="irm https://antigravity.google/cli/install.ps1 | iex", InstallerSource="antigravity.google (Google)", InstallerTarget=@"%LOCALAPPDATA%\agy\bin\agy.exe" },
            new VendorDefinition { Id="zcode", DisplayName="ZCode", Kind="app", SupportsInstall=false, SupportsInteractiveLogin=false, SupportsQuota=true, RequiresCredentialPermission=true, ExecutableName="", InstallerCommand="", InstallerSource="", InstallerTarget="" },
        };

        // T-50: FreeBuff is OPTIONAL — the four CORE vendors above stay always
        // present, and FreeBuff joins the list only when it is positively
        // detected. It is a CLI (its own visible login), reports an absolute
        // balance (never a percentage), and its local credential needs a
        // permission LIMISAW does not have by default.
        public static readonly VendorDefinition Freebuff = new VendorDefinition
        {
            Id = "freebuff", DisplayName = "FreeBuff", Kind = "cli",
            SupportsInstall = false, SupportsInteractiveLogin = true, SupportsQuota = true,
            RequiresCredentialPermission = true, ExecutableName = "freebuff",
            InstallerCommand = "", InstallerSource = "", InstallerTarget = "",
        };

        // The CORE vendors always shown, plus FreeBuff only when detected.
        // T-51 P1-1: detection is the PUBLISHED generation fact, never a live
        // discovery walk here — this method is reached from UI card
        // construction, which the PERF-003 contract keeps free of registry/
        // PATH I/O. Before the first generation there is nothing published, so
        // the optional vendor is simply absent and the core rows render.
        public static VendorDefinition[] Present()
        {
            bool present = ExecutableDiscovery.PublishedPresence("freebuff") == true;
            if (!present) return All;
            var list = new List<VendorDefinition>(All);
            list.Add(Freebuff);
            return list.ToArray();
        }

        public static VendorDefinition Find(string id)
        {
            if (id == "freebuff") return Freebuff;
            foreach (var v in All) if (v.Id == id) return v;
            return null;
        }
    }

    class VendorConnection
    {
        public string VendorId;
        public ConnectionState State;
        public ConnectionErrorCode ErrorCode;
        public ConnectionAction RecommendedAction;
        public bool Installed;
        public bool Authenticated;
        public bool AuthKnown;             // the adapter actually read authentication state
        public bool Monitorable;
        public bool ConnectivityOk;
        public bool ConnectivityKnown;
        public bool VerificationOk;
        public bool UserActionRequired;
        public double? LastVerifiedUtc;
        public string ResolvedPath;
        public List<string> DuplicatePaths = new List<string>();
        public List<string> CandidatePaths = new List<string>();
        public string Reason;
        public ConnectionStage Stage;
        public string CliVersion;
        // The vendor's own subscription plan label (Codex planType, e.g.
        // "plus"). DISTINCT from CliVersion: the app-server path never runs a
        // version command, so conflating the two printed "plus" as a version.
        public string Plan;
        // Codex connection target identity (CORE-001): exact canonical home +
        // its stable digest. Never derived from a display label.
        public string SelectedHomeId;
        public string SelectedHomePath;
        public List<string> DuplicateRemoteHomeIds = new List<string>();
        public List<string> UnverifiedRemoteHomeIds = new List<string>();
        // ZCode safe facts (categories only, never values).
        public string CredentialOrigin;    // environment / config / none
        public string HostCategory;        // zai / bigmodel / unknown
        public bool ConfigPresent;
        public bool ConfigAccess;
        // SRC-007 (completes SRC-006:R020): the provider entry and the
        // NON-SECRET revision identity of the config file the generation
        // represents. Both ride the shared secret-free Zcode generation
        // summary (ZcodeSource.GenerationSummary), never the credential, never
        // the config body.
        public string ZcodeProviderId;     // "builtin:zai-coding-plan" / "" — never a key
        public string ZcodeConfigRevision; // path+length+mtime token, never content
        // Antigravity safe facts (R072): the auth MODE the vendor's own
        // settings declare (a category name, never a credential), whether the
        // canonical local data directory exists (presence, never auth truth)
        // and the PRESENCE of GEMINI_API_KEY — never its value.
        public string AuthMode;            // vendorsession / gemini-api-key / unknown / ""
        public bool LocalDataPresent;
        public bool ApiKeyPresent;

        // An owned deep-enough copy. Every list is copied and every field rides
        // MemberwiseClone, so publishing a snapshot and then mutating the
        // original object cannot reach the coordinator, and mutating what
        // Latest() handed back cannot reach the coordinator either.
        public VendorConnection Clone()
        {
            var c = (VendorConnection)MemberwiseClone();
            c.CandidatePaths = new List<string>(CandidatePaths ?? new List<string>());
            c.DuplicatePaths = new List<string>(DuplicatePaths ?? new List<string>());
            c.DuplicateRemoteHomeIds = new List<string>(DuplicateRemoteHomeIds ?? new List<string>());
            c.UnverifiedRemoteHomeIds = new List<string>(UnverifiedRemoteHomeIds ?? new List<string>());
            return c;
        }
    }

    class ConnectionPresentation
    {
        public string Title;
        public string StateText;
        public string ReasonText;
        public string PrimaryActionText;
        public ConnectionSeverity Severity;
        public ConnectionAction RecommendedAction;

        public static ConnectionPresentation From(VendorConnection c)
        {
            var p = new ConnectionPresentation();
            var vd = VendorRegistry.Find(c.VendorId);
            p.Title = vd != null ? vd.DisplayName : c.VendorId;
            p.RecommendedAction = c.RecommendedAction;
            switch (c.State)
            {
                case ConnectionState.NotInstalled: p.StateText = "Not installed"; p.Severity = ConnectionSeverity.Warning; break;
                case ConnectionState.Partial: p.StateText = "Partial \u00b7 journal fallback only"; p.Severity = ConnectionSeverity.Warning; break;
                case ConnectionState.Installed: p.StateText = "Installed"; p.Severity = ConnectionSeverity.Info; break;
                case ConnectionState.SignInRequired: p.StateText = "Sign-in required"; p.Severity = ConnectionSeverity.Warning; break;
                case ConnectionState.WaitingForUser: p.StateText = "Waiting for sign-in"; p.Severity = ConnectionSeverity.Info; break;
                case ConnectionState.PermissionRequired: p.StateText = "Permission required"; p.Severity = ConnectionSeverity.Warning; break;
                case ConnectionState.Verifying: p.StateText = "Verifying"; p.Severity = ConnectionSeverity.Info; break;
                case ConnectionState.Connected: p.StateText = "Connected"; p.Severity = ConnectionSeverity.Info; break;
                case ConnectionState.ConnectedQuotaUnavailable: p.StateText = "Connected \u00b7 quota unavailable"; p.Severity = ConnectionSeverity.Warning; break;
                case ConnectionState.DuplicateRemoteAccount: p.StateText = "DUPLICATE_REMOTE_ACCOUNT"; p.Severity = ConnectionSeverity.Warning; break;
                case ConnectionState.Degraded: p.StateText = "Degraded"; p.Severity = ConnectionSeverity.Warning; break;
                case ConnectionState.RepairAvailable: p.StateText = "Repair available"; p.Severity = ConnectionSeverity.Warning; break;
                case ConnectionState.VendorUnavailable: p.StateText = "Vendor unavailable"; p.Severity = ConnectionSeverity.Error; break;
                case ConnectionState.UnsupportedConfiguration: p.StateText = "Unsupported configuration"; p.Severity = ConnectionSeverity.Error; break;
                case ConnectionState.Failed: p.StateText = "Failed"; p.Severity = ConnectionSeverity.Error; break;
                case ConnectionState.Discovering: p.StateText = "Discovering"; p.Severity = ConnectionSeverity.Info; break;
                default: p.StateText = "Unknown"; p.Severity = ConnectionSeverity.Info; break;
            }
            // The projection is the FINAL sanitization boundary: a connector
            // string reaching the card passes redaction here even when the
            // adapter forgot to sanitize. Paths stay readable on purpose.
            p.ReasonText = ConnectionDiagnostics.Redact(c.Reason ?? "", null);
            p.PrimaryActionText = ActionLabel(c.RecommendedAction, c.VendorId);
            return p;
        }

        // CORE-005: a duplicate install is a conflict whether or not the
        // adapter also set Installed (a conflicting candidate set IS installed).
        // Testing Installed first printed "found" and hid the conflict.
        public static string DiscoveryLabel(VendorConnection c)
        {
            if (c.CandidatePaths.Count > 1) return "conflict";
            return c.Installed ? "found" : "not found";
        }

        static string ActionLabel(ConnectionAction a, string vendorId)
        {
            var vd = VendorRegistry.Find(vendorId);
            switch (a)
            {
                case ConnectionAction.Install: return "Install";
                case ConnectionAction.Verify: return "Verify";
                case ConnectionAction.Connect: return vd != null && vd.IsCli ? "Sign in" : "Connect";
                case ConnectionAction.AllowAndConnect: return "Allow & connect";
                case ConnectionAction.OpenVendor: return "Open " + (vd != null ? vd.DisplayName : vendorId);
                case ConnectionAction.CheckAgain: return "Check again";
                case ConnectionAction.Troubleshoot: return "Troubleshoot";
                default: return "";
            }
        }
    }

    static class ConnectionErrorPriority
    {
        public static ConnectionErrorCode Pick(ConnectionErrorCode a, ConnectionErrorCode b)
        {
            if (a == ConnectionErrorCode.None) return b;
            if (b == ConnectionErrorCode.None) return a;
            return Rank(a) <= Rank(b) ? a : b;
        }

        public static int Rank(ConnectionErrorCode c)
        {
            switch (c)
            {
                case ConnectionErrorCode.CliMissing: return 1;
                case ConnectionErrorCode.CliNotExecutable: return 1;
                case ConnectionErrorCode.PathMissing: return 1;
                case ConnectionErrorCode.ConflictingInstallations: return 1;
                case ConnectionErrorCode.PermissionRequired: return 2;
                case ConnectionErrorCode.AuthMissing: return 3;
                case ConnectionErrorCode.AuthExpired: return 3;
                case ConnectionErrorCode.AuthRejected: return 3;
                case ConnectionErrorCode.CredentialMissing: return 3;
                case ConnectionErrorCode.CredentialRejected: return 3;
                case ConnectionErrorCode.DnsFailed: return 4;
                case ConnectionErrorCode.TlsFailed: return 4;
                case ConnectionErrorCode.ProxyFailed: return 4;
                case ConnectionErrorCode.NetworkTimeout: return 4;
                case ConnectionErrorCode.ServiceUnavailable: return 4;
                case ConnectionErrorCode.QuotaNotSupported: return 5;
                case ConnectionErrorCode.QuotaNotAvailable: return 5;
                case ConnectionErrorCode.ProtocolChanged: return 6;
                case ConnectionErrorCode.OutputInvalid: return 6;
                case ConnectionErrorCode.DeadlineExceeded: return 6;
                case ConnectionErrorCode.ResponseTooLarge: return 6;
                default: return 7;
            }
        }

        public static ConnectionAction RecommendedAction(ConnectionErrorCode code, ConnectionState state)
        {
            switch (code)
            {
                case ConnectionErrorCode.CliMissing: return ConnectionAction.Install;
                case ConnectionErrorCode.CliNotExecutable: return ConnectionAction.Install;
                case ConnectionErrorCode.PathMissing: return ConnectionAction.Install;
                case ConnectionErrorCode.ConflictingInstallations: return ConnectionAction.Troubleshoot;
                case ConnectionErrorCode.PermissionRequired: return ConnectionAction.AllowAndConnect;
                case ConnectionErrorCode.AuthMissing: return ConnectionAction.Connect;
                case ConnectionErrorCode.AuthExpired: return ConnectionAction.Connect;
                case ConnectionErrorCode.AuthRejected: return ConnectionAction.Connect;
                case ConnectionErrorCode.CredentialMissing: return ConnectionAction.OpenVendor;
                case ConnectionErrorCode.CredentialRejected: return ConnectionAction.OpenVendor;
                case ConnectionErrorCode.DnsFailed: return ConnectionAction.CheckAgain;
                case ConnectionErrorCode.TlsFailed: return ConnectionAction.CheckAgain;
                case ConnectionErrorCode.ProxyFailed: return ConnectionAction.CheckAgain;
                case ConnectionErrorCode.NetworkTimeout: return ConnectionAction.CheckAgain;
                case ConnectionErrorCode.ServiceUnavailable: return ConnectionAction.CheckAgain;
                case ConnectionErrorCode.QuotaNotSupported: return ConnectionAction.None;
                case ConnectionErrorCode.QuotaNotAvailable: return ConnectionAction.None;
                case ConnectionErrorCode.ProtocolChanged: return ConnectionAction.Troubleshoot;
                case ConnectionErrorCode.OutputInvalid: return ConnectionAction.Troubleshoot;
                default: return ConnectionAction.CheckAgain;
            }
        }
    }

    // ── Level 0 discovery ────────────────────────────────────────────────────
    // Fast, local, non-invasive: file existence over PATH sources, never a
    // process, never a network call, never a quota probe.
    static class ExecutableDiscovery
    {
        public static Func<string, string> GetEnvironmentVariable = name => Environment.GetEnvironmentVariable(name);
        public static Func<string, bool> FileExists = path => File.Exists(path);
        public static Func<bool> ZcodeAllowConfig = null;
        public static Func<string, string[]> FallbackFor = key =>
        {
            var tool = Cli.Find(key);
            if (tool == null) return new string[0];
            return tool.Fallbacks ?? new string[0];
        };
        // R072: the binary NAME for one vendor key (agy for antigravity).
        // Overridable so tests can run Level 0 without touching the registry.
        public static Func<string, string> BinaryFor = key =>
        {
            var tool = Cli.Find(key);
            return tool != null ? tool.Binary : key;
        };
        public static Func<string> GetUserPath = () =>
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Environment", false)) { if (k == null) return null; return k.GetValue("Path") as string; } } catch { return null; }
        };
        public static Func<string> GetMachinePath = () =>
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"Environment", false)) { if (k == null) return null; return k.GetValue("Path") as string; } } catch { return null; }
        };

        // PERF-003 (SRC-006:R020): filesystem probes for a refresh
        // GENERATION are counted per generation so duplicate negatives from
        // overlapping PATH sources are checked once per generation, and the
        // per-candidate FileExists count proves it. BuildConnections/Project
        // reuse the already-built generation and perform zero I/O.
        public static int RegistryOpens, PathSourcesRead, FileExistsChecks,
            ConfigReads, ConfigParses, ResolveWalks;
        internal static void ResetDiscoveryCountsForTests()
        { RegistryOpens = PathSourcesRead = FileExistsChecks = ConfigReads = ConfigParses = ResolveWalks = 0; }

        // ── PERF-003 (SRC-006:R020): the refresh-generation snapshot ────────
        // ONE secret-free discovery snapshot per refresh generation, built off
        // the UI thread (the sweep worker). Within a generation every
        // normalized executable candidate is FileExists-checked at most once,
        // each CLI executable resolves once and is reused by provider probes,
        // Cli.Status and the connection projection, and the Zcode config body
        // is read/parsed at most once (ProbeZcode caches by generation). A
        // changed or deleted executable/config is observed by the NEXT
        // generation: BeginGeneration drops everything, and the next explicit
        // Refresh builds again. The snapshot carries candidate paths, resolved
        // paths and VendorConnection Level-0 projections — never a credential
        // value.
        static readonly object SnapGate = new object();
        static Dictionary<string, List<string>> GenCandidates;
        static Dictionary<string, string> GenResolved;
        static Dictionary<string, VendorConnection> GenLevel0;
        // T-51 P1-1: OPTIONAL vendors (FreeBuff) are not in Cli.Tools, so their
        // positive presence rides the SAME generation snapshot as its own
        // published fact. `null` in the generation slot means not built.
        static bool? GenFreebuffPresent;
        public static int GenGeneration;

        // T-51 P1-1: the single optional-vendor presence probe. The generation
        // (worker thread) calls it; production reads FreebuffDiscovery, a
        // harness substitutes it so generation I/O stays exact and no real
        // machine registry/Start Menu is ever consulted by a harness.
        public static Func<string, bool> OptionalVendorPresent =
            key => key == "freebuff" && FreebuffDiscovery.HasExecutable();

        // An explicit/new Refresh creates a new generation. Cheap: bumps the
        // number and drops the previous generation's data — the next build is
        // the next generation's work, off the UI thread.
        public static void BeginGeneration()
        {
            lock (SnapGate)
            {
                GenGeneration++;
                GenCandidates = null; GenResolved = null; GenLevel0 = null;
                GenFreebuffPresent = null;
            }
        }

        public static int CurrentGeneration { get { lock (SnapGate) return GenGeneration; } }

        // T-51 P1-1: the PUBLISHED presence of one optional vendor. `null` =
        // no generation built yet, so a UI projection must use its
        // deterministic placeholder instead of walking PATH/registry itself.
        // This is what keeps VendorRegistry.Present() and the Settings row free
        // of discovery I/O: they read here, the worker generation writes here.
        public static bool? PublishedPresence(string vendorId)
        {
            lock (SnapGate)
            {
                if (GenLevel0 == null) return null;
                if (vendorId == "freebuff") return GenFreebuffPresent;
                return GenCandidates != null && GenCandidates.ContainsKey(vendorId);
            }
        }

        // Build the generation. Called from the sweep worker, NEVER the UI
        // thread: this is the only place PATH/registry/config I/O happens.
        // Cross-vendor parallelism is untouched — this runs once before the
        // providers' own probes and serializes nothing behind vendor I/O.
        public static void EnsureGeneration()
        {
            lock (SnapGate) if (GenLevel0 != null) return;
            // PERF-003 (SRC-006:R020): build one immutable candidate snapshot.
            var fresh = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Cli.Tool t in Cli.Tools) fresh[t.Key] = DiscoverFresh(t.Key);
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Cli.Tool t in Cli.Tools)
            {
                List<string> list;
                resolved[t.Key] = fresh.TryGetValue(t.Key, out list) && list.Count > 0 ? list[0] : "";
            }
            lock (SnapGate)
            {
                GenCandidates = fresh;
                GenResolved = resolved;
            }
            var level0 = new Dictionary<string, VendorConnection>(StringComparer.OrdinalIgnoreCase);
            foreach (Cli.Tool t in Cli.Tools)
                level0[t.Key] = BuildConnectionFromFresh(fresh[t.Key], t.Key);
            // The adapters' rich Level-0 (auth structure, credential origin,
            // journal fallback) — still secret-free, still no extra executable
            // discovery (GenCandidates is live above).
            level0["codex"] = CodexConnectionAdapter.DiscoverFrom(level0["codex"]);
            level0["claude"] = ClaudeConnectionAdapter.DiscoverFrom(level0["claude"]);
            level0["antigravity"] = AntigravityConnectionAdapter.DiscoverFrom(level0["antigravity"]);
            level0["zcode"] = ZcodeConnectionAdapter.Discover();
            // T-51 P1-1: the OPTIONAL vendor's positive presence is discovered
            // HERE (worker generation) and published beside the rest of the
            // Level-0 snapshot, so no UI projection ever has to walk PATH or
            // the registry itself.
            bool freebuffPresent = OptionalVendorPresent != null && OptionalVendorPresent("freebuff");
            if (freebuffPresent)
                level0["freebuff"] = FreebuffConnectionAdapter.BuildLevel0(
                    FreebuffAllowConfig != null && FreebuffAllowConfig(), true);
            lock (SnapGate)
            {
                GenLevel0 = level0;
                GenFreebuffPresent = freebuffPresent;
            }
        }

        // Null = this generation is not built (legacy behaviour applies);
        // otherwise the clone of the stored Level-0 projection, or null when
        // the vendor key is not part of the generation.
        public static VendorConnection SnapshotLevel0(string vendorId)
        {
            lock (SnapGate)
            {
                if (GenLevel0 == null) return null;
                VendorConnection vc;
                return GenLevel0.TryGetValue(vendorId, out vc) ? vc.Clone() : null;
            }
        }

        // Null = generation not built; "" = resolved to nothing; otherwise the
        // resolved executable path from the snapshot.
        public static string SnapshotResolved(string key)
        {
            lock (SnapGate)
            {
                if (GenResolved == null) return null;
                string v;
                return GenResolved.TryGetValue(key, out v) ? v : "";
            }
        }

        public static List<string> Discover(string key)
        {
            // Inside a built generation the candidate list is immutable — the
            // caller gets a copy with ZERO filesystem/registry work.
            List<string> snap;
            lock (SnapGate)
                if (GenCandidates != null && GenCandidates.TryGetValue(key, out snap))
                    return new List<string>(snap);
            return DiscoverFresh(key);
        }

        internal static List<string> DiscoverFresh(string key)
        {
            string binary = BinaryFor(key);
            var exts = new[] { ".exe", ".cmd", ".bat" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<string>();
            var sources = new List<string>();
            string procPath = GetEnvironmentVariable("PATH") ?? "";
            sources.Add(procPath);
            string userPath;
            if (GetUserPath != null) { RegistryOpens++; userPath = GetUserPath(); }
            else userPath = null;
            if (userPath != null) sources.Add(userPath);
            string machinePath;
            if (GetMachinePath != null) { RegistryOpens++; machinePath = GetMachinePath(); }
            else machinePath = null;
            if (machinePath != null) sources.Add(machinePath);
            PathSourcesRead += sources.Count;
            foreach (string src in sources)
            {
                if (string.IsNullOrEmpty(src)) continue;
                foreach (string dir in src.Split(Path.PathSeparator))
                {
                    if (dir.Length == 0) continue;
                    foreach (string ext in exts)
                    {
                        string cand;
                        try { cand = Path.Combine(dir.Trim('"'), binary + ext); } catch { continue; }
                        string norm;
                        try { norm = Path.GetFullPath(cand); } catch { norm = cand; }
                        // PERF-003: mark the normalized candidate BEFORE probing
                        // the filesystem, so overlapping PATH sources never pay
                        // the negative check twice in one generation.
                        if (seen.Contains(norm)) continue;
                        seen.Add(norm);
                        FileExistsChecks++;
                        if (FileExists(cand)) results.Add(norm);
                    }
                }
            }
            foreach (string raw in FallbackFor(key))
            {
                string cand = Environment.ExpandEnvironmentVariables(raw);
                string norm;
                try { norm = Path.GetFullPath(cand); } catch { norm = cand; }
                if (seen.Contains(norm)) continue;
                seen.Add(norm);
                FileExistsChecks++;
                if (FileExists(cand)) results.Add(norm);
            }
            return results;
        }

        // The Level-0 snapshot for one vendor. ZCode resolves through
        // ProbeZcode's own structured truth (R077) instead of a second config
        // parser; CLI vendors resolve through PATH discovery.
        //
        // PERF-003 (SRC-006:R020): when a refresh generation is already built,
        // the stored projection is returned with NO registry/PATH/config-body
        // I/O — this is what keeps BuildConnections/ProjectProbeConnections on
        // the UI thread free of discovery work. Without a generation (tests,
        // explicit fresh discovery) the real build runs below.
        public static VendorConnection BuildConnection(string vendorId)
        {
            VendorConnection snap = SnapshotLevel0(vendorId);
            if (snap != null) return snap;
            // T-51 P1-1: a generation that IS built answers for its vendors
            // even when the answer is "absent". Without this, an absent optional
            // vendor would fall through to a live PATH/registry walk on an
            // arbitrary caller — the exact UI-thread discovery I/O this ticket
            // removes.
            bool? published = PublishedPresence(vendorId);
            if (published == false)
            {
                if (vendorId == "freebuff")
                    return FreebuffConnectionAdapter.BuildLevel0(
                        FreebuffAllowConfig != null && FreebuffAllowConfig(), false);
                return BuildConnectionFromFresh(new List<string>(), vendorId);
            }
            return BuildConnectionFresh(vendorId);
        }

        // The real Level-0 build: always performs discovery. Used to construct
        // a generation and by user-triggered actions that must see a binary
        // installed mid-session without waiting for the next Refresh.
        public static VendorConnection BuildConnectionFresh(string vendorId)
        {
            if (vendorId == "zcode") return BuildZcodeConnection();
            // T-50: FreeBuff's Level-0 carries the permission/credential
            // category, so it uses its own projection rather than the bare
            // executable walk. `freebuffReadConfig` comes from the same seam the
            // discovery generation uses.
            if (vendorId == "freebuff")
                return FreebuffConnectionAdapter.BuildLevel0(
                    FreebuffAllowConfig != null && FreebuffAllowConfig(), HasExecutableFor("freebuff"));
            return BuildConnectionFromFresh(DiscoverFresh(vendorId), vendorId);
        }

        // T-50: the FreeBuff credential permission seam, assigned by the form
        // from LIMISAW.ini (same pattern as ZcodeAllowConfig) so the projection
        // and the sweep agree on one owner.
        public static Func<bool> FreebuffAllowConfig = null;

        // Positive presence for one discovery key, used by projections that need
        // "is this executable installed" without a full Level-0 build.
        static bool HasExecutableFor(string key)
        {
            if (key == "freebuff") return FreebuffDiscovery.HasExecutable();
            return DiscoverFresh(key).Count > 0;
        }

        static VendorConnection BuildConnectionFromFresh(List<string> cands, string vendorId)
        {
            var vc = new VendorConnection { VendorId = vendorId };
            vc.CandidatePaths = new List<string>(cands);
            vc.Stage = ConnectionStage.Discovery;
            if (cands.Count == 0)
            {
                vc.State = ConnectionState.NotInstalled; vc.ErrorCode = ConnectionErrorCode.CliMissing;
                vc.RecommendedAction = ConnectionAction.Install; vc.Reason = "Not installed";
            }
            else if (cands.Count > 1)
            {
                vc.State = ConnectionState.Failed; vc.ErrorCode = ConnectionErrorCode.ConflictingInstallations;
                vc.RecommendedAction = ConnectionAction.Troubleshoot; vc.Reason = "Multiple installations found";
                vc.DuplicatePaths = new List<string>(cands);
                vc.ResolvedPath = cands[0];
                vc.Installed = true;
            }
            else
            {
                vc.State = ConnectionState.Installed; vc.Installed = true;
                vc.ResolvedPath = cands[0]; vc.Reason = "Installed";
                vc.ErrorCode = ConnectionErrorCode.None;
                vc.RecommendedAction = ConnectionAction.Verify;
            }
            return vc;
        }

        // R077: the six deterministic refusals ProbeZcode.Resolve already
        // knows, mapped onto connection states with no generic Unknown for a
        // known refusal. Resolve's State field is the authority; its key VALUE
        // never enters the snapshot.
        static VendorConnection BuildZcodeConnection()
        {
            var vc = new VendorConnection { VendorId = "zcode", Stage = ConnectionStage.Discovery };
            bool allow = ZcodeAllowConfig != null ? ZcodeAllowConfig() : false;
            vc.ConfigAccess = allow;
            // SRC-007 (completes SRC-006:R020): the Level-0 projection
            // consumes the SAME shared secret-free generation summary the
            // worker consumes — one summary per generation, no second config
            // walk. Presence/origin/provider/host/revision all come from it.
            ZcodeSource.ZcodeGenerationSummary sum = ZcodeSource.GenerationSummary(allow);
            vc.ConfigPresent = sum.ConfigPresent;
            vc.ZcodeProviderId = sum.ProviderId;
            vc.ZcodeConfigRevision = sum.ConfigRevision;
            var key = ZcodeSource.Resolve(allow);
            switch (key.State ?? "")
            {
                case "env":
                    vc.Installed = true; vc.State = ConnectionState.Verifying;
                    vc.ErrorCode = ConnectionErrorCode.None;
                    vc.Reason = "Environment credential present";
                    vc.CredentialOrigin = "environment";
                    vc.HostCategory = "unknown";
                    break;
                case "config":
                    vc.Installed = true; vc.State = ConnectionState.Verifying;
                    vc.ErrorCode = ConnectionErrorCode.None;
                    vc.Reason = "ZCode config credential available";
                    vc.CredentialOrigin = "config";
                    vc.HostCategory = HostCategory(key.Host);
                    break;
                case "config-denied":
                    vc.State = ConnectionState.PermissionRequired;
                    vc.ErrorCode = ConnectionErrorCode.PermissionRequired;
                    vc.RecommendedAction = ConnectionAction.AllowAndConnect;
                    vc.Reason = "LIMISAW found ZCode configuration but does not read its credential without permission.";
                    vc.UserActionRequired = true;
                    vc.CredentialOrigin = "none";
                    vc.HostCategory = sum.HostCategory;
                    break;
                case "no-provider":
                    vc.State = ConnectionState.UnsupportedConfiguration;
                    vc.ErrorCode = ConnectionErrorCode.OutputInvalid;
                    vc.RecommendedAction = ConnectionAction.OpenVendor;
                    vc.Reason = key.Refusal ?? "ZCode config has no supported provider";
                    vc.CredentialOrigin = "none";
                    vc.HostCategory = sum.HostCategory;
                    break;
                case "no-key":
                    vc.State = ConnectionState.SignInRequired;
                    vc.ErrorCode = ConnectionErrorCode.CredentialMissing;
                    vc.RecommendedAction = ConnectionAction.OpenVendor;
                    vc.Reason = key.Refusal ?? "The Z.ai / BigModel provider has no readable key";
                    vc.CredentialOrigin = "none";
                    vc.HostCategory = sum.HostCategory;
                    break;
                default: // not-detected: no config and no env credential
                    vc.State = ConnectionState.SignInRequired;
                    vc.ErrorCode = ConnectionErrorCode.CredentialMissing;
                    vc.RecommendedAction = ConnectionAction.OpenVendor;
                    vc.Reason = "No ZCode credential found.";
                    vc.CredentialOrigin = "none";
                    vc.HostCategory = "unknown";
                    break;
            }
            return vc;
        }

        static string HostCategory(string host)
        {
            if (string.IsNullOrEmpty(host)) return "unknown";
            if (host.IndexOf("bigmodel", StringComparison.OrdinalIgnoreCase) >= 0) return "bigmodel";
            if (host.IndexOf("zai", StringComparison.OrdinalIgnoreCase) >= 0) return "zai";
            return "unknown";
        }
    }

    // ── coordinator ──────────────────────────────────────────────────────────
    // One place owns connection state. Every dictionary access holds one small
    // private gate; vendor I/O never happens under it. Generations are the
    // authority: Begin mints the next one, Cancel and Shutdown supersede every
    // live one, and a completion publishes only while its generation is
    // current — so a cancelled or stale completion can never repaint a newer
    // result, and a repaint never has to guess which result won.
    class ConnectionCoordinator
    {
        readonly object gate = new object();
        readonly Dictionary<string, int> generations = new Dictionary<string, int>();
        readonly Dictionary<string, VendorConnection> latest = new Dictionary<string, VendorConnection>();
        readonly Dictionary<string, bool> active = new Dictionary<string, bool>();
        bool shutdown;

        public int Begin(string vendorId)
        {
            lock (gate)
            {
                if (shutdown) return -1;
                bool a;
                if (active.TryGetValue(vendorId, out a) && a) return -1;
                int g;
                generations.TryGetValue(vendorId, out g);
                g += 1;
                generations[vendorId] = g;
                active[vendorId] = true;
                return g;
            }
        }

        public bool TryPublish(string vendorId, int generation, VendorConnection conn)
        {
            lock (gate)
            {
                if (shutdown) return false;
                int cur;
                generations.TryGetValue(vendorId, out cur);
                if (generation != cur) return false;
                // CORE-002 (audit/6): a TERMINAL completion requires the
                // operation to still be ACTIVE. A completion whose generation
                // numerically matches but whose slot was already released
                // (a late retry of an earlier attempt, a worker whose watcher
                // was superseded and re-begun) must not overwrite the
                // authoritative snapshot — the generation alone is no longer
                // sufficient authority.
                bool a;
                if (!active.TryGetValue(vendorId, out a) || !a) return false;
                latest[vendorId] = conn == null ? null : conn.Clone();
                active[vendorId] = false;
                return true;
            }
        }

        // CORE-002 (audit/6): a RETRYABLE watcher observation updates what the
        // user sees but must NOT release the vendor slot. The operation stays
        // active under the same generation, so the next scheduled attempt
        // still belongs to the same ownership and a second Begin for the
        // vendor is still refused while the cadence runs. A terminal result
        // is never routed here — the watcher caller classifies first.
        // CORE-002 (audit/7): a matching generation is NECESSARY but not
        // SUFFICIENT authority — a stale same-generation callback whose
        // operation was already released by a terminal completion must not
        // mutate Latest. Only an operation that still owns its ACTIVE slot
        // may publish (same gate TryPublish applies).
        // CORE-002 (audit/7): a matching generation is NECESSARY but not
        // SUFFICIENT authority — a stale same-generation callback whose
        // operation was already released by a terminal completion must not
        // mutate Latest. Only an operation that still owns its ACTIVE slot
        // may publish (same gate TryPublish applies).
        public bool TryProgressResult(string vendorId, int generation, VendorConnection conn)
        {
            lock (gate)
            {
                if (shutdown) return false;
                int cur;
                generations.TryGetValue(vendorId, out cur);
                if (generation != cur) return false;
                bool a;
                if (!active.TryGetValue(vendorId, out a) || !a) return false;
                latest[vendorId] = conn == null ? null : conn.Clone();
                return true;
            }
        }

        // An in-flight stage change (WaitingForUser, Verifying) that keeps the
        // operation active: the card updates without handing the vendor slot
        // back. CORE-002 (audit/7): the publication requires the matching
        // generation to still own an ACTIVE operation, so a late
        // same-generation callback cannot overwrite a terminal result.
        // An in-flight stage change (WaitingForUser, Verifying) that keeps the
        // operation active: the card updates without handing the vendor slot
        // back. CORE-002 (audit/7): the publication requires the matching
        // generation to still own an ACTIVE operation, so a late
        // same-generation callback cannot overwrite a terminal result.
        public bool TryProgress(string vendorId, int generation, ConnectionState state, string reason)
        {
            lock (gate)
            {
                if (shutdown) return false;
                int cur;
                generations.TryGetValue(vendorId, out cur);
                if (generation != cur) return false;
                bool a;
                if (!active.TryGetValue(vendorId, out a) || !a) return false;
                VendorConnection v;
                latest.TryGetValue(vendorId, out v);
                v = v == null ? new VendorConnection { VendorId = vendorId } : v.Clone();
                v.State = state;
                if (reason != null) v.Reason = reason;
                latest[vendorId] = v;
                return true;
            }
        }

        // CORE-001: a fresh read-only observation — a successful periodic quota
        // read, or a materially changed Level-0 discovery fact — is authoritative
        // Level-1 truth even though no interactive verification is running.
        // Observe refuses to clobber an operation that is in flight (its own
        // generation is the authority), and otherwise advances the generation so
        // a stale completion can never overwrite the fresh fact. The snapshot is
        // cloned, so the caller's object stays its own.
        public bool Observe(string vendorId, VendorConnection conn)
        {
            lock (gate)
            {
                if (shutdown) return false;
                bool a;
                if (active.TryGetValue(vendorId, out a) && a) return false;
                int g;
                generations.TryGetValue(vendorId, out g);
                generations[vendorId] = g + 1;
                latest[vendorId] = conn == null ? null : conn.Clone();
                active[vendorId] = false;
                return true;
            }
        }

        // Cancelling supersedes the running operation's generation, so the one
        // thing a cancelled completion cannot do is publish. Unscoped by
        // design: the caller means "cancel whichever operation is current".
        public void Cancel(string vendorId)
        {
            lock (gate)
            {
                int g;
                generations.TryGetValue(vendorId, out g);
                generations[vendorId] = g + 1;
                active[vendorId] = false;
            }
        }

        // CORE-002 (audit/6): generation-scoped cancel. A stale caller —
        // most importantly a watcher expiry whose operation was already
        // superseded by a newer generation — must be a NO-OP for the newer
        // operation: the unscoped Cancel above would otherwise invalidate a
        // generation the caller never owned. Returns true when the supplied
        // generation was still current and is now superseded.
        public bool TryCancel(string vendorId, int generation)
        {
            lock (gate)
            {
                if (shutdown) return false;
                int cur;
                generations.TryGetValue(vendorId, out cur);
                if (generation != cur) return false;
                generations[vendorId] = cur + 1;
                active[vendorId] = false;
                return true;
            }
        }

        // CORE-002 (audit/6): one atomic invalidation for a vendor whose
        // CREDENTIAL AUTHORITY changed (ZcodeReadConfig today; the future
        // FreeBuff permission model tomorrow). Forget() alone only dropped
        // the last snapshot: an in-flight verification under the OLD
        // permission stayed publication-authorized, which is exactly wrong
        // when the permission change means "you may no longer read that
        // credential". This advances the generation (old workers become
        // stale), releases ownership, drops the stale verdict and lets the
        // caller cancel the matching watcher. Generic on purpose — no
        // ZCode-only lifecycle hack.
        public bool InvalidateOperation(string vendorId)
        {
            lock (gate)
            {
                if (shutdown) return false;
                int g;
                generations.TryGetValue(vendorId, out g);
                generations[vendorId] = g + 1;
                active[vendorId] = false;
                latest.Remove(vendorId);
                return true;
            }
        }

        public void Forget(string vendorId)
        {
            lock (gate)
            {
                latest.Remove(vendorId);
            }
        }

        public void Shutdown()
        {
            lock (gate)
            {
                shutdown = true;
                var keys = new List<string>(active.Keys);
                foreach (var k in keys) active[k] = false;
                // Every live generation is invalidated: a completion that lands
                // after shutdown is stale by construction.
                var gkeys = new List<string>(generations.Keys);
                foreach (var k in gkeys) generations[k] += 1;
            }
        }

        public VendorConnection Latest(string vendorId)
        {
            lock (gate)
            {
                VendorConnection v;
                if (latest.TryGetValue(vendorId, out v)) return v == null ? null : v.Clone();
                return null;
            }
        }

        public bool IsActive(string vendorId)
        {
            lock (gate) { bool a; return active.TryGetValue(vendorId, out a) && a; }
        }

        public int CurrentGeneration(string vendorId)
        {
            lock (gate) { int g; return generations.TryGetValue(vendorId, out g) ? g : 0; }
        }

        public bool IsShutdown { get { lock (gate) return shutdown; } }
    }

    // ── diagnostics ──────────────────────────────────────────────────────────
    static class ConnectionDiagnostics
    {
        // One central helper for the diagnostics timestamp contract: epoch
        // seconds -> UTC DateTime -> ISO-8601 "o". Never local, never a raw
        // double rendered with a DateTime format.
        public static string IsoUtc(double? epoch)
        {
            if (!epoch.HasValue || epoch.Value <= 0) return null;
            var t = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch.Value);
            return t.ToString("o", CultureInfo.InvariantCulture);
        }

        // The real kernel version. Environment.OSVersion.VersionString answers
        // 6.2.9200 on any modern Windows whose process has no supportedOS
        // manifest entry — LIMISAW.exe is built without a manifest, so it always
        // lied "Windows 8". RtlGetVersion reports the truth (10.0.19045); the
        // managed value stays a bounded fallback for a machine where ntdll is
        // somehow unavailable.
        public static string WindowsVersionString()
        {
            try
            {
                var v = new Native.OSVERSIONINFOEX();
                v.dwOSVersionInfoSize = Marshal.SizeOf(typeof(Native.OSVERSIONINFOEX));
                if (Native.RtlGetVersion(ref v) == 0)
                    return string.Format(CultureInfo.InvariantCulture, "Microsoft Windows NT {0}.{1}.{2}",
                        v.dwMajorVersion, v.dwMinorVersion, v.dwBuildNumber);
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }

        public static string BuildReport(VendorConnection c, string limisawVersion)
        {
            var lines = new List<string>();
            lines.Add("LIMISAW " + limisawVersion);
            lines.Add("Windows " + WindowsVersionString() + " " + (Environment.Is64BitOperatingSystem ? "x64" : "x86"));
            lines.Add("vendor: " + c.VendorId);
            lines.Add("state: " + c.State);
            lines.Add("error: " + c.ErrorCode);
            lines.Add("action: " + c.RecommendedAction);
            lines.Add("stage: " + c.Stage);
            lines.Add("installation: " + (c.Installed ? "installed" : "not installed"));
            lines.Add("authentication: " + (c.AuthKnown ? (c.Authenticated ? "authenticated" : "not authenticated") : "unknown"));
            lines.Add("monitorable: " + (c.Monitorable ? "yes" : "no"));
            lines.Add("connectivity: " + (c.ConnectivityKnown ? (c.ConnectivityOk ? "ok" : "failed") : "unknown"));
            lines.Add("verification: " + (c.VerificationOk ? "ok" : "not verified"));
            if (!string.IsNullOrEmpty(c.CliVersion)) lines.Add("cli version: " + c.CliVersion);
            if (!string.IsNullOrEmpty(c.ResolvedPath)) lines.Add("path: " + c.ResolvedPath);
            if (c.DuplicatePaths != null && c.DuplicatePaths.Count > 1)
                lines.Add("duplicates: " + string.Join("; ", c.DuplicatePaths.ToArray()));
            lines.Add("lastVerified: " + (IsoUtc(c.LastVerifiedUtc) ?? "never"));
            if (!string.IsNullOrEmpty(c.Reason)) lines.Add("reason: " + c.Reason);
            if (c.VendorId == "zcode")
            {
                lines.Add("config present: " + (c.ConfigPresent ? "yes" : "no"));
                lines.Add("config access: " + (c.ConfigAccess ? "on" : "off"));
                lines.Add("credential origin: " + CredentialOriginCategory(c));
                lines.Add("host category: " + (c.HostCategory ?? "unknown"));
                // SRC-007 (completes SRC-006:R020): the secret-free generation
                // summary's provider + revision identity in the report too.
                if (!string.IsNullOrEmpty(c.ZcodeProviderId))
                    lines.Add("config provider: " + c.ZcodeProviderId);
                if (!string.IsNullOrEmpty(c.ZcodeConfigRevision))
                    lines.Add("config revision: " + c.ZcodeConfigRevision);
            }
            if (c.VendorId == "codex")
            {
                if (!string.IsNullOrEmpty(c.Plan)) lines.Add("plan: " + c.Plan);
                if (!string.IsNullOrEmpty(c.SelectedHomeId)) lines.Add("home id: " + c.SelectedHomeId);
                if (!string.IsNullOrEmpty(c.SelectedHomePath)) lines.Add("home: " + c.SelectedHomePath);
                foreach (string id in c.DuplicateRemoteHomeIds)
                    lines.Add("DUPLICATE_REMOTE_ACCOUNT home id: " + id + " — signed in to an account already shown above; sign in with a different ChatGPT account to add another Codex account");
                foreach (string id in c.UnverifiedRemoteHomeIds)
                    lines.Add("remote identity unverified for home id: " + id);
            }
            string raw = string.Join("\r\n", lines.ToArray());
            return Redact(raw, null);
        }

        // The ACTIVE credential origin: a config FILE existing is presence,
        // not origin. Origin is what the adapter actually resolved, stored on
        // the snapshot; only when the snapshot carries nothing do we fall back
        // to the env check.
        static string CredentialOriginCategory(VendorConnection c)
        {
            if (!string.IsNullOrEmpty(c.CredentialOrigin)) return c.CredentialOrigin;
            if (ZcodeSource.HasEnvKey()) return "environment";
            return "none";
        }

        // The one redaction boundary every connector string passes. Protects
        // known secrets, Bearer shapes, Authorization headers and safely
        // detectable api-key assignments. Windows paths are NOT redacted soup:
        // the assignment patterns require a key-ish name and a separator.
        //
        // KnownSecrets is the "secret the adapter possesses" channel: the env
        // credential LIMISAW was explicitly given. A config credential never
        // reaches this list — the adapter redacts it where it resolves it.
        public static Func<string[]> KnownSecrets = CollectKnownSecrets;

        static string[] CollectKnownSecrets()
        {
            var list = new List<string>();
            foreach (string n in new[] { ZcodeSource.EnvPrimary, ZcodeSource.EnvAlternate, AntigravityAuthSettings.EnvKeyName })
            {
                string v = (Environment.GetEnvironmentVariable(n) ?? "").Trim();
                if (v.Length >= 4) list.Add(v);
            }
            return list.ToArray();
        }

        public static string Redact(string text, string knownSecret)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string r = text;
            if (!string.IsNullOrEmpty(knownSecret) && knownSecret.Length >= 4) r = r.Replace(knownSecret, "<key>");
            foreach (string s in KnownSecrets())
                r = r.Replace(s, "<key>");
            r = Regex.Replace(r, @"(?i)Bearer\s+[A-Za-z0-9\-_\.]+", "Bearer <redacted>");
            r = Regex.Replace(r, @"(?i)Authorization\s*:\s*[^\r\n]+", "Authorization: <redacted>");
            // api_key = "..." / apiKey: '...' / Authorization-style assignments
            // with a quoted value. Name + separator + quote: a bare path or a
            // status sentence never matches.
            r = Regex.Replace(r, @"(?i)\b(api[_\-\s]?key|access[_\-\s]?token|refresh[_\-\s]?token|client[_\-\s]?secret)""?\s*[=:]\s*""[^""\r\n]{4,}""",
                m => m.Value.Substring(0, m.Value.IndexOfAny(new[] { '=', ':' })) + "= <redacted>");
            r = Regex.Replace(r, @"(?i)\b(api[_\-\s]?key|access[_\-\s]?token|refresh[_\-\s]?token|client[_\-\s]?secret)""?\s*[=:]\s*'[^'\r\n]{4,}'",
                m => m.Value.Substring(0, m.Value.IndexOfAny(new[] { '=', ':' })) + "= <redacted>");
            r = Regex.Replace(r, @"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", "<email>");
            if (!string.IsNullOrEmpty(knownSecret) && knownSecret.Length >= 4) r = r.Replace(knownSecret, "<key>");
            foreach (string s in KnownSecrets())
                r = r.Replace(s, "<key>");
            return r;
        }

        public static bool TryCopyToClipboard(string text, out string note)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(text);
                note = "Diagnostics copied";
                return true;
            }
            catch (Exception ex)
            {
                note = "Copy failed: " + ex.GetType().Name;
                return false;
            }
        }
    }

    // ── Level-0 proof counters ───────────────────────────────────────────────
    // "Level 0 starts nothing" used to be inferred from file-existence calls,
    // which proves nothing about processes, sockets or quota probes. These
    // counters are the observable seam: the Level 0 test asserts all three are
    // still zero after a full discovery pass.
    internal static class ConnectionProbeCounters
    {
        static int processStarts, networkCalls, quotaProbes;

        public static int ProcessStarts { get { return processStarts; } }
        public static int NetworkCalls { get { return networkCalls; } }
        public static int QuotaProbes { get { return quotaProbes; } }

        public static void Reset()
        {
            lock (typeof(ConnectionProbeCounters))
            { processStarts = 0; networkCalls = 0; quotaProbes = 0; }
        }

        public static void CountProcess() { lock (typeof(ConnectionProbeCounters)) processStarts++; }
        public static void CountNetwork() { lock (typeof(ConnectionProbeCounters)) networkCalls++; }
        public static void CountQuotaProbe() { lock (typeof(ConnectionProbeCounters)) quotaProbes++; }
    }

    // ── process ownership ────────────────────────────────────────────────────
    // PROBE children are bounded, hidden, redirected and adopted into a
    // ChildSweeper scope that dies with LIMISAW. INTERACTIVE children are
    // visible, shell-executed and structurally outside every scope: they are
    // the user's process and survive LIMISAW closing.
    static class ConnectionProcessLauncher
    {
        public static Cli.Result RunProbe(string exe, string[] args, double deadline, string cwd)
        {
            ConnectionProbeCounters.CountProcess();
            return Cli.Run(exe, args, deadline, cwd);
        }

        // R081: OPTIONAL non-owning exit observation. Observing a process is
        // NOT ownership: the interactive child stays user-owned (outside
        // ChildSweeper, never killed at shutdown), only LIMISAW's own wait
        // handle is disposed at shutdown, and the exit event carries NO exit
        // code — it is a signal that "something happened", never authority
        // about authentication (a 0 exit code is not login success).
        public class InteractiveLaunch
        {
            public int ProcessId = -1;
            // Fires at most once when the observed child EXITS. May be null:
            // observation is optional. (Initialized to null explicitly, the
            // same convention as CodexSource.StartSession — tests assign it.)
            public Action Exited = null;
            // W2-003: the observation object itself. The launch holds it so
            // the subscription outlives the launcher's stack frame; disposing
            // it detaches the handler and drops LIMISAW's wrapper only — the
            // child is never touched.
            internal ProcessExitObserver Observer = null;
        }

        // W2-003/R011: the event-driven exit observer. The old shape parked a
        // ThreadPool worker on a blocking auto-reset-event wait for the entire
        // life of the child — one blocked pool worker per running login, and
        // the events were enabled AFTER subscribing (a child exiting inside
        // that window could be lost). Now: subscribe, enable, reconcile
        // HasExited, and let the OS raise Exited on a framework callback
        // thread. The observer owns ONLY the wrapper, the subscription and its
        // own once-gate — never the child.
        //
        // ONE LIFECYCLE AUTHORITY: the Interlocked once-gate. Whichever of
        // FireOnce/Dispose wins the gate performs the whole completion —
        // detach the handler, dispose LIMISAW's wrapper — and the loser does
        // nothing, so a Dispose racing a legitimately-won completion can
        // never suppress its callback, and no path double-disposes.
        internal sealed class ProcessExitObserver : IDisposable
        {
            readonly Process Proc;
            readonly Action OnExit;
            int Fired; // Interlocked once-gate: at most one completion, ever

            internal ProcessExitObserver(Process proc, Action onExit)
            {
                Proc = proc;
                OnExit = onExit;
                // Subscribe BEFORE enabling: a child that exits between the
                // two calls still gets the event delivered once raising is
                // enabled; the HasExited reconcile below covers the reverse
                // ordering (exited before subscription).
                Proc.Exited += OnProcExited;
                // W2-003: an EnableRaisingEvents failure is NOT swallowed. If
                // raising cannot be enabled there is no reliable observation,
                // and pretending otherwise loses exit signals silently. The
                // constructor throws; DefaultStartInteractive's caller keeps
                // the launch successful, marks the observer unavailable,
                // disposes the wrapper it no longer owns, and the normal
                // bounded watcher cadence remains the fallback.
                Proc.EnableRaisingEvents = true;
                // Reconcile the HasExited race: the child may already be gone
                // (or the event already delivered) by the time we get here.
                bool gone = false;
                try { gone = Proc.HasExited; } catch { gone = false; }
                Track(this);
                if (gone) FireOnce();
            }

            void OnProcExited(object sender, EventArgs e) { FireOnce(); }

            void FireOnce()
            {
                if (Interlocked.Exchange(ref Fired, 1) != 0) return;
                Untrack(this);
                try { Proc.Exited -= OnProcExited; } catch { }
                try { Proc.Dispose(); } catch { }
                Action cb = OnExit;
                if (cb != null) { try { cb(); } catch { } }
            }

            // Observer shutdown: suppress any later callback, detach the
            // handler, dispose the wrapper. NEVER Kill, NEVER CloseMainWindow,
            // never a ChildSweeper adoption — the child is the user's. If a
            // completion already won the gate, this does nothing: one
            // lifecycle authority, no suppressed legit callback, no leak.
            public void Dispose()
            {
                if (Interlocked.Exchange(ref Fired, 1) != 0) return;
                Untrack(this);
                try { Proc.Exited -= OnProcExited; } catch { }
                try { Proc.Dispose(); } catch { }
            }

            internal bool CallbackFired { get { return Volatile.Read(ref Fired) == 1; } }
        }

        // W2-003/R011: the application-owned observer registry. Interactive
        // launches are fire-and-forget at their call sites (Antigravity's exit
        // poke), so the observer resources are tracked GLOBALLY for the one
        // cleanup that legitimately owns them: application shutdown. The
        // registry holds observer resources only — never the children. Every
        // completion (exit callback or explicit Dispose) self-removes, so the
        // live set is exactly the observers still waiting on a child.
        static readonly object ObserverGate = new object();
        static readonly List<ProcessExitObserver> LiveObservers = new List<ProcessExitObserver>();

        static void Track(ProcessExitObserver o)
        { lock (ObserverGate) LiveObservers.Add(o); }

        static void Untrack(ProcessExitObserver o)
        { lock (ObserverGate) LiveObservers.Remove(o); }

        internal static int LiveObserverCount
        { get { lock (ObserverGate) return LiveObservers.Count; } }

        // Test-visible membership: is this observer still tracked?
        internal static bool Tracks(ProcessExitObserver o)
        { lock (ObserverGate) return LiveObservers.Contains(o); }

        // Application shutdown: detach and dispose every tracked observer
        // (LIMISAW's wrappers only — the user's interactive login children are
        // never touched). Idempotent.
        internal static void ShutdownObservers()
        {
            ProcessExitObserver[] snapshot;
            lock (ObserverGate) { snapshot = LiveObservers.ToArray(); LiveObservers.Clear(); }
            foreach (ProcessExitObserver o in snapshot) { try { o.Dispose(); } catch { } }
        }

        // The one production observation seam. Tests substitute it so exit
        // timing is deterministic without any real child process.
        internal static Func<string, string, Action, InteractiveLaunch, string> StartInteractiveImpl = DefaultStartInteractive;

        static string DefaultStartInteractive(string exe, string args, Action onExit, InteractiveLaunch launch)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
                Process p = Process.Start(psi);
                if (p == null) return "could_not_start";
                launch.ProcessId = p.Id;
                if (onExit != null)
                {
                    // W2-003: observation is OPTIONAL. A failure inside the
                    // observer setup must not convert a successful
                    // Process.Start into a login-launch failure — and the
                    // wrapper the observer no longer owns must not leak.
                    try
                    {
                        launch.Observer = new ProcessExitObserver(p, onExit);
                        return null; // the observer owns the wrapper now
                    }
                    catch { try { p.Dispose(); } catch { } return null; }
                }
                p.Dispose();   // LIMISAW's handle only; the user's process stays
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name; }
        }

        // Visible, shell-executed, USER-OWNED interactive launch with optional
        // exit observation. The child is never adopted, never killed, and its
        // output is never captured for credential scraping.
        public static InteractiveLaunch StartInteractive(string exe, string args, Action onExit, out string error)
        {
            ConnectionProbeCounters.CountProcess();
            error = null;
            var launch = new InteractiveLaunch();
            error = StartInteractiveImpl(exe, args, onExit, launch);
            if (error != null) return null;
            return launch;
        }

        // A visible interactive launch whose child carries extra environment
        // (UseShellExecute cannot set any), via a PowerShell wrapper. The child
        // chain stays user-owned: no scope adopts it.
        public static bool StartInteractiveWithEnv(string exe, string args, string envName, string envValue, out string error)
        {
            ConnectionProbeCounters.CountProcess();
            error = null;
            try
            {
                string command = "$env:" + envName + " = '" + (envValue ?? "").Replace("'", "''")
                    + "'; & '" + exe.Replace("'", "''") + "'";
                if (!string.IsNullOrEmpty(args)) command += " " + args.Replace("\"", "`\"");
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoLogo -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "`\"") + "\"")
                { UseShellExecute = true };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex) { error = ex.GetType().Name; return false; }
        }

        public static bool StartInteractiveCli(CliInfo cli, out string error)
        {
            error = null;
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoLogo -ExecutionPolicy Bypass -NoExit -Command \"" + cli.PowerShell.Replace("\"", "`\"") + "\"")
                { UseShellExecute = true };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex) { error = ex.GetType().Name; return false; }
        }
    }

    // ── CLI verification result shapes ───────────────────────────────────────
    // Free-form output is never state: every CLI step parses into one of these
    // and the adapters key on the structured result.
    internal class CliVersionResult
    {
        public bool Ok;
        public string Version = "";
        public ConnectionErrorCode Error = ConnectionErrorCode.None;
        public string Detail;
    }

    internal enum CliAuthCategory { Unknown = 0, LoggedIn, LoggedOut, Expired, Rejected }

    internal class CliAuthResult
    {
        public CliAuthCategory Category;
        public string Detail;
        public bool MachineReadable;
    }

    internal class ConnectionVerifyResult
    {
        public ConnectionState State = ConnectionState.Failed;
        public ConnectionErrorCode Error = ConnectionErrorCode.UnknownFailure;
        public string Reason = "";
        public bool Authenticated;
        public bool Monitorable;
        public string CliVersion;
        public string Plan;      // vendor planType ("plus"), never a CLI version
        public string Detail;
        public string RemoteAccountIdentity;
    }

    // ── ZCode launcher discovery ─────────────────────────────────────────────
    // Open ZCode must use a POSITIVELY discovered local launcher before falling
    // back to the official URL. Positive evidence only: an App Paths
    // registration resolvable to an existing executable, or a Start Menu .lnk
    // resolvable to an existing target. No disk scans, no guessed exe paths.
    internal static class ZcodeLauncherDiscovery
    {
        internal static Func<string, bool> FileExists = File.Exists;
        internal static Func<string> AppPathsTarget = ReadAppPathsTarget;
        internal static Func<string, string> ResolveLnk = ResolveShortcutTarget;

        static string ReadAppPathsTarget()
        {
            string leaf = "zcode.exe";
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + leaf, false))
                    {
                        if (k == null) continue;
                        string v = (k.GetValue(null) ?? k.GetValue("Path")) as string;
                        if (string.IsNullOrEmpty(v)) continue;
                        v = Environment.ExpandEnvironmentVariables(v.Trim().Trim('"'));
                        if (FileExists(v)) return v;
                    }
                }
                catch { }
            }
            return null;
        }

        // Resolves a .lnk through the shell so the target can be PROVEN to
        // exist instead of trusting the link's name.
        static string ResolveShortcutTarget(string lnkPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                string target = shortcut.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch { return null; }
        }

        static readonly string[] StartMenuDirs =
        {
            @"%APPDATA%\Microsoft\Windows\Start Menu\Programs",
            @"%ProgramData%\Microsoft\Windows\Start Menu\Programs",
        };

        // The positively discovered launcher, or null when none qualifies.
        public static string Find()
        {
            string app = AppPathsTarget();
            if (!string.IsNullOrEmpty(app)) return app;
            foreach (string raw in StartMenuDirs)
            {
                string dir;
                try { dir = Environment.ExpandEnvironmentVariables(raw); } catch { continue; }
                if (!Directory.Exists(dir)) continue;
                string[] links;
                try { links = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (string link in links)
                {
                    if (Path.GetFileName(link).IndexOf("zcode", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string target = ResolveLnk(link);
                    if (!string.IsNullOrEmpty(target) && FileExists(target)) return target;
                }
            }
            return null;
        }
    }

    // ── ZCode adapter ────────────────────────────────────────────────────────
    static class ZcodeConnectionAdapter
    {
        public const string OfficialUrl = "https://z.ai/manage-apikey";

        public static VendorConnection Discover()
        {
            return ExecutableDiscovery.BuildConnection("zcode");
        }

        public static VendorConnection Verify(VendorConnection baseConn, bool allowConfig)
        {
            var key = ZcodeSource.Resolve(allowConfig);
            // SRC-007 (completes SRC-006:R020): the worker consumes the SAME
            // shared secret-free generation summary the Level-0 projection
            // consumes — one summary per generation, no extra config walk.
            ZcodeSource.ZcodeGenerationSummary sum = ZcodeSource.GenerationSummary(allowConfig);
            baseConn.ConfigAccess = allowConfig;
            baseConn.ConfigPresent = sum.ConfigPresent;
            baseConn.ZcodeProviderId = sum.ProviderId;
            baseConn.ZcodeConfigRevision = sum.ConfigRevision;
            if (key.Value.Length == 0)
            {
                if (key.State == "config-denied")
                {
                    baseConn.State = ConnectionState.PermissionRequired;
                    baseConn.ErrorCode = ConnectionErrorCode.PermissionRequired;
                    baseConn.RecommendedAction = ConnectionAction.AllowAndConnect;
                    baseConn.Reason = key.Refusal;
                    baseConn.CredentialOrigin = "none";
                    baseConn.HostCategory = sum.HostCategory;
                    baseConn.Stage = ConnectionStage.Authentication;
                    return baseConn;
                }
                baseConn.State = ConnectionState.SignInRequired;
                baseConn.ErrorCode = ConnectionErrorCode.CredentialMissing;
                baseConn.RecommendedAction = ConnectionAction.OpenVendor;
                baseConn.Reason = key.Refusal ?? "Sign-in required";
                baseConn.CredentialOrigin = "none";
                baseConn.HostCategory = sum.HostCategory;
                baseConn.Stage = ConnectionStage.Authentication;
                return baseConn;
            }
            baseConn.CredentialOrigin = sum.CredentialOrigin;
            baseConn.HostCategory = string.IsNullOrEmpty(key.Host)
                ? "unknown"
                : (key.Host.IndexOf("bigmodel", StringComparison.OrdinalIgnoreCase) >= 0 ? "bigmodel" : "zai");
            double deadline = Stamp.Now + 12;
            var acc = ZcodeSource.Probe(deadline, allowConfig);
            if (acc.Ok)
            {
                baseConn.State = ConnectionState.Connected;
                baseConn.ErrorCode = ConnectionErrorCode.None;
                baseConn.Authenticated = true; baseConn.AuthKnown = true;
                baseConn.Monitorable = true; baseConn.VerificationOk = true;
                baseConn.ConnectivityOk = true; baseConn.ConnectivityKnown = true;
                baseConn.LastVerifiedUtc = Stamp.Now;
                baseConn.RecommendedAction = ConnectionAction.None;
                baseConn.Reason = "Connected";
                baseConn.Stage = ConnectionStage.Quota;
                return baseConn;
            }
            baseConn.Authenticated = true; baseConn.AuthKnown = true; // a credential was resolved and sent
            string err = (acc.Error ?? "").ToLowerInvariant();
            if (err.Contains("401") || err.Contains("403") || err.Contains("credential") || err.Contains("unauthorized")
                || err.Contains("invalid") && err.Contains("key"))
            {
                baseConn.State = ConnectionState.Failed;
                baseConn.ErrorCode = ConnectionErrorCode.CredentialRejected;
                baseConn.RecommendedAction = ConnectionAction.OpenVendor;
                baseConn.Reason = ConnectionDiagnostics.Redact(acc.Error, key.Value);
                baseConn.ConnectivityOk = true; baseConn.ConnectivityKnown = true;
                baseConn.Stage = ConnectionStage.Authentication;
                return baseConn;
            }
            if (err.Contains("timeout") || err.Contains("deadline"))
            {
                baseConn.State = ConnectionState.Failed;
                baseConn.ErrorCode = ConnectionErrorCode.NetworkTimeout;
                baseConn.RecommendedAction = ConnectionAction.CheckAgain;
                baseConn.Reason = ConnectionDiagnostics.Redact(acc.Error, key.Value);
                baseConn.Stage = ConnectionStage.Connectivity;
                return baseConn;
            }
            if (err.Contains("tls") || err.Contains("ssl") || err.Contains("securechannel"))
            {
                baseConn.State = ConnectionState.Failed;
                baseConn.ErrorCode = ConnectionErrorCode.TlsFailed;
                baseConn.RecommendedAction = ConnectionAction.CheckAgain;
                baseConn.Reason = ConnectionDiagnostics.Redact(acc.Error, key.Value);
                baseConn.Stage = ConnectionStage.Connectivity;
                return baseConn;
            }
            if (err.Contains("protocol") || err.Contains("no readable window") || err.Contains("did not return json"))
            {
                baseConn.State = ConnectionState.Failed;
                baseConn.ErrorCode = ConnectionErrorCode.ProtocolChanged;
                baseConn.RecommendedAction = ConnectionAction.Troubleshoot;
                baseConn.Reason = ConnectionDiagnostics.Redact(acc.Error, key.Value);
                baseConn.Stage = ConnectionStage.Quota;
                return baseConn;
            }
            if (err.Contains("response_too_large"))
            {
                baseConn.State = ConnectionState.Failed;
                baseConn.ErrorCode = ConnectionErrorCode.ResponseTooLarge;
                baseConn.RecommendedAction = ConnectionAction.Troubleshoot;
                baseConn.Reason = ConnectionDiagnostics.Redact(acc.Error, key.Value);
                baseConn.Stage = ConnectionStage.Quota;
                return baseConn;
            }
            baseConn.State = ConnectionState.Failed;
            baseConn.ErrorCode = ConnectionErrorCode.ServiceUnavailable;
            baseConn.RecommendedAction = ConnectionAction.CheckAgain;
            baseConn.Reason = ConnectionDiagnostics.Redact(acc.Error ?? "Service unavailable", key.Value);
            baseConn.Stage = ConnectionStage.Connectivity;
            return baseConn;
        }

        public static bool TryAllowAndConnect(LimisawSettings settings, bool userConfirmed, out string note)
        {
            note = "";
            if (!userConfirmed) { note = "Cancelled"; return false; }
            bool want = true;
            bool prev = settings.ZcodeReadConfig;
            settings.ZcodeReadConfig = want;
            var r = settings.SaveSettings();
            if (!r.Saved)
            {
                settings.ZcodeReadConfig = prev;
                note = "Permission was not saved — " + r.Reason;
                return false;
            }
            note = "Permission saved — verifying";
            return true;
        }

        // Positive local launcher first; the official page only when no
        // reliable launcher exists. Never a guessed executable path.
        public static bool TryOpenVendor(out string note)
        {
            note = "";
            try
            {
                string launcher = ZcodeLauncherDiscovery.Find();
                if (!string.IsNullOrEmpty(launcher))
                {
                    var psi = new ProcessStartInfo(launcher) { UseShellExecute = true };
                    Process.Start(psi);
                    note = "Opened ZCode";
                    return true;
                }
                var url = new ProcessStartInfo(OfficialUrl) { UseShellExecute = true };
                Process.Start(url);
                note = "Opened " + OfficialUrl;
                return true;
            }
            catch (Exception ex) { note = ex.GetType().Name; return false; }
        }
    }

    // ── Codex connection homes ───────────────────────────────────────────────
    // Connection onboarding needs a TARGET HOME even before auth exists, while
    // CodexSource.Homes() must keep returning only authenticated homes for
    // quota probing. This is the connection-specific view: every home is
    // listed, each carries the exact canonical path, its stable digest id and
    // the PRESENCE of auth structure (never its contents).
    internal class CodexTargetHome
    {
        public string Path;
        public string Id;
        public string Label;
        public bool HasAuthStructure;
        public bool IsDefaultHome;
    }

    internal static class CodexHomeDiscovery
    {
        internal static Func<string, bool> DirectoryExists = Directory.Exists;
        internal static Func<string, bool> FileExists = File.Exists;
        internal static Func<string, string> GetEnv = name => Environment.GetEnvironmentVariable(name);
        internal static Func<string> UserProfile = () =>
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME");

        public static List<CodexTargetHome> Homes()
        {
            var found = new List<CodexTargetHome>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string profile = UserProfile();
            if (string.IsNullOrEmpty(profile)) return found;

            Action<string, string, bool> add = (dir, label, isDefault) =>
            {
                if (string.IsNullOrEmpty(dir)) return;
                string full;
                try { full = Path.GetFullPath(dir); } catch { return; }
                string norm = full.TrimEnd('\\', '/').ToLowerInvariant();
                if (!seen.Add(norm)) return;
                if (!DirectoryExists(full)) return;
                found.Add(new CodexTargetHome
                {
                    Path = full,
                    Id = HomeId(full),
                    Label = label,
                    HasAuthStructure = FileExists(Path.Combine(full, "auth.json")),
                    IsDefaultHome = isDefault,
                });
            };

            string envHome = GetEnv("CODEX_HOME");
            if (!string.IsNullOrEmpty(envHome)) add(envHome, "Codex", false);
            add(Path.Combine(profile, ".codex"), "Codex", true);
            string[] siblings;
            try { siblings = Directory.GetDirectories(profile, ".codex-*"); }
            catch { siblings = new string[0]; }
            Array.Sort(siblings, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in siblings)
            {
                string label = Path.GetFileName(dir).Replace(".codex-", "").Replace("_", " ");
                add(dir, label.Length > 0 ? label : "Codex", false);
            }
            return found;
        }

        public static string HomeId(string full)
        {
            string norm = (full ?? "").TrimEnd('\\', '/').ToLowerInvariant();
            byte[] bytes = System.Security.Cryptography.SHA256.Create()
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(norm));
            var sb = new System.Text.StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }

    // ── Codex device-code prompt (EPHEMERAL) ─────────────────────────────────
    // The one-time user code a device-code login mints is screen data for the
    // seconds the attempt lives, and nothing else. It is never written to
    // settings, never put on a VendorConnection (so it cannot reach a
    // diagnostic report), never logged, and never kept after the attempt it
    // belongs to reaches a terminal state — which is why every terminal
    // transition in CodexManagedLogin clears it by home id.
    internal static class CodexDeviceCodePrompt
    {
        static readonly object Gate = new object();
        static string OwnerHomeId = "", OwnerHomePath = "", Code = "", Url = "";

        internal static void Set(string homeId, string homePath, string userCode, string url)
        {
            lock (Gate)
            { OwnerHomeId = homeId ?? ""; OwnerHomePath = homePath ?? ""; Code = userCode ?? ""; Url = url ?? ""; }
        }

        // Terminal for THIS home: the code stops existing the moment the login
        // it authorizes is over, whatever the outcome was.
        internal static void ClearHome(string homeId)
        {
            lock (Gate)
            {
                if (string.IsNullOrEmpty(homeId) || OwnerHomeId != homeId) return;
                OwnerHomeId = OwnerHomePath = Code = Url = "";
            }
        }

        internal static void Clear()
        {
            lock (Gate) { OwnerHomeId = OwnerHomePath = Code = Url = ""; }
        }

        // "" when nothing is pending: callers render nothing rather than a
        // stale code from an attempt that already ended.
        internal static string CodeFor(string homeId)
        {
            lock (Gate) { return OwnerHomeId.Length > 0 && OwnerHomeId == homeId ? Code : ""; }
        }

        internal static string ActiveCode { get { lock (Gate) return Code; } }
        internal static string ActiveHomePath { get { lock (Gate) return OwnerHomePath; } }
        internal static bool Active { get { lock (Gate) return Code.Length > 0; } }
    }

    // ── Codex managed-login ownership ────────────────────────────────────────
    // A managed browser login is a LIVE vendor operation, not a fire-and-forget
    // launch. Until it reaches a terminal state LIMISAW owns the vendor's
    // loginId and must be able to cancel exactly that attempt on exactly the
    // app-server session that minted it. The binding is explicit:
    //
    //   canonical home identity + onboarding generation + loginId + session
    //
    // Nothing here reads or moves a credential; the loginId is the vendor's own
    // handle for an unfinished browser flow and is never persisted.
    internal static class CodexManagedLogin
    {
        internal class Attempt
        {
            public string HomeId;
            public string HomePath;
            public string LoginId;
            public int Generation;
            // The EXACT app-server session that minted this loginId. A different
            // Entry for the same home is a different vendor process which never
            // knew this loginId — cancelling there would be a lie, so identity
            // is compared by reference, never by home path.
            public CodexSource.SessionPool.Entry Session;
            // Terminal: the operation is over. Set exactly once; the transition
            // is what removes runtime ownership.
            public bool Terminal;
        }

        static readonly object Gate = new object();
        // At most one live attempt per canonical home. The coordinator already
        // refuses a second concurrent codex operation, so a second registration
        // for the same home can only be a superseding one.
        static readonly Dictionary<string, Attempt> ByHome =
            new Dictionary<string, Attempt>(StringComparer.Ordinal);
        // Generations whose attempt was retired WITHOUT a completed vendor
        // cancel round trip (the session was already gone, or shutdown won).
        // Diagnostic only: ConnectionCoordinator owns publication authority.
        static readonly HashSet<int> Fenced = new HashSet<int>();

        // Observability seams for the regression matrix: how many vendor
        // cancels actually went out, and for which loginIds. Never a credential.
        internal static int CancelCount, FenceCount, CompleteCount;
        internal static readonly List<string> CancelledLoginIds = new List<string>();

        // Records a cancel that a caller already performed on the lease it was
        // holding (the browser-open failure path cancels inline rather than
        // re-leasing the session it is standing on).
        internal static void NoteCancelled(string loginId)
        {
            lock (Gate) { CancelCount++; CancelledLoginIds.Add(loginId); }
        }

        internal static void Reset()
        {
            lock (Gate)
            {
                ByHome.Clear(); Fenced.Clear();
                CancelCount = FenceCount = CompleteCount = 0;
                CancelledLoginIds.Clear();
            }
            CodexDeviceCodePrompt.Clear();
        }

        // Registered BEFORE the browser is opened: from this instant the
        // loginId is owned, so every later exit path has something exact to
        // cancel. A registration for a home that already holds a live attempt
        // supersedes it — the older one is returned so the caller can retire it.
        internal static Attempt Register(string homeId, string homePath, string loginId,
            int generation, CodexSource.SessionPool.Entry session, out Attempt superseded)
        {
            var a = new Attempt
            {
                HomeId = homeId, HomePath = homePath, LoginId = loginId,
                Generation = generation, Session = session,
            };
            lock (Gate)
            {
                ByHome.TryGetValue(homeId, out superseded);
                ByHome[homeId] = a;
                // A generation that starts a login is live again by definition.
                Fenced.Remove(generation);
            }
            return a;
        }

        internal static Attempt Peek(string homeId)
        {
            lock (Gate) { Attempt a; return ByHome.TryGetValue(homeId, out a) ? a : null; }
        }

        internal static bool Pending(string homeId) { return Peek(homeId) != null; }

        internal static bool PendingGeneration(int generation)
        {
            lock (Gate)
            {
                foreach (var kv in ByHome) if (kv.Value.Generation == generation) return true;
                return false;
            }
        }

        internal static int PendingCount { get { lock (Gate) return ByHome.Count; } }

        // Claim the attempt for a terminal transition. Exactly one caller wins:
        // whoever takes it owns what happens next, and every later taker gets
        // null. This is what makes "cancelled once" true under a timeout that
        // races a supersession.
        internal static Attempt TakeByHome(string homeId)
        {
            lock (Gate)
            {
                Attempt a;
                if (!ByHome.TryGetValue(homeId, out a)) return null;
                ByHome.Remove(homeId);
                a.Terminal = true;
                // Terminal: whatever one-time code this attempt showed stops
                // existing here, before any caller can read it again.
                CodexDeviceCodePrompt.ClearHome(homeId);
                return a;
            }
        }

        internal static Attempt TakeByGeneration(int generation)
        {
            lock (Gate)
            {
                foreach (var kv in ByHome)
                {
                    if (kv.Value.Generation != generation) continue;
                    var a = kv.Value;
                    ByHome.Remove(kv.Key);
                    a.Terminal = true;
                    CodexDeviceCodePrompt.ClearHome(a.HomeId);
                    return a;
                }
                return null;
            }
        }

        // SUCCESS / DUPLICATE: the vendor login itself FINISHED. Ownership is
        // dropped and no cancel is sent — cancelling a completed login would
        // attack the credential the user just created.
        internal static bool Complete(string homeId)
        {
            var a = TakeByHome(homeId);
            if (a == null) return false;
            lock (Gate) CompleteCount++;
            return true;
        }

        // Record that vendor cancellation was impossible. This is diagnostic
        // lifecycle state; ConnectionCoordinator generation checks reject late
        // publication and remain the single correctness authority.
        internal static void Fence(int generation)
        {
            lock (Gate) { if (Fenced.Add(generation)) FenceCount++; }
        }

        internal static bool IsFenced(int generation)
        {
            lock (Gate) return Fenced.Contains(generation);
        }

        // Cancel EXACTLY this attempt on EXACTLY its existing session. A dead
        // attempt never justifies starting a new app-server child. When the
        // exact session is unavailable the operation is retired locally and
        // its generation is fenced as diagnostic state.
        internal static bool Cancel(Attempt a, double deadline)
        {
            if (a == null || string.IsNullOrEmpty(a.LoginId)) return false;
            if (Stamp.Now >= deadline) { Fence(a.Generation); return false; }
            CodexSource.SessionPool.SessionLease lease = null;
            try
            {
                lease = CodexSource.Pool.CheckoutExisting(a.HomePath, deadline);
            }
            catch { lease = null; }
            if (lease == null) { Fence(a.Generation); return false; }
            using (lease)
            {
                // Reference identity, not the home path: a recreated session is
                // a different vendor process that never minted this loginId.
                if (!ReferenceEquals(lease.Entry, a.Session)) { Fence(a.Generation); return false; }
                try
                {
                    object reply = lease.Entry.Link.Call("account/login/cancel",
                        new Dictionary<string, object> { { "loginId", a.LoginId } }, deadline);
                    if (reply == null) { lease.Retire(); Fence(a.Generation); return false; }
                }
                catch { lease.Retire(); Fence(a.Generation); return false; }
            }
            lock (Gate) { CancelCount++; CancelledLoginIds.Add(a.LoginId); }
            return true;
        }

        // TIMEOUT / SUPERSESSION: cancel only this generation's attempt, once.
        // A generation that owns nothing is already terminal — nothing to undo.
        internal static bool CancelGeneration(int generation, double deadline)
        {
            var a = TakeByGeneration(generation);
            if (a == null) return false;
            bool told = Cancel(a, deadline);
            // The home stops waiting for a distinct account either way: the
            // pending marker belongs to the dead operation, not to the user's
            // directory, which keeps its files and credentials untouched.
            CodexSource.CancelDistinctAccount(a.HomeId);
            if (!told) Fence(a.Generation);
            return true;
        }

        // SHUTDOWN: bounded, best-effort, and never vendor-starting. Whatever
        // cannot be told inside the budget is fenced for diagnostics; the
        // coordinator's shutdown/generation gate rejects late publication.
        internal static int ShutdownPending(double deadline)
        {
            List<Attempt> all = new List<Attempt>();
            lock (Gate)
            {
                foreach (var kv in ByHome) { kv.Value.Terminal = true; all.Add(kv.Value); }
                ByHome.Clear();
            }
            foreach (var a in all) CodexDeviceCodePrompt.ClearHome(a.HomeId);
            foreach (var a in all)
            {
                if (!Cancel(a, deadline)) Fence(a.Generation);
                CodexSource.CancelDistinctAccount(a.HomeId);
            }
            return all.Count;
        }
    }

    // ── Codex connection adapter ─────────────────────────────────────────────
    internal static class CodexConnectionAdapter
    {
        internal const int VerifyDeadlineS = 20;

        // Seams. The launch seam records the launch a test asked for instead of
        // starting a real browser-auth child; the verify seam lets a fake
        // app-server answer for the real SessionPool.
        internal class LoginLaunch
        {
            public string Program;
            public string Arguments;
            public string EnvName;
            public string EnvValue;
        }
        internal static Func<LoginLaunch, bool> Launch = DefaultLaunch;
        // The managed app-server browser flow is used for additional homes.
        // Unlike `codex login`, it does not pre-emptively logout an existing
        // duplicate home before the user completes a different sign-in.
        internal static Func<string, bool> OpenAuthUrl = DefaultOpenAuthUrl;
        internal static int LaunchCount;
        internal static readonly List<LoginLaunch> Launched = new List<LoginLaunch>();

        static bool DefaultLaunch(LoginLaunch l)
        {
            string error;
            if (l.EnvName == null)
            {
                var launch = ConnectionProcessLauncher.StartInteractive(l.Program, l.Arguments, null, out error);
                return launch != null;
            }
            return ConnectionProcessLauncher.StartInteractiveWithEnv(l.Program, l.Arguments, l.EnvName, l.EnvValue, out error);
        }

        static bool DefaultOpenAuthUrl(string url)
        {
            try
            {
                var psi = new ProcessStartInfo(url) { UseShellExecute = true };
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        static bool TrustedAuthUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "chatgpt.com" || host.EndsWith(".chatgpt.com")
                || host == "openai.com" || host.EndsWith(".openai.com");
        }

        internal static bool StartManagedLogin(string home, int generation, double deadline)
        {
            string ignored;
            return StartVendorLogin(home, generation, deadline, false, out ignored);
        }

        // The EXPLICIT "different account" retry. The browser flow above can
        // complete instantly as the account the browser already holds, which is
        // exactly the duplicate the user is trying to escape. Device-code
        // authorization is the vendor's own supported way out: the code is
        // approved inside whichever ChatGPT session the user chooses, so the
        // decision stops being the browser's. Same home, same ownership model,
        // same cancellation identity — only the login type differs.
        internal static bool StartDeviceCodeLogin(string home, int generation, double deadline, out string userCode)
        {
            return StartVendorLogin(home, generation, deadline, true, out userCode);
        }

        static bool StartVendorLogin(string home, int generation, double deadline,
            bool deviceCode, out string userCode)
        {
            userCode = "";
            CodexSource.RefreshAuthSession(home);
            string exe = CodexSource.ResolveExe("codex");
            if (string.IsNullOrEmpty(exe)) return false;
            var lease = CodexSource.Pool.Checkout(home, exe, deadline);
            if (lease == null) return false;
            using (lease)
            {
                // Ownership taken inside this call, so a throw on ANY later line
                // (the browser opener included) cannot leave a registered
                // attempt behind with nobody left to end it.
                CodexManagedLogin.Attempt owned = null;
                try
                {
                    var entry = lease.Entry;
                    if (!entry.InitDone)
                    {
                        object init = entry.Link.Call("initialize", new Dictionary<string, object> {
                            { "clientInfo", new Dictionary<string, object> { { "name", "limisaw" }, { "version", "1.0.0" } } },
                            { "capabilities", null },
                        }, deadline);
                        if (init == null || J.Get(init, "error") != null)
                        { lease.Retire(); return false; }
                        entry.Link.Notify("initialized", null);
                        entry.InitDone = true;
                    }
                    string loginType = deviceCode ? "chatgptDeviceCode" : "chatgpt";
                    object reply = entry.Link.Call("account/login/start",
                        new Dictionary<string, object> { { "type", loginType } }, deadline);
                    if (reply == null || J.Get(reply, "error") != null) return false;
                    object result = J.Get(reply, "result");
                    // Validate the response BEFORE anything is owned or opened.
                    if (J.Str(J.Get(result, "type")) != loginType) return false;
                    string loginId = J.Str(J.Get(result, "loginId"));
                    string url = J.Str(J.Get(result, deviceCode ? "verificationUrl" : "authUrl"));
                    string code = deviceCode ? J.Str(J.Get(result, "userCode")) : "";
                    // A response with no loginId gives LIMISAW nothing it could
                    // ever cancel; it is not a login this process may own.
                    if (string.IsNullOrEmpty(loginId)) return false;
                    // A device-code result without the code is unusable: the
                    // user would be sent to a page with nothing to type. Refuse
                    // it before anything is owned, so nothing needs undoing.
                    if (deviceCode && string.IsNullOrEmpty(code)) return false;
                    // Ownership is registered BEFORE the browser opens — and
                    // before the URL is judged — so from this instant every exit
                    // path has an exact loginId and an exact session to cancel
                    // on, including the paths that never return through here.
                    CodexManagedLogin.Attempt superseded;
                    var attempt = CodexManagedLogin.Register(CodexHomeDiscovery.HomeId(home), home,
                        loginId, generation, entry, out superseded);
                    owned = attempt;
                    RetireSuperseded(superseded, attempt, entry, deadline);
                    // Screen-only, and bound to the same home the attempt is:
                    // every terminal transition below clears it again.
                    if (deviceCode) CodexDeviceCodePrompt.Set(attempt.HomeId, home, code, url);
                    // An untrusted authUrl is never opened; the vendor login it
                    // belongs to is released rather than left pending.
                    if (!TrustedAuthUrl(url))
                    { CancelHeldAttempt(attempt, entry, deadline); return false; }
                    if (OpenAuthUrl(url)) { userCode = code; return true; }
                    // No browser was opened. Cancel EXACTLY this login attempt
                    // on the session that minted it — we still hold its lease —
                    // and clear the ownership. The vendor keeps the
                    // pre-existing local auth intact.
                    CancelHeldAttempt(attempt, entry, deadline);
                    return false;
                }
                catch
                {
                    lease.Retire();
                    // The session is being destroyed, so this loginId can never
                    // be cancelled on the process that minted it: drop the
                    // ownership and fence the generation instead of pretending.
                    if (owned != null && CodexManagedLogin.TakeByHome(owned.HomeId) != null)
                        CodexManagedLogin.Fence(owned.Generation);
                    return false;
                }
            }
        }

        // Cancel an attempt on the lease the caller is ALREADY standing on.
        // Re-leasing the same entry from inside its own lease would deadlock,
        // so the inline paths use this instead of CodexManagedLogin.Cancel.
        static void CancelHeldAttempt(CodexManagedLogin.Attempt attempt,
            CodexSource.SessionPool.Entry entry, double deadline)
        {
            if (attempt == null) return;
            if (CodexManagedLogin.TakeByHome(attempt.HomeId) == null) return;
            if (Stamp.Now >= deadline) { CodexManagedLogin.Fence(attempt.Generation); return; }
            try
            {
                object reply = entry.Link.Call("account/login/cancel",
                    new Dictionary<string, object> { { "loginId", attempt.LoginId } }, deadline);
                if (reply == null) { CodexManagedLogin.Fence(attempt.Generation); return; }
                CodexManagedLogin.NoteCancelled(attempt.LoginId);
            }
            catch { CodexManagedLogin.Fence(attempt.Generation); }
        }

        // SUPERSESSION inside one home: an older attempt never survives the
        // newer one that displaced it. It is cancelled when it was minted by
        // the very session we hold, and fenced for diagnostics. The coordinator
        // generation check is what rejects generation-N publication.
        static void RetireSuperseded(CodexManagedLogin.Attempt superseded,
            CodexManagedLogin.Attempt current, CodexSource.SessionPool.Entry entry, double deadline)
        {
            if (superseded == null || ReferenceEquals(superseded, current)) return;
            superseded.Terminal = true;
            if (ReferenceEquals(superseded.Session, entry)
                && !string.IsNullOrEmpty(superseded.LoginId) && Stamp.Now < deadline)
            {
                try
                {
                    entry.Link.Call("account/login/cancel",
                        new Dictionary<string, object> { { "loginId", superseded.LoginId } }, deadline);
                    CodexManagedLogin.NoteCancelled(superseded.LoginId);
                }
                catch { }
            }
            CodexManagedLogin.Fence(superseded.Generation);
        }

        // The target home for THIS connection: exact canonical identity, the
        // explicitly set CODEX_HOME winning over the default, and no label
        // routing anywhere.
        public static CodexTargetHome TargetHome()
        {
            var homes = CodexHomeDiscovery.Homes();
            string env = CodexHomeDiscovery.GetEnv("CODEX_HOME");
            if (!string.IsNullOrEmpty(env))
            {
                string full;
                try { full = Path.GetFullPath(env); } catch { full = env; }
                foreach (var h in homes)
                    if (string.Equals(h.Path, full, StringComparison.OrdinalIgnoreCase)) return h;
            }
            foreach (var h in homes) if (h.IsDefaultHome) return h;
            return homes.Count > 0 ? homes[0] : null;
        }

        // Level 0 + auth-structure presence: never a credential value.
        public static VendorConnection Discover()
        {
            return DiscoverFrom(ExecutableDiscovery.BuildConnection("codex"));
        }

        internal static VendorConnection DiscoverFrom(VendorConnection vc)
        {
            if (!vc.Installed) return vc;
            var home = TargetHome();
            if (home != null)
            {
                vc.SelectedHomeId = home.Id;
                vc.SelectedHomePath = home.Path;
                if (!home.HasAuthStructure)
                {
                    vc.State = ConnectionState.SignInRequired;
                    vc.ErrorCode = ConnectionErrorCode.AuthMissing;
                    vc.RecommendedAction = ConnectionAction.Connect;
                    vc.Reason = "Codex is installed but no authentication was found for " + home.Label;
                    vc.AuthKnown = true; vc.Authenticated = false;
                    vc.UserActionRequired = true;
                }
                else
                {
                    // Presence is not final auth truth: still needs Level 1.
                    vc.Reason = "Authentication present — verifying";
                    vc.RecommendedAction = ConnectionAction.Verify;
                }
            }
            return vc;
        }

        // The vendor-owned visible login. A second click cannot spawn a second
        // login because the caller begins a coordinator operation first and
        // Begin refuses while one is active.
        public static bool LaunchLogin(CodexTargetHome home, out string error)
        {
            error = null;
            string exe = CodexSource.ResolveExe("codex");
            if (string.IsNullOrEmpty(exe)) { error = "cli_missing"; return false; }
            var l = new LoginLaunch { Program = exe, Arguments = "login" };
            if (home != null && !home.IsDefaultHome)
            {
                // Non-default exact CODEX_HOME: the child carries its own
                // environment; LIMISAW's process environment is never touched.
                l.EnvName = "CODEX_HOME";
                l.EnvValue = home.Path;
            }
            LaunchCount++;
            lock (Launched) Launched.Add(l);
            return Launch(l); // fire-and-forget by contract; a false return reports through error
        }

        public static string NextAccountHome()
        {
            string profile = CodexHomeDiscovery.UserProfile();
            if (string.IsNullOrEmpty(profile)) return "";
            // Retry an existing duplicate in place. Its credentials and local
            // files remain owned by the user; reauthentication can promote it.
            for (int n = 2; n <= 99; n++)
            {
                string dir = Path.Combine(profile, ".codex-account" + n);
                if (Directory.Exists(dir) && CodexSource.IsKnownDuplicateHome(CodexHomeDiscovery.HomeId(dir)))
                    return dir;
            }
            for (int n = 2; n <= 99; n++)
            {
                string dir = Path.Combine(profile, ".codex-account" + n);
                try
                {
                    if (File.Exists(dir)) continue;
                    if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, "auth.json"))) return dir;
                }
                catch { return ""; }
            }
            return "";
        }

        // ONLY an already-created duplicate home. This deliberately does not
        // share NextAccountHome's free-slot fallback: a retry that found no
        // duplicate must fail, never quietly allocate `.codex-account3`.
        public static string DuplicateAccountHome()
        {
            string profile = CodexHomeDiscovery.UserProfile();
            if (string.IsNullOrEmpty(profile)) return "";
            for (int n = 2; n <= 99; n++)
            {
                string dir = Path.Combine(profile, ".codex-account" + n);
                if (Directory.Exists(dir) && CodexSource.IsKnownDuplicateHome(CodexHomeDiscovery.HomeId(dir)))
                    return dir;
            }
            return "";
        }

        // "Retry different account": the SAME secondary home, authorized by
        // device code so the user picks the ChatGPT account instead of the
        // browser picking it for them. Nothing is created, nothing is deleted,
        // and the existing auth stays until a different account replaces it.
        public static bool LaunchRetryDifferentAccount(int generation, out string home,
            out string userCode, out string error)
        {
            home = ""; userCode = ""; error = null;
            string exe = CodexSource.ResolveExe("codex");
            if (string.IsNullOrEmpty(exe)) { error = "cli_missing"; return false; }
            string dir = DuplicateAccountHome();
            if (dir.Length == 0) { error = "no_duplicate_home"; return false; }
            home = dir;
            string id = CodexHomeDiscovery.HomeId(dir);
            CodexSource.AwaitDistinctAccount(id, dir);
            if (StartDeviceCodeLogin(dir, generation, Stamp.Now + 30, out userCode)) return true;
            CodexSource.CancelDistinctAccount(id);
            userCode = "";
            error = "device_login_failed";
            return false;
        }

        // The onboarding generation is part of the operation's identity: the
        // managed login it starts is owned by THAT generation and by no other.
        public static bool LaunchAddAccount(int generation, out string home, out string error)
        {
            home = ""; error = null;
            string exe = CodexSource.ResolveExe("codex");
            if (string.IsNullOrEmpty(exe)) { error = "cli_missing"; return false; }
            string dir = NextAccountHome();
            if (dir.Length == 0) { error = "no_home_available"; return false; }
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { error = ex.GetType().Name; return false; }
            home = dir;
            string id = CodexHomeDiscovery.HomeId(dir);
            CodexSource.AwaitDistinctAccount(id, dir);
            if (StartManagedLogin(dir, generation, Stamp.Now + 30)) return true;
            CodexSource.CancelDistinctAccount(id);
            error = "managed_login_failed";
            return false;
        }

        // Read-only Level 1: one bounded app-server read through the SAME
        // SessionPool the sweep uses. A dead session for this home is dropped,
        // recreated and retried exactly ONCE inside this operation.
        public static ConnectionVerifyResult VerifyHome(string homePath, double deadline)
        {
            CodexSource.RefreshAuthSession(homePath);
            VerifyOutcome outc;
            string exe = CodexSource.ResolveExe("codex");
            if (string.IsNullOrEmpty(exe))
            {
                var missing = new ConnectionVerifyResult
                { Error = ConnectionErrorCode.CliMissing, Reason = "Codex CLI not found" };
                return missing;
            }
            outc = VerifyOnce(homePath, exe, deadline);
            if (outc.DeadSession)
            {
                // Drop only THIS home (VerifyOnce already dropped it), recreate
                // on the next checkout, retry once.
                outc = VerifyOnce(homePath, exe, deadline);
            }
            return outc.Result;
        }

        class VerifyOutcome
        {
            public ConnectionVerifyResult Result = new ConnectionVerifyResult();
            public bool DeadSession;
        }

        static VerifyOutcome VerifyOnce(string homePath, string exe, double deadline)
        {
            var outc = new VerifyOutcome();
            var r = outc.Result;
            CodexSource.SessionPool.SessionLease lease = CodexSource.Pool.Checkout(homePath, exe, deadline);
            if (lease == null) { r.Error = ConnectionErrorCode.CliNotExecutable; r.Reason = "codex app-server did not start"; return outc; }
            using (lease)
            {
                CodexSource.SessionPool.Entry entry = lease.Entry;
                try
                {
                    if (!entry.InitDone)
                    {
                        object init = entry.Link.Call("initialize", new Dictionary<string, object> {
                            { "clientInfo", new Dictionary<string, object> { { "name", "limisaw" }, { "version", "1.0.0" } } },
                            { "capabilities", null },
                        }, deadline);
                        if (init == null) { lease.Retire(); outc.DeadSession = true; r.Error = ConnectionErrorCode.DeadlineExceeded; r.Reason = "codex app-server did not answer initialize"; return outc; }
                        if (J.Get(init, "error") != null) { lease.Retire(); r.State = ConnectionState.SignInRequired; r.Error = ConnectionErrorCode.AuthRejected; r.Reason = "initialize error"; return outc; }
                        entry.Link.Notify("initialized", null);
                        entry.InitDone = true;
                    }
                    object rl = entry.Link.Call("account/rateLimits/read", null, deadline);
                    if (rl == null) { lease.Retire(); outc.DeadSession = true; r.Error = ConnectionErrorCode.DeadlineExceeded; r.Reason = "codex app-server did not answer rateLimits"; return outc; }
                    if (J.Get(rl, "error") != null)
                    {
                        // A structured error is a HEALTHY session answering for an
                        // account whose quota is not exposed: auth is fine.
                        r.State = ConnectionState.ConnectedQuotaUnavailable;
                        r.Error = ConnectionErrorCode.QuotaNotAvailable;
                        r.Reason = "authenticated, subscription quota not exposed";
                        r.Authenticated = true;
                        return outc;
                    }
                    object result = J.Get(rl, "result") ?? new Dictionary<string, object>();
                    object accountRead = null;
                    if (string.IsNullOrWhiteSpace(J.Str(J.Get(result, "accountId"))) && Stamp.Now < deadline)
                    {
                        object reply = entry.Link.Call("account/read", new Dictionary<string, object>(), deadline);
                        if (reply != null && J.Get(reply, "error") == null)
                            accountRead = J.Get(reply, "result");
                    }
                    r.RemoteAccountIdentity = CodexSource.RemoteIdentity(result, accountRead);
                    CodexSource.RememberRemoteIdentity(CodexHomeDiscovery.HomeId(homePath), r.RemoteAccountIdentity);
                    string plan;
                    List<ProbeWindow> windows = CodexSource.ParseWindows(result, out plan);
                    r.Authenticated = true;
                    // `plan` is the vendor's planType (e.g. "plus"), NOT a CLI
                    // version. It rides the report as its own labelled field; the
                    // version slot stays empty because the app-server path never
                    // runs `codex --version` (see BuildReport's guarded emit).
                    r.Plan = plan;
                    bool readable = false;
                    foreach (var w in windows) if (w.Available && w.Remaining.HasValue) readable = true;
                    if (readable)
                    {
                        r.State = ConnectionState.Connected;
                        r.Error = ConnectionErrorCode.None;
                        r.Reason = "Connected" + (string.IsNullOrEmpty(plan) ? "" : " · " + plan);
                        r.Monitorable = true;
                    }
                    else
                    {
                        r.State = ConnectionState.ConnectedQuotaUnavailable;
                        r.Error = ConnectionErrorCode.QuotaNotAvailable;
                        r.Reason = "authenticated, subscription quota not exposed";
                    }
                    return outc;
                }
                catch (Exception ex)
                {
                    lease.Retire();
                    outc.DeadSession = true;
                    r.Error = ConnectionErrorCode.UnknownFailure;
                    r.Reason = ex.GetType().Name;
                    return outc;
                }
            }
        }

        public static ConnectionVerifyResult VerifyAddedHome(string homePath, double deadline)
        {
            string homeId = CodexHomeDiscovery.HomeId(homePath);
            if (!CodexSource.LoginChangedAuth(homeId, homePath))
                return new ConnectionVerifyResult
                {
                    State = ConnectionState.WaitingForUser,
                    Reason = "WAITING_FOR_DISTINCT_ACCOUNT — complete Codex login in the browser",
                };
            var r = VerifyHome(homePath, deadline);
            if (r.State != ConnectionState.Connected && r.State != ConnectionState.ConnectedQuotaUnavailable)
                return r;
            bool duplicate;
            bool distinct = CodexSource.IsDistinctFromKnownHomes(
                homeId, r.RemoteAccountIdentity, out duplicate);
            if (duplicate)
            {
                r.State = ConnectionState.DuplicateRemoteAccount;
                r.Reason = "Signed in, but as an account already listed — no extra Codex card was added. Retry different account reuses this same home and lets you pick a different ChatGPT account.";
                r.Monitorable = false;
                // Known duplicate from this instant, so the retry has a home to
                // reuse without waiting for the next sweep to recompute the set.
                CodexSource.MarkDuplicateHome(homeId);
                // The vendor login FINISHED — to the wrong account, but it
                // finished. Ownership is dropped without a cancel: the home and
                // its credentials stay exactly as the user left them, ready for
                // a retry that signs in somewhere else.
                CodexManagedLogin.Complete(homeId);
            }
            else if (!distinct)
            {
                r.State = ConnectionState.WaitingForUser;
                r.Reason = "Waiting for distinct Codex account identity — switch to the intended ChatGPT account in the browser";
                r.Monitorable = false;
            }
            else
            {
                // A verified distinct remote account: the operation is terminal
                // and a completed vendor login is never cancelled. The home keeps
                // its local identity and stops being a duplicate.
                CodexSource.ClearDuplicateHome(homeId);
                CodexSource.CancelDistinctAccount(homeId);
                CodexManagedLogin.Complete(homeId);
            }
            return r;
        }

        // Deeper troubleshooting only: a bounded `codex doctor` PROBE child,
        // run when discovery/verify could not classify the problem. Never on
        // refresh, never per verification, never at startup.
        public static VendorConnection Doctor(VendorConnection baseConn)
        {
            string exe = CodexSource.ResolveExe("codex");
            baseConn.State = ConnectionState.Degraded;
            if (string.IsNullOrEmpty(exe))
            {
                baseConn.ErrorCode = ConnectionErrorCode.CliMissing;
                baseConn.Reason = "codex doctor: CLI not found";
                baseConn.RecommendedAction = ConnectionAction.Install;
                return baseConn;
            }
            baseConn.Stage = ConnectionStage.Authentication;
            var res = Cli.Run(exe, new[] { "doctor" }, Stamp.Now + 15, null);
            baseConn.ErrorCode = ClassifyDoctor(res, baseConn);
            baseConn.Reason = "codex doctor: " + baseConn.ErrorCode;
            baseConn.RecommendedAction = ConnectionErrorPriority.RecommendedAction(baseConn.ErrorCode, baseConn.State);
            return baseConn;
        }

        // Parser seam over the doctor output: installation/path, auth,
        // network, protocol or unknown — sanitized, never a raw dump.
        internal static ConnectionErrorCode ClassifyDoctor(Cli.Result res, VendorConnection conn)
        {
            if (res == null) return ConnectionErrorCode.UnknownFailure;
            if (!res.Ok)
            {
                string err = (res.Error ?? "").ToLowerInvariant();
                if (err.Contains("timeout")) return ConnectionErrorCode.DeadlineExceeded;
                if (err.Contains("not found") || err.Contains("could not start")) return ConnectionErrorCode.CliNotExecutable;
                if (err.Contains("tls") || err.Contains("ssl")) return ConnectionErrorCode.TlsFailed;
                if (err.Contains("network") || err.Contains("dns") || err.Contains("proxy")) return ConnectionErrorCode.NetworkTimeout;
                return ConnectionErrorCode.UnknownFailure;
            }
            string text = (res.Stdout ?? "").ToLowerInvariant();
            if (text.Contains("not found") || text.Contains("no installation") || text.Contains("path")) return ConnectionErrorCode.PathMissing;
            if (text.Contains("not logged in") || text.Contains("auth") || text.Contains("login") || text.Contains("unauthorized")) return ConnectionErrorCode.AuthMissing;
            if (text.Contains("tls") || text.Contains("ssl") || text.Contains("certificate")) return ConnectionErrorCode.TlsFailed;
            if (text.Contains("network") || text.Contains("dns") || text.Contains("proxy") || text.Contains("timeout")) return ConnectionErrorCode.NetworkTimeout;
            if (text.Contains("protocol") || text.Contains("version mismatch")) return ConnectionErrorCode.ProtocolChanged;
            return ConnectionErrorCode.UnknownFailure;
        }
    }

    // ── Claude connection adapter ────────────────────────────────────────────
    internal static class ClaudeConnectionAdapter
    {
        internal const int VerifyDeadlineS = 25;

        internal class LoginLaunch
        {
            public string Program;
            public string Arguments;
            // Set only for an ADDITIONAL account: the child carries its own
            // CLAUDE_CONFIG_DIR, so the sign-in lands in the new directory
            // instead of replacing the account already in ~/.claude, and
            // LIMISAW's own environment is never touched.
            public string EnvName;
            public string EnvValue;
        }
        internal static Func<LoginLaunch, bool> Launch = DefaultLaunch;
        internal static int LaunchCount;
        internal static readonly List<LoginLaunch> Launched = new List<LoginLaunch>();

        static bool DefaultLaunch(LoginLaunch l)
        {
            string error;
            if (l.EnvName == null)
            {
                var launch = ConnectionProcessLauncher.StartInteractive(l.Program, l.Arguments, null, out error);
                return launch != null;
            }
            return ConnectionProcessLauncher.StartInteractiveWithEnv(l.Program, l.Arguments, l.EnvName, l.EnvValue, out error);
        }

        // Parser seam: the machine-readable answer when the CLI offers one,
        // normalized categories otherwise. Independent of process execution —
        // the harness feeds text directly.
        internal static CliAuthResult ParseAuthStatus(string stdout, string stderr)
        {
            var r = new CliAuthResult();
            string combined = (stdout ?? "") + "\n" + (stderr ?? "");
            object doc = J.Parse(stdout ?? "");
            if (doc != null)
            {
                r.MachineReadable = true;
                // Field shapes the CLI has shipped; unknown shapes fall through
                // to text markers rather than guessing.
                foreach (string key in new[] { "loggedIn", "authenticated", "logged_in", "hasCompletedOnboarding" })
                {
                    object v = J.Get(doc, key);
                    if (v is bool)
                    {
                        r.Category = (bool)v ? CliAuthCategory.LoggedIn : CliAuthCategory.LoggedOut;
                        return r;
                    }
                }
                string status = (J.Str(J.Get(doc, "status")) ?? "").ToLowerInvariant();
                if (status.Contains("expire") || status.Contains("reject")) { r.Category = CliAuthCategory.Expired; return r; }
                if (status.Contains("out")) { r.Category = CliAuthCategory.LoggedOut; return r; }
                if (status.Contains("in")) { r.Category = CliAuthCategory.LoggedIn; return r; }
            }
            string text = combined.ToLowerInvariant();
            if (text.Contains("expired") || text.Contains("token has expired") || text.Contains("unauthorized")
                || text.Contains("invalid api key") || text.Contains("authentication error"))
            { r.Category = CliAuthCategory.Expired; return r; }
            if (text.Contains("not logged in") || text.Contains("logged out") || text.Contains("please log in")
                || text.Contains("run /login") || text.Contains("no account"))
            { r.Category = CliAuthCategory.LoggedOut; return r; }
            if (text.Contains("logged in") || text.Contains("authenticated as") || text.Contains("signed in"))
            { r.Category = CliAuthCategory.LoggedIn; return r; }
            r.Category = CliAuthCategory.Unknown;
            return r;
        }

        // Read-only Level 1: the binary actually starts and a harmless version
        // comes back under a bounded deadline.
        internal static CliVersionResult Version(double deadline)
        {
            var r = new CliVersionResult();
            string exe = Cli.Resolve("claude");
            if (exe.Length == 0) { r.Error = ConnectionErrorCode.CliMissing; r.Detail = "claude CLI not found"; return r; }
            var res = Cli.Run(exe, new[] { "--version" }, deadline, null);
            if (!res.Ok)
            {
                string err = (res.Error ?? "").ToLowerInvariant();
                if (err.Contains("timeout") || err.Contains("deadline")) r.Error = ConnectionErrorCode.DeadlineExceeded;
                else if (err.Contains("response_too_large")) r.Error = ConnectionErrorCode.ResponseTooLarge;
                else r.Error = ConnectionErrorCode.CliNotExecutable;
                r.Detail = res.Error;
                return r;
            }
            r.Ok = true;
            r.Version = FirstLine(res.Stdout);
            return r;
        }

        static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string line = text.Split('\n')[0].Trim();
            return line.Length > 80 ? line.Substring(0, 80) : line;
        }

        // Level 0 + auth via the vendor command; ~/.claude existing is never
        // authentication evidence.
        public static VendorConnection Discover()
        {
            return DiscoverFrom(ExecutableDiscovery.BuildConnection("claude"));
        }

        internal static VendorConnection DiscoverFrom(VendorConnection vc)
        {
            if (!vc.Installed) return vc;
            vc.RecommendedAction = ConnectionAction.Verify;
            return vc;
        }


        // The next free account home: ~/.claude-account2, -account3, and so on.
        // The DEFAULT ~/.claude is never a candidate, and that is the whole
        // point: signing in there does not add an account, it REPLACES the one
        // already living in it — the trap that makes the first account
        // disappear from the list.
        //
        // A directory that exists but holds no account yet (the user opened
        // this flow and closed the login window) is REUSED rather than skipped.
        // Skipping it would leave a trail of empty ~/.claude-accountN folders
        // and walk the number up on every abandoned attempt. A home that does
        // hold an account is never a candidate — same marker rule the sweep
        // discovers by, asked through the sweep's own code.
        public static string NextAccountHome()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(profile)) return "";
            for (int n = 2; n <= 99; n++)
            {
                string dir = Path.Combine(profile, ".claude-account" + n);
                try
                {
                    if (File.Exists(dir)) continue;          // a FILE by that name
                    if (!Directory.Exists(dir)) return dir;  // free
                    if (!ClaudeSource.LooksLikeHome(dir)) return dir;   // made, never used
                }
                catch { return ""; }
            }
            return "";
        }

        // One click: make the directory, then start the vendor's own visible
        // sign-in inside it. LIMISAW never sees the credential — it only
        // decides WHERE the CLI writes one. An abandoned login leaves an empty
        // directory and NOT a card: a home with no account in it is not an
        // account, and the next click reuses that same directory.
        public static bool LaunchAddAccount(out string home, out string error)
        {
            home = ""; error = null;
            string exe = Cli.Resolve("claude");
            if (string.IsNullOrEmpty(exe)) { error = "cli_missing"; return false; }
            string dir = NextAccountHome();
            if (dir.Length == 0) { error = "no_home_available"; return false; }
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { error = ex.GetType().Name; return false; }
            home = dir;
            var l = new LoginLaunch
            {
                Program = exe, Arguments = "auth login",
                EnvName = "CLAUDE_CONFIG_DIR", EnvValue = dir,
            };
            LaunchCount++;
            lock (Launched) Launched.Add(l);
            return Launch(l);
        }

        public static bool LaunchLogin(out string error)
        {
            error = null;
            string exe = Cli.Resolve("claude");
            if (string.IsNullOrEmpty(exe)) { error = "cli_missing"; return false; }
            var l = new LoginLaunch { Program = exe, Arguments = "auth login" };
            LaunchCount++;
            lock (Launched) Launched.Add(l);
            // CORE-003 (audit/6): the REAL launch result is the contract —
            // Codex already returned it. A false here means Process.Start
            // failed; the caller cancels the operation and reports the
            // failure instead of starting a 90-second watcher for a login
            // that never launched.
            return Launch(l);
        }

        // Auth status through the bounded probe child, parsed through the seam.
        internal static CliAuthResult AuthStatus(double deadline)
        {
            string exe = Cli.Resolve("claude");
            if (exe.Length == 0) return new CliAuthResult { Category = CliAuthCategory.Unknown, Detail = "cli_missing" };
            var res = Cli.Run(exe, new[] { "auth", "status" }, deadline, null);
            return ParseAuthStatus(res.Stdout, res.Error);
        }

        // QUOTA classification reuses the existing safe read path — the
        // adapter classifies, it does not fork the parser.
        public static ConnectionVerifyResult Verify(double deadline)
        {
            var r = new ConnectionVerifyResult();
            var ver = Version(deadline);
            if (!ver.Ok)
            {
                r.State = ver.Error == ConnectionErrorCode.CliMissing ? ConnectionState.NotInstalled : ConnectionState.Failed;
                r.Error = ver.Error;
                r.Reason = ver.Detail ?? "claude version check failed";
                return r;
            }
            r.CliVersion = ver.Version;
            r.State = ConnectionState.Verifying;
            double now = Stamp.Now;
            string quotaError;
            var reading = ClaudeSource.CliUsage(deadline, now, out quotaError);
            if (reading != null && reading.Windows.Count > 0)
            {
                r.State = ConnectionState.Connected;
                r.Error = ConnectionErrorCode.None;
                r.Reason = "Connected · " + ver.Version;
                r.Authenticated = true;
                r.Monitorable = true;
                return r;
            }
            string err = (quotaError ?? "").ToLowerInvariant();
            if (err.Contains("not logged in") || err.Contains("log in") || err.Contains("login"))
            {
                r.State = ConnectionState.SignInRequired;
                r.Error = ConnectionErrorCode.AuthMissing;
                r.Reason = quotaError;
                return r;
            }
            if (err.Contains("no subscription limits") || err.Contains("api key") || err.Contains("console"))
            {
                // Authenticated account whose mode exposes no subscription
                // quota: NEVER a red connection failure and never a reinstall.
                r.State = ConnectionState.ConnectedQuotaUnavailable;
                r.Error = ConnectionErrorCode.QuotaNotAvailable;
                r.Reason = "authenticated, subscription quota not exposed";
                r.Authenticated = true;
                return r;
            }
            if (err.Contains("timeout") || err.Contains("deadline"))
            { r.State = ConnectionState.Failed; r.Error = ConnectionErrorCode.NetworkTimeout; r.Reason = quotaError; return r; }
            if (err.Contains("tls") || err.Contains("ssl") || err.Contains("securechannel"))
            { r.State = ConnectionState.Failed; r.Error = ConnectionErrorCode.TlsFailed; r.Reason = quotaError; return r; }
            if (err.Contains("proxy"))
            { r.State = ConnectionState.Failed; r.Error = ConnectionErrorCode.ProxyFailed; r.Reason = quotaError; return r; }
            r.State = ConnectionState.Failed;
            r.Error = ConnectionErrorCode.UnknownFailure;
            r.Reason = quotaError ?? "claude usage could not be read";
            return r;
        }

        // Deeper troubleshooting only: a bounded `claude doctor` probe child
        // with sanitized classification.
        public static VendorConnection Doctor(VendorConnection baseConn)
        {
            string exe = Cli.Resolve("claude");
            baseConn.State = ConnectionState.Degraded;
            baseConn.Stage = ConnectionStage.Authentication;
            if (string.IsNullOrEmpty(exe))
            {
                baseConn.ErrorCode = ConnectionErrorCode.CliMissing;
                baseConn.Reason = "claude doctor: CLI not found";
                baseConn.RecommendedAction = ConnectionAction.Install;
                return baseConn;
            }
            var res = Cli.Run(exe, new[] { "doctor" }, Stamp.Now + 15, null);
            baseConn.ErrorCode = ClassifyDoctor(res);
            baseConn.Reason = "claude doctor: " + baseConn.ErrorCode;
            baseConn.RecommendedAction = ConnectionErrorPriority.RecommendedAction(baseConn.ErrorCode, baseConn.State);
            return baseConn;
        }

        internal static ConnectionErrorCode ClassifyDoctor(Cli.Result res)
        {
            if (res == null) return ConnectionErrorCode.UnknownFailure;
            if (!res.Ok)
            {
                string err = (res.Error ?? "").ToLowerInvariant();
                if (err.Contains("timeout")) return ConnectionErrorCode.DeadlineExceeded;
                if (err.Contains("not found") || err.Contains("could not start")) return ConnectionErrorCode.CliNotExecutable;
                return ConnectionErrorCode.UnknownFailure;
            }
            string text = (res.Stdout ?? "").ToLowerInvariant();
            if (text.Contains("not found") || text.Contains("stale") || text.Contains("path")) return ConnectionErrorCode.PathMissing;
            if (text.Contains("shell") || text.Contains("powershell") || text.Contains("wsl")) return ConnectionErrorCode.CliNotExecutable;
            if (text.Contains("oauth") || text.Contains("login") || text.Contains("not logged in")) return ConnectionErrorCode.AuthMissing;
            if (text.Contains("expired") || text.Contains("token")) return ConnectionErrorCode.AuthExpired;
            if (text.Contains("tls") || text.Contains("ssl") || text.Contains("certificate")) return ConnectionErrorCode.TlsFailed;
            if (text.Contains("proxy")) return ConnectionErrorCode.ProxyFailed;
            if (text.Contains("network") || text.Contains("dns") || text.Contains("timeout")) return ConnectionErrorCode.NetworkTimeout;
            return ConnectionErrorCode.UnknownFailure;
        }
    }

    // ── Antigravity connection adapter ─────────────────────────────────────
    // Consistent with Codex/Claude/Zcode adapters: one Discover, one vendor-
    // owned visible login launch, one Verify that REUSES the existing quota
    // read path (AntigravitySource.CliWindows — no forked parser), and an
    // explicit-only Doctor composed of harmless vendor steps (agy has NO
    // doctor subcommand; none is invented). The journal fallback that keeps
    // the Accounts card useful is quota evidence only: it can NEVER prove a
    // connection (R074/R075).
    internal static class AntigravityConnectionAdapter
    {
        internal const int VerifyDeadlineS = 20;

        internal class LoginLaunch
        {
            public string Program;
            public string Arguments;
        }
        internal static Func<LoginLaunch, bool> Launch = DefaultLaunch;
        internal static int LaunchCount;
        internal static readonly List<LoginLaunch> Launched = new List<LoginLaunch>();

        static bool DefaultLaunch(LoginLaunch l)
        {
            string error;
            // R081: the interactive login is observed (exit pokes the watcher)
            // but never owned — the child survives LIMISAW by design.
            var launch = ConnectionProcessLauncher.StartInteractive(l.Program, l.Arguments,
                () => ConnectionWatcher.Poke("antigravity"), out error);
            return launch != null;
        }

        // R072 Level 0: executable discovery through the shared ExecutableDiscovery
        // (no duplicated PATH logic), local-data presence through the existing
        // AntigravitySource.DataDir() authority, and the auth MODE from the
        // vendor's own settings — presence and category only. Local data means
        // "Antigravity has been present before", never "authenticated".
        public static VendorConnection Discover()
        {
            return DiscoverFrom(ExecutableDiscovery.BuildConnection("antigravity"));
        }

        internal static VendorConnection DiscoverFrom(VendorConnection vc)
        {
            vc.LocalDataPresent = AntigravityAuthSettings.LocalDataPresent();
            vc.ApiKeyPresent = AntigravityAuthSettings.GeminiApiKeyPresent();
            string declared;
            var mode = AntigravityAuthSettings.ReadMode(out declared);
            vc.AuthMode = mode == AntigravityAuthMode.GeminiApiKey ? "gemini-api-key"
                : mode == AntigravityAuthMode.VendorSession ? "vendorsession" : "unknown";

            // CLI absent + historical local data: Partial (journal fallback
            // only) — the vendor was here, the verifier is not. The Install
            // action stands; the journal must never read as Connected.
            if (!vc.Installed && vc.LocalDataPresent && vc.State == ConnectionState.NotInstalled)
            {
                vc.State = ConnectionState.Partial;
                vc.ErrorCode = ConnectionErrorCode.CliMissing;
                vc.RecommendedAction = ConnectionAction.Install;
                vc.Reason = "Journal fallback only — install the Antigravity CLI to verify";
            }
            // CLI present: level 0 never claims auth; Level 1 verifies.
            if (vc.Installed && vc.State == ConnectionState.Installed)
            {
                vc.RecommendedAction = ConnectionAction.Verify;
                if (mode == AntigravityAuthMode.GeminiApiKey && !vc.ApiKeyPresent)
                {
                    // Deterministic causal evidence already at Level 0: the
                    // vendor's own settings demand an API key and none is set.
                    vc.State = ConnectionState.UnsupportedConfiguration;
                    vc.ErrorCode = ConnectionErrorCode.CredentialMissing;
                    vc.RecommendedAction = ConnectionAction.OpenVendor;
                    vc.Reason = "Auth mode is gemini-api-key but GEMINI_API_KEY is not set";
                    vc.AuthKnown = true; vc.Authenticated = false;
                    vc.UserActionRequired = true;
                }
            }
            return vc;
        }

        // R073: the vendor's own visible authentication entry. agy's documented
        // surface (1.1.27 --help) has no login/auth subcommand: its normal
        // interactive startup IS the auth entry, reusing the OS keyring and
        // opening a browser when sign-in is required. LIMISAW launches exactly
        // that, visibly, and implements no OAuth of its own. The process is
        // USER-OWNED: outside ChildSweeper, never killed at shutdown, and its
        // output is never captured.
        public static bool LaunchLogin(out string error)
        {
            error = null;
            string exe = Cli.Resolve("antigravity");
            if (string.IsNullOrEmpty(exe)) { error = "cli_missing"; return false; }
            var l = new LoginLaunch { Program = exe, Arguments = "" };
            LaunchCount++;
            lock (Launched) Launched.Add(l);
            // CORE-003 (audit/6): the REAL launch result is the contract —
            // Codex already returned it. A false here means Process.Start
            // failed; the caller cancels the operation and reports the
            // failure instead of starting a 90-second watcher for a login
            // that never launched.
            return Launch(l);
        }

        // R074: classification over the EXISTING /usage read. AntigravitySource
        // keeps owning the exact command, the bounded Cli.Run, the payload
        // limits, the JSON parser and the pool-qualified windows; the adapter
        // consumes the structured result and never re-parses usage JSON.
        public static ConnectionVerifyResult Verify(double deadline)
        {
            var r = new ConnectionVerifyResult();
            string exe = Cli.Resolve("antigravity");
            if (exe.Length == 0)
            {
                // Journal data may still exist: Partial, never NotInstalled.
                bool data = AntigravityAuthSettings.LocalDataPresent();
                r.State = data ? ConnectionState.Partial : ConnectionState.NotInstalled;
                r.Error = ConnectionErrorCode.CliMissing;
                r.Reason = data ? "Journal fallback only — install the Antigravity CLI to verify" : "Antigravity CLI not found";
                return r;
            }
            r.State = ConnectionState.Verifying;
            List<ProbeWindow> windows;
            windows = ReadCliUsage(deadline);
            if (windows != null && windows.Count > 0)
            {
                r.State = ConnectionState.Connected;
                r.Error = ConnectionErrorCode.None;
                r.Reason = "Connected";
                r.Authenticated = true;
                r.Monitorable = true;
                return r;
            }
            return ClassifyUsageFailure(r, LastUsageError());
        }

        // The narrow structured seam over AntigravitySource's CLI usage path:
        // exact command, bounded run, parser and pool-qualified windows all
        // stay owned by AntigravitySource; the adapter only consumes.
        internal static Func<double, List<ProbeWindow>> ReadCliUsage = AntigravitySource.ReadCliUsage;
        internal static Func<string> LastUsageError = () => AntigravitySource.LastCliUsageError;

        // R075/R022 classification: structured evidence in causal order,
        // sanitized reason, never one giant UnknownFailure, and never Install
        // for an auth/credential failure on an installed CLI.
        internal static ConnectionVerifyResult ClassifyUsageFailure(ConnectionVerifyResult r, string quotaError)
        {
            string err = (quotaError ?? "").ToLowerInvariant();
            // Network evidence first: vendor text like "proxy authentication
            // required" mentions auth but is a PROXY failure, not a sign-in
            // problem — causal order matters.
            if (err.Contains("timeout") || err.Contains("deadline"))
            { r.State = ConnectionState.Failed; r.Error = ConnectionErrorCode.NetworkTimeout; r.Reason = ConnectionDiagnostics.Redact(quotaError, null); return r; }
            if (err.Contains("tls") || err.Contains("ssl") || err.Contains("securechannel") || err.Contains("certificate"))
            { r.State = ConnectionState.Failed; r.Error = ConnectionErrorCode.TlsFailed; r.Reason = ConnectionDiagnostics.Redact(quotaError, null); return r; }
            if (err.Contains("proxy"))
            { r.State = ConnectionState.Failed; r.Error = ConnectionErrorCode.ProxyFailed; r.Reason = ConnectionDiagnostics.Redact(quotaError, null); return r; }
            if (err.Contains("dns") || err.Contains("name resolution") || err.Contains("host") && err.Contains("not") && err.Contains("found"))
            { r.State = ConnectionState.Failed; r.Error = ConnectionErrorCode.DnsFailed; r.Reason = ConnectionDiagnostics.Redact(quotaError, null); return r; }
            // Vendor output that says authentication is absent/expired.
            if (err.Contains("not logged in") || err.Contains("log in") || err.Contains("login")
                || err.Contains("unauthorized") || err.Contains("authentication") || err.Contains("credentials")
                || err.Contains("auth"))
            {
                r.State = ConnectionState.SignInRequired;
                r.Error = ConnectionErrorCode.AuthMissing;
                r.Reason = ConnectionDiagnostics.Redact(quotaError ?? "Sign-in required", null);
                return r;
            }
            // Keyring/session-specific vendor failure.
            if (err.Contains("keyring") || err.Contains("keychain") || err.Contains("session") || err.Contains("token"))
            {
                r.State = ConnectionState.Failed;
                r.Error = ConnectionErrorCode.AuthRejected;
                r.Reason = ConnectionDiagnostics.Redact(quotaError ?? "Authentication rejected", null);
                return r;
            }
            // gemini/api-key mode whose required environment credential is absent.
            string declared;
            var mode = AntigravityAuthSettings.ReadMode(out declared);
            if (mode == AntigravityAuthMode.GeminiApiKey && !AntigravityAuthSettings.GeminiApiKeyPresent())
            {
                r.State = ConnectionState.UnsupportedConfiguration;
                r.Error = ConnectionErrorCode.CredentialMissing;
                r.Reason = "Auth mode is gemini-api-key but GEMINI_API_KEY is not set";
                return r;
            }
            // Authenticated API-key mode whose quota surface is not exposed.
            if (err.Contains("no readable quota") || err.Contains("subscription") || err.Contains("no readable window"))
            {
                r.State = ConnectionState.ConnectedQuotaUnavailable;
                r.Error = ConnectionErrorCode.QuotaNotAvailable;
                r.Reason = "authenticated, subscription quota not exposed";
                r.Authenticated = true;
                return r;
            }
            if (err.Contains("could not start") || err.Contains("not executable") || err.Contains("access denied"))
            { r.State = ConnectionState.Failed; r.Error = ConnectionErrorCode.CliNotExecutable; r.Reason = ConnectionDiagnostics.Redact(quotaError, null); return r; }
            // The CLI ran and answered non-JSON / unsupported shape.
            if (err.Contains("did not return json") || err.Contains("no readable quota window") || err.Contains("json"))
            {
                r.State = ConnectionState.Failed;
                r.Error = ConnectionErrorCode.ProtocolChanged;
                r.Reason = ConnectionDiagnostics.Redact(quotaError ?? "usage response not understood", null);
                return r;
            }
            r.State = ConnectionState.Failed;
            r.Error = ConnectionErrorCode.UnknownFailure;
            r.Reason = ConnectionDiagnostics.Redact(quotaError ?? "Antigravity usage could not be read", null);
            return r;
        }

        // Harmless CLI start check seam: a bounded `agy --version` run — the
        // safest "the binary works" probe (no auth, no network). Tests fake it
        // so Doctor is deterministic without a real child.
        internal static Func<string, Cli.Result> VersionProbe = exe => Cli.Run(exe, new[] { "--version" }, Stamp.Now + 15, null);

        // R075: explicit-only troubleshooting. agy ships no doctor subcommand
        // (verified against the installed 1.1.27 help) and none is invented:
        // Doctor is a structured diagnostic pass — Level 0 executable/config
        // state, a harmless version check, then one live /usage attempt with
        // the same sanitized classification as Verify.
        public static VendorConnection Doctor(VendorConnection baseConn)
        {
            string exe = Cli.Resolve("antigravity");
            baseConn.State = ConnectionState.Degraded;
            baseConn.Stage = ConnectionStage.Authentication;
            if (string.IsNullOrEmpty(exe))
            {
                baseConn.ErrorCode = ConnectionErrorCode.CliMissing;
                baseConn.Reason = "Antigravity CLI not found";
                baseConn.RecommendedAction = ConnectionAction.Install;
                return baseConn;
            }
            // Harmless CLI start check: a bounded version-style run. agy answers
            // `--version` without auth; it is the safest "the binary works" probe.
            var ver = VersionProbe(exe);
            if (!ver.Ok)
            {
                string verr = (ver.Error ?? "").ToLowerInvariant();
                if (verr.Contains("timeout") || verr.Contains("deadline"))
                    baseConn.ErrorCode = ConnectionErrorCode.DeadlineExceeded;
                else
                    baseConn.ErrorCode = ConnectionErrorCode.CliNotExecutable;
                baseConn.Reason = "Antigravity CLI did not start: " + baseConn.ErrorCode;
                baseConn.RecommendedAction = ConnectionErrorPriority.RecommendedAction(baseConn.ErrorCode, baseConn.State);
                return baseConn;
            }
            baseConn.CliVersion = FirstLine(ver.Stdout);
            // Live /usage attempt through the SAME structured path Verify uses.
            var result = new ConnectionVerifyResult();
            var windows = ReadCliUsage(Stamp.Now + VerifyDeadlineS);
            if (windows != null && windows.Count > 0)
            {
                baseConn.State = ConnectionState.Connected;
                baseConn.ErrorCode = ConnectionErrorCode.None;
                baseConn.Reason = "Connected";
                baseConn.Authenticated = true;
                baseConn.Monitorable = true;
                baseConn.VerificationOk = true;
                baseConn.LastVerifiedUtc = Stamp.Now;
                baseConn.RecommendedAction = ConnectionAction.None;
                baseConn.Stage = ConnectionStage.Quota;
                return baseConn;
            }
            var classified = ClassifyUsageFailure(result, LastUsageError());
            // Troubleshooting REPORTS the failure; a transient network failure
            // observed here is a Degraded diagnosis with an action, while a
            // causal auth/credential verdict keeps its own exact state.
            baseConn.State = classified.State == ConnectionState.SignInRequired
                || classified.State == ConnectionState.UnsupportedConfiguration
                || classified.State == ConnectionState.ConnectedQuotaUnavailable
                ? classified.State
                : (classified.State == ConnectionState.Connected ? ConnectionState.Degraded : ConnectionState.Degraded);
            baseConn.ErrorCode = classified.Error;
            baseConn.Reason = "Antigravity check: " + classified.Reason;
            baseConn.RecommendedAction = ConnectionErrorPriority.RecommendedAction(baseConn.ErrorCode, baseConn.State);
            return baseConn;
        }

        static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string line = text.Split('\n')[0].Trim();
            return line.Length > 80 ? line.Substring(0, 80) : line;
        }
    }

    // ── the common post-action watcher ───────────────────────────────────────
    // After an interactive installer/login LIMISAW verifies automatically on a
    // finite, low-frequency cadence. Process exit is a signal that may trigger
    // an earlier attempt, never authority in itself. Each operation belongs to
    // a vendor generation: a newer action invalidates the older watcher and
    // shutdown stops every watcher without touching user-owned children.
    internal static class ConnectionWatcher
    {
        // Test seam (W2-002 A): pause after a tick has CAPTURED its due
        // operations but before callback dispatch, so a harness can run
        // BeginShutdown inside the capture/dispatch gap. Null in production.
        internal static Action BeforeDispatch = null;

        // Documented bounded cadence: immediate, 0.5s, 1s, 2s, 3s, then every
        // ~5s — inside one onboarding window of 90 seconds.
        internal static readonly double[] Delays = { 0, 0.5, 1, 2, 3 };
        internal const double SteadyDelayS = 5;
        internal const double WindowS = 90;

        internal class Operation
        {
            public string VendorId;
            public int Generation;
            public double StartedAt;
            public double NextAttemptAt;
            public int Attempt;
            // R081/R026 in-flight guard: at most ONE verification attempt per
            // operation may run at a time. The tick skips a due operation whose
            // previous attempt has not finished; AttemptFinished re-opens it.
            public bool InFlight;
            // R029: a Poke that lands while an attempt is in flight must not be
            // lost — the next attempt runs as soon as the current one finishes.
            public bool PokePending;
            // One verification attempt. Returns the result snapshot, or null
            // to keep waiting (e.g. the installer binary has not appeared yet).
            public Func<VendorConnection> Verify;
        }

        static readonly object Gate = new object();
        static readonly List<Operation> Ops = new List<Operation>();
        static System.Threading.Timer Timer;

        internal static Func<double> Now = () => Stamp.Now;
        // The owner of what an attempt means (publish + repaint). The form
        // binds these; tests bind their own.
        internal static Action<Operation> OnAttempt;
        internal static Action<Operation> OnExpire;
        internal static Action<Operation> OnRemoved = null;

        internal static int StartedCount, ExpiredCount;
        internal static VendorConnection LastExpired = null;

        public static void Start(Operation op)
        {
            if (op == null) return;
            // SUPERSESSION: the operations a newer one displaces are reported
            // through the SAME removal seam as an explicit cancel, so whoever
            // owns the vendor-side resources of generation N learns that N is
            // dead. Dispatch happens outside Gate — a removal handler may do
            // bounded vendor I/O and must never hold the watcher's lock.
            List<Operation> removed = new List<Operation>();
            lock (Gate)
            {
                for (int i = Ops.Count - 1; i >= 0; i--)
                {
                    if (Ops[i].VendorId != op.VendorId) continue;
                    removed.Add(Ops[i]);
                    Ops.RemoveAt(i);
                }
                op.StartedAt = Now();
                op.Attempt = 0;
                op.NextAttemptAt = op.StartedAt + DelayFor(0);
                Ops.Add(op);
                StartedCount++;
                EnsureTimer();
            }
            Dispatch(removed, op.Generation);
        }

        public static void CancelGeneration(string vendorId, int generation)
        {
            List<Operation> removed = new List<Operation>();
            lock (Gate)
            {
                for (int i = Ops.Count - 1; i >= 0; i--)
                {
                    if (Ops[i].VendorId != vendorId) continue;
                    if (generation >= 0 && Ops[i].Generation != generation) continue;
                    removed.Add(Ops[i]);
                    Ops.RemoveAt(i);
                }
            }
            Dispatch(removed, int.MinValue);
        }

        // `keepGeneration` is the generation that is taking over: N+1 must never
        // be handed its own removal notice when Start displaces a same-vendor N.
        static void Dispatch(List<Operation> removed, int keepGeneration)
        {
            var handler = OnRemoved;
            if (handler == null) return;
            foreach (var o in removed)
            {
                if (o.Generation == keepGeneration) continue;
                try { handler(o); } catch { }
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                Ops.Clear();
                if (Timer != null) { Timer.Dispose(); Timer = null; }
            }
        }

        // Process exit pokes: the vendor's operation attempts again at once.
        // A poke at an operation that is IN FLIGHT is remembered (PokePending)
        // instead of dropped, so the signal survives until the attempt's
        // completion re-opens the operation.
        public static void Poke(string vendorId)
        {
            lock (Gate)
            {
                double now = Now();
                foreach (var op in Ops)
                {
                    if (op.VendorId != vendorId) continue;
                    if (op.InFlight) { op.PokePending = true; continue; }
                    if (op.NextAttemptAt > now) op.NextAttemptAt = now;
                }
            }
        }

        // R028: attempt completion. The attempt callback may run async work
        // (vendor CLI I/O); it reports "done" here so the operation becomes
        // eligible for its next attempt. A completion from a SUPERSEDED
        // operation (its watcher was removed by Start/CancelGeneration/Shutdown)
        // is a no-op: it can neither revive nor reschedule the dead watcher.
        public static void AttemptFinished(string vendorId, int generation)
        {
            double now = Now();
            Operation resumed = null;
            lock (Gate)
            {
                foreach (var op in Ops)
                {
                    if (op.VendorId != vendorId || op.Generation != generation) continue;
                    op.InFlight = false;
                    // A poke that arrived during the attempt accelerates the
                    // next one instead of being lost.
                    if (op.PokePending) { op.PokePending = false; op.NextAttemptAt = now; }
                    resumed = op;
                    break;
                }
            }
            // Outside the lock: the timer tick reads the same state.
            if (resumed != null) Tick(null);
        }

        internal static int PendingCount { get { lock (Gate) return Ops.Count; } }
        internal static bool Pending(string vendorId, int generation)
        {
            lock (Gate)
            {
                foreach (var op in Ops) if (op.VendorId == vendorId && op.Generation == generation) return true;
                return false;
            }
        }

        // Test/diagnostic seam: how many operations currently hold an in-flight
        // attempt. The no-overlap regression reads this under a slow fake verify.
        internal static int InFlightCount
        {
            get { lock (Gate) { int n = 0; foreach (var op in Ops) if (op.InFlight) n++; return n; } }
        }

        internal static double DelayFor(int attempt)
        {
            if (attempt < Delays.Length) return Delays[attempt];
            return SteadyDelayS;
        }

        static void EnsureTimer()
        {
            if (Timer != null) return;
            Timer = new System.Threading.Timer(Tick, null, TickPeriodMs, TickPeriodMs);
        }

        // The tick period the timer restarts with after Shutdown. Tests may
        // tighten it for speed; production default is 250 ms.
        internal static int TickPeriodMs = 250;

        static void Tick(object _)
        {
            List<Operation> due = new List<Operation>();
            List<Operation> expired = new List<Operation>();
            lock (Gate)
            {
                if (Ops.Count == 0) return;
                double now = Now();
                for (int i = Ops.Count - 1; i >= 0; i--)
                {
                    var op = Ops[i];
                    if (now - op.StartedAt >= WindowS)
                    {
                        Ops.RemoveAt(i);
                        expired.Add(op);
                        continue;
                    }
                    // R026/R027: an operation with an attempt still running is
                    // skipped — never a second concurrent verify for the same
                    // vendor operation. Other vendors keep their own cadence.
                    if (op.InFlight) continue;
                    if (now >= op.NextAttemptAt)
                    {
                        op.Attempt++;
                        op.InFlight = true;
                        op.NextAttemptAt = now + DelayFor(op.Attempt);
                        due.Add(op);
                    }
                }
            }
            foreach (var op in expired)
            {
                ExpiredCount++;
                if (OnExpire != null) OnExpire(op);
            }
            // Test seam (W2-002 A): pause AFTER the due operations were
            // captured under the lock but BEFORE dispatch, so a harness can
            // run BeginShutdown inside the capture/dispatch gap. Null in
            // production.
            Action beforeDispatch = BeforeDispatch;
            if (beforeDispatch != null) beforeDispatch();
            // Due attempts run on POOL threads, not the timer thread: a slow
            // OnAttempt can never block the next tick from firing other
            // vendors' due attempts (cross-vendor parallelism), while each
            // operation's own InFlight flag still serializes its retries.
            foreach (var op in due)
            {
                var target = op;
                if (OnAttempt == null) continue;
                System.Threading.ThreadPool.QueueUserWorkItem(poolArg =>
                {
                    try { OnAttempt(target); }
                    catch { ConnectionWatcher.AttemptFinished(target.VendorId, target.Generation); }
                });
            }
        }
    }

    // ── vendor operation registry ────────────────────────────────────────────
    // vendorId -> the adapter entry points the UI routes through. Tests swap
    // entries instead of launching real vendor CLIs; production registers the
    // real adapters here.
    // What an "add another account" click produced. The home is named back so
    // the UI can say WHERE the new account will live instead of just "started".
    internal class AddAccountOutcome
    {
        public bool Ok;
        public string Home = "";
        public string Error;
        // EPHEMERAL: a device-code retry's one-time code, carried to the screen
        // and nowhere else. It is never persisted, never copied onto a
        // VendorConnection, and dies with the attempt that minted it.
        public string UserCode = "";
    }

    internal static class ConnectionAdapterRegistry
    {
        // One read-only verification attempt per vendor.
        internal static readonly Dictionary<string, Func<ConnectionVerifyResult>> Verify =
            new Dictionary<string, Func<ConnectionVerifyResult>>(StringComparer.Ordinal);

        // Deeper troubleshooting, run ONLY on an explicit Troubleshoot click.
        internal static readonly Dictionary<string, Func<VendorConnection, VendorConnection>> Doctor =
            new Dictionary<string, Func<VendorConnection, VendorConnection>>(StringComparer.Ordinal);

        // The interactive sign-in launch. Returns false when the vendor CLI is
        // missing; the caller reports that instead of a second path.
        internal static readonly Dictionary<string, Func<bool>> SignIn =
            new Dictionary<string, Func<bool>>(StringComparer.Ordinal);

        // Signing in to an ADDITIONAL account. Present only for vendors where a
        // second account is a second config home — which is also what makes the
        // button appear, so a vendor that cannot do it never offers it.
        // The onboarding GENERATION is passed in: an add-account flow that
        // owns live vendor state (Codex's managed login) binds that state to
        // the generation the coordinator already minted for this click.
        internal static readonly Dictionary<string, Func<int, AddAccountOutcome>> AddAccount =
            new Dictionary<string, Func<int, AddAccountOutcome>>(StringComparer.Ordinal);

        // Retrying an ADD that authenticated as an account already present. It
        // is a separate entry, not a flag on AddAccount, because it targets an
        // EXISTING duplicate home and may never allocate a new one.
        internal static readonly Dictionary<string, Func<int, AddAccountOutcome>> RetryDifferentAccount =
            new Dictionary<string, Func<int, AddAccountOutcome>>(StringComparer.Ordinal);

        static ConnectionAdapterRegistry()
        {
            Verify["codex"] = () =>
            {
                var home = CodexConnectionAdapter.TargetHome();
                if (home == null)
                    return new ConnectionVerifyResult { State = ConnectionState.SignInRequired, Error = ConnectionErrorCode.AuthMissing, Reason = "No Codex home found" };
                var r = CodexConnectionAdapter.VerifyHome(home.Path, Stamp.Now + CodexConnectionAdapter.VerifyDeadlineS);
                r.Detail = home.Id;
                return r;
            };
            Verify["claude"] = () => ClaudeConnectionAdapter.Verify(Stamp.Now + ClaudeConnectionAdapter.VerifyDeadlineS);
            Verify["antigravity"] = () => AntigravityConnectionAdapter.Verify(Stamp.Now + AntigravityConnectionAdapter.VerifyDeadlineS);
            // T-50: FreeBuff verification is the usage read behind the
            // permission, and its result carries an absolute balance rather than
            // a ConnectionVerifyResult, so the form dispatches it directly
            // (like zcode). Only the vendor-owned VISIBLE login is registered
            // here.
            SignIn["freebuff"] = () =>
            {
                string error;
                return FreebuffConnectionAdapter.TryOpenVendor(out error);
            };

            Doctor["codex"] = c => CodexConnectionAdapter.Doctor(c);
            Doctor["claude"] = c => ClaudeConnectionAdapter.Doctor(c);
            Doctor["antigravity"] = c => AntigravityConnectionAdapter.Doctor(c);

            SignIn["codex"] = () =>
            {
                string error;
                return CodexConnectionAdapter.LaunchLogin(CodexConnectionAdapter.TargetHome(), out error);
            };
            SignIn["claude"] = () =>
            {
                string error;
                return ClaudeConnectionAdapter.LaunchLogin(out error);
            };
            SignIn["antigravity"] = () =>
            {
                string error;
                return AntigravityConnectionAdapter.LaunchLogin(out error);
            };

            AddAccount["claude"] = _ =>
            {
                string home, error;
                bool ok = ClaudeConnectionAdapter.LaunchAddAccount(out home, out error);
                return new AddAccountOutcome { Ok = ok, Home = home, Error = error };
            };
            AddAccount["codex"] = gen =>
            {
                string home, error;
                bool ok = CodexConnectionAdapter.LaunchAddAccount(gen, out home, out error);
                return new AddAccountOutcome { Ok = ok, Home = home, Error = error };
            };
            RetryDifferentAccount["codex"] = gen =>
            {
                string home, code, error;
                bool ok = CodexConnectionAdapter.LaunchRetryDifferentAccount(gen, out home, out code, out error);
                return new AddAccountOutcome { Ok = ok, Home = home, Error = error, UserCode = code };
            };
        }

        internal static void Reset()
        {
            Verify.Clear(); Doctor.Clear(); SignIn.Clear(); AddAccount.Clear();
            RetryDifferentAccount.Clear();
        }
    }
}
