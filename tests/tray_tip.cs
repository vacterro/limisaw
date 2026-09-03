using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Two shell contracts the apps used to break silently:
//
// 1. NotifyIcon.Text throws above 63 characters. UpdateTray assigns the tooltip
//    BEFORE the icon, so one over-long tip aborted the whole tray update and
//    froze the tray on the previous number. The tip is now GROWN account by
//    account and every branch leaves through one clamp, so more vendors can
//    never push it over the limit.
// 2. WM_NCHITTEST LPARAM packs two SIGNED 16-bit screen coords. On a monitor
//    above the primary the y half sets the high bit; a checked IntPtr->int
//    conversion overflows and the title-strip drag dies on that monitor.
//
// Build + run:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:tray_tip.exe -r:System.dll -r:System.Drawing.dll ^
//     -r:System.Windows.Forms.dll tests\tray_tip.cs
//   tray_tip.exe            (exit 0 = all PASS)
public static class TrayTip
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    const int WM_NCHITTEST = 0x84, HTCAPTION = 2;

    static int fails = 0;
    static void Check(string name, bool ok, string detail)
    {
        if (ok) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    // ── verbatim clamp + BuildTip shape from LIMISAW.cs ──
    const int TrayTipMax = 63;

    class Account
    {
        public string Provider = "", Name = "";
        public bool Ok = true;
        public string Abbrev = "";
    }

    static string ShortText(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        return value.Substring(0, max - 3) + "...";
    }

    static string ClampTip(string tip)
    {
        if (string.IsNullOrEmpty(tip) || tip.Length <= TrayTipMax) return tip;
        return tip.Substring(0, TrayTipMax - 3) + "...";
    }

    static string ShortName(Account a)
    {
        string p = a.Provider.Length > 0 ? char.ToUpperInvariant(a.Provider[0]).ToString() : "?";
        string n = a.Name != null ? a.Name : "";
        for (int i = n.Length - 1; i >= 0; i--)
            if (char.IsDigit(n[i])) return p + n[i];
        return p;
    }

    static int ShownRem(int remaining, bool showUsed) { return showUsed ? 100 - remaining : remaining; }

    static string BuildTip(List<Account> accounts, bool stale, string metricLabel,
                           int value, bool available, bool showUsed)
    {
        string head = "LIMISAW | " + (showUsed ? "highest used " : "lowest remaining ");
        string tip = head + (available ? ShownRem(value, showUsed) + "% (" + ShortText(metricLabel, 28) + ")" : "--");
        foreach (Account a in accounts)
        {
            if (!a.Ok) continue;
            string piece = " | " + ShortName(a) + " " + a.Abbrev;
            if (tip.Length + piece.Length > TrayTipMax - (stale ? 8 : 0)) break;
            tip += piece;
        }
        if (stale) tip += " | stale";
        return ClampTip(tip);
    }

    // ── verbatim hit-test decode from LIMISAW.cs / SAITULS.cs / Problip.cs ──
    class HitForm : Form
    {
        public string Result = "no message";
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                try
                {
                    int raw = unchecked((int)m.LParam.ToInt64());
                    int x = raw & 0xFFFF; if (x > 0x7FFF) x -= 0x10000;
                    int y = (raw >> 16) & 0xFFFF; if (y > 0x7FFF) y -= 0x10000;
                    Result = x + "," + y;
                }
                catch (Exception ex) { Result = "THROW " + ex.GetType().Name; }
                m.Result = (IntPtr)HTCAPTION;
                return;
            }
            base.WndProc(ref m);
        }
    }

    static IntPtr PackPoint(int x, int y)
    {
        unchecked { return new IntPtr((long)(uint)(((y & 0xFFFF) << 16) | (x & 0xFFFF))); }
    }

    static List<Account> Fleet(int count)
    {
        // The worst realistic case: every vendor logged in, every window full,
        // long pool names in the metric label.
        var list = new List<Account>();
        string[] providers = { "codex", "codex", "codex", "claude", "antigravity" };
        string[] names = { "Codex", "Account2", "Account3Free", "Claude", "Antigravity" };
        string[] abbrevs = {
            "5h 100% \u00B7 week 100%", "5h 100% \u00B7 week 100%", "month 100%",
            "5h 100% \u00B7 week 100%",
            "week 100% \u00B7 5h 100% \u00B7 week 100%",
        };
        for (int i = 0; i < count && i < providers.Length; i++)
            list.Add(new Account { Provider = providers[i], Name = names[i], Abbrev = abbrevs[i] });
        return list;
    }

    public static int Main()
    {
        using (var tray = new NotifyIcon())
        {
            // ── 1. every tooltip the shell can be handed is assignable ──
            foreach (int fleet in new[] { 0, 1, 3, 5 })
                foreach (bool used in new[] { false, true })
                    foreach (bool stale in new[] { false, true })
                    {
                        string tip = BuildTip(Fleet(fleet), stale,
                            "Antigravity \u00B7 Antigravity \u00B7 week (Claude and GPT models)",
                            used ? 0 : 100, true, used);
                        bool assigned;
                        string why = "";
                        try { tray.Text = tip; assigned = true; }
                        catch (Exception ex) { assigned = false; why = ex.GetType().Name; }
                        Check("tooltip accepted: " + fleet + " accounts, used=" + used + ", stale=" + stale,
                            assigned && tip.Length <= TrayTipMax,
                            assigned ? "len=" + tip.Length : why);
                    }

            // an unclamped tip is genuinely rejected — proves the clamp is load-bearing
            bool rejected = false;
            try { tray.Text = new string('x', TrayTipMax + 1); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check("shell still rejects " + (TrayTipMax + 1) + " chars", rejected, "clamp is load-bearing");

            // ── 2. the reading itself ──
            var one = new List<Account>();
            Check("Left mode prints what remains",
                BuildTip(one, false, "Codex \u00B7 5h", 40, true, false) == "LIMISAW | lowest remaining 40% (Codex \u00B7 5h)",
                BuildTip(one, false, "Codex \u00B7 5h", 40, true, false));
            Check("Used mode prints the spent share",
                BuildTip(one, false, "Codex \u00B7 5h", 40, true, true) == "LIMISAW | highest used 60% (Codex \u00B7 5h)",
                BuildTip(one, false, "Codex \u00B7 5h", 40, true, true));
            Check("nothing available stays '--'",
                BuildTip(one, false, "Codex \u00B7 5h", 0, false, true) == "LIMISAW | highest used --",
                BuildTip(one, false, "Codex \u00B7 5h", 0, false, true));
            Check("stale is never dropped by the account loop",
                BuildTip(Fleet(5), true, "Codex \u00B7 5h", 100, true, false).EndsWith("| stale"),
                BuildTip(Fleet(5), true, "Codex \u00B7 5h", 100, true, false));
            Check("short tooltip is not truncated",
                !BuildTip(one, false, "Codex \u00B7 5h", 40, true, false).EndsWith("..."),
                BuildTip(one, false, "Codex \u00B7 5h", 40, true, false));
            Check("account initials disambiguate same-vendor accounts",
                ShortName(new Account { Provider = "codex", Name = "Account2" }) == "C2"
                && ShortName(new Account { Provider = "claude", Name = "Claude" }) == "C",
                ShortName(new Account { Provider = "codex", Name = "Account2" }));
        }

        // ── 3. hit-test decode on every monitor quadrant ──
        using (var f = new HitForm())
        {
            IntPtr h = f.Handle;
            var points = new int[][] {
                new int[] {  300,  400 },   // primary
                new int[] { -1200,  400 },  // monitor to the left
                new int[] {  300, -250 },   // monitor above  (used to overflow)
                new int[] { -1200, -250 },  // up-left        (used to overflow)
            };
            foreach (int[] p in points)
            {
                SendMessage(h, WM_NCHITTEST, IntPtr.Zero, PackPoint(p[0], p[1]));
                string want = p[0] + "," + p[1];
                Check("NCHITTEST decodes screen point " + want, f.Result == want, f.Result);
            }
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
