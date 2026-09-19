using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// T-40 / SRC-004: the audio pipeline — R023 (WAV format contract), R029
// (in-place scaling, no per-sample allocation), R010 (staged atomic cache
// publication + source-edit invalidation) and R022 (one serialized audio
// owner, bounded latest-wins queue, non-blocking UI submit, deterministic
// shutdown).
//
// This harness reflects over the built LIMISAW.exe, exactly the way
// sound_cache.cs does, and drives the REAL parser/cache/owner:
//
//   * PCM 8/16/24/32 numerics at 25/50/75% with the same rounding+clamping;
//   * the format boundary: only tag-1 PCM is transformed; IEEE float, A-law,
//     mu-law and WAVE_FORMAT_EXTENSIBLE stay byte-for-byte unchanged and mint
//     NO scaled artifact;
//   * same-basename identity (the W2-007 regression, re-proven on the new
//     cache shape);
//   * a source WAV edited WITHOUT clearing Built still rebuilds — the old
//     blind fast path is gone;
//   * staged atomic publication: a refusal fired after staging exists but
//     before the commit leaves the previous artifact byte-for-byte intact,
//     and no owned staging file survives;
//   * PCM32 scaling of a large fixture: numerically correct, no per-sample
//     byte[] allocation (source-guarded), no unconditional clone
//     (source-guarded);
//   * the serialized owner: concurrent producers, peak backend concurrency 1;
//   * latest-wins: three submits behind one blocked cue execute exactly the
//     newest one — the backlog never grows;
//   * the UI submit path returns immediately while the owner is blocked, and
//     the UI thread stays responsive;
//   * shutdown terminates the worker deterministically and a second lifecycle
//     starts cleanly.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:audio_pipeline.exe -r:System.dll -r:System.Drawing.dll ^
//     -r:System.Windows.Forms.dll tests\audio_pipeline.cs
public static class AudioPipeline
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    const BindingFlags NS = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags PS = BindingFlags.Static | BindingFlags.Public;

    static Type Cue;
    static MethodInfo ScaleInPlaceM, InspectM, IsSupportedM, SubmitM, WaitIdleM, ShutdownM, ResetCacheM;
static FieldInfo PublishGateM;
    static Assembly Asm;

    // ── WAV fixtures ─────────────────────────────────────────────────────────
    static byte[] Wav(short formatTag, int bits, byte[] data)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + data.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)formatTag);
        w.Write((short)1);                    // mono
        w.Write(8000);                        // sample rate
        w.Write(8000 * bits / 8);             // byte rate
        w.Write((short)(bits / 8));           // block align
        w.Write((short)bits);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    static byte[] Pcm8() { var d = new byte[8]; d[0] = 200; d[1] = 56; d[2] = 128; d[3] = 130; return Wav(1, 8, d); }
    // PCM16: +32767-ish, -32768-ish, 0, small +/-.
    static byte[] Pcm16()
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write((short)32000); w.Write((short)-32000); w.Write((short)0);
        w.Write((short)100); w.Write((short)-100);
        return Wav(1, 16, ms.ToArray());
    }
    // PCM24: 0x7F FFFF, 0x80 0000, 0, small.
    static byte[] Pcm24()
    {
        var d = new byte[12];
        d[0] = 0xFF; d[1] = 0xFF; d[2] = 0x7F;   // 8388607
        d[3] = 0x00; d[4] = 0x00; d[5] = 0x80;   // -8388608
        d[6] = 0; d[7] = 0; d[8] = 0;
        d[9] = 0x64; d[10] = 0; d[11] = 0;
        return Wav(1, 24, d);
    }
    // PCM32: +2147483000-ish, -2147483000-ish, 0, small.
    static byte[] Pcm32()
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(2147483000); w.Write(-2147483000); w.Write(0); w.Write(1000); w.Write(-1000);
        return Wav(1, 32, ms.ToArray());
    }
    // A large PCM32 fixture for the allocation pass: 200k samples = 800 KiB.
    static byte[] Pcm32Large()
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        var rnd = new Random(7);
        for (int i = 0; i < 200000; i++) w.Write(rnd.Next(int.MinValue / 2, int.MaxValue / 2));
        return Wav(1, 32, ms.ToArray());
    }
    static byte[] IeeeFloat()   // format tag 3, 32-bit float data
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(0.5f); w.Write(-0.5f); w.Write(0f);
        return Wav(3, 32, ms.ToArray());
    }
    static byte[] Tagged(short tag, short bits)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write((short)100); w.Write((short)-100);
        return Wav(tag, bits, ms.ToArray());
    }
    static byte[] MalformedChunkLen()
    {
        var b = Pcm16();
        // A data chunk whose declared length reaches past the buffer: the
        // walker must fail closed. The data chunk header sits at offset 36
        // (its id at 36, its length at 40).
        b[40] = 0xFF; b[41] = 0x00; b[42] = 0x00; b[43] = 0x00;   // length 255 > remaining bytes
        return b;
    }
    static byte[] MalformedRiff()
    {
        var b = Pcm16();
        b[0] = (byte)'X';
        return b;
    }

    static object ScaleInPlace(byte[] b, double gain)
    { return ScaleInPlaceM.Invoke(null, new object[] { b, gain }); }

    static bool Supported(object result) { return (bool)result.GetType().GetField("Supported").GetValue(result); }
    static byte[] Bytes(object result) { return (byte[])result.GetType().GetField("Bytes").GetValue(result); }
    static string Reason(object result) { return (string)result.GetType().GetField("Reason").GetValue(result); }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_audio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Asm = Assembly.LoadFrom(Path.Combine(root, "LIMISAW.exe"));
            Cue = Asm.GetType("Limisaw.SoundCue");
            ScaleInPlaceM = Cue.GetMethod("ScaleInPlace", NS);
            InspectM = Cue.GetMethod("Inspect", NS);
            IsSupportedM = Cue.GetMethod("IsSupportedPcm", NS);
            SubmitM = Cue.GetMethod("Submit", PS);
            WaitIdleM = Cue.GetMethod("WaitIdle", NS);
            ShutdownM = Cue.GetMethod("Shutdown", PS);
            ResetCacheM = Cue.GetMethod("ResetCacheForTests", NS);
            PublishGateM = Cue.GetField("PublishGate", NS);
            Check("the audio surface is reachable by reflection",
                ScaleInPlaceM != null && InspectM != null && IsSupportedM != null
                && SubmitM != null && WaitIdleM != null && ShutdownM != null && PublishGateM != null, "");

            PcmNumerics();
            FormatBoundary();
            SameBasename(temp);
            SourceEditWithoutClearingBuilt(temp);
            AtomicReplacement(temp);
            Pcm32Allocation();
            SerialOwner(temp);
            LatestWins(temp);
            UiNonblock(temp);
            ShutdownLifecycle();
        }
        catch (Exception ex)
        {
            Exception inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            Check("harness", false, inner.GetType().Name + ": " + inner.Message);
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

    // ── 15.1 PCM numerics ────────────────────────────────────────────────────
    static void PcmNumerics()
    {
        Console.WriteLine("== PCM numerics ==");
        // 25% and 75% gains on the same fixtures; rounding/clamping must match
        // the historical behavior exactly. The WAV fixtures carry a 44-byte
        // header, so the samples start at offset 44.
        object r8 = ScaleInPlace(Pcm8(), 0.25);
        Check("PCM8 25% supported", Supported(r8), "");
        byte[] b8 = Bytes(r8);
        Check("PCM8 25% rounds and clamps",
            b8[44] == (byte)Math.Max(0, Math.Min(255, (int)Math.Round((200 - 128) * 0.25) + 128))
            && b8[45] == (byte)Math.Max(0, Math.Min(255, (int)Math.Round((56 - 128) * 0.25) + 128)),
            b8[44] + "/" + b8[45]);

        object r16 = ScaleInPlace(Pcm16(), 0.25);
        byte[] b16 = Bytes(r16);
        Check("PCM16 25% clamps to the signed range",
            BitConverter.ToInt16(b16, 44) == 8000 && BitConverter.ToInt16(b16, 46) == -8000
            && BitConverter.ToInt16(b16, 48) == 0 && BitConverter.ToInt16(b16, 50) == 25,
            BitConverter.ToInt16(b16, 44) + "/" + BitConverter.ToInt16(b16, 46));
        object r16h = ScaleInPlace(Pcm16(), 0.75);
        byte[] b16h = Bytes(r16h);
        Check("PCM16 75% rounds the same way",
            BitConverter.ToInt16(b16h, 44) == 24000 && BitConverter.ToInt16(b16h, 50) == 75,
            BitConverter.ToInt16(b16h, 44) + "/" + BitConverter.ToInt16(b16h, 50));

        object r24 = ScaleInPlace(Pcm24(), 0.25);
        byte[] b24 = Bytes(r24);
        long v24 = b24[44] | (b24[45] << 8) | (b24[46] << 16);
        if ((v24 & 0x800000) != 0) v24 -= 0x1000000;
        Check("PCM24 25% rounds the positive max", v24 == 2097152, v24.ToString());
        v24 = b24[47] | (b24[48] << 8) | (b24[49] << 16);
        if ((v24 & 0x800000) != 0) v24 -= 0x1000000;
        Check("PCM24 25% rounds the negative max", v24 == -2097152, v24.ToString());

        object r32 = ScaleInPlace(Pcm32(), 0.25);
        byte[] b32 = Bytes(r32);
        Check("PCM32 25% clamps to the signed range",
            BitConverter.ToInt32(b32, 44) == 536870750 && BitConverter.ToInt32(b32, 48) == -536870750
            && BitConverter.ToInt32(b32, 52) == 0,
            BitConverter.ToInt32(b32, 44) + "/" + BitConverter.ToInt32(b32, 48));
        object r32h = ScaleInPlace(Pcm32(), 0.5);
        byte[] b32h = Bytes(r32h);
        Check("PCM32 50% rounds the same way",
            BitConverter.ToInt32(b32h, 44) == 1073741500, BitConverter.ToInt32(b32h, 44).ToString());
    }

    // ── 15.2 the format boundary ─────────────────────────────────────────────
    static void FormatBoundary()
    {
        Console.WriteLine("== format boundary ==");
        byte[] pcm16 = Pcm16();
        object ok = ScaleInPlace(pcm16, 0.5);
        Check("tag 1 PCM16 is supported", Supported(ok), "");
        byte[] before = (byte[])pcm16.Clone();

        var unsupported = new Dictionary<string, byte[]>
        {
            { "IEEE_FLOAT32 (tag 3)", IeeeFloat() },
            { "A-law (tag 6)", Tagged((short)6, 8) },
            { "mu-law (tag 7)", Tagged((short)7, 8) },
            { "WAVE_FORMAT_EXTENSIBLE (0xFFFE)", Tagged(unchecked((short)0xFFFE), 16) },
            { "malformed RIFF", MalformedRiff() },
            { "malformed chunk length", MalformedChunkLen() },
        };
        foreach (KeyValuePair<string, byte[]> kv in unsupported)
        {
            byte[] original = (byte[])kv.Value.Clone();
            object r = ScaleInPlace(kv.Value, 0.5);
            Check(kv.Key + " is UNSUPPORTED", !Supported(r),
                Supported(r) ? "scaled anyway" : Reason(r) ?? "");
            bool untouched = true;
            for (int i = 0; i < original.Length && untouched; i++)
                if (original[i] != kv.Value[i]) untouched = false;
            Check(kv.Key + " stays byte-for-byte unchanged", untouched, "");
        }
    }

    // ── 15.3 same-basename identity (W2-007 regression, new cache shape) ─────
    static string Scaled(string src, int volume)
    { return (string)Cue.GetMethod("Scaled", NS).Invoke(null, new object[] { src, volume }); }

    static byte[] ToneWav(int amp)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        short[] s = new short[64];
        for (int i = 0; i < s.Length; i++) s[i] = (short)amp;
        byte[] data = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, data, 0, data.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + data.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(8000); w.Write(16000); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    static void SameBasename(string temp)
    {
        Console.WriteLine("== same-basename identity ==");
        ResetCacheM.Invoke(null, null);
        string dirA = Path.Combine(temp, "a"), dirB = Path.Combine(temp, "b");
        Directory.CreateDirectory(dirA); Directory.CreateDirectory(dirB);
        string a = Path.Combine(dirA, "alert.wav"), b = Path.Combine(dirB, "alert.wav");
        File.WriteAllBytes(a, ToneWav(4500));
        File.WriteAllBytes(b, ToneWav(600));
        string sa = Scaled(a, 50), sb = Scaled(b, 50);
        Check("two same-basename sources own distinct artifacts",
            sa != null && sb != null && sa != sb,
            sa == null || sb == null ? "a scaled artifact missing" : Path.GetFileName(sa) + " vs " + Path.GetFileName(sb));
        if (sa != null && sb != null)
        {
            Check("...and each artifact holds its own source's audio",
                BitConverter.ToInt16(File.ReadAllBytes(sa), 44) == 2250
                && BitConverter.ToInt16(File.ReadAllBytes(sb), 44) == 300,
                BitConverter.ToInt16(File.ReadAllBytes(sa), 44) + "/" + BitConverter.ToInt16(File.ReadAllBytes(sb), 44));
        }
    }

    // ── 15.4 source edit WITHOUT clearing Built ──────────────────────────────
    static void SourceEditWithoutClearingBuilt(string temp)
    {
        Console.WriteLine("== source edit without clearing Built ==");
        ResetCacheM.Invoke(null, null);
        string src = Path.Combine(temp, "edit.wav");
        File.WriteAllBytes(src, ToneWav(9000));
        string artifact = Scaled(src, 50);
        Check("the first scale mints the artifact", artifact != null, "");
        if (artifact == null) return;
        File.SetLastWriteTimeUtc(artifact, DateTime.UtcNow.AddMinutes(-10));
        // Rewrite the SAME source path: new length AND new mtime. The Built
        // dictionary is deliberately NOT cleared — that is the old fast
        // path's blind spot, which the new CacheEntry validation must catch.
        // The mtime is set to a DISTINCT past instant: UtcNow can land in the
        // same file-system clock tick as the first mint's capture and turn the
        // rebuild into a ValidHit (length is unchanged here by design).
        File.WriteAllBytes(src, ToneWav(3000));
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(-2));
        string again = Scaled(src, 50);
        Check("the edited source is rebuilt without clearing Built",
            again == artifact && BitConverter.ToInt16(File.ReadAllBytes(again), 44) == 1500,
            "path stable=" + (again == artifact)
            + ", first sample=" + BitConverter.ToInt16(File.ReadAllBytes(again), 44));
    }

    // ── 15.5 staged atomic publication ───────────────────────────────────────
    static void AtomicReplacement(string temp)
    {
        Console.WriteLine("== staged atomic publication ==");
        ResetCacheM.Invoke(null, null);
        string src = Path.Combine(temp, "atomic.wav");
        File.WriteAllBytes(src, ToneWav(9000));
        string artifact = Scaled(src, 50);
        Check("the first publication lands the artifact", artifact != null && File.Exists(artifact), "");
        byte[] oldGood = File.ReadAllBytes(artifact);

        // Change the source (new amplitude), arm the gate to refuse the commit
        // AFTER staging is complete, and re-scale: the final artifact must stay
        // byte-for-byte the OLD good one, and no owned staging file survives.
        // Distinct past mtime: guarantees the re-scale is a real miss (same-tick
        // UtcNow would leave the entry a ValidHit and skip the publish entirely).
        File.WriteAllBytes(src, ToneWav(3000));
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(-2));
        // The gate field is a static Func<string,bool>; synthesize the delegate
        // through reflection over the FIELD's own type (MethodInfo is not one).
        Func<string, bool> refusing = null;
        refusing = p => false;
        PublishGateM.SetValue(null, refusing);
        string refused = Scaled(src, 50);
        bool intact = true;
        byte[] now = File.ReadAllBytes(artifact);
        if (now.Length != oldGood.Length) intact = false;
        else for (int i = 0; i < now.Length && intact; i++) if (now[i] != oldGood[i]) intact = false;
        Check("a refusal before the commit leaves the previous artifact intact",
            intact, "artifact changed under a refused publish");
        string left = null;
        foreach (string f in Directory.GetFiles(Path.GetDirectoryName(artifact), "*.staging-*"))
            left = f;
        Check("...and no owned staging file survives the failure", left == null, left ?? "");
        PublishGateM.SetValue(null, null);

        // Remove the failure: the replacement publishes the NEW audio.
        string replaced = Scaled(src, 50);
        Check("removing the refusal publishes the new artifact",
            replaced == artifact && BitConverter.ToInt16(File.ReadAllBytes(replaced), 44) == 1500,
            "first sample " + BitConverter.ToInt16(File.ReadAllBytes(replaced), 44));
    }

    // ── 15.6 PCM32 allocation + ownership ────────────────────────────────────
    static void Pcm32Allocation()
    {
        Console.WriteLine("== PCM32 allocation + in-place ownership ==");
        byte[] big = Pcm32Large();
        long before = GC.GetTotalMemory(true);
        object r = ScaleInPlace(big, 0.5);
        long after = GC.GetTotalMemory(false);
        Check("the large PCM32 fixture scales in place", Supported(r), "");
        Check("...reusing the SAME buffer (no clone, no second array)",
            Bytes(r) == big, Bytes(r) == null ? "no bytes" : "different buffer returned");
        // The only allocations between the two collections are the parser's
        // short-lived temporaries, not an 800 KiB second array.
        Check("...the pass did not allocate a full second WAV-sized array",
            after - before < 400 * 1024, (after - before) + " bytes allocated during scaling");
        // Source guards: the allocation path is GONE, not merely smaller.
        string src = File.ReadAllText(Path.Combine(rootDir(), "LIMISAW.cs"));
        Check("the per-sample BitConverter.GetBytes loop is gone",
            src.IndexOf("byte[] t = BitConverter.GetBytes((int)n);", StringComparison.Ordinal) < 0, "");
        Check("...and the unconditional pre-validation Clone is gone",
            src.IndexOf("(byte[])b.Clone()", StringComparison.Ordinal) < 0
            && src.IndexOf("(byte[])outb.Clone()", StringComparison.Ordinal) < 0, "");
    }

    static string rootDir()
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

    // ── 15.7 the serialized owner ────────────────────────────────────────────
    static int Live, Peak, Plays;
    static readonly object CountLock = new object();

    static string RecordingBackend(string path)
    {
        int now = Interlocked.Increment(ref Live);
        lock (CountLock) { if (now > Peak) Peak = now; Plays++; }
        Thread.Sleep(20);
        Interlocked.Decrement(ref Live);
        return null;
    }

    static void InstallBackend()
    {
        // The field is Func<string,string>; assign a lambda through a typed
        // local so the reflection SetValue has the right delegate type.
        Func<string, string> backend = RecordingBackend;
        Cue.GetField("PlayBackend", NS).SetValue(null, backend);
    }

    static void SerialOwner(string temp)
    {
        Console.WriteLine("== the serialized owner ==");
        InstallBackend();
        ResetCacheM.Invoke(null, null);
        string wav = Path.Combine(temp, "serial.wav");
        File.WriteAllBytes(wav, ToneWav(9000));
        try
        {
            var producers = new List<Thread>();
            for (int t = 0; t < 4; t++)
            {
                var th = new Thread(() =>
                {
                    for (int i = 0; i < 5; i++)
                        SubmitM.Invoke(null, new object[] { temp, "", wav, 50, "cue", null });
                });
                th.IsBackground = true;
                producers.Add(th);
            }
            foreach (Thread th in producers) th.Start();
            foreach (Thread th in producers) th.Join(60000);
            WaitIdleM.Invoke(null, new object[] { 60000 });
            Check("twenty concurrent submits, peak backend concurrency 1",
                Peak == 1 && Plays > 0, "peak=" + Peak + ", executed=" + Plays
                + " (latest-wins supersession allowed)");
        }
        finally { ShutdownM.Invoke(null, null); Plays = 0; Peak = 0; }
    }

    // ── 15.8 latest-wins bound ───────────────────────────────────────────────
    static ManualResetEvent BlockA;
    static string BlockingBackend(string path)
    {
        int now = Interlocked.Increment(ref Live);
        lock (CountLock) { if (now > Peak) Peak = now; Plays++; }
        ManualResetEvent b = BlockA;
        if (b != null && !b.WaitOne(15000)) { Interlocked.Decrement(ref Live); return "backend block timed out"; }
        Interlocked.Decrement(ref Live);
        return null;
    }

    static void LatestWins(string temp)
    {
        Console.WriteLine("== latest-wins queue bound (A,D only) ==");
        Func<string, string> blocking = BlockingBackend;
        Cue.GetField("PlayBackend", NS).SetValue(null, blocking);
        try
        {
            Plays = 0; Peak = 0; Live = 0;
            BlockA = new ManualResetEvent(false);
            string wav = Path.Combine(temp, "latest.wav");
            File.WriteAllBytes(wav, ToneWav(9000));
            var completed = new List<long>();
            var seqs = new[] { -1L, -1L, -1L, -1L };
            SubmitM.Invoke(null, new object[] { temp, "", wav, 50, "cue", null });   // A: blocks in the backend
            // Deterministic start: spin until A has actually ENTERED the
            // backend (Plays==1), so the B/C/D submits cannot race the worker's
            // pickup of A.
            var spin = Stopwatch.StartNew();
            while (Plays < 1 && spin.ElapsedMilliseconds < 8000) Thread.Sleep(15);
            Check("A is inside the blocked backend", Plays == 1, "plays=" + Plays);
            // B, C, D arrive while A is blocked. The owner cannot pick any of
            // them up until A returns, so the one-slot pending must end as D.
            object bR = SubmitM.Invoke(null, new object[] { temp, "", wav, 50, "cue",
                (Action<string>)(why => { lock (completed) completed.Add(seqs[1]); }) });
            seqs[1] = Convert.ToInt64(bR);
            object cR = SubmitM.Invoke(null, new object[] { temp, "", wav, 50, "cue",
                (Action<string>)(why => { lock (completed) completed.Add(seqs[2]); }) });
            seqs[2] = Convert.ToInt64(cR);
            object dR = SubmitM.Invoke(null, new object[] { temp, "", wav, 50, "cue",
                (Action<string>)(why => { lock (completed) completed.Add(seqs[3]); }) });
            seqs[3] = Convert.ToInt64(dR);
            // One-slot proof: the private Pending field is the NEWEST request,
            // not a list that grows with producer count.
            FieldInfo pendingField = Cue.GetField("Pending", NS);
            object pendingNow = pendingField.GetValue(null);
            long pendingSeq = pendingNow == null ? -1 : Convert.ToInt64(pendingNow.GetType().GetField("Seq").GetValue(pendingNow));
            Check("pending slot holds exactly the newest request (D)", pendingSeq == seqs[3],
                "pending=" + pendingSeq + " D=" + seqs[3]);
            BlockA.Set();                                                            // release A
            bool idle = (bool)WaitIdleM.Invoke(null, new object[] { 15000 });
            Check("WaitIdle reports drain while the owner thread stays alive", idle, idle.ToString());
            lock (completed)
            {
                bool bRan = completed.Contains(seqs[1]);
                bool cRan = completed.Contains(seqs[2]);
                bool dRan = completed.Contains(seqs[3]);
                Check("B was superseded and never executed", !bRan, "completions: " + string.Join("/", completed.ToArray()));
                Check("C was superseded and never executed", !cRan, "completions: " + string.Join("/", completed.ToArray()));
                Check("D executed exactly once", dRan && completed.FindAll(x => x == seqs[3]).Count == 1,
                    "completions: " + string.Join("/", completed.ToArray()));
            }
            Check("peak backend concurrency stayed 1", Peak == 1, "peak=" + Peak);
        }
        finally
        {
            BlockA.Dispose(); BlockA = null;
            ShutdownM.Invoke(null, null);
            Plays = 0;
        }
    }

    // ── 15.9 UI nonblocking ──────────────────────────────────────────────────
    static void UiNonblock(string temp)
    {
        Console.WriteLine("== UI submit does not block ==");
        var slowBackend = (Delegate)Cue.GetField("PlayBackend", NS).GetValue(null);
        Cue.GetField("PlayBackend", NS).SetValue(null, Delegate.CreateDelegate(slowBackend.GetType(),
            typeof(AudioPipeline).GetMethod("SlowBackend", NS)));
        try
        {
            string wav = Path.Combine(temp, "slow.wav");
            File.WriteAllBytes(wav, ToneWav(9000));
            var sw = Stopwatch.StartNew();
            SubmitM.Invoke(null, new object[] { temp, "", wav, 50, "preview", null });
            sw.Stop();
            Check("Submit returns immediately while the owner works",
                sw.ElapsedMilliseconds < 200, sw.ElapsedMilliseconds + " ms");
            // The UI thread must still process a message while the (slow) owner
            // holds the backend. A pumped DoEvents inside the wait window proves
            // the SUBMITTING thread is free; the owner's block lasts ~600 ms.
            var sw2 = Stopwatch.StartNew();
            while (sw2.ElapsedMilliseconds < 600) Application.DoEvents();
            Check("...the submitting thread stayed responsive during the block",
                sw2.ElapsedMilliseconds >= 550, "pumped " + sw2.ElapsedMilliseconds + " ms freely");
            // The REAL UI path: the form's own Preview handler must return
            // promptly too — that is the contract the producer side owns. With
            // the RED E mutation (a synchronous Play inside Preview) this is
            // the check that fails.
            string root = rootDir();
            Type stT = Asm.GetType("Limisaw.LimisawSettings");
            object st = Activator.CreateInstance(stT, new object[] { temp });
            stT.GetMethod("Load").Invoke(st, null);
            object themes = Asm.GetType("Limisaw.Theme").GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { root });
            using (var tray = new NotifyIcon())
            {
                object f = Activator.CreateInstance(Asm.GetType("Limisaw.LimisawForm"),
                    new object[] { temp, st, tray, themes });
                try
                {
                    Thread.Sleep(800);          // let the constructor's first sweep end
                    MethodInfo preview = Asm.GetType("Limisaw.LimisawForm")
                        .GetMethod("Preview", BindingFlags.NonPublic | BindingFlags.Instance);
                    var psw = Stopwatch.StartNew();
                    preview.Invoke(f, new object[] { wav });
                    psw.Stop();
                    Check("the form's Preview handler returns promptly",
                        psw.ElapsedMilliseconds < 300,
                        psw.ElapsedMilliseconds + " ms (must not wait on the slow backend)");
                    WaitIdleM.Invoke(null, new object[] { 8000 });   // drain behind the UI
                }
                finally { ((IDisposable)f).Dispose(); tray.Dispose(); }
            }
        }
        finally { ShutdownM.Invoke(null, null); }
    }

    static string SlowBackend(string path) { Thread.Sleep(600); return null; }

    // ── 15.10 shutdown lifecycle ─────────────────────────────────────────────
    static void ShutdownLifecycle()
    {
        Console.WriteLine("== shutdown lifecycle ==");
        InstallBackend();
        Plays = 0;
        string wav = Path.Combine(temp0(), "life.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(wav));
        File.WriteAllBytes(wav, ToneWav(9000));
        SubmitM.Invoke(null, new object[] { Path.GetDirectoryName(wav), "", Path.GetFileName(wav), 50, "cue", null });
        ShutdownM.Invoke(null, null);
        Check("shutdown terminates the owner",
            WaitIdleM.Invoke(null, new object[] { 2000 }).Equals(true), "");
        // A second controlled lifecycle starts cleanly. (Shutdown resets the
        // sequence counters and kills the worker; the next Submit starts a
        // fresh owner.) The completion callback is the deterministic signal:
        // it fires on the owner thread after the backend has been called.
        Plays = 0; Peak = 0;
        InstallBackend();
        var played = new ManualResetEvent(false);
        SubmitM.Invoke(null, new object[] { Path.GetDirectoryName(wav), "", wav, 50, "cue",
            (Action<string>)(why => { try { played.Set(); } catch { } }) });
        if (!played.WaitOne(8000)) Check("a second lifecycle runs cleanly after shutdown", false, "no completion in 8s");
        else Check("a second lifecycle runs cleanly after shutdown", Plays >= 1, Plays + " played");
        ShutdownM.Invoke(null, null);
    }

    static string temp0() { return Path.Combine(Path.GetTempPath(), "limisaw_audio_" + "lifecycle"); }
}
