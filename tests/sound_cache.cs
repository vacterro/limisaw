using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

// W2-007: the scaled-WAV cache derived its artifact name from the source's
// BASENAME only — `alert.wav` at 50% became `alert_v50.wav` no matter which
// alert.wav it came from. Two distinct files with the same name (the user's own
// folder and the Sounds folder beside the exe both legitimately hold
// `pop_cartoon_pop.wav`) therefore aliased ONE cache artifact: whichever was
// scaled first won, and the other silently played the wrong sound. The mtime
// guard did not help — the cached copy was newer than both sources, so neither
// rebuilt.
//
// The name now carries a digest of the canonical source path. This harness
// builds two audibly different WAVs that share a basename, drives the real
// SoundCue.Play through the real cache, and asserts the artifacts are distinct
// files with distinct bytes — including with the mtimes in both orders, because
// the old code's only defence was an mtime comparison.
//
// Build + run: pwsh .\build.ps1 -Tests   (reflects over LIMISAW.exe)
public static class SoundCacheTest
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

    static Type Cue;
    static MethodInfo ScaledM;
    static readonly List<string> Played = new List<string>();

    // The seam PERF-004 introduced: the real player cannot be asked what it was
    // handed, and playing real audio in a test harness is not acceptable.
    public static string Backend(string path) { lock (Played) Played.Add(path); return null; }

    static string Scaled(string src, int volume)
    {
        return (string)ScaledM.Invoke(null, new object[] { src, volume });
    }

    // 16-bit mono PCM at a chosen constant amplitude: two different amplitudes
    // are two audibly different files, which is what makes "the wrong sound
    // played" observable in bytes.
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
        using (var h = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path)));
    }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        Assembly asm = Assembly.LoadFrom(Path.Combine(root, "LIMISAW.exe"));
        Cue = asm.GetType("Limisaw.SoundCue");
        ScaledM = Cue.GetMethod("Scaled", NS);
        FieldInfo built = Cue.GetField("Built", NS);
        FieldInfo backend = Cue.GetField("PlayBackend", BindingFlags.Static | BindingFlags.NonPublic);
        Check("the cache and its seams are reachable",
            Cue != null && ScaledM != null && built != null && backend != null, "");
        if (ScaledM == null || built == null) return 1;

        string temp = Path.Combine(Path.GetTempPath(), "limisaw_wavcache_" + Guid.NewGuid().ToString("N"));
        string dirA = Path.Combine(temp, "user-folder");
        string dirB = Path.Combine(temp, "shipped-folder");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        // The collision the audit reported: same basename, different content.
        string a = Path.Combine(dirA, "pop_cartoon_pop.wav");
        string b = Path.Combine(dirB, "pop_cartoon_pop.wav");
        File.WriteAllBytes(a, Tone(9000, 64));
        File.WriteAllBytes(b, Tone(1200, 64));
        Check("the two sources share a basename but not their bytes",
            Path.GetFileName(a) == Path.GetFileName(b) && Sha(a) != Sha(b), "");

        Console.WriteLine();
        Console.WriteLine("== two files, one basename: two artifacts ==");
        string sa = Scaled(a, 50);
        string sb = Scaled(b, 50);
        Check("both were scaled", sa != null && sb != null, "");
        if (sa == null || sb == null) return 1;
        Check("the artifacts are distinct files", sa != sb,
            sa == sb ? "both aliased " + Path.GetFileName(sa) : Path.GetFileName(sa) + " vs " + Path.GetFileName(sb));
        Check("...with distinct bytes on disk", Sha(sa) != Sha(sb), "");
        Check("...and the readable basename survives in the name",
            Path.GetFileName(sa).StartsWith("pop_cartoon_pop_", StringComparison.Ordinal)
            && Path.GetFileName(sa).EndsWith("_v50.wav", StringComparison.Ordinal),
            Path.GetFileName(sa));

        // Each artifact must be the scaling of ITS OWN source. The loud file
        // scaled to 50% is still louder than the quiet one at 50%.
        byte[] ba = File.ReadAllBytes(sa), bb = File.ReadAllBytes(sb);
        short firstA = BitConverter.ToInt16(ba, 44), firstB = BitConverter.ToInt16(bb, 44);
        Check("each artifact is the scaling of its own source",
            firstA == 4500 && firstB == 600, "A=" + firstA + " (want 4500), B=" + firstB + " (want 600)");

        Console.WriteLine();
        Console.WriteLine("== alternating playback: neither entry moves under the other ==");
        string keepA = sa, keepB = sb;
        string shaA = Sha(sa), shaB = Sha(sb);
        for (int i = 0; i < 6; i++)
        {
            Scaled(a, 50);
            Scaled(b, 50);
        }
        Check("the paths are stable across repeats",
            Scaled(a, 50) == keepA && Scaled(b, 50) == keepB, "");
        Check("...and neither file was rewritten by the other",
            Sha(keepA) == shaA && Sha(keepB) == shaB, "");

        Console.WriteLine();
        Console.WriteLine("== the mtime order cannot resurrect the alias ==");
        // The old code's only defence was "rebuild if the artifact is older than
        // the source". Both orders must be safe, because with one shared name
        // whichever source was newer would clobber the other's audio.
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddHours(-5));
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow);
        var cache = (System.Collections.IDictionary)built.GetValue(null);
        cache.Clear();
        string sa2 = Scaled(a, 50), sb2 = Scaled(b, 50);
        Check("B newer than A: still two artifacts", sa2 != sb2, "");
        Check("...and A still holds A's audio",
            BitConverter.ToInt16(File.ReadAllBytes(sa2), 44) == 4500,
            "first sample " + BitConverter.ToInt16(File.ReadAllBytes(sa2), 44));

        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddHours(-5));
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow);
        cache.Clear();
        string sa3 = Scaled(a, 50), sb3 = Scaled(b, 50);
        Check("A newer than B: still two artifacts", sa3 != sb3, "");
        Check("...and B still holds B's audio",
            BitConverter.ToInt16(File.ReadAllBytes(sb3), 44) == 600,
            "first sample " + BitConverter.ToInt16(File.ReadAllBytes(sb3), 44));

        Console.WriteLine();
        Console.WriteLine("== a real edit of one source still rebuilds only that one ==");
        cache.Clear();
        // The artifact is aged first, then the source rewritten: that IS the real
        // sequence (cached at some point, user edits the WAV later) and it is the
        // only deterministic one. Writing both inside the same clock tick leaves
        // the rebuild guard's `<` comparison equal, which is a filesystem
        // granularity artifact rather than anything this ticket changed.
        File.SetLastWriteTimeUtc(sa3, DateTime.UtcNow.AddMinutes(-10));
        File.WriteAllBytes(a, Tone(6000, 64));
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow);
        string sa4 = Scaled(a, 50);
        Check("the edited source is rescaled in place",
            sa4 == sa3 && BitConverter.ToInt16(File.ReadAllBytes(sa4), 44) == 3000,
            "first sample " + BitConverter.ToInt16(File.ReadAllBytes(sa4), 44));
        Check("...and the untouched source's artifact is unchanged",
            BitConverter.ToInt16(File.ReadAllBytes(sb3), 44) == 600, "");

        Console.WriteLine();
        Console.WriteLine("== the volume step still keys the cache ==");
        string v50 = Scaled(a, 50), v75 = Scaled(a, 75);
        Check("different volumes are different artifacts", v50 != v75,
            Path.GetFileName(v50) + " vs " + Path.GetFileName(v75));
        Check("...and 5% quantisation still collapses neighbours",
            Scaled(a, 52) == v50, "");

        Console.WriteLine();
        Console.WriteLine("== the whole Play path hands over the right artifact ==");
        MethodInfo play = Cue.GetMethod("Play", PS);
        backend.SetValue(null, Delegate.CreateDelegate(backend.FieldType,
            typeof(SoundCacheTest).GetMethod("Backend", PS)));
        try
        {
            lock (Played) Played.Clear();
            object whyA = play.Invoke(null, new object[] { dirA, dirA, "pop_cartoon_pop.wav", 50 });
            object whyB = play.Invoke(null, new object[] { dirB, dirB, "pop_cartoon_pop.wav", 50 });
            Check("both cues played without complaint", whyA == null && whyB == null,
                (whyA ?? "") + " / " + (whyB ?? ""));
            Check("...and the player got two DIFFERENT files",
                Played.Count == 2 && Played[0] != Played[1],
                Played.Count == 2 ? Path.GetFileName(Played[0]) + " vs " + Path.GetFileName(Played[1])
                                  : Played.Count + " cues");
        }
        finally { backend.SetValue(null, null); }

        Console.WriteLine();
        Console.WriteLine("== the shape of the fix ==");
        string dir = root;
        for (int i = 0; i < 4 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "LIMISAW.cs"))) break;
            DirectoryInfo up = Directory.GetParent(dir);
            dir = up == null ? null : up.FullName;
        }
        string src = File.ReadAllText(Path.Combine(dir ?? root, "LIMISAW.cs"));
        Check("the basename-only artifact name is gone",
            src.IndexOf("Path.GetFileNameWithoutExtension(src) + \"_v\" + q", StringComparison.Ordinal) < 0, "");
        Check("...replaced by a path digest",
            src.IndexOf("Path.GetFileNameWithoutExtension(src) + \"_\" + Tag(src) + \"_v\" + q", StringComparison.Ordinal) >= 0, "");

        try { Directory.Delete(temp, true); } catch { }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }
}
