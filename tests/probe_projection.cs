using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// CORE-001: connection-state ownership was split. Publish(ProbeResult) replaced
// the account list and called BuildConnections(), but nothing fed the fresh
// probe truth into the coordinator, so a working Codex/Claude/Antigravity quota
// read could never promote its card past Installed, and a terminal ZCode
// verification failure was discarded by the immediate repaint. This harness
// drives the REAL form: an injected sweep publishes accounts, the projection
// runs, and the resulting Connections cards are asserted.
//
//   A. fresh successful Codex read          -> Connected
//   B. fresh successful Claude read         -> Connected
//   C. fresh successful Antigravity read    -> Connected
//   D. fresh successful ZCode read          -> Connected (no manual Refresh)
//   E. authenticated but no quota window    -> ConnectedQuotaUnavailable
//   F. ZCode 401/403                        -> Failed + CredentialRejected + OpenVendor
//   G. ZCode timeout                        -> Failed + NetworkTimeout + CheckAgain
//   H. ZCode malformed/protocol             -> Failed + ProtocolChanged + Troubleshoot
//   I. ZCode missing credential             -> SignInRequired + OpenVendor
//   J. unrelated repaint/refresh            -> latest terminal result survives
//
// Build + run: pwsh .\build.ps1 -Tests
public static class ProbeProjection
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;

    static Type accType, winType, resType, formType, settingsType, coordType, connType, presentType;
    static object form, settings, coordinator;
    static int UiThread;

    static object Window(string key, int rem)
    {
        object w = Activator.CreateInstance(winType);
        winType.GetField("Key").SetValue(w, key);
        winType.GetField("Base").SetValue(w, "five_hour");
        winType.GetField("Label").SetValue(w, key);
        winType.GetField("Available").SetValue(w, true);
        winType.GetField("Rem").SetValue(w, rem);
        winType.GetField("Reset").SetValue(w, "2126-09-05T18:00:00");
        winType.GetField("DurationMinutes").SetValue(w, 300);
        return w;
    }

    static object Account(string provider, bool ok, bool reading)
    {
        object a = Activator.CreateInstance(accType);
        accType.GetField("Provider").SetValue(a, provider);
        accType.GetField("ProviderLabel").SetValue(a, provider);
        accType.GetField("Name").SetValue(a, provider + "-acct");
        accType.GetField("SourceId").SetValue(a, "home-" + provider);
        accType.GetField("Status").SetValue(a, ok ? "OK" : "ERROR");
        accType.GetField("Ok").SetValue(a, ok);
        if (reading)
        {
            IList ws = (IList)accType.GetField("Windows").GetValue(a);
            ws.Add(Window("five_hour", 42));
        }
        return a;
    }

    static object Snapshot(params object[] accounts)
    {
        object res = Activator.CreateInstance(resType);
        IList list = (IList)resType.GetField("Accounts").GetValue(res);
        foreach (object a in accounts) list.Add(a);
        return res;
    }

    static FieldInfo Field(string name) { return formType.GetField(name, NP); }
    static object Get(string name) { return Field(name).GetValue(form); }
    static bool Flag(string name) { return (bool)Get(name); }
    static void Call(string name) { formType.GetMethod(name, BindingFlags.Public | NP).Invoke(form, null); }
    static void BuildConnections() { formType.GetMethod("BuildConnections", NP).Invoke(form, null); }

    static object Conn(string vendorId)
    {
        IList conns = (IList)Get("Connections");
        foreach (object c in conns)
            if ((string)connType.GetField("VendorId").GetValue(c) == vendorId) return c;
        return null;
    }

    static string State(string vendorId)
    {
        object c = Conn(vendorId);
        return c == null ? "<missing>" : connType.GetField("State").GetValue(c).ToString();
    }

    static string Action(string vendorId)
    {
        object c = Conn(vendorId);
        return c == null ? "<missing>" : connType.GetField("RecommendedAction").GetValue(c).ToString();
    }

    static string ErrorCode(string vendorId)
    {
        object c = Conn(vendorId);
        return c == null ? "<missing>" : connType.GetField("ErrorCode").GetValue(c).ToString();
    }

    static bool Wait(Func<bool> condition, int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Application.DoEvents();
            Thread.Sleep(5);
        }
        return condition();
    }

    static object SweepResult;
    static T Sweep<T>(bool zcodeReadConfig) { return (T)SweepResult; }

    static void Publish(params object[] accounts)
    {
        SweepResult = Snapshot(accounts);
        MethodInfo sweep = typeof(ProbeProjection).GetMethod("Sweep", BindingFlags.NonPublic | BindingFlags.Static)
            .MakeGenericMethod(resType);
        Field("SweepSource").SetValue(form,
            Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(bool), resType), sweep));
        Call("RefreshData");
        Wait(() => !Flag("Refreshing"), 30000);
    }

    static object Terminal(string vendorId, string state, string errorCode, string action, string reason)
    {
        object c = Activator.CreateInstance(connType);
        connType.GetField("VendorId").SetValue(c, vendorId);
        connType.GetField("State").SetValue(c, Enum.Parse(connType.GetField("State").FieldType, state));
        connType.GetField("ErrorCode").SetValue(c, Enum.Parse(connType.GetField("ErrorCode").FieldType, errorCode));
        connType.GetField("RecommendedAction").SetValue(c, Enum.Parse(connType.GetField("RecommendedAction").FieldType, action));
        connType.GetField("Reason").SetValue(c, reason);
        return c;
    }

    static void Observe(string vendorId, object conn)
    {
        coordType.GetMethod("Observe").Invoke(coordinator, new object[] { vendorId, conn });
    }

    public static int Main()
    {
        UiThread = Thread.CurrentThread.ManagedThreadId;
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_projection_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(temp, "no-codex"));
            Environment.SetEnvironmentVariable("PATH", "");
            foreach (string key in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(key, "");

            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            Assembly asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            accType = asm.GetType("Limisaw.AccountData");
            winType = asm.GetType("Limisaw.WindowData");
            resType = asm.GetType("Limisaw.ProbeResult");
            formType = asm.GetType("Limisaw.LimisawForm");
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            coordType = asm.GetType("Limisaw.ConnectionCoordinator");
            connType = asm.GetType("Limisaw.VendorConnection");
            presentType = asm.GetType("Limisaw.ConnectionPresentation");
            Type themeType = asm.GetType("Limisaw.Theme");

            settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            object themes = themeType.GetMethod("Load", PS).Invoke(null, new object[] { root });

            using (var tray = new NotifyIcon())
            using (Form f = (Form)Activator.CreateInstance(formType,
                new object[] { temp, settings, tray, themes }))
            {
                form = f;
                coordinator = Field("ConnCoordinator").GetValue(form);
                object timer = Field("RefreshTimer").GetValue(form);
                timer.GetType().GetMethod("Stop").Invoke(timer, null);
                Wait(() => !Flag("Refreshing"), 60000);

                FreshReads();
                NoQuota();
                DiscoveryLabels();
                TerminalPersistence();
                ObserveSemantics();
            }
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    static void FreshReads()
    {
        Console.WriteLine("== A-E: a successful read promotes; an authenticated vendor with no window does not fake quota ==");
        Publish(Account("codex", true, true), Account("claude", true, true),
                Account("zcode", true, true), Account("antigravity", true, false));
        Check("A: fresh Codex read -> Connected", State("codex") == "Connected", State("codex"));
        Check("B: fresh Claude read -> Connected", State("claude") == "Connected", State("claude"));
        Check("D: fresh ZCode read -> Connected without a manual Refresh",
            State("zcode") == "Connected", State("zcode"));
        object zc = Conn("zcode");
        Check("D: the promoted card is monitorable and verified",
            (bool)connType.GetField("Monitorable").GetValue(zc)
            && (bool)connType.GetField("VerificationOk").GetValue(zc), "");
        Check("E: authenticated with no quota window -> ConnectedQuotaUnavailable",
            State("antigravity") == "ConnectedQuotaUnavailable", State("antigravity"));

        Publish(Account("antigravity", true, true));
        Check("C: a later fresh Antigravity read -> Connected", State("antigravity") == "Connected", State("antigravity"));
    }

    static void NoQuota()
    {
        Console.WriteLine();
        Console.WriteLine("== J: a failed read never downgrades a terminal result ==");
        Publish(Account("codex", false, false), Account("claude", false, false),
                Account("antigravity", false, false), Account("zcode", false, false));
        Check("J: a failed read leaves the previous results intact",
            State("codex") == "Connected" && State("claude") == "Connected"
            && State("antigravity") == "Connected" && State("zcode") == "Connected",
            "codex=" + State("codex") + ", claude=" + State("claude")
            + ", antigravity=" + State("antigravity") + ", zcode=" + State("zcode"));
    }

    static object DiscoveryConn(string[] paths, bool installed)
    {
        object c = Activator.CreateInstance(connType);
        IList list = (IList)connType.GetField("CandidatePaths").GetValue(c);
        foreach (string p in paths) list.Add(p);
        connType.GetField("Installed").SetValue(c, installed);
        return c;
    }

    static string DiscoveryLabel(object c)
    {
        return (string)presentType.GetMethod("DiscoveryLabel").Invoke(null, new object[] { c });
    }

    static void DiscoveryLabels()
    {
        Console.WriteLine();
        Console.WriteLine("== CORE-005: a duplicate install reads as a conflict, not a clean 'found' ==");
        Check("two candidate paths + Installed -> conflict",
            DiscoveryLabel(DiscoveryConn(new[] { "a", "b" }, true)) == "conflict",
            DiscoveryLabel(DiscoveryConn(new[] { "a", "b" }, true)));
        Check("two candidate paths, Installed not yet set -> conflict",
            DiscoveryLabel(DiscoveryConn(new[] { "a", "b" }, false)) == "conflict",
            DiscoveryLabel(DiscoveryConn(new[] { "a", "b" }, false)));
        Check("one candidate path + Installed -> found",
            DiscoveryLabel(DiscoveryConn(new[] { "a" }, true)) == "found",
            DiscoveryLabel(DiscoveryConn(new[] { "a" }, true)));
        Check("no candidate path -> not found",
            DiscoveryLabel(DiscoveryConn(new string[0], false)) == "not found",
            DiscoveryLabel(DiscoveryConn(new string[0], false)));
    }

    static void TerminalPersistence()
    {
        Console.WriteLine();
        Console.WriteLine("== F-I: a terminal ZCode result survives the repaint ==");

        Observe("zcode", Terminal("zcode", "Failed", "CredentialRejected", "OpenVendor", "401"));
        BuildConnections();
        Check("F: 401/403 -> Failed + CredentialRejected + OpenVendor",
            State("zcode") == "Failed" && ErrorCode("zcode") == "CredentialRejected" && Action("zcode") == "OpenVendor",
            State("zcode") + "/" + ErrorCode("zcode") + "/" + Action("zcode"));

        Observe("zcode", Terminal("zcode", "Failed", "NetworkTimeout", "CheckAgain", "timed out"));
        BuildConnections();
        Check("G: timeout -> Failed + NetworkTimeout + CheckAgain",
            State("zcode") == "Failed" && ErrorCode("zcode") == "NetworkTimeout" && Action("zcode") == "CheckAgain",
            State("zcode") + "/" + ErrorCode("zcode") + "/" + Action("zcode"));

        Observe("zcode", Terminal("zcode", "Failed", "ProtocolChanged", "Troubleshoot", "shape changed"));
        BuildConnections();
        Check("H: malformed/protocol -> Failed + ProtocolChanged + Troubleshoot",
            State("zcode") == "Failed" && ErrorCode("zcode") == "ProtocolChanged" && Action("zcode") == "Troubleshoot",
            State("zcode") + "/" + ErrorCode("zcode") + "/" + Action("zcode"));

        Observe("zcode", Terminal("zcode", "SignInRequired", "CredentialMissing", "OpenVendor", "no key"));
        BuildConnections();
        // IMP-002: the sweep's auth-rejected projection must keep Installed set.
        // A vendor that answered with a credential verdict IS installed, so a
        // first-sweep card may never read "installation: not installed". Scoped
        // to the branch itself, not a whole-file grep.
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 4 && !File.Exists(Path.Combine(root, "LIMISAW.cs")); i++)
            { var up = Directory.GetParent(root); if (up == null) break; root = up.FullName; }
            string src = File.ReadAllText(Path.Combine(root, "LIMISAW.cs"));
            int at = src.IndexOf("nvc.Reason = authReason.Length > 0", StringComparison.Ordinal);
            string branch = at >= 0 ? src.Substring(Math.Max(0, at - 1200), Math.Min(1600, src.Length - Math.Max(0, at - 1200))) : "";
            Check("the auth-rejected projection sets Installed before Observe",
                at >= 0 && branch.Contains("nvc.Installed = true;") && branch.Contains("ConnCoordinator.Observe"),
                branch.Length == 0 ? "branch not found" : "");
        }

        Check("I: missing credential -> SignInRequired + OpenVendor",
            State("zcode") == "SignInRequired" && ErrorCode("zcode") == "CredentialMissing" && Action("zcode") == "OpenVendor",
            State("zcode") + "/" + ErrorCode("zcode") + "/" + Action("zcode"));

        // J again: an unrelated refresh that reports no usable ZCode reading
        // must not erase the terminal result.
        Publish(Account("codex", true, true), Account("zcode", false, false));
        Check("J: an unrelated refresh does not erase the terminal result",
            State("zcode") == "SignInRequired" && ErrorCode("zcode") == "CredentialMissing",
            State("zcode") + "/" + ErrorCode("zcode"));
        Check("J: the successful Codex read still promotes its card",
            State("codex") == "Connected", State("codex"));
    }

    static void ObserveSemantics()
    {
        Console.WriteLine();
        Console.WriteLine("== CORE-001: Observe never clobbers an in-flight verification ==");
        coordType.GetMethod("Shutdown").Invoke(coordinator, null);

        // A fresh coordinator, so the shutdown above does not mask this.
        object coord = Activator.CreateInstance(coordType);
        object fresh = Terminal("codex", "Connected", "None", "None", "probe");
        int g1 = (int)coordType.GetMethod("Begin").Invoke(coord, new object[] { "codex" });
        bool observed = (bool)coordType.GetMethod("Observe").Invoke(coord, new object[] { "codex", fresh });
        Check("Observe refuses while a verification is in flight", !observed && g1 >= 0, "observed=" + observed);
        bool late = (bool)coordType.GetMethod("TryPublish").Invoke(coord, new object[] { "codex", g1, fresh });
        Check("...and the in-flight generation still publishes", late, "late=" + late);

        // After it settles, Observe advances the generation so a stale
        // completion can never overwrite the fresh observation.
        int g2 = (int)coordType.GetMethod("Begin").Invoke(coord, new object[] { "claude" });
        coordType.GetMethod("Cancel").Invoke(coord, new object[] { "claude" });
        object obs = Terminal("claude", "Connected", "None", "None", "probe");
        bool ok2 = (bool)coordType.GetMethod("Observe").Invoke(coord, new object[] { "claude", obs });
        bool stale = (bool)coordType.GetMethod("TryPublish").Invoke(coord, new object[] { "claude", g2, Terminal("claude", "Failed", "NetworkTimeout", "CheckAgain", "stale") });
        object kept = coordType.GetMethod("Latest").Invoke(coord, new object[] { "claude" });
        Check("Observe advances the generation so a stale completion is rejected", ok2 && !stale, "ok=" + ok2 + ", stale=" + stale);
        Check("...and the fresh observation is what Latest holds",
            kept != null && connType.GetField("State").GetValue(kept).ToString() == "Connected",
            kept == null ? "null" : connType.GetField("State").GetValue(kept).ToString());
    }
}
