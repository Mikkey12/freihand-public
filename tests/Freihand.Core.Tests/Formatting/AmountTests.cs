using Freihand.Core.Formatting;

namespace Freihand.Core.Tests.Formatting;

/// <summary>Beträge in CHF mit Schweizer Zahlformat (BA-17).</summary>
public class AmountTests
{
    private readonly SwissTextFormatter _formatter = new(SwissFormatOptions.Default);

    [Theory]
    [InlineData("1.250,50 Franken", "CHF 1’250.50")]
    [InlineData("1250,50 Franken", "CHF 1’250.50")]
    [InlineData("1250 Franken", "CHF 1’250")]
    [InlineData("20 Franken", "CHF 20")]
    [InlineData("12,5 Franken", "CHF 12.50")]
    [InlineData("1.250.000 Schweizer Franken", "CHF 1’250’000")]
    [InlineData("12 Franken 50.", "CHF 12.50.")]
    [InlineData("12 Franken und 5 Rappen", "CHF 12.05")]
    [InlineData("12 Franken 50 Rappen", "CHF 12.50")]
    [InlineData("12 CHF 50 Rappen", "CHF 12.50")]
    [InlineData("CHF 12 und 50 Rappen", "CHF 12.50")]
    [InlineData("von CHF 10 bis 20 Franken", "von CHF 10 bis CHF 20")]
    [InlineData("5 CHF 20 Personen", "CHF 5 20 Personen")]
    [InlineData("12 Franken und 3.500 Euro", "CHF 12 und EUR 3’500")]
    [InlineData("1250 CHF", "CHF 1’250")]
    [InlineData("80 Fr.", "CHF 80")]
    [InlineData("CHF 1250.50", "CHF 1’250.50")]
    [InlineData("CHF 1'250.50", "CHF 1’250.50")]
    [InlineData("Fr. 20.–", "CHF 20.–")]
    [InlineData("SFr. 20.-", "CHF 20.–")]
    [InlineData("fünfzig Franken", "CHF 50")]
    [InlineData("drei Franken", "CHF 3")]
    [InlineData("2.500 Euro", "EUR 2’500")]
    [InlineData("99,90 €", "EUR 99.90")]
    public void FormatsAmounts(string input, string expected)
    {
        Assert.Equal(expected, _formatter.Format(input));
    }

    [Theory]
    [InlineData("50 Rappen")]
    [InlineData("ein Franken")]
    [InlineData("Fr. Meier")]
    [InlineData("die 100-Franken-Note")]
    [InlineData("drei Millionen Franken")]
    public void LeavesUnclearAmounts(string input)
    {
        Assert.Equal(input, _formatter.Format(input));
    }

    [Fact]
    public void CentsNeedRappenOrEndOfClause()
    {
        Assert.Equal("CHF 12 50 Personen", _formatter.Format("12 Franken 50 Personen"));
    }

    [Fact]
    public void AmountsCanStayUnchanged()
    {
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default with { FormatAmounts = false });

        Assert.Equal("1’250.50 Franken", formatter.Format("1.250,50 Franken"));
    }
}
