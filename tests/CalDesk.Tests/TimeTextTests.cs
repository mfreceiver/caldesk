using CalDesk.Core;
using Xunit;

namespace CalDesk.Tests;

/// <summary>
/// Table-driven coverage for compact / HH:mm time entry used by QuickAdd.
/// Culture-invariant: does not accept seconds, AM/PM, or locale-specific forms.
/// </summary>
public sealed class TimeTextTests
{
    [Theory]
    [InlineData("0930", 9, 30)]
    [InlineData("930", 9, 30)]
    [InlineData("09:30", 9, 30)]
    [InlineData("9:30", 9, 30)]
    [InlineData("2345", 23, 45)]
    [InlineData("00:00", 0, 0)]
    [InlineData("0:00", 0, 0)]
    [InlineData("2359", 23, 59)]
    [InlineData("9", 9, 0)]   // single digit hour → HH:00
    [InlineData("09", 9, 0)]  // two-digit hour → HH:00
    [InlineData("23", 23, 0)]
    public void TryParse_Accepts_Compact_And_Colon_Forms(string input, int hour, int minute)
    {
        Assert.True(TimeText.TryParse(input, out var t));
        Assert.Equal(new TimeOnly(hour, minute), t);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2400")]
    [InlineData("0960")]
    [InlineData("2500")]
    [InlineData("9:5")]       // minutes must be two digits when colon is used
    [InlineData("09:5")]
    [InlineData("09:30:00")]  // no seconds
    [InlineData("9:30 AM")]
    [InlineData("9:30am")]
    [InlineData("abc")]
    [InlineData("9:60")]
    [InlineData("24:00")]
    [InlineData("99")]
    public void TryParse_Rejects_Invalid(string input)
    {
        Assert.False(TimeText.TryParse(input, out _));
    }

    [Fact]
    public void TryParse_Null_Is_False()
    {
        Assert.False(TimeText.TryParse(null, out _));
    }
}
