using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CalDesk.App.ViewModels;
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

        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnDayDoubleAdd(object? sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is FrameworkElement fe && fe.DataContext is WeekDay day)
        {
            ViewModel.AddNewCommand.Execute(day.Date);
            e.Handled = true;
        }
    }

    // Closing the window hides it to the tray (app stays alive via tray icon).
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
