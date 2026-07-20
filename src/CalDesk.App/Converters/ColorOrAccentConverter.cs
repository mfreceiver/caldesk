using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CalDesk.App.Converters;

/// <summary>MultiBinding converter: returns the calendar Color hex brush when present,
/// otherwise falls back to the AccentBrush proxy (so uncolored events follow the theme).
/// See §3.11 / R2.</summary>
public sealed class ColorOrAccentConverter : IMultiValueConverter
{
    private static readonly BrushConverter C = new();

    public object Convert(object[] v, Type t, object p, CultureInfo c)
    {
        if (v.Length > 0 && v[0] is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try { return C.ConvertFromString(hex) as Brush ?? Fallback(v); }
            catch (FormatException) { /* fall through */ }
        }

        return Fallback(v);
    }

    private static object Fallback(object[] v) => v.Length > 1 && v[1] is Brush b ? b : Brushes.Transparent;

    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
