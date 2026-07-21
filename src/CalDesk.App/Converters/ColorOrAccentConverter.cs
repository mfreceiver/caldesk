using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CalDesk.App.Converters;

/// <summary>MultiBinding converter: returns the calendar Color hex brush when present,
/// otherwise falls back to the AccentBrush proxy (so uncolored events follow the theme).
/// See §3.11 / R2.
/// Caches frozen brush instances keyed by hex color; fallback (accent) is not cached
/// because BrushResourceProxy must remain responsive to runtime theme changes.</summary>
public sealed class ColorOrAccentConverter : IMultiValueConverter
{
    private static readonly BrushConverter C = new();
    private static readonly ConcurrentDictionary<string, Brush> _cache = new();

    public object Convert(object[] v, Type t, object p, CultureInfo c)
    {
        if (v.Length > 0 && v[0] is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            var key = "hex:" + hex;
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            try
            {
                var brush = C.ConvertFromString(hex) as Brush;
                if (brush != null)
                {
                    if (brush.CanFreeze)
                        brush.Freeze();
                    _cache[key] = brush;
                    return brush;
                }
            }
            catch (FormatException) { /* fall through */ }
        }

        return Fallback(v);
    }

    private static object Fallback(object[] v) => v.Length > 1 && v[1] is Brush b ? b : Brushes.Transparent;

    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
