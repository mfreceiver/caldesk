using System.Text.RegularExpressions;
using CalDesk.Core.Services;
using Xunit;

public class CalendarPaletteTests
{
    [Fact]
    public void Presets_AreTwelveValidHex_NoGray()
    {
        var hex = new Regex("^#[0-9a-f]{6}$");
        Assert.Equal(12, CalendarPalette.Presets.Count);
        Assert.All(CalendarPalette.Presets, c => Assert.Matches(hex, c));
        Assert.DoesNotContain("#d9d9d9", CalendarPalette.Presets);
    }
}
