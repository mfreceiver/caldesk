using System.Runtime.InteropServices;

namespace CalDesk.App.Windowing;

/// <summary>Minimal Win32 interop for desktop-gadget window behavior.</summary>
internal static class Win32
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

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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
