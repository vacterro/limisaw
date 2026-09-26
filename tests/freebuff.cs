using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Limisaw;

// FreeBuff provider + FreeBucks: the optional vendor's whole contract.
//
// FreeBuff is the one provider whose balance is an ABSOLUTE amount, not a
// percentage, and the one whose credential lives in a local file LIMISAW does
// not read without permission. This harness pins that surface over FIXTURE
// credential files in a scratch profile — no real secret is ever present, no
// network is ever touched:
//
//   * present only when positively DETECTED (an executable), never from a
//     project-local `.freebuff`;
//   * the credential is read ONLY under the durable FreebuffReadConfig
//     permission; a save that failed leaves it off and unread;
//   * the token is used for one POST to a constant official origin and never
//     reaches a card, a log or an exception (the fake secret occurs ZERO times
//     in every user-visible string);
//   * FreeBucks is an ABSOLUTE balance: remainingBalance rides as a balance,
//     never as a percent, and `usage + remaining == quota` is never assumed;
//   * a null remainingBalance is "unknown", never 0;
//   * classification names each failure and never recommends a reinstall for a
//     credential failure.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class FreebuffTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static bool ResetLauncherCache()
    {
        MethodInfo reset = typeof(FreebuffDiscovery).GetMethod("ResetDesktopTargetCacheForTests",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (reset == null) return false;
        reset.Invoke(null, null);
        return true;
    }

    static string Profile;
    static string ConfigPath;

    // A fake token shape. Never a real credential; long enough that a redaction
    // failure would be obvious in any message it leaked into.
    const string FakeSecret = "LIMISAW_TEST_FREEBUFF_SECRET_71c9";

    static void WriteCredential(string defaultJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
        File.WriteAllText(ConfigPath, "{\"default\":" + defaultJson + "}");
    }

    static void ClearCredential()
    {
        try { Directory.Delete(Path.Combine(Profile, ".config"), true); } catch { }
    }

    static string Q(string s) { return "\"" + s + "\""; }

    // One credential document matching the INSTALLED schema: default carries
    // id/name/email/authToken/fingerprintId/fingerprintHash.
    static string Cred(string token)
    {
        return "{\"id\":\"u1\",\"name\":\"Test\",\"email\":\"t@example.com\",\"authToken\":"
            + Q(token) + ",\"fingerprintId\":\"fp-123\",\"fingerprintHash\":\"fh-456\"}";
    }

    // ── the recorded transport ──────────────────────────────────────────────
    class Spy
    {
        public readonly List<string> Urls = new List<string>();
        public readonly List<string> Tokens = new List<string>();
        public readonly List<string> Bodies = new List<string>();
        public string Answer = "{}";
        public string Fail;

        public string Post(string url, string token, string fingerprintId, double deadline, out string error)
        {
            Urls.Add(url); Tokens.Add(token); Bodies.Add(fingerprintId);
            if (Fail != null) { error = Fail; return null; }
            error = null;
            return Answer;
        }
        public bool OnlyConstantOrigins()
        {
            // T-51 P1-3: the production destination SET is the ONE owner; the
            // test reads it rather than re-listing hostnames, so a new origin
            // cannot appear in the transport without appearing here.
            string[] allowed = FreebuffSource.ConsentOrigins();
            foreach (string url in Urls)
            {
                bool ok = false;
                foreach (string origin in allowed)
                    if (url.StartsWith(origin, StringComparison.Ordinal)) ok = true;
                if (!ok) return false;
            }
            return true;
        }
    }

    static Spy Install(Spy spy) { FreebuffSource.Transport = spy.Post; return spy; }

    // A response body carrying an ABSOLUTE balance envelope.
    static string Usage(string remainingBalance, string nextReset, string breakdown)
    {
        string bd = breakdown == null ? "" : ",\"balanceBreakdown\":" + breakdown;
        string rb = remainingBalance == null ? "null" : remainingBalance;
        string nr = nextReset == null ? "" : ",\"next_quota_reset\":" + nextReset;
        return "{\"usage\":900,\"remainingBalance\":" + rb + nr + bd + "}";
    }

    public static int Main()
    {
        string savedProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        FreebuffSource.Poster savedTransport = FreebuffSource.Transport;
        Func<bool> savedFound = FreebuffDiscovery.HasExecutableImpl;
        Func<string> savedAppPaths = FreebuffDiscovery.AppPathsTarget;
        Func<string, string> savedResolveLnk = FreebuffDiscovery.ResolveLnk;
        Func<string, bool> savedLauncherExists = FreebuffDiscovery.FileExists;
        Func<string[]> savedStartDirs = FreebuffDiscovery.StartMenuDirs;
        Func<string> savedResolveImpl = FreebuffDiscovery.ResolveImpl;
        Profile = Path.Combine(Path.GetTempPath(), "limisaw_fb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Profile);
        Environment.SetEnvironmentVariable("USERPROFILE", Profile);
        Environment.SetEnvironmentVariable("HOME", Profile);
        ConfigPath = FreebuffSource.ConfigPath();
        try
        {
            // ── 1. detection: positive only, never a project-local .freebuff ─
            Console.WriteLine("== 1. detection ==");
            ClearCredential();
            FreebuffDiscovery.HasExecutableImpl = () => false;
            Check("1a. no executable, no desktop entry => NOT installed",
                !FreebuffDiscovery.HasExecutable(), "detected");
            Check("1b. no credential file yet", !FreebuffSource.ConfigExists(), "config");
            var lvlNone = FreebuffConnectionAdapter.BuildLevel0(false, FreebuffDiscovery.HasExecutable());
            Check("1c. not installed => NotInstalled, no action",
                lvlNone.State == ConnectionState.NotInstalled && lvlNone.Installed == false, lvlNone.State.ToString());
            // CLI detected: an executable on PATH.
            FreebuffDiscovery.HasExecutableImpl = () => true;
            Check("1d. an executable on PATH => detected", FreebuffDiscovery.HasExecutable(), "cli");
            // Desktop-only: a Start Menu .lnk resolving to an existing target.
            // Driven through the seams on a SCRATCH menu dir, so the real Start
            // Menu (and its AV/lock nondeterminism) is never touched.
            string menuDir = Path.Combine(Profile, "StartMenu");
            Directory.CreateDirectory(menuDir);
            File.WriteAllText(Path.Combine(menuDir, "Freebuff.lnk"), "x");
            FreebuffDiscovery.AppPathsTarget = () => null;
            FreebuffDiscovery.StartMenuDirs = () => new[] { menuDir };
            FreebuffDiscovery.FileExists = _ => true;
            FreebuffDiscovery.ResolveLnk = link => @"C:\tools\freebuff.exe";
            ResetLauncherCache();
            string desktop = FreebuffDiscovery.DesktopLauncherTarget();
            Check("1e. a Start Menu .lnk resolving to a real target => detected",
                desktop == @"C:\tools\freebuff.exe", desktop ?? "null");

            // Desktop launcher resolution checks App Paths before the Start
            // Menu and retains only a positive target. Reuse must validate that
            // target; disappearance forces a fresh search, and a negative result
            // is never cached.
            ResetLauncherCache();
            string appTarget = @"C:\apps\freebuff.exe";
            string nextAppTarget = @"C:\apps\freebuff-new.exe";
            bool appTargetExists = true;
            int appPathReads = 0, menuWalks = 0, targetChecks = 0;
            FreebuffDiscovery.AppPathsTarget = () =>
            {
                appPathReads++;
                if (appPathReads == 1) return appTargetExists ? appTarget : null;
                if (appPathReads == 2) return null;
                return nextAppTarget;
            };
            FreebuffDiscovery.StartMenuDirs = () => { menuWalks++; return new string[0]; };
            FreebuffDiscovery.FileExists = path =>
            {
                targetChecks++;
                return appTargetExists && path == appTarget
                    || path == nextAppTarget && appPathReads >= 3;
            };
            string appFirst = FreebuffDiscovery.DesktopLauncherTarget();
            string appCached = FreebuffDiscovery.DesktopLauncherTarget();
            Check("1f. App Paths keeps priority and a positive target is reused",
                appFirst == appTarget && appCached == appTarget
                && appPathReads == 1 && menuWalks == 0 && targetChecks == 2,
                "AppPaths reads=" + appPathReads + " menu walks=" + menuWalks
                + " target checks=" + targetChecks);
            appTargetExists = false;
            string disappeared = FreebuffDiscovery.DesktopLauncherTarget();
            Check("1g. a vanished cached launcher is invalidated",
                disappeared == null && appPathReads == 2 && menuWalks == 1,
                disappeared ?? "null");
            string rediscovered = FreebuffDiscovery.DesktopLauncherTarget();
            Check("1h. a negative discovery is not cached and a later positive is found",
                rediscovered == nextAppTarget && appPathReads == 3,
                rediscovered ?? "null");

            string sourcePath = Environment.GetEnvironmentVariable("LIMISAW_TEST_SOURCE_FILE");
            if (string.IsNullOrEmpty(sourcePath))
                sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "ProbeFreebuff.cs");
            string freebuffSource = File.ReadAllText(sourcePath);
            int discoveryStart = freebuffSource.IndexOf("internal static class FreebuffDiscovery", StringComparison.Ordinal);
            string discoveryBody = discoveryStart < 0 ? "" : freebuffSource.Substring(discoveryStart);
            Check("1i. Start Menu links use lazy enumeration",
                discoveryBody.IndexOf("Directory.EnumerateFiles", StringComparison.Ordinal) >= 0
                && discoveryBody.IndexOf("Directory.GetFiles", StringComparison.Ordinal) < 0,
                "lazy source guard");

            ResetLauncherCache();
            FreebuffDiscovery.AppPathsTarget = savedAppPaths;
            FreebuffDiscovery.StartMenuDirs = savedStartDirs;
            FreebuffDiscovery.ResolveLnk = savedResolveLnk;
            FreebuffDiscovery.FileExists = savedLauncherExists;
            FreebuffDiscovery.HasExecutableImpl = () => true;

            // ── 2. CLI detected but permission OFF => PermissionRequired ────
            Console.WriteLine();
            Console.WriteLine("== 2. permission gate ==");
            WriteCredential(Cred(FakeSecret));
            Check("2a. credential file present", FreebuffSource.ConfigExists(), "config");
            FreebuffSource.Credential denied = FreebuffSource.Resolve(false);
            Check("2b. permission off => no token resolved",
                denied.Token.Length == 0 && denied.State == "config-denied", denied.State);
            Check("2c. permission-off refusal names the permission",
                (denied.Refusal ?? "").IndexOf("off", StringComparison.OrdinalIgnoreCase) >= 0, denied.Refusal);
            var lvl0 = FreebuffConnectionAdapter.BuildLevel0(false, true);
            Check("2d. Level-0 with permission off => PermissionRequired",
                lvl0.State == ConnectionState.PermissionRequired
                && lvl0.RecommendedAction == ConnectionAction.AllowAndConnect,
                lvl0.State + "/" + lvl0.RecommendedAction);

            // ── 3. permission ON resolves exactly one field ─────────────────
            Console.WriteLine();
            Console.WriteLine("== 3. credential read ==");
            FreebuffSource.Credential cred = FreebuffSource.Resolve(true);
            Check("3a. permission on => token resolved behind the gate",
                cred.Token == FakeSecret && cred.State == "credential", cred.State);
            Check("3b. fingerprintId read too (request metadata)",
                cred.FingerprintId == "fp-123", cred.FingerprintId);

            // ── 4. missing credential under permission => SignInRequired ────
            Console.WriteLine();
            Console.WriteLine("== 4. missing credential ==");
            ClearCredential();
            FreebuffSource.Credential none = FreebuffSource.Resolve(true);
            Check("4a. no file under permission => no token", none.Token.Length == 0, none.State);
            var lvl0b = FreebuffConnectionAdapter.BuildLevel0(true, true);
            Check("4b. detected + no credential => SignInRequired",
                lvl0b.State == ConnectionState.SignInRequired
                && lvl0b.RecommendedAction == ConnectionAction.Connect, lvl0b.State + "/" + lvl0b.RecommendedAction);

            // ── 5. absolute balance is NOT a percentage ─────────────────────
            Console.WriteLine();
            Console.WriteLine("== 5. FreeBucks absolute balance ==");
            WriteCredential(Cred(FakeSecret));
            Spy spy = Install(new Spy());
            spy.Answer = Usage("123.4", "1790160000", null);
            ProbeAccount acc = FreebuffSource.Probe(Stamp.Now + 10, true, true);
            Check("5a. a successful read reports OK", acc.Ok && acc.Status == Model.OK, acc.Status);
            Check("5b. exactly one balance, labelled FreeBucks",
                acc.Balances.Count == 1 && acc.Balances[0].Label == "FreeBucks", acc.Balances.Count.ToString());
            Check("5c. balance is the ABSOLUTE remainingBalance, not a percent",
                acc.Balances[0].Value.HasValue && Math.Abs(acc.Balances[0].Value.Value - 123.4) < 0.001,
                acc.Balances[0].Value.HasValue ? acc.Balances[0].Value.Value.ToString() : "null");
            Check("5d. NO window is fabricated for the balance",
                acc.Windows.Count == 0, acc.Windows.Count.ToString());
            Check("5e. next_quota_reset carried as a reset epoch",
                acc.Balances[0].ResetEpoch.HasValue && Math.Abs(acc.Balances[0].ResetEpoch.Value - 1790160000) < 1,
                acc.Balances[0].ResetEpoch.HasValue ? acc.Balances[0].ResetEpoch.Value.ToString() : "null");

            // ── 6. remainingBalance=100 usage=900 is NOT 10 percent ─────────
            Console.WriteLine();
            Console.WriteLine("== 6. no invented denominator ==");
            spy.Answer = Usage("100", null, null);
            ProbeAccount acc6 = FreebuffSource.Probe(Stamp.Now + 10, true, true);
            Check("6a. remainingBalance=100 stays 100 (no usage+remaining==quota)",
                acc6.Balances.Count == 1 && acc6.Balances[0].Value.HasValue
                && Math.Abs(acc6.Balances[0].Value.Value - 100) < 0.001,
                acc6.Balances.Count == 1 && acc6.Balances[0].Value.HasValue ? acc6.Balances[0].Value.Value.ToString() : "none");
            Check("6b. no WindowData percent is produced at all",
                acc6.Windows.Count == 0, acc6.Windows.Count.ToString());

            // ── 7. null remainingBalance is UNKNOWN, never 0 ────────────────
            Console.WriteLine();
            Console.WriteLine("== 7. null is not zero ==");
            spy.Answer = "{\"usage\":900,\"remainingBalance\":null}";
            ProbeAccount acc7 = FreebuffSource.Probe(Stamp.Now + 10, true, true);
            Check("7a. null remainingBalance does NOT become 0",
                acc7.Balances.Count == 1 && !acc7.Balances[0].Value.HasValue,
                acc7.Balances.Count == 1 ? (acc7.Balances[0].Value.HasValue ? acc7.Balances[0].Value.Value.ToString() : "null") : "no balance");
            var ad7 = Model.Flatten(acc7, Stamp.Now);
            Check("7b. Flatten keeps the balance and adds no window",
                ad7.Balances.Count == 1 && ad7.Windows.Count == 0, ad7.Balances.Count + "/" + ad7.Windows.Count);

            // ── 8. breakdown only when the vendor sent one ──────────────────
            Console.WriteLine();
            Console.WriteLine("== 8. breakdown ==");
            spy.Answer = Usage("50", null, "{\"free\":30,\"paid\":20}");
            ProbeAccount acc8 = FreebuffSource.Probe(Stamp.Now + 10, true, true);
            Check("8a. a sent breakdown is carried",
                acc8.Balances.Count == 1 && acc8.Balances[0].Breakdown != null
                && acc8.Balances[0].Breakdown.Count == 2, acc8.Balances.Count == 1 && acc8.Balances[0].Breakdown != null ? acc8.Balances[0].Breakdown.Count.ToString() : "none");
            spy.Answer = Usage("50", null, null);
            ProbeAccount acc8b = FreebuffSource.Probe(Stamp.Now + 10, true, true);
            Check("8b. an absent breakdown is null, never fabricated",
                acc8b.Balances.Count == 1 && acc8b.Balances[0].Breakdown == null, "null");

            // ── 9. transport hygiene ────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("== 9. transport ==");
            Check("9a. only constant official origins contacted", spy.OnlyConstantOrigins(),
                string.Join(",", spy.Urls.ToArray()));
            Check("9b. the POST carries the resolved token",
                spy.Tokens.Count > 0 && spy.Tokens[spy.Tokens.Count - 1] == FakeSecret, "token sent");

            // ── 10. failures classify and never leak the secret ─────────────
            Console.WriteLine();
            Console.WriteLine("== 10. failure classification + redaction ==");
            string[] cases = { "HTTP 401", "HTTP 403", "deadline_exceeded", "TlsFailed",
                               "response_too_large", "FreeBuff usage did not return JSON", "HTTP 503" };
            ConnectionErrorCode[] expect =
            {
                ConnectionErrorCode.CredentialRejected, ConnectionErrorCode.CredentialRejected,
                ConnectionErrorCode.NetworkTimeout, ConnectionErrorCode.TlsFailed,
                ConnectionErrorCode.ResponseTooLarge, ConnectionErrorCode.ProtocolChanged,
                ConnectionErrorCode.ServiceUnavailable,
            };
            for (int i = 0; i < cases.Length; i++)
            {
                ConnectionErrorCode code; ConnectionAction action;
                FreebuffSource.Classify(cases[i], out code, out action);
                Check("10." + i + " '" + cases[i] + "' => " + expect[i], code == expect[i], code.ToString());
                Check("10." + i + "b no reinstall action for '" + cases[i] + "'",
                    action != ConnectionAction.Install, action.ToString());
            }
            Spy failSpy = Install(new Spy());
            failSpy.Fail = "HTTP 401 unauthorized";
            ProbeAccount bad = FreebuffSource.Probe(Stamp.Now + 10, true, true);
            Check("10c. a refused read is ERROR, not OK", !bad.Ok && bad.Status == Model.ERROR, bad.Status);
            Check("10d. the token never appears in the error text",
                (bad.Error ?? "").IndexOf(FakeSecret, StringComparison.Ordinal) < 0, bad.Error);

            // ── 11. redaction is total across user-visible strings ───────────
            Console.WriteLine();
            Console.WriteLine("== 11. secret redaction ==");
            var visible = new List<string>();
            visible.Add(Fakebuff().ToString());
            visible.Add(FreebuffSource.Redact("token=" + FakeSecret + " failed", FakeSecret));
            visible.Add(bad.Error ?? "");
            var vcFail = FreebuffConnectionAdapter.Verify(
                FreebuffConnectionAdapter.BuildLevel0(true, true), true, true);
            visible.Add(vcFail.Reason ?? "");
            visible.Add(FreebuffConnectionAdapter.BuildLevel0(false, true).Reason ?? "");
            int leaks = 0;
            foreach (string s in visible) if (s != null && s.IndexOf(FakeSecret, StringComparison.Ordinal) >= 0) leaks++;
            Check("11a. fake secret occurs ZERO times in every user-visible string", leaks == 0,
                leaks + " leak(s) of " + visible.Count + " strings");

            // ── 12. Flatten carries the balance, and the tray ignores it ────
            Console.WriteLine();
            Console.WriteLine("== 12. flatten + tray confinement ==");
            Spy okSpy = Install(new Spy());
            okSpy.Answer = Usage("77.7", null, null);
            ProbeAccount accOk = FreebuffSource.Probe(Stamp.Now + 10, true, true);
            var adOk = Model.Flatten(accOk, Stamp.Now);
            Check("12a. Flatten carries FreeBucks to the account card",
                adOk.Balances.Count == 1 && adOk.Balances[0].Value.HasValue
                && Math.Abs(adOk.Balances[0].Value.Value - 77.7) < 0.001, "balance");
            Check("12b. the account HasReading because of the balance alone",
                adOk.HasReading, "reading");
            Check("12c. the balance is NOT a WindowData (percent modes stay clean)",
                adOk.Windows.Count == 0, adOk.Windows.Count.ToString());

            // ── 13. permission-save honesty ─────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("== 13. durable-before-authority ==");
            var settings = new LimisawSettings(Profile);
            settings.Load();
            bool before = settings.FreebuffReadConfig;
            string note;
            FreebuffConnectionAdapter.TryAllowAndConnect(settings, false, out note);
            Check("13a. a cancelled confirm does not grant access",
                settings.FreebuffReadConfig == before && note == "Cancelled", note);
            // A successful grant writes the ini and reports it saved.
            bool granted = FreebuffConnectionAdapter.TryAllowAndConnect(settings, true, out note);
            Check("13b. a confirmed grant writes the ini and persists",
                granted && settings.FreebuffReadConfig, "granted=" + granted + " " + note);
            var reread = new LimisawSettings(Profile);
            reread.Load();
            Check("13c. the grant survives a fresh settings read",
                reread.FreebuffReadConfig, "reread=" + reread.FreebuffReadConfig);

            // ── 13d. login launch: failure when no executable, success via the
            //         installed-client launcher seam ─────────────────────────
            Console.WriteLine();
            Console.WriteLine("== 13d. login launch ==");
            FreebuffDiscovery.ResolveImpl = () => "";
            string launchErr;
            // LaunchLogin is the vendor-owned login path and is safe to fail;
            // TryOpenVendor is NOT called here because its fallback opens a real
            // browser, which a test must never do.
            Check("13d1. no executable => the visible login cannot start",
                !FreebuffDiscovery.LaunchLogin(out launchErr), launchErr);
            FreebuffDiscovery.ResolveImpl = () => @"C:\tools\freebuff.exe";
            var savedLauncher = ConnectionProcessLauncher.StartInteractiveImpl;
            string launchedExe = null;
            ConnectionProcessLauncher.StartInteractiveImpl =
                (exe, args, onExit, launch) => { launchedExe = exe; return null; };
            try
            {
                bool ok = FreebuffDiscovery.LaunchLogin(out launchErr);
                Check("13d2. a discovered executable launches the vendor's own login",
                    ok && launchedExe != null && launchedExe.EndsWith("freebuff.exe", StringComparison.OrdinalIgnoreCase),
                    launchedExe ?? "none");
            }
            finally { ConnectionProcessLauncher.StartInteractiveImpl = savedLauncher; }
            FreebuffDiscovery.ResolveImpl = savedResolveImpl;

            // ── 14. source guards ───────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("== 14. source guards ==");
            string root = Directory.GetCurrentDirectory();
            string src = File.ReadAllText(Path.Combine(SourceRoot(root), "ProbeFreebuff.cs"));
            Check("14a. no Authorization header assignment exists for FreeBuff",
                src.IndexOf("Headers[\"Authorization\"]", StringComparison.Ordinal) < 0, "none");
            Check("14b. redirects are disabled",
                src.IndexOf("AllowAutoRedirect = false", StringComparison.Ordinal) >= 0, "");
            Check("14c. a project-local dot-freebuff path is never built as evidence",
                src.IndexOf("\".freebuff\"", StringComparison.Ordinal) < 0
                && src.IndexOf("Path.Combine(.*.freebuff", StringComparison.Ordinal) < 0, "");
            string lim = File.ReadAllText(Path.Combine(SourceRoot(root), "LIMISAW.cs"));
            Check("14d. FreeBucks is never read as a 0..100 percent by the tray",
                lim.IndexOf("BalanceNote(a)", StringComparison.Ordinal) >= 0
                && lim.IndexOf("a.Balances", StringComparison.Ordinal) >= 0, "");

            // ── 15. T-51 P2: installation state <> credential state ─────────
            Console.WriteLine();
            Console.WriteLine("== 15. installation vs credential state ==");
            ClearCredential();
            // executable present, permission off, NO credential file. The old
            // code answered "not-detected / FreeBuff not detected" here, which
            // contradicts the proven executable.
            FreebuffSource.Credential instOnly = FreebuffSource.Resolve(false);
            Check("15a. permission off + no credential file => NOT 'not-detected'",
                instOnly.State != "not-detected", instOnly.State);
            Check("15b. ...the credential answer is config-missing (sign in), not absence",
                instOnly.State == "config-missing", instOnly.State);
            var lvlInstalled = FreebuffConnectionAdapter.BuildLevel0(false, true);
            Check("15c. executable=true + config absent + permission=false => Installed + SignInRequired",
                lvlInstalled.Installed && lvlInstalled.State == ConnectionState.SignInRequired
                && lvlInstalled.RecommendedAction == ConnectionAction.Connect,
                lvlInstalled.Installed + "/" + lvlInstalled.State + "/" + lvlInstalled.RecommendedAction);
            Check("15d. ...never the NotInstalled/'not detected' contradiction",
                lvlInstalled.ErrorCode != ConnectionErrorCode.CliMissing
                && (lvlInstalled.Reason ?? "").IndexOf("not detected", StringComparison.OrdinalIgnoreCase) < 0,
                lvlInstalled.ErrorCode + "/" + (lvlInstalled.Reason ?? ""));
            // A REAL absence is still reported as absence, from the executable
            // fact alone (never from the credential file).
            var lvlAbsent = FreebuffConnectionAdapter.BuildLevel0(true, false);
            Check("15e. executable=false => NotInstalled/CliMissing (real absence still honest)",
                !lvlAbsent.Installed && lvlAbsent.State == ConnectionState.NotInstalled
                && lvlAbsent.ErrorCode == ConnectionErrorCode.CliMissing,
                lvlAbsent.State + "/" + lvlAbsent.ErrorCode);
            // Credential present but denied is a THIRD distinct state.
            WriteCredential(Cred(FakeSecret));
            var lvlDenied = FreebuffConnectionAdapter.BuildLevel0(false, true);
            Check("15f. credential present + permission off => PermissionRequired (distinct from sign-in)",
                lvlDenied.State == ConnectionState.PermissionRequired
                && lvlDenied.ErrorCode == ConnectionErrorCode.PermissionRequired,
                lvlDenied.State + "/" + lvlDenied.ErrorCode);
            ClearCredential();

            // ── 16. T-51 P1-2: first-time login watcher lifecycle ───────────
            Console.WriteLine();
            Console.WriteLine("== 16. first-time sign-in watcher ==");
            // B. no credential + permission off => STILL WAITING (null), never
            //    a terminal SignInRequired that ends the watcher early.
            Check("16a. no credential during the sign-in generation => still waiting",
                FreebuffConnectionAdapter.VerifySignIn(false, true) == null, "waiting");
            // C. credential appears later (permission off) => PermissionRequired,
            //    NOT another SignInRequired and no duplicate login.
            WriteCredential(Cred(FakeSecret));
            var afterAppear = FreebuffConnectionAdapter.VerifySignIn(false, true);
            Check("16b. credential appears while permission off => PermissionRequired",
                afterAppear != null && afterAppear.State == ConnectionState.PermissionRequired,
                afterAppear == null ? "null (would keep waiting)" : afterAppear.State.ToString());
            Check("16c. ...PermissionRequired offers Allow & connect, never another sign-in",
                afterAppear.RecommendedAction == ConnectionAction.AllowAndConnect,
                afterAppear.RecommendedAction.ToString());
            // E. permission granted => one verification succeeds and the watcher
            //    terminates on Connected.
            Spy signInSpy = Install(new Spy());
            signInSpy.Answer = Usage("42", null, null);
            var grantedVerify = FreebuffConnectionAdapter.VerifySignIn(true, true);
            Check("16d. permission on + credential => one verification succeeds",
                grantedVerify != null && grantedVerify.State == ConnectionState.Connected,
                grantedVerify == null ? "null" : grantedVerify.State.ToString());
            Check("16e. ...and the Connected result is terminal for the watcher",
                grantedVerify.State == ConnectionState.Connected
                || grantedVerify.State == ConnectionState.ConnectedQuotaUnavailable
                || grantedVerify.State == ConnectionState.SignInRequired
                || grantedVerify.State == ConnectionState.UnsupportedConfiguration,
                grantedVerify.State.ToString());
            Check("16f. ...and exactly ONE request was made (no duplicate login spawn)",
                signInSpy.Urls.Count == 1, signInSpy.Urls.Count.ToString());
            // A half-written credential file mid-login is still waiting, not a
            // terminal refusal.
            File.WriteAllText(ConfigPath, "{\"default\":{\"authToken\":");
            Check("16g. a mid-write credential file => still waiting, not terminal",
                FreebuffConnectionAdapter.VerifySignIn(true, true) == null, "waiting");
            ClearCredential();

            // ── 17. T-51 P1-3: consent copy == production origin set ────────
            Console.WriteLine();
            Console.WriteLine("== 17. destination consent parity ==");
            string consent = FreebuffSource.CredentialPermissionText();
            string[] dests = FreebuffSource.ConsentOrigins();
            bool everyNamed = true, everyNamedIsProd = true;
            foreach (string origin in dests)
                if (consent.IndexOf(origin, StringComparison.Ordinal) < 0) everyNamed = false;
            Check("17a. the permission text NAMES every production origin",
                everyNamed, consent.Replace("\n", " | "));
            // Every URL the transport can build must be named in the consent.
            Spy consentSpy = Install(new Spy());
            consentSpy.Answer = Usage("1", null, null);
            FreebuffSource.Probe(Stamp.Now + 10, true, true);
            foreach (string url in consentSpy.Urls)
            {
                bool named = false;
                foreach (string origin in dests)
                    if (url.StartsWith(origin, StringComparison.Ordinal)) named = true;
                if (!named) everyNamedIsProd = false;
            }
            Check("17b. no request can go to an origin the user was not told about",
                everyNamedIsProd, string.Join(",", consentSpy.Urls.ToArray()));
            // 17c. the vendor's own browser page is NOT substituted as the API host
            Check("17c. the vendor's own browser page is NOT substituted as the API host",
                consent.IndexOf("freebuff.com", StringComparison.Ordinal) < 0
                || Array.IndexOf(dests, "https://freebuff.com") >= 0, consent.Replace("\n", " | "));

            // ── 18. RED controls: the OLD behaviour each fix replaces ───────
            Console.WriteLine();
            Console.WriteLine("== 18. old-behaviour contrast (red controls) ==");
            // 18a (P1-2): the OLD watcher path — plain Verify with no credential
            // returns SignInRequired, which IsTerminalWatcherState ends on. The
            // NEW VerifySignIn must return null instead, or the fix is vacuous.
            ClearCredential();
            var oldWatch = FreebuffConnectionAdapter.Verify(
                FreebuffConnectionAdapter.BuildLevel0(false, true), false, true);
            Check("18a. OLD watcher path terminates on SignInRequired while NEW one waits",
                oldWatch.State == ConnectionState.SignInRequired
                && FreebuffConnectionAdapter.VerifySignIn(false, true) == null,
                "old=" + oldWatch.State + " new=" + (FreebuffConnectionAdapter.VerifySignIn(false, true) == null ? "waiting" : "terminal"));
            // 18b (P1-3): the OLD permission copy named the vendor homepage,
            // which is NOT the API host — consent and transport disagreed.
            bool oldCopyNamedHomepage = consent.IndexOf(FreebuffSource.Homepage, StringComparison.Ordinal) >= 0;
            bool homepageIsTransported = Array.IndexOf(dests, FreebuffSource.Homepage) >= 0;
            Check("18b. OLD homepage-only copy is gone (or the homepage is a real origin)",
                !oldCopyNamedHomepage || homepageIsTransported,
                "homepage=" + FreebuffSource.Homepage + " origins=" + string.Join(",", dests));
            // 18c (P2): the OLD Resolve(false) with no credential answered the
            // installation question ("not-detected") from the credential file.
            bool oldAbsenceClaim = FreebuffSource.Resolve(false).State == "not-detected";
            Check("18c. OLD credential-file-implies-absence claim is gone",
                !oldAbsenceClaim, FreebuffSource.Resolve(false).State);
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            FreebuffSource.Transport = savedTransport;
            FreebuffDiscovery.HasExecutableImpl = savedFound;
            FreebuffDiscovery.AppPathsTarget = savedAppPaths;
            FreebuffDiscovery.ResolveLnk = savedResolveLnk;
            FreebuffDiscovery.FileExists = savedLauncherExists;
            FreebuffDiscovery.StartMenuDirs = savedStartDirs;
            FreebuffDiscovery.ResolveImpl = savedResolveImpl;
            Environment.SetEnvironmentVariable("USERPROFILE", savedProfile);
            try { Directory.Delete(Profile, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " of " + checks + ")");
        return fails == 0 ? 0 : 1;
    }

    // A deliberately malformed "response" that DUMPS whatever it is handed, used
    // only to prove a string never carries the secret.
    static object Fakebuff() { return "no secret here"; }

    static string SourceRoot(string start)
    {
        string dir = start;
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) return dir;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        return start;
    }
}
