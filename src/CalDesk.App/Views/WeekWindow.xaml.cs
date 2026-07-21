using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CalDesk.App.ViewModels;
using CalDesk.App.Windowing;
using CalDesk.Core.Models;

namespace CalDesk.App.Views;

public partial class WeekWindow : Window
{
    public WeekViewModel ViewModel { get; }

    /// <summary>When true (settings lock), the window ignores drag gestures.</summary>
    public bool IsPositionLocked { get; set; }

    public WeekWindow(WeekViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (IsPositionLocked)
        {
            return;
        }

        // Let ResizeGrip / interactive children handle their own input.
        if (e.OriginalSource is DependencyObject src &&
            (FindAncestor<ResizeGrip>(src) is not null ||
             FindAncestor<Button>(src) is not null ||
             FindAncestor<ScrollBar>(src) is not null))
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnDayDoubleAdd(object? sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is FrameworkElement fe && fe.DataContext is WeekDay day)
        {
            ViewModel.AddNewCommand.Execute(day.Date);
            e.Handled = true;
        }
    }

    /// <summary>Raised when the user clicks the "月" segment to open the month view.</summary>
    public event Action? MonthRequested;

    private void OnMonth(object sender, RoutedEventArgs e) => MonthRequested?.Invoke();

    // T12 (WindowChrome) + T13 (ResizeSession 协调器) + BorderlessResize (WM_NCHITTEST).
    // T13-C5 决策: 保留 BorderlessResize.Attach — 见 docs/ocmar/ledgers/2026-07-21-t13-resize-coordinator.md
    // 简述: BorderlessResize 与 WindowChrome.ResizeBorderThickness 都提供边/角 resize,
    // 两者通过 WM_NCHITTEST hook 互斥; 当前 BorderlessResize 抢先 hook, 所以 WindowChrome
    // 的 resize 逻辑实际未生效. T12-C5 真机手测已确认"resize 明显流畅", 说明保留
    // BorderlessResize 是当前最稳的方案. 真机 A/B 测两种方案(移除 BorderlessResize, 只留
    // WindowChrome)需 winbuild 验证, 在 T13 内不盲删.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BorderlessResize.Attach(this);
        DpiHandling.Attach(this);
        DwmAppearance.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    // Closing the window hides it to the tray (app stays alive via tray icon).
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
