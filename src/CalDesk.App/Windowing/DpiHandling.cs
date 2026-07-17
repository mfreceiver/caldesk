using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CalDesk.App.Windowing;

/// <summary>DPI robustness: (1) apply crispness flags, (2) persist DIU bounds,
/// (3) conditionally adopt WM_DPICHANGED suggested rect IF testing shows auto-handling fails
/// for transparent windows. Per spec §1.3 — do NOT hook unless manual test proves it's needed.</summary>
public static class DpiHandling
{
    private const int WM_DPICHANGED = 0x02E0;

    public static void Attach(Window w)
    {
        // (1) crispness
        w.UseLayoutRounding = true;
        w.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(w, TextRenderingMode.Auto);

        // (3) conditional DPI hook — guarded so it's a no-op unless enabled.
        // NOTE: WPF PerMonitorV2 normally auto-applies the suggested rect. Enable here only if
        // Windows dual-DPI testing shows the transparent window mis-sizes on monitor switch.
        if (!EnableExplicitDpiHook) return;
        var src = HwndSource.FromHwnd(new WindowInteropHelper(w).EnsureHandle());
        src?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg != WM_DPICHANGED) return IntPtr.Zero;
            var rect = Marshal.PtrToStructure<NativeRect>(lp);
            var t = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice;
            double s = t?.M11 ?? 1.0;
            w.Left = rect.Left / s;
            w.Top = rect.Top / s;
            w.Width = (rect.Right - rect.Left) / s;
            w.Height = (rect.Bottom - rect.Top) / s;
            handled = true;
            return IntPtr.Zero;
        });
    }

    /// <summary>Flip to true ONLY after a dual-DPI test confirms auto-handling is insufficient.</summary>
    public static bool EnableExplicitDpiHook { get; set; } = false;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}
