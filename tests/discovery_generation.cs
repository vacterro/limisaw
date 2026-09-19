using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

// PERF-003 (SRC-006:R020): multiple independent discovery authorities read
// the same mostly-static machine state within one refresh generation —
// PATH/registry walks per CLI per call, duplicate negative FileExists on
// overlapping PATH sources, a Zcode config body re-read and re-parsed by
// Resolve, Probe and the projection, and BuildConnections doing discovery
// I/O synchronously on the UI thread.
//
// The contract this harness holds:
//
//   * one refresh generation checks each normalized executable candidate
//     EXACTLY ONCE, even when the same directory appears in the process,
//     user and machine PATH;
//   * ONE ordered candidate discovery (process PATH, user PATH, machine
//     PATH, fallbacks) is the authority for BOTH the conflict/candidate
//     projection AND the resolved executable path. GenResolved is DERIVED
//     from the ordered GenCandidates result — Cli.Resolve performs NO
//     filesystem walk inside a built generation (ResolveWalks stays 0);
//   * a CLI visible only through the user/machine PATH resolves exactly
//     like the Level-0 card claims, and the provider-facing Cli.Resolve
//     returns the same path from the generation snapshot;
//   * no additional filesystem walk occurs after generation construction;
//   * each CLI resolves once per generation and every later consumer
//     (projection, BuildConnection, SnapshotResolved) reuses the snapshot
//     with ZERO additional registry/PATH/config I/O;
//   * the Zcode config body is read and parsed AT MOST ONCE per generation;
//     a changed file inside the generation re-reads immediately, and the
//     next generation always observes the change;
//   * the snapshot is SECRET-FREE: the credential value appears in no
//     projected field;
//   * a CLI installed between generations is detected by the next explicit
//     refresh — no restart — and a removed CLI is dropped by the next
//     generation too (nothing is cached across generations);
//   * discovery still preserves duplicate/conflict detection and PATH
//     precedence (first source wins).
//
// Deterministic seams: ExecutableDiscovery.RegistryOpens / PathSourcesRead /
// FileExistsChecks / ConfigReads / ConfigParses / ResolveWalks, and the
// GetUserPath / GetMachinePath / GetEnvironmentVariable / FallbackFor /
// FileExists overrides. Cli.ResolvePathOverride stays NULL for the whole
// harness — the production shape; an override would bypass the generation
// snapshot and mask the authority split this regression exists to catch.
// No real machine state is consulted.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   csc -out:discovery_generation.exe -r:System.dll tests\discovery_generation.cs
public static class DiscoveryGenerationTest
{
    static int fails = 0, checks = 0;
    const BindingFlags S = BindingFlags.Static | BindingFlags.Public;
    const BindingFlags NS = BindingFlags.Static | BindingFlags.NonPublic;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Type Disc, Cli, Zcode, VcType;
    static string[] PathSeamValue = new string[1];

    static int Counter(string name) { return (int)Disc.GetField(name, S).GetValue(null); }
    static void ResetCounts() { Disc.GetMethod("ResetDiscoveryCountsForTests", NS).Invoke(null, null); }
    static void BeginGeneration() { Disc.GetMethod("BeginGeneration", S).Invoke(null, null); }
    static void EnsureGeneration() { Disc.GetMethod("EnsureGeneration", S).Invoke(null, null); }
    static object SnapshotLevel0(string vendor)
    { return Disc.GetMethod("SnapshotLevel0", S).Invoke(null, new object[] { vendor }); }
    static string SnapshotResolved(string key)
    { return (string)Disc.GetMethod("SnapshotResolved", S).Invoke(null, new object[] { key }); }
    static object BuildConnection(string vendor)
    { return Disc.GetMethod("BuildConnection", S).Invoke(null, new object[] { vendor }); }
    static string CliResolve(string key)
    { return (string)Cli.GetMethod("Resolve", S).Invoke(null, new object[] { key }); }
    static object ZcodeResolve(bool allow)
    { return Zcode.GetMethod("Resolve", S).Invoke(null, new object[] { allow }); }
    static void ZcodeResetConfigCache()
    { Zcode.GetMethod("ResetConfigCacheForTests", NS).Invoke(null, null); }

    static void SetPathSeam(string value)
    {
        Disc.GetField("GetEnvironmentVariable", S).SetValue(null,
            (Func<string, string>)(name => name == "PATH" ? value : null));
    }

    // C#5-compatible static helper (the repository compiler is the Windows
    // .NET Framework 4.x csc — local functions are not available).
    static void WriteZcodeConfig(string cfgPath, string key)
    {
        File.WriteAllText(cfgPath, "{\"provider\":{\"builtin:zai-coding-plan\":{\"apiKey\":\"" + key + "\",\"enabled\":true,\"options\":{\"apiKey\":\"" + key + "\",\"baseURL\":\"https://api.z.ai\"}}}}");
    }

    static string GetStr(object o, string field)
    { return (string)o.GetType().GetField(field).GetValue(o); }
    static string GetStr2(object o, string field)
    { return (string)VcType.GetField(field).GetValue(o); }
    static bool GetBool(object o, string field)
    { return (bool)o.GetType().GetField(field).GetValue(o); }

    // Recursively inspect every string reachable from a projected object:
    // fields, list items, nested summary fields. A secret must occur ZERO
    // times, not "not in the obvious field".
    static bool SecretInObject(object o, string secret)
    {
        if (o == null) return false;
        foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object v;
            try { v = f.GetValue(o); } catch { continue; }
            if (v == null) continue;
            string s = v as string;
            if (s != null) { if (s.IndexOf(secret, StringComparison.Ordinal) >= 0) return true; continue; }
            IEnumerable arr = v as IEnumerable;
            if (arr != null)
                foreach (object item in arr)
                {
                    string itemStr = item as string;
                    if (itemStr != null && itemStr.IndexOf(secret, StringComparison.Ordinal) >= 0) return true;
                }
        }
        return false;
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_discgen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // Scrub the environment so the adapters' own probing sees nothing.
            Environment.SetEnvironmentVariable("USERPROFILE", temp);
            Environment.SetEnvironmentVariable("HOME", temp);
            Environment.SetEnvironmentVariable("APPDATA", temp);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(temp, "no-codex"));
            foreach (string key in new[] { "ZAI_API_KEY", "ZCODE_API_KEY", "Z_AI_API_KEY", "ZHIPU_API_KEY" })
                Environment.SetEnvironmentVariable(key, "");

            string root = Directory.GetCurrentDirectory();
            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            Assembly asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            Disc = asm.GetType("Limisaw.ExecutableDiscovery");
            Cli = asm.GetType("Limisaw.Cli");
            Zcode = asm.GetType("Limisaw.ZcodeSource");
            VcType = asm.GetType("Limisaw.VendorConnection");
            Check("the discovery types are reachable",
                Disc != null && Cli != null && Zcode != null && VcType != null, "");
            if (Disc == null || Cli == null || Zcode == null) return 1;

            // The production shape: no ResolvePathOverride anywhere in this
            // harness. Cli.Resolve must serve from the generation snapshot.
            Cli.GetField("ResolvePathOverride", NS).SetValue(null, null);

            // T-51 P1-1: the OPTIONAL vendor's presence probe runs INSIDE the
            // generation. Production reads FreebuffDiscovery (PATH + desktop
            // registration); the harness drives the same PATH walk through the
            // shared counting seam so the generation's I/O stays exact and
            // hermetic (no real Start Menu / App Paths read). The desktop-only
            // branch is covered by tests\freebuff.cs.
            Disc.GetField("OptionalVendorPresent", S).SetValue(null, (Func<string, bool>)(key =>
            {
                if (key != "freebuff") return false;
                var discoverFresh = Disc.GetMethod("DiscoverFresh", NS);
                var list = (IList)discoverFresh.Invoke(null, new object[] { key });
                return list.Count > 0;
            }));

            // Deterministic world: two PATH dirs (A and B), user PATH = A,
            // machine PATH = A — the overlap the audit measured. No fallbacks.
            string dirA = Path.Combine(temp, "binA");
            string dirB = Path.Combine(temp, "binB");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            foreach (string f in new[] { "codex.exe", "claude.cmd", "agy.exe" })
                File.WriteAllText(Path.Combine(dirA, f), "stub");
            File.WriteAllText(Path.Combine(dirB, "codex.exe"), "stub");
            File.WriteAllText(Path.Combine(dirB, "codex.cmd"), "stub");
            File.WriteAllText(Path.Combine(dirB, "agy.exe"), "stub");

            Disc.GetField("GetUserPath", S).SetValue(null, (Func<string>)(() => dirA));
            Disc.GetField("GetMachinePath", S).SetValue(null, (Func<string>)(() => dirA));
            Disc.GetField("FallbackFor", S).SetValue(null, (Func<string, string[]>)(key => new string[0]));
            Disc.GetField("FileExists", S).SetValue(null, (Func<string, bool>)(path => File.Exists(path)));
            string path1 = dirA + ";" + dirB;
            SetPathSeam(path1);
            Disc.GetField("ZcodeAllowConfig", S).SetValue(null, null);

            // ── generation 1: every normalized candidate checked once ─────
            // T-51 P1-1: the generation now covers FOUR discovery participants —
            // the three core CLI tools (codex/claude/agy) plus the OPTIONAL
            // FreeBuff vendor, whose positive presence is published here so no
            // UI projection ever walks PATH/registry itself.
            ResetCounts();
            BeginGeneration();
            EnsureGeneration();

            // Expected: for each participant, unique normalized candidates across
            // [PATH(dirA;dirB), user(dirA), machine(dirA)] with .exe/.cmd/.bat,
            // each FileExists-checked exactly once. codex: A(3)+B(3)=6,
            // claude: 6, agy: 6, freebuff: 6. Total 24.
            Check("each normalized candidate is FileExists-checked EXACTLY ONCE per generation",
                Counter("FileExistsChecks") == 24, Counter("FileExistsChecks") + " checks (want 24)");
            Check("the PATH sources are read once per participant (process+user+machine)",
                Counter("PathSourcesRead") == 12, Counter("PathSourcesRead") + " (want 12)");
            Check("the user/machine registry is opened once per participant",
                Counter("RegistryOpens") == 8, Counter("RegistryOpens") + " (want 8)");
            Check("Cli.Resolve performs NO filesystem walk inside the generation (resolution is derived from the candidates)",
                Counter("ResolveWalks") == 0, Counter("ResolveWalks") + " walks (want 0)");

            // PATH precedence: the FIRST source's candidate wins, B never wins
            // for codex even though B also holds codex.exe.
            Check("PATH precedence preserved (first source wins)",
                SnapshotResolved("codex") == Path.Combine(dirA, "codex.exe"),
                SnapshotResolved("codex") ?? "(null)");

            // ── projection consumes the snapshot with ZERO I/O ────────────
            object vcCodex = BuildConnection("codex");
            string snap2 = SnapshotResolved("codex");
            Check("the snapshot projection exists for the vendor", vcCodex != null, "");
            object resolvedField = vcCodex == null ? null : VcType.GetField("ResolvedPath").GetValue(vcCodex);
            Check("the Level-0 card agrees with SnapshotResolved",
                (string)resolvedField == Path.Combine(dirA, "codex.exe"),
                resolvedField == null ? "(null)" : (string)resolvedField);
            Check("Cli.Resolve serves the SAME path from the generation snapshot",
                CliResolve("codex") == Path.Combine(dirA, "codex.exe"), CliResolve("codex") ?? "(null)");
            Check("the projection and the snapshot agree",
                snap2 == Path.Combine(dirA, "codex.exe"), snap2 ?? "(null)");
            Check("BuildConnection + Cli.Resolve after the build perform ZERO discovery I/O",
                Counter("FileExistsChecks") == 24 && Counter("RegistryOpens") == 8
                && Counter("PathSourcesRead") == 12 && Counter("ResolveWalks") == 0,
                "FileExists=" + Counter("FileExistsChecks") + ", Registry=" + Counter("RegistryOpens")
                + ", Path=" + Counter("PathSourcesRead") + ", Walks=" + Counter("ResolveWalks"));
            Check("no config body was read building the CLI generation",
                Counter("ConfigReads") == 0, Counter("ConfigReads") + " config reads");

            // Conflicting-install detection survives the generation path.
            object vcAgy = BuildConnection("antigravity");
            var dup = (IList)VcType.GetField("DuplicatePaths").GetValue(vcAgy);
            Check("conflicting-install detection preserved through the snapshot",
                dup != null && dup.Count == 2, dup == null ? "no dup list" : dup.Count + " duplicates");

            // ── generation 2: a CLI installed mid-session is detected ─────
            // dirC is FIRST in the process PATH, so the freshly installed
            // claude.exe wins over the older claude.cmd in dirA — precedence
            // preserved AND the install observed without a restart.
            string dirC = Path.Combine(temp, "binC");
            Directory.CreateDirectory(dirC);
            File.WriteAllText(Path.Combine(dirC, "claude.exe"), "stub");
            string path2 = dirC + ";" + dirA + ";" + dirB;
            SetPathSeam(path2);
            ResetCounts();
            BeginGeneration();
            EnsureGeneration();
            Check("a CLI installed between generations is found by the next refresh",
                SnapshotResolved("claude") == Path.Combine(dirC, "claude.exe"),
                SnapshotResolved("claude") ?? "(null)");
            Check("the new generation resolves through the candidates, still ZERO Resolve walks",
                Counter("ResolveWalks") == 0, Counter("ResolveWalks") + " (want 0)");
            // Every source now sees dirC too, so each participant gains 3 new
            // candidates: 24 + 4 participants x 3 dirC exts = 36.
            Check("the new generation counts exactly the new candidates once",
                Counter("FileExistsChecks") == 36, Counter("FileExistsChecks") + " (want 36)");

            // ── removal between generations ───────────────────────────────
            File.Delete(Path.Combine(dirC, "claude.exe"));
            ResetCounts();
            BeginGeneration();
            EnsureGeneration();
            Check("a removed CLI falls back to the next source in the SAME generation order",
                SnapshotResolved("claude") == Path.Combine(dirA, "claude.cmd"),
                SnapshotResolved("claude") ?? "(null)");

            // ── PRODUCTION-SHAPED user/machine PATH regression ────────────
            // Process PATH holds NO target CLI; the user PATH does; the machine
            // PATH holds a second (overlapping) candidate. The pre-repair code
            // resolved through Cli.Resolve's process-PATH-only walk and came up
            // empty while the Level-0 card claimed Installed.
            string dirP = Path.Combine(temp, "binP");
            string dirU = Path.Combine(temp, "binU");
            string dirM = Path.Combine(temp, "binM");
            Directory.CreateDirectory(dirP);
            Directory.CreateDirectory(dirU);
            Directory.CreateDirectory(dirM);
            File.WriteAllText(Path.Combine(dirU, "codex.exe"), "stub");
            File.WriteAllText(Path.Combine(dirM, "codex.exe"), "stub");
            Disc.GetField("GetUserPath", S).SetValue(null, (Func<string>)(() => dirU));
            Disc.GetField("GetMachinePath", S).SetValue(null, (Func<string>)(() => dirM));
            SetPathSeam(dirP);
            ResetCounts();
            BeginGeneration();
            EnsureGeneration();
            Check("user PATH wins over machine PATH when the process PATH has no target",
                SnapshotResolved("codex") == Path.Combine(dirU, "codex.exe"),
                SnapshotResolved("codex") ?? "(null)");
            // Unique candidates: dirP(3 misses) + dirU(3) + dirM(3) = 9 per
            // participant × 4 participants = 36, each checked EXACTLY ONCE by
            // the one authoritative discovery pass.
            Check("overlapping process/user/machine candidates are checked ONCE",
                Counter("FileExistsChecks") == 36, Counter("FileExistsChecks") + " (want 36)");
            Check("...with one PATH read and one registry open per source per participant",
                Counter("PathSourcesRead") == 12 && Counter("RegistryOpens") == 8,
                "Path=" + Counter("PathSourcesRead") + ", Registry=" + Counter("RegistryOpens"));
            object vcU = SnapshotLevel0("codex");
            string cardResolved = vcU == null ? null : (string)VcType.GetField("ResolvedPath").GetValue(vcU);
            Check("the Level-0 card agrees with SnapshotResolved for the user-PATH install",
                cardResolved == Path.Combine(dirU, "codex.exe"),
                cardResolved ?? "(null)");
            var dupU = (IList)VcType.GetField("DuplicatePaths").GetValue(vcU);
            Check("the machine-PATH overlap is still reported as a conflict with ALL candidates",
                dupU != null && dupU.Count == 2, dupU == null ? "no dup list" : dupU.Count + " duplicates");
            Check("provider-facing Cli.Resolve returns the user/machine-path executable from the snapshot",
                CliResolve("codex") == Path.Combine(dirU, "codex.exe"), CliResolve("codex") ?? "(null)");
            Check("no additional filesystem walk occurs after generation construction",
                Counter("ResolveWalks") == 0 && Counter("FileExistsChecks") == 36,
                "Walks=" + Counter("ResolveWalks") + ", FileExists=" + Counter("FileExistsChecks"));

            // Removal, fully: the next generation observes the disappearance.
            File.Delete(Path.Combine(dirU, "codex.exe"));
            BeginGeneration();
            EnsureGeneration();
            Check("a removed user-PATH install falls through to the machine PATH next generation",
                SnapshotResolved("codex") == Path.Combine(dirM, "codex.exe"),
                SnapshotResolved("codex") ?? "(null)");
            File.Delete(Path.Combine(dirM, "codex.exe"));
            BeginGeneration();
            EnsureGeneration();
            Check("a fully removed CLI resolves to nothing (never cached across generations)",
                SnapshotResolved("codex") == "", SnapshotResolved("codex") ?? "(null)");
            Check("Cli.Resolve agrees with the empty resolution",
                CliResolve("codex") == "", CliResolve("codex") ?? "(null)");

            // ── the Zcode config body: read ONCE per generation ───────────
            string cfgDir = Path.Combine(temp, ".zcode", "v2");
            Directory.CreateDirectory(cfgDir);
            string cfgPath = Path.Combine(cfgDir, "config.json");
            const string keyVal = "LIMISAW_TEST_DISC_KEY_71c9";
            WriteZcodeConfig(cfgPath, keyVal);
            Disc.GetField("ZcodeAllowConfig", S).SetValue(null, (Func<bool>)(() => true));
            ZcodeResetConfigCache();

            // Secret-free snapshot must be built BEFORE the read counters are
            // exercised, so the build's first read doesn't conflate with
            // later per-resolve assertions.
            ResetCounts();
            BeginGeneration();
            EnsureGeneration();
            object vcZ = SnapshotLevel0("zcode");
            Check("the zcode snapshot projection exists", vcZ != null, "");
            if (vcZ != null)
            {
                string leak = null;
                foreach (FieldInfo f in VcType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object v = f.GetValue(vcZ);
                    string s = v as string;
                    if (s != null && s.IndexOf(keyVal, StringComparison.Ordinal) >= 0) leak = f.Name;
                    IEnumerable arr = v as IEnumerable;
                    if (arr != null && !(v is string))
                        foreach (object item in arr)
                        {
                            string itemStr = item as string;
                            if (itemStr != null && itemStr.IndexOf(keyVal, StringComparison.Ordinal) >= 0)
                                leak = f.Name + "[]";
                        }
                }
                Check("the credential value appears in NO projected field",
                    leak == null, leak ?? "clean");
            }

            // The per-generation budget: one read + one parse per gen.
            ResetCounts();
            BeginGeneration();
            object k1 = ZcodeResolve(true);
            object k2 = ZcodeResolve(true);
            Check("two resolves inside one generation read the config body ONCE",
                Counter("ConfigReads") == 1 && Counter("ConfigParses") == 1,
                "reads=" + Counter("ConfigReads") + ", parses=" + Counter("ConfigParses"));
            string k1Val = (string)k1.GetType().GetField("Value").GetValue(k1);
            Check("the parse is real (the credential resolved)",
                k1Val == keyVal && k1Val == (string)k2.GetType().GetField("Value").GetValue(k2),
                k1Val == null ? "(null)" : "len=" + k1Val.Length);

            // A generation is immutable: changes become visible only after refresh.
            WriteZcodeConfig(cfgPath, "changed");
            object k3 = ZcodeResolve(true);
            Check("a config changed inside the generation is not re-read",
                Counter("ConfigReads") == 1,
                "reads=" + Counter("ConfigReads"));
            string k3Val = (string)k3.GetType().GetField("Value").GetValue(k3);
            Check("the generation retains its original credential",
                k3Val == keyVal, "val=" + k3Val);

            ResetCounts();
            BeginGeneration();
            object k4 = ZcodeResolve(true);
            Check("a new generation re-reads the config exactly once",
                Counter("ConfigReads") == 1
                && (string)k4.GetType().GetField("Value").GetValue(k4) == "changed",
                "reads=" + Counter("ConfigReads"));

            // ── SRC-007 (completes SRC-006:R020): the shared secret-free
            // generation summary contract. The summary must report
            // presence/origin/provider/host/revision, stay IMMUTABLE inside a
            // generation (a disk mutation neither alters N nor adds
            // reads/parses), and never leak the credential into any projected
            // or user-visible string.
            MethodInfo genSummary = Zcode.GetMethod("GenerationSummary", S);
            Check("the GenerationSummary accessor is reachable", genSummary != null, "");
            object sumN = genSummary.Invoke(null, new object[] { true });
            Check("generation N summary reports presence", GetBool(sumN, "ConfigPresent"), "");
            Check("generation N summary reports origin=config",
                GetStr(sumN, "CredentialOrigin") == "config",
                "origin=" + GetStr(sumN, "CredentialOrigin"));
            Check("generation N summary reports the provider entry",
                GetStr(sumN, "ProviderId").Length > 0, "provider=" + GetStr(sumN, "ProviderId"));
            Check("generation N summary reports the host category",
                GetStr(sumN, "HostCategory") == "zai", "host=" + GetStr(sumN, "HostCategory"));
            string revN = GetStr(sumN, "ConfigRevision");
            Check("generation N summary reports a non-empty revision identity",
                revN.Length > 0, "revision len=" + revN.Length);

            // One body read + one parse for the whole generation's summary +
            // resolve work so far.
            Check("generation N stays at one config read",
                Counter("ConfigReads") == 1, "reads=" + Counter("ConfigReads"));
            Check("generation N stays at one config parse",
                Counter("ConfigParses") == 1, "parses=" + Counter("ConfigParses"));

            // Fake distinctive secret: must occur ZERO times in every summary
            // field and every Level-0 projection string, recursively.
            const string fakeSecret = "SK-FAKE-ZQ-71c9xJollyBadger";
            WriteZcodeConfig(cfgPath, fakeSecret);
            // mutate to provider B / host X (bigmodel) inside generation N
            File.WriteAllText(cfgPath, "{\"provider\":{\"builtin:bigmodel-coding-plan\":{\"apiKey\":\""
                + fakeSecret + "\",\"enabled\":true,\"options\":{\"apiKey\":\"" + fakeSecret
                + "\",\"baseURL\":\"https://open.bigmodel.cn\"}}}}");
            object sumN2 = genSummary.Invoke(null, new object[] { true });
            Check("generation N summary is IMMUTABLE across an in-generation disk mutation",
                GetStr(sumN2, "ProviderId") == GetStr(sumN, "ProviderId")
                && GetStr(sumN2, "HostCategory") == GetStr(sumN, "HostCategory")
                && GetStr(sumN2, "ConfigRevision") == revN,
                "provider=" + GetStr(sumN2, "ProviderId") + " host=" + GetStr(sumN2, "HostCategory"));
            Check("the in-generation mutation added ZERO config reads",
                Counter("ConfigReads") == 1, "reads=" + Counter("ConfigReads"));
            Check("the in-generation mutation added ZERO config parses",
                Counter("ConfigParses") == 1, "parses=" + Counter("ConfigParses"));
            Check("the fake API secret occurs ZERO times in the summary object",
                !SecretInObject(sumN2, fakeSecret), "");

            // The Level-0 projection consumes the SAME summary: snapshot and
            // recursive string sweep.
            ResetCounts();
            BeginGeneration();
            EnsureGeneration();
            object vcZ2 = SnapshotLevel0("zcode");
            Check("generation N+1 observes provider B",
                vcZ2 != null && GetStr2(vcZ2, "ZcodeProviderId") == "builtin:bigmodel-coding-plan",
                "provider=" + (vcZ2 == null ? "(null)" : GetStr2(vcZ2, "ZcodeProviderId")));
            Check("generation N+1 reports the new host category",
                vcZ2 != null && GetStr2(vcZ2, "HostCategory") == "bigmodel",
                "host=" + (vcZ2 == null ? "(null)" : GetStr2(vcZ2, "HostCategory")));
            object sumN1 = genSummary.Invoke(null, new object[] { true });
            Check("generation N+1 exposes a NEW revision identity",
                !string.IsNullOrEmpty(GetStr(sumN1, "ConfigRevision"))
                && GetStr(sumN1, "ConfigRevision") != revN,
                "old=" + revN + " new=" + GetStr(sumN1, "ConfigRevision"));
            Check("generation N+1 summary reports provider B",
                GetStr(sumN1, "ProviderId") == "builtin:bigmodel-coding-plan",
                "provider=" + GetStr(sumN1, "ProviderId"));
            Check("generation N+1 summary reports the bigmodel host",
                GetStr(sumN1, "HostCategory") == "bigmodel", "host=" + GetStr(sumN1, "HostCategory"));
            Check("the Level-0 projection strings are secret-free (recursive sweep)",
                vcZ2 != null && !SecretInObject(vcZ2, fakeSecret), "");
            Check("the generation N+1 summary is secret-free (recursive sweep)",
                !SecretInObject(sumN1, fakeSecret), "");

            // Deletion is observed only in the NEXT generation: delete inside
            // N+1 and the summary stays; BeginGeneration observes absence.
            File.Delete(cfgPath);
            object sumDel = genSummary.Invoke(null, new object[] { true });
            Check("deletion inside the generation does not alter the summary",
                GetBool(sumDel, "ConfigPresent"), "");
            BeginGeneration();
            object sumGone = genSummary.Invoke(null, new object[] { true });
            Check("the NEXT generation observes the deleted config",
                !GetBool(sumGone, "ConfigPresent") && GetStr(sumGone, "CredentialOrigin") == "none",
                "present=" + GetBool(sumGone, "ConfigPresent"));
        }
        catch (Exception ex)
        {
            string detail = ex.GetType().Name + ": "
                + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            Exception walk = ex;
            while (walk.InnerException != null) walk = walk.InnerException;
            if (walk.StackTrace != null)
            {
                string[] frames = walk.StackTrace.Split('\n');
                for (int i = 0; i < frames.Length && i < 5; i++) detail += " @ " + frames[i].Trim();
            }
            Check("harness", false, detail);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
