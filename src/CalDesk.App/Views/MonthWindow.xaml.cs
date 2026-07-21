using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CalDesk.App.ViewModels;
using CalDesk.App.Windowing;

namespace CalDesk.App.Views;

public partial class MonthWindow : Window
{
    public MonthViewModel ViewModel { get; }

    /// <summary>When true (settings lock), the window ignores drag gestures.</summary>
    public bool IsPositionLocked { get; set; }

    /// <summary>Raised when the user clicks the "周" button to switch back to the week window.</summary>
    public event Action? BackRequested;

    public MonthWindow(MonthViewModel vm)
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

    private void OnBackToWeek(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    // T12 (WindowChrome) + T13 (ResizeSession 协调器) + BorderlessResize (WM_NCHITTEST).
    // T13-C5 决策: 保留 BorderlessResize.Attach — 见 docs/ocmar/ledgers/2026-07-21-t13-resize-coordinator.md
    // 简述: 同 WeekWindow — BorderlessResize 抢先 hook WM_NCHITTEST, WindowChrome 的
    // resize 逻辑实际未生效; T12-C5 手测已确认流畅, 保留待 winbuild 真机 A/B 验证
    // WindowChrome-only 方案后才决定是否移除.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BorderlessResize.Attach(this);
        DpiHandling.Attach(this);
        DwmAppearance.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    // Closing hides to the tray (app stays alive via tray icon), matching the week window.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ComputeBottomLimits();

    /// <summary>
    /// M3 / §3.16: recompute month-grid and detail-panel min/max heights from the active font-size tier.
    /// Public so <c>App.ApplyFontSize</c> can re-run after S/M/L changes.
    /// </summary>
    public void ComputeBottomLimits()
    {
        double body = (double)Application.Current.FindResource("FontBody");
        double monthCell = (double)Application.Current.FindResource("FontMonthCell");
        double micro = (double)Application.Current.FindResource("FontMicro");
        double small = (double)Application.Current.FindResource("FontSmall");
        double headerFs = (double)Application.Current.FindResource("FontHeader");

        double cellRow = Math.Ceiling(monthCell * 1.35) + 2;
        double cellMin = 3 * cellRow + 6;                      // overlay DD has no layout height
        double gridMin = 6 * cellMin;                          // R4：Row2.MinHeight = 6×cell

        double detailRow = Math.Ceiling(body * 1.35) + 6;
        double detailHdr = Math.Ceiling(body * 1.3) + 10;
        double detailMin = detailHdr + 3 * detailRow;          // 底部 3 行
        double detailMax = detailHdr + 5 * detailRow;          // 双栏底部 5 行封顶（最多 10 项）

        RootGrid.RowDefinitions[2].MinHeight = gridMin;
        RootGrid.RowDefinitions[4].MinHeight = detailMin;
        RootGrid.RowDefinitions[4].MaxHeight = detailMax;

        double chrome = 24 + (Math.Ceiling(headerFs * 1.3) + 20) + (Math.Ceiling(micro * 1.3) + 8) + 10
                        + (Math.Ceiling(small * 1.3) + 18);
        MinHeight = chrome + gridMin + detailMin;              // ≈600 @ S
    }
}
