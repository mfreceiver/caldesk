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
}
