using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

// Zcode (Z.ai / BigModel GLM Coding Plan) quota.
//
// Zcode is the one vendor here with no CLI to ask: it ships as an Electron
// desktop app and puts nothing on PATH. Its quota lives behind an authenticated
// HTTP endpoint — `GET /api/monitor/usage/quota/limit` — which is the exact call
// the app itself makes (buildZaiQuotaUrl / BigModelUsageQuotaProvider in its own
// bundle), and it answers with both windows at once:
//
//   {"code":200,"data":{"level":"lite","limits":[
//     {"type":"CREDIT_LIMIT","unit":3,"number":5,  "usage":2000,
//      "remaining":1829,"percentage":8,"nextResetTime":1788498595214},
//     {"type":"CREDIT_LIMIT","unit":6,"number":1,  "usage":10000,
//      "remaining":9829,"percentage":1,"nextResetTime":1789085298997}]}}
//
// `unit` is a calendar unit and `number` its count: 3/5 is the 5-hour window,
// 6/1 the weekly one. `TIME_LIMIT` rows are the monthly MCP/tool allowance, not
// a quota window, so they are ignored (the app's own `NL` set treats them as a
// separate family).
//
// ── the read costs nothing ───────────────────────────────────────────────────
//
// Reading the quota must never spend it. Every provider in LIMISAW obeys that,
// each in its vendor's own idiom, and none of them sends a prompt:
//
//   * Codex     — `account/rateLimits/read` over the app-server's JSON-RPC. A
//                 read method; no completion, no turn.
//   * Claude    — `claude -p "/usage"`, a slash command the CLI answers locally.
//                 Measured on 2.1.259: `num_turns: 0`, `total_cost_usd: 0`.
//   * Antigravity — `agy -p "/usage" --output-format json`, which its own
//                 changelog describes as answering "without starting an agent
//                 turn, spending quota, or leaving a conversation behind".
//   * Zcode     — this monitor endpoint. Measured: six consecutive reads left
//                 `currentValue` at 0 and both remainders identical.
//
// Asking a model "how much quota do I have left" would be the one implementation
// that burns the thing it reports, so no provider may ever be "fixed" that way.
//
// ── why this one needs a switch ──────────────────────────────────────────────
//
// Every other provider is asked through the vendor's own CLI, which means
// LIMISAW never holds a credential: the CLI authenticates the way its vendor
// intends and hands back a number. Zcode has no such path, so reading its quota
// needs an API key, and a key sitting in another application's config file is
// NOT LIMISAW's to take. Reading it silently would make this program do exactly
// what its README promises it does not.
//
// So the key comes from one of two places, in this order:
//
//   1. `ZAI_API_KEY` / `ZCODE_API_KEY` in the environment — the user handed it
//      over deliberately. Always allowed.
//   2. Zcode's own config, `%USERPROFILE%\.zcode\v2\config.json` — ONLY when
//      `ZcodeReadConfig=1` is set in LIMISAW.ini. Off by default, and the ini is
//      the user's file, so enabling it is an explicit act.
//
// With neither, the card says so and nothing is read. The key is used for one
// GET to the vendor's own quota host and is never logged, never written, never
// sent anywhere else — `Redact` exists to keep it out of error text even by
// accident.
namespace Limisaw
{
    static class ZcodeSource
    {
        // The endpoint path, identical on both hosts the vendor operates.
        //
        // READ-ONLY BY CONTRACT. This is a monitor endpoint: it reports quota and
        // spends none. Measured — six consecutive reads left `currentValue` at 0
        // and both remainders unchanged. That property is the whole reason
        // LIMISAW may poll every few minutes, and it is what
        // `tests/limits.cs` pins so nobody later "fixes" a probe by asking a
        // model how much quota is left.
        public const string QuotaPath = "/api/monitor/usage/quota/limit";

        // Z.ai and BigModel are the same product on two hosts (mainland vs
        // international). Only these two are ever contacted, and the host is a
        // constant here rather than anything read from a file: a config-supplied
        // URL would turn "read a key" into "send the key wherever this file
        // says", which is a different and much worse permission.
        public const string HostZai = "https://api.z.ai";
        public const string HostBigModel = "https://open.bigmodel.cn";

        static readonly string[] Hosts = { HostZai, HostBigModel };

        // Calendar units as the vendor numbers them, paired with `number`.
        const int UnitHour = 3;
        const int UnitWeek = 6;

        public static string ConfigPath()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(profile)) return "";
            return Path.Combine(profile, ".zcode", "v2", "config.json");
        }

        // Zcode present at all? A credential the user has supplied is enough to
        // offer the card, with or without the desktop config: the documented
        // env path (ZAI_API_KEY / ZCODE_API_KEY) reaches the same endpoint and
        // Resolve() honors it, so gating Probe.Run behind the config file made
        // the advertised env-only use case dead. "Installed but I may not read
        // your key" stays a separate situation with its own message.
        public static bool Installed()
        {
            if (HasEnvKey()) return true;
            string path = ConfigPath();
            return path.Length > 0 && File.Exists(path);
        }

        public static bool ConfigExists()
        {
            string path = ConfigPath();
            return path.Length > 0 && File.Exists(path);
        }

        // True when either supported env credential is set and non-blank.
        public static bool HasEnvKey()
        {
            return EnvKey() != null;
        }

        // Which env credential is set, by name (ZAI_API_KEY wins — the same
        // order Resolve() consumes them). The VALUE never passes through here.
        public static string EnvKey()
        {
            foreach (string name in new[] { EnvPrimary, EnvAlternate })
                if (((Environment.GetEnvironmentVariable(name) ?? "").Trim()).Length > 0) return name;
            return null;
        }

        // ── credential ───────────────────────────────────────────────────────
        public class Key
        {
            public string Value = "";
            public string Origin = "";     // shown to the user, never the key
            public string Refusal;         // why there is no key, if there is none
            public string Host;            // which host this credential belongs to, when known
            // The machine-readable situation, for the Settings row and tests.
            // One of: "", "env", "config", "not-detected", "config-denied",
            // "no-provider", "no-key".
            public string State = "";
        }

        public const string EnvPrimary = "ZAI_API_KEY";
        public const string EnvAlternate = "ZCODE_API_KEY";

        // `allowConfig` is LIMISAW.ini's ZcodeReadConfig (Settings: "Config
        // access"). It is passed in rather than read here so the decision has
        // exactly one owner (LimisawSettings) and the test can drive both
        // halves without touching an ini.
        //
        // Every refusal names the situation it actually is — not detected, not
        // permitted, no supported provider, provider without a readable key —
        // because "unavailable" telling the user to sign in when the real
        // problem is a permission LIMISAW itself owns is a dead end.
        public static Key Resolve(bool allowConfig)
        {
            foreach (string name in new[] { EnvPrimary, EnvAlternate })
            {
                string value = (Environment.GetEnvironmentVariable(name) ?? "").Trim();
                if (value.Length > 0)
                    return new Key { Value = value, Origin = "$" + name, State = "env" };
            }
            if (!allowConfig)
                return ConfigExists()
                    ? new Key
                    {
                        Refusal = "Zcode detected — config access is off; enable it in Settings, or set " + EnvPrimary,
                        State = "config-denied",
                    }
                    : new Key
                    {
                        Refusal = "Zcode not detected — install Zcode, enable config access in Settings, or set " + EnvPrimary,
                        State = "not-detected",
                    };
            string path = ConfigPath();
            if (path.Length == 0 || !File.Exists(path))
                return new Key { Refusal = "Zcode config not found (" + EnvPrimary + " not set either)", State = "not-detected" };
            ConfigScan scan = ScanConfigCached(path);
            if (!scan.Parsed)
                return new Key { Refusal = "Zcode config could not be read as JSON", State = "no-provider" };
            if (!scan.HasProviderMap)
                return new Key { Refusal = "Zcode config has no provider list", State = "no-provider" };
            if (!scan.VendorProviderSeen)
                return new Key
                {
                    Refusal = "config allowed — no Z.ai / BigModel provider in Zcode's config",
                    State = "no-provider",
                };
            if (string.IsNullOrEmpty(scan.Key))
                return new Key
                {
                    Refusal = "config allowed — the Z.ai / BigModel provider has no readable API key",
                    State = "no-key",
                };
            return new Key
            {
                Value = scan.Key,
                Origin = "Zcode config",
                Host = scan.HostHint,
                State = "config",
            };
        }

        // Which host a config provider belongs to. A HINT for order only: both
        // hosts are still tried, so a wrong guess costs nothing but a reorder,
        // while a right one spends the budget on the host that owns the key.
        // NEVER a destination: the quota request goes only to the two constant
        // hosts above, whatever a config file claims.
        static string HostHint(string providerId, string baseUrl)
        {
            if (providerId != null)
            {
                if (providerId.IndexOf("bigmodel", StringComparison.OrdinalIgnoreCase) >= 0) return HostBigModel;
                if (providerId.IndexOf("zai", StringComparison.OrdinalIgnoreCase) >= 0) return HostZai;
            }
            string host = HostOf(baseUrl);
            if (host == "open.bigmodel.cn" || host.EndsWith(".bigmodel.cn", StringComparison.Ordinal)) return HostBigModel;
            if (host == "api.z.ai" || host == "zcode.z.ai" || host.EndsWith(".z.ai", StringComparison.Ordinal)) return HostZai;
            return null;
        }

        static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try { return new Uri(url.Trim()).Host.ToLowerInvariant(); }
            catch { return ""; }
        }

        // One provider entry out of Zcode's config, classified. The key is the
        // only secret-shaped thing read here and it never leaves this class
        // except into the Key that the probe uses for one header.
        public class ZcodeProvider
        {
            public string Id = "";
            public bool Enabled = true;      // absent flag counts as enabled
            public bool CodingPlan;          // a *-coding-plan id
            public bool StartPlan;           // a *-start-plan id
            public string BaseUrl = "";      // HINT ONLY, never a destination
            public string Key = "";

            // Higher is preferred. Enabled outranks disabled (a disabled
            // provider's key is a leftover, not a live credential), a Coding
            // Plan key outranks the generic pay-as-you-go one (it reports plan
            // windows, which is what the tray shows), a start-plan variant
            // sits between the two. Equal rank keeps config order.
            //
            // Rank only ever compares KEY-BEARING candidates. A provider with
            // no key has no credential to offer, however plan-shaped its id —
            // letting it outrank a usable key was the bug where an enabled
            // coding-plan slot without an apiKey masked a working generic
            // provider's real credential.
            public int Rank
            {
                get { return (Enabled ? 4 : 0) + (CodingPlan ? 2 : 0) + (StartPlan ? 1 : 0); }
            }
            public bool HasKey
            {
                get { return Key != null && Key.Trim().Length > 0; }
            }
        }

        // The trust boundary. Only the vendor's own built-in provider family
        // may hand LIMISAW a credential: a `builtin:` id carrying the vendor's
        // name, or a `builtin:` entry whose own base URL is one of the vendor's
        // known hosts. Everything else — custom, OpenAI-compatible,
        // third-party, however vendor-like its name — is invisible to the
        // credential scan no matter what key it holds.
        internal static bool VendorFamily(string id, string baseUrl)
        {
            if (!(id ?? "").StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)) return false;
            bool zaiId = id.IndexOf("zai", StringComparison.OrdinalIgnoreCase) >= 0;
            bool bigId = id.IndexOf("bigmodel", StringComparison.OrdinalIgnoreCase) >= 0;
            if (zaiId || bigId) return true;
            string host = HostOf(baseUrl);
            return host == "api.z.ai" || host == "zcode.z.ai"
                || host.EndsWith(".z.ai", StringComparison.Ordinal)
                || host == "open.bigmodel.cn" || host.EndsWith(".bigmodel.cn", StringComparison.Ordinal);
        }

        public class ConfigScan
        {
            public bool Parsed;             // the file existed and was valid JSON under the size cap
            public bool HasProviderMap;     // a provider map is present
            public bool VendorProviderSeen; // at least one Z.ai / BigModel family entry
            public string ProviderId;       // the entry the key came from
            public string Key;              // empty when nothing readable
            public string HostHint;
            public ZcodeGenerationSummary Summary; // secret-free generation snapshot (R020)
        }

        // ── SRC-007 (completes SRC-006:R020): the shared secret-free generation
        // summary. ONE immutable snapshot per refresh generation, consumed by
        // BOTH the worker (Probe/Resolve) and the Level-0/UI projection
        // (BuildZcodeConnection/Verify) with no extra config read/parse/stat:
        // it is filled by the single ScanConfig pass that generation already
        // performs. Contains ONLY safe metadata:
        //   ConfigPresent / CredentialOrigin / ProviderId / HostCategory /
        //   ConfigRevision.
        // ConfigRevision identifies the config file revision the generation
        // represents, derived from NON-SECRET metadata captured during the
        // same single scan (canonical path + length + last-write ticks). The
        // config BODY is never hashed and never stored — hashing it would mean
        // keeping a digest of credential-bearing bytes and re-reading them on
        // every check. ABSOLUTE SECRET RULE: no API key, no bearer token, no
        // config body, no raw credential JSON, no arbitrary provider values
        // ever enters this summary.
        public class ZcodeGenerationSummary
        {
            public bool ConfigPresent;
            public string CredentialOrigin;   // environment / config / none
            public string ProviderId;        // the provider entry id (safe: a builtin: name, never a key)
            public bool VendorProviderSeen;  // a trusted family entry exists (with or without a key)
            public string HostCategory;      // zai / bigmodel / unknown
            public string ConfigRevision;    // path+length+mtime token, never content

            internal static ZcodeGenerationSummary Build(bool present, string origin,
                string providerId, bool providerSeen, string hostCategory, string revision)
            {
                var s = new ZcodeGenerationSummary();
                s.ConfigPresent = present;
                s.CredentialOrigin = origin ?? "";
                s.ProviderId = providerId ?? "";
                s.VendorProviderSeen = providerSeen;
                s.HostCategory = hostCategory ?? "unknown";
                s.ConfigRevision = revision ?? "";
                return s;
            }
        }

        // Short stable digest of a non-secret string (same shape as
        // LIMISAW SoundCue.Tag / Probe.HomeId).
        static string Tag(string text)
        {
            byte[] bytes = System.Security.Cryptography.SHA256.Create()
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? ""));
            var sb = new System.Text.StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        // ── PERF-003 (SRC-006:R020): generation-scoped config body cache ─────
        // The Zcode config body is read/parsed AT MOST ONCE per refresh
        // generation: Resolve (BuildZcodeConnection in the snapshot build),
        // Probe (the sweep) and Verify (a user check) all consume the same
        // cached scan. A generation is an immutable discovery transaction:
        // changes made during it become visible only after BeginGeneration.
        // Only active when a refresh generation exists — legacy call sites
        // keep the always-fresh behaviour. The cached scan carries the
        // credential only inside ProbeZcode's sealed scope.
        static ConfigScan CachedScan;
        static string CachedScanPath;
        static int CachedScanGen = int.MinValue;

        internal static ConfigScan ScanConfigCached(string path)
        {
            int gen = ExecutableDiscovery.CurrentGeneration;
            if (gen <= 0) return ScanConfig(path);
            if (CachedScan != null && CachedScanGen == gen && CachedScanPath == path)
                return CachedScan;
            ConfigScan scan = ScanConfig(path);
            CachedScan = scan;
            CachedScanGen = gen;
            CachedScanPath = path;
            return scan;
        }

        internal static void ResetConfigCacheForTests()
        { CachedScan = null; CachedScanPath = null; CachedScanGen = int.MinValue; CachedSummary = null; CachedSummaryGen = int.MinValue; }

        // ── SRC-007: the shared secret-free generation summary accessor ────
        // ONE summary per refresh generation, consumed by BOTH the worker and
        // the Level-0/UI projection. Inside a generation it is IMMUTABLE and
        // memoized once: no config read/parse/stat walk happens again after
        // the snapshot is built — a disk mutation during generation N cannot
        // alter N. Outside a generation (legacy/tests, gen <= 0) it is
        // computed fresh each call from the same one-scan budget.
        static ZcodeGenerationSummary CachedSummary;
        static int CachedSummaryGen = int.MinValue;

        public static ZcodeGenerationSummary GenerationSummary(bool allowConfig)
        {
            int gen = ExecutableDiscovery.CurrentGeneration;
            if (gen > 0 && CachedSummary != null && CachedSummaryGen == gen) return CachedSummary;
            ZcodeGenerationSummary s = BuildSummary(allowConfig);
            if (gen > 0) { CachedSummary = s; CachedSummaryGen = gen; }
            return s;
        }

        // Builds the snapshot. env credential wins the origin exactly like
        // Resolve; the config body is read only when the config path is the
        // one actually consulted, and only through the per-generation cached
        // scan, so the budget stays <=1 read + <=1 parse per generation.
        static ZcodeGenerationSummary BuildSummary(bool allowConfig)
        {
            if (HasEnvKey())
                return ZcodeGenerationSummary.Build(ConfigExists(), "environment", "", false, "unknown", "");
            if (!allowConfig)
                return ZcodeGenerationSummary.Build(ConfigExists(), "none", "", false, "unknown", "");
            string path = ConfigPath();
            if (path.Length == 0 || !File.Exists(path))
                return ZcodeGenerationSummary.Build(false, "none", "", false, "unknown", "");
            ZcodeGenerationSummary s = (ExecutableDiscovery.CurrentGeneration > 0
                ? ScanConfigCached(path) : ScanConfig(path)).Summary;
            return s ?? ZcodeGenerationSummary.Build(true, "none", "", false, "unknown", "");
        }

        // The whole config scan in one place: exactly one field out of one
        // file — the best usable (key-bearing) Z.ai / BigModel provider's
        // apiKey — read-only, size-capped, everything else in the config
        // ignored. Provider ids are DISCOVERED, not a fixed list: Zcode
        // legitimately ships variants (`builtin:zai-coding-plan`,
        // `builtin:zai-start-plan`, the bigmodel twins, future plan ids), and
        // a fixed array silently went blind the day the vendor added one.
        internal static ConfigScan ScanConfig(string path)
        {
            var scan = new ConfigScan();
            object doc;
            long length = 0, mtimeTicks = 0;
            try
            {
                // A config that grew huge is a config we do not understand;
                // refusing to parse it beats loading an arbitrary blob. The
                // metadata check is only the fast refusal (W2-005): the OPENED
                // handle is the real bound, so a file that grows after this stat
                // still cannot be read past the cap.
                var info = new FileInfo(path);
                length = info.Length;
                mtimeTicks = info.LastWriteTimeUtc.Ticks;
                if (info.Length > 4 * 1024 * 1024) { FillSummary(scan, path, length, mtimeTicks, null); return scan; }
                long bytesRead;
                string readError;
                // PERF-003 (SRC-006:R020): one body read + one parse per
                // generation are the audited budget; the counting seams are
                // what the regression harness asserts.
                ExecutableDiscovery.ConfigReads++;
                string text = BoundedFile.ReadAllText(path, 4 * 1024 * 1024, out bytesRead, out readError);
                if (text == null) { FillSummary(scan, path, length, mtimeTicks, null); return scan; }
                doc = J.Parse(text);
            }
            catch { FillSummary(scan, path, length, mtimeTicks, null); return scan; }
            if (doc == null) { FillSummary(scan, path, length, mtimeTicks, null); return scan; }
            ExecutableDiscovery.ConfigParses++;
            scan.Parsed = true;
            object providers = J.Get(doc, "provider");
            if (providers == null) { FillSummary(scan, path, length, mtimeTicks, null); return scan; }
            scan.HasProviderMap = true;

            ZcodeProvider bestKey = null;
            var map = J.Obj(providers);
            if (map == null) { FillSummary(scan, path, length, mtimeTicks, null); return scan; }
            foreach (KeyValuePair<string, object> entry in map)
            {
                string id = entry.Key ?? "";
                object body = entry.Value;
                if (body == null) continue;
                string baseUrl = J.Str(J.Get(J.Get(body, "options"), "baseURL"))
                    ?? J.Str(J.Get(body, "baseURL")) ?? "";
                if (!VendorFamily(id, baseUrl)) continue;
                var p = new ZcodeProvider
                {
                    Id = id,
                    BaseUrl = baseUrl,
                    CodingPlan = id.IndexOf("coding-plan", StringComparison.OrdinalIgnoreCase) >= 0,
                    StartPlan = id.IndexOf("start-plan", StringComparison.OrdinalIgnoreCase) >= 0,
                    Key = J.Str(J.Get(J.Get(body, "options"), "apiKey")) ?? J.Str(J.Get(body, "apiKey")) ?? "",
                };
                object enabledFlag = J.Get(body, "enabled");
                if (enabledFlag == null) enabledFlag = J.Get(J.Get(body, "options"), "enabled");
                if (enabledFlag != null)
                {
                    if (enabledFlag is bool) p.Enabled = (bool)enabledFlag;
                    else
                    {
                        string text = J.Str(enabledFlag);
                        if (text != null) p.Enabled = string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    }
                }
                // Two independent facts, tracked separately: a trusted vendor
                // family entry EXISTS (with or without a key — the Settings
                // row says "provider found, no readable key", never "no
                // provider"), and the best USABLE credential source. Only
                // key-bearing candidates compete for the key, so a keyless
                // high-rank provider can never suppress a working one.
                scan.VendorProviderSeen = true;
                if (p.HasKey && (bestKey == null || RankBetter(p, bestKey))) bestKey = p;
            }
            if (bestKey != null)
            {
                scan.ProviderId = bestKey.Id;
                scan.Key = bestKey.Key.Trim();
                scan.HostHint = HostHint(bestKey.Id, bestKey.BaseUrl);
            }
            FillSummary(scan, path, length, mtimeTicks, bestKey);
            return scan;
        }

        // SRC-007: fill the secret-free generation summary from facts the ONE
        // scan already produced (no extra read/parse/stat: length and mtime
        // came from the FileInfo the scan already took). `bestKey` may be
        // null (no usable key): provider presence is reported through
        // VendorProviderSeen, ProviderId stays empty, and host falls back to
        // the provider map's own family hint when one exists.
        static void FillSummary(ConfigScan scan, string path, long length, long mtimeTicks, ZcodeProvider bestKey)
        {
            string hostCategory;
            if (bestKey != null)
                hostCategory = HostHint(bestKey.Id, bestKey.BaseUrl) == HostBigModel ? "bigmodel" : "zai";
            else hostCategory = "unknown";
            string revision = Tag(path.ToLowerInvariant() + "|" + length + "|" + mtimeTicks);
            scan.Summary = ZcodeGenerationSummary.Build(
                true, "config", bestKey != null ? bestKey.Id : "", scan.VendorProviderSeen, hostCategory, revision);
        }

        // Compared ONLY between providers that carry a key: enabled outranks
        // disabled, then Coding Plan > Start Plan > generic, then the config's
        // own order stands (first seen wins). A keyless provider never enters
        // the comparison, so it cannot mask a usable credential by rank.
        static bool RankBetter(ZcodeProvider candidate, ZcodeProvider incumbent)
        {
            return candidate.Rank > incumbent.Rank;
        }

        // Exactly one field out of one file: the best Z.ai / BigModel
        // provider's apiKey. Nothing else in that config is looked at, and the
        // file is opened read-only.
        public static string FromConfig(string path)
        {
            string providerId;
            return FromConfig(path, out providerId);
        }

        // `providerId` reports WHICH provider entry the key came from, which is
        // the only reliable hint about which of the two hosts owns it.
        public static string FromConfig(string path, out string providerId)
        {
            providerId = null;
            ConfigScan scan = ScanConfig(path);
            providerId = scan.ProviderId;
            return scan.Key;
        }

        // A secret must not reach a card, a log or an exception message. Every
        // string that leaves this file goes through here.
        public static string Redact(string text, string secret)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret)) return text;
            return text.Replace(secret, "<key>");
        }

        // ── probe ────────────────────────────────────────────────────────────
        // The request seam. Production is `Get`, an HTTPS call to one of the two
        // constant hosts; a test substitutes a transport so host fallback and the
        // retry budget can be driven without a socket. This is NOT a way to point
        // LIMISAW at another URL: the hosts stay constants above.
        internal delegate string Fetcher(string url, string key, double deadline, out string error);

        internal static Fetcher Transport = Get;

        // The host that owns the key first, the other one still after it.
        static string[] HostOrder(string first)
        {
            if (string.IsNullOrEmpty(first)) return Hosts;
            var order = new List<string> { first };
            foreach (string host in Hosts)
                if (host != first) order.Add(host);
            return order.ToArray();
        }

        public static ProbeAccount Probe(double deadline, bool allowConfig)
        {
            var acc = new ProbeAccount
            {
                Provider = "zcode", ProviderLabel = "Zcode", Name = "Zcode",
            };
            Key key = Resolve(allowConfig);
            if (key.Value.Length == 0)
            {
                // Not a fault in Zcode and not something a retry fixes: it is a
                // permission the user has not granted. Quiet, so the card reads
                // muted `idle:` instead of a red line with nothing broken.
                acc.Status = Model.UNAVAILABLE;
                acc.Quiet = true;
                acc.Error = key.Refusal;
                acc.Windows.Add(ProbeWindow.Unavailable(Model.FIVE_HOUR));
                acc.Windows.Add(ProbeWindow.Unavailable(Model.WEEKLY));
                return acc;
            }

            string[] hosts = HostOrder(key.Host);
            string lastError = null;
            for (int i = 0; i < hosts.Length; i++)
            {
                // W2-002: the account deadline is SHARED, so a host that hangs
                // must not spend it all. What is left is divided by the hosts
                // still untried — an unreachable api.z.ai leaves a real attempt
                // for open.bigmodel.cn instead of timing out past the deadline
                // and skipping the fallback the two-host design promises. A host
                // that fails fast forfeits nothing: the next one inherits the
                // whole remainder, and the last host gets all of it.
                double remaining = deadline - Stamp.Now;
                if (remaining <= 0.2) break;
                double attempt = Stamp.Now + remaining / (hosts.Length - i);
                string error;
                string body = Transport(hosts[i] + QuotaPath, key.Value, attempt, out error);
                if (body == null) { lastError = error; continue; }
                string plan;
                List<ProbeWindow> windows = ParseQuota(body, out plan, out error);
                if (windows == null || windows.Count == 0) { lastError = error; continue; }
                acc.Status = Model.OK;
                acc.Ok = true;
                acc.Plan = plan;
                acc.Windows = windows;
                return acc;
            }

            acc.Status = Model.ERROR;
            acc.Error = Redact(lastError ?? "Zcode quota could not be read", key.Value);
            acc.Windows.Add(ProbeWindow.Unavailable(Model.FIVE_HOUR));
            acc.Windows.Add(ProbeWindow.Unavailable(Model.WEEKLY));
            return acc;
        }

        static bool TlsReady;

        // A .NET Framework 4.0 target defaults to SSL3 + TLS 1.0, which every
        // current host refuses — measured: `SecureChannelFailure` against
        // api.z.ai on the first live call. TLS 1.2 is requested explicitly and
        // ADDED to whatever the process already allows, so this never downgrades
        // a policy someone else set. 1.3 is named by value because the enum
        // member does not exist in this framework.
        static void EnsureTls()
        {
            if (TlsReady) return;
            TlsReady = true;
            try
            {
                const SecurityProtocolType Tls13 = (SecurityProtocolType)12288;
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | Tls13;
            }
            catch
            {
                // An older framework may reject 1.3 outright; 1.2 alone still
                // gets us in, and a failure here is not worth losing the sweep.
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
                catch { }
            }
        }

        // W2-001: one explicit success-response ceiling. The quota payload is a
        // small JSON object; 1 MiB is orders of magnitude past any real
        // response while keeping retained memory tiny. Measured in BYTES, not
        // UTF-16 chars: the cap must bound what the socket can hand us, not
        // what a StringBuilder happened to reserve.
        internal const int MaxResponseBytes = 1024 * 1024;

        // The bounded reader for the one success path. Returns the decoded body
        // only when it fits the cap and the absolute deadline; otherwise null
        // with a machine-readable error. A declared length past the cap is
        // refused before any read; an unknown/chunked body is read
        // incrementally and abandoned the instant max+1 proves it oversized.
        internal static string ReadBoundedResponse(Stream stream, long contentLength, int maxBytes,
            double absoluteDeadline, out string error)
        {
            error = null;
            if (contentLength > maxBytes) { error = "response_too_large"; return null; }
            var acc = new System.IO.MemoryStream();
            var buf = new byte[8192];
            while (true)
            {
                // The absolute deadline, not ReadWriteTimeout, is the operation
                // budget: a server that trickles bytes just inside each socket
                // timeout can never keep this loop alive past the caller's
                // deadline.
                if (Stamp.Now > absoluteDeadline) { error = "deadline_exceeded"; return null; }
                long room = (long)maxBytes + 1 - acc.Length;
                if (room <= 0) { error = "response_too_large"; return null; }
                int toRead = buf.Length;
                if (toRead > room) toRead = (int)room;
                int n;
                try { n = stream.Read(buf, 0, toRead); }
                catch (Exception ex) { error = ex.GetType().Name; return null; }
                if (n <= 0) break;
                acc.Write(buf, 0, n);
                if (acc.Length > maxBytes) { error = "response_too_large"; return null; }
            }
            try { return System.Text.Encoding.UTF8.GetString(acc.ToArray()); }
            catch (Exception ex) { error = ex.GetType().Name; return null; }
        }

        // One GET, one header, no body, no redirects followed to another host.
        // Returns null and an explanation rather than throwing, so a dead
        // network is one card's error instead of a lost sweep.
        static string Get(string url, string key, double deadline, out string error)
        {
            error = null;
            double remaining = deadline - Stamp.Now;
            if (remaining <= 0.2) { error = "deadline_exceeded"; return null; }
            EnsureTls();
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = (int)Math.Max(500, Math.Min(remaining, 30.0) * 1000);
                request.ReadWriteTimeout = request.Timeout;
                request.UserAgent = "LIMISAW";
                request.Accept = "application/json";
                // A 3xx to another origin would forward the key somewhere the
                // user never authorised.
                request.AllowAutoRedirect = false;
                request.Headers["Authorization"] = key;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                {
                    if ((int)response.StatusCode >= 300)
                    { error = "HTTP " + (int)response.StatusCode; return null; }
                    if (stream == null) { error = "empty response"; return null; }
                    // W2-001/R030: the success body is BOUNDED and the absolute
                    // deadline is re-checked on every read. A declared length
                    // past the ceiling is refused without reading; an
                    // unknown/chunked body is read incrementally only until
                    // max+1 proves it oversized. Oversized or truncated input
                    // is NEVER parsed as quota truth.
                    return ReadBoundedResponse(stream, response.ContentLength, MaxResponseBytes, deadline, out error);
                }
            }
            catch (WebException ex)
            {
                // W2-010: the error response is a real WebResponse holding a
                // connection from the pool. Reading the status and walking away
                // leaked it on every 401/403/500, one lease per refresh against
                // a host the account will keep failing against until the key
                // is fixed.
                using (var response = ex.Response as HttpWebResponse)
                {
                    error = response != null
                        ? "HTTP " + (int)response.StatusCode
                        : ex.Status.ToString();
                }
                return null;
            }
            catch (Exception ex) { error = ex.GetType().Name; return null; }
        }

        // The vendor's envelope carries its own status separately from HTTP:
        // a 200 with `code: 401` is how an expired key answers, and reading it
        // as success would render "0 quota" on a working account.
        public static List<ProbeWindow> ParseQuota(string body, out string plan, out string error)
        {
            plan = null;
            error = null;
            object doc = J.Parse(body);
            if (doc == null) { error = "Zcode quota did not return JSON"; return null; }
            double? code = J.Num(J.Get(doc, "code"));
            object data = J.Get(doc, "data");
            if (code.HasValue && (int)code.Value != 200 && (int)code.Value != 0)
            {
                string message = (J.Str(J.Get(doc, "msg")) ?? "").Trim();
                error = "Zcode quota: " + (message.Length > 0 ? message : "code " + (int)code.Value);
                return null;
            }
            if (data == null) { error = "Zcode quota response carried no data"; return null; }
            plan = Normalise(J.Str(J.Get(data, "level")));

            var windows = new List<ProbeWindow>();
            foreach (object row in J.Arr(J.Get(data, "limits")))
            {
                string key = WindowKey(J.Str(J.Get(row, "type")),
                    J.Num(J.Get(row, "unit")), J.Num(J.Get(row, "number")));
                if (key == null) continue;
                windows.Add(new ProbeWindow
                {
                    Key = key,
                    DurationMinutes = Model.KnownDuration(key),
                    Available = true,
                    Remaining = RemainingPercent(row),
                    ResetEpoch = Stamp.Epoch(J.Get(row, "nextResetTime")),
                    Source = "zcode-quota-api",
                });
            }
            windows.Sort((a, b) =>
            {
                int da = a.DurationMinutes ?? int.MaxValue, db = b.DurationMinutes ?? int.MaxValue;
                return da != db ? da.CompareTo(db) : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            if (windows.Count == 0) error = "Zcode quota reported no readable window";
            return windows;
        }

        // TOKENS_LIMIT and CREDIT_LIMIT are the same family — the vendor's own
        // code treats them interchangeably, and which one appears depends on how
        // the plan is metered. TIME_LIMIT is the monthly tool allowance, a
        // different thing entirely, so it is not a window here.
        static string WindowKey(string type, double? unit, double? number)
        {
            string kind = (type ?? "").Trim().ToUpperInvariant();
            if (kind != "CREDIT_LIMIT" && kind != "TOKENS_LIMIT") return null;
            if (!unit.HasValue || !number.HasValue) return null;
            int u = (int)unit.Value, n = (int)number.Value;
            if (u == UnitHour && n == 5) return Model.FIVE_HOUR;
            if (u == UnitWeek && n == 1) return Model.WEEKLY;
            return null;
        }

        // `remaining`/`usage` are counts and `percentage` is percent USED. The
        // counts are preferred because they are exact where the percentage is
        // already rounded to a whole number by the server — at a 10 000-credit
        // weekly cap, one percent is a hundred credits.
        static double? RemainingPercent(object row)
        {
            double? remaining = J.Num(J.Get(row, "remaining"));
            double? total = J.Num(J.Get(row, "usage"));
            if (remaining.HasValue && total.HasValue && total.Value > 0)
                return Math.Max(0.0, Math.Min(100.0, remaining.Value / total.Value * 100.0));
            double? used = J.Num(J.Get(row, "percentage"));
            if (used.HasValue) return Math.Max(0.0, Math.Min(100.0, 100.0 - used.Value));
            return null;
        }

        // "lite" -> "Lite". The plan name is a tag on the card, and the vendor
        // sends it lowercase.
        static string Normalise(string level)
        {
            string text = (level ?? "").Trim();
            if (text.Length == 0) return null;
            return char.ToUpperInvariant(text[0]) + text.Substring(1).ToLowerInvariant();
        }
    }
}
