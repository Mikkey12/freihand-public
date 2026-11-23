using Freihand.Core.Formatting;

namespace Freihand.Core.Tests.Formatting;

/// <summary>Zahlen, gesprochene Zahlen, Einheiten und Fachabkürzungen (BA-17).</summary>
public class NumberAndUnitTests
{
    private readonly SwissTextFormatter _formatter = new(SwissFormatOptions.Default);

    [Theory]
    [InlineData("1.250.000 Einwohner", "1’250’000 Einwohner")]
    [InlineData("rund 12.500 Besucher", "rund 12’500 Besucher")]
    [InlineData("2,5 Millionen", "2.5 Millionen")]
    [InlineData("1.250,75 Punkte", "1’250.75 Punkte")]
    [InlineData("1'250 Punkte", "1’250 Punkte")]
    [InlineData("Faktor 0,75.", "Faktor 0.75.")]
    public void WritesSwissNumbers(string input, string expected)
    {
        Assert.Equal(expected, _formatter.Format(input));
    }

    [Theory]
    [InlineData("Im Jahr 2026")]
    [InlineData("PLZ 8001")]
    [InlineData("Auftrag 4711-12")]
    [InlineData("Seite 12.5")]
    [InlineData("A4 und v2,5")]
    [InlineData("am 1.12. um 10.15")]
    public void LeavesUngroupedNumbers(string input)
    {
        Assert.Equal(input, _formatter.Format(input));
    }

    [Theory]
    [InlineData("12 Kilometer", "12 km")]
    [InlineData("12,5 Kilometern", "12.5 km")]
    [InlineData("12000 Meter", "12’000 m")]
    [InlineData("80 Kilometer pro Stunde", "80 km/h")]
    [InlineData("120 Stundenkilometer", "120 km/h")]
    [InlineData("3 Deziliter", "3 dl")]
    [InlineData("21 Grad Celsius", "21 °C")]
    [InlineData("15 Prozent", "15 %")]
    [InlineData("0,5 Promille", "0.5 ‰")]
    [InlineData("4.500 Kilowattstunden", "4’500 kWh")]
    [InlineData("zwei Kilo Äpfel", "2 kg Äpfel")]
    public void AbbreviatesUnits(string input, string expected)
    {
        Assert.Equal(expected, _formatter.Format(input));
    }

    [Theory]
    [InlineData("der Meter")]
    [InlineData("ein Kilometer")]
    [InlineData("5 Kilometer-Lauf")]
    [InlineData("21 Grad")]
    [InlineData("3 Stunden")]
    public void LeavesUnitsWithoutNumber(string input)
    {
        Assert.Equal(input, _formatter.Format(input));
    }

    [Theory]
    [InlineData("drei Tage", "drei Tage")]
    [InlineData("zwölf Monate", "zwölf Monate")]
    [InlineData("dreizehn Monate", "13 Monate")]
    [InlineData("Siebenundzwanzig Teilnehmer", "27 Teilnehmer")]
    [InlineData("dreißig Jahre", "30 Jahre")]
    [InlineData("zweihundertfünfzig Gäste", "250 Gäste")]
    [InlineData("zweitausendsechsundzwanzig", "2026")]
    [InlineData("einhundert Seiten", "100 Seiten")]
    [InlineData("tausendundeins Nacht", "1001 Nacht")]
    [InlineData("hundert Mal", "hundert Mal")]
    [InlineData("tausend Dank", "tausend Dank")]
    [InlineData("ein paar Hundert Leute", "ein paar Hundert Leute")]
    [InlineData("eine Woche", "eine Woche")]
    [InlineData("Achtung, zwanzigste Ausgabe", "Achtung, zwanzigste Ausgabe")]
    [InlineData("acht Prozent", "8 %")]
    [InlineData("tausend Franken", "CHF 1’000")]
    public void WritesSpokenNumbersAsDigitsFromThirteen(string input, string expected)
    {
        Assert.Equal(expected, _formatter.Format(input));
    }

    [Fact]
    public void SpokenNumberThresholdIsConfigurable()
    {
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default with { SpokenNumberDigitsFrom = 2 });

        Assert.Equal("eins, 2 und 3 Tage", formatter.Format("eins, zwei und drei Tage"));
    }

    [Fact]
    public void SpokenNumbersCanStayWords()
    {
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default with { SpokenNumbersToDigits = false });

        Assert.Equal("dreizehn Monate", formatter.Format("dreizehn Monate"));
    }

    [Theory]
    [InlineData("z.B. heute", "z. B. heute")]
    [InlineData("d.h. morgen", "d. h. morgen")]
    [InlineData("inkl. MwSt.", "inkl. MWST")]
    [InlineData("ohne Mwst", "ohne MWST")]
    [InlineData("i.d.R. nicht", "i. d. R. nicht")]
    [InlineData("z. B. heute", "z. B. heute")]
    public void WritesTechnicalAbbreviations(string input, string expected)
    {
        Assert.Equal(expected, _formatter.Format(input));
    }

    [Fact]
    public void AbbreviationsAreConfigurable()
    {
        var options = SwissFormatOptions.Default with
        {
            Abbreviations = new Dictionary<string, string> { ["Alters- und Hinterlassenenversicherung"] = "AHV" },
        };

        Assert.Equal("die AHV, z.B.", new SwissTextFormatter(options).Format("die Alters- und Hinterlassenenversicherung, z.B."));
    }

    [Fact]
    public void UnitsAreConfigurable()
    {
        var options = SwissFormatOptions.Default with { Units = new Dictionary<string, string> { ["Stunden"] = "h" } };

        Assert.Equal("3 h und 12 Kilometer", new SwissTextFormatter(options).Format("3 Stunden und 12 Kilometer"));
    }

    [Fact]
    public void GroupingCanStartAtFiveDigits()
    {
        var formatter = new SwissTextFormatter(SwissFormatOptions.Default with { GroupingMinDigits = 5 });

        Assert.Equal("CHF 1250.50 und 12’500 m", formatter.Format("1.250,50 Franken und 12500 Meter"));
    }
}
