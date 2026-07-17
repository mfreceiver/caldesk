using System.Reflection;
using System.Windows;

namespace CalDesk.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Version is derived from git at build time (Directory.Version.props); never hardcoded.
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "dev";
        VersionText.Text = "版本 " + version;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
