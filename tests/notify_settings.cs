using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

// LIMISAW alert settings (T-103): per-event on/off, a user-chosen WAV, a
// volume, and a low-quota threshold. Three things are load-bearing and were
// each a real bug elsewhere in this codebase:
//
//   * a stored sound that no longer exists must resolve to nothing (a stale
//     WAV name is silent silence, exactly the library-rename defect);
//   * volume scaling must round-trip the ini and clamp, and must actually
//     change the samples without corrupting the RIFF header;
//   * the low threshold must clamp so a hand-edited ini cannot set 0% or 200%.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:notify_settings.exe -r:System.dll tests\notify_settings.cs
//   notify_settings.exe            (exit 0 = all PASS)
public static class NotifySettingsTest
{
    static int fails = 0, checks = 0;
    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Assembly Load()
    {
        string root = Directory.GetCurrentDirectory();
        string exe = Path.Combine(root, "LIMISAW.exe");
        if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
        return Assembly.LoadFrom(Path.GetFullPath(exe));
    }

    // A tiny 16-bit mono WAV with every sample at `amp`, so a scaled copy can
    // be checked by reading one sample back.
    static byte[] Tone(int amp)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        short[] s = new short[8];
        for (int i = 0; i < s.Length; i++) s[i] = (short)amp;
        byte[] data = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, data, 0, data.Length);
        int dataLen = data.Length;
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(8000); w.Write(16000); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    static int FirstSample(Assembly asm, byte[] wav)
    {
        Type cue = asm.GetType("Limisaw.SoundCue");
        MethodInfo scale = cue.GetMethod("Scale", BindingFlags.NonPublic | BindingFlags.Static);
        byte[] half = (byte[])scale.Invoke(null, new object[] { wav, 0.5 });
        // data starts after the 44-byte canonical header built above
        return BitConverter.ToInt16(half, 44);
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_notify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assembly asm = Load();
            Type settingsType = asm.GetType("Limisaw.LimisawSettings");
            Type cue = asm.GetType("Limisaw.SoundCue");

            // --- settings round-trip + clamps ---
            string dir = Path.Combine(temp, "cfg");
            Directory.CreateDirectory(dir);
            object s = Activator.CreateInstance(settingsType, new object[] { dir });
            settingsType.GetField("ResetSound").SetValue(s, false);
            settingsType.GetField("ResetSoundFile").SetValue(s, "mine.wav");
            settingsType.GetField("NotifyLow").SetValue(s, true);
            settingsType.GetField("LowPct").SetValue(s, 33);
            settingsType.GetField("SoundVolume").SetValue(s, 42);
            settingsType.GetMethod("Save").Invoke(s, null);

            object s2 = Activator.CreateInstance(settingsType, new object[] { dir });
            settingsType.GetMethod("Load").Invoke(s2, null);
            Check("reset sound off survives a save/load",
                (bool)settingsType.GetField("ResetSound").GetValue(s2) == false, "");
            Check("a chosen reset WAV survives a save/load",
                (string)settingsType.GetField("ResetSoundFile").GetValue(s2) == "mine.wav", "");
            Check("low alert + threshold survive a save/load",
                (bool)settingsType.GetField("NotifyLow").GetValue(s2)
                && (int)settingsType.GetField("LowPct").GetValue(s2) == 33, "");
            Check("volume survives a save/load",
                (int)settingsType.GetField("SoundVolume").GetValue(s2) == 42, "");

            // Clamp a hand-edited ini out of range.
            File.WriteAllText(Path.Combine(dir, "LIMISAW.ini"),
                "[limisaw]\r\nLowPct=200\r\nSoundVolume=900\r\n");
            object s3 = Activator.CreateInstance(settingsType, new object[] { dir });
            settingsType.GetMethod("Load").Invoke(s3, null);
            int low = (int)settingsType.GetField("LowPct").GetValue(s3);
            int vol = (int)settingsType.GetField("SoundVolume").GetValue(s3);
            Check("a bogus LowPct clamps into 5..95", low >= 5 && low <= 95, "LowPct=" + low);
            Check("a bogus SoundVolume clamps into 0..100", vol >= 0 && vol <= 100, "Vol=" + vol);

            // --- SoundCue.Library + Resolve ---
            MethodInfo lib = cue.GetMethod("Library", BindingFlags.Public | BindingFlags.Static);
            MethodInfo resolve = cue.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static);

            string root2 = Path.Combine(temp, "app");
            string sounds = Path.Combine(root2, "Sounds");
            Directory.CreateDirectory(sounds);
            File.WriteAllBytes(Path.Combine(sounds, "pop.wav"), Tone(1000));
            string found = (string)lib.Invoke(null, new object[] { root2, "" });
            Check("Library prefers a Sounds folder next to the exe",
                string.Equals(Path.GetFullPath(found), Path.GetFullPath(sounds),
                    StringComparison.OrdinalIgnoreCase), found);

            string bare = (string)resolve.Invoke(null, new object[] { root2, "", "pop.wav" });
            Check("a bare name resolves inside the library",
                bare != null && File.Exists(bare), bare ?? "null");
            string gone = (string)resolve.Invoke(null, new object[] { root2, "", "does_not_exist.wav" });
            Check("a missing stored sound resolves to null, not a crash",
                gone == null, gone ?? "null");

            string abs = Path.Combine(temp, "elsewhere.wav");
            File.WriteAllBytes(abs, Tone(500));
            string rAbs = (string)resolve.Invoke(null, new object[] { root2, "", abs });
            Check("an absolute stored path resolves as-is", rAbs == abs, rAbs ?? "null");

            // --- Scale: halves the amplitude, leaves the RIFF header intact ---
            byte[] tone = Tone(10000);
            int half = FirstSample(asm, tone);
            Check("volume 0.5 halves the 16-bit samples", half == 5000, "sample=" + half);
            byte[] halfWav = (byte[])cue.GetMethod("Scale", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { tone, 0.5 });
            Check("scaling leaves the RIFF/WAVE header bytes untouched",
                System.Text.Encoding.ASCII.GetString(halfWav, 0, 4) == "RIFF"
                && System.Text.Encoding.ASCII.GetString(halfWav, 8, 4) == "WAVE", "");
            byte[] passthru = (byte[])cue.GetMethod("Scale", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { tone, 1.0 });
            Check("volume 1.0 returns the bytes unchanged",
                passthru.Length == tone.Length && BitConverter.ToInt16(passthru, 44) == 10000, "");

            Console.WriteLine("---");
            Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
            return fails == 0 ? 0 : 1;
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
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
