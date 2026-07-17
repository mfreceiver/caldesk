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
             FindAncestor<ScrollBar>(src) is not null ||
             FindAncestor<GridSplitter>(src) is not null))
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

    // Borderless-resize (WM_NCHITTEST) + DPI crispness flags are attached once the Win32 hwnd exists.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BorderlessResize.Attach(this);
        DpiHandling.Attach(this);
    }

    // Closing hides to the tray (app stays alive via tray icon), matching the week window.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    // --- Top/bottom split ratio persistence (GridSplitter Row2↔Row4) ---

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplySplitRatio(ViewModel.SplitRatio);

    private void OnSplitChanged(object sender, DragCompletedEventArgs e)
    {
        var top = RootGrid.RowDefinitions[2].ActualHeight;
        var bottom = RootGrid.RowDefinitions[4].ActualHeight;
        var total = top + bottom;
        if (total > 0)
        {
            ViewModel.SplitRatio = top / total;
            ApplySplitRatio(ViewModel.SplitRatio);
        }
    }

    private void ApplySplitRatio(double ratio)
    {
        if (double.IsNaN(ratio) || ratio <= 0.02 || ratio >= 0.98)
        {
            return;
        }

        RootGrid.RowDefinitions[2].Height = new GridLength(ratio * 100.0, GridUnitType.Star);
        RootGrid.RowDefinitions[4].Height = new GridLength((1.0 - ratio) * 100.0, GridUnitType.Star);
    }
}
