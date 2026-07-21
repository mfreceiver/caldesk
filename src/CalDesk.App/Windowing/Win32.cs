using System.Runtime.InteropServices;

namespace CalDesk.App.Windowing;

/// <summary>Minimal Win32 interop for desktop-gadget window behavior.</summary>
internal static partial class Win32
{
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TOOLWINDOW = 0x00000080;     // hide from Alt+Tab / taskbar
    public const int WS_EX_NOACTIVATE = 0x08000000;      // do not steal focus on click
    public const int WS_EX_TRANSPARENT = 0x00000020;     // click-through

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM = new(1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    // T13: resize session边沿检测。WM_ENTERSIZEMOVE 在用户开始拖动边/角或移动窗口时
    // 立即由 DefWindowProc 发出；WM_EXITSIZEMOVE 在拖动结束（鼠标松开）发出。比
    // SizeChanged 边沿更可靠——后者在 minimize/restore/SetWidth/SetHeight 也会触发。
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;

    // DWM attributes (dwmapi) — used by DwmAppearance for system shadow + Win11 corners.
    public const int DWMWA_NCRENDERING_POLICY = 2;
    public const int DWMNCRP_ENABLED = 2;
    public const int DWMWA_ALLOW_NCPAINT = 4;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int cb);

    public static IntPtr GetExtendedStyle(IntPtr hWnd) => GetWindowLongPtr(hWnd, GWL_EXSTYLE);

    public static void SetExtendedStyle(IntPtr hWnd, IntPtr value) => SetWindowLongPtr(hWnd, GWL_EXSTYLE, value);

    /// <summary>Add an extended style flag, preserving the others.</summary>
    public static void AddExtendedStyle(IntPtr hWnd, IntPtr flag)
    {
        var current = GetExtendedStyle(hWnd);
        SetExtendedStyle(hWnd, new IntPtr(current.ToInt64() | flag.ToInt64()));
    }

    /// <summary>Remove an extended style flag.</summary>
    public static void RemoveExtendedStyle(IntPtr hWnd, IntPtr flag)
    {
        var current = GetExtendedStyle(hWnd);
        SetExtendedStyle(hWnd, new IntPtr(current.ToInt64() & ~flag.ToInt64()));
    }
}
