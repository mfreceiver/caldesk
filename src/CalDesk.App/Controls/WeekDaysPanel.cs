using System.Windows;
using System.Windows.Controls;

namespace CalDesk.App.Controls;

public sealed class WeekDaysPanel : Panel
{
    public static readonly DependencyProperty ViewportHeightProperty =
        DependencyProperty.Register(nameof(ViewportHeight), typeof(double), typeof(WeekDaysPanel),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double ViewportHeight { get => (double)GetValue(ViewportHeightProperty); set => SetValue(ViewportHeightProperty, value); }
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var natural = InternalChildren.Cast<UIElement>().Sum(x => x.DesiredSize.Height);
        var viewport = double.IsNaN(ViewportHeight) || double.IsInfinity(ViewportHeight) ? availableSize.Height : ViewportHeight;
        return new Size(availableSize.Width, double.IsInfinity(viewport) ? natural : Math.Max(natural, viewport));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var natural = InternalChildren.Cast<UIElement>().Sum(x => x.DesiredSize.Height);
        var extra = finalSize.Height > natural && InternalChildren.Count > 0 ? (finalSize.Height - natural) / InternalChildren.Count : 0;
        var y = 0d;
        foreach (UIElement child in InternalChildren) { var h = child.DesiredSize.Height + extra; child.Arrange(new Rect(0, y, finalSize.Width, h)); y += h; }
        return finalSize;
    }
}
