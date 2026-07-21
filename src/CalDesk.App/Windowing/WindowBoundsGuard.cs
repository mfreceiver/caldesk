using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CalDesk.App.Windowing;

/// <summary>
/// T10 / G3: 恢复窗口 Left/Top 后, 校验窗口矩形是否至少有一部分落在当前可见屏幕
/// 工作区内. 若窗口完全在屏外 (例如保存位置所在的显示器已断开), clamp 到原中心点
/// 所在显示器的 WorkArea; 中心点也不在任何显示器时回退到主屏 WorkArea.
///
/// 实现说明:
/// <list type="bullet">
/// <item>多屏支持: 通过 Win32 <c>EnumDisplayMonitors</c> + <c>GetMonitorInfo</c> 枚举所有
///   显示器, 包含主屏左侧的负坐标副屏.</item>
/// <item>DPI 安全: 窗口的 WPF Left/Top/ActualWidth/ActualHeight 为 DIP (Device
///   Independent Pixel) 坐标; 而 Win32 MONITORINFO.rcWork 为物理像素坐标. 本方法
///   使用 <c>PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice</c>
///   获取 DPI 缩放因子, 将窗口 DIP 坐标转换为物理像素后与显示器工作区 (物理像素)
///   比较, 再将结果 clamp 回 DIP 坐标赋值. 全程统一在物理像素空间作交集/包含测试,
///   避免 PerMonitorV2 缩放失真.</item>
/// <item>NaN / Infinity / 零尺寸: 任何异常值都直接 return — 不动窗口, 启动不被打断.</item>
/// <item>不引入新包: 完全基于 Win32 API (user32), 沿用 <see cref="Win32"/> 的
///   <c>LibraryImport</c> source-gen 风格, 复用现有 AllowUnsafeBlocks 配置.</item>
/// <item>线程模型: 仅在 UI 线程调用; 不持有共享状态, callback 委托仅在调用期间 alive.</item>
/// </list>
/// </summary>
public static partial class WindowBoundsGuard
{
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    /// <summary>最小可见面积阈值 (物理像素). 窗口与任一 WorkArea 交集面积 &lt; 16×16 时
    /// 视为"完全在屏外", 触发 clamp. 选 16 是为了让 8×8 拖动手柄也算可见.</summary>
    private const double MinVisibleSidePhys = 16.0;

    /// <summary>获取窗口当前的 DPI 缩放因子 (物理像素 / DIP).</summary>
    /// <returns>缩放因子, 若窗口未展示则返回 1.0.</returns>
    private static double GetDpiScale(Window w)
    {
        var t = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice;
        return t?.M11 ?? 1.0;
    }

    /// <summary>Clamp <paramref name="w"/> 的 Left/Top 到当前可见屏幕工作区.
    /// 调用前请确保 <paramref name="w"/> 已被 Show 过 (<see cref="Window.ActualWidth"/>/
    /// <c>ActualHeight</c> 非零). 该方法是幂等的: 已可见的窗口不会被搬动.</summary>
    public static void ClampOntoVisibleScreen(Window w)
    {
        if (w is null) return;

        // 1. 读取窗口 DIP 位置/大小
        var leftDip = w.Left;
        var topDip = w.Top;
        var widthDip = w.ActualWidth;
        var heightDip = w.ActualHeight;

        // NaN / Infinity / 零尺寸 — 启动早期或 layout 未完成, 不动.
        if (double.IsNaN(leftDip) || double.IsNaN(topDip) ||
            double.IsInfinity(leftDip) || double.IsInfinity(topDip) ||
            double.IsNaN(widthDip) || double.IsNaN(heightDip) ||
            double.IsInfinity(widthDip) || double.IsInfinity(heightDip) ||
            widthDip <= 0 || heightDip <= 0)
        {
            return;
        }

        // 2. 获取 DPI 缩放因子, 转换到物理像素空间
        var scale = GetDpiScale(w);

        var leftPhys = leftDip * scale;
        var topPhys = topDip * scale;
        var widthPhys = widthDip * scale;
        var heightPhys = heightDip * scale;
        var rightPhys = leftPhys + widthPhys;
        var bottomPhys = topPhys + heightPhys;
        var centerXPhys = leftPhys + widthPhys / 2;
        var centerYPhys = topPhys + heightPhys / 2;

        // 3. 枚举显示器工作区 (物理像素)
        var (monitors, primaryMonitor) = EnumerateWorkAreas();
        if (monitors.Count == 0)
        {
            // 没拿到任何显示器 (极早启动或 headless), 不动.
            return;
        }

        // 4. 查找最大重叠显示器 与 包含窗口中心点的显示器
        RectD? bestWorkByOverlap = null;
        double bestOverlap = 0;
        RectD? workContainingCenter = null;

        foreach (var m in monitors)
        {
            // 交集面积 (物理像素)
            var ix1 = Math.Max(leftPhys, m.Left);
            var iy1 = Math.Max(topPhys, m.Top);
            var ix2 = Math.Min(rightPhys, m.Right);
            var iy2 = Math.Min(bottomPhys, m.Bottom);
            if (ix2 > ix1 && iy2 > iy1)
            {
                var overlap = (ix2 - ix1) * (iy2 - iy1);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    bestWorkByOverlap = m;
                }
            }

            if (workContainingCenter is null &&
                centerXPhys >= m.Left && centerXPhys < m.Right &&
                centerYPhys >= m.Top && centerYPhys < m.Bottom)
            {
                workContainingCenter = m;
            }
        }

        // 至少 MinVisibleSidePhys × MinVisibleSidePhys 物理像素可见 — 视为可见, 不动.
        var thresholdArea = MinVisibleSidePhys * MinVisibleSidePhys;
        if (bestOverlap >= thresholdArea)
        {
            return;
        }

        // 5. 完全在屏外. 选目标 WorkArea:
        //   1) 窗口中心点所在显示器 (用户期望的原位置所在屏) — 优先.
        //   2) 否则: 上述"最大重叠"的那个显示器.
        //   3) 最后兜底: 主屏 (通过 MONITORINFOF_PRIMARY 标记识别).
        RectD target;
        if (workContainingCenter is { } wc)
        {
            target = wc;
        }
        else if (bestWorkByOverlap is { } bo)
        {
            target = bo;
        }
        else if (primaryMonitor is { } pm)
        {
            target = pm;
        }
        else
        {
            // 极罕见: 无任何显示器有 PRIMARY 标记, 退到第一个枚举结果.
            target = monitors[0];
        }

        // 6. 把窗口物理矩形 clamp 到 target 内. 优先保持原始尺寸, 仅当即使完全移动后
        //    窗口依然可见面积不足阈值时再收缩尺寸.
        double newLeftPhys, newTopPhys, newWidthPhys, newHeightPhys;

        // 首先尝试仅移动, 保持 size.
        newWidthPhys = widthPhys;
        newHeightPhys = heightPhys;
        newLeftPhys = Math.Max(target.Left, Math.Min(leftPhys, target.Right - newWidthPhys));
        newTopPhys = Math.Max(target.Top, Math.Min(topPhys, target.Bottom - newHeightPhys));

        // 验证移动后是否仍有足够可见面积
        var movedRight = newLeftPhys + newWidthPhys;
        var movedBottom = newTopPhys + newHeightPhys;
        var movedOverlapX = Math.Min(movedRight, target.Right) - Math.Max(newLeftPhys, target.Left);
        var movedOverlapY = Math.Min(movedBottom, target.Bottom) - Math.Max(newTopPhys, target.Top);
        var movedOverlapArea = Math.Max(0, movedOverlapX) * Math.Max(0, movedOverlapY);

        if (movedOverlapArea < thresholdArea)
        {
            // 移动后仍不足 — 收缩尺寸到 target 内
            newWidthPhys = Math.Min(widthPhys, target.Width);
            newHeightPhys = Math.Min(heightPhys, target.Height);
            newLeftPhys = Math.Max(target.Left, Math.Min(leftPhys, target.Right - newWidthPhys));
            newTopPhys = Math.Max(target.Top, Math.Min(topPhys, target.Bottom - newHeightPhys));
        }

        // 7. 极端数值防御 (上式理论上不可能 NaN, 但保持鲁棒性).
        if (double.IsNaN(newLeftPhys) || double.IsNaN(newTopPhys) ||
            double.IsInfinity(newLeftPhys) || double.IsInfinity(newTopPhys))
        {
            newLeftPhys = target.Left + Math.Max(0, (target.Width - newWidthPhys) / 2);
            newTopPhys = target.Top + Math.Max(0, (target.Height - newHeightPhys) / 2);
        }

        // 8. 转换回 DIP 坐标并赋值
        w.Left = newLeftPhys / scale;
        w.Top = newTopPhys / scale;
        // 若尺寸被缩放过, 同步更新 (否则保持原样)
        if (newWidthPhys != widthPhys || newHeightPhys != heightPhys)
        {
            w.Width = newWidthPhys / scale;
            w.Height = newHeightPhys / scale;
        }
    }

    // --- Internals ---

    private readonly struct RectD
    {
        public readonly double Left;
        public readonly double Top;
        public readonly double Right;
        public readonly double Bottom;
        public readonly double Width;
        public readonly double Height;

        public RectD(double left, double top, double right, double bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
            Width = right - left; Height = bottom - top;
        }
    }

    /// <summary>枚举所有显示器的 WorkArea (物理像素). 返回列表 与 主屏 (若有).</summary>
    private static (List<RectD> All, RectD? Primary) EnumerateWorkAreas()
    {
        var result = new List<RectD>(4);
        RectD? primary = null;
        // 委托必须 keep-alive 到 EnumDisplayMonitors 返回之后 (function pointer 的 GC 根);
        // LibraryImport 会在 marshal 期间 fixed 委托的 function pointer, 但保留本地
        // 引用直到调用结束最稳.
        MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData) =>
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var w = info.rcWork;
                var rect = new RectD(w.Left, w.Top, w.Right, w.Bottom);
                result.Add(rect);
                if ((info.dwFlags & MONITORINFOF_PRIMARY) != 0)
                {
                    primary = rect;
                }
            }
            return true; // 继续枚举
        };

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败 (极少见), 返回已收集的 (可能为空).
        }
        GC.KeepAlive(proc); // ensure the delegate isn't collected during the native call.
        return (result, primary);
    }

    // --- Win32 P/Invoke (source-gen, 沿用 Win32.cs 风格) ---

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>Win32 <c>MONITORENUMPROC</c> — <c>BOOL (CALLBACK*)(HMONITOR, HDC, LPRECT, LPARAM)</c>.
    /// .NET 端用 <see cref="IntPtr"/> 接收 LPRECT, 内部 <c>Marshal.PtrToStructure</c> 解析 —
    /// 避免 delegate 签名带 <c>ref</c> struct 的复杂 marshaling.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
