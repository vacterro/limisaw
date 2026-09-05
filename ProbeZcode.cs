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

        // True when either supported env credential is set and non-blank.
        public static bool HasEnvKey()
        {
            foreach (string name in new[] { EnvPrimary, EnvAlternate })
                if (((Environment.GetEnvironmentVariable(name) ?? "").Trim()).Length > 0) return true;
            return false;
        }

        // ── credential ───────────────────────────────────────────────────────
        public class Key
        {
            public string Value = "";
            public string Origin = "";     // shown to the user, never the key
            public string Refusal;         // why there is no key, if there is none
            public string Host;            // which host this credential belongs to, when known
        }

        public const string EnvPrimary = "ZAI_API_KEY";
        public const string EnvAlternate = "ZCODE_API_KEY";

        // `allowConfig` is LIMISAW.ini's ZcodeReadConfig. It is passed in rather
        // than read here so the decision has exactly one owner (LimisawSettings)
        // and the test can drive both halves without touching an ini.
        public static Key Resolve(bool allowConfig)
        {
            foreach (string name in new[] { EnvPrimary, EnvAlternate })
            {
                string value = (Environment.GetEnvironmentVariable(name) ?? "").Trim();
                if (value.Length > 0)
                    return new Key { Value = value, Origin = "$" + name };
            }
            if (!allowConfig)
                return new Key
                {
                    Refusal = "set " + EnvPrimary + ", or ZcodeReadConfig=1 in "
                        + "LIMISAW.ini to let LIMISAW read Zcode's own key",
                };
            string path = ConfigPath();
            if (path.Length == 0 || !File.Exists(path))
                return new Key { Refusal = "Zcode config not found (" + EnvPrimary + " not set either)" };
            string providerId;
            string key = FromConfig(path, out providerId);
            if (key == null)
                return new Key { Refusal = "no Coding Plan key in Zcode's config — sign in to Zcode, or set " + EnvPrimary };
            return new Key { Value = key, Origin = "Zcode config", Host = HostHint(providerId) };
        }

        // Which host a config provider belongs to. A HINT for order only: both
        // hosts are still tried, so a wrong guess costs nothing but a reorder,
        // while a right one spends the budget on the host that owns the key.
        static string HostHint(string providerId)
        {
            if (providerId == null) return null;
            if (providerId.IndexOf("bigmodel", StringComparison.OrdinalIgnoreCase) >= 0) return HostBigModel;
            if (providerId.IndexOf("zai", StringComparison.OrdinalIgnoreCase) >= 0) return HostZai;
            return null;
        }

        // Exactly one field out of one file: the Coding Plan provider's apiKey.
        // Nothing else in that config is looked at, and the file is opened
        // read-only. The order matters — a Coding Plan key reports plan windows,
        // while the plain `builtin:zai` API key reports the same endpoint for a
        // pay-as-you-go account, so the plan providers are preferred.
        static readonly string[] ProviderIds =
        {
            "builtin:zai-coding-plan",
            "builtin:bigmodel-coding-plan",
            "builtin:zai",
            "builtin:bigmodel",
        };

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
            object doc;
            try
            {
                // A config that grew huge is a config we do not understand;
                // refusing to parse it beats loading an arbitrary blob.
                var info = new FileInfo(path);
                if (info.Length > 4 * 1024 * 1024) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs))
                    doc = J.Parse(reader.ReadToEnd());
            }
            catch { return null; }
            object providers = J.Get(doc, "provider");
            if (providers == null) return null;
            foreach (string id in ProviderIds)
            {
                object entry = J.Get(providers, id);
                if (entry == null) continue;
                string key = J.Str(J.Get(J.Get(entry, "options"), "apiKey"));
                if (string.IsNullOrEmpty(key)) key = J.Str(J.Get(entry, "apiKey"));
                if (!string.IsNullOrEmpty(key) && key.Trim().Length > 0)
                {
                    providerId = id;
                    return key.Trim();
                }
            }
            return null;
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
                    using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                error = response != null
                    ? "HTTP " + (int)response.StatusCode
                    : ex.Status.ToString();
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
