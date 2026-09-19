using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using Limisaw;

// W2-010 (SRC-004:R024) + W2-001 (SRC-005): the Zcode probe's HTTP boundary.
//
// Two contracts are pinned here against a real local HttpListener driving the
// PRODUCTION transport (`ZcodeSource.Transport`, which defaults to `Get`):
//
//   * error responses are still read as "HTTP <code>" and the WebResponse is
//     owned by a using, so a 401/403/500 cannot leak its connection lease;
//   * the success body is BOUNDED — a declared Content-Length past the ceiling
//     is refused before any body is read, a chunked/unknown body is abandoned
//     the moment max+1 proves it oversized, a valid JSON prefix followed by an
//     oversized tail is never parsed as quota, and a slow trickle that keeps
//     each socket read alive cannot outlive the absolute deadline.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main ZcodeResponseTest)
public static class ZcodeResponseTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    // One scripted response. `DeclaredLength >= 0` lets a test declare a
    // Content-Length the body deliberately does not honor (the oversize
    // refusal path); `Chunked` sends without a Content-Length.
    class Resp
    {
        public int Code = 200;
        public byte[] Body = new byte[0];
        public bool Chunked;
        public long DeclaredLength = -1;
        public int ChunkSize = 8192;
        public int ChunkDelayMs = 0;
    }

    // A stream that never reaches EOF and hands back one byte per ~150ms call:
    // the only way out of ReadBoundedResponse is the absolute deadline, which
    // is exactly the invariant under test.
    class TrickleStream : Stream
    {
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return 0; } }
        public override long Position { get { return 0; } set { } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) { return 0; }
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(150);
            if (count <= 0) return 0;
            buffer[offset] = (byte)'x';
            return 1;
        }
    }

    public static int Main()
    {
        // Pick a free port by binding, reading it, and keeping the listener on it.
        var listener = new HttpListener();
        string url = "";
        for (int attempt = 0; attempt < 20; attempt++)
        {
            int port = 47000 + (Environment.TickCount % 2000) + attempt;
            url = "http://127.0.0.1:" + port + "/";
            try { listener = new HttpListener(); listener.Prefixes.Add(url); listener.Start(); break; }
            catch { listener.Close(); listener = new HttpListener(); }
        }
        if (!listener.IsListening) { Console.WriteLine("FAIL  no local port to listen on"); return 1; }

        var queue = new Queue<Resp>();
        var sync = new object();
        var worker = new Thread(() =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { return; }
                Resp spec;
                lock (sync) spec = queue.Count > 0 ? queue.Dequeue() : new Resp();
                try
                {
                    ctx.Response.StatusCode = spec.Code;
                    ctx.Response.KeepAlive = false;
                    if (spec.Chunked)
                    {
                        ctx.Response.SendChunked = true;
                        int off = 0;
                        while (off < spec.Body.Length && listener.IsListening)
                        {
                            int n = Math.Min(spec.ChunkSize, spec.Body.Length - off);
                            try { ctx.Response.OutputStream.Write(spec.Body, off, n); ctx.Response.OutputStream.Flush(); }
                            catch { break; }
                            off += n;
                            if (spec.ChunkDelayMs > 0) Thread.Sleep(spec.ChunkDelayMs);
                        }
                    }
                    else
                    {
                        long declared = spec.DeclaredLength >= 0 ? spec.DeclaredLength : spec.Body.Length;
                        ctx.Response.ContentLength64 = declared;
                        // Only a short prefix is written when the declared
                        // length is a lie; the client refuses from the header
                        // before any meaningful body arrives.
                        int toWrite = spec.DeclaredLength >= 0 ? Math.Min(spec.Body.Length, 64) : spec.Body.Length;
                        if (toWrite > 0) { try { ctx.Response.OutputStream.Write(spec.Body, 0, toWrite); ctx.Response.OutputStream.Flush(); } catch { } }
                    }
                }
                catch { }
                try { ctx.Response.Close(); } catch { }
            }
        });
        worker.IsBackground = true;
        worker.Start();

        try
        {
            // The production transport: Transport defaults to ZcodeSource.Get.
            // Each harness is a fresh process, so no test fake can remain here.
            ZcodeSource.Fetcher real = ZcodeSource.Transport;
            Check("the seam uses the production transport", real != null, "");

            // An env key so Probe-level code is never needed: this harness calls
            // the transport directly, exactly as Probe does.
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, "resp-key");

            Console.WriteLine("== the error path still answers with the vendor's own status ==");
            var codes = new[] { "401", "401", "403", "500", "500", "429" };
            var seen = new List<string>();
            foreach (string code in codes)
            {
                lock (sync) queue.Enqueue(new Resp { Code = int.Parse(code), Body = System.Text.Encoding.ASCII.GetBytes(new string('x', 4096)) });
                string error;
                string body = real(url + "api/monitor/usage/quota/limit", "resp-key", Stamp.Now + 5, out error);
                seen.Add(body == null ? error : "body");
            }
            Check("six error responses read as HTTP <code>, none as a crash",
                seen.Count == 6 && seen[0] == "HTTP 401" && seen[2] == "HTTP 403"
                && seen[3] == "HTTP 500" && seen[5] == "HTTP 429",
                string.Join(",", seen.ToArray()));

            lock (sync) queue.Enqueue(new Resp { Body = System.Text.Encoding.ASCII.GetBytes(new string('x', 4096)) });
            string okError;
            string okBody = real(url + "api/monitor/usage/quota/limit", "resp-key", Stamp.Now + 5, out okError);
            Check("a success still returns the body through the same transport",
                okBody != null && okBody.Length == 4096, "len=" + (okBody ?? "").Length);

            Console.WriteLine("== W2-001 bounded success body ==");
            // Declared Content-Length over the ceiling: refused before any body
            // read, with the machine-readable error the adapter classifies.
            lock (sync) queue.Enqueue(new Resp
            {
                Body = System.Text.Encoding.ASCII.GetBytes(new string('x', 4096)),
                DeclaredLength = (long)ZcodeSource.MaxResponseBytes + 1024,
            });
            string overErr;
            string overBody = real(url + "api/monitor/usage/quota/limit", "resp-key", Stamp.Now + 5, out overErr);
            Check("declared Content-Length over cap -> response_too_large",
                overBody == null && overErr == "response_too_large", (overBody == null ? "null" : "body") + "/" + overErr);

            // Chunked/unknown body over the ceiling: read incrementally and
            // abandoned the instant max+1 proves it oversized.
            lock (sync) queue.Enqueue(new Resp
            {
                Chunked = true,
                Body = new byte[ZcodeSource.MaxResponseBytes + 64 * 1024],
                ChunkSize = 8192,
            });
            string chunkErr;
            string chunkBody = real(url + "api/monitor/usage/quota/limit", "resp-key", Stamp.Now + 15, out chunkErr);
            Check("chunked/unknown body over cap -> response_too_large",
                chunkBody == null && chunkErr == "response_too_large", (chunkBody == null ? "null" : "body") + "/" + chunkErr);

            // A valid JSON prefix followed by an oversized tail must NEVER be
            // handed to the parser as quota truth.
            string prefix = "{\"code\":200,\"data\":{\"level\":\"lite\",\"limits\":[]}}";
            var prefixBytes = new List<byte>(System.Text.Encoding.UTF8.GetBytes(prefix));
            prefixBytes.AddRange(new byte[ZcodeSource.MaxResponseBytes + 4096]);
            lock (sync) queue.Enqueue(new Resp { Chunked = true, Body = prefixBytes.ToArray(), ChunkSize = 8192 });
            string tailErr;
            string tailBody = real(url + "api/monitor/usage/quota/limit", "resp-key", Stamp.Now + 15, out tailErr);
            Check("valid JSON prefix + oversized tail -> refused, never parsed",
                tailBody == null && tailErr == "response_too_large", (tailBody == null ? "null" : "body") + "/" + tailErr);

            // Slow trickle: each socket read succeeds, but the absolute
            // deadline (not ReadWriteTimeout) ends the operation.
            double trickleDeadline = Stamp.Now + 0.4;
            string trickleErr;
            string trickleBody = ZcodeSource.ReadBoundedResponse(new TrickleStream(), -1, ZcodeSource.MaxResponseBytes, trickleDeadline, out trickleErr);
            Check("slow trickle past the absolute deadline -> deadline_exceeded",
                trickleBody == null && trickleErr == "deadline_exceeded", (trickleBody == null ? "null" : "body") + "/" + trickleErr);

            // The connection adapter turns the transport error into the
            // deterministic troubleshooting state.
            var savedTransport = ZcodeSource.Transport;
            ZcodeSource.Transport = (string u, string k, double d, out string e) => { e = "response_too_large"; return null; };
            try
            {
                var baseConn = new VendorConnection { VendorId = "zcode", State = ConnectionState.Verifying };
                var mapped = ZcodeConnectionAdapter.Verify(baseConn, true);
                Check("adapter maps response_too_large -> ResponseTooLarge",
                    mapped.ErrorCode == ConnectionErrorCode.ResponseTooLarge, mapped.ErrorCode.ToString());
            }
            finally { ZcodeSource.Transport = savedTransport; }

            // Source guards: the shapes that cannot be observed from a black box.
            string src = File.ReadAllText(Path.Combine(SourceRoot(Directory.GetCurrentDirectory()), "ProbeZcode.cs"));
            Check("the error response is owned by a using in Get's catch",
                src.IndexOf("using (var response = ex.Response as HttpWebResponse)", StringComparison.Ordinal) >= 0, "");
            Check("the old leak shape is gone",
                src.IndexOf("var response = ex.Response as HttpWebResponse;\r\n                error", StringComparison.Ordinal) < 0
                && src.IndexOf("var response = ex.Response as HttpWebResponse;\n                error", StringComparison.Ordinal) < 0, "");
            Check("redirects stay disabled",
                src.IndexOf("request.AllowAutoRedirect = false", StringComparison.Ordinal) >= 0, "");
            Check("Get reads the success body through the bounded reader",
                src.IndexOf("ReadBoundedResponse(stream, response.ContentLength", StringComparison.Ordinal) >= 0, "");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ZcodeSource.EnvPrimary, null);
            try { listener.Close(); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " of " + checks + ")");
        return fails == 0 ? 0 : 1;
    }

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
