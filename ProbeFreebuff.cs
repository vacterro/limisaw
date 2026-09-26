using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

// FreeBuff (freebuff.com / Codebuff) quota — OPTIONAL provider.
//
// Unlike the four core vendors, FreeBuff is not always present and is never
// assumed. It is shown only when it is POSITIVELY detected: an installed
// `freebuff` executable discovered through process/USER/MACHINE PATH or a
// desktop registration (App Paths / Start Menu), exactly like the other CLIs.
// A project-local `.freebuff` directory is NOT evidence of an installation and
// is never consulted.
//
// FreeBuff is also the only vendor whose balance lives in a CREDENTIAL-BEARING
// local file — `~/.config/manicode/credentials.json`, written by the FreeBuff
// CLI itself. Reading it is a permission LIMISAW does not have by default:
// `FreebuffReadConfig` in LIMISAW.ini is off until the user turns it on, and the
// switch must be DURABLE before any credential is read (a failed save means no
// read, no network, and the row says so). The token is used for one POST to the
// vendor's own usage endpoint and is never logged, stored or shown.
//
// FreeBucks is NOT a percentage. The usage endpoint returns an ABSOLUTE credit
// balance (`remainingBalance`) with no denominator, so it is carried as a
// BalanceData and never turned into a percent. `usage` and `remainingBalance`
// are independent numbers: `usage + remaining == quota` is an assumption the
// vendor never states, so it is never made.
namespace Limisaw
{
    static class FreebuffSource
    {
        public const string Homepage = "https://freebuff.com";

        // ONE authoritative usage destination (T-51 P1-3). Established from the
        // already-audited upstream client, not chosen: the installed FreeBuff
        // binary builds its API base URL from NEXT_PUBLIC_CODEBUFF_APP_URL
        // ("https://www.codebuff.com") and posts the credential to
        // `${that}/api/v1/usage`; NEXT_PUBLIC_FREEBUFF_APP_URL ("freebuff.com")
        // is only the browser app URL. A live probe agrees: codebuff answers
        // JSON 401 on /api/v1/usage while freebuff.com answers 404 HTML, so it
        // is not an endpoint at all. Trusted origins ONLY, hardcoded here and
        // never read from a config file: a config-supplied URL would turn "read
        // a token" into "send the token wherever this file says".
        public const string UsagePath = "/api/v1/usage";
        public const string UsageOrigin = "https://www.codebuff.com";

        // The production destination SET. `ConsentOrigins` is the same array,
        // so the permission copy (CredentialPermissionText) and the transport
        // read ONE owner and cannot drift apart again.
        static readonly string[] Origins = { UsageOrigin };

        public static string[] ConsentOrigins() { return (string[])Origins.Clone(); }

        // The FreeBucks balance this provider reports. `fingerprintId` is set on
        // the request body; the CLI's own default value is the literal string
        // below, which the vendor treats as an anonymous usage read.
        const string CliUsageFingerprint = "cli-usage";

        // Test seam: production reads USERPROFILE/HOME; a harness points the
        // credential path at a scratch profile.
        internal static Func<string> UserProfile = () =>
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME");

        public static string ConfigDir()
        {
            string profile = UserProfile();
            if (string.IsNullOrEmpty(profile)) return "";
            return Path.Combine(profile, ".config", "manicode");
        }

        public static string ConfigPath()
        {
            string dir = ConfigDir();
            return dir.Length == 0 ? "" : Path.Combine(dir, "credentials.json");
        }

        // Positive detection only: a discovered executable, nothing else. The
        // caller passes the result of FreebuffDiscovery.HasExecutable so this
        // class never touches PATH/registry itself.
        public static bool Installed(bool executableFound) { return executableFound; }

        public static bool ConfigExists()
        {
            string path = ConfigPath();
            return path.Length > 0 && File.Exists(path);
        }

        // ── credential read: the one field, behind the permission ────────────
        // The installed schema is `{ "default": { id, name, email, authToken,
        // fingerprintId, fingerprintHash }, "chatgptOAuth": {...} }`. Exactly
        // one field is read — the `default.authToken` — and it never leaves the
        // credential's sealed scope. Everything else in the file (email, name,
        // ids, the OAuth block) is ignored.
        public class Credential
        {
            public string Token = "";
            public string FingerprintId = "";
            public string State = "";   // "credential" / "config-missing" / "config-denied" / "no-token"
            public string Refusal;
        }

        // `allowConfig` is LIMISAW.ini's FreebuffReadConfig, threaded in rather
        // than read here so the decision has exactly one owner.
        //
        // T-51 P2: INSTALLATION and CREDENTIAL are separate facts. This class
        // only ever answers the CREDENTIAL question — whether an executable is
        // present is the caller's `executableFound`, never inferred here. So a
        // missing credential file under a closed permission is
        // "config-missing" (sign in), NOT "not-detected": claiming the provider
        // is absent while a proven executable exists is the contradiction this
        // fixes.
        public static Credential Resolve(bool allowConfig)
        {
            if (!allowConfig)
                return ConfigExists()
                    ? new Credential { State = "config-denied", Refusal = "FreeBuff credential access is off; enable it in Settings" }
                    : new Credential { State = "config-missing", Refusal = "No FreeBuff credential stored yet — sign in first" };
            string path = ConfigPath();
            if (path.Length == 0 || !File.Exists(path))
                return new Credential { State = "config-missing", Refusal = "FreeBuff credentials not found" };
            object doc;
            try
            {
                long bytesRead; string readError;
                string text = BoundedFile.ReadAllText(path, 4 * 1024 * 1024, out bytesRead, out readError);
                if (text == null) return new Credential { State = "config-invalid", Refusal = "FreeBuff credentials could not be read" };
                doc = J.Parse(text);
            }
            catch { return new Credential { State = "config-invalid", Refusal = "FreeBuff credentials could not be read" }; }
            if (doc == null) return new Credential { State = "config-invalid", Refusal = "FreeBuff credentials are not valid JSON" };
            object def = J.Get(doc, "default");
            if (def == null) return new Credential { State = "no-token", Refusal = "FreeBuff credentials carry no default account" };
            string token = (J.Str(J.Get(def, "authToken")) ?? "").Trim();
            string fingerprint = (J.Str(J.Get(def, "fingerprintId")) ?? "").Trim();
            if (token.Length == 0)
                return new Credential { State = "no-token", Refusal = "FreeBuff stored no readable auth token" };
            return new Credential { Token = token, FingerprintId = fingerprint, State = "credential" };
        }

        // Redacts the token from any string that could reach a card, a log or
        // an exception message. Every string leaving this file goes through it.
        public static string Redact(string text, string secret)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret)) return text;
            return text.Replace(secret, "<token>");
        }

        // ── probe ────────────────────────────────────────────────────────────
        // The request seam. Production is `Post`; a test substitutes a transport
        // so classification and bounded-read behaviour are exercised without a
        // socket. NOT a way to point LIMISAW at another URL: the origins stay
        // constants above.
        internal delegate string Poster(string url, string token, string fingerprintId, double deadline, out string error);

        internal static Poster Transport = Post;

        // The permission-dialog body (T-51 P1-3). Derived from `Origins` so the
        // exact origins the user is told about are the exact origins the
        // transport can contact; one origin to name, or every verified origin,
        // never a subset. `Homepage` is the vendor's own page, which is NOT
        // necessarily the API host, so it is never substituted here.
        public static string CredentialPermissionText()
        {
            string[] origins = ConsentOrigins();
            string destination = origins.Length == 1
                ? origins[0]
                : string.Join(" and ", origins);
            return "Allow LIMISAW to read FreeBuff's stored credential?\n\n"
                + "LIMISAW will read only the token needed to fetch your FreeBucks balance.\n"
                + "It will not display, log or store the token anywhere.\n"
                + "Requests go only to " + destination + ".\n"
                + "HTTPS only, redirects disabled \u2014 the destination is fixed in code and not configurable.\n"
                + "Permission can be disabled again in Settings.\n\nContinue?";
        }

        public static ProbeAccount Probe(double deadline, bool allowConfig, bool executableFound)
        {
            var acc = new ProbeAccount
            {
                Provider = "freebuff", ProviderLabel = "FreeBuff", Name = "FreeBuff",
            };
            Credential cred = Resolve(allowConfig);
            if (cred.Token.Length == 0)
            {
                // A permission LIMISAW owns is not a fault: quiet, muted card.
                acc.Status = Model.UNAVAILABLE;
                acc.Quiet = true;
                acc.Error = cred.Refusal;
                return acc;
            }

            string lastError = null;
            for (int i = 0; i < Origins.Length; i++)
            {
                double remaining = deadline - Stamp.Now;
                if (remaining <= 0.2) break;
                double attempt = Stamp.Now + remaining / (Origins.Length - i);
                string error;
                string body = Transport(Origins[i] + UsagePath, cred.Token,
                    cred.FingerprintId.Length > 0 ? cred.FingerprintId : CliUsageFingerprint,
                    attempt, out error);
                if (body == null) { lastError = error; continue; }
                double? balance; double? reset; List<KeyValuePair<string, double>> breakdown;
                if (!ParseBalance(body, out balance, out reset, out breakdown, out error)) { lastError = error; continue; }
                acc.Status = Model.OK;
                acc.Ok = true;
                acc.Balances.Add(new BalanceData
                {
                    Id = "freebucks",
                    Label = "FreeBucks",
                    Value = balance,
                    Unit = "credits",
                    ResetEpoch = reset,
                    Breakdown = breakdown,
                });
                return acc;
            }

            acc.Status = Model.ERROR;
            acc.Error = Redact(lastError ?? "FreeBuff usage could not be read", cred.Token);
            return acc;
        }

        // ── response parsing ─────────────────────────────────────────────────
        // The response carries an ABSOLUTE balance. `remainingBalance` is the
        // number (null stays null — a missing amount is never 0). `usage` is a
        // separate count and is NEVER combined with it. `next_quota_reset` is an
        // epoch or ISO instant. A breakdown is copied only for keys the vendor
        // actually sent.
        //
        // ABSOLUTE RULE: a null/missing remainingBalance produces a balance with
        // Value == null (visible as "unavailable"), never 0, and never a percent
        // derived from usage.
        public static bool ParseBalance(string body, out double? value, out double? reset,
            out List<KeyValuePair<string, double>> breakdown, out string error)
        {
            value = null; reset = null; breakdown = null; error = null;
            object doc = J.Parse(body);
            if (doc == null) { error = "FreeBuff usage did not return JSON"; return false; }
            // Keep the vendor's own balance object wherever it nests under the
            // response envelope.
            object data = J.Get(doc, "data") ?? doc;
            object errEnvelope = J.Get(doc, "error");
            if (errEnvelope != null)
            {
                string message = (J.Str(J.Get(doc, "message")) ?? J.Str(errEnvelope) ?? "").Trim();
                error = "FreeBuff usage: " + (message.Length > 0 ? message : "service error");
                return false;
            }
            object raw = J.Get(data, "remainingBalance");
            if (raw == null && data != doc) raw = J.Get(doc, "remainingBalance");
            double? num = J.Num(raw);
            // An explicit null remainingBalance is honest "unknown", not zero.
            if (raw != null) value = num;
            object resetRaw = J.Get(data, "next_quota_reset") ?? J.Get(doc, "next_quota_reset");
            reset = Stamp.Epoch(resetRaw);
            object bd = J.Get(data, "balanceBreakdown") ?? J.Get(doc, "balanceBreakdown");
            var map = J.Obj(bd);
            if (map != null)
            {
                breakdown = new List<KeyValuePair<string, double>>();
                foreach (KeyValuePair<string, object> entry in map)
                {
                    double? v = J.Num(entry.Value);
                    if (v.HasValue) breakdown.Add(new KeyValuePair<string, double>(entry.Key, v.Value));
                }
                if (breakdown.Count == 0) breakdown = null;
            }
            return true;
        }

        static bool TlsReady;

        // TLS 1.2+ explicitly: the .NET Framework 4.x default (SSL3/TLS 1.0) is
        // refused by current hosts. Same fix as Zcode, ADDED to the process
        // policy so it never downgrades what someone else set.
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
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
                catch { }
            }
        }

        internal const int MaxResponseBytes = 1024 * 1024;

        // The bounded reader: returns the decoded body only when it fits the cap
        // and the absolute deadline; otherwise null with a machine-readable
        // error. A declared length past the cap is refused before any read.
        internal static string ReadBoundedResponse(Stream stream, long contentLength, int maxBytes,
            double absoluteDeadline, out string error)
        {
            error = null;
            if (contentLength > maxBytes) { error = "response_too_large"; return null; }
            var acc = new System.IO.MemoryStream();
            var buf = new byte[8192];
            while (true)
            {
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
            try { return Encoding.UTF8.GetString(acc.ToArray()); }
            catch (Exception ex) { error = ex.GetType().Name; return null; }
        }

        // One POST of a small JSON body. No redirects (a 3xx to another origin
        // would forward the token somewhere the user never authorised), TLS
        // only, no token in the error text.
        static string Post(string url, string token, string fingerprintId, double deadline, out string error)
        {
            error = null;
            double remaining = deadline - Stamp.Now;
            if (remaining <= 0.2) { error = "deadline_exceeded"; return null; }
            EnsureTls();
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = (int)Math.Max(500, Math.Min(remaining, 30.0) * 1000);
                request.ReadWriteTimeout = request.Timeout;
                request.UserAgent = "LIMISAW";
                request.Accept = "application/json";
                request.AllowAutoRedirect = false;
                // The token rides the body, exactly as the installed client
                // sends it; no Authorization header is added.
                string payload = "{\"fingerprintId\":\"" + JsonEscape(fingerprintId)
                    + "\",\"authToken\":\"" + JsonEscape(token) + "\"}";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                request.ContentLength = bytes.Length;
                using (Stream req = request.GetRequestStream())
                    req.Write(bytes, 0, bytes.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                {
                    if ((int)response.StatusCode >= 300)
                    { error = "HTTP " + (int)response.StatusCode; return null; }
                    if (stream == null) { error = "empty response"; return null; }
                    return ReadBoundedResponse(stream, response.ContentLength, MaxResponseBytes, deadline, out error);
                }
            }
            catch (WebException ex)
            {
                // The error response holds a pooled connection; read the status
                // and dispose it rather than leaking a lease per refresh.
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

        // Minimal JSON string escaping for the two request-body fields. No
        // external dependency, and a token with quotes/backslashes cannot break
        // the envelope.
        static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // Failure classification for the connection card, mirroring Zcode's
        // shape so the same states/actions apply. Never recommends a reinstall
        // for a credential failure.
        public static ConnectionState Classify(string error, out ConnectionErrorCode code, out ConnectionAction action)
        {
            string err = (error ?? "").ToLowerInvariant();
            if (err.Contains("401") || err.Contains("403") || err.Contains("unauthorized")
                || (err.Contains("invalid") && err.Contains("token")))
            { code = ConnectionErrorCode.CredentialRejected; action = ConnectionAction.OpenVendor; return ConnectionState.Failed; }
            if (err.Contains("timeout") || err.Contains("deadline"))
            { code = ConnectionErrorCode.NetworkTimeout; action = ConnectionAction.CheckAgain; return ConnectionState.Failed; }
            if (err.Contains("tls") || err.Contains("ssl") || err.Contains("securechannel"))
            { code = ConnectionErrorCode.TlsFailed; action = ConnectionAction.CheckAgain; return ConnectionState.Failed; }
            if (err.Contains("response_too_large"))
            { code = ConnectionErrorCode.ResponseTooLarge; action = ConnectionAction.Troubleshoot; return ConnectionState.Failed; }
            if (err.Contains("did not return json") || err.Contains("protocol") || err.Contains("no readable"))
            { code = ConnectionErrorCode.ProtocolChanged; action = ConnectionAction.Troubleshoot; return ConnectionState.Failed; }
            code = ConnectionErrorCode.ServiceUnavailable; action = ConnectionAction.CheckAgain; return ConnectionState.Failed;
        }
    }

    // ── FreeBuff connection adapter ──────────────────────────────────────────
    // Level-0 projection, verification and login, mirroring ZcodeConnectionAdapter
    // so the existing Connections surface drives it unchanged. Every refusal
    // names its actual situation, and a credential failure never recommends a
    // reinstall.
    static class FreebuffConnectionAdapter
    {
        public static VendorConnection Discover()
        {
            return ExecutableDiscovery.BuildConnection("freebuff");
        }

        // Level-0 only: presence, the permission state and the credential
        // category (never a value). No network.
        public static VendorConnection BuildLevel0(bool allowConfig, bool executableFound)
        {
            var vc = new VendorConnection { VendorId = "freebuff", Stage = ConnectionStage.Discovery };
            vc.ConfigAccess = allowConfig;
            if (!executableFound)
            {
                vc.Installed = false;
                vc.State = ConnectionState.NotInstalled;
                vc.ErrorCode = ConnectionErrorCode.CliMissing;
                vc.RecommendedAction = ConnectionAction.None;
                vc.Reason = "FreeBuff not detected";
                return vc;
            }
            vc.Installed = true;
            vc.ConfigPresent = FreebuffSource.ConfigExists();
            FreebuffSource.Credential cred = FreebuffSource.Resolve(allowConfig);
            switch (cred.State)
            {
                case "credential":
                    vc.State = ConnectionState.Verifying;
                    vc.ErrorCode = ConnectionErrorCode.None;
                    vc.Reason = "FreeBuff credential available";
                    vc.CredentialOrigin = "config";
                    break;
                case "config-denied":
                    vc.State = ConnectionState.PermissionRequired;
                    vc.ErrorCode = ConnectionErrorCode.PermissionRequired;
                    vc.RecommendedAction = ConnectionAction.AllowAndConnect;
                    vc.Reason = "LIMISAW found FreeBuff's stored credential but does not read it without permission.";
                    vc.UserActionRequired = true;
                    vc.CredentialOrigin = "none";
                    break;
                case "no-token":
                    vc.State = ConnectionState.SignInRequired;
                    vc.ErrorCode = ConnectionErrorCode.CredentialMissing;
                    vc.RecommendedAction = ConnectionAction.Connect;
                    vc.Reason = cred.Refusal ?? "FreeBuff has no stored credential";
                    vc.CredentialOrigin = "none";
                    break;
                default: // config-missing / config-invalid
                    vc.State = ConnectionState.SignInRequired;
                    vc.ErrorCode = ConnectionErrorCode.CredentialMissing;
                    vc.RecommendedAction = ConnectionAction.Connect;
                    vc.Reason = cred.Refusal ?? "FreeBuff sign-in required";
                    vc.CredentialOrigin = "none";
                    break;
            }
            return vc;
        }

        // T-51 P1-2: the FIRST-TIME interactive sign-in contract. During an
        // ACTIVE sign-in generation the vendor login is running in its own
        // window; a credential that is not there YET is STILL WAITING, not the
        // terminal SignInRequired — otherwise the watcher stops before the user
        // can finish. Returns null to keep waiting.
        //
        // The narrowest integration: this is a `customVerify` body, so the
        // existing watcher/cadence/generation model is reused unchanged.
        public static VendorConnection VerifySignIn(bool allowConfig, bool executableFound)
        {
            if (!allowConfig)
            {
                // Permission off. A stored credential means the user must GRANT,
                // not sign in again: PermissionRequired, never a second login
                // and never a silent token read.
                if (FreebuffSource.ConfigExists())
                    return Verify(BuildLevel0(false, executableFound), false, executableFound);
                // No credential yet: the login is still in flight. WAIT.
                return null;
            }
            FreebuffSource.Credential cred = FreebuffSource.Resolve(true);
            // A missing or half-written (mid-login) credential file is STILL
            // WAITING. Only a parsed file that genuinely carries no usable
            // token, or a real credential, is handed to the authoritative
            // Verify below (terminal SignInRequired or Connected).
            if (cred.State == "config-missing" || cred.State == "config-invalid") return null;
            return Verify(BuildLevel0(true, executableFound), true, executableFound);
        }

        // One read-only verification: resolve the token under the permission,
        // POST the usage endpoint, classify. `baseConn` is the Level-0 row.
        public static VendorConnection Verify(VendorConnection baseConn, bool allowConfig, bool executableFound)
        {
            VendorConnection vc = baseConn ?? BuildLevel0(allowConfig, executableFound);
            vc.ConfigAccess = allowConfig;
            FreebuffSource.Credential cred = FreebuffSource.Resolve(allowConfig);
            if (cred.Token.Length == 0)
            {
                if (cred.State == "config-denied")
                {
                    vc.State = ConnectionState.PermissionRequired;
                    vc.ErrorCode = ConnectionErrorCode.PermissionRequired;
                    vc.RecommendedAction = ConnectionAction.AllowAndConnect;
                    vc.Reason = cred.Refusal;
                    vc.CredentialOrigin = "none";
                    vc.Stage = ConnectionStage.Authentication;
                    return vc;
                }
                vc.State = ConnectionState.SignInRequired;
                vc.ErrorCode = ConnectionErrorCode.CredentialMissing;
                vc.RecommendedAction = ConnectionAction.Connect;
                vc.Reason = cred.Refusal ?? "FreeBuff sign-in required";
                vc.CredentialOrigin = "none";
                vc.Stage = ConnectionStage.Authentication;
                return vc;
            }
            vc.CredentialOrigin = "config";
            double deadline = Stamp.Now + 12;
            ProbeAccount acc = FreebuffSource.Probe(deadline, allowConfig, executableFound);
            double? balance = null;
            foreach (BalanceData b in acc.Balances) if (b.Value.HasValue) balance = b.Value;
            if (acc.Ok)
            {
                vc.State = ConnectionState.Connected;
                vc.ErrorCode = ConnectionErrorCode.None;
                vc.Authenticated = true; vc.AuthKnown = true;
                vc.Monitorable = true; vc.VerificationOk = true;
                vc.ConnectivityOk = true; vc.ConnectivityKnown = true;
                vc.LastVerifiedUtc = Stamp.Now;
                vc.RecommendedAction = ConnectionAction.None;
                vc.Reason = balance.HasValue ? "Connected" : "Connected · balance unavailable";
                vc.Stage = ConnectionStage.Quota;
                return vc;
            }
            vc.Authenticated = true; vc.AuthKnown = true; // a credential was resolved and sent
            ConnectionErrorCode code; ConnectionAction action;
            ConnectionState state = FreebuffSource.Classify(acc.Error, out code, out action);
            vc.State = state;
            vc.ErrorCode = code;
            vc.RecommendedAction = action;
            vc.Reason = FreebuffSource.Redact(acc.Error, cred.Token);
            vc.ConnectivityKnown = code != ConnectionErrorCode.CredentialRejected;
            vc.ConnectivityOk = vc.ConnectivityKnown;
            vc.Stage = code == ConnectionErrorCode.CredentialRejected
                ? ConnectionStage.Authentication : ConnectionStage.Connectivity;
            return vc;
        }

        // The grant becomes authoritative only when it is durable: a failed
        // save leaves access OFF and reports why, never a silent half-state.
        public static bool TryAllowAndConnect(LimisawSettings settings, bool userConfirmed, out string note)
        {
            note = "";
            if (!userConfirmed) { note = "Cancelled"; return false; }
            bool prev = settings.FreebuffReadConfig;
            settings.FreebuffReadConfig = true;
            var r = settings.SaveSettings();
            if (!r.Saved)
            {
                settings.FreebuffReadConfig = prev;
                note = "Permission was not saved — " + r.Reason;
                return false;
            }
            note = "Permission saved — verifying";
            return true;
        }

        // The vendor-owned visible login (no in-app OAuth, no stdout capture).
        public static bool TryOpenVendor(out string note)
        {
            note = "";
            if (FreebuffDiscovery.LaunchLogin(out note)) return true;
            try
            {
                var url = new ProcessStartInfo(FreebuffSource.Homepage) { UseShellExecute = true };
                Process.Start(url);
                note = "Opened " + FreebuffSource.Homepage;
                return true;
            }
            catch (Exception ex) { note = ex.GetType().Name; return false; }
        }
    }

    // Positive FreeBuff executable discovery. Reuses ExecutableDiscovery's
    // candidate walk (process/USER/MACHINE PATH) plus the same App Paths / Start
    // Menu launcher resolution Zcode uses. A project-local `.freebuff` is NEVER
    // consulted: it is not an installation.
    internal static class FreebuffDiscovery
    {
        public const string BinaryName = "freebuff";

        // Test seams, mirroring ZcodeLauncherDiscovery: production reads the
        // real filesystem and registry, a harness drives both without touching
        // this machine.
        internal static Func<string, bool> FileExists = path => File.Exists(path);
        internal static Func<string> AppPathsTarget = ReadAppPathsTarget;
        internal static Func<string, string> ResolveLnk = ZcodeLauncherDiscovery.ResolveLnk;
        static readonly object DesktopTargetGate = new object();
        static string CachedDesktopTarget;

        // Test seam for isolating positive-cache lifetime cases.
        internal static void ResetDesktopTargetCacheForTests()
        { lock (DesktopTargetGate) CachedDesktopTarget = null; }

        static string CacheDesktopTarget(string target)
        {
            lock (DesktopTargetGate) CachedDesktopTarget = target;
            return target;
        }
        // The one "is it installed" seam. Production reads the real filesystem
        // and registry; a harness drives both without touching this machine.
        internal static Func<bool> HasExecutableImpl = DefaultHasExecutable;

        // The candidate list: the discovered executable paths (may be empty).
        public static List<string> Candidates()
        {
            // DiscoverFresh walks process PATH + USER/MACHINE PATH for
            // freebuff.exe/.cmd/.bat using the shared counting seams.
            return ExecutableDiscovery.DiscoverFresh(BinaryName);
        }

        // Positively present at all: an executable on PATH, or a desktop
        // registration (App Paths / Start Menu) resolving to an existing file.
        public static bool HasExecutable() { return HasExecutableImpl(); }

        static bool DefaultHasExecutable()
        {
            if (Candidates().Count > 0) return true;
            return !string.IsNullOrEmpty(DesktopLauncherTarget());
        }

        // Test seam: production reads the real Start Menu; a harness points it
        // at a scratch tree so no real .lnk is ever consulted.
        internal static Func<string[]> StartMenuDirs = () => new[]
        {
            @"%APPDATA%\Microsoft\Windows\Start Menu\Programs",
            @"%ProgramData%\Microsoft\Windows\Start Menu\Programs",
        };

        // App Paths then Start Menu, resolving a .lnk through the shell so the
        // target can be PROVEN to exist. Returns the target path or null.
        public static string DesktopLauncherTarget()
        {
            string cached;
            lock (DesktopTargetGate) cached = CachedDesktopTarget;
            if (!string.IsNullOrEmpty(cached))
            {
                bool exists = false;
                try { exists = FileExists != null && FileExists(cached); } catch { }
                if (exists) return cached;
                lock (DesktopTargetGate)
                    if (CachedDesktopTarget == cached) CachedDesktopTarget = null;
            }

            string app = AppPathsTarget != null ? AppPathsTarget() : null;
            if (!string.IsNullOrEmpty(app))
            {
                bool exists = false;
                try { exists = FileExists != null && FileExists(app); } catch { }
                if (exists) return CacheDesktopTarget(app);
            }
            foreach (string raw in (StartMenuDirs != null ? StartMenuDirs() : new string[0]))
            {
                string dir;
                try { dir = Environment.ExpandEnvironmentVariables(raw); } catch { continue; }
                if (!Directory.Exists(dir)) continue;
                try
                {
                    // Enumerate lazily and stop on the first matching shortcut
                    // whose resolved target still exists.
                    foreach (string link in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileName(link).IndexOf("freebuff", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string target = ResolveLnk != null ? ResolveLnk(link) : ZcodeLauncherDiscovery.ResolveLnk(link);
                        if (!string.IsNullOrEmpty(target) && FileExists != null && FileExists(target))
                            return CacheDesktopTarget(target);
                    }
                }
                catch { }
            }
            return null;
        }

        // The App Paths lookup walks HKCU then HKLM and returns the first value
        // whose target still EXISTS. A per-user uninstall leaves the HKCU value
        // behind pointing at a removed file, and returning it unchecked hid the
        // machine-wide entry forever. The hive read is a seam so that exact
        // shape is testable without a registry.
        internal static Func<int, string, string> HiveAppPathValue = ReadHiveAppPathValue;
        static string ReadHiveAppPathValue(int hiveIndex, string leaf)
        {
            Microsoft.Win32.RegistryKey hive = hiveIndex == 0
                ? Microsoft.Win32.Registry.CurrentUser
                : Microsoft.Win32.Registry.LocalMachine;
            try
            {
                using (var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + leaf, false))
                {
                    if (k == null) return null;
                    return (k.GetValue(null) ?? k.GetValue("Path")) as string;
                }
            }
            catch { return null; }
        }

        static string ReadAppPathsTarget()
        {
            string leaf = BinaryName + ".exe";
            for (int hive = 0; hive < 2; hive++)
            {
                string raw = HiveAppPathValue != null ? HiveAppPathValue(hive, leaf) : null;
                if (string.IsNullOrEmpty(raw)) continue;
                string v = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                // A value that no longer resolves is stale, not an answer: the
                // next hive still gets its turn.
                if (FileExists != null && !FileExists(v)) continue;
                // DesktopLauncherTarget keeps the single positive-cache boundary
                // for whatever this returns.
                return v;
            }
            return null;
        }

        // The resolved executable for launching the vendor's own login, or "".
        public static string Resolve() { return ResolveImpl(); }

        internal static Func<string> ResolveImpl = DefaultResolve;

        static string DefaultResolve()
        {
            List<string> cands = Candidates();
            if (cands.Count > 0) return cands[0];
            string target = DesktopLauncherTarget();
            return target ?? "";
        }

        // The vendor-owned VISIBLE login. FreeBuff's CLI performs its own
        // browser sign-in; LIMISAW launches it interactively and never captures
        // stdout, never parses an OAuth response and never kills the child.
        // Returns false when no executable was discovered.
        public static bool LaunchLogin(out string error)
        {
            error = null;
            string exe = Resolve();
            if (string.IsNullOrEmpty(exe)) { error = "FreeBuff executable not found"; return false; }
            ConnectionProcessLauncher.InteractiveLaunch launch;
            error = null;
            launch = ConnectionProcessLauncher.StartInteractive(exe, "login", null, out error);
            return launch != null && error == null;
        }
    }
}
