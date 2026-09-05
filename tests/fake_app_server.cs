using System;
using System.Threading;

// A fake `codex app-server` for tests/codex_session.cs (compiled by the
// harness at run time, never shipped). It speaks just enough JSON-RPC 2.0 over
// stdio to exercise the real RpcSession reader: every request is answered with
// {"jsonrpc":"2.0","id":N,"result":{"ok":true}}, EXCEPT a method named "slow",
// which it answers 2.5 seconds late. The slow reply is the shape a hung quota
// read needs to prove that a caller whose deadline expired discards the late
// answer instead of parking it in Responses forever.
static class FakeAppServer
{
    static void Main()
    {
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            int at = line.IndexOf("\"id\":");
            if (at < 0) continue;
            string digits = "";
            for (int i = at + 5; i < line.Length && char.IsDigit(line[i]); i++) digits += line[i];
            if (digits.Length == 0) continue;
            if (line.Contains("slow")) Thread.Sleep(2500);
            Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + digits + ",\"result\":{\"ok\":true}}");
            Console.Out.Flush();
        }
    }
}
