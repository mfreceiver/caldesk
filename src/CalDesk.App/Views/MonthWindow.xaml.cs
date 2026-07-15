using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CalDesk.App.ViewModels;
using CalDesk.Core.Models;

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

        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    /// <summary>Double-click an empty area of a day cell to add an event on that date.</summary>
    private void OnCellAdd(object? sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is FrameworkElement fe && fe.DataContext is MonthDay day)
        {
            ViewModel.AddNewCommand.Execute(day.Date);
            e.Handled = true;
        }
    }

    private void OnBackToWeek(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    // Closing hides to the tray (app stays alive via tray icon), matching the week window.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
