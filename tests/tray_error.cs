using System;

// Replays the UpdateTray failure/recovery contract and the footer precedence
// rule verbatim, with the render step forced to throw. Proves the swallow is
// gone: a failure is recorded and shown, and a later success clears it.
public static class TrayError
{
    static string TrayErrorMsg = "";
    static string LastError = "";
    static int RefreshCalls = 0;
    static bool FailRender = false;
    static bool Threw = false;

    static void RenderTrayBitmap()
    {
        if (FailRender) throw new InvalidOperationException("GetHicon failed");
    }

    // ── verbatim shape from LIMISAW.cs UpdateTray ──
    static void UpdateTray()
    {
        try
        {
            RenderTrayBitmap();
            TrayErrorMsg = "";
        }
        catch (Exception ex)
        {
            TrayErrorMsg = ex.GetType().Name + ": " + ex.Message;
            try { RefreshCalls++; } catch { }
        }
    }

    static void CallUpdateTray()
    {
        Threw = false;
        try { UpdateTray(); }
        catch { Threw = true; }
    }

    // ── verbatim footer precedence from LIMISAW.cs OnPaint ──
    static string Footer(int refreshSeconds)
    {
        string problem = LastError.Length > 0 ? "Error: " + LastError
            : TrayErrorMsg.Length > 0 ? "Tray icon failed: " + TrayErrorMsg : "";
        return problem.Length > 0 ? problem
            : "Auto-refresh " + (refreshSeconds / 60) + " min - F5 refresh - U used/left - Esc hide";
    }

    static int fails = 0;
    static void Check(string name, bool cond, string detail)
    {
        if (cond) Console.WriteLine("PASS  " + name + (detail.Length > 0 ? "  -> " + detail : ""));
        else { fails++; Console.WriteLine("FAIL  " + name + "  -> " + detail); }
    }

    public static int Main()
    {
        // 1. healthy render leaves no error and never throws
        FailRender = false; LastError = ""; TrayErrorMsg = ""; RefreshCalls = 0;
        CallUpdateTray();
        Check("healthy render records no error", TrayErrorMsg == "" && !Threw, "trayError=" + (TrayErrorMsg == "" ? "(empty)" : TrayErrorMsg));
        Check("healthy footer is the normal hint", Footer(180).StartsWith("Auto-refresh"), Footer(180));

        // 2. render failure is CAPTURED, not swallowed, and still does not throw
        FailRender = true;
        CallUpdateTray();
        Check("render failure does not escape to the caller", !Threw, "threw=" + Threw);
        Check("render failure is recorded", TrayErrorMsg == "InvalidOperationException: GetHicon failed", TrayErrorMsg);
        Check("failure triggers a repaint", RefreshCalls == 1, "Refresh() calls=" + RefreshCalls);
        Check("footer surfaces the failure", Footer(180) == "Tray icon failed: InvalidOperationException: GetHicon failed", Footer(180));

        // 3. a fetch error outranks the tray error
        LastError = "python not found";
        Check("fetch error outranks tray error", Footer(180) == "Error: python not found", Footer(180));

        // 4. recovery clears the tray error
        LastError = ""; FailRender = false;
        CallUpdateTray();
        Check("successful render clears the error", TrayErrorMsg == "", "trayError=" + (TrayErrorMsg == "" ? "(empty)" : TrayErrorMsg));
        Check("footer returns to the normal hint", Footer(180).StartsWith("Auto-refresh"), Footer(180));

        // 5. repeated failures do not accumulate state
        FailRender = true;
        CallUpdateTray(); CallUpdateTray(); CallUpdateTray();
        Check("repeated failures keep one current reason", TrayErrorMsg == "InvalidOperationException: GetHicon failed" && !Threw, "calls survived, trayError=" + TrayErrorMsg);

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "PASS (0 failures)" : "FAILED (" + fails + " failures)");
        return fails == 0 ? 0 : 1;
    }
}
