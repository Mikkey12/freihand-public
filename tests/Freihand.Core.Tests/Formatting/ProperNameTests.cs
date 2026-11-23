using Freihand.Core.Formatting;

namespace Freihand.Core.Tests.Formatting;

/// <summary>Eigennamen bleiben unverändert (BA-17); geschützt sind Wörter aus dem persönlichen Vokabular (BA-03).</summary>
public class ProperNameTests
{
    [Theory]
    [InlineData("Die Straße ist gesperrt.", "Die Strasse ist gesperrt.")]
    [InlineData("STRAẞE", "STRASSE")]
    [InlineData("Grüße und Maße", "Grüsse und Masse")]
    public void ReplacesSharpS(string input, string expected)
    {
        Assert.Equal(expected, new SwissTextFormatter(SwissFormatOptions.Default).Format(input));
    }

    [Theory]
    [InlineData("Johann Strauß komponierte.", "Johann Strauß komponierte.")]
    [InlineData("Strauß, Strauß und Strauß.", "Strauß, Strauß und Strauß.")]
    [InlineData("Die Straußenfeder", "Die Straussenfeder")]
    [InlineData("Familie Strauße", "Familie Strausse")]
    public void KeepsProtectedTermsAsWholeWords(string input, string expected)
    {
        var options = SwissFormatOptions.Default with { ProtectedTerms = ["Strauß"] };

        Assert.Equal(expected, new SwissTextFormatter(options).Format(input));
    }

    [Fact]
    public void ProtectedTermIsCaseSensitive()
    {
        var options = SwissFormatOptions.Default with { ProtectedTerms = ["Strauß"] };

        Assert.Equal("ein strauss", new SwissTextFormatter(options).Format("ein strauß"));
    }

    [Theory]
    [InlineData("Gruß an Hans Weißmüller", "Gruss an Hans Weißmüller")]
    [InlineData("Die Firma Elf Zwanzig AG", "Die Firma Elf Zwanzig AG")]
    [InlineData("Tagung im Hotel Drei Könige", "Tagung im Hotel Drei Könige")]
    [InlineData("Termin bei Dr. Fünfzig", "Termin bei Dr. Fünfzig")]
    [InlineData("Die Halle 1.250 von Müller", "Die Halle 1.250 von Müller")]
    [InlineData("Mail an demo.weiß@example.org", "Mail an demo.weiß@example.org")]
    public void ProtectedNamesAndAddressesStay(string input, string expected)
    {
        var options = SwissFormatOptions.Default with
        {
            ProtectedTerms = ["Weißmüller", "Elf Zwanzig AG", "Drei Könige", "Fünfzig", "Halle 1.250"],
        };

        Assert.Equal(expected, new SwissTextFormatter(options).Format(input));
    }

    [Fact]
    public void ProtectedTermWinsOverAbbreviation()
    {
        var options = SwissFormatOptions.Default with { ProtectedTerms = ["MwSt"] };

        Assert.Equal("Firma MwSt", new SwissTextFormatter(options).Format("Firma MwSt"));
    }

    [Fact]
    public void SharpSCanBeKeptPerProfile()
    {
        var options = SwissFormatOptions.Default with { ReplaceSharpS = false };

        Assert.Equal("Die Straße", new SwissTextFormatter(options).Format("Die Straße"));
    }
}
