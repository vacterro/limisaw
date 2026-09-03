using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// The countdown readout (T-104): TrayShow off/pct/time over the single number.
// Rules under test:
//
//   * CountdownText: minutes under an hour ("12m"), whole hours under two days
//     ("3h"), days above that ("2d"); unknown/unparseable/past stamp is "--",
//     never "0m" (zero minutes reads as "right now" and lies on stale data);
//   * DrawSingle with TrayShow=off draws nothing (icon only);
//   * TrayShow round-trips through save/load and rejects junk.
//
// Build + run (from the repo root, after building LIMISAW.exe):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:tray_countdown.exe -r:System.dll -r:System.Drawing.dll ^
//     -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll tests\tray_countdown.cs
//   tray_countdown.exe            (exit 0 = all PASS)
public static class TrayCountdownTest
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

    static string Iso(DateTime dt) { return dt.ToString("yyyy-MM-ddTHH:mm:ss"); }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "limisaw_count_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assembly asm = Load();
            Type settingsType = asm.GetType("Limisaw.LimisawSettings");
            Type themeType = asm.GetType("Limisaw.Theme");
            Type formType = asm.GetType("Limisaw.LimisawForm");
            BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

            object settings = Activator.CreateInstance(settingsType, new object[] { temp });
            settingsType.GetMethod("Load").Invoke(settings, null);
            object themes = themeType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { Directory.GetCurrentDirectory() });

            // --- CountdownText unit checks (static, no form needed) ---
            var cd = formType.GetMethod("CountdownText", BindingFlags.NonPublic | BindingFlags.Static);
            Func<string, string> T = iso => (string)cd.Invoke(null, new object[] { iso });
            // A minute can tick over between building the stamp and reading it,
            // so accept either side of the boundary.
            string m12 = T(Iso(DateTime.Now.AddMinutes(12)));
            Check("12 minutes out reads 12m", m12 == "12m" || m12 == "11m", m12);
            Check("3 hours out reads 3h", T(Iso(DateTime.Now.AddHours(3))) == "3h",
                T(Iso(DateTime.Now.AddHours(3))));
            Check("2 days out reads 2d", T(Iso(DateTime.Now.AddDays(2))) == "2d",
                T(Iso(DateTime.Now.AddDays(2))));
            Check("empty stamp is --", T(null) == "--" && T("") == "--", "");
            Check("garbage stamp is --", T("not-a-date") == "--", T("not-a-date"));
            Check("a past stamp is --, never 0m", T(Iso(DateTime.Now.AddHours(-1))) == "--",
                T(Iso(DateTime.Now.AddHours(-1))));

            // --- DrawSingle honours TrayShow ---
            using (var tray = new NotifyIcon())
            using (Form form = (Form)Activator.CreateInstance(formType, new object[] { temp, settings, tray, themes }))
            {
                formType.GetField("Refreshing", NP).SetValue(form, true);
                var draw = formType.GetMethod("DrawSingle", NP, null,
                    new Type[] { typeof(Graphics), typeof(int), typeof(bool), typeof(string) }, null);
                string soon = Iso(DateTime.Now.AddMinutes(45));

                settingsType.GetField("TrayShow").SetValue(settings, "off");
                using (var bmp = new Bitmap(16, 16))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Magenta);
                    draw.Invoke(form, new object[] { g, 80, true, soon });
                    Check("TrayShow=off draws nothing (icon only)",
                        bmp.GetPixel(8, 8).ToArgb() == Color.Magenta.ToArgb(), "");
                }

                settingsType.GetField("TrayShow").SetValue(settings, "time");
                using (var bmp = new Bitmap(16, 16))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Magenta);
                    draw.Invoke(form, new object[] { g, 80, true, soon });
                    bool anyInk = false;
                    for (int y = 0; y < 16 && !anyInk; y++)
                        for (int x = 0; x < 16 && !anyInk; x++)
                            if (bmp.GetPixel(x, y).ToArgb() != Color.Magenta.ToArgb()) anyInk = true;
                    Check("TrayShow=time draws the countdown", anyInk, "");
                }

                // --- TrayShow persists + clamps ---
                settingsType.GetField("TrayShow").SetValue(settings, "time");
                settingsType.GetMethod("Save").Invoke(settings, null);
                object s2 = Activator.CreateInstance(settingsType, new object[] { temp });
                settingsType.GetMethod("Load").Invoke(s2, null);
                Check("TrayShow=time survives save/load",
                    (string)settingsType.GetField("TrayShow").GetValue(s2) == "time", "");
                File.WriteAllText(Path.Combine(temp, "LIMISAW.ini"), "[limisaw]\r\nTrayShow=bogus\r\n");
                object s3 = Activator.CreateInstance(settingsType, new object[] { temp });
                settingsType.GetMethod("Load").Invoke(s3, null);
                Check("a bogus TrayShow falls back to pct",
                    (string)settingsType.GetField("TrayShow").GetValue(s3) == "pct",
                    (string)settingsType.GetField("TrayShow").GetValue(s3));
            }

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
