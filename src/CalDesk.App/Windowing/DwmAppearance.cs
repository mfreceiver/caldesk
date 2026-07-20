namespace CalDesk.App.Windowing;

/// <summary>
/// Spike / T12: apply DWM system chrome (shadow + rounded corners) on opaque WindowChrome windows.
/// Win11 only; Win10 is a silent no-op (fallback left empty for T12 polish if needed).
/// </summary>
public static class DwmAppearance
{
    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Build 22000+ = Windows 11. Win10: leave appearance to WPF (no DWM corner/shadow).
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        try
        {
            // Enable non-client DWM rendering so the system can draw a window shadow.
            int policy = Win32.DWMNCRP_ENABLED;
            _ = Win32.DwmSetWindowAttribute(
                hwnd, Win32.DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));

            int allowNcPaint = 1;
            _ = Win32.DwmSetWindowAttribute(
                hwnd, Win32.DWMWA_ALLOW_NCPAINT, ref allowNcPaint, sizeof(int));

            // Rounded corners (DWMWCP_ROUND).
            int corner = Win32.DWMWCP_ROUND;
            _ = Win32.DwmSetWindowAttribute(
                hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch
        {
            // Silent: appearance is best-effort; never block window show.
        }
    }
}
