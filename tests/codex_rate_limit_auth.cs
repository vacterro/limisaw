using System;
using System.Collections.Generic;
using System.IO;
using Limisaw;

// SRC-028: a Codex rate-limit read refused with HTTP 401 Unauthorized is an
// AUTHENTICATION failure, not "authenticated quota unavailable".
//
// The observed provider error is a JSON-RPC error object whose OUTER code is
// -32603 and whose nested message reports the rate-limit fetch failing with
// HTTP 401 Unauthorized. -32603 is only a wrapper; it is emitted for
// quota-unavailable, transient and auth failures alike, so the classification
// below reads the nested text and NEVER the outer code alone.
//
// Everything here is deterministic and offline: the app-server is the
// CodexSource.StartSession seam, no real `codex` runs, no browser opens, no
// credential is written, read back or logged.
//
// Covered:
//   R001 the exact observed 401 family classifies as auth, and -32603 alone does not
//   R002 a 401 sweep keeps history and is never a fresh reading or a fresh 0%
//   R004 the Connection card projects SignInRequired / AuthRejected / Connect
//   R005 a successful re-read after an auth revision change restores Connected
//   R006 a non-auth structured error stays ConnectedQuotaUnavailable
//   R007 no restart storm, no credential text in any diagnostic
//
// The tray side of R003 (Any available must not select an auth-rejected carried
// reading) lives in tests/tray_render_modes.cs, where the form fixture is.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class CodexRateLimitAuthTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    // ── the verbatim observed shapes ────────────────────────────────────────
    const string Observed401 =
        "{\"code\":-32603,\"message\":\"failed to fetch codex rate limits: request to "
        + "https://chatgpt.com/backend-api/wham/usage failed with status 401 Unauthorized\"}";

    // The same wrapper with NO auth marker in the nested text. This is the
    // control that must NOT be read as an auth failure.
    const string Outer32603Only =
        "{\"code\":-32603,\"message\":\"failed to fetch codex rate limits: internal error\"}";

    const string ExpiredAuth =
        "{\"code\":-32603,\"message\":\"failed to fetch codex rate limits: 401 Unauthorized - "
        + "your access token has expired\"}";

    const string QuotaNotExposed =
        "{\"code\":-32603,\"message\":\"authenticated, subscription quota not exposed\"}";

    const string NetworkHiccup =
        "{\"code\":-32603,\"message\":\"failed to fetch codex rate limits: connection reset by peer\"}";

    // A 401 whose message also carries a bearer token: the sanitizer must strip
    // it before ANY consumer can see it.
    const string WithToken =
        "{\"code\":-32603,\"message\":\"failed to fetch codex rate limits: 401 Unauthorized (bearer "
        + "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJVadQssw5c)\"}";

    const string JwtFragment = "eyJhbGciOiJIUzI1NiJ9";

    static string profile;
    static string home;
    static int sessionStarts;

    // Empty => a healthy quota result. Otherwise the app-server answers the
    // rate-limit read with this error object.
    static string sessionError = "";

    static CodexSource.RpcLink FakeSession()
    {
        sessionStarts++;
        var link = new CodexSource.RpcLink();
        link.Call = (method, parameters, deadline) =>
        {
            if (method == "initialize")
                return new Dictionary<string, object> { { "result", new Dictionary<string, object>() } };
            if (method == "account/rateLimits/read")
            {
                if (sessionError.Length == 0)
                    return new Dictionary<string, object> { { "result", Quota("plus", 300, 20.0, 10080, 55.0) } };
                return new Dictionary<string, object> { { "error", J.Parse(sessionError) } };
            }
            return null;
        };
        link.Notify = (m, p) => { };
        link.Alive = () => true;
        link.Drop = () => { };
        return link;
    }

    static object Quota(string plan, int fiveHMin, double fiveHUsed, int weekMin, double weekUsed)
    {
        return new Dictionary<string, object>
        {
            { "rateLimits", new Dictionary<string, object>
                {
                    { "planType", plan },
                    { "primary", new Dictionary<string, object>
                        { { "windowDurationMins", (double)fiveHMin }, { "usedPercent", fiveHUsed },
                          { "resetsAt", 9999999999999.0 } } },
                    { "secondary", new Dictionary<string, object>
                        { { "windowDurationMins", (double)weekMin }, { "usedPercent", weekUsed },
                          { "resetsAt", 9999999999999.0 } } },
                } },
        };
    }

    static CodexSource.RateLimitFailure Classify(string json)
    {
        return CodexSource.ClassifyRateLimitError(json == null ? null : J.Parse(json));
    }

    public static int Main()
    {
        string savedProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        string savedHome = Environment.GetEnvironmentVariable("HOME");
        string savedCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var savedResolveExe = CodexSource.ResolveExe;
        var savedStartSession = CodexSource.StartSession;
        profile = Path.Combine(Path.GetTempPath(), "limisaw_src028_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        home = Path.Combine(profile, ".codex");
        Directory.CreateDirectory(home);
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", profile);
            Environment.SetEnvironmentVariable("HOME", profile);
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            CodexSource.ResolveExe = _ => "codex.cmd";
            CodexSource.StartSession = (exe, h) => FakeSession();

            // ── R001: the classifier ────────────────────────────────────────
            Console.WriteLine("== R001: classification of the observed 401 family ==");
            Check("R001a. the exact observed -32603 + 401 Unauthorized payload is AuthRejected",
                Classify(Observed401) == CodexSource.RateLimitFailure.AuthRejected,
                Classify(Observed401).ToString());
            Check("R001b. explicit expiry evidence selects AuthExpired",
                Classify(ExpiredAuth) == CodexSource.RateLimitFailure.AuthExpired,
                Classify(ExpiredAuth).ToString());
            Check("R001c. the outer -32603 ALONE is never sufficient auth evidence",
                Classify(Outer32603Only) != CodexSource.RateLimitFailure.AuthRejected
                && Classify(Outer32603Only) != CodexSource.RateLimitFailure.AuthExpired,
                Classify(Outer32603Only).ToString());
            Check("R001d. the auth reason names the transport status it actually saw",
                CodexSource.AuthFailureReason(CodexSource.RateLimitFailure.AuthRejected, J.Parse(Observed401))
                    == "authentication required (401 Unauthorized)",
                CodexSource.AuthFailureReason(CodexSource.RateLimitFailure.AuthRejected, J.Parse(Observed401)));
            Check("R001e. an auth marker with no HTTP status still produces a truthful reason",
                CodexSource.AuthFailureReason(CodexSource.RateLimitFailure.AuthRejected,
                    J.Parse("{\"message\":\"unauthenticated\"}")) == "authentication required",
                CodexSource.AuthFailureReason(CodexSource.RateLimitFailure.AuthRejected,
                    J.Parse("{\"message\":\"unauthenticated\"}")));
            Check("R006a. a non-auth structured error is QuotaUnavailable, never auth",
                Classify(QuotaNotExposed) == CodexSource.RateLimitFailure.QuotaUnavailable,
                Classify(QuotaNotExposed).ToString());
            Check("R006b. a network hiccup is Transient, never auth",
                Classify(NetworkHiccup) == CodexSource.RateLimitFailure.Transient,
                Classify(NetworkHiccup).ToString());
            Check("R006c. an unrecognized shape is Protocol, never guessed into a stronger verdict",
                Classify("{\"code\":-1,\"message\":\"zorp\"}") == CodexSource.RateLimitFailure.Protocol,
                Classify("{\"code\":-1,\"message\":\"zorp\"}").ToString());
            Check("R006d. no structured error at all is None",
                CodexSource.ClassifyRateLimitError(null) == CodexSource.RateLimitFailure.None, "");
            Check("R007a. the sanitizer strips a bearer/JWT credential from the error text",
                CodexSource.SanitizeErrorText(J.Write(J.Parse(WithToken)))
                    .IndexOf(JwtFragment, StringComparison.Ordinal) < 0,
                CodexSource.SanitizeErrorText(J.Write(J.Parse(WithToken))));
            Check("R007b. sanitizing keeps the actionable half of the message",
                CodexSource.SanitizeErrorText(J.Write(J.Parse(WithToken)))
                    .IndexOf("401 Unauthorized", StringComparison.Ordinal) >= 0,
                CodexSource.SanitizeErrorText(J.Write(J.Parse(WithToken))));
            Check("R007c. the credential-bearing message is STILL an auth rejection after sanitizing",
                Classify(WithToken) == CodexSource.RateLimitFailure.AuthRejected,
                Classify(WithToken).ToString());

            // ── R004: the Connection card ───────────────────────────────────
            Console.WriteLine("== R004: the Connections card projects the auth truth ==");
            sessionError = Observed401;
            CodexSource.Pool.Reset();
            var verify = CodexConnectionAdapter.VerifyHome(home, Stamp.Now + 20);
            Check("R004a. a 401 rate-limit read is SignInRequired, never ConnectedQuotaUnavailable",
                verify.State == ConnectionState.SignInRequired
                && verify.State != ConnectionState.ConnectedQuotaUnavailable,
                verify.State.ToString());
            Check("R004b. Authenticated is false and the vendor is not monitorable",
                !verify.Authenticated && !verify.Monitorable,
                "authenticated=" + verify.Authenticated + " monitorable=" + verify.Monitorable);
            Check("R004c. the error is AuthRejected, not QuotaNotAvailable",
                verify.Error == ConnectionErrorCode.AuthRejected, verify.Error.ToString());
            Check("R004d. the recommended action resolves to Connect",
                ConnectionErrorPriority.RecommendedAction(verify.Error, verify.State) == ConnectionAction.Connect,
                ConnectionErrorPriority.RecommendedAction(verify.Error, verify.State).ToString());
            Check("R004e. the reason is the sanitized actionable 401 sentence",
                verify.Reason.IndexOf("401", StringComparison.Ordinal) >= 0
                && verify.Reason.IndexOf("authentication required", StringComparison.Ordinal) >= 0,
                verify.Reason);
            Check("R007d. no credential or token text reaches the card",
                verify.Reason.IndexOf(JwtFragment, StringComparison.Ordinal) < 0
                && verify.Reason.IndexOf("bearer", StringComparison.OrdinalIgnoreCase) < 0,
                verify.Reason);

            sessionError = ExpiredAuth;
            CodexSource.Pool.Reset();
            var expired = CodexConnectionAdapter.VerifyHome(home, Stamp.Now + 20);
            Check("R004f. explicit expiry evidence projects AuthExpired with the same sign-in action",
                expired.Error == ConnectionErrorCode.AuthExpired
                && expired.State == ConnectionState.SignInRequired
                && ConnectionErrorPriority.RecommendedAction(expired.Error, expired.State) == ConnectionAction.Connect,
                expired.State + "/" + expired.Error);

            sessionError = QuotaNotExposed;
            CodexSource.Pool.Reset();
            var generic = CodexConnectionAdapter.VerifyHome(home, Stamp.Now + 20);
            Check("R006e. a non-auth structured error still projects ConnectedQuotaUnavailable",
                generic.State == ConnectionState.ConnectedQuotaUnavailable
                && generic.Error == ConnectionErrorCode.QuotaNotAvailable
                && generic.Authenticated,
                generic.State + "/" + generic.Error + "/auth=" + generic.Authenticated);

            // ── R007: repeated 401 must not restart-storm ──────────────────
            sessionError = Observed401;
            CodexSource.Pool.Reset();
            sessionStarts = 0;
            for (int i = 0; i < 3; i++) CodexConnectionAdapter.VerifyHome(home, Stamp.Now + 20);
            Check("R007e. three consecutive 401 verifications start the app-server ONCE, not three times",
                sessionStarts == 1, sessionStarts + " session start(s)");

            // ── R005: recovery after reauthentication ──────────────────────
            Console.WriteLine("== R005: reauthentication restores a fresh quota ==");
            string authFile = Path.Combine(home, "auth.json");
            File.WriteAllText(authFile, "{\"tok\":\"rev-one\"}");
            CodexSource.Pool.Reset();
            CodexSource.RefreshAuthSession(home);
            sessionError = "";
            sessionStarts = 0;
            var good = CodexConnectionAdapter.VerifyHome(home, Stamp.Now + 20);
            Check("R005a. a successful read restores Connected with a fresh monitorable quota",
                good.State == ConnectionState.Connected && good.Authenticated && good.Monitorable,
                good.State + "/auth=" + good.Authenticated);
            // The user signs in again: auth.json changes, the existing revision
            // mechanism evicts the pooled session, and the next read is fresh.
            int before = sessionStarts;
            File.WriteAllText(authFile, "{\"tok\":\"rev-two-after-reauthentication\"}");
            CodexSource.RefreshAuthSession(home);
            var recovered = CodexConnectionAdapter.VerifyHome(home, Stamp.Now + 20);
            Check("R005b. an auth revision change evicts the pooled session (a real re-read, no cache hit)",
                sessionStarts > before, before + " -> " + sessionStarts);
            Check("R005c. the re-read after reauthentication is Connected again",
                recovered.State == ConnectionState.Connected
                && recovered.Error == ConnectionErrorCode.None
                && recovered.Authenticated,
                recovered.State + "/" + recovered.Error);

            // ── R002: the periodic probe keeps history, never a fresh 0% ───
            Console.WriteLine("== R002: the 401 sweep keeps stale history, not a fresh 0% ==");
            sessionError = Observed401;
            CodexSource.Pool.Reset();
            var probed = CodexSource.Sweep(Stamp.Now + 120, 35);
            Check("R002a. the sweep produced exactly one slot for the one discovered home",
                probed.Count == 1, probed.Count.ToString());
            Check("R002b. the slot is NOT Ok and carries typed auth provenance",
                probed.Count == 1 && !probed[0].Ok && probed[0].AuthFailed
                && probed[0].AuthFailureClass == CodexSource.RateLimitFailure.AuthRejected,
                probed.Count == 0 ? "no slot"
                    : "ok=" + probed[0].Ok + " authFailed=" + probed[0].AuthFailed);
            Check("R002c. the probe's own text is the sanitized auth reason, not a raw JSON blob",
                probed.Count == 1
                && probed[0].Error.IndexOf("authentication required", StringComparison.Ordinal) >= 0
                && probed[0].Error.IndexOf("401", StringComparison.Ordinal) >= 0
                && probed[0].Error.IndexOf("{", StringComparison.Ordinal) < 0,
                probed.Count == 0 ? "no slot" : probed[0].Error);
            Check("R007f. the probe's own text leaks no credential",
                probed.Count == 1 && probed[0].Error.IndexOf(JwtFragment, StringComparison.Ordinal) < 0, "");
            var flat = Model.Flatten(probed[0], Stamp.Now);
            Check("R002d. the flattened account is not a fresh reading",
                !flat.Ok && !flat.HasReading && flat.AuthFailed,
                "ok=" + flat.Ok + " hasReading=" + flat.HasReading + " authFailed=" + flat.AuthFailed);
            bool anyFreshZero = false;
            foreach (WindowData w in flat.Windows) if (w.Available) anyFreshZero = true;
            Check("R002e. the failure is never rewritten as a fresh 0% reading",
                !anyFreshZero && flat.Windows.Count > 0,
                flat.Windows.Count + " window(s), any available=" + anyFreshZero);
            Check("R002f. typing survives the flatten boundary",
                flat.AuthFailedClass == CodexSource.RateLimitFailure.AuthRejected
                && flat.AuthFailureReason.IndexOf("401", StringComparison.Ordinal) >= 0,
                flat.AuthFailureReason);

            Console.WriteLine("");
            if (fails == 0) Console.WriteLine("PASS (" + checks + " checks, 0 failures)");
            else Console.WriteLine("FAILED (" + fails + " of " + checks + " checks)");
            return fails == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  harness");
            Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            CodexSource.Pool.Reset();
            CodexSource.ResolveExe = savedResolveExe;
            CodexSource.StartSession = savedStartSession;
            Environment.SetEnvironmentVariable("USERPROFILE", savedProfile);
            Environment.SetEnvironmentVariable("HOME", savedHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", savedCodexHome);
            try { Directory.Delete(profile, true); } catch { }
        }
    }
}
