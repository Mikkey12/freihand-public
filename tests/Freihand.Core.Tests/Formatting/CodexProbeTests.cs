using System.Globalization;
using Freihand.Core.Formatting;

namespace Freihand.Core.Tests.Formatting;

/// <summary>
/// Prüftests von Codex für P4 (BA-17), mit unveränderten Erwartungen übernommen aus <c>codex-probes/</c>
/// (Branch <c>codex/pruefproben</c>): Codex-Review R5, Nachprüfung N3 und zweite Nachprüfung F3.
/// </summary>
public sealed class CodexProbeTests
{
    // R5, aus ReviewRegressionTests.cs
    [Fact]
    public void Review_CurrencyNegativePatternIsApplied()
    {
        var number = (NumberFormatInfo)RegionalFormat.SwissDefault.Number.Clone();
        number.CurrencyNegativePattern = 0;
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default,
            new RegionalFormat(number, RegionalFormat.SwissDefault.DateTime, "CHF"));
        Assert.Equal("(CHF12.50)", formatter.Format("-12,50 Franken"));
    }

    // R5, aus ReviewRegressionTests.cs
    [Fact]
    public void Review_CurrencySubunitsUseRequestedDecimalDigits()
    {
        var number = (NumberFormatInfo)RegionalFormat.SwissDefault.Number.Clone();
        number.CurrencyDecimalDigits = 3;
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default,
            new RegionalFormat(number, RegionalFormat.SwissDefault.DateTime, "CHF"));
        Assert.Equal("CHF 12.500", formatter.Format("12 Franken 50"));
    }

    public static TheoryData<int> NegativePatterns => new(Enumerable.Range(0, 17));

    // N3, aus FollowupBoundaryTests.cs
    [Theory]
    [MemberData(nameof(NegativePatterns))]
    public void FormattingNegativeAmountTwiceIsStable(int pattern)
    {
        var number = (NumberFormatInfo)RegionalFormat.SwissDefault.Number.Clone();
        number.CurrencyNegativePattern = pattern;
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default, new(number, RegionalFormat.SwissDefault.DateTime, "CHF"));
        var once = formatter.Format("-12,50 Franken");
        Assert.Equal(once, formatter.Format(once));
    }

    // R5, aus FollowupBoundaryTests.cs
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void FractionalAmountsNeverLoseDigits(int digits)
    {
        var number = (NumberFormatInfo)RegionalFormat.SwissDefault.Number.Clone();
        number.CurrencyDecimalDigits = digits;
        number.CurrencyNegativePattern = 0;
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default, new(number, RegionalFormat.SwissDefault.DateTime, "CHF"));
        Assert.Equal("(CHF12.3456)", formatter.Format("-12,3456 Franken"));
        Assert.Equal(digits == 3 ? "(CHF12.050)" : "(CHF12.05)", formatter.Format("-12 Franken 5 Rappen"));
    }

    // F3, aus SecondFollowupTests.cs
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CurrencyLockDoesNotSplitSpokenSubunits(int positive)
    {
        var number = (NumberFormatInfo)RegionalFormat.SwissDefault.Number.Clone();
        number.CurrencyPositivePattern = positive;
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default, new(number, RegionalFormat.SwissDefault.DateTime, "CHF"));
        var expected = positive switch { 0 => "CHF12.50", 1 => "12.50CHF", 2 => "CHF 12.50", _ => "12.50 CHF" };
        Assert.Equal(expected, formatter.Format("12 CHF und 50 Rappen"));
    }
}
