using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// CORE-003 (audit/7.md, SRC-011:R003): accepted-disk revision ownership.
//
// The audited defect: startup Load performed many independent
// GetPrivateProfileString reads and then unconditionally ran
//     AdoptBaseline(this);
//     AcceptDiskRevision();          // return value IGNORED
// GetPrivateProfileString cannot report "the file was unreadable" — against a
// locked ini it hands back the DEFAULT for every key. So a temporarily
// unreadable EXISTING ini produced a live object full of defaults, that state
// became the durable baseline, and AcceptDiskRevision failed silently leaving
//     AcceptedDiskAbsent == false && AcceptedDiskFingerprint == null
// Stage read that ambiguous combination as `neverAccepted` and skipped the
// pre-stage accepted-revision comparison entirely. Once the real ini became
// readable again, a later incidental or explicit save seeded its temp from the
// existing file and overwrote the owned values with bytes the process never
// accepted.
//
// Required invariant: overwrite authority is EXPLICIT and comes only from a
// coherent byte-revision acceptance. Three distinct states:
//   NO AUTHORITY     - nothing was ever coherently accepted;
//   ACCEPTED ABSENT  - the process coherently established no ini existed;
//   ACCEPTED PRESENT - one exact byte revision was parsed and accepted.
//
// Build + run: pwsh .\build.ps1 -Tests
public static class Core003SettingsRevisionTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static Type settingsType;

    static object New(string dir)
    {
        return Activator.CreateInstance(settingsType, new object[] { dir });
    }
    static void Load(object s) { settingsType.GetMethod("Load").Invoke(s, null); }
    static object SaveSettings(object s) { return settingsType.GetMethod("SaveSettings").Invoke(s, null); }
    static object SavePosition(object s) { return settingsType.GetMethod("SavePosition").Invoke(s, null); }
    static object ReloadEx(object s) { return settingsType.GetMethod("ReloadEx").Invoke(s, null); }
    static bool Saved(object r) { return (bool)r.GetType().GetField("Saved").GetValue(r); }
    static string Reason(object r) { return (string)r.GetType().GetField("Reason").GetValue(r); }
    static object Get(object s, string f) { return settingsType.GetField(f).GetValue(s); }
    static void Set(object s, string f, object v) { settingsType.GetField(f).SetValue(s, v); }

    // The authority projection. Pre-fix there is no AcceptedDiskAuthority
    // field at all, which is itself the NO-AUTHORITY truth: the field returns
    // null and Authority() reads false. The behavioural checks below are the
    // real contract; this only names the state.
    static bool Authority(object s)
    {
        FieldInfo f = settingsType.GetField("AcceptedDiskAuthority",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return f != null && (bool)f.GetValue(s);
    }
    static bool AccAbsent(object s)
    {
        return (bool)settingsType.GetField("AcceptedDiskAbsent",
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(s);
    }
    static string AccFp(object s)
    {
        FieldInfo f = settingsType.GetField("AcceptedDiskFingerprint",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return f == null ? null : (string)f.GetValue(s);
    }
    static string RevisionCheck(object s)
    {
        MethodInfo m = settingsType.GetMethod("DiskRevisionMatches",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return m == null ? "(no DiskRevisionMatches)" : Convert.ToString(m.Invoke(s, null));
    }

    static string Sha(string path)
    {
        using (var sha = SHA256.Create())
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            byte[] h = sha.ComputeHash(fs);
            var sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

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

    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "limisaw_core003_" + tag + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static int Main()
    {
        string root = Directory.GetCurrentDirectory();
        try
        {
            string exe = Path.Combine(root, "LIMISAW.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "..", "LIMISAW.exe");
            Assembly asm = Assembly.LoadFrom(Path.GetFullPath(exe));
            settingsType = asm.GetType("Limisaw.LimisawSettings");
            if (settingsType == null) throw new Exception("Limisaw.LimisawSettings not found in " + exe);

            ScenarioA(root);
            ScenarioB(root);
            ScenarioC(root);
            ScenarioD(root);
            ScenarioE(root);
            ScenarioF(root);
        }
        catch (Exception ex)
        {
            fails++;
            Console.WriteLine("FAIL  harness threw");
            Console.WriteLine(ex.ToString());
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }

    // ── A: unreadable existing ini at startup ───────────────────────────────
    static void ScenarioA(string root)
    {
        Console.WriteLine("== A: an unreadable EXISTING ini at startup grants NO AUTHORITY ==");
        string dir = TempDir("a");
        string ini = Path.Combine(dir, "LIMISAW.ini");
        File.WriteAllLines(ini, new string[]
        {
            "[limisaw]",
            "Theme=nord",
            "LowPct=33",
            "FutureKey=keep-me",
            "[codex]",
            "session=abc123",
        });
        byte[] before = File.ReadAllBytes(ini);

        object s = New(dir);
        // The real Windows unreadability technique: an exclusive handle.
        using (var hold = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Load(s);
        }

        Check("NO AUTHORITY after an unreadable startup read",
            !Authority(s) && !AccAbsent(s) && AccFp(s) == null,
            "authority=" + Authority(s) + " absent=" + AccAbsent(s) + " fp=" + (AccFp(s) ?? "null"));
        Check("the live object holds the class defaults, not a parse of the locked file",
            (string)Get(s, "ThemeSlug") == "goldendefault" && (int)Get(s, "LowPct") == 20,
            (string)Get(s, "ThemeSlug") + "/" + Get(s, "LowPct"));

        // The file is readable again NOW — that does not retroactively prove
        // the process accepted any revision.
        object p = SavePosition(s);
        Check("an incidental SavePosition is refused while NO AUTHORITY",
            !Saved(p) && Reason(p) != null && Reason(p).Length > 0,
            "Saved=" + Saved(p) + " reason=" + Reason(p));
        object e = SaveSettings(s);
        Check("an explicit SaveSettings is refused while NO AUTHORITY",
            !Saved(e) && Reason(e) != null && Reason(e).Length > 0,
            "Saved=" + Saved(e) + " reason=" + Reason(e));

        byte[] after = File.ReadAllBytes(ini);
        Check("the existing file is byte-identical (nothing was overwritten)",
            SameBytes(before, after), "bytes=" + after.Length);
        string text = File.ReadAllText(ini);
        Check("the distinctive owned values survive (Theme=nord, LowPct=33)",
            text.IndexOf("Theme=nord", StringComparison.Ordinal) >= 0
            && text.IndexOf("LowPct=33", StringComparison.Ordinal) >= 0, "");
        Check("the unknown owned-section key survives",
            text.IndexOf("FutureKey=keep-me", StringComparison.Ordinal) >= 0, "");
        Check("the foreign section survives",
            text.IndexOf("[codex]", StringComparison.Ordinal) >= 0
            && text.IndexOf("session=abc123", StringComparison.Ordinal) >= 0, "");
        Check("no staging residue remains", !File.Exists(ini + ".tmp"), ini + ".tmp");
        Check("the refusal is explicit, not reported as a successful save",
            !Saved(p) && !Saved(e), "");
    }

    // ── B: recovery after an unreadable startup ─────────────────────────────
    static void ScenarioB(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== B: ReloadEx recovers NO AUTHORITY and the save proceeds ==");
        string dir = TempDir("b");
        string ini = Path.Combine(dir, "LIMISAW.ini");
        File.WriteAllLines(ini, new string[]
        {
            "[limisaw]",
            "Theme=nord",
            "LowPct=33",
            "FutureKey=keep-me",
            "[codex]",
            "session=abc123",
        });

        object s = New(dir);
        using (var hold = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Load(s);
        }
        Check("B fixture starts with NO AUTHORITY", !Authority(s), "");

        object r = ReloadEx(s);
        bool accepted = (bool)r.GetType().GetField("Accepted").GetValue(r);
        Check("ReloadEx coherently accepts the real disk revision", accepted, "Accepted=" + accepted);
        Check("authority becomes ACCEPTED PRESENT with the exact revision",
            Authority(s) && !AccAbsent(s) && AccFp(s) == Sha(ini),
            "fp=" + (AccFp(s) ?? "null"));

        Set(s, "ThemeSlug", "dracula");
        Set(s, "LowPct", 44);
        object b = SaveSettings(s);
        Check("the save now succeeds", Saved(b), "Saved=" + Saved(b) + " reason=" + Reason(b));
        string text = File.ReadAllText(ini);
        Check("the new user change landed",
            text.IndexOf("Theme=dracula", StringComparison.Ordinal) >= 0
            && text.IndexOf("LowPct=44", StringComparison.Ordinal) >= 0, "");
        Check("unknown keys survive the save",
            text.IndexOf("FutureKey=keep-me", StringComparison.Ordinal) >= 0, "");
        Check("foreign sections survive the save",
            text.IndexOf("[codex]", StringComparison.Ordinal) >= 0
            && text.IndexOf("session=abc123", StringComparison.Ordinal) >= 0, "");
        Check("the committed bytes are the new accepted revision",
            Authority(s) && AccFp(s) == Sha(ini), "fp=" + (AccFp(s) ?? "null"));
    }

    // ── C: revision changes during the startup parse ────────────────────────
    static void ScenarioC(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== C: a revision that moves during the startup parse is not accepted ==");
        FieldInfo seam = settingsType.GetField("BeforeStartupParse",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (seam == null)
        {
            Console.WriteLine("NOTE  C: no startup-parse seam in this build (pre-fix) — scenario C not exercised");
            return;
        }
        string dir = TempDir("c");
        string ini = Path.Combine(dir, "LIMISAW.ini");
        File.WriteAllLines(ini, new string[] { "[limisaw]", "Theme=nord", "LowPct=33" });

        object s = New(dir);
        // Deterministic: while Load holds the before-fingerprint, the ini is
        // replaced with revision B before the candidate parse runs.
        seam.SetValue(s, (Action)(() => File.WriteAllLines(ini,
            new string[] { "[limisaw]", "Theme=dracula", "LowPct=77" })));
        Load(s);
        seam.SetValue(s, null);

        Check("a moving revision during startup parse leaves NO AUTHORITY",
            !Authority(s) && !AccAbsent(s) && AccFp(s) == null,
            "authority=" + Authority(s));
        Check("the A/B hybrid is not adopted as the durable baseline",
            (string)Get(s, "ThemeSlug") == "goldendefault" && (int)Get(s, "LowPct") == 20,
            (string)Get(s, "ThemeSlug") + "/" + Get(s, "LowPct"));
        Check("no ACCEPTED PRESENT authority is granted",
            AccFp(s) == null, "fp=" + (AccFp(s) ?? "null"));
        object p = SavePosition(s);
        Check("a save against the existing file is refused",
            !Saved(p), "Saved=" + Saved(p) + " reason=" + Reason(p));

        object r = ReloadEx(s);
        Check("a later stable ReloadEx recovers",
            (bool)r.GetType().GetField("Accepted").GetValue(r) && Authority(s),
            "Accepted=" + r.GetType().GetField("Accepted").GetValue(r));
    }

    // ── D: coherent startup ─────────────────────────────────────────────────
    static void ScenarioD(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== D: one stable readable ini is accepted cleanly ==");
        string dir = TempDir("d");
        string ini = Path.Combine(dir, "LIMISAW.ini");
        File.WriteAllLines(ini, new string[] { "[limisaw]", "Theme=nord", "LowPct=33" });

        object s = New(dir);
        Load(s);
        Check("Load accepts a stable readable revision",
            Authority(s) && !AccAbsent(s) && AccFp(s) == Sha(ini), "fp=" + (AccFp(s) ?? "null"));
        Check("the live values match the file",
            (string)Get(s, "ThemeSlug") == "nord" && (int)Get(s, "LowPct") == 33,
            (string)Get(s, "ThemeSlug") + "/" + Get(s, "LowPct"));
        Check("the durable baseline is the accepted candidate", !(bool)settingsType.GetProperty("Dirty").GetValue(s, null), "");
        Check("DiskRevisionMatches reports Match", RevisionCheck(s) == "Match", RevisionCheck(s));

        Set(s, "ThemeSlug", "dracula");
        object d = SaveSettings(s);
        Check("a normal later save succeeds", Saved(d), "Saved=" + Saved(d) + " reason=" + Reason(d));
        Check("the committed revision is accepted",
            Authority(s) && AccFp(s) == Sha(ini), "fp=" + (AccFp(s) ?? "null"));
    }

    // ── E: absent first run ─────────────────────────────────────────────────
    static void ScenarioE(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== E: an absent first run still creates the ini ==");
        string dir = TempDir("e");
        string ini = Path.Combine(dir, "LIMISAW.ini");

        object s = New(dir);
        Load(s);
        Check("an absent startup is ACCEPTED ABSENT",
            Authority(s) && AccAbsent(s) && AccFp(s) == null,
            "authority=" + Authority(s) + " absent=" + AccAbsent(s));
        Check("normal defaults stay the live values",
            (int)Get(s, "RefreshSeconds") == 300 && (string)Get(s, "ThemeSlug") == "goldendefault",
            (string)Get(s, "ThemeSlug"));
        Check("accepted-absent answers Match", RevisionCheck(s) == "Match", RevisionCheck(s));

        Set(s, "ThemeSlug", "nord");
        object e = SaveSettings(s);
        Check("the first SaveSettings creates LIMISAW.ini",
            Saved(e) && File.Exists(ini), "Saved=" + Saved(e) + " reason=" + Reason(e));
        Check("the created revision becomes ACCEPTED PRESENT",
            Authority(s) && !AccAbsent(s) && AccFp(s) == Sha(ini), "fp=" + (AccFp(s) ?? "null"));
    }

    // ── F: a successful Stage installs its own staged revision ──────────────
    static void ScenarioF(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== F: a successful Stage installs its staged fingerprint directly ==");
        string dir = TempDir("f");
        string ini = Path.Combine(dir, "LIMISAW.ini");
        File.WriteAllLines(ini, new string[] { "[limisaw]", "Theme=nord" });

        object s = New(dir);
        Load(s);
        Set(s, "ThemeSlug", "dracula");
        object f = SaveSettings(s);
        Check("F: the staged save lands", Saved(f), "Saved=" + Saved(f) + " reason=" + Reason(f));
        Check("F: accepted-present fingerprint equals the committed bytes",
            Authority(s) && !AccAbsent(s) && AccFp(s) == Sha(ini), "fp=" + (AccFp(s) ?? "null"));
        Check("F: the next unchanged revision check sees Match",
            RevisionCheck(s) == "Match", RevisionCheck(s));

        string src = File.ReadAllText(Path.Combine(SourceRoot(), "LIMISAW.cs"));
        Check("F: Stage installs its staged fingerprint directly",
            src.IndexOf("AcceptDiskPresent(stagedFingerprint)", StringComparison.Ordinal) >= 0, "");
        Check("F: the blind post-commit AcceptDiskRevision reread is gone",
            src.IndexOf("AcceptDiskRevision()", StringComparison.Ordinal) < 0, "");
    }
}
