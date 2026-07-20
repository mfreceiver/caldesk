using System.Windows;
using System.Windows.Controls;
using CalDesk.App.ViewModels;

namespace CalDesk.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
        {
            vm.Password = pb.Password;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>§3.12 S3: clicking a color swatch assigns that swatch's hex to the owning
    /// <see cref="SettingsViewModel.CalendarRow.Color"/>. Single-select only — re-clicking
    /// the selected swatch does <b>not</b> clear the color. Selection visuals are driven by
    /// declarative ControlTemplate.Triggers (EqualsConverter MultiBinding).</summary>
    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not string hex)
        {
            return;
        }

        var row = FindAncestorDataContext<SettingsViewModel.CalendarRow>(fe);
        if (row is null)
        {
            return;
        }

        // Single-select: always set, never toggle-off.
        row.Color = hex;
    }

    private static T? FindAncestorDataContext<T>(FrameworkElement fe) where T : class
    {
        var cur = fe;
        while (cur is not null)
        {
            if (cur.DataContext is T row)
            {
                return row;
            }

            cur = cur.Parent as FrameworkElement;
        }

        return null;
    }
}
