using System;
using System.Threading;

// A fake `codex app-server` for tests/codex_session.cs (compiled by the
// harness at run time, never shipped). It exercises pending ownership,
// unsolicited numeric ids, late replies, reverse ordering, and child death.
static class FakeAppServer
{
    static string firstReverseId, firstReverseMethod;

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
            string method = ReadMethod(line);
            if (method == "never") continue;
            if (method == "slow") Thread.Sleep(2500);
            if (method == "noise")
                for (int i = 0; i < 4096; i++) WriteReply((1000000000 + i).ToString(), "unsolicited");
            if (method == "reverseA" || method == "reverseB")
            {
                if (firstReverseId == null)
                {
                    firstReverseId = digits;
                    firstReverseMethod = method;
                    continue;
                }
                WriteReply(digits, method); // later request answers first
                WriteReply(firstReverseId, firstReverseMethod);
                firstReverseId = firstReverseMethod = null;
                Console.Out.Flush();
                continue;
            }
            WriteReply(digits, method);
            Console.Out.Flush();
        }
    }

    static string ReadMethod(string line)
    {
        const string key = "\"method\":\"";
        int at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return "";
        int start = at + key.Length;
        int end = line.IndexOf('"', start);
        return end < 0 ? "" : line.Substring(start, end - start);
    }

    static void WriteReply(string id, string method)
    {
        Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id
            + ",\"result\":{\"ok\":true,\"method\":\"" + method + "\"}}");
    }
}
