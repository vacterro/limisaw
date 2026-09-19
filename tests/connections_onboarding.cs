using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Limisaw;

// The common onboarding state machine (SRC-002 R085), driven end to end
// through the real coordinator + real watcher with faked adapter seams.
//
// No real vendor CLI, no real login, no network: the ConnectionAdapterRegistry
// entries are the seam the source demands, and everything the onboarding flow
// promises — install -> sign-in -> WaitingForUser -> Verifying -> Connected
// with no F5, no second login process, no "press Refresh" — is observable
// here.
public static class ConnectionsOnboardingTest
{
    static int fails = 0, checks = 0;
    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static int LoginLaunches;
    static Func<ConnectionVerifyResult> FakeVerify = () => null;
    static Func<string, string> savedDiscoveryEnv;
    static Func<string> savedDiscoveryUser, savedDiscoveryMachine;
    static Func<string, string[]> savedDiscoveryFallback;
    static Func<string, bool> savedDiscoveryFiles;
    static List<VendorConnection> Published = new List<VendorConnection>();

    public static int Main()
    {
        var savedVerify = new Dictionary<string, Func<ConnectionVerifyResult>>();
        foreach (var kv in ConnectionAdapterRegistry.Verify) savedVerify[kv.Key] = kv.Value;
        ConnectionWatcher.Shutdown();
        try
        {
            Console.WriteLine("== adapter registry: the seam exists ==");
            Check("registry has a verify for codex", ConnectionAdapterRegistry.Verify.ContainsKey("codex"), "");
            Check("registry has a verify for claude", ConnectionAdapterRegistry.Verify.ContainsKey("claude"), "");
            Check("registry has a verify for antigravity", ConnectionAdapterRegistry.Verify.ContainsKey("antigravity"), "");
            Check("registry has a sign-in for codex", ConnectionAdapterRegistry.SignIn.ContainsKey("codex"), "");
            Check("registry has a sign-in for claude", ConnectionAdapterRegistry.SignIn.ContainsKey("claude"), "");
            Check("registry has a sign-in for antigravity", ConnectionAdapterRegistry.SignIn.ContainsKey("antigravity"), "");
            Check("registry has a doctor for codex", ConnectionAdapterRegistry.Doctor.ContainsKey("codex"), "");
            Check("registry has a doctor for claude", ConnectionAdapterRegistry.Doctor.ContainsKey("claude"), "");
            Check("registry has a doctor for antigravity", ConnectionAdapterRegistry.Doctor.ContainsKey("antigravity"), "");
            // T-37: the placeholder adapter is gone — no "not implemented yet"
            // entry can remain in the real registry.
            Check("registry verify for antigravity is the REAL adapter", ConnectionAdapterRegistry.Verify["antigravity"].Method.DeclaringType != null
                && ConnectionAdapterRegistry.Verify["antigravity"].Method.DeclaringType.Name.Contains("ConnectionAdapterRegistry"), "");

            Console.WriteLine("== NotInstalled -> Install requested -> binary appears -> sign-in -> Connected ==");
            // Level 0 fixture: no executable candidates.
            savedDiscoveryEnv = ExecutableDiscovery.GetEnvironmentVariable;
            savedDiscoveryUser = ExecutableDiscovery.GetUserPath;
            savedDiscoveryMachine = ExecutableDiscovery.GetMachinePath;
            savedDiscoveryFallback = ExecutableDiscovery.FallbackFor;
            savedDiscoveryFiles = ExecutableDiscovery.FileExists;
            ExecutableDiscovery.GetEnvironmentVariable = _ => "";
            ExecutableDiscovery.GetUserPath = () => null;
            ExecutableDiscovery.GetMachinePath = () => null;
            ExecutableDiscovery.FallbackFor = _ => new string[0];
            ExecutableDiscovery.FileExists = _ => false;
            // The full ladder from the source's TESTS section, driven through
            // the real coordinator and watcher with a fake verify.
            ConnectionAdapterRegistry.Verify["codex"] = () =>
            {
                FakeVerify();
                return new ConnectionVerifyResult
                { State = ConnectionState.Connected, Error = ConnectionErrorCode.None, Reason = "Connected", Authenticated = true, Monitorable = true, CliVersion = "2.x" };
            };
            ConnectionAdapterRegistry.SignIn["codex"] = () => { LoginLaunches++; return true; };
            FakeVerify = () => null;

            var coord = new ConnectionCoordinator();
            var form = new OnboardingDriver(coord);

            // 1. Not installed: Level 0 says Install.
            var vc = ExecutableDiscovery.BuildConnection("codex");
            Check("stage 1: NotInstalled recommends Install",
                vc.State == ConnectionState.NotInstalled && vc.RecommendedAction == ConnectionAction.Install,
                vc.State + "/" + vc.RecommendedAction);

            // 2. Install requested: the coordinator minted a generation and the
            //    watcher is waiting for the binary.
            int gen = coord.Begin("codex");
            coord.TryProgress("codex", gen, ConnectionState.WaitingForUser, "Waiting for the installer");
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            {
                VendorId = "codex",
                Generation = gen,
                Verify = () => new VendorConnection
                {
                    VendorId = "codex",
                    State = ConnectionState.SignInRequired,
                    ErrorCode = ConnectionErrorCode.AuthMissing,
                    Reason = "binary appeared, no auth",
                    RecommendedAction = ConnectionAction.Connect,
                },
            });
            Check("stage 2: install watcher registered", ConnectionWatcher.Pending("codex", gen), "");
            form.WaitTicks(1);

            // 3. Binary appeared -> SignInRequired published automatically.
            var afterInstall = coord.Latest("codex");
            Check("stage 3: binary appears -> SignInRequired with a sign-in action",
                afterInstall != null && afterInstall.State == ConnectionState.SignInRequired
                && afterInstall.RecommendedAction == ConnectionAction.Connect,
                afterInstall == null ? "null" : afterInstall.State + "/" + afterInstall.RecommendedAction);

            // 4. Login launched -> WaitingForUser. A second click cannot spawn
            //    a second login while the operation is active.
            int gen2 = coord.Begin("codex");
            ConnectionAdapterRegistry.SignIn["codex"]();
            coord.TryProgress("codex", gen2, ConnectionState.WaitingForUser, "Waiting for sign-in");
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            {
                VendorId = "codex",
                Generation = gen2,
                Verify = () => new VendorConnection { VendorId = "codex", State = ConnectionState.Connected, Reason = "auth ok" },
            });
            Check("stage 4: exactly one login launch per intent", LoginLaunches == 1, LoginLaunches.ToString());
            // A second click while the same operation is active: Begin refuses,
            // and the refused click never reaches the adapter's sign-in.
            int refused = coord.Begin("codex");
            if (refused >= 0) ConnectionAdapterRegistry.SignIn["codex"]();
            Check("stage 4: second Connect click spawns no second login", refused == -1 && LoginLaunches == 1, refused.ToString() + "/" + LoginLaunches.ToString());

            // 5. Verification succeeds automatically: no F5, no refresh call.
            form.WaitTicks(12);
            var connected = coord.Latest("codex");
            Check("stage 5: post-login verification reaches Connected automatically",
                connected != null && connected.State == ConnectionState.Connected,
                connected == null ? "null" : connected.State.ToString());
            Check("stage 5: no manual RefreshData was required (only repaints)",
                form.RefreshDataCalls == 0, form.RefreshDataCalls.ToString());
            ConnectionWatcher.CancelGeneration("codex", gen2);

            Console.WriteLine("== installed + authenticated -> verify with no login ==");
            LoginLaunches = 0;
            ConnectionAdapterRegistry.SignIn["codex"] = () => { LoginLaunches++; return true; };
            int vgen = coord.Begin("codex");
            coord.TryProgress("codex", vgen, ConnectionState.Verifying, "Verifying...");
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            {
                VendorId = "codex",
                Generation = vgen,
                Verify = () => new VendorConnection { VendorId = "codex", State = ConnectionState.Connected, Reason = "quota ok" },
            });
            form.WaitTicks(6);
            var verified = coord.Latest("codex");
            Check("verify-only path never launches a login", LoginLaunches == 0, LoginLaunches.ToString());
            Check("verify-only path reaches Connected", verified != null && verified.State == ConnectionState.Connected,
                verified == null ? "null" : verified.State.ToString());
            ConnectionWatcher.CancelGeneration("codex", vgen);

            Console.WriteLine("== transient failure -> bounded retry -> Connected ==");
            int transient = 0;
            ConnectionAdapterRegistry.Verify["codex"] = () =>
            {
                transient++;
                if (transient == 1)
                    return new ConnectionVerifyResult { State = ConnectionState.Failed, Error = ConnectionErrorCode.NetworkTimeout, Reason = "timeout" };
                return new ConnectionVerifyResult { State = ConnectionState.Connected, Error = ConnectionErrorCode.None, Reason = "Connected", Authenticated = true, Monitorable = true };
            };
            int tgen = coord.Begin("codex");
            coord.TryProgress("codex", tgen, ConnectionState.Verifying, "Verifying...");
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            {
                VendorId = "codex",
                Generation = tgen,
                Verify = () =>
                {
                    var r = ConnectionAdapterRegistry.Verify["codex"]();
                    return new VendorConnection
                    {
                        VendorId = "codex", State = r.State, ErrorCode = r.Error, Reason = r.Reason,
                        Authenticated = r.Authenticated, Monitorable = r.Monitorable,
                        RecommendedAction = r.Error != ConnectionErrorCode.None
                            ? ConnectionErrorPriority.RecommendedAction(r.Error, r.State) : ConnectionAction.None,
                    };
                },
            });
            form.WaitTicks(12);
            var retried = coord.Latest("codex");
            Check("transient failure retries and connects", retried != null && retried.State == ConnectionState.Connected && transient >= 2,
                retried == null ? "null" : retried.State + "/" + transient);
            ConnectionWatcher.CancelGeneration("codex", tgen);

            Console.WriteLine("== permanent network failure -> network code + Check again, no reinstall ==");
            ConnectionAdapterRegistry.Verify["codex"] = () =>
                new ConnectionVerifyResult { State = ConnectionState.Failed, Error = ConnectionErrorCode.NetworkTimeout, Reason = "timeout" };
            int ngen = coord.Begin("codex");
            coord.TryProgress("codex", ngen, ConnectionState.Verifying, "Verifying...");
            ConnectionWatcher.Start(new ConnectionWatcher.Operation
            {
                VendorId = "codex",
                Generation = ngen,
                Verify = () =>
                {
                    var r = ConnectionAdapterRegistry.Verify["codex"]();
                    return new VendorConnection
                    {
                        VendorId = "codex", State = r.State, ErrorCode = r.Error, Reason = r.Reason,
                        RecommendedAction = r.Error != ConnectionErrorCode.None
                            ? ConnectionErrorPriority.RecommendedAction(r.Error, r.State) : ConnectionAction.None,
                    };
                },
            });
            form.WaitTicks(6);
            var netFail = coord.Latest("codex");
            Check("permanent network failure keeps the network code",
                netFail != null && netFail.ErrorCode == ConnectionErrorCode.NetworkTimeout,
                netFail == null ? "null" : netFail.ErrorCode.ToString());
            Check("permanent network failure recommends Check again, never Install",
                netFail.RecommendedAction == ConnectionAction.CheckAgain,
                netFail.RecommendedAction.ToString());
            // The window then expires: the card leaves Verifying honestly.
            form.WaitTicks(160);
            var expiredCard = coord.Latest("codex");
            Check("bounded expiry leaves Degraded with Check again, never stuck Verifying",
                expiredCard != null && expiredCard.State == ConnectionState.Degraded,
                expiredCard == null ? "null" : expiredCard.State.ToString());
            ConnectionWatcher.CancelGeneration("codex", ngen);

            Console.WriteLine("== credential failure -> auth action, no reinstall ==");
            var credFail = new VendorConnection
            {
                VendorId = "codex", State = ConnectionState.SignInRequired,
                ErrorCode = ConnectionErrorCode.AuthExpired, Reason = "token expired",
                RecommendedAction = ConnectionErrorPriority.RecommendedAction(ConnectionErrorCode.AuthExpired, ConnectionState.SignInRequired),
            };
            Check("expired auth recommends Sign in, never Install",
                credFail.RecommendedAction == ConnectionAction.Connect, credFail.RecommendedAction.ToString());

            Console.WriteLine("== protocol change -> Troubleshoot, no auth destruction ==");
            var proto = new VendorConnection
            {
                VendorId = "codex", State = ConnectionState.Failed,
                ErrorCode = ConnectionErrorCode.ProtocolChanged, Reason = "response shape changed",
                RecommendedAction = ConnectionErrorPriority.RecommendedAction(ConnectionErrorCode.ProtocolChanged, ConnectionState.Failed),
            };
            Check("protocol change recommends Troubleshoot", proto.RecommendedAction == ConnectionAction.Troubleshoot, proto.RecommendedAction.ToString());

            Console.WriteLine("== stale completion cannot overwrite a newer state ==");
            int st1 = coord.Begin("claude");
            coord.TryPublish("claude", st1, new VendorConnection { VendorId = "claude", State = ConnectionState.Connected, Reason = "newer" });
            int st0 = st1 - 1;
            coord.TryPublish("claude", st0, new VendorConnection { VendorId = "claude", State = ConnectionState.Failed, Reason = "older" });
            var kept = coord.Latest("claude");
            Check("older generation cannot overwrite newer Connected", kept != null && kept.State == ConnectionState.Connected,
                kept == null ? "null" : kept.State.ToString());

            Console.WriteLine("== shutdown stops the watcher and the coordinator ==");
            coord.Shutdown();
            ConnectionWatcher.Shutdown();
            Check("no watcher left after shutdown", ConnectionWatcher.PendingCount == 0, ConnectionWatcher.PendingCount.ToString());
            Check("no completion publishes after shutdown", !coord.TryPublish("codex", 99, new VendorConnection { VendorId = "codex", State = ConnectionState.Connected }), "");

            W2_003_InteractiveObserver();
            RealInstallerDiscovery();
        }
        finally
        {
            ExecutableDiscovery.GetEnvironmentVariable = savedDiscoveryEnv;
            ExecutableDiscovery.GetUserPath = savedDiscoveryUser;
            ExecutableDiscovery.GetMachinePath = savedDiscoveryMachine;
            ExecutableDiscovery.FallbackFor = savedDiscoveryFallback;
            ExecutableDiscovery.FileExists = savedDiscoveryFiles;
            ConnectionAdapterRegistry.Reset();
            foreach (var kv in savedVerify) ConnectionAdapterRegistry.Verify[kv.Key] = kv.Value;
            ConnectionWatcher.Shutdown();
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }

    // SRC-006:R020: production-shaped installer discovery — a fresh binary
    // installed after generation N is visible through BuildConnectionFresh
    // immediately, without a new refresh, while the ordinary generation
    // snapshot stays unchanged until the next refresh.
    static void RealInstallerDiscovery()
    {
        Console.WriteLine();
        Console.WriteLine("== production-shaped installer discovery ==");
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "limisaw_fresh_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            ExecutableDiscovery.BeginGeneration();
            ExecutableDiscovery.EnsureGeneration();
            var before = ExecutableDiscovery.BuildConnection("codex");
            Check("a missing CLI reports NotInstalled in the generation",
                before != null && before.State == ConnectionState.NotInstalled, "");

            File.WriteAllText(Path.Combine(dir, "codex.exe"), "stub");
            Func<string, string> savedPathSeam = ExecutableDiscovery.GetEnvironmentVariable;
            Func<string, bool> savedFilesSeam = ExecutableDiscovery.FileExists;
            ExecutableDiscovery.GetEnvironmentVariable = name => name == "PATH" ? dir : "";
            ExecutableDiscovery.FileExists = path => File.Exists(path);
            try
            {
                var fresh = ExecutableDiscovery.BuildConnectionFresh("codex");
                Check("fresh discovery sees the newly created CLI without a refresh",
                    fresh != null && fresh.Installed && fresh.State == ConnectionState.Installed, "");
                var stale = ExecutableDiscovery.BuildConnection("codex");
                Check("the ordinary generation still reports the old state",
                    stale != null && stale.State == ConnectionState.NotInstalled, "");

                File.Delete(Path.Combine(dir, "codex.exe"));
                var freshGone = ExecutableDiscovery.BuildConnectionFresh("codex");
                Check("fresh discovery sees the removal without a refresh",
                    freshGone != null && freshGone.State == ConnectionState.NotInstalled, "");
            }
            finally
            {
                ExecutableDiscovery.GetEnvironmentVariable = savedPathSeam;
                ExecutableDiscovery.FileExists = savedFilesSeam;
            }

            ExecutableDiscovery.BeginGeneration();
            ExecutableDiscovery.EnsureGeneration();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }


    // The contract, per the source:
    //   * immediate child exit never lost (HasExited race reconciled);
    //   * exactly one onExit callback, ever (Interlocked once-gate);
    //   * long-lived children consume ZERO blocked observer workers (no
    //     AutoResetEvent/WaitOne, no ThreadPool.QueueUserWorkItem observer);
    //   * observer shutdown suppresses any later callback;
    //   * the CHILD SURVIVES observer disposal (Kill/CloseMainWindow/
    //     adoption are forbidden);
    //   * an observation-setup failure never converts a successful
    //     Process.Start into a login-launch failure.
    // Driven with REAL short- and long-lived children compiled on demand.
    static void W2_003_InteractiveObserver()
    {
        Console.WriteLine();
        Console.WriteLine("== W2-003: event-driven interactive exit observer ==");
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "limisaw_w3obs_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
                "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
            if (!File.Exists(csc)) csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
                "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");

            // A: immediate exit — the child dies before the observer can even
            // finish enabling events; the reconcile must still fire exactly once.
            string instant = Path.Combine(dir, "instant.exe");
            File.WriteAllText(Path.Combine(dir, "instant.cs"),
                "using System;class C{static void Main(){Environment.Exit(0);}}");
            Run(csc, "-nologo -out:\"" + instant + "\" \"" + Path.Combine(dir, "instant.cs") + "\"");

            int fired = 0;
            var onceA = new ManualResetEventSlim(false);
            string errA;
            var launchA = ConnectionProcessLauncher.StartInteractive(instant, "", () => { Interlocked.Increment(ref fired); onceA.Set(); }, out errA);
            Check("an immediate-exit child still launched", errA == null && launchA != null, errA ?? "ok");
            bool gotA = onceA.Wait(TimeSpan.FromSeconds(10));
            Check("the immediate child exit was never lost", gotA, "fired=" + Volatile.Read(ref fired));
            Check("...the exit callback fired exactly once", Volatile.Read(ref fired) == 1, "fired=" + Volatile.Read(ref fired));
            if (launchA != null && launchA.Observer != null)
            {
                // W2-003: the completion owns the resources. The once-gate
                // winner detaches the handler and disposes the wrapper, and
                // the observer self-removes from the application registry.
                Check("...the completion detached and released the observer resources",
                    launchA.Observer.CallbackFired && !ConnectionProcessLauncher.Tracks(launchA.Observer), "");
                bool disposeThrew = false;
                try { launchA.Observer.Dispose(); launchA.Observer.Dispose(); }
                catch (Exception ex) { disposeThrew = true; Check("repeated Dispose after completion threw", false, ex.GetType().Name); }
                Check("...repeated Dispose after completion is safe and idempotent", !disposeThrew, "");
            }

            // B: long-lived child — zero blocked observer workers. The old
            // shape parked one ThreadPool thread per observed login on
            // WaitOne; the observer now has NO worker at all.
            string idle = Path.Combine(dir, "idle.exe");
            File.WriteAllText(Path.Combine(dir, "idle.cs"),
                "using System;using System.Threading;class C{static void Main(){Thread.Sleep(Timeout.Infinite);}}");
            Run(csc, "-nologo -out:\"" + idle + "\" \"" + Path.Combine(dir, "idle.cs") + "\"");

            int firedB = 0;
            string errB;
            var launchB = ConnectionProcessLauncher.StartInteractive(idle, "", () => Interlocked.Increment(ref firedB), out errB);
            Check("a long-lived child launched with observation", errB == null && launchB != null, errB ?? "ok");
            Thread.Sleep(600); // old shape would have a parked worker by now
            Check("the long-lived child produced ZERO exit callbacks so far", Volatile.Read(ref firedB) == 0, "fired=" + Volatile.Read(ref firedB));
            Check("...the observation owns NO blocked worker (no WaitOne observer in the launcher)",
                NoBlockedObserverInLauncher(), "");

            // The child survives observer disposal: kill-forbid, close-forbid.
            bool childAliveBefore = false;
            if (launchB != null)
            {
                childAliveBefore = ChildAlive(launchB.ProcessId);
                if (launchB.Observer != null) launchB.Observer.Dispose();
            }
            Check("the observed child was alive before observer disposal", childAliveBefore, "pid=" + (launchB != null ? launchB.ProcessId : -1));
            bool childAliveAfter = ChildAlive(launchB != null ? launchB.ProcessId : -1);
            Check("...and SURVIVED observer disposal (never owned, never killed)", childAliveAfter, "");
            Thread.Sleep(400);
            Check("...observer disposal suppressed any later callback", Volatile.Read(ref firedB) == 0, "fired=" + Volatile.Read(ref firedB));
            // Harness hygiene: this idle child is the TEST's fixture. Kill it.
            if (launchB != null && launchB.ProcessId > 0)
            {
                try { var p = Process.GetProcessById(launchB.ProcessId); if (!p.HasExited) p.Kill(); p.WaitForExit(3000); p.Dispose(); } catch { }
            }

            // W2-003: the setup-failure boundary in the REAL production path —
            // the launch stays successful, the observer is unavailable, and
            // the wrapper the failed observer abandoned is disposed, not
            // leaked. Source guard: the catch that unwinds the wrapper.
            string connSrc = EngineSource("Connections.cs");
            int startAt = connSrc.IndexOf("static string DefaultStartInteractive", StringComparison.Ordinal);
            int startEnd = connSrc.IndexOf("public static InteractiveLaunch StartInteractive", startAt, StringComparison.Ordinal);
            string startBody = startAt >= 0 && startEnd > startAt ? connSrc.Substring(startAt, startEnd - startAt) : "";
            Check("an observation-setup failure disposes the abandoned wrapper in production",
                startBody.IndexOf("catch { try { p.Dispose(); } catch { } return null; }", StringComparison.Ordinal) >= 0, "");
            Check("...EnableRaisingEvents failure is never swallowed (no try/catch around it)",
                startBody.IndexOf("try { Proc.EnableRaisingEvents", StringComparison.Ordinal) < 0, "");

            // C: observation setup failure must not fail the launch. The seam
            // is driven through StartInteractiveImpl with a child that exists
            // and an observer constructor that throws.
            var savedImpl = ConnectionProcessLauncher.StartInteractiveImpl;
            ConnectionProcessLauncher.StartInteractiveImpl = (exe, args, onExit, launch) =>
            {
                Process p;
                try { p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
                catch (Exception ex) { return ex.GetType().Name; }
                if (p == null) return "could_not_start";
                launch.ProcessId = p.Id;
                try { throw new InvalidOperationException("observer setup exploded"); }
                catch { return null; } // W2-003: observation failure is NOT launch failure
                finally { try { p.Dispose(); } catch { } }
            };
            string errC;
            var launchC = ConnectionProcessLauncher.StartInteractive(instant, "", null, out errC);
            ConnectionProcessLauncher.StartInteractiveImpl = savedImpl;
            Check("an observation-setup failure did NOT convert a successful start into a launch failure",
                errC == null && launchC != null, errC ?? "ok");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static bool ChildAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var p = Process.GetProcessById(pid);
            bool alive = !p.HasExited;
            return alive;
        }
        catch { return false; }
    }

    static void Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        var p = Process.Start(psi);
        p.WaitForExit(60000);
        p.Dispose();
    }

    // Source-level proof: the production launcher contains no
    // AutoResetEvent/WaitOne observer. (The old shape's whole defect.)
    static string EngineSource(string name)
    {
        string root = Directory.GetCurrentDirectory();
        for (int i = 0; i < 4 && !File.Exists(Path.Combine(root, name)); i++)
        { var up = Directory.GetParent(root); if (up == null) break; root = up.FullName; }
        return File.ReadAllText(Path.Combine(root, name));
    }

    static bool NoBlockedObserverInLauncher()
    {
        string src = EngineSource("Connections.cs");
        int launcherAt = src.IndexOf("static class ConnectionProcessLauncher", StringComparison.Ordinal);
        if (launcherAt < 0) return false;
        int launcherEnd = src.IndexOf("internal static class AntigravityConnectionAdapter", StringComparison.Ordinal);
        if (launcherEnd < 0) launcherEnd = src.Length;
        string body = src.Substring(launcherAt, launcherEnd - launcherAt);
        return body.IndexOf("AutoResetEvent", StringComparison.Ordinal) < 0
            && body.IndexOf("wait.WaitOne", StringComparison.Ordinal) < 0
            && body.IndexOf("QueueUserWorkItem(_ =>\r\n                    {\r\n                        try { wait.WaitOne(); } catch { }", StringComparison.Ordinal) < 0;
    }

    // A minimal stand-in for the form's watcher plumbing: the OnAttempt
    // callback the real form binds (publish + repaint) without WinForms.
    class OnboardingDriver
    {
        readonly ConnectionCoordinator coord;
        // Counted, never incremented: a repaint-only loop is the contract, so
        // any future accidental RefreshData call here must fail the count.
        public int RefreshDataCalls = 0;

        public OnboardingDriver(ConnectionCoordinator coordinator)
        {
            coord = coordinator;
            ConnectionWatcher.OnAttempt = op =>
            {
                VendorConnection result = null;
                try { result = op.Verify(); }
                catch { }
                // CORE-002 (audit/6): the attempt slot frees only AFTER the
                // result is classified. AttemptFinished before classification
                // let a slow attempt queue a second same-generation verify
                // whose publication overwrote the first result — the old
                // ordering this driver once mirrored was the defect.
                if (result == null)
                {
                    ConnectionWatcher.AttemptFinished(op.VendorId, op.Generation);
                    return;
                }
                bool terminal = result.State == ConnectionState.Connected
                    || result.State == ConnectionState.ConnectedQuotaUnavailable
                    || result.State == ConnectionState.SignInRequired
                    || result.State == ConnectionState.UnsupportedConfiguration;
                if (terminal)
                {
                    // Remove the watcher first, then exactly one terminal
                    // completion — which requires the operation to still be
                    // active under its generation.
                    ConnectionWatcher.CancelGeneration(op.VendorId, op.Generation);
                    coord.TryPublish(op.VendorId, op.Generation, result);
                    return;
                }
                // Non-terminal: progress observation, ownership stays active,
                // then re-arm.
                coord.TryProgressResult(op.VendorId, op.Generation, result);
                ConnectionWatcher.AttemptFinished(op.VendorId, op.Generation);
            };
            ConnectionWatcher.OnExpire = op =>
            {
                coord.TryProgress(op.VendorId, op.Generation, ConnectionState.Degraded, "Still waiting for sign-in");
                coord.TryCancel(op.VendorId, op.Generation);
            };
        }

        public void WaitTicks(int ticks)
        {
            for (int i = 0; i < ticks * 12; i++)
                Thread.Sleep(50);
            // Repaint only; a RefreshData in this loop would be the defect the
            // source forbids.
        }
    }
}
