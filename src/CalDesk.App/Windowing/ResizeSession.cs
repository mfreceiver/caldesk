using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CalDesk.App.Windowing;

/// <summary>
/// 统一 resize session 协调器 (T13 / A4+B3)：
/// <list type="bullet">
/// <item>通过 HwndSource hook 监听 <c>WM_ENTERSIZEMOVE</c> / <c>WM_EXITSIZEMOVE</c>
///   精确检测 WPF Window 的 resize 拖动 session 边沿。比 <see cref="Window.SizeChanged"/>
///   边沿更可靠——后者在 minimize/restore/SetWidth/SetHeight 等非拖动路径也会触发，
///   无法区分"用户拖动 resize"与"代码/系统调整尺寸"。</item>
/// <item>resize 期间调用方应通过 <see cref="ScheduleUpdate"/> 通知本类：内部 reset
///   <see cref="DispatcherTimer"/>（默认 200ms），并不立即落盘，避免每像素写设置。</item>
/// <item>debounce 到期（用户停止拖动 200ms）→ <c>onCommit</c> flush 一次当前尺寸到 settings。</item>
/// <item>resize 结束（<c>WM_EXITSIZEMOVE</c>）→ 立即 stop timer 并 <c>onCommit</c> flush
///   最终尺寸，保证最终值一定落盘。</item>
/// <item>非 resize 期间（普通 <see cref="Window.SizeChanged"/> 一次性触发，如 ApplyWindowSettings
///   改宽高）→ <see cref="ScheduleUpdate"/> 立即走 onUpdate + onCommit，避免错过一次性事件。</item>
/// <item>暴露 <see cref="IsResizing"/> + <see cref="ResizeStarted"/> / <see cref="ResizeEnded"/> 事件，
///   让 <c>App.SyncCompleted</c> 回调在 resize 期间 defer 刷新到 resize 结束后合并触发一次。</item>
/// <item><see cref="IDisposable"/>：释放 timer + RemoveHook 防止 App 生命周期泄漏。</item>
/// </list>
/// <remarks>
/// <para>线程模型：所有 hook 回调和 timer Tick 都在 UI 线程（Dispatcher），调用方回调应保持轻量。
/// onCommit 中的 I/O（settings 落盘）由 <c>App</c> 控制；本类不直接落盘，仅作协调器。</para>
/// <para>WPF HWND hook 强依赖 Windows——Linux 单测不能跑，本类不在 tests 范围内（task 说明）。</para>
/// </remarks>
/// </summary>
public sealed class ResizeSession : IDisposable
{
    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _debounceTimer;
    private readonly Action<double, double> _onUpdate;
    private readonly Action<double, double> _onCommit;
    private bool _isResizing;
    private bool _disposed;

    /// <summary>默认 debounce 间隔（200ms）。调整期拖动每像素停止 200ms 才落盘，避免
    /// 拖动过程中频繁写 settings.json。</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>构造并 attach hook + 启动 debounce timer（timer 默认 Stopped，
    /// 仅在 resize 期间被 Start/Stop）。</summary>
    /// <param name="window">要监听的 WPF Window（HWND 必须已存在，建议在
    /// <c>OnSourceInitialized</c> 或 <c>SourceInitialized</c> event 触发后调用）。</param>
    /// <param name="onUpdate">每次 size 变化（resize 期间每个 SizeChanged、resize 结束、
    /// debounce tick、非 resize 的一次性事件）→ 把当前 w/h 写入内存 settings
    /// （<c>AppSettings.WindowWidth</c> 等）。可空，但通常不空。</param>
    /// <param name="onCommit">flush 触发点（debounce 到期 / resize 结束 / 非 resize 一次性
    /// 事件）→ 把 settings 真正 Save 到磁盘。空时退化为只更新内存，依赖外部 OnExit 落盘
    /// （保留旧行为）。</param>
    /// <param name="debounce">可选：覆盖默认 200ms 去抖间隔。</param>
    public ResizeSession(
        Window window,
        Action<double, double>? onUpdate,
        Action<double, double>? onCommit,
        TimeSpan? debounce = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _onUpdate = onUpdate ?? ((_, _) => { });
        _onCommit = onCommit ?? ((_, _) => { });

        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        if (_source == null)
        {
            throw new InvalidOperationException(
                "ResizeSession.Attach: window has no HWND; call after SourceInitialized.");
        }

        _source.AddHook(WndProc);

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = debounce is { } d && d > TimeSpan.Zero ? d : DefaultDebounce,
        };
        _debounceTimer.Tick += OnDebounceTick;
    }

    /// <summary>当前是否在 user-driven resize 拖动 session 中。App.SyncCompleted 回调用此标志
    /// 决定是否 defer 刷新。</summary>
    public bool IsResizing => _isResizing;

    /// <summary>resize session 开始（<c>WM_ENTERSIZEMOVE</c>）— 边沿触发，仅一次。
    /// 调用方应只在已 _isResizing=false 时收到。</summary>
    public event Action? ResizeStarted;

    /// <summary>resize session 结束（<c>WM_EXITSIZEMOVE</c>）— 边沿触发，仅一次。
    /// App 收到后检查并合并 pending sync refresh。</summary>
    public event Action? ResizeEnded;

    /// <summary>由 <see cref="Window.SizeChanged"/> 调用。每次 size 变化都应让本类有机会
    /// restart debounce（resize 中）或立即 commit（非 resize 一次性事件）。</summary>
    public void ScheduleUpdate()
    {
        if (_disposed) return;

        // 始终先 update 内存（in-memory state 必须反映当前真实尺寸）。
        _onUpdate(_window.Width, _window.Height);

        if (!_isResizing)
        {
            // 非 resize 期间的 SizeChanged（如 ApplyWindowSettings、minimize/restore、
            // DPI 变化等）。一次性 commit，不再 restart debounce。
            _debounceTimer.Stop();
            _onCommit(_window.Width, _window.Height);
            return;
        }

        // Resize 中：只 restart debounce，让 commit 推迟到"用户停止拖动 200ms"或"resize 结束"。
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>App 退出时调用：stop + 解绑 timer，RemoveHook 释放 hook 链引用。
    /// 若窗口已先 Dispose（HwndSource 不可用），RemoveHook 是 no-op。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTick;

        if (_source != null)
        {
            try
            {
                _source.RemoveHook(WndProc);
            }
            catch
            {
                // HwndSource 可能已被窗口释放；忽略。
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_ENTERSIZEMOVE)
        {
            if (!_isResizing)
            {
                _isResizing = true;
                ResizeStarted?.Invoke();
            }
        }
        else if (msg == Win32.WM_EXITSIZEMOVE)
        {
            if (_isResizing)
            {
                _isResizing = false;
                // Final flush: 立即 stop debounce，commit 当前最终尺寸。
                _debounceTimer.Stop();
                _onUpdate(_window.Width, _window.Height);
                _onCommit(_window.Width, _window.Height);
                ResizeEnded?.Invoke();
            }
        }

        // 始终返回 IntPtr.Zero — 我们不消费消息，只做边沿检测。
        return IntPtr.Zero;
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        // 仅在仍在 resize 时 commit；如 resize 已结束（EXITSIZEMOVE 已先于 tick 到达），
        // 那次 EXITSIZEMOVE 已 commit，无需重复。
        if (_isResizing)
        {
            _onUpdate(_window.Width, _window.Height);
            _onCommit(_window.Width, _window.Height);
        }
    }
}
