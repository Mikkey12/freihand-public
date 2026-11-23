using Freihand.Core.Formatting;

namespace Freihand.Core.Tests.Formatting;

/// <summary>Datums- und Uhrzeitformat (BA-17).</summary>
public class DateAndTimeTests
{
    private readonly SwissTextFormatter _formatter = new(SwissFormatOptions.Default);

    [Theory]
    [InlineData("am 27. Oktober 2026", "am 27.10.2026")]
    [InlineData("am 1. März 2027", "am 01.03.2027")]
    [InlineData("am 5.Mai 2026", "am 05.05.2026")]
    [InlineData("am 2. Jänner 2027", "am 02.01.2027")]
    [InlineData("Dienstag, 27.10.2026, ", "Dienstag, 27.10.2026, ")]
    [InlineData("vom 1.3.2026 bis 9.12.2026", "vom 01.03.2026 bis 09.12.2026")]
    [InlineData("Am 29.2.2028.", "Am 29.02.2028.")]
    public void WritesShortDateOfDeCh(string input, string expected)
    {
        Assert.Equal(expected, _formatter.Format(input));
    }

    [Theory]
    [InlineData("am 27. Oktober")]
    [InlineData("im Oktober 2026")]
    [InlineData("am 31.2.2026")]
    [InlineData("am 29. Februar 2026")]
    [InlineData("am 27.10.26")]
    [InlineData("am 2026-10-27")]
    [InlineData("am 13.13.2026")]
    public void LeavesIncompleteOrInvalidDates(string input)
    {
        Assert.Equal(input, _formatter.Format(input));
    }

    [Theory]
    [InlineData("um 14.30 Uhr", "um 14:30 Uhr")]
    [InlineData("um 14:30 Uhr", "um 14:30 Uhr")]
    [InlineData("um 8.05Uhr", "um 08:05 Uhr")]
    [InlineData("um 14 Uhr 30", "um 14:30 Uhr")]
    [InlineData("um 9 Uhr 5.", "um 09:05 Uhr.")]
    [InlineData("um vierzehn Uhr dreißig", "um 14:30 Uhr")]
    [InlineData("um drei Uhr", "um 3 Uhr")]
    [InlineData("um 14 Uhr", "um 14 Uhr")]
    public void WritesShortTimeOfDeCh(string input, string expected)
    {
        Assert.Equal(expected, _formatter.Format(input));
    }

    [Theory]
    [InlineData("um 25.30 Uhr")]
    [InlineData("um 24 Uhr 00")]
    [InlineData("um 14 Uhr 75")]
    [InlineData("um 14:30")]
    [InlineData("um halb drei")]
    [InlineData("um 14 Uhr 30 Minuten später")]
    public void LeavesUnclearTimes(string input)
    {
        Assert.Equal(input, _formatter.Format(input));
    }

    [Theory]
    [InlineData("ab 14 Uhr 20 Franken", "ab 14 Uhr CHF 20")]
    [InlineData("um 12 Uhr 15 Prozent", "um 12 Uhr 15 %")]
    public void AmountsAndUnitsAfterUhrAreNoTime(string input, string expected)
    {
        Assert.Equal(expected, _formatter.Format(input));
    }

    [Theory]
    [InlineData("am 27. Oktober 2026", "am 27. Oktober 2026")]
    [InlineData("am Dienstag, 27.10.2026", "am Dienstag, 27. Oktober 2026")]
    [InlineData("am Dienstag 27. Oktober 2026", "am Dienstag, 27. Oktober 2026")]
    [InlineData("am Mittwoch, 27.10.2026", "am Mittwoch, 27.10.2026")]
    public void LongDateAddsNoWeekdayAndKeepsMismatches(string input, string expected)
    {
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default with { DateStyle = DateStyle.Long });

        Assert.Equal(expected, formatter.Format(input));
    }

    [Fact]
    public void ShortDateKeepsSpokenWeekday()
    {
        Assert.Equal("Dienstag, 27.10.2026", _formatter.Format("Dienstag, 27. Oktober 2026"));
    }

    [Fact]
    public void UnchangedStylesKeepDatesAndTimes()
    {
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default with
        {
            DateStyle = DateStyle.Unchanged,
            TimeStyle = TimeStyle.Unchanged,
        });

        Assert.Equal("am 27. Oktober 2026 um 14.30 Uhr", formatter.Format("am 27. Oktober 2026 um 14.30 Uhr"));
    }
}
