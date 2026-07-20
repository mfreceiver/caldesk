using System.Windows;
using System.Windows.Media;

namespace CalDesk.App.Converters;

/// <summary>R2 (§3.11): Freezable proxy that carries a {DynamicResource AccentBrush} via a
/// DependencyProperty. When the theme swaps, the DP re-resolves and the proxy raises
/// change notifications — so any MultiBinding that binds to <see cref="Brush"/> re-evaluates
/// and the color dot follows the theme. Replaces TargetNullValue (which is not a DP and
/// cannot hold a DynamicResource).</summary>
public sealed class BrushResourceProxy : Freezable
{
    public Brush? Brush { get => (Brush?)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    public static readonly DependencyProperty BrushProperty =
        DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(BrushResourceProxy));

    protected override Freezable CreateInstanceCore() => new BrushResourceProxy();
}
