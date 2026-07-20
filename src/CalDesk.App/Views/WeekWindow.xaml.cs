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

    // Borderless-resize (WM_NCHITTEST) + DPI + DWM chrome (spike T7S; BorderlessResize kept until T13).
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
