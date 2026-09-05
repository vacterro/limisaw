using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Limisaw;

// W2-006: LimisawSettings.Read used a fixed 2048-char StringBuilder.
// GetPrivateProfileString copies what fits and returns nSize-1 when it ran out
// of room — it reports no error — so a TrayItems/AccountOrder list long enough
// to fill the buffer came back TRUNCATED, usually mid-id. The user's arranged
// order was then silently wrong after a restart, and a half-copied id matched
// no real reading, so a row could vanish or jump.
//
// The read now grows the buffer until the value fits. This harness writes real
// oversized values through the real Save(), reads them back through the real
// Load(), and asserts exact round-trip equality plus identical painted order —
// including the case where the 2048th character lands INSIDE an id, which is
// what proves no partial id is ever accepted.
//
// Build + run: pwsh .\build.ps1 -Tests   (engine-linked, -main IniLongValuesTest)
public static class IniLongValuesTest
{
    static int fails = 0, checks = 0;

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    static string Scratch;

    static LimisawSettings Fresh()
    {
        Scratch = Path.Combine(Path.GetTempPath(), "limisaw_inilong_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        return new LimisawSettings(Scratch);
    }

    // Ids shaped like the real ones: provider/account/window.
    static List<string> Ids(int count, int pad)
    {
        var ids = new List<string>();
        for (int i = 1; i <= count; i++)
            ids.Add("codex/" + new string('a', pad) + i.ToString("0000") + "/weekly");
        return ids;
    }

    static string Join(List<string> ids) { return string.Join("|", ids.ToArray()); }

    // The painted order is a private instance method on the form, and building a
    // form here would open a window. The ordering rule it applies to the loaded
    // value is Split() — saved order first, deduped — so the assertion that
    // matters at this layer is that Split sees every id, in order, uncut.
    static List<string> Order(string value) { return LimisawSettings.Split(value); }

    public static int Main()
    {
        Console.WriteLine("== the old 2048-char buffer: what it used to cut ==");
        // 200 windows is not hypothetical: three vendors x several accounts x
        // per-model windows reaches it, and each id is ~30 chars.
        List<string> many = Ids(200, 8);
        string trayValue = Join(many);
        Check("the fixture really exceeds the old buffer", trayValue.Length > 2048,
            "chars=" + trayValue.Length);

        var s = Fresh();
        s.TrayItems = trayValue;
        s.AccountOrder = Join(Ids(120, 12));
        s.Save();
        Check("the oversized save reports success", !s.LastSaveFailed, "");

        var reread = new LimisawSettings(Scratch);
        reread.Load();
        Check("TrayItems survives the round trip exactly",
            reread.TrayItems == trayValue,
            "wrote " + trayValue.Length + " chars, read " + reread.TrayItems.Length);
        Check("AccountOrder survives the round trip exactly",
            reread.AccountOrder == s.AccountOrder,
            "wrote " + s.AccountOrder.Length + " chars, read " + reread.AccountOrder.Length);
        Check("every tray id is back, in the same order",
            Order(reread.TrayItems).Count == many.Count
                && string.Join("|", Order(reread.TrayItems).ToArray()) == Join(many),
            "ids=" + Order(reread.TrayItems).Count + " of " + many.Count);
        Check("every card key is back, in the same order",
            string.Join("|", Order(reread.AccountOrder).ToArray()) == s.AccountOrder,
            "ids=" + Order(reread.AccountOrder).Count);
        Check("the last id is not the truncated one",
            Order(reread.TrayItems)[Order(reread.TrayItems).Count - 1] == many[many.Count - 1],
            Order(reread.TrayItems)[Order(reread.TrayItems).Count - 1]);

        Console.WriteLine();
        Console.WriteLine("== the 2048th char lands INSIDE an id: no partial id ==");
        // Build a value whose character 2048 falls in the middle of an id, so a
        // truncating read cannot help producing a fragment that looks like a
        // plausible id. Nothing may accept it.
        var ids = new List<string>();
        int len = 0;
        int n = 0;
        while (len < 2100)
        {
            string id = "antigravity/user" + (++n).ToString("000") + "/window-" + new string('z', 9);
            ids.Add(id);
            len += id.Length + 1;
        }
        string boundary = Join(ids);
        Check("the boundary fixture straddles 2048", boundary.Length > 2048, "chars=" + boundary.Length);
        int cutInto = 0;
        {
            int pos = 0;
            foreach (string id in ids)
            {
                if (pos < 2047 && pos + id.Length > 2047) { cutInto = id.Length; break; }
                pos += id.Length + 1;
            }
        }
        Check("...and char 2048 is inside an id, not on a separator", cutInto > 0,
            "the id crossing the boundary is " + cutInto + " chars");

        var b = Fresh();
        b.TrayItems = boundary;
        b.Save();
        var backB = new LimisawSettings(Scratch);
        backB.Load();
        Check("the straddling value round-trips whole", backB.TrayItems == boundary,
            "read " + backB.TrayItems.Length + " of " + boundary.Length);
        List<string> parsed = Order(backB.TrayItems);
        bool allWhole = parsed.Count == ids.Count;
        string bad = allWhole ? "" : parsed.Count + " ids of " + ids.Count;
        for (int i = 0; i < parsed.Count && allWhole; i++)
            if (parsed[i] != ids[i]) { allWhole = false; bad = "got \"" + parsed[i] + "\""; }
        Check("no id comes back as a fragment", allWhole, bad);

        Console.WriteLine();
        Console.WriteLine("== the reload path reads the same way ==");
        // Reload() is the Refresh-time re-read (W2-003). It must not be a second
        // implementation with the old ceiling.
        var live = Fresh();
        live.TrayItems = "";
        live.Save();
        File.AppendAllText(Path.Combine(Scratch, "LIMISAW.ini"), "");
        var edited = new LimisawSettings(Scratch);
        edited.Load();
        edited.TrayItems = trayValue;
        edited.Save();
        bool changed = live.Reload();
        Check("Reload notices the oversized external edit", changed, "");
        Check("...and reads it whole, not to 2048", live.TrayItems == trayValue,
            "read " + live.TrayItems.Length + " of " + trayValue.Length);

        Console.WriteLine();
        Console.WriteLine("== short values are untouched, and the loop is bounded ==");
        var small = Fresh();
        small.TrayItems = "codex/a/weekly|codex/a/hourly";
        small.AccountOrder = "codex/a";
        small.Save();
        var backSmall = new LimisawSettings(Scratch);
        backSmall.Load();
        Check("a short value still round-trips", backSmall.TrayItems == small.TrayItems
            && backSmall.AccountOrder == small.AccountOrder, "");
        FieldInfo growths = typeof(LimisawSettings).GetField("ReadGrowths",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Check("...and cost no buffer growth at all", growths != null && (int)growths.GetValue(backSmall) == 0,
            growths == null ? "ReadGrowths missing" : "growths=" + growths.GetValue(backSmall));
        Check("the oversized read DID have to grow",
            growths != null && (int)growths.GetValue(reread) > 0,
            growths == null ? "" : "growths=" + growths.GetValue(reread));

        // A missing key must still yield the documented default, not "".
        var defaults = Fresh();
        defaults.Load();
        Check("an absent key still returns its default",
            defaults.RefreshSeconds == 300 && defaults.ThemeSlug == "goldendefault",
            defaults.RefreshSeconds + "/" + defaults.ThemeSlug);

        // Source guard: the fixed-size read must not come back.
        string src = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "..", "..", "LIMISAW.cs"));
        Check("no fixed-capacity single-shot Read survives in source",
            src.IndexOf("StringBuilder(2048); GetPrivateProfileString", StringComparison.Ordinal) < 0, "");

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "PASS (" + checks + " checks, 0 failures)"
            : "FAILED (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }
}
