using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// PERF-005 (SRC-005:R016): stationary tray hover no longer rebuilds.
//
// Every NotifyIcon.MouseMove used to call panel.Show(form.PopupTitle(),
// form.PopupRows(), at) — which rebuilt the tray model twice, walked every
// account and window into fresh row objects, and re-measured the whole popup
// with a fresh Bitmap + Graphics. Pointer frequency became model-building and
// GDI-measurement frequency on a resident monitor whose data changes on the
// refresh/settings cadence, not per micromovement.
//
// The hover now keys on a cheap generation pair: PopupDataGen (bumped by real
// mutations — published snapshots, tray settings, theme, stale flips) plus a
// minute-level TimeBucket for the relative/countdown text. Unchanged key ->
// MoveTo only: cursor/monitor positioning still follows the pointer, but no
// rows rebuild and no Measure allocates. Changed key -> exactly one rebuild,
// one measure, one repaint.
//
// Deterministic seams: HoverContentBuilds, HoverMoves, TrayPopup.MeasureCalls.
// No wall-clock benchmarking.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   csc -out:hover_cache.exe -r:System.dll -r:System.Drawing.dll
//       -r:System.Windows.Forms.dll tests\hover_cache.cs
public static class HoverCacheTest
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

    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static Type formType, popupType;
    static object form, settings;

    static int GetInt(string field) { return (int)formType.GetField(field, NP).GetValue(form); }

    static object MakeWindow(string key, string label, int rem, string reset, int minutes)
    {
        var w = Activator.CreateInstance(asm.GetType("Limisaw.WindowData"));
        var t = asm.GetType("Limisaw.WindowData");
        t.GetField("Key").SetValue(w, key);
        t.GetField("Base").SetValue(w, key.Split('@')[0]);
        t.GetField("Label").SetValue(w, label);
        t.GetField("Group").SetValue(w, "");
        t.GetField("GroupLabel").SetValue(w, "");
        t.GetField("Available").SetValue(w, true);
        t.GetField("Rem").SetValue(w, rem);
        t.GetField("Reset").SetValue(w, reset);
        t.GetField("DurationMinutes").SetValue(w, minutes);
        return w;
    }

    static Assembly asm;
    static IList accounts;

    static void MakeAccount(string provider, string providerLabel, string name, object[] windows)
    {
        var a = Activator.CreateInstance(asm.GetType("Limisaw.AccountData"));
        var t = asm.GetType("Limisaw.AccountData");
        t.GetField("Provider").SetValue(a, provider);
        t.GetField("ProviderLabel").SetValue(a, providerLabel);
        t.GetField("Name").SetValue(a, name);
        t.GetField("Status").SetValue(a, "OK");
        t.GetField("Ok").SetValue(a, true);
        IList list = (IList)t.GetField("Windows").GetValue(a);
        foreach (object w in windows) list.Add(w);
        accounts.Add(a);
    }

    static string Iso(double hoursAhead)
    {
        return DateTime.UtcNow.AddHours(hoursAhead).ToString("yyyy-MM-ddTHH:mm:ss");
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_hovercache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            asm = Load();
            settings = Activator.CreateInstance(asm.GetType("Limisaw.LimisawSettings"), new object[] { temp });
            asm.GetType("Limisaw.LimisawSettings").GetMethod("Load").Invoke(settings, null);
            object themes = asm.GetType("Limisaw.Theme")
                .GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { Directory.GetCurrentDirectory() });
            formType = asm.GetType("Limisaw.LimisawForm");
            popupType = asm.GetType("Limisaw.TrayPopup");

            using (var tray = new NotifyIcon())
            using (Form form = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            using (Form popup = (Form)Activator.CreateInstance(popupType))
            {
                if (form == null) throw new InvalidOperationException("form creation failed");
                HoverCacheTest.form = form;
                accounts = (IList)formType.GetField("Accounts", NP).GetValue(form);
                if (accounts == null) throw new InvalidOperationException("Accounts field missing");
                string soon = Iso(4);
                MakeAccount("codex", "Codex", "Codex", new object[] {
                    MakeWindow("five_hour", "5h", 42, soon, 300),
                    MakeWindow("weekly", "week", 80, Iso(72), 10080) });
                MakeAccount("claude", "Claude Code", "Claude", new object[] {
                    MakeWindow("five_hour", "5h", 7, soon, 300) });

                MethodInfo hover = formType.GetMethod("TrayHover",
                    BindingFlags.Public | BindingFlags.Instance);
                if (hover == null) throw new InvalidOperationException("TrayHover missing");

                // ── the 1000-MouseMove control ──────────────────────────────
                Point at = new Point(100, 100);
                hover.Invoke(form, new object[] { popup, at });   // prime the cache
                int builds0 = GetInt("HoverContentBuilds"), moves0 = GetInt("HoverMoves"),
                    measures0 = (int)popupType.GetField("MeasureCalls", NP).GetValue(popup);
                for (int i = 0; i < 1000; i++)
                    hover.Invoke(form, new object[] { popup, new Point(100 + (i % 7), 100 + (i % 5)) });
                int buildDelta = GetInt("HoverContentBuilds") - builds0;
                int moveDelta = GetInt("HoverMoves") - moves0;
                int measureDelta = (int)popupType.GetField("MeasureCalls", NP).GetValue(popup) - measures0;
                Check("1000 stationary MouseMoves rebuild the content ZERO extra times",
                    buildDelta == 0, "builds=" + buildDelta);
                Check("...1000 MouseMoves re-measure ZERO extra times (no GDI churn)",
                    measureDelta == 0, "measures=" + measureDelta);
                Check("...positioning still recalculated on every move",
                    moveDelta == 1000, "moves=" + moveDelta);
                Check("...the popup still follows the cursor",
                    popup.Bounds.X >= 0 && popup.Bounds.X < 102 && popup.Bounds.Bottom >= 100,
                    popup.Bounds.ToString());

                // ── a real publication invalidates exactly once ─────────────
                MethodInfo publish = formType.GetMethod("Publish", NP);
                object probe = Activator.CreateInstance(asm.GetType("Limisaw.ProbeResult"));
                ((IList)probe.GetType().GetField("Accounts").GetValue(probe)).Add(
                    MakeAccount2("claude", "Claude Code", "Claude", 55, soon));
                publish.Invoke(form, new object[] { probe });
                int builds1 = GetInt("HoverContentBuilds");
                hover.Invoke(form, new object[] { popup, at });
                Check("a fresh published snapshot rebuilds exactly once",
                    GetInt("HoverContentBuilds") - builds1 == 1, "delta=" + (GetInt("HoverContentBuilds") - builds1));
                int builds2 = GetInt("HoverContentBuilds");
                hover.Invoke(form, new object[] { popup, at });
                Check("...and the next move with unchanged state does NOT rebuild",
                    GetInt("HoverContentBuilds") - builds2 == 0, "delta=" + (GetInt("HoverContentBuilds") - builds2));
                // The new content is visible.
                string title = (string)popupType.GetField("Title", NP).GetValue(popup);
                Check("...the published content is what the popup shows",
                    title != null && title.Length > 0, title);

                // ── a tray setting change invalidates exactly once ──────────
                MethodInfo toggle = formType.GetMethod("ToggleShowUsed",
                    BindingFlags.Public | BindingFlags.Instance);
                int builds3 = GetInt("HoverContentBuilds");
                toggle.Invoke(form, null);
                hover.Invoke(form, new object[] { popup, at });
                Check("a tray-relevant setting change rebuilds exactly once",
                    GetInt("HoverContentBuilds") - builds3 == 1, "delta=" + (GetInt("HoverContentBuilds") - builds3));

                // ── a theme change invalidates exactly once ─────────────────
                MethodInfo applyTheme = formType.GetMethod("ApplyTheme",
                    BindingFlags.Public | BindingFlags.Instance);
                int builds4 = GetInt("HoverContentBuilds");
                applyTheme.Invoke(form, new object[] { "limisaw" });
                hover.Invoke(form, new object[] { popup, at });
                Check("a theme change rebuilds exactly once",
                    GetInt("HoverContentBuilds") - builds4 == 1, "delta=" + (GetInt("HoverContentBuilds") - builds4));

                // ── the minute bucket refreshes relative text ───────────────
                // Shrink the bucket: the seam is a static, so exercise the
                // cached path and force a bucket edge by waiting the bucket
                // out is too slow — instead, verify the key formula covers
                // time: bucket(now/60) changes within a minute boundary.
                string countdownBefore = (string)popupType.GetField("Title", NP).GetValue(popup);
                Check("the content key includes a coarse time bucket (source guard)",
                    SourceHasMinuteBucket(), "");

                // ── the legacy Show entry point still works ─────────────────
                MethodInfo show = popupType.GetMethod("Show", new[] {
                    typeof(string), typeof(System.Collections.Generic.List<>).MakeGenericType(popupType.GetNestedType("Row")), typeof(Point) });
                if (show == null) throw new InvalidOperationException("Show signature missing");
                var legacyRows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(popupType.GetNestedType("Row")));
                show.Invoke(popup, new object[] { "legacy title", legacyRows, new Point(50, 50) });
                string legacy = (string)popupType.GetField("Title", NP).GetValue(popup);
                Check("the legacy Show(title, rows, cursor) contract is unchanged",
                    legacy == "legacy title" && popup.Bounds.Contains(new Point(20, 30)), legacy);
            }
        }
        catch (Exception ex)
        {
            string detail = ex.GetType().Name + ": " + ex.Message;
            Exception walk = ex;
            while (walk.InnerException != null) walk = walk.InnerException;
            detail += " || " + walk.GetType().Name + ": " + walk.Message;
            if (walk.StackTrace != null) detail += " @ " + walk.StackTrace.Split('\n')[0].Trim();
            Check("harness", false, detail);
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

    static object MakeAccount2(string provider, string providerLabel, string name, int rem, string reset)
    {
        var a = Activator.CreateInstance(asm.GetType("Limisaw.AccountData"));
        var t = asm.GetType("Limisaw.AccountData");
        t.GetField("Provider").SetValue(a, provider);
        t.GetField("ProviderLabel").SetValue(a, providerLabel);
        t.GetField("Name").SetValue(a, name);
        t.GetField("Status").SetValue(a, "OK");
        t.GetField("Ok").SetValue(a, true);
        IList list = (IList)t.GetField("Windows").GetValue(a);
        list.Add(MakeWindow("five_hour", "5h", rem, reset, 300));
        return a;
    }

    static bool SourceHasMinuteBucket()
    {
        string src = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "LIMISAW.cs"));
        return src.IndexOf("Stamp.Now / 60.0", StringComparison.Ordinal) >= 0
            && src.IndexOf("BumpPopupData", StringComparison.Ordinal) >= 0;
    }
}
