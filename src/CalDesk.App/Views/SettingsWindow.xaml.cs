using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>Clicking a color swatch assigns that swatch's hex to the owning row's
    /// <see cref="SettingsViewModel.CalendarRow.Color"/>. The swatch's <c>DataContext</c>
    /// is the hex string itself (the inner ItemsControl binds to
    /// <c>CalendarPalette.Presets</c>); the owning row is the data context of the closest
    /// ancestor that's a <see cref="CalendarRow"/> (i.e. the outer row's data item). After
    /// the assignment we walk the swatch panel to refresh which swatch carries the selected
    /// ring (BorderBrush).</summary>
    /// <summary>When a row's swatch panel is first created, paint the selected ring on the
    /// swatch that matches the row's current <see cref="SettingsViewModel.CalendarRow.Color"/>
    /// (if any). This mirrors the per-click refresh in <see cref="OnPickColor"/>.</summary>
    private void OnSwatchPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement panel)
        {
            return;
        }

        var row = FindAncestorDataContext<SettingsViewModel.CalendarRow>(panel);
        if (row is null)
        {
            return;
        }

        foreach (var child in FindVisualChildren<Border>(panel))
        {
            if (child.DataContext is not string swatchHex)
            {
                continue;
            }

            bool selected = row.Color is not null
                && string.Equals(row.Color, swatchHex, StringComparison.OrdinalIgnoreCase);
            child.BorderBrush = selected ? Brushes.Black : Brushes.Transparent;
            child.BorderThickness = new Thickness(selected ? 2 : 0);
        }

        // Re-run the ring paint whenever the row's Color changes (e.g. from code, or after
        // SaveCalendarsAsync refresh) so the highlighted swatch tracks the model.
        row.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var child in FindVisualChildren<Border>(panel))
            {
                if (child.DataContext is not string h)
                {
                    continue;
                }

                bool sel = row.Color is not null
                    && string.Equals(row.Color, h, StringComparison.OrdinalIgnoreCase);
                child.BorderBrush = sel ? Brushes.Black : Brushes.Transparent;
                child.BorderThickness = new Thickness(sel ? 2 : 0);
            }
        }));
    }

    private void OnPickColor(object sender, MouseButtonEventArgs e)
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

        // Toggle off if the same swatch is clicked again — keeps the picker forgiving.
        row.Color = string.Equals(row.Color, hex, StringComparison.OrdinalIgnoreCase)
            ? null
            : hex;

        // Refresh the ring state on every swatch in the same row. The swatches live in the
        // ItemsControl that's a sibling of the CheckBox; we walk up to the row Grid first.
        if (FindAncestorItemsControl(fe) is { } swatchHost)
        {
            foreach (var child in FindVisualChildren<Border>(swatchHost))
            {
                if (child.DataContext is string swatchHex)
                {
                bool selected = row.Color is not null
                    && string.Equals(row.Color, swatchHex, StringComparison.OrdinalIgnoreCase);
                    child.BorderBrush = selected ? Brushes.Black : Brushes.Transparent;
                    child.BorderThickness = new Thickness(selected ? 2 : 0);
                }
            }
        }
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

    /// <summary>Walk up to the inner swatch ItemsControl (whose items are the palette hex strings).
    /// We identify it by checking that its first item is a string.</summary>
    private static ItemsControl? FindAncestorItemsControl(DependencyObject fe)
    {
        var cur = fe;
        while (cur is not null)
        {
            if (cur is ItemsControl ic && ic.Items.Count > 0 && ic.Items.OfType<object>().FirstOrDefault() is string)
            {
                return ic;
            }

            cur = VisualTreeHelper.GetParent(cur);
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        var queue = new System.Collections.Generic.Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur is T hit)
            {
                yield return hit;
            }

            int count = VisualTreeHelper.GetChildrenCount(cur);
            for (int i = 0; i < count; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(cur, i));
            }
        }
    }
}
