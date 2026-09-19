using System;
using System.Collections.Generic;
using System.IO;
using Limisaw;

// Zcode credential discovery + the config-access permission contract.
//
// Zcode has no CLI, so LIMISAW may only read its quota key from somewhere the
// user deliberately allowed: the environment always, Zcode's own config.json
// only under an explicit, durable Config access switch. This harness pins that
// whole surface over FIXTURE configs in a scratch profile — no real secret is
// ever present, no network is ever touched:
//
//   * provider ids are DISCOVERED inside the narrow Z.ai/BigModel family trust
//     boundary (the old fixed id list went blind the day the vendor shipped a
//     variant like builtin:zai-start-plan);
//   * the credential competes only among KEY-BEARING providers: a keyless
//     high-rank plan slot never masks a working key (VendorProviderSeen stays
//     an independent fact, true with or without a key);
//   * a Coding Plan credential outranks the generic pay-as-you-go one;
//   * a disabled provider's key is a leftover, never preferred over a live one;
//   * unrelated custom / OpenAI-compatible / third-party providers are
//     invisible to the scan no matter what they hold;
//   * a config-supplied URL is a HOST HINT for ordering only — the request can
//     never be pointed anywhere but the two constant quota hosts;
//   * every distinct failure says what it is: not detected / config access off
//     / no supported provider / no readable key;
//   * the secret is redacted out of every error, even by accident.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class ZcodeDetectionTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Profile;
    static string ConfigPath;

    // A fake key shape. Never a real credential; long enough that a redaction
    // failure would be obvious in any message it leaked into.
    const string FakeKey = "fixture-secret-key-0123456789abcdef-LIMISAW-TEST-ONLY";

    static void WriteConfig(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
        File.WriteAllText(ConfigPath, json);
    }

    static void ClearConfig()
    {
        try { Directory.Delete(Path.Combine(Profile, ".zcode"), true); } catch { }
    }

    static void Env(string name, string value)
    { Environment.SetEnvironmentVariable(name, value); }

    static string Q(string s) { return "\"" + s + "\""; }

    // One provider entry, options-shaped like the real config carries.
    static string Provider(string id, bool enabled, string baseUrl, bool withKey)
    {
        return Q(id) + ":{\"enabled\":" + (enabled ? "true" : "false")
            + ",\"options\":{\"baseURL\":" + Q(baseUrl)
            + (withKey ? ",\"apiKey\":" + Q(FakeKey) : "") + "}}";
    }

    static string Config(params string[] providers)
    {
        return "{\"provider\":{" + string.Join(",", providers) + "}}";
    }

    // ── the recorded transport ──────────────────────────────────────────────
    class Spy
    {
        public readonly List<string> Urls = new List<string>();
        public readonly List<string> Keys = new List<string>();
        public string Answer = "{}";
        public string Fail;

        public string Fetch(string url, string key, double deadline, out string error)
        {
            Urls.Add(url); Keys.Add(key);
            if (Fail != null) { error = Fail; return null; }
            error = null;
            return Answer;
        }
        public bool OnlyConstantHosts()
        {
            foreach (string url in Urls)
                if (!url.StartsWith(ZcodeSource.HostZai, StringComparison.Ordinal)
                    && !url.StartsWith(ZcodeSource.HostBigModel, StringComparison.Ordinal))
                    return false;
            return true;
        }
    }

    static Spy Install(Spy spy) { ZcodeSource.Transport = spy.Fetch; return spy; }

    public static int Main()
    {
        string savedProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        string savedPrimary = Environment.GetEnvironmentVariable(ZcodeSource.EnvPrimary);
        string savedAlternate = Environment.GetEnvironmentVariable(ZcodeSource.EnvAlternate);
        ZcodeSource.Fetcher savedTransport = ZcodeSource.Transport;
        Profile = Path.Combine(Path.GetTempPath(), "limisaw_zdet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Profile);
        Environment.SetEnvironmentVariable("USERPROFILE", Profile);
        ConfigPath = ZcodeSource.ConfigPath();
        try
        {
            Env(ZcodeSource.EnvPrimary, null);
            Env(ZcodeSource.EnvAlternate, null);

            // ── 1. no config, no env ────────────────────────────────────────
            ClearConfig();
            ZcodeSource.Key k = ZcodeSource.Resolve(false);
            Check("1. no config, no env: not detected, nothing read",
                k.Value.Length == 0 && k.State == "not-detected" && k.Refusal != null,
                k.State + " / " + (k.Refusal ?? "-"));

            // ── 2. config exists, permission false ──────────────────────────
            WriteConfig(Config(Provider("builtin:zai-coding-plan", true, "https://api.z.ai", true)));
            k = ZcodeSource.Resolve(false);
            Check("2. config exists, permission off: detected but closed, key NOT read",
                k.Value.Length == 0 && k.State == "config-denied" && !k.Refusal.Contains("sign in"),
                k.State + " / " + (k.Refusal ?? "-"));
            Check("2b. the closed refusal still names both ways in",
                k.Refusal.Contains("Settings") && k.Refusal.Contains(ZcodeSource.EnvPrimary), k.Refusal);

            // ── 3. supported Z.ai plan provider + options.apiKey ────────────
            ZcodeSource.Key ok = ZcodeSource.Resolve(true);
            Check("3. zai-coding-plan with options.apiKey is read under permission",
                ok.Value == FakeKey && ok.State == "config" && ok.Origin == "Zcode config"
                && ok.Host == ZcodeSource.HostZai,
                ok.State + " host=" + (ok.Host ?? "-") + (ok.Value.Length == 0 ? " no key" : " key ok"));

            // ── 4. supported BigModel plan provider ─────────────────────────
            WriteConfig(Config(Provider("builtin:bigmodel-coding-plan", true, "https://open.bigmodel.cn", true)));
            ok = ZcodeSource.Resolve(true);
            Check("4. bigmodel-coding-plan is read and hints its own host",
                ok.Value == FakeKey && ok.Host == ZcodeSource.HostBigModel,
                "host=" + (ok.Host ?? "-"));

            // ── 5. a legitimate family variant the old fixed list missed ────
            WriteConfig(Config(Provider("builtin:zai-start-plan", true, "https://zcode.z.ai", true)));
            ok = ZcodeSource.Resolve(true);
            Check("5. builtin:zai-start-plan (variant id) is discovered",
                ok.Value == FakeKey && ok.State == "config",
                ok.State + " / " + (ok.Value.Length > 0 ? "key read" : "nothing"));

            // ── 6. generic builtin provider, top-level apiKey ───────────────
            WriteConfig("{\"provider\":{" + Q("builtin:zai")
                + ":{\"enabled\":true,\"baseURL\":\"https://api.z.ai\",\"apiKey\":" + Q(FakeKey) + "}}}");
            ok = ZcodeSource.Resolve(true);
            Check("6. a generic builtin provider with a top-level apiKey is read",
                ok.Value == FakeKey && ok.State == "config", ok.State);

            // ── 7. third-party providers are never consumed ─────────────────
            WriteConfig(Config(
                Provider("custom:openai-proxy", true, "https://api.openai.com", true),
                Provider("builtin:openai", true, "https://api.openai.com", true)));
            ok = ZcodeSource.Resolve(true);
            Check("7. an unrelated custom provider's key is NEVER consumed",
                ok.Value.Length == 0 && ok.State == "no-provider", ok.State);

            // ── 8. malformed config ─────────────────────────────────────────
            WriteConfig("{\"provider\": { broken");
            ok = ZcodeSource.Resolve(true);
            Check("8. a malformed config is refused, not half-parsed",
                ok.Value.Length == 0 && ok.State == "no-provider", ok.State);
            Check("8b. FromConfig returns nothing for garbage",
                ZcodeSource.FromConfig(ConfigPath) == null, "-");

            // ── 9. oversized config ─────────────────────────────────────────
            var big = new System.Text.StringBuilder("{\"provider\":{\"builtin:zai\":{\"options\":{\"apiKey\":\"");
            big.Append('x', 5 * 1024 * 1024);
            big.Append("\"}}}");
            WriteConfig(big.ToString());
            ok = ZcodeSource.Resolve(true);
            Check("9. an oversized config is refused before parsing",
                ok.Value.Length == 0 && ok.State == "no-provider", ok.State);

            // ── 10. supported provider present, no readable key ─────────────
            WriteConfig(Config(Provider("builtin:zai-coding-plan", true, "https://api.z.ai", false)));
            ok = ZcodeSource.Resolve(true);
            Check("10. a supported provider with no readable apiKey says exactly that",
                ok.Value.Length == 0 && ok.State == "no-key",
                ok.State + " / " + (ok.Refusal ?? "-"));

            // ── 11. env key overrides config ────────────────────────────────
            WriteConfig(Config(Provider("builtin:zai-coding-plan", true, "https://api.z.ai", true)));
            Env(ZcodeSource.EnvPrimary, "env-wins");
            ok = ZcodeSource.Resolve(true);
            Check("11. an env key outranks the config and needs no permission",
                ok.Value == "env-wins" && ok.State == "env" && ok.Origin == "$" + ZcodeSource.EnvPrimary,
                ok.State + " / " + ok.Origin);
            Env(ZcodeSource.EnvPrimary, null);

            // ── 12. permission false proves the credential is not read ──────
            WriteConfig(Config(Provider("builtin:zai-coding-plan", true, "https://api.z.ai", true)));
            Spy spy = Install(new Spy());
            double deadline = Stamp.Now + 5;
            ProbeAccount probed = ZcodeSource.Probe(deadline, false);
            Check("12. with permission off the probe never sends anything anywhere",
                spy.Urls.Count == 0 && probed.Status == Model.UNAVAILABLE && probed.Quiet,
                "transport calls=" + spy.Urls.Count + " status=" + probed.Status);
            ZcodeSource.Transport = savedTransport;

            // ── 13. the secret never reaches an error or status ─────────────
            WriteConfig(Config(Provider("builtin:zai-coding-plan", true, "https://api.z.ai", true)));
            spy = Install(new Spy { Fail = "HTTP 401 with " + FakeKey });
            deadline = Stamp.Now + 5;
            probed = ZcodeSource.Probe(deadline, true);
            string error = probed.Error ?? "";
            Check("13a. a transport error carrying the key is redacted before it lands",
                error.IndexOf(FakeKey, StringComparison.Ordinal) < 0 && error.Length > 0,
                error.Length > 60 ? error.Substring(0, 60) : error);
            Check("13b. refusals never contain the key either",
                ZcodeSource.Resolve(true).Refusal == null, "a real key leaves no refusal");
            ZcodeSource.Key noKey = ZcodeSource.Resolve(false);
            Check("13c. the key does not appear in any refusal text",
                noKey.Refusal == null || noKey.Refusal.IndexOf(FakeKey, StringComparison.Ordinal) < 0, "-");
            ZcodeSource.Transport = savedTransport;

            // ── 14. a config URL can never redirect the credential ──────────
            // A hostile config claims its provider lives at an attacker host
            // AND names its id after the vendor. The key must still go only to
            // the two constant quota hosts, and redirects must stay off.
            WriteConfig(Config(Provider("builtin:zai-coding-plan", true, "https://evil.example.com", true)));
            spy = Install(new Spy());
            deadline = Stamp.Now + 5;
            ZcodeSource.Probe(deadline, true);
            Check("14a. every request went to a constant quota host only",
                spy.Urls.Count > 0 && spy.OnlyConstantHosts(),
                spy.Urls.Count + " call(s), hosts=" + (spy.Urls.Count > 0 ? new Uri(spy.Urls[0]).Host : "-"));
            bool evil = false;
            foreach (string url in spy.Urls)
                if (url.IndexOf("evil.example", StringComparison.Ordinal) >= 0) evil = true;
            Check("14b. the config's own URL is never a destination",
                !evil, string.Join(" ; ", spy.Urls.ToArray()));
            ZcodeSource.Transport = savedTransport;
            string source = Path.Combine(Directory.GetCurrentDirectory(), "ProbeZcode.cs");
            if (!File.Exists(source))
                source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "ProbeZcode.cs");
            source = File.ReadAllText(source);
            Check("14c. the transport refuses redirects by source",
                source.IndexOf("AllowAutoRedirect = false", StringComparison.Ordinal) >= 0, "guard");

            // ── 15. disabled/unusable providers are not preferred ───────────
            // A disabled Coding Plan key is a leftover; the live generic one wins.
            WriteConfig(Config(
                Provider("builtin:zai-coding-plan", false, "https://api.z.ai", true),
                Provider("builtin:zai", true, "https://api.z.ai", true)));
            ZcodeSource.ConfigScan scan = ZcodeSource.ScanConfig(ConfigPath);
            Check("15a. a disabled plan provider loses to an enabled generic one",
                scan.ProviderId == "builtin:zai", scan.ProviderId ?? "-");
            // ...and a live Coding Plan credential outranks the generic one.
            WriteConfig(Config(
                Provider("builtin:zai", true, "https://api.z.ai", true),
                Provider("builtin:zai-coding-plan", true, "https://api.z.ai", true)));
            scan = ZcodeSource.ScanConfig(ConfigPath);
            Check("15b. a Coding Plan key outranks the generic pay-as-you-go one",
                scan.ProviderId == "builtin:zai-coding-plan", scan.ProviderId ?? "-");
            // ...and the boundary stays narrow: an arbitrary id is invisible.
            WriteConfig(Config(
                Provider("mycorp:zai-clone", true, "https://api.z.ai", true),
                Provider("builtin:bigmodel-coding-plan", false, "https://open.bigmodel.cn", false)));
            scan = ZcodeSource.ScanConfig(ConfigPath);
            Check("15c. a vendor-named THIRD-PARTY id outside the builtin family is refused",
                scan.ProviderId == null, scan.ProviderId ?? "(nothing)");

            // ── 16. the credential competes among KEY-BEARING providers only ─
            // The rank alone must never decide: an enabled coding-plan slot
            // with NO apiKey once outranked a working generic provider and the
            // valid credential was silently dropped with it.
            //
            // CASE 1: keyless enabled coding-plan, keyed enabled generic.
            WriteConfig(Config(
                Provider("builtin:zai-coding-plan", true, "https://api.z.ai", false),
                Provider("builtin:zai", true, "https://api.z.ai", true)));
            scan = ZcodeSource.ScanConfig(ConfigPath);
            Check("16a. a keyless coding-plan never masks a keyed generic provider",
                scan.ProviderId == "builtin:zai" && scan.Key == FakeKey && scan.VendorProviderSeen,
                (scan.ProviderId ?? "-") + " vendorSeen=" + scan.VendorProviderSeen);
            ok = ZcodeSource.Resolve(true);
            Check("16a2. ...and Resolve hands over that generic credential",
                ok.Value == FakeKey && ok.State == "config", ok.State);

            // CASE 2: keyless enabled coding-plan, keyed enabled start-plan —
            // the start-plan variant is the best USABLE source.
            WriteConfig(Config(
                Provider("builtin:zai-coding-plan", true, "https://api.z.ai", false),
                Provider("builtin:zai-start-plan", true, "https://api.z.ai", true)));
            scan = ZcodeSource.ScanConfig(ConfigPath);
            Check("16b. a keyless coding-plan never masks a keyed start-plan provider",
                scan.ProviderId == "builtin:zai-start-plan" && scan.Key == FakeKey,
                scan.ProviderId ?? "-");

            // CASE 3: keyed DISABLED coding-plan, keyless enabled generic. The
            // documented fallback policy stands: enabled-with-key outranks
            // disabled-with-key whenever both exist, and a disabled provider's
            // key is used only when NO enabled provider has one — the same
            // last-resort the scan has always applied when the disabled entry
            // was the only key-bearing candidate.
            WriteConfig(Config(
                Provider("builtin:zai", true, "https://api.z.ai", false),
                Provider("builtin:zai-coding-plan", false, "https://api.z.ai", true)));
            scan = ZcodeSource.ScanConfig(ConfigPath);
            Check("16c. a disabled provider's key is the documented last-resort fallback",
                scan.ProviderId == "builtin:zai-coding-plan" && scan.Key == FakeKey,
                scan.ProviderId ?? "-");
            // ...but an enabled key-bearing provider of lower plan-rank still
            // outranks the disabled leftover (15a's shape, credential side).
            WriteConfig(Config(
                Provider("builtin:zai", true, "https://api.z.ai", true),
                Provider("builtin:zai-coding-plan", false, "https://api.z.ai", true)));
            scan = ZcodeSource.ScanConfig(ConfigPath);
            Check("16c2. an enabled generic key still beats a disabled plan key",
                scan.ProviderId == "builtin:zai", scan.ProviderId ?? "-");

            // CASE 4: trusted family providers, NONE with a key. The vendor
            // was seen (the Settings row says "provider found, no readable
            // key", never "no provider"), the key stays empty, and Resolve
            // reports the no-key state. No key material appears anywhere.
            WriteConfig(Config(
                Provider("builtin:zai-coding-plan", true, "https://api.z.ai", false),
                Provider("builtin:zai", true, "https://api.z.ai", false)));
            scan = ZcodeSource.ScanConfig(ConfigPath);
            Check("16d. keyless family providers still count as seen",
                scan.VendorProviderSeen && string.IsNullOrEmpty(scan.Key) && scan.ProviderId == null,
                "vendorSeen=" + scan.VendorProviderSeen + " id=" + (scan.ProviderId ?? "-"));
            ok = ZcodeSource.Resolve(true);
            Check("16d2. ...and Resolve says provider-found-but-no-readable-key",
                ok.Value.Length == 0 && ok.State == "no-key" && ok.Refusal != null,
                ok.State + " / " + (ok.Refusal ?? "-"));
            Check("16d3. the no-key path names no key material",
                ok.Refusal.IndexOf(FakeKey, StringComparison.Ordinal) < 0, ok.Refusal ?? "-");

            // ── the chosen provider id steers the host order, not the URL ───
            WriteConfig(Config(Provider("builtin:bigmodel-coding-plan", true, "https://open.bigmodel.cn", true)));
            spy = Install(new Spy());
            deadline = Stamp.Now + 5;
            ZcodeSource.Probe(deadline, true);
            Check("17. a bigmodel hint tries its host first but only constant hosts",
                spy.Urls.Count > 0 && spy.Urls[0].StartsWith(ZcodeSource.HostBigModel, StringComparison.Ordinal)
                && spy.OnlyConstantHosts(),
                spy.Urls.Count > 0 ? new Uri(spy.Urls[0]).Host : "-");
            ZcodeSource.Transport = savedTransport;
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
            Environment.SetEnvironmentVariable("USERPROFILE", savedProfile);
            Env(ZcodeSource.EnvPrimary, savedPrimary);
            Env(ZcodeSource.EnvAlternate, savedAlternate);
            ZcodeSource.Transport = savedTransport;
            try { Directory.Delete(Profile, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
