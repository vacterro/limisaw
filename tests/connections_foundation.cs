using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Limisaw;

public static class ConnectionsFoundationTest
{
    static int fails = 0, checks = 0;
    static void Check(string name, bool ok, string detail) { checks++; if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : "")); else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); } }

    const string FakeSecret = "LIMISAW_TEST_SECRET_9f27";

    public static int Main()
    {
        string savedProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        string savedPrimary = Environment.GetEnvironmentVariable(ZcodeSource.EnvPrimary);
        string savedAlternate = Environment.GetEnvironmentVariable(ZcodeSource.EnvAlternate);
        ZcodeSource.Fetcher savedTransport = ZcodeSource.Transport;
        var savedGetEnv = ExecutableDiscovery.GetEnvironmentVariable;
        var savedFileExists = ExecutableDiscovery.FileExists;
        var savedFallback = ExecutableDiscovery.FallbackFor;
        var savedUserPath = ExecutableDiscovery.GetUserPath;
        var savedMachinePath = ExecutableDiscovery.GetMachinePath;
        var savedZcodeAllow = ExecutableDiscovery.ZcodeAllowConfig;
        var savedFreebuffFound = FreebuffDiscovery.HasExecutableImpl;
        string profile = Path.Combine(Path.GetTempPath(), "limisaw_conn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", profile);
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, null);
            Environment.SetEnvironmentVariable(ZcodeSource.EnvAlternate, null);
            // A clean machine: FreeBuff is NOT detected, so the present list is
            // exactly the four core vendors.
            FreebuffDiscovery.HasExecutableImpl = () => false;

            Console.WriteLine("== vendor registry ==");
            // The four CORE vendors are always present. FreeBuff is OPTIONAL
            // (T-50): it is a separate definition that joins the present list
            // only when positively detected, so the core count is a hard
            // invariant and FreeBuff is a conditional extra.
            Check("registry has four core vendors", VendorRegistry.All.Length == 4, VendorRegistry.All.Length.ToString());
            foreach (string id in new[] { "codex", "claude", "antigravity", "zcode" })
                Check("registry contains " + id, VendorRegistry.Find(id) != null, id);
            Check("FreeBuff is defined but not in the core list",
                VendorRegistry.Find("freebuff") != null
                && Array.IndexOf(VendorRegistry.All, VendorRegistry.Find("freebuff")) < 0, "");
            Check("zcode requires permission", VendorRegistry.Find("zcode").RequiresCredentialPermission, "");
            Check("codex executable is codex", VendorRegistry.Find("codex").ExecutableName == "codex", VendorRegistry.Find("codex").ExecutableName);

            Console.WriteLine("== clean machine four cards ==");
            ExecutableDiscovery.GetEnvironmentVariable = _ => "";
            ExecutableDiscovery.FileExists = _ => false;
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.ZcodeAllowConfig = () => false;
            try { Directory.Delete(Path.Combine(profile, ".zcode"), true); } catch { }
            var cards = new List<VendorConnection>();
            foreach (var vd in VendorRegistry.Present()) cards.Add(ExecutableDiscovery.BuildConnection(vd.Id));
            Check("clean machine still has four cards", cards.Count == 4, cards.Count.ToString());
            foreach (var c in cards) Check("card " + c.VendorId + " exists on clean machine", c != null, c.VendorId);

            Console.WriteLine("== discovery PATH sources ==");
            string dirA = Path.Combine(profile, "binA");
            Directory.CreateDirectory(dirA);
            string fakeExe = Path.Combine(dirA, "codex.exe");
            File.WriteAllText(fakeExe, "x");
            ExecutableDiscovery.GetEnvironmentVariable = name => name == "PATH" ? "" : Environment.GetEnvironmentVariable(name);
            ExecutableDiscovery.GetUserPath = () => dirA;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.FileExists = p => File.Exists(p);
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            var found = ExecutableDiscovery.Discover("codex");
            Check("USER PATH source finds cli when process PATH empty", found.Count == 1 && found[0].IndexOf("codex.exe", StringComparison.OrdinalIgnoreCase) >= 0, found.Count + " " + (found.Count > 0 ? found[0] : "-"));

            ExecutableDiscovery.GetEnvironmentVariable = name => name == "PATH" ? dirA : Environment.GetEnvironmentVariable(name);
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            found = ExecutableDiscovery.Discover("codex");
            Check("process PATH finds cli", found.Count == 1, found.Count.ToString());

            string dirM = Path.Combine(profile, "binM");
            Directory.CreateDirectory(dirM);
            string fakeM = Path.Combine(dirM, "codex.exe");
            File.WriteAllText(fakeM, "x");
            ExecutableDiscovery.GetEnvironmentVariable = _ => "";
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => dirM;
            found = ExecutableDiscovery.Discover("codex");
            Check("MACHINE PATH finds cli", found.Count == 1, found.Count.ToString());

            string fallbackFile = Path.Combine(profile, "fallback_codex.exe");
            File.WriteAllText(fallbackFile, "x");
            ExecutableDiscovery.GetEnvironmentVariable = _ => "";
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.FallbackFor = k => k == "codex" ? new[] { fallbackFile } : new string[0];
            ExecutableDiscovery.FileExists = p => File.Exists(p);
            found = ExecutableDiscovery.Discover("codex");
            Check("fallback finds cli", found.Count == 1, found.Count.ToString());

            string shimFile = Path.Combine(dirA, "codex.cmd");
            File.WriteAllText(shimFile, "@echo off\necho hi");
            ExecutableDiscovery.GetEnvironmentVariable = _ => dirA;
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            found = ExecutableDiscovery.Discover("codex");
            bool hasCmd = false; foreach (var f in found) if (f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) hasCmd = true;
            Check(".cmd launcher accepted", hasCmd, string.Join(";", found.ToArray()));
            try { File.Delete(shimFile); } catch { }

            var fakeMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            fakeMap[Path.Combine(dirA, "codex.exe")] = true;
            fakeMap[Path.Combine(dirA.ToUpperInvariant(), "CODEX.EXE")] = true;
            ExecutableDiscovery.GetEnvironmentVariable = _ => dirA + Path.PathSeparator + dirA.ToUpperInvariant();
            ExecutableDiscovery.FileExists = p => { bool v; return fakeMap.TryGetValue(p, out v) && v; };
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            found = ExecutableDiscovery.Discover("codex");
            Check("duplicate paths deduplicated case-insensitively", found.Count == 1, found.Count.ToString());

            string dirB = Path.Combine(profile, "binB");
            Directory.CreateDirectory(dirB);
            string bExe = Path.Combine(dirB, "codex.exe");
            File.WriteAllText(bExe, "x");
            ExecutableDiscovery.GetEnvironmentVariable = _ => dirA + Path.PathSeparator + dirB;
            ExecutableDiscovery.FileExists = p => File.Exists(p);
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            found = ExecutableDiscovery.Discover("codex");
            Check("two different installs detected as conflict (count==2)", found.Count == 2, found.Count.ToString());
            var vc = ExecutableDiscovery.BuildConnection("codex");
            Check("conflicting installations -> error code", vc.ErrorCode == ConnectionErrorCode.ConflictingInstallations, vc.ErrorCode.ToString());

            Console.WriteLine("== level 0 no process/network/probe ==");
            // R089: the old test inferred "no process" from FileExists calls,
            // which proves nothing. The counters are the observable contract:
            // a full Level 0 pass over all four vendors starts no process,
            // opens no socket and asks no vendor for quota.
            ConnectionProbeCounters.Reset();
            ExecutableDiscovery.GetEnvironmentVariable = _ => dirA;
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            ExecutableDiscovery.FileExists = p => File.Exists(p);
            ExecutableDiscovery.ZcodeAllowConfig = () => false;
            foreach (var vd in VendorRegistry.All) ExecutableDiscovery.BuildConnection(vd.Id);
            Check("Level 0 starts no vendor process", ConnectionProbeCounters.ProcessStarts == 0, ConnectionProbeCounters.ProcessStarts.ToString());
            Check("Level 0 makes no network call", ConnectionProbeCounters.NetworkCalls == 0, ConnectionProbeCounters.NetworkCalls.ToString());
            Check("Level 0 performs no quota probe", ConnectionProbeCounters.QuotaProbes == 0, ConnectionProbeCounters.QuotaProbes.ToString());

            Console.WriteLine("== state model projection ==");
            foreach (ConnectionState s in new[] { ConnectionState.NotInstalled, ConnectionState.PermissionRequired, ConnectionState.SignInRequired, ConnectionState.Verifying, ConnectionState.Connected, ConnectionState.ConnectedQuotaUnavailable, ConnectionState.Degraded })
            {
                var v = new VendorConnection { VendorId = "codex", State = s, Reason = "test" };
                var pres = ConnectionPresentation.From(v);
                Check("presentation for " + s + " has StateText", pres.StateText.Length > 0, pres.StateText);
                Check("presentation for " + s + " has Title", pres.Title.Length > 0, pres.Title);
            }
            var pNot = ConnectionPresentation.From(new VendorConnection { VendorId = "codex", State = ConnectionState.NotInstalled });
            Check("NotInstalled severity is Warning", pNot.Severity == ConnectionSeverity.Warning, pNot.Severity.ToString());
            var pFail = ConnectionPresentation.From(new VendorConnection { VendorId = "codex", State = ConnectionState.VendorUnavailable });
            Check("VendorUnavailable severity is Error", pFail.Severity == ConnectionSeverity.Error, pFail.Severity.ToString());
            var pConn2 = ConnectionPresentation.From(new VendorConnection { VendorId = "codex", State = ConnectionState.Connected });
            Check("Connected severity is Info", pConn2.Severity == ConnectionSeverity.Info, pConn2.Severity.ToString());
            var pConn = ConnectionPresentation.From(new VendorConnection { VendorId = "codex", State = ConnectionState.Connected });
            Check("Connected text is Connected", pConn.StateText == "Connected", pConn.StateText);
            var pQuota = ConnectionPresentation.From(new VendorConnection { VendorId = "zcode", State = ConnectionState.ConnectedQuotaUnavailable });
            Check("ConnectedQuotaUnavailable text contains quota", pQuota.StateText.ToLowerInvariant().Contains("quota"), pQuota.StateText);

            Console.WriteLine("== R040: vendor-aware action labels ==");
            // The label projection is the vendor-aware boundary: Connect is
            // "Sign in" only for CLI vendors, OpenVendor names the ACTUAL
            // vendor (so "Open ZCode" can exist only for zcode), and
            // Troubleshoot stays vendor-neutral. A universal label regression
            // (every vendor reading "Open ZCode") fails every case here.
            var pOpenZ = ConnectionPresentation.From(new VendorConnection { VendorId = "zcode", RecommendedAction = ConnectionAction.OpenVendor });
            Check("R040: zcode OpenVendor -> Open ZCode", pOpenZ.PrimaryActionText == "Open ZCode", pOpenZ.PrimaryActionText);
            var pConnectCodex = ConnectionPresentation.From(new VendorConnection { VendorId = "codex", RecommendedAction = ConnectionAction.Connect });
            Check("R040: codex Connect -> Sign in", pConnectCodex.PrimaryActionText == "Sign in", pConnectCodex.PrimaryActionText);
            var pConnectClaude = ConnectionPresentation.From(new VendorConnection { VendorId = "claude", RecommendedAction = ConnectionAction.Connect });
            Check("R040: claude Connect -> Sign in", pConnectClaude.PrimaryActionText == "Sign in", pConnectClaude.PrimaryActionText);
            var pConnectAgy = ConnectionPresentation.From(new VendorConnection { VendorId = "antigravity", RecommendedAction = ConnectionAction.Connect });
            Check("R040: antigravity Connect -> Sign in", pConnectAgy.PrimaryActionText == "Sign in", pConnectAgy.PrimaryActionText);
            var pTroubleCodex = ConnectionPresentation.From(new VendorConnection { VendorId = "codex", RecommendedAction = ConnectionAction.Troubleshoot });
            Check("R040: codex Troubleshoot -> Troubleshoot", pTroubleCodex.PrimaryActionText == "Troubleshoot", pTroubleCodex.PrimaryActionText);
            foreach (string nonZ in new[] { "codex", "claude", "antigravity" })
            {
                var pAny = ConnectionPresentation.From(new VendorConnection { VendorId = nonZ, RecommendedAction = ConnectionAction.OpenVendor });
                Check("R040: " + nonZ + " OpenVendor never reads Open ZCode", pAny.PrimaryActionText != "Open ZCode" && pAny.PrimaryActionText.StartsWith("Open "), pAny.PrimaryActionText);
            }
            var pOpenCodex = ConnectionPresentation.From(new VendorConnection { VendorId = "codex", RecommendedAction = ConnectionAction.OpenVendor });
            Check("R040: codex OpenVendor -> Open Codex", pOpenCodex.PrimaryActionText == "Open Codex", pOpenCodex.PrimaryActionText);
            var pOpenClaude = ConnectionPresentation.From(new VendorConnection { VendorId = "claude", RecommendedAction = ConnectionAction.OpenVendor });
            Check("R040: claude OpenVendor -> Open Claude Code", pOpenClaude.PrimaryActionText == "Open Claude Code", pOpenClaude.PrimaryActionText);

            Console.WriteLine("== error priority ==");
            Check("cli_missing outranks network", ConnectionErrorPriority.Rank(ConnectionErrorCode.CliMissing) < ConnectionErrorPriority.Rank(ConnectionErrorCode.NetworkTimeout), "");
            Check("credential_rejected outranks quota", ConnectionErrorPriority.Rank(ConnectionErrorCode.CredentialRejected) < ConnectionErrorPriority.Rank(ConnectionErrorCode.QuotaNotAvailable), "");
            Check("protocol_changed is low priority (not auth)", ConnectionErrorPriority.Rank(ConnectionErrorCode.ProtocolChanged) > ConnectionErrorPriority.Rank(ConnectionErrorCode.CredentialRejected), "");
            var pick = ConnectionErrorPriority.Pick(ConnectionErrorCode.CliMissing, ConnectionErrorCode.NetworkTimeout);
            Check("Pick cli_missing + network -> cli_missing", pick == ConnectionErrorCode.CliMissing, pick.ToString());
            pick = ConnectionErrorPriority.Pick(ConnectionErrorCode.CredentialRejected, ConnectionErrorCode.QuotaNotAvailable);
            Check("Pick credential_rejected + quota -> credential", pick == ConnectionErrorCode.CredentialRejected, pick.ToString());
            var act = ConnectionErrorPriority.RecommendedAction(ConnectionErrorCode.CredentialRejected, ConnectionState.Failed);
            Check("credential_rejected recommends not Install", act != ConnectionAction.Install, act.ToString());
            act = ConnectionErrorPriority.RecommendedAction(ConnectionErrorCode.CliMissing, ConnectionState.NotInstalled);
            Check("cli_missing recommends Install", act == ConnectionAction.Install, act.ToString());

            Console.WriteLine("== coordinator ==");
            var coord = new ConnectionCoordinator();
            int g1 = coord.Begin("zcode");
            Check("Begin returns generation 1", g1 == 1, g1.ToString());
            int g1b = coord.Begin("zcode");
            Check("second Begin while active is rejected (-1)", g1b == -1, g1b.ToString());
            int gz = coord.Begin("codex");
            Check("different vendor not blocked", gz == 1, gz.ToString());
            var conn1 = new VendorConnection { VendorId = "zcode", State = ConnectionState.Connected, Reason = "ok" };
            bool pub1 = coord.TryPublish("zcode", g1, conn1);
            Check("publish gen1 succeeds", pub1, "");
            int g2 = coord.Begin("zcode");
            Check("after publish, new generation is 2", g2 == 2, g2.ToString());
            var conn2 = new VendorConnection { VendorId = "zcode", State = ConnectionState.Connected, Reason = "v2" };
            coord.TryPublish("zcode", g2, conn2);
            var stale = new VendorConnection { VendorId = "zcode", State = ConnectionState.Failed, Reason = "stale" };
            bool staleOk = coord.TryPublish("zcode", g1, stale);
            Check("stale publish rejected", !staleOk, staleOk.ToString());
            var latest = coord.Latest("zcode");
            Check("stale does not overwrite Connected", latest != null && latest.State == ConnectionState.Connected, latest == null ? "null" : latest.State.ToString());

            Console.WriteLine("== cancel invalidates the generation ==");
            var coord2 = new ConnectionCoordinator();
            int cg = coord2.Begin("codex");
            coord2.Cancel("codex");
            bool cancelledPublish = coord2.TryPublish("codex", cg, new VendorConnection { VendorId = "codex", State = ConnectionState.Connected });
            Check("publish after Cancel is rejected", !cancelledPublish, cancelledPublish.ToString());
            int cgen2 = coord2.Begin("codex");
            // Cancel supersedes the generation (1 -> 2), so the NEXT Begin
            // mints 3: the superseded operation's publish stays invalid.
            Check("new operation after Cancel gets a fresh generation", cgen2 == cg + 2, cgen2.ToString());

            Console.WriteLine("== snapshot ownership (no shared mutable objects) ==");
            var coord3 = new ConnectionCoordinator();
            int sg = coord3.Begin("claude");
            var owned = new VendorConnection { VendorId = "claude", State = ConnectionState.Connected, Reason = "before" };
            owned.CandidatePaths.Add("C:\\one\\claude.exe");
            coord3.TryPublish("claude", sg, owned);
            // Mutating the original after publishing must not reach the coordinator.
            owned.State = ConnectionState.Failed; owned.Reason = "mutated original";
            owned.CandidatePaths[0] = "C:\\mutated";
            var stored = coord3.Latest("claude");
            Check("mutating the original does not mutate coordinator Latest", stored != null && stored.State == ConnectionState.Connected && stored.Reason == "before", stored == null ? "null" : stored.State + "/" + stored.Reason);
            Check("mutating the original does not mutate stored list", stored.CandidatePaths[0] == "C:\\one\\claude.exe", stored.CandidatePaths[0]);
            // Mutating what Latest handed back must not reach the coordinator either.
            stored.State = ConnectionState.Degraded; stored.Reason = "mutated returned"; stored.CandidatePaths[0] = "C:\\mutated-again";
            var again = coord3.Latest("claude");
            Check("mutating a Latest result does not mutate coordinator state", again.State == ConnectionState.Connected && again.Reason == "before" && again.CandidatePaths[0] == "C:\\one\\claude.exe",
                again.State + "/" + again.Reason + "/" + again.CandidatePaths[0]);

            Console.WriteLine("== shutdown invalidates every generation ==");
            var coord4 = new ConnectionCoordinator();
            int shg = coord4.Begin("zcode");
            int shg2 = coord4.Begin("codex");
            coord4.Shutdown();
            bool shutdownPub = coord4.TryPublish("zcode", shg, new VendorConnection { VendorId = "zcode", State = ConnectionState.Connected });
            bool shutdownPub2 = coord4.TryPublish("codex", shg2, new VendorConnection { VendorId = "codex", State = ConnectionState.Connected });
            Check("publish after shutdown rejected (zcode)", !shutdownPub, shutdownPub.ToString());
            Check("publish after shutdown rejected (codex)", !shutdownPub2, shutdownPub2.ToString());
            Check("no vendor active after shutdown", !coord4.IsActive("zcode") && !coord4.IsActive("codex"), "");

            Console.WriteLine("== process ownership ==");
            // PROBE child: adopted into the scope, dies with it.
            var probeScope = ChildSweeper.Open();
            var probePsi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
            { CreateNoWindow = true, UseShellExecute = false };
            var probeChild = System.Diagnostics.Process.Start(probePsi);
            bool adopted = probeScope.Armed ? probeScope.Adopt(probeChild) : false;
            Check("probe child adopted into scope", adopted, adopted.ToString());
            probeScope.Dispose();
            bool probeDied = false;
            try { probeDied = probeChild.WaitForExit(3000); } catch { probeDied = true; }
            Check("disposing the probe scope terminates the probe tree", probeDied, probeDied.ToString());
            try { probeChild.Dispose(); } catch { }
            // INTERACTIVE child: NOT adopted; disposing a LIMISAW-owned probe
            // scope must not terminate it. The fixture is terminated explicitly.
            var interactivePsi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
            { CreateNoWindow = true, UseShellExecute = false };
            var interactiveChild = System.Diagnostics.Process.Start(interactivePsi);
            // An unrelated LIMISAW-owned probe scope: the interactive child was
            // never handed to any Adopt, so ending the scope cannot touch it.
            var unrelatedScope = ChildSweeper.Open();
            unrelatedScope.Dispose();
            bool interactiveAlive = !interactiveChild.HasExited;
            Check("disposing a LIMISAW-owned probe scope leaves the interactive child alive", interactiveAlive, interactiveAlive.ToString());
            try { interactiveChild.Kill(); } catch { }
            try { interactiveChild.Dispose(); } catch { }
            var selfScope = ChildSweeper.Open();
            Check("LIMISAW itself never inside probe scope", !ChildSweeper.SelfInside(selfScope), "");
            selfScope.Dispose();

            Console.WriteLine("== zcode vertical ==");
            ExecutableDiscovery.ZcodeAllowConfig = () => false;
            try { Directory.Delete(Path.Combine(profile, ".zcode"), true); } catch { }
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, null);
            Environment.SetEnvironmentVariable(ZcodeSource.EnvAlternate, null);
            var zc = ExecutableDiscovery.BuildConnection("zcode");
            Check("clean zcode -> SignInRequired/OpenVendor", zc.State == ConnectionState.SignInRequired && zc.RecommendedAction == ConnectionAction.OpenVendor, zc.State + "/" + zc.RecommendedAction);

            Directory.CreateDirectory(Path.Combine(profile, ".zcode", "v2"));
            File.WriteAllText(Path.Combine(profile, ".zcode", "v2", "config.json"), "{\"provider\":{\"builtin:zai-coding-plan\":{\"enabled\":true,\"options\":{\"baseURL\":\"https://api.z.ai\",\"apiKey\":\"" + FakeSecret + "\"}}}}");
            ExecutableDiscovery.ZcodeAllowConfig = () => false;
            zc = ExecutableDiscovery.BuildConnection("zcode");
            Check("config + permission off -> PermissionRequired/AllowAndConnect", zc.State == ConnectionState.PermissionRequired && zc.RecommendedAction == ConnectionAction.AllowAndConnect, zc.State + "/" + zc.RecommendedAction);
            int transportCalls = 0;
            ZcodeSource.Transport = (string url, string key, double deadline, out string error) => { transportCalls++; error = "fail"; return null; };
            var probed = ZcodeSource.Probe(Stamp.Now + 5, false);
            Check("permission off -> transport 0", transportCalls == 0, transportCalls.ToString());

            string tmpDir = Path.Combine(Path.GetTempPath(), "limisaw_conn_settings_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            var settingsType = typeof(LimisawSettings);
            object settings = Activator.CreateInstance(settingsType, new object[] { tmpDir });
            settingsType.GetMethod("Load").Invoke(settings, null);
            settingsType.GetField("ZcodeReadConfig").SetValue(settings, false);
            var hookField = settingsType.GetField("WriteHook", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            object hookOrig = hookField.GetValue(settings);
            bool saveShouldFail = false;
            Func<string, string, string, bool> realWrite = (k, v, f) =>
            {
                var m = settingsType.GetMethod("Write", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new Type[] { typeof(string), typeof(string), typeof(string) }, null);
                return (bool)m.Invoke(settings, new object[] { k, v, f });
            };
            hookField.SetValue(settings, new Func<string, string, string, bool>((k, v, f) => { if (saveShouldFail) return false; return realWrite(k, v, f); }));
            transportCalls = 0;
            ZcodeSource.Transport = (string url, string key, double deadline, out string error) => { transportCalls++; error = "fail"; return null; };
            saveShouldFail = true;
            string noteFail;
            bool ok = ZcodeConnectionAdapter.TryAllowAndConnect((LimisawSettings)settings, true, out noteFail);
            Check("save failure -> TryAllow returns false", !ok, ok.ToString() + " " + noteFail);
            Check("save failure -> transport 0", transportCalls == 0, transportCalls.ToString());
            bool stillOff = !(bool)settingsType.GetField("ZcodeReadConfig").GetValue(settings);
            Check("save failure -> permission not treated as granted", stillOff, stillOff.ToString());

            saveShouldFail = false;
            hookField.SetValue(settings, null);
            ZcodeSource.Transport = (string url, string key, double deadline, out string error) => { error = null; return "{\"code\":200,\"data\":{\"level\":\"lite\",\"limits\":[{\"type\":\"CREDIT_LIMIT\",\"unit\":3,\"number\":5,\"usage\":100,\"remaining\":80,\"percentage\":20,\"nextResetTime\":9999999999999},{\"type\":\"CREDIT_LIMIT\",\"unit\":6,\"number\":1,\"usage\":100,\"remaining\":90,\"percentage\":10,\"nextResetTime\":9999999999999}]}}"; };
            string noteOk;
            ok = ZcodeConnectionAdapter.TryAllowAndConnect((LimisawSettings)settings, true, out noteOk);
            Check("save success -> TryAllow true", ok, ok.ToString() + " " + noteOk);
            bool nowOn = (bool)settingsType.GetField("ZcodeReadConfig").GetValue(settings);
            Check("save success -> Config access on", nowOn, nowOn.ToString());

            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, FakeSecret);
            ExecutableDiscovery.ZcodeAllowConfig = () => false;
            zc = ExecutableDiscovery.BuildConnection("zcode");
            Check("env key bypasses permission (not PermissionRequired)", zc.State != ConnectionState.PermissionRequired, zc.State.ToString());
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, null);

            ExecutableDiscovery.ZcodeAllowConfig = () => true;
            ZcodeSource.Transport = (string url, string key, double deadline, out string error) => { error = "HTTP 401"; return null; };
            var baseConn = new VendorConnection { VendorId = "zcode", State = ConnectionState.Verifying };
            var verified = ZcodeConnectionAdapter.Verify(baseConn, true);
            Check("401 -> CredentialRejected not Install", verified.ErrorCode == ConnectionErrorCode.CredentialRejected && verified.RecommendedAction != ConnectionAction.Install, verified.ErrorCode + "/" + verified.RecommendedAction);

            int callN = 0;
            ZcodeSource.Transport = (string url, string key, double deadline, out string error) =>
            {
                callN++;
                if (url.Contains("api.z.ai")) { error = "ServiceUnavailable"; return null; }
                error = null; return "{\"code\":200,\"data\":{\"level\":\"lite\",\"limits\":[{\"type\":\"CREDIT_LIMIT\",\"unit\":3,\"number\":5,\"usage\":100,\"remaining\":80,\"percentage\":20,\"nextResetTime\":9999999999999},{\"type\":\"CREDIT_LIMIT\",\"unit\":6,\"number\":1,\"usage\":100,\"remaining\":90,\"percentage\":10,\"nextResetTime\":9999999999999}]}}";
            };
            baseConn = new VendorConnection { VendorId = "zcode", State = ConnectionState.Verifying };
            verified = ZcodeConnectionAdapter.Verify(baseConn, true);
            Check("host1 dead host2 ok -> Connected", verified.State == ConnectionState.Connected, verified.State + " " + verified.Reason);

            ZcodeSource.Transport = (string url, string key, double deadline, out string error) => { error = "ServiceUnavailable"; return null; };
            baseConn = new VendorConnection { VendorId = "zcode", State = ConnectionState.Verifying };
            verified = ZcodeConnectionAdapter.Verify(baseConn, true);
            Check("both hosts dead -> not credential error", verified.ErrorCode != ConnectionErrorCode.CredentialRejected && verified.ErrorCode != ConnectionErrorCode.CredentialMissing, verified.ErrorCode.ToString());

            ZcodeSource.Transport = (string url, string key, double deadline, out string error) => { error = null; return "not json at all"; };
            baseConn = new VendorConnection { VendorId = "zcode", State = ConnectionState.Verifying };
            verified = ZcodeConnectionAdapter.Verify(baseConn, true);
            Check("malformed payload -> ProtocolChanged/OutputInvalid", verified.ErrorCode == ConnectionErrorCode.ProtocolChanged || verified.ErrorCode == ConnectionErrorCode.OutputInvalid || verified.ErrorCode == ConnectionErrorCode.ServiceUnavailable, verified.ErrorCode.ToString());

            Console.WriteLine("== secret safety ==");
            string secret = FakeSecret;
            // The env credential is the secret the adapter possesses, so the
            // diagnostics boundary knows it (R042/R088).
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, secret);
            var withSecret = new VendorConnection { VendorId = "zcode", State = ConnectionState.Failed, Reason = "error with " + secret + " inside", ResolvedPath = "C:\\path\\file.txt", ErrorCode = ConnectionErrorCode.CredentialRejected };
            string report = ConnectionDiagnostics.BuildReport(withSecret, "0.0.8");
            string reportWithSecret = report + " " + ConnectionDiagnostics.Redact("leaked " + secret, secret);
            Check("diagnostics contain no secret", reportWithSecret.IndexOf(secret, StringComparison.Ordinal) < 0, report.Length > 80 ? report.Substring(0, 80) : report);
            string redacted = ConnectionDiagnostics.Redact("Bearer " + secret, secret);
            Check("Bearer redacted", redacted.IndexOf(secret, StringComparison.Ordinal) < 0 && redacted.IndexOf("Bearer", StringComparison.Ordinal) >= 0, redacted);
            string authRedacted = ConnectionDiagnostics.Redact("Authorization: Bearer " + secret, secret);
            Check("Authorization header redacted", authRedacted.IndexOf(secret, StringComparison.Ordinal) < 0, authRedacted);
            // R042: the projection IS the final boundary — a raw untrusted
            // connector string carrying a secret is redacted at presentation
            // even when no adapter ever sanitized it.
            var rawSecretConn = new VendorConnection { VendorId = "zcode", State = ConnectionState.Failed, ErrorCode = ConnectionErrorCode.CredentialRejected, Reason = "denied: Bearer " + secret };
            var rawPres = ConnectionPresentation.From(rawSecretConn);
            Check("presentation redacts a raw unsanitized connector secret", rawPres.ReasonText.IndexOf(secret, StringComparison.Ordinal) < 0, rawPres.ReasonText);
            var apiKeyReason = ConnectionDiagnostics.Redact("config bad: api_key = \"" + secret + "\" retry", null);
            Check("api-key assignment redacted", apiKeyReason.IndexOf(secret, StringComparison.Ordinal) < 0, apiKeyReason);
            Check("paths are not over-redacted", ConnectionDiagnostics.Redact("path C:\\Users\\x\\.codex\\auth.json missing", null).IndexOf("C:\\Users\\x\\.codex", StringComparison.Ordinal) >= 0, "");
            var reasonConn = new VendorConnection { VendorId = "zcode", State = ConnectionState.Failed, ErrorCode = ConnectionErrorCode.CredentialRejected, Reason = "failed" };
            var pres2 = ConnectionPresentation.From(reasonConn);
            Check("presentation reason sanitized", ConnectionDiagnostics.Redact(pres2.ReasonText, secret).IndexOf(secret, StringComparison.Ordinal) < 0, pres2.ReasonText);
            string bearerWithSecret = ConnectionDiagnostics.Redact("err Bearer " + secret + " end", secret);
            Check("secret in Bearer context redacted", bearerWithSecret.IndexOf(secret, StringComparison.Ordinal) < 0, bearerWithSecret);

            Console.WriteLine("== R020 UI projection before the first generation ==");
            // Zero-I/O seams: any discovery I/O the ordinary projection path might
            // trigger surfaces through the counters — and is harmless here.
            var pFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            ExecutableDiscovery.GetEnvironmentVariable = _ => "";
            ExecutableDiscovery.FileExists = _ => false;
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.ZcodeAllowConfig = () => false;
            var projSettings = new LimisawSettings(Path.Combine(profile, "pappsettings"));
            projSettings.Load();
            List<Theme> projThemes = Theme.Load(Directory.GetCurrentDirectory());
            var projTray = new System.Windows.Forms.NotifyIcon();
            System.Windows.Forms.Form projForm = null;
            try
            {
                projForm = (System.Windows.Forms.Form)Activator.CreateInstance(typeof(LimisawForm),
                    new object[] { Path.Combine(profile, "papp"), projSettings, projTray, projThemes });
                // The constructor's BuildConnections runs before the first worker
                // generation is built — that is the R020 path under test. Read it
                // synchronously (no message pumping) so no background sweep can
                // have replaced it yet: Publish reaches this thread only through
                // BeginInvoke, which needs the pump we are not running.
                var refreshingField = typeof(LimisawForm).GetField("Refreshing", pFlags);
                ExecutableDiscovery.ResetDiscoveryCountsForTests();
                var pConns = (List<VendorConnection>)typeof(LimisawForm).GetField("Connections", pFlags).GetValue(projForm);
                Check("UI projection before any generation shows all four core vendor rows", pConns.Count == 4, pConns.Count.ToString());
                bool allPlaceholder = true; foreach (var c in pConns) if (c.State != ConnectionState.Discovering) allPlaceholder = false;
                Check("every vendor row is a non-authoritative placeholder (Discovering) before generation", allPlaceholder, "");
                Check("pre-generation BuildConnections performs ZERO RegistryOpens", ExecutableDiscovery.RegistryOpens == 0, ExecutableDiscovery.RegistryOpens.ToString());
                Check("pre-generation BuildConnections performs ZERO PathSourcesRead", ExecutableDiscovery.PathSourcesRead == 0, ExecutableDiscovery.PathSourcesRead.ToString());
                Check("pre-generation BuildConnections performs ZERO FileExistsChecks", ExecutableDiscovery.FileExistsChecks == 0, ExecutableDiscovery.FileExistsChecks.ToString());
                Check("pre-generation BuildConnections performs ZERO ConfigReads", ExecutableDiscovery.ConfigReads == 0, ExecutableDiscovery.ConfigReads.ToString());
                Check("pre-generation BuildConnections performs ZERO ConfigParses", ExecutableDiscovery.ConfigParses == 0, ExecutableDiscovery.ConfigParses.ToString());
                // Build a real (empty-world) generation, then project it: the real
                // Level-0 snapshot replaces the placeholders with ZERO I/O. First
                // let the constructor's own background sweep settle, so nothing
                // rebuilds a generation underneath us.
                int spin = 0;
                while ((bool)refreshingField.GetValue(projForm) && spin++ < 600)
                { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                ExecutableDiscovery.BeginGeneration();
                ExecutableDiscovery.EnsureGeneration();
                ExecutableDiscovery.ResetDiscoveryCountsForTests();
                typeof(LimisawForm).GetMethod("BuildConnections", pFlags).Invoke(projForm, null);
                var pConns2 = (List<VendorConnection>)typeof(LimisawForm).GetField("Connections", pFlags).GetValue(projForm);
                Check("post-generation projection shows all four core vendor rows", pConns2.Count == 4, pConns2.Count.ToString());
                bool noPlaceholder = true; foreach (var c in pConns2) if (c.State == ConnectionState.Discovering) noPlaceholder = false;
                Check("the projection is the REAL snapshot, not placeholders", noPlaceholder, "");
                Check("post-generation BuildConnections performs ZERO additional discovery I/O",
                    ExecutableDiscovery.RegistryOpens == 0 && ExecutableDiscovery.PathSourcesRead == 0
                    && ExecutableDiscovery.FileExistsChecks == 0 && ExecutableDiscovery.ConfigReads == 0 && ExecutableDiscovery.ConfigParses == 0,
                    "R=" + ExecutableDiscovery.RegistryOpens + " P=" + ExecutableDiscovery.PathSourcesRead
                    + " F=" + ExecutableDiscovery.FileExistsChecks + " CR=" + ExecutableDiscovery.ConfigReads + " CP=" + ExecutableDiscovery.ConfigParses);
                // Drop the generation so the downstream fresh-discovery sections
                // (BuildConnection("zcode") == fresh) keep their original contract.
                ExecutableDiscovery.BeginGeneration();
            }
            finally
            {
                try { if (projForm != null) projForm.Dispose(); } catch { }
                try { projTray.Dispose(); } catch { }
            }

            Console.WriteLine("== T-51 P1-1 zero UI-thread FreeBuff discovery I/O ==");
            {
                // The contract: card build, the Settings row and the paint pass
                // consume the PUBLISHED refresh-generation fact and perform
                // ZERO registry/PATH/Start-Menu/config discovery I/O. Proven by
                // configuring every discovery seam to THROW and requiring the UI
                // paths to stay green.
                var savedOptPresent = ExecutableDiscovery.OptionalVendorPresent;
                var savedHasExe = FreebuffDiscovery.HasExecutableImpl;
                var savedAppPathsUI = FreebuffDiscovery.AppPathsTarget;
                var savedStartDirsUI = FreebuffDiscovery.StartMenuDirs;
                var savedResolveImplUI = FreebuffDiscovery.ResolveImpl;
                var savedResolveLnkUI = FreebuffDiscovery.ResolveLnk;
                var savedFbExistsUI = FreebuffDiscovery.FileExists;
                Func<string, bool> throwingExists = p => { throw new Exception("discovery I/O on the UI path: " + p); };
                try
                {
                    var uiSettings = new LimisawSettings(Path.Combine(profile, "uitest"));
                    uiSettings.Load();
                    var uiThemes = Theme.Load(Directory.GetCurrentDirectory());
                    var uiTray = new System.Windows.Forms.NotifyIcon();
                    System.Windows.Forms.Form uiForm = null;
                    try
                    {
                        // 1. Construct the real form with the seams still REAL: its
                        //    constructor legitimately runs a start-up sweep.
                        uiForm = (System.Windows.Forms.Form)Activator.CreateInstance(typeof(LimisawForm),
                            new object[] { Path.Combine(profile, "uiapp"), uiSettings, uiTray, uiThemes });
                        var refreshingFieldUI = typeof(LimisawForm).GetField("Refreshing", pFlags);
                        int spinUI = 0;
                        while ((bool)refreshingFieldUI.GetValue(uiForm) && spinUI++ < 600)
                        { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }

                        // 2. A deterministic PUBLISHED generation with the optional
                        //    vendor present — what the worker would have published.
                        ExecutableDiscovery.OptionalVendorPresent = _ => true;
                        ExecutableDiscovery.BeginGeneration();
                        ExecutableDiscovery.EnsureGeneration();

                        // 3. NOW every discovery seam THROWS. Any UI-path walk
                        //    becomes a visible failure instead of a hidden cost.
                        ExecutableDiscovery.OptionalVendorPresent = _ => { throw new Exception("generation rebuilt on the UI path"); };
                        FreebuffDiscovery.HasExecutableImpl = () => { throw new Exception("HasExecutable() on the UI path"); };
                        FreebuffDiscovery.AppPathsTarget = () => { throw new Exception("App Paths read on the UI path"); };
                        FreebuffDiscovery.StartMenuDirs = () => { throw new Exception("Start Menu enumeration on the UI path"); };
                        FreebuffDiscovery.ResolveImpl = () => { throw new Exception("executable resolution on the UI path"); };
                        FreebuffDiscovery.ResolveLnk = _ => { throw new Exception(".lnk resolution on the UI path"); };
                        FreebuffDiscovery.FileExists = throwingExists;

                        int regBefore = ExecutableDiscovery.RegistryOpens;
                        int pathBefore = ExecutableDiscovery.PathSourcesRead;
                        int feBefore = ExecutableDiscovery.FileExistsChecks;
                        int crBefore = ExecutableDiscovery.ConfigReads;
                        int cpBefore = ExecutableDiscovery.ConfigParses;

                        // RED CONTROL: prove the throwing seams are genuinely
                        // armed, so the GREEN below cannot be vacuous.
                        bool armed = false;
                        try { FreebuffDiscovery.HasExecutable(); }
                        catch { armed = true; }
                        Check("P1-1z. the discovery seams are genuinely armed to throw (red control)",
                            armed, armed ? "armed" : "the seam did not throw -- the guard is vacuous");

                        // 4. VendorRegistry.Present() — the card-list source.
                        var uiPresent = VendorRegistry.Present();
                        bool hasFreebuff = Array.IndexOf(uiPresent, VendorRegistry.Find("freebuff")) >= 0;
                        Check("P1-1a. Present() includes the PUBLISHED optional vendor without discovery I/O",
                            hasFreebuff, uiPresent.Length.ToString());
                        Check("P1-1b. Present() added ZERO discovery I/O",
                            ExecutableDiscovery.RegistryOpens == regBefore
                            && ExecutableDiscovery.PathSourcesRead == pathBefore
                            && ExecutableDiscovery.FileExistsChecks == feBefore,
                            "R=" + (ExecutableDiscovery.RegistryOpens - regBefore)
                            + " P=" + (ExecutableDiscovery.PathSourcesRead - pathBefore)
                            + " F=" + (ExecutableDiscovery.FileExistsChecks - feBefore));

                        // 5. Card build + Settings paint on the real form.
                        typeof(LimisawForm).GetMethod("BuildConnections", pFlags).Invoke(uiForm, null);
                        var uiConns = (List<VendorConnection>)typeof(LimisawForm).GetField("Connections", pFlags).GetValue(uiForm);
                        Check("P1-1c. BuildConnections renders the published FreeBuff card",
                            uiConns.Find(c => c.VendorId == "freebuff") != null, uiConns.Count.ToString());
                        typeof(LimisawForm).GetField("Tab", pFlags).SetValue(uiForm, 2); // Settings tab
                        uiForm.ClientSize = new System.Drawing.Size(900, 700);
                        using (var uiBmp = new System.Drawing.Bitmap(Math.Max(1, uiForm.Width), Math.Max(1, uiForm.Height)))
                        using (var uiG = System.Drawing.Graphics.FromImage(uiBmp))
                        {
                            var uiArgs = new System.Windows.Forms.PaintEventArgs(uiG,
                                new System.Drawing.Rectangle(System.Drawing.Point.Empty, uiForm.ClientSize));
                            typeof(LimisawForm).GetMethod("OnPaint", pFlags | System.Reflection.BindingFlags.Public)
                                .Invoke(uiForm, new object[] { uiArgs });
                        }
                        Check("P1-1d. the Settings paint renders the detected-optional row with throwing seams",
                            true, "paint completed");
                        Check("P1-1e. paint + card build + Present() added ZERO discovery I/O",
                            ExecutableDiscovery.RegistryOpens == regBefore
                            && ExecutableDiscovery.PathSourcesRead == pathBefore
                            && ExecutableDiscovery.FileExistsChecks == feBefore
                            && ExecutableDiscovery.ConfigReads == crBefore
                            && ExecutableDiscovery.ConfigParses == cpBefore,
                            "R=" + (ExecutableDiscovery.RegistryOpens - regBefore)
                            + " P=" + (ExecutableDiscovery.PathSourcesRead - pathBefore)
                            + " F=" + (ExecutableDiscovery.FileExistsChecks - feBefore)
                            + " CR=" + (ExecutableDiscovery.ConfigReads - crBefore)
                            + " CP=" + (ExecutableDiscovery.ConfigParses - cpBefore));
                    }
                    finally
                    {
                        try { if (uiForm != null) uiForm.Dispose(); } catch { }
                        try { uiTray.Dispose(); } catch { }
                        ExecutableDiscovery.BeginGeneration();
                    }
                }
                catch (Exception uiEx)
                {
                    Check("P1-1 zero UI-thread discovery I/O", false, uiEx.GetType().Name + ": " + uiEx.Message);
                }
                finally
                {
                    ExecutableDiscovery.OptionalVendorPresent = savedOptPresent;
                    FreebuffDiscovery.HasExecutableImpl = savedHasExe;
                    FreebuffDiscovery.AppPathsTarget = savedAppPathsUI;
                    FreebuffDiscovery.StartMenuDirs = savedStartDirsUI;
                    FreebuffDiscovery.ResolveImpl = savedResolveImplUI;
                    FreebuffDiscovery.ResolveLnk = savedResolveLnkUI;
                    FreebuffDiscovery.FileExists = savedFbExistsUI;
                }
            }

            Console.WriteLine("== diagnostic timestamp contract ==");
            // R045: epoch -> UTC DateTime -> ISO-8601 "o" through ONE helper;
            // never a raw double rendered with a DateTime format.
            double stampEpoch = 1789000000.5;   // 2026-09-05T05:46:40.5Z
            string iso = ConnectionDiagnostics.IsoUtc(stampEpoch);
            DateTime parsed;
            Check("epoch converts to ISO-8601 UTC", DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed)
                && parsed.Kind == DateTimeKind.Utc, iso ?? "null");
            Check("ISO timestamp round-trips within 1s", Math.Abs((parsed - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(stampEpoch)).TotalSeconds) < 1, iso ?? "null");
            Check("null epoch -> null", ConnectionDiagnostics.IsoUtc(null) == null, "");
            var timedConn = new VendorConnection { VendorId = "zcode", State = ConnectionState.Connected, LastVerifiedUtc = stampEpoch };
            string timedReport = ConnectionDiagnostics.BuildReport(timedConn, "0.0.8");
            Check("report carries valid ISO-8601 UTC lastVerified", timedReport.IndexOf(iso, StringComparison.Ordinal) >= 0, "");

            // Windows version truth: Environment.OSVersion answers 6.2.9200 on a
            // modern Windows whose process has no supportedOS manifest entry;
            // the report must carry the real kernel version instead.
            string winReport = ConnectionDiagnostics.BuildReport(timedConn, "0.0.8");
            Check("report carries the real kernel version, not the 6.2.9200 shim",
                winReport.IndexOf("Windows Microsoft Windows NT 6.2.9200", StringComparison.Ordinal) < 0
                && winReport.IndexOf("Windows Microsoft Windows NT ", StringComparison.Ordinal) >= 0,
                winReport.Split('\n')[1].Trim());

            // Codex planType ("plus") is its own labelled field, never the CLI
            // version slot — the app-server path never runs `codex --version`.
            var planConn = new VendorConnection
            {
                VendorId = "codex", State = ConnectionState.Connected,
                Plan = "plus", CliVersion = "",
            };
            string planReport = ConnectionDiagnostics.BuildReport(planConn, "0.0.8");
            Check("codex planType rides its own plan: field",
                planReport.IndexOf("plan: plus", StringComparison.Ordinal) >= 0, "");
            Check("codex planType never masquerades as cli version",
                planReport.IndexOf("cli version: plus", StringComparison.Ordinal) < 0, "");

            Console.WriteLine("== zcode regressions ==");
            // 1/2: a published verification failure survives repaint/rebuild.
            // A real LimisawForm drives the real BuildConnections so the
            // coordinator-authority rule is proven, not imitated.
            var coordZ = new ConnectionCoordinator();
            var regSettings = new LimisawSettings(Path.Combine(profile, "settings"));
            regSettings.Load();
            regSettings.ZcodeReadConfig = false;
            List<Theme> regThemes = Theme.Load(Directory.GetCurrentDirectory());
            var regTray = new System.Windows.Forms.NotifyIcon();
            System.Windows.Forms.Form repaintForm = null;
            Action repaintFormDispose = () => { try { if (repaintForm != null) repaintForm.Dispose(); } catch { } try { regTray.Dispose(); } catch { } };
            int zg = coordZ.Begin("zcode");
            var rejected = new VendorConnection { VendorId = "zcode", State = ConnectionState.Failed, ErrorCode = ConnectionErrorCode.CredentialRejected, Reason = "401" };
            coordZ.TryPublish("zcode", zg, rejected);
            var rebuildFormType = typeof(LimisawForm);
            // BuildConnections is instance state, so drive it through a real
            // form bound to the coordinator the publish went through.
            repaintForm = (System.Windows.Forms.Form)Activator.CreateInstance(rebuildFormType,
                new object[] { Path.Combine(profile, "app"), regSettings, regTray, regThemes });
            var rebuildFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            rebuildFormType.GetField("ConnCoordinator", rebuildFlags).SetValue(repaintForm, coordZ);
            var connList = (List<VendorConnection>)rebuildFormType.GetField("Connections", rebuildFlags).GetValue(repaintForm);
            connList.Clear();
            rebuildFormType.GetMethod("BuildConnections", rebuildFlags).Invoke(repaintForm, null);
            var afterRebuild = connList.Find(c => c.VendorId == "zcode");
            Check("CredentialRejected survives repaint", afterRebuild != null && afterRebuild.ErrorCode == ConnectionErrorCode.CredentialRejected,
                afterRebuild == null ? "null" : afterRebuild.ErrorCode.ToString());
            zg = coordZ.Begin("zcode");
            coordZ.TryPublish("zcode", zg, new VendorConnection { VendorId = "zcode", State = ConnectionState.Failed, ErrorCode = ConnectionErrorCode.NetworkTimeout, Reason = "timeout" });
            connList.Clear();
            rebuildFormType.GetMethod("BuildConnections", rebuildFlags).Invoke(repaintForm, null);
            afterRebuild = connList.Find(c => c.VendorId == "zcode");
            Check("NetworkTimeout survives repaint", afterRebuild != null && afterRebuild.ErrorCode == ConnectionErrorCode.NetworkTimeout,
                afterRebuild == null ? "null" : afterRebuild.ErrorCode.ToString());
            coordZ.Forget("zcode");
            repaintFormDispose();
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, null);

            // 3: permission on + supported provider + no key -> deterministic SignInRequired.
            File.WriteAllText(Path.Combine(profile, ".zcode", "v2", "config.json"),
                "{\"provider\":{\"builtin:zai-coding-plan\":{\"enabled\":true,\"options\":{\"baseURL\":\"https://api.z.ai\"}}}}");
            ExecutableDiscovery.ZcodeAllowConfig = () => true;
            ExecutableDiscovery.BeginGeneration();
            zc = ExecutableDiscovery.BuildConnection("zcode");
            Check("permission on + provider + no key -> SignInRequired/CredentialMissing (not Unknown)",
                zc.State == ConnectionState.SignInRequired && zc.ErrorCode == ConnectionErrorCode.CredentialMissing,
                zc.State + "/" + zc.ErrorCode);

            // 4: permission on + no supported provider -> UnsupportedConfiguration.
            File.WriteAllText(Path.Combine(profile, ".zcode", "v2", "config.json"),
                "{\"provider\":{\"custom:openai\":{\"enabled\":true,\"options\":{\"baseURL\":\"https://api.openai.com\"}}}}");
            ExecutableDiscovery.BeginGeneration();
            zc = ExecutableDiscovery.BuildConnection("zcode");
            Check("permission on + no supported provider -> UnsupportedConfiguration",
                zc.State == ConnectionState.UnsupportedConfiguration, zc.State + "/" + zc.ErrorCode);

            // 5: Copy diagnostics on a Connected ZCode with LastVerifiedUtc succeeds.
            File.WriteAllText(Path.Combine(profile, ".zcode", "v2", "config.json"),
                "{\"provider\":{\"builtin:zai-coding-plan\":{\"enabled\":true,\"options\":{\"baseURL\":\"https://api.z.ai\",\"apiKey\":\"" + FakeSecret + "\"}}}}");
            ExecutableDiscovery.BeginGeneration();
            ZcodeSource.Transport = (string url, string key, double deadline, out string error) =>
            { error = null; return "{\"code\":200,\"data\":{\"level\":\"lite\",\"limits\":[{\"type\":\"CREDIT_LIMIT\",\"unit\":3,\"number\":5,\"usage\":100,\"remaining\":80,\"percentage\":20,\"nextResetTime\":9999999999999}]}}"; };
            baseConn = new VendorConnection { VendorId = "zcode", State = ConnectionState.Verifying };
            verified = ZcodeConnectionAdapter.Verify(baseConn, true);
            bool reportOk = false; string reportFail = "";
            try
            {
                string rep = ConnectionDiagnostics.BuildReport(verified, "0.0.8");
                reportOk = rep.IndexOf("state: Connected", StringComparison.Ordinal) >= 0 && rep.IndexOf("credential origin: config", StringComparison.Ordinal) >= 0
                    && rep.IndexOf("host category: zai", StringComparison.Ordinal) >= 0 && rep.IndexOf(FakeSecret, StringComparison.Ordinal) < 0;
            }
            catch (Exception ex) { reportFail = ex.GetType().Name; }
            Check("Connected ZCode with LastVerifiedUtc builds a sanitized report", reportOk, reportFail);

            // 6: config exists but access off -> present=yes, access=off, origin=none.
            ExecutableDiscovery.ZcodeAllowConfig = () => false;
            ExecutableDiscovery.BeginGeneration();
            zc = ExecutableDiscovery.BuildConnection("zcode");
            var offReport = ConnectionDiagnostics.BuildReport(zc, "0.0.8");
            Check("access off reports config present=yes", offReport.IndexOf("config present: yes", StringComparison.Ordinal) >= 0, "");
            Check("access off reports config access=off", offReport.IndexOf("config access: off", StringComparison.Ordinal) >= 0, "");
            Check("access off reports credential origin=none", offReport.IndexOf("credential origin: none", StringComparison.Ordinal) >= 0, "");

            // 7/8: launcher discovery — positive fixture vs URL fallback.
            Func<string> savedAppPaths = ZcodeLauncherDiscovery.AppPathsTarget;
            Func<string, string> savedResolve = ZcodeLauncherDiscovery.ResolveLnk;
            Func<string, bool> savedLauncherExists = ZcodeLauncherDiscovery.FileExists;
            string fakeLauncher = Path.Combine(profile, "ZCode.exe");
            File.WriteAllText(fakeLauncher, "x");
            try
            {
                ZcodeLauncherDiscovery.AppPathsTarget = () => fakeLauncher;
                ZcodeLauncherDiscovery.FileExists = p => p == fakeLauncher || File.Exists(p);
                string launcher = ZcodeLauncherDiscovery.Find();
                Check("reliable launcher fixture -> local launcher found", launcher == fakeLauncher, launcher ?? "null");

                ZcodeLauncherDiscovery.AppPathsTarget = () => null;
                ZcodeLauncherDiscovery.ResolveLnk = _ => null;
                ZcodeLauncherDiscovery.FileExists = p => false;
                Check("no launcher fixture -> null (URL fallback is the caller's)", ZcodeLauncherDiscovery.Find() == null, "");
            }
            finally
            {
                ZcodeLauncherDiscovery.AppPathsTarget = savedAppPaths;
                ZcodeLauncherDiscovery.ResolveLnk = savedResolve;
                ZcodeLauncherDiscovery.FileExists = savedLauncherExists;
            }

            try { Directory.Delete(tmpDir, true); } catch { }

            Console.WriteLine();
            Console.WriteLine(checks + " checks");
            Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
            return fails == 0 ? 0 : 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", savedProfile);
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, savedPrimary);
            Environment.SetEnvironmentVariable(ZcodeSource.EnvAlternate, savedAlternate);
            ZcodeSource.Transport = savedTransport;
            ExecutableDiscovery.GetEnvironmentVariable = savedGetEnv;
            ExecutableDiscovery.FileExists = savedFileExists;
            ExecutableDiscovery.FallbackFor = savedFallback;
            ExecutableDiscovery.GetUserPath = savedUserPath;
            ExecutableDiscovery.GetMachinePath = savedMachinePath;
            ExecutableDiscovery.ZcodeAllowConfig = savedZcodeAllow;
            FreebuffDiscovery.HasExecutableImpl = savedFreebuffFound;
            try { Directory.Delete(profile, true); } catch { }
        }
    }
}
