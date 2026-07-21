using System.Windows;
using System.Windows.Controls;

namespace CalDesk.App.Controls;

/// <summary>T8: 删 ViewportHeightProperty + AffectsMeasure 反馈环。实现纯自然高度测量，
/// 子项均分或自然排列。使用 ItemsPanelTemplate 时确保等分高度。</summary>
public sealed class WeekDaysPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var natural = InternalChildren.Cast<UIElement>().Sum(x => x.DesiredSize.Height);
        return new Size(availableSize.Width, natural);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        if (count == 0) return finalSize;

        // 均分 extra space（若有）以保持 7 卡等高质量感。若 finalSize 高于 natural，
        // 各卡加 extra / count；若 finalSize 低于 natural（容器被 ScrollViewer 限制），
        // 仍均分可用高度确保不重叠。
        var natural = InternalChildren.Cast<UIElement>().Sum(x => x.DesiredSize.Height);
        var extra = finalSize.Height > natural
            ? (finalSize.Height - natural) / count
            : 0d;
        var y = 0d;
        foreach (UIElement child in InternalChildren)
        {
            var h = child.DesiredSize.Height + extra;
            // 确保不小于 1px （极窄容器防御）
            h = Math.Max(1.0, h);
            child.Arrange(new Rect(0, y, finalSize.Width, h));
            y += h;
        }
        return finalSize;
    }
}
