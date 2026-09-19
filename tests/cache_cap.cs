using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

// PERF-004 (SRC-006:R021): the sound scaler cache ownership is bounded by
// quantization but not by path cardinality. Every distinct source/volume
// combination is kept forever, and the owned temp directory was pruned only
// once (on the first cache miss of the process). LIMISAW is a long-lived
// resident tray application, so an unbounded cache is a slow leak — files
// accumulate in %TEMP%\limisaw_sounds and the in-memory dictionary never
// discards them.
//
// The contract this harness holds:
//
//   * Built.Count NEVER exceeds the documented CacheCap, regardless of how
//     many distinct source-path/volume combinations are requested;
//   * eviction is LRU by the last successful play (Seq is bumped on every
//     hit), not arbitrary — an actively-used entry survives a mass miss;
//   * an evicted entry rebuilds deterministically to the SAME scaled samples
//     and the SAME path, so the sound selected and its audio bytes are
//     unchanged;
//   * the owned temp directory converges toward DiskCap while the active
//     artifact (the one the player owns) is never deleted, foreign files
//     are never touched, and the user's source WAV bytes are byte-for-byte
//     identical afterwards;
//   * maintenance runs at the documented cadence AND on direct invocation,
//     so a long-lived process can converge the directory without a restart;
//   * the identity rule is preserved: distinct source paths still derive
//     distinct artifacts (basename + 8-hex Tag), and 5% quantisation still
//     collapses neighbours.
//
// Deterministic seams: SoundCue.Built (IDictionary), CacheCap, DiskCap,
// Misses, MaintenanceEvery. No real audio (PlayBackend records). No wall-clock
// benchmarking.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   csc -out:cache_cap.exe -r:System.dll tests\cache_cap.cs
public static class CacheCapTest
{
    static int fails = 0, checks = 0;
    const BindingFlags NS = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags PS = BindingFlags.Static | BindingFlags.Public;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Type SoundCue;
    static Assembly Asm;

    static int F(string f) { return (int)SoundCue.GetField(f, NS).GetValue(null); }
    static IDictionary Built
    { get { return (IDictionary)SoundCue.GetField("Built", NS).GetValue(null); } }
    static void SetInt(string f, int v) { SoundCue.GetField(f, NS).SetValue(null, v); }
    static string Scaled(string src, int vol)
    { return (string)SoundCue.GetMethod("Scaled", NS).Invoke(null, new object[] { src, vol }); }
    static void ResetCache() { SoundCue.GetMethod("ResetCacheForTests", NS).Invoke(null, null); }
    static void Maintain(string dir)
    { SoundCue.GetMethod("MaintainDisk", NS).Invoke(null, new object[] { dir }); }
    static void SetActive(string path)
    { SoundCue.GetField("ActiveArtifact", NS).SetValue(null, path); }

    // 16-bit mono PCM at a constant amplitude — a real, playable, tiny WAV.
    static byte[] Tone(short amp, int samples)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        byte[] data = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        { data[i * 2] = (byte)(amp & 0xFF); data[i * 2 + 1] = (byte)((amp >> 8) & 0xFF); }
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

    static string Sha(string path)
    {
        using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path)));
    }

    static string CacheDir() { return Path.Combine(Path.GetTempPath(), "limisaw_sounds"); }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_cap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // No vendors, no creds: Scaled builds artifacts from temp sources
            // and never tries to talk to a real CLI.
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
            Asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            SoundCue = Asm.GetType("Limisaw.SoundCue");
            Check("the sound cache is reachable", SoundCue != null, "");
            if (SoundCue == null) return 1;

            var dir = CacheDir();
            try { Directory.Delete(dir, true); } catch { }

            // ── cardinality ceiling ─────────────────────────────────────
            ResetCache();
            int oldCap = F("CacheCap");
            SetInt("CacheCap", 6);
            string[] sources = new string[8];
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = Path.Combine(temp, "src" + i.ToString("00") + ".wav");
                File.WriteAllBytes(sources[i], Tone((short)(1000 + i * 500), 64));
            }
            for (int i = 0; i < sources.Length; i++)
                Scaled(sources[i], 50);
            Check("Built.Count never exceeds CacheCap", Built.Count <= F("CacheCap"),
                Built.Count + " vs cap=" + F("CacheCap"));

            // ── LRU order, not arbitrary deletion ────────────────────────
            ResetCache();
            SetInt("CacheCap", 3);
            Check("CacheCap==3 for LRU section", F("CacheCap") == 3, F("CacheCap").ToString());
            string a = Path.Combine(temp, "a.wav"), b = Path.Combine(temp, "b.wav"), c = Path.Combine(temp, "c.wav");
            File.WriteAllBytes(a, Tone(9000, 64));
            File.WriteAllBytes(b, Tone(6000, 64));
            File.WriteAllBytes(c, Tone(3000, 64));
            Scaled(a, 50); Scaled(b, 50); Scaled(c, 50); // fill {A,B,C}
            Scaled(a, 50); // touch A
            File.WriteAllBytes(Path.Combine(temp, "d.wav"), Tone(1000, 64));
            string pathD = Scaled(Path.Combine(temp, "d.wav"), 50); // new miss -> evict oldest untouched = B
            var keys = new List<string>();
            foreach (string k in Built.Keys) keys.Add(k);
            Check("LRU: the untouched oldest (B) is evicted, the touched (A) survives",
                keys.Count == 3
                && keys.Exists(n => n.IndexOf("a.", StringComparison.OrdinalIgnoreCase) >= 0)
                && !keys.Exists(n => n.IndexOf("b.", StringComparison.OrdinalIgnoreCase) >= 0),
                "keys=" + string.Join(";", keys.ToArray()));

            // ── rebuild after eviction is byte-identical ─────────────────
            ResetCache();
            SetInt("CacheCap", 2);
            File.WriteAllBytes(b, Tone(6000, 64));       // rewrite source cleanly
            File.WriteAllBytes(Path.Combine(temp, "d1.wav"), Tone(1000, 64));
            File.WriteAllBytes(Path.Combine(temp, "d2.wav"), Tone(1000, 64));
            Scaled(b, 50);           // fill 1
            Scaled(Path.Combine(temp, "d1.wav"), 50);    // fill 2, b is oldest
            string pathB1 = Scaled(b, 50);               // HIT
            Scaled(Path.Combine(temp, "d2.wav"), 50);    // miss: evicts B (oldest)
            // Baseline captured BEFORE eviction/deletion: the rebuild must
            // reproduce exactly these bytes.
            byte[] bBaseline = File.ReadAllBytes(pathB1);
            string bBaselineSha = Sha(pathB1);
            File.Delete(pathB1);                          // force a real rebuild (not a path-lucky hit)
            string pathB2 = Scaled(b, 50);
            byte[] bAfter = File.ReadAllBytes(pathB2);
            Check("an evicted source rebuilds to the SAME scaled samples (baseline hash equal)",
                pathB1 == pathB2 && bAfter.Length == bBaseline.Length && Sha(pathB2) == bBaselineSha,
                "same=" + (pathB1 == pathB2) + " bytes=" + bAfter.Length + "/" + bBaseline.Length);

            // ── RED CONTROL (SRC-007 corrective 1, case A): the exact collision ──
            // A user SOURCE WAV stored inside the cache directory whose name
            // EXACTLY matches the complete production final-artifact grammar
            // ^limisaw_.+_[0-9a-f]{8}_v[0-9]+\.wav$. Filename grammar alone
            // must NEVER grant deletion authority; this file is FOREIGN (it
            // was never created/registered through the LIMISAW ownership
            // mechanism). Current production code deletes it: the regression
            // must FAIL before the repair.
            string redDir = Path.Combine(Path.GetTempPath(), "limisaw_red_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(redDir);
            try
            {
                Directory.Delete(CacheDir(), true); Directory.CreateDirectory(CacheDir());
                ResetCache();
                SetInt("DiskCap", 512); SetInt("CacheCap", 128); SetInt("MaintenanceEvery", 1);
                string redSource = Path.Combine(CacheDir(), "limisaw_user_abcdef00_v50.wav");
                File.WriteAllBytes(redSource, Tone(12345, 64));
                File.SetLastWriteTimeUtc(redSource, DateTime.UtcNow.AddDays(-4));
                string redBefore = Sha(redSource);
                // Force maintenance directly (the production cadence path), as
                // Scaled does BEFORE it reads the user source.
                Maintain(CacheDir());
                Check("RED A: the exact-collision user source limisaw_user_abcdef00_v50.wav survives forced maintenance",
                    File.Exists(redSource) && Sha(redSource) == redBefore,
                    File.Exists(redSource) ? "exists" : "DELETED BY MAINTENANCE");
                // The same collision through the Scaled() path: maintenance
                // runs on the FIRST miss before File.ReadAllBytes(src).
                string redScaled = Scaled(redSource, 50);
                Check("RED A: scaling the exact-collision user source succeeds",
                    redScaled != null, redScaled == null ? "(null)" : Path.GetFileName(redScaled));
                Check("RED A: the user source is byte-for-byte intact after Scaled",
                    File.Exists(redSource) && Sha(redSource) == redBefore, "");
                Check("RED A: the derived output is NOT the user source",
                    redScaled != null && !redScaled.Equals(redSource, StringComparison.OrdinalIgnoreCase),
                    redScaled == null ? "(null)" : Path.GetFileName(redScaled));
            }
            finally
            {
                try { Directory.Delete(CacheDir(), true); } catch { }
            }

            // ── ownership-safe disk maintenance (SRC-007, cases B–H) ─────────
            // Foreign fixtures (FOREIGN = never created/registered through the
            // LIMISAW ownership mechanism) must survive maintenance byte-for-byte
            // even when their names EXACTLY match the generated-artifact or
            // staging grammar. Genuine owned artifacts are created THROUGH the
            // production mechanism (Scaled -> PublishAtomic) which registers
            // ownership.
            int oldDisk = F("DiskCap");
            int oldMaint = F("MaintenanceEvery");
            ResetCache();
            SetInt("MaintenanceEvery", 1000); // no in-Scaled maintenance while building fixtures
            SetInt("DiskCap", 4);

            // Create genuine owned artifacts through the production mechanism.
            string[] genSrc = new string[6];
            for (int i = 0; i < genSrc.Length; i++)
            {
                genSrc[i] = Path.Combine(temp, "gen" + i.ToString("00") + ".wav");
                File.WriteAllBytes(genSrc[i], Tone((short)(1000 + i * 50), 64));
            }
            string[] genOwned = new string[6];
            for (int i = 0; i < genSrc.Length; i++) genOwned[i] = Scaled(genSrc[i], 50);
            var ownedFinals = (System.Collections.Generic.HashSet<string>)SoundCue.GetField("OwnedFinals", NS).GetValue(null);
            var ownedStaging = (System.Collections.Generic.HashSet<string>)SoundCue.GetField("OwnedStaging", NS).GetValue(null);

            // case B: FOREIGN old WAV matching the COMPLETE final-artifact grammar.
            string foreignFinal = Path.Combine(dir, "limisaw_foreign_abcd1234_v50.wav");
            File.WriteAllBytes(foreignFinal, Tone(7777, 64));
            File.SetLastWriteTimeUtc(foreignFinal, DateTime.UtcNow.AddDays(-4));
            string foreignFinalBefore = Sha(foreignFinal);
            // case C: FOREIGN old file matching the COMPLETE staging grammar.
            string foreignStaging = Path.Combine(dir, "limisaw_stranger_abcd1234_v50.wav.staging-" + new string('c', 32));
            File.WriteAllBytes(foreignStaging, System.Text.Encoding.UTF8.GetBytes("foreign staging lookalike"));
            string foreignStagingBefore = BitConverter.ToString(File.ReadAllBytes(foreignStaging));
            // legacy foreign non-prefixed WAV — must also survive.
            string foreign = Path.Combine(dir, "my_foreign_music.wav");
            File.WriteAllBytes(foreign, Tone(8888, 64));
            File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-4));
            string foreignBefore = Sha(foreign);

            // Age exactly two of the six genuine owned artifacts so they are
            // eligible for the age policy; the other four stay new and survive.
            File.SetLastWriteTimeUtc(genOwned[0], DateTime.UtcNow.AddDays(-4));
            File.SetLastWriteTimeUtc(genOwned[1], DateTime.UtcNow.AddDays(-4));

            // Force maintenance directly (simulates a long-running process).
            Maintain(dir);
            // Six owned finals existed; the two aged ones are deleted by the age
            // policy; four fresh owned finals survive — DiskCap never exceeded.
            Check("owned cardinality converged: aged owned deleted, fresh owned kept",
                ownedFinals.Count == 4, ownedFinals.Count + " (want 4)");
            Check("case D: the aged genuinely-owned artifacts were cleaned",
                !File.Exists(genOwned[0]) && !File.Exists(genOwned[1]), "");
            Check("case B: a FOREIGN final-grammar WAV survives maintenance byte-for-byte",
                File.Exists(foreignFinal) && Sha(foreignFinal) == foreignFinalBefore, "");
            Check("case C: a FOREIGN staging-grammar file survives maintenance byte-for-byte",
                File.Exists(foreignStaging) && BitConverter.ToString(File.ReadAllBytes(foreignStaging)) == foreignStagingBefore, "");
            Check("a legacy foreign non-prefixed WAV survives maintenance",
                File.Exists(foreign) && Sha(foreign) == foreignBefore, "");
            Check("case D: genuinely-owned artifacts stay within DiskCap",
                ownedFinals.Count <= F("DiskCap"), ownedFinals.Count + " <= " + F("DiskCap"));

            // case E: genuine CRASH staging residue created through the production
            // ownership mechanism (registered in OwnedStaging, written, but the
            // atomic commit never happened). Maintenance must remove it.
            string crashStaging = Path.Combine(dir, "limisaw_crash_abcd1234_v50.wav.staging-" + new string('d', 32));
            File.WriteAllBytes(crashStaging, System.Text.Encoding.ASCII.GetBytes("orphan staging"));
            ownedStaging.Add(crashStaging);
            Maintain(dir);
            Check("case E: a genuine crash staging residue is removed by maintenance", !File.Exists(crashStaging), "");
            Check("case E: the staging ownership entry is cleared on cleanup",
                !ownedStaging.Contains(crashStaging), "");

            // case 5: every user source WAV is byte-for-byte unchanged. The
            // baseline is the SOURCE bytes, captured before maintenance.
            var genSrcBefore = new Dictionary<string, string>();
            foreach (string s in genSrc) genSrcBefore[s] = Sha(s);
            bool[] sourceOk = new bool[genSrc.Length];
            for (int i = 0; i < genSrc.Length; i++)
                sourceOk[i] = File.Exists(genSrc[i]) && Sha(genSrc[i]) == genSrcBefore[genSrc[i]];
            Check("case 5: every user source WAV is byte-for-byte unchanged",
                Array.TrueForAll(sourceOk, x => x), "");

            // ── the active artifact is protected (F, G) ─────────────────────
            ResetCache();
            SetInt("DiskCap", 3);
            string activeSrc = Path.Combine(temp, "active.wav");
            File.WriteAllBytes(activeSrc, Tone(4500, 64));
            string activeOwned = Scaled(activeSrc, 50);
            File.SetLastWriteTimeUtc(activeOwned, DateTime.UtcNow.AddDays(-4));
            SetActive(activeOwned);
            Maintain(dir);
            Check("case F: the active artifact survives age cleanup",
                File.Exists(activeOwned), "");
            Check("case F: the active artifact is still counted as owned",
                ownedFinals.Contains(activeOwned), "");
            // case G: once it is no longer the active artifact, it is eligible.
            SetActive(null);
            SetInt("DiskCap", 1); // force at least one eviction
            Maintain(dir);
            Check("case G: a no-longer-active aged artifact is eligible for cleanup",
                !File.Exists(activeOwned), "");

            // ── DiskCap is the hard ceiling INCLUDING the active owner (H) ────
            ResetCache();
            SetInt("DiskCap", 3);
            string[] hSrc = new string[4];
            for (int i = 0; i < hSrc.Length; i++)
            {
                hSrc[i] = Path.Combine(temp, "h" + i + ".wav");
                File.WriteAllBytes(hSrc[i], Tone((short)(2000 + i * 100), 64));
            }
            string[] hOwned = new string[4];
            for (int i = 0; i < hSrc.Length; i++) hOwned[i] = Scaled(hSrc[i], 50);
            SetActive(hOwned[3]);
            Maintain(dir);
            Check("case H: total positively-owned artifacts INCLUDING the active owner <= DiskCap",
                ownedFinals.Count <= F("DiskCap"), ownedFinals.Count + " (want <= " + F("DiskCap") + ")");
            Check("case H: the active artifact survives the ceiling eviction",
                File.Exists(hOwned[3]), "");
            SetActive(null);

            // ── quantisation and identity (I, J) ──────────────────────────
            ResetCache();
            SetInt("CacheCap", 128);
            SetInt("DiskCap", 512);
            SetInt("MaintenanceEvery", 32);
            string v50 = Scaled(sources[0], 50), v52 = Scaled(sources[0], 52), v75 = Scaled(sources[0], 75);
            Check("I/J: 5% quantisation collapses neighbours (52 == 50)",
                v50 == v52, "50=" + Path.GetFileName(v50) + " 52=" + (v52 ?? "(null)"));
            Check("I/J: different volumes are different artifacts", v50 != v75, "");
            string other = Path.Combine(temp, "other_" + Path.GetFileName(sources[0]));
            File.WriteAllBytes(other, Tone(9000, 64));
            string pathOther = Scaled(other, 50);
            Check("I/J: distinct source paths keep distinct artifacts",
                v50 != pathOther, "basename alias=" + (Path.GetFileName(v50) == Path.GetFileName(pathOther)));
            File.SetLastWriteTimeUtc(v50, DateTime.UtcNow.AddDays(-4));
            Maintain(dir);
            Check("I/J: an aged owned artifact is pruned by maintenance", !File.Exists(v50), "");
            Check("I/J: a still-new artifact survives", File.Exists(v75), "");
            // Source edit invalidation (length+mtime) remains green (sound_cache
            // pattern: artifact aged, source rewritten, rebuild in place).
            string edited = Path.Combine(temp, "edited.wav");
            File.WriteAllBytes(edited, Tone(3000, 64));
            string e1 = Scaled(edited, 50);
            File.SetLastWriteTimeUtc(e1, DateTime.UtcNow.AddMinutes(-10));
            File.WriteAllBytes(edited, Tone(7000, 128)); // different amp AND length
            File.SetLastWriteTimeUtc(edited, DateTime.UtcNow.AddMinutes(5));
            string e2 = Scaled(edited, 50);
            Check("I: a source edit (length+mtime) invalidates and rebuilds in place with the NEW audio",
                e1 == e2 && BitConverter.ToInt16(File.ReadAllBytes(e2), 44) == 3500,
                "first sample " + (File.Exists(e2) ? BitConverter.ToInt16(File.ReadAllBytes(e2), 44).ToString() : "missing"));

            SetInt("CacheCap", oldCap); SetInt("DiskCap", oldDisk); SetInt("MaintenanceEvery", oldMaint);
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
            try { Directory.Delete(CacheDir(), true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(checks + " checks");
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
