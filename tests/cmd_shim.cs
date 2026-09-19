using System;
using System.IO;
using Limisaw;

public static class CmdShimTest
{
    static int fails = 0, checks = 0;
    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string ResolveCmd(string key)
    {
        return Cli.Resolve(key);
    }

    public static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "limisaw_cmdshim_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shim.cmd"), "@echo ARGS:%*\r\n@echo A1:%~1\r\n@echo A2:%~2\r\n");
            string spacedDir = Path.Combine(dir, "sp ace");
            Directory.CreateDirectory(spacedDir);
            File.WriteAllText(Path.Combine(spacedDir, "shim2.cmd"), "@echo ok\r\n");

            string oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + spacedDir + Path.PathSeparator + (oldPath ?? ""));

                string resolved = Cli.Resolve("shim");
                Check(".cmd discovered by Resolve", resolved.EndsWith("shim.cmd", StringComparison.OrdinalIgnoreCase), resolved);
                Cli.Result r = Cli.Run(resolved, new[] { "a b", "plain" }, Stamp.Now + 10, null);
                Check("batch shim runs (not structurally unlaunchable)", r.Ok, r.Error + "|" + r.Stdout.Substring(0, Math.Min(60, r.Stdout.Length)));
                Check("first arg round-trips with space", r.Stdout.Contains("a b"), r.Stdout.Replace("\r\n", "|"));
                Check("second arg round-trips", r.Stdout.Contains("plain"), r.Stdout.Replace("\r\n", "|"));

                string resolved2 = Cli.Resolve("shim2");
                Cli.Result r2 = Cli.Run(resolved2, new string[] { }, Stamp.Now + 10, null);
                Check("spaced path shim runs", r2.Ok && r2.Stdout.Contains("ok"), r2.Error + "|" + r2.Stdout.Replace("\r\n", "|"));

                Cli.Result r3 = Cli.Run(resolved, new[] { "a&b", "plain" }, Stamp.Now + 10, null);
                Check("ampersand arg is not reinterpreted as a shell operator", r3.Ok && r3.Stdout.Contains("a&b"),
                    r3.Error + "|" + (r3.Ok ? r3.Stdout.Replace("\r\n", "|") : ""));

                Check("IsBatchShim identifies .cmd", Cli.IsBatchShim(@"C:\x\foo.cmd"), "");
                Check("IsBatchShim identifies .bat", Cli.IsBatchShim(@"C:\x\foo.bat"), "");
                Check("IsBatchShim rejects .exe", !Cli.IsBatchShim(@"C:\x\foo.exe"), "");

                string inner = Cli.Quote("a b") + " " + Cli.Quote("plain");
                string shell = Cli.BatchShellArgs(resolved, inner);
                Check("BatchShellArgs wraps with /d /s /c", shell.StartsWith("/d /s /c"), shell);

                string src = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Probe.cs"));
                Check("Cli.Run routes batch through ComSpec", src.Contains("IsBatchShim") && src.Contains("ComSpec"), "");
                Check("RpcSession.Start routes batch through ComSpec", src.Contains("RpcSession.Start") && src.Contains("BatchShellArgs"), "");
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
        }
        catch (Exception ex) { fails++; Console.WriteLine("FAIL  harness threw -> " + ex); }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Console.WriteLine(); Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " of " + checks + ")");
        return fails == 0 ? 0 : 1;
    }
}
