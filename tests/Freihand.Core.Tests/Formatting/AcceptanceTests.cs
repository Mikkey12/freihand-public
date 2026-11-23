using Freihand.Core.Formatting;

namespace Freihand.Core.Tests.Formatting;

/// <summary>
/// Abnahme BA-17 mit der Voreinstellung de-CH, wie Windows sie ausliefert: Testsätze mit Daten, Beträgen, Einheiten und
/// Eigennamen ergeben Schweizer Schreibweise.
/// </summary>
public class AcceptanceTests
{
    private static readonly SwissFormatOptions Options = SwissFormatOptions.Default with
    {
        ProtectedTerms = ["Strauß", "Weißhaupt AG", "Meier"],
    };

    private readonly SwissTextFormatter _formatter = new(Options);

    [Theory]
    [InlineData(
        "Herr Strauß überweist am 27. Oktober 2026 um 14.30 Uhr 1.250,50 Franken an die Weißhaupt AG.",
        "Herr Strauß überweist am 27.10.2026 um 14:30 Uhr CHF 1’250.50 an die Weißhaupt AG.")]
    [InlineData(
        "Frau Meier ist am 3.11.2026 zwölf Kilometer gelaufen und hat dabei 2,5 Liter Wasser getrunken.",
        "Frau Meier ist am 03.11.2026 12 km gelaufen und hat dabei 2.5 l Wasser getrunken.")]
    [InlineData(
        "Die Offerte für 120 Quadratmeter beträgt 48.000 Franken zuzüglich 8,1 Prozent MwSt.",
        "Die Offerte für 120 m² beträgt CHF 48’000 zuzüglich 8.1 % MWST")]
    [InlineData(
        "Der Strauß Blumen kostet fünfunddreißig Franken fünfzig, die Lieferung am 1. März 2027 ist gratis.",
        "Der Strauß Blumen kostet CHF 35.50, die Lieferung am 01.03.2027 ist gratis.")]
    [InlineData(
        "Wir treffen uns um 9 Uhr 15 an der Großen Straße, z.B. beim Eingang.",
        "Wir treffen uns um 09:15 Uhr an der Grossen Strasse, z. B. beim Eingang.")]
    public void FormatsTestSentence(string input, string expected)
    {
        Assert.Equal(expected, _formatter.Format(input));
    }

    [Theory]
    [InlineData("Guten Tag, ich melde mich morgen wieder.")]
    [InlineData("Im Jahr 2026 zog er nach 8001 Zürich, Kundennummer 123456.")]
    [InlineData("Version 2.10.3 läuft auf 192.0.2.20, siehe https://example.org/a?b=1,5&c=Maß.")]
    [InlineData("Am 27.10. um 14:30 kommen drei Leute, tausend Dank!")]
    [InlineData("Ein Wort mit geschütztem Leerschlag,\ttabulator\nund Umbruch.  Doppelte Leerschläge bleiben.")]
    [InlineData("")]
    [InlineData("CHF 1’250.50 und 27.10.2026 um 14:30 Uhr, 12 km und 8.1 %")]
    public void LeavesTextWithoutHitsUnchanged(string input)
    {
        var result = _formatter.Format(input);

        Assert.Equal(input, result);
        Assert.Equal(input.Length, result.Length);
    }

    [Fact]
    public void IsIdempotent()
    {
        var once = _formatter.Format("Am 27. Oktober 2026 zahlt Strauß 1.250,50 Franken für 12,5 Kilometer.");

        Assert.Equal(once, _formatter.Format(once));
    }

    [Fact]
    public void DisabledOptionsLeaveTextUnchanged()
    {
        const string input = "Die Straße kostet 1.250,50 Franken.";

        var formatter = new SwissTextFormatter(SwissFormatOptions.Default with { Enabled = false });

        Assert.Equal(input, formatter.Format(input));
    }
}
