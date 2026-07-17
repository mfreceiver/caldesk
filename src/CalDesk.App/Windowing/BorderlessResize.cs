using System.Windows;
using System.Windows.Interop;

namespace CalDesk.App.Windowing;

/// <summary>Enables edge/corner resize on a borderless (WindowStyle=None) transparent WPF window
/// by handling WM_NCHITTEST.</summary>
public static class BorderlessResize
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    public static void Attach(Window w, double resizeBorder = 6)
    {
        var src = PresentationSource.FromVisual(w) as HwndSource
                  ?? HwndSource.FromHwnd(new WindowInteropHelper(w).EnsureHandle());
        if (src == null) return;
        src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            // Pack as Int64 so multi-monitor negative coords on 64-bit don't overflow ToInt32().
            var packed = lParam.ToInt64();
            var x = (short)(packed & 0xffff);
            var y = (short)((packed >> 16) & 0xffff);
            var p = w.PointFromScreen(new Point(x, y));
            var b = resizeBorder * (PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0);
            bool left = p.X <= b, right = p.X >= w.ActualWidth - b;
            bool top = p.Y <= b, bottom = p.Y >= w.ActualHeight - b;
            int hit = 0;
            if (top && left) hit = HTTOPLEFT;
            else if (top && right) hit = HTTOPRIGHT;
            else if (bottom && left) hit = HTBOTTOMLEFT;
            else if (bottom && right) hit = HTBOTTOMRIGHT;
            else if (left) hit = HTLEFT;
            else if (right) hit = HTRIGHT;
            else if (top) hit = HTTOP;
            else if (bottom) hit = HTBOTTOM;
            if (hit != 0) { handled = true; return new IntPtr(hit); }
            return IntPtr.Zero;
        });
    }
}
