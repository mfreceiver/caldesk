using System.Windows;
using System.Windows.Interop;

namespace CalDesk.App.Windowing;

/// <summary>Applies desktop-gadget window styles (no Alt+Tab, no focus steal, Z-order modes).</summary>
public static class WindowExtensions
{
    /// <summary>Ensure the window does not appear in Alt+Tab and does not steal focus on click.</summary>
    public static void ApplyGadgetStyles(this Window window)
    {
        var hwnd = EnsureHandle(window);
        Win32.AddExtendedStyle(hwnd, new IntPtr(Win32.WS_EX_TOOLWINDOW));
        Win32.AddExtendedStyle(hwnd, new IntPtr(Win32.WS_EX_NOACTIVATE));
    }

    /// <summary>Toggle mouse click-through.</summary>
    public static void SetClickThrough(this Window window, bool enabled)
    {
        var hwnd = EnsureHandle(window);
        if (enabled)
        {
            Win32.AddExtendedStyle(hwnd, new IntPtr(Win32.WS_EX_TRANSPARENT));
        }
        else
        {
            Win32.RemoveExtendedStyle(hwnd, new IntPtr(Win32.WS_EX_TRANSPARENT));
        }
    }

    /// <summary>Send the window below normal windows (Desktop mode).</summary>
    public static void SendToDesktop(this Window window)
    {
        var hwnd = EnsureHandle(window);
        _ = Win32.SetWindowPos(hwnd, Win32.HWND_BOTTOM, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>Pin the window above all normal windows (Topmost mode).</summary>
    public static void SetTopmost(this Window window, bool topmost)
    {
        var hwnd = EnsureHandle(window);
        var after = topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST;
        _ = Win32.SetWindowPos(hwnd, after, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private static IntPtr EnsureHandle(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
        {
            helper.EnsureHandle();
        }

        return helper.Handle;
    }
}
