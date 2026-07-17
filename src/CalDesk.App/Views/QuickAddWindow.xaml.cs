using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CalDesk.App.Windowing;

namespace CalDesk.App.Views;

public partial class QuickAddWindow : Window
{
    public QuickAddWindow()
    {
        InitializeComponent();
    }

    /// <summary>Title-bar drag: DragMove on left-button drag from the title row,
    /// but let interactive children (the × close button) handle their own input
    /// so clicking close doesn't start a window drag.</summary>
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindAncestor<Button>(src) is not null)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    /// <summary>× button and any explicit close invocation. (ESC still works via
    /// IsCancel on the close/cancel buttons; Enter via IsDefault on Save.)</summary>
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // DPI crispness flags for the transparent card (per spec §1.3 / T3 DpiHandling).
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DpiHandling.Attach(this);
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
}
