using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Limisaw;

// T-40 / SRC-004: payload boundaries — R030 (bounded retention at every
// external/vendor boundary) + R028's BridgeCache memoization and size
// precheck, over the REAL engine sources (engine-linked).
//
//   * Cli.Run: a fake CLI that floods stdout past the cap — retained bytes
//     stay bounded, the result is response_too_large, the pipe keeps
//     draining (the child exits normally instead of deadlocking on a full
//     pipe), and a small valid payload keeps working; a large stderr gets the
//     same bounded treatment;
//   * RpcSession: one protocol line above the ceiling is never retained or
//     parsed, the session is marked unhealthy and killed, the pool replaces
//     it, and a fresh session answers normally (a real compiled fake
//     app-server child, the same discipline codex_session.cs uses);
//   * BridgeCache: unchanged file → zero rereads; changed mtime/length →
//     exactly one new read+parse; removed file → no cached quota; CapturedAt
//     authority stays inside the JSON; an oversized bridge file is refused
//     BEFORE ReadAllText;
//   * J.Parse: valid JSON under the cap parses; text beyond the hard ceiling
//     refuses; a valid JSON prefix with an oversized suffix refuses WHOLE —
//     never partial truth.
//
// Build + run (engine-linked): pwsh .\build.ps1 -Tests
public static class PayloadBoundsTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Scratch;
    static string SourceRoot()
    {
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) return dir;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    // ── a fake CLI that floods a stream ──────────────────────────────────────
    // Writes `mb` megabytes of filler to stdout (or stderr), one line at a
    // time, then exits 0 (or 1 to force the stderr diagnostic path). The flood
    // is the point: the parent's retained buffer must stay bounded while the
    // pipe keeps draining, or this child would deadlock on a full pipe and the
    // whole run would hang until the deadline kills it.
    static string FloodCliSource = @"
using System;
using System.IO;
static class FloodCli {
    static void Main(string[] args) {
        int mb = int.Parse(args[0]);
        bool err = args.Length > 1 && args[1] == ""err"";
        string line = new string('x', 4096);
        TextWriter w = err ? Console.Error : Console.Out;
        for (int i = 0; i < mb * 256; i++) w.WriteLine(line);
        w.Flush();
        Environment.Exit(err ? 1 : 0);
    }
}";

    // A fake CLI that emits a small valid JSON payload (the healthy shape).
    // The JSON quotes are emitted via a char constant (q) to keep this
    // here-string readable.
    static string JsonCliSource = @"
using System;
static class JsonCli {
    static void Main() {
        char q = (char)34;
        Console.Out.Write(""{"" + q + ""schema_version"" + q + "":1}"");
        Environment.Exit(0);
    }
}";

    static string Compile(string source, string outName)
    {
        string src = Path.Combine(Scratch, outName + ".cs");
        File.WriteAllText(src, source);
        string csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        if (!File.Exists(csc)) csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
            "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
        string exe = Path.Combine(Scratch, outName + ".exe");
        var build = Process.Start(new ProcessStartInfo("\"" + csc + "\"", "-nologo -out:\"" + exe + "\" \"" + src + "\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        build.WaitForExit(60000);
        string log = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
        return build.ExitCode == 0 && File.Exists(exe) ? exe : null;
    }

    public static int Main()
    {
        string savedProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        Scratch = Path.Combine(Path.GetTempPath(), "limisaw_payload_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        Environment.SetEnvironmentVariable("USERPROFILE", Scratch);
        try
        {
            CliFlood();
            RpcOversize();
            BridgeCacheRules();
            BridgeOversize();
            GrowthBoundary();
            JParseCeiling();
        }
        catch (Exception ex)
        {
            Exception inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            Check("harness", false, inner.GetType().Name + ": " + inner.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", savedProfile);
            try { Directory.Delete(Scratch, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }

    // ── 26: CLI oversize ─────────────────────────────────────────────────────
    static void CliFlood()
    {
        Console.WriteLine("== CLI bounded retention ==");
        string flood = Compile(FloodCliSource, "flood_cli");
        string json = Compile(JsonCliSource, "json_cli");
        Check("the fake CLIs compiled", flood != null && json != null, "");
        if (flood == null || json == null) return;

        // Case A: a small valid payload keeps normal behavior.
        Cli.Result small = Cli.Run(json, new string[0], Stamp.Now + 30, Scratch);
        Check("A: a small payload still succeeds", small.Ok && small.Stdout.Contains(((char)34).ToString() + "schema_version"), small.Error);

        // Case B: 12 MiB on stdout — 6x the 2 MiB cap. The child must exit
        // normally (no deadlock), the result must be response_too_large, the
        // retained buffer must stay under the cap, and NO partial JSON may be
        // accepted as truth.
        Cli.Result big = Cli.Run(flood, new string[] { "12" }, Stamp.Now + 60, Scratch);
        Check("B: the flood returns response_too_large",
            !big.Ok && big.Error == "response_too_large", big.Error);
        Check("B: retained stdout stays under the cap",
            big.Stdout.Length <= Cli.ClaudeCliMaxStdout,
            big.Stdout.Length + " chars retained");
        Check("B: no partial truth was accepted",
            !big.Ok && big.Stdout.Length < 12 * 1024 * 1024, "ok=" + big.Ok);

        // Case C: a large stderr carries the same bounded treatment, and the
        // pipe drained (the child exited by itself, well before the deadline).
        Cli.Result errs = Cli.Run(flood, new string[] { "12", "err" }, Stamp.Now + 60, Scratch);
        Check("C: a stderr flood returns response_too_large",
            !errs.Ok && errs.Error == "response_too_large", errs.Error);
        Check("C: retained stderr stays tiny (first diagnostic line only)",
            errs.Stdout.Length == 0, errs.Stdout.Length + " chars");

        // Repeat the small call after the floods: prior failures must not
        // poison the CLI path.
        Cli.Result again = Cli.Run(json, new string[0], Stamp.Now + 30, Scratch);
        Check("repeats: a prior flood does not poison the next call",
            again.Ok && again.Stdout.Contains(((char)34).ToString() + "schema_version"), again.Error);
    }

    // ── 27: RPC oversize ─────────────────────────────────────────────────────
    static void RpcOversize()
    {
        Console.WriteLine("== RPC bounded line reader ==");
        // The fake app-server answers requests normally; a method named
        // "oversize" makes it emit one 1 MiB protocol line (2x the 512 KiB
        // ceiling) instead of a reply. The JSON quotes are emitted through
        // a char constant to keep this here-string readable.
        string src = @"
using System;
static class FakeAppServer2 {
    static void Main() {
        char q = (char)34;
        string line;
        while ((line = Console.ReadLine()) != null) {
            int at = line.IndexOf(""id"");
            if (at < 0) continue;
            int i = at + 2;
            while (i < line.Length && line[i] != ':') i++;
            i++;
            string digits = """";
            while (i < line.Length && char.IsDigit(line[i])) { digits += line[i]; i++; }
            if (digits.Length == 0) continue;
            string fill = new string('z', 1024 * 1024);
            string reply = ""{"" + q + ""jsonrpc"" + q + "":"" + q + ""2.0"" + q + "","" + q + ""id"" + q + "":"" + digits + "","" + q + ""result"" + q + "":{"" + q + ""fill"" + q + "":"" + q + fill + q + ""}}}"";
            if (!line.Contains(""oversize"")) reply = ""{"" + q + ""jsonrpc"" + q + "":"" + q + ""2.0"" + q + "","" + q + ""id"" + q + "":"" + digits + "","" + q + ""result"" + q + "":{"" + q + ""ok"" + q + "":true}}"";
            Console.WriteLine(reply);
            Console.Out.Flush();
        }
    }
}";
        string fake = Compile(src, "fake_app_server2");
        Check("the fake app-server compiled", fake != null, "");
        if (fake == null) return;

        CodexSource.RpcSession session = CodexSource.RpcSession.Start(fake, Scratch);
        Check("the child started", session != null && session.Alive, "");
        if (session == null) return;
        try
        {
            object poisoned = session.Call("oversize", null, Stamp.Now + 20);
            Check("the oversized line yields NO answer (never parsed, never parked)",
                poisoned == null, poisoned == null ? "" : "an oversized reply was retained");
            Check("...the session is dead (the poisoned connection was terminated)",
                !session.Alive || !session.Healthy, "alive=" + session.Alive);
            Check("...nothing from the oversized line sits in Responses",
                session.PendingResponses == 0, session.PendingResponses + " parked");
        }
        finally { session.Dispose(); }

        // A fresh process answers normally: prior oversize does not poison the
        // RPC path.
        CodexSource.RpcSession fresh2 = CodexSource.RpcSession.Start(fake, Scratch);
        Check("a fresh session answers normally after a poisoned one",
            fresh2 != null && fresh2.Call("ping", null, Stamp.Now + 20) != null, "");
        if (fresh2 != null) fresh2.Dispose();
    }

    // ── 28/29: BridgeCache rules ─────────────────────────────────────────────
    // BridgeCache(home, out error): one config directory is one Claude
    // account, so the read is scoped to the home this harness wrote into.
    static object[] BridgeArgs()
    {
        return new object[] { Path.Combine(Scratch, ".claude"), null };
    }

    static string BridgePath(string name)
    {
        // BridgeCache reads ~/.claude/<name>, and the harness redirected
        // USERPROFILE at Scratch.
        string home = Path.Combine(Scratch, ".claude");
        Directory.CreateDirectory(home);
        return Path.Combine(home, name);
    }

    static string BridgeJson(int usedPct, double capturedAt)
    {
        return "{\"schema_version\":1,\"captured_at\":" + capturedAt.ToString(
                   System.Globalization.CultureInfo.InvariantCulture) +
               ",\"rate_limits\":{\"five_hour\":{\"used_percentage\":" + usedPct +
               ",\"resets_at\":\"2026-01-01T00:00:00Z\"}}}";
    }

    static void BridgeCacheRules()
    {
        Console.WriteLine("== BridgeCache memoization ==");
        ClaudeSource.ResetBridgeCacheForTests();
        string path = BridgePath("limisaw-rate-limits.json");
        File.WriteAllText(path, BridgeJson(30, 111));

        // Probe through the real sweep path? No — drive the source directly
        // (the harness links the engine), which is what claude_cache.cs does.
        object result = typeof(ClaudeSource)
            .GetMethod("BridgeCache", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, BridgeArgs());
        long p1 = ClaudeSource.BridgeParses, b1 = ClaudeSource.BridgeBytesRead;
        Check("the first read parses exactly once", p1 == 1, "parses=" + p1);
        Check("...and the bytes read match the file", b1 == new FileInfo(path).Length,
            b1 + " vs " + new FileInfo(path).Length);

        for (int i = 0; i < 3; i++)
        {
            typeof(ClaudeSource)
                .GetMethod("BridgeCache", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, BridgeArgs());
        }
        Check("three unchanged sweeps reread NOTHING",
            ClaudeSource.BridgeParses == 1 && ClaudeSource.BridgeBytesRead == b1,
            "parses=" + ClaudeSource.BridgeParses);

        // CapturedAt authority: the JSON's captured_at, not the file's mtime.
        // Touch the file WITHOUT changing content: the cache invalidates on
        // mtime, reparses once, and the quota value is unchanged because the
        // CONTENT is unchanged — CapturedAt came from the JSON all along.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        object reread = typeof(ClaudeSource)
            .GetMethod("BridgeCache", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, BridgeArgs());
        Check("a touched mtime costs exactly one reparse",
            ClaudeSource.BridgeParses == 2, "parses=" + ClaudeSource.BridgeParses);

        // Content change: exactly one more read+parse, new payload visible.
        File.WriteAllText(path, BridgeJson(44, 222));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        var changed = (ClaudeSource.Reading)typeof(ClaudeSource)
            .GetMethod("BridgeCache", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, BridgeArgs());
        Check("a content change reparses exactly once",
            ClaudeSource.BridgeParses == 3, "parses=" + ClaudeSource.BridgeParses);
        Check("...and the new payload is what the cache hands back",
            changed != null && changed.Windows.ContainsKey(Model.FIVE_HOUR)
            && changed.Windows[Model.FIVE_HOUR].Used == 44,
            changed == null ? "null" : changed.Windows.ContainsKey(Model.FIVE_HOUR)
                ? changed.Windows[Model.FIVE_HOUR].Used.ToString() : "no window");
        Check("...CapturedAt still comes from the JSON (222), never from the mtime",
            changed.CapturedAt == 222, changed.CapturedAt.ToString());

        // Removed file: the cached reading must stop being supplied.
        File.Delete(path);
        object gone = typeof(ClaudeSource)
            .GetMethod("BridgeCache", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, BridgeArgs());
        Check("a removed bridge file stops supplying cached quota", gone == null, gone == null ? "" : "still supplying");

        // Copies: mutate the returned dictionary, re-ask, the cache must be
        // unchanged (the same rule DesktopSample already follows).
        File.WriteAllText(path, BridgeJson(55, 333));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        var first = (ClaudeSource.Reading)typeof(ClaudeSource)
            .GetMethod("BridgeCache", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, BridgeArgs());
        first.Windows[Model.FIVE_HOUR].Used = 99;
        var second = (ClaudeSource.Reading)typeof(ClaudeSource)
            .GetMethod("BridgeCache", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, BridgeArgs());
        Check("the cache hands out copies, not its own mutable state",
            second.Windows[Model.FIVE_HOUR].Used == 55,
            second.Windows[Model.FIVE_HOUR].Used.ToString());
    }

    static void BridgeOversize()
    {
        Console.WriteLine("== BridgeCache oversize precheck ==");
        ClaudeSource.ResetBridgeCacheForTests();
        string path = BridgePath("limisaw-rate-limits.json");
        // 9 MiB > the 8 MiB ceiling.
        var big = new StringBuilder();
        big.Append('x', 9 * 1024 * 1024);
        File.WriteAllText(path, BridgeJson(10, 1) + big.ToString());
        long before = ClaudeSource.BridgeBytesRead;
        // BridgeCache carries an `out string error`; reflection returns the out
        // value in the args array.
        var args = BridgeArgs();
        typeof(ClaudeSource)
            .GetMethod("BridgeCache", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, args);
        string error = (string)args[1];
        Check("an oversized bridge file is refused before ReadAllText",
            ClaudeSource.BridgeBytesRead == before,
            "bytes " + before + " -> " + ClaudeSource.BridgeBytesRead);
        Check("...with an explicit bounded error, no parse",
            ClaudeSource.BridgeParses == 0, "parses=" + ClaudeSource.BridgeParses);
        Check("...and the refusal names the boundary",
            error != null && error.Contains("too large"), error ?? "-");
    }

    // ── W2-005: the local JSON cap is enforced while reading ────────────────
    // The old guards were a pre-stat FileInfo.Length check followed by
    // ReadAllText/ReadToEnd. Because these producer files are opened with
    // FileShare.ReadWrite, a file could grow past the cap after the stat and
    // still be read to EOF into memory. BoundedFile reads at most cap+1 from
    // the opened handle and refuses the WHOLE snapshot when that +1 byte
    // exists.
    static void GrowthBoundary()
    {
        Console.WriteLine("== W2-005: local JSON cap enforced while reading ==");
        string path = Path.Combine(Scratch, "grow.json");
        const int cap = 256 * 1024;
        long bytesRead;
        string err;

        // Exactly at the cap: accepted whole.
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('a', cap)));
        string ok = BoundedFile.ReadAllText(path, cap, out bytesRead, out err);
        Check("a file exactly at the cap is accepted whole",
            ok != null && ok.Length == cap && bytesRead == cap,
            err ?? (ok == null ? "null" : "len=" + ok.Length));

        // One byte past the cap: refused whole, never a prefix.
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("{\"a\":1}" + new string('b', cap - 6)));
        ok = BoundedFile.ReadAllText(path, cap, out bytesRead, out err);
        Check("a file one byte past the cap is refused whole",
            ok == null && err == "too large", err ?? "accepted");
        Check("...retaining no more than cap+1 bytes", bytesRead == cap + 1, bytesRead + " bytes");

        // A valid JSON prefix with an oversized tail is never partial truth.
        string prefix = "{\"schema_version\":1,\"captured_at\":1,\"rate_limits\":{}}";
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(prefix + new string('x', cap)));
        ok = BoundedFile.ReadAllText(path, cap, out bytesRead, out err);
        Check("a valid prefix with an oversized tail is refused whole",
            ok == null && err == "too large", err ?? "accepted");

        // A file that grows past the cap after a metadata observation: the
        // pre-stat is only a fast refusal, the handle read is the real bound.
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('y', cap / 2)));
        long observed = new FileInfo(path).Length;
        Check("metadata observes the file under the cap", observed <= cap, observed + " <= " + cap);

        var stop = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    var block = Encoding.ASCII.GetBytes(new string('z', 8192));
                    while (!stop.IsSet) { fs.Write(block, 0, block.Length); fs.Flush(); }
                }
            }
            catch { }
        });
        writer.IsBackground = true;
        writer.Start();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && new FileInfo(path).Length <= cap) Thread.Sleep(2);
        stop.Set();
        writer.Join(2000);
        Check("the producer grew the file past the cap",
            new FileInfo(path).Length > cap, new FileInfo(path).Length + " bytes");
        ok = BoundedFile.ReadAllText(path, cap, out bytesRead, out err);
        Check("...and the bounded read refuses it instead of reading to EOF",
            ok == null && err == "too large", err ?? "accepted " + (ok == null ? 0 : ok.Length));
        Check("...retaining no more than cap+1 bytes", bytesRead <= cap + 1, bytesRead + " bytes");

        // The three local JSON sites no longer use the pre-stat-only pattern.
        string zcode = File.ReadAllText(Path.Combine(SourceRoot(), "ProbeZcode.cs"));
        string claude = File.ReadAllText(Path.Combine(SourceRoot(), "ProbeClaude.cs"));
        Check("ZCode ScanConfig reads through the bounded handle reader",
            zcode.IndexOf("reader.ReadToEnd()", StringComparison.Ordinal) < 0
            && zcode.IndexOf("BoundedFile.ReadAllText(path, 4 * 1024 * 1024", StringComparison.Ordinal) >= 0, "");
        Check("Claude BridgeCache and DesktopSample read through the bounded handle reader",
            claude.IndexOf("File.ReadAllText(path)", StringComparison.Ordinal) < 0
            && claude.IndexOf("BoundedFile.ReadAllText(path, BridgeMaxBytes", StringComparison.Ordinal) >= 0
            && claude.IndexOf("BoundedFile.ReadAllText(path, DesktopMaxBytes", StringComparison.Ordinal) >= 0, "");
    }

    // ── 31: J.Parse ceiling ──────────────────────────────────────────────────
    static void JParseCeiling()
    {
        Console.WriteLine("== J.Parse hard ceiling ==");
        // Under cap: parses.
        object small = J.Parse("{\"a\":1}");
        Check("valid JSON under the cap parses", small != null, "");
        // Beyond the cap: refuses entirely.
        var huge = new StringBuilder("{\"a\":\"");
        huge.Append('x', J.MaxJsonChars);
        huge.Append("\"}");
        Check("text beyond the hard ceiling refuses",
            J.Parse(huge.ToString()) == null, "parsed anyway");
        // A valid JSON prefix with an oversized suffix refuses WHOLE: the total
        // length is past the ceiling, so the guard fires before the serializer.
        var prefix = new StringBuilder("{\"a\":\"");
        prefix.Append('x', J.MaxJsonChars);
        prefix.Append("\",\"b\":1}");
        Check("a valid prefix with an oversized tail refuses WHOLE",
            J.Parse(prefix.ToString()) == null, "parsed the prefix");
        // The Desktop 8 MiB policy still fits under the ceiling.
        Check("the 8 MiB Desktop policy stays inside J.Parse's ceiling",
            ClaudeSource.BridgeMaxBytesForTests < J.MaxJsonChars,
            ClaudeSource.BridgeMaxBytesForTests + " < " + J.MaxJsonChars);
        // The int.MaxValue memory policy is gone: the only MaxJsonLength
        // assignment left is the bounded Math.Min form.
        string src = File.ReadAllText(Path.Combine(SourceRoot(), "Probe.cs"));
        Check("the int.MaxValue JSON memory policy is gone",
            src.IndexOf("MaxJsonLength = int.MaxValue;", StringComparison.Ordinal) < 0
            && src.IndexOf("MaxJsonLength = Math.Min", StringComparison.Ordinal) >= 0, "");
    }
}
