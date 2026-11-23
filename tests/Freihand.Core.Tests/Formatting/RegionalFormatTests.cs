using System.Globalization;
using Freihand.Core.Formatting;

namespace Freihand.Core.Tests.Formatting;

/// <summary>
/// Zahl-, Datums-, Zeit- und Währungsformat folgen den Ländereinstellungen von Windows (BA-17). Alle Formate sind hier
/// ausdrücklich gesetzt, damit die Tests nicht von den ICU-Daten des Systems abhängen.
/// </summary>
public class RegionalFormatTests
{
    private const string Sentence =
        "Am 27. Oktober 2026 um 14.30 Uhr zahlt Strauß 1.250,50 Franken für 12.500 Meter und 2,5 Prozent auf 1.250.000.";

    private static readonly SwissFormatOptions Options = SwissFormatOptions.Default with { ProtectedTerms = ["Strauß"] };

    [Fact]
    public void DefaultIsDeChAsShippedByWindows()
    {
        var format = RegionalFormat.SwissDefault;

        Assert.Equal("’", format.Number.NumberGroupSeparator);
        Assert.Equal(".", format.Number.NumberDecimalSeparator);
        Assert.Equal("dd.MM.yyyy", format.DateTime.ShortDatePattern);
        Assert.Equal("dddd, d. MMMM yyyy", format.DateTime.LongDatePattern);
        Assert.Equal("HH:mm", format.DateTime.ShortTimePattern);
        Assert.Equal("CHF", format.Number.CurrencySymbol);
        Assert.Equal(2, format.Number.CurrencyPositivePattern);
        Assert.Equal(2, format.Number.CurrencyNegativePattern);
        Assert.Equal("-", format.Number.NegativeSign);
        Assert.Equal("CHF", format.CurrencyIsoCode);
        Assert.Equal(
            "Am 27.10.2026 um 14:30 Uhr zahlt Strauß CHF 1’250.50 für 12’500 m und 2.5 % auf 1’250’000.",
            new SwissTextFormatter(Options).Format(Sentence));
    }

    [Fact]
    public void NullRegionalFormatMeansDeCh()
    {
        Assert.Equal(new SwissTextFormatter(Options).Format(Sentence), new SwissTextFormatter(Options, null).Format(Sentence));
    }

    [Fact]
    public void StandardRuleSetIsChStandard()
    {
        Assert.Equal("ch-standard", SwissFormatOptions.StandardId);
        Assert.Equal("ch-standard", SwissFormatOptions.Default.Id);
    }

    [Theory]
    [InlineData("'", "Am 27.10.2026 um 14:30 Uhr zahlt Strauß CHF 1'250.50 für 12'500 m und 2.5 % auf 1'250'000.")]
    [InlineData(" ", "Am 27.10.2026 um 14:30 Uhr zahlt Strauß CHF 1 250.50 für 12 500 m und 2.5 % auf 1 250 000.")]
    [InlineData(" ", "Am 27.10.2026 um 14:30 Uhr zahlt Strauß CHF 1 250.50 für 12 500 m und 2.5 % auf 1 250 000.")]
    public void ThousandsSeparatorFollowsSettings(string separator, string expected)
    {
        var format = Regional(number =>
        {
            number.NumberGroupSeparator = separator;
            number.CurrencyGroupSeparator = separator;
        });

        Assert.Equal(expected, new SwissTextFormatter(Options, format).Format(Sentence));
    }

    [Fact]
    public void DecimalCommaFollowsSettings()
    {
        var format = Regional(number =>
        {
            number.NumberDecimalSeparator = ",";
            number.CurrencyDecimalSeparator = ",";
        });

        Assert.Equal(
            "Am 27.10.2026 um 14:30 Uhr zahlt Strauß CHF 1’250,50 für 12’500 m und 2,5 % auf 1’250’000.",
            new SwissTextFormatter(Options, format).Format(Sentence));
    }

    [Fact]
    public void GroupSizesFollowSettings()
    {
        var format = Regional(number =>
        {
            number.NumberGroupSizes = [3, 2];
            number.CurrencyGroupSizes = [0];
        });

        Assert.Equal(
            "Am 27.10.2026 um 14:30 Uhr zahlt Strauß CHF 1250.50 für 12’500 m und 2.5 % auf 12’50’000.",
            new SwissTextFormatter(Options, format).Format(Sentence));
    }

    [Theory]
    [InlineData("yyyy-MM-dd", "H:mm", "Am 2026-10-27 um 14:30 Uhr")]
    [InlineData("d.M.yy", "HH.mm", "Am 27.10.26 um 14.30 Uhr")]
    [InlineData("dd/MM/yyyy", "hh:mm tt", "Am 27/10/2026 um 02:30 PM")]
    public void DateAndTimePatternsFollowSettings(string datePattern, string timePattern, string expectedStart)
    {
        var format = Regional(dateTime: dateTime =>
        {
            dateTime.ShortDatePattern = datePattern;
            dateTime.ShortTimePattern = timePattern;
            dateTime.DateSeparator = "/";
            dateTime.AMDesignator = "AM";
            dateTime.PMDesignator = "PM";
        });

        Assert.StartsWith(expectedStart + " ", new SwissTextFormatter(Options, format).Format(Sentence));
    }

    [Fact]
    public void LongDatePatternAndMonthNamesFollowSettings()
    {
        var format = Regional(dateTime: dateTime =>
        {
            dateTime.LongDatePattern = "dddd d MMMM yyyy";
            dateTime.MonthGenitiveNames = ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre", ""];
            dateTime.MonthNames = dateTime.MonthGenitiveNames;
        });
        var formatter = new SwissTextFormatter(Options with { DateStyle = DateStyle.Long }, format);

        Assert.Equal("am 27 octobre 2026", formatter.Format("am 27.10.2026"));
    }

    [Theory]
    [InlineData(0, "CHF", "CHF1’250.50 und EUR3")]
    [InlineData(1, "CHF", "1’250.50CHF und 3EUR")]
    [InlineData(2, "Fr.", "Fr. 1’250.50 und EUR 3")]
    [InlineData(3, "CHF", "1’250.50 CHF und 3 EUR")]
    public void CurrencyPatternAndSymbolFollowSettings(int pattern, string symbol, string expected)
    {
        var format = Regional(number =>
        {
            number.CurrencyPositivePattern = pattern;
            number.CurrencySymbol = symbol;
        });

        Assert.Equal(expected, new SwissTextFormatter(Options, format).Format("1.250,50 Franken und 3 Euro"));
    }

    [Fact]
    public void ForeignLocalCurrencySymbolIsNotUsedForFranken()
    {
        var format = Regional(number => number.CurrencySymbol = "€", currencyIsoCode: "EUR");

        Assert.Equal("CHF 12.50 und € 3", new SwissTextFormatter(Options, format).Format("12,5 Franken und 3 Euro"));
    }

    // Codex-Review R5: gesprochene Rappen folgen derselben Stellenregel wie geschriebene Beträge
    [Theory]
    [InlineData(3, "12,5 Franken", "CHF 12.500")]
    [InlineData(3, "12 Franken 50", "CHF 12.500")]
    [InlineData(3, "12 Franken und 5 Rappen", "CHF 12.050")]
    [InlineData(3, "CHF 12.5", "CHF 12.500")]
    [InlineData(2, "12 Franken 50", "CHF 12.50")]
    [InlineData(0, "12,50 Franken", "CHF 12.50")]
    [InlineData(0, "12 Franken 5 Rappen", "CHF 12.05")]
    [InlineData(2, "12,345 Franken", "CHF 12.345")]
    [InlineData(3, "20 Franken", "CHF 20")]
    public void CurrencyDecimalDigitsPadButNeverCut(int digits, string input, string expected)
    {
        var format = Regional(number => number.CurrencyDecimalDigits = digits);

        Assert.Equal(expected, new SwissTextFormatter(Options, format).Format(input));
    }

    [Fact]
    public void NegativeAmountUsesDeChPattern()
    {
        Assert.Equal("CHF-12.50 und CHF-1’250", new SwissTextFormatter(Options).Format("-12,50 Franken und CHF -1250"));
    }

    // Codex-Review R5: Vorzeichen gehört zum Betrag und folgt dem negativen Währungsmuster
    [Theory]
    [InlineData(0, "(CHF12.50)")]
    [InlineData(1, "-CHF12.50")]
    [InlineData(2, "CHF-12.50")]
    [InlineData(3, "CHF12.50-")]
    [InlineData(4, "(12.50CHF)")]
    [InlineData(5, "-12.50CHF")]
    [InlineData(6, "12.50-CHF")]
    [InlineData(7, "12.50CHF-")]
    [InlineData(8, "-12.50 CHF")]
    [InlineData(9, "-CHF 12.50")]
    [InlineData(10, "12.50 CHF-")]
    [InlineData(11, "CHF 12.50-")]
    [InlineData(12, "CHF -12.50")]
    [InlineData(13, "12.50- CHF")]
    [InlineData(14, "(CHF 12.50)")]
    [InlineData(15, "(12.50 CHF)")]
    [InlineData(16, "CHF- 12.50")]
    public void NegativeCurrencyPatternFollowsSettings(int pattern, string expected)
    {
        var format = Regional(number => number.CurrencyNegativePattern = pattern);
        var formatter = new SwissTextFormatter(Options, format);

        Assert.Equal(expected, formatter.Format("-12,50 Franken"));
        Assert.Equal(expected, formatter.Format("-12 Franken 50"));
        Assert.Equal(expected, formatter.Format("CHF -12.50"));
        Assert.Equal(expected, formatter.Format("-CHF 12.50"));
        Assert.Equal(expected, formatter.Format("−12,50 Franken"));
    }

    // Codex-Nachprüfung N3: neu erzeugte Beträge bleiben bei einem zweiten Durchlauf gleich
    [Theory]
    [InlineData(2, 0, "-12,50 Franken", "(CHF12.50)")]
    [InlineData(2, 4, "-12,50 Franken", "(12.50CHF)")]
    [InlineData(2, 15, "-12,50 Franken", "(12.50 CHF)")]
    [InlineData(2, 14, "-12,50 Franken", "(CHF 12.50)")]
    public void FormattedNegativeAmountStaysOnSecondPass(int positive, int negative, string input, string expected)
    {
        var format = Regional(number =>
        {
            number.CurrencyPositivePattern = positive;
            number.CurrencyNegativePattern = negative;
        });
        var formatter = new SwissTextFormatter(Options, format);

        var once = formatter.Format(input);

        Assert.Equal(expected, once);
        Assert.Equal(once, formatter.Format(once));
    }

    public static TheoryData<int, int> AllCurrencyPatterns()
    {
        var data = new TheoryData<int, int>();
        for (var positive = 0; positive <= 3; positive++)
        {
            for (var negative = 0; negative <= 16; negative++)
            {
                data.Add(positive, negative);
            }
        }

        return data;
    }

    // Codex-Nachprüfung N3: zweimal formatieren ergibt dasselbe wie einmal, für alle Währungsmuster
    [Theory]
    [MemberData(nameof(AllCurrencyPatterns))]
    public void FormattingIsIdempotentForAllCurrencyPatterns(int positive, int negative)
    {
        const string input =
            "Strauß zahlt -12,50 Franken, CHF -3 und 1.250,50 Franken, erhält -1.250 Franken 5 Rappen und 20 Euro, "
            + "Fr. 20.– und −7,5 Franken; am 27. Oktober 2026 um 14.30 Uhr, 12,5 Kilometer.";
        var format = Regional(number =>
        {
            number.CurrencyPositivePattern = positive;
            number.CurrencyNegativePattern = negative;
        });
        var formatter = new SwissTextFormatter(Options, format);

        var once = formatter.Format(input);

        Assert.Equal(once, formatter.Format(once));
    }

    // Codex-Nachprüfung 2, F3: ein gesprochener Betrag mit Rappen wird nie durch ein kürzeres fertiges Teilstück gesperrt
    [Theory]
    [MemberData(nameof(AllCurrencyPatterns))]
    public void SpokenSubunitsAreNeverSplitByLock(int positive, int negative)
    {
        var format = Regional(number =>
        {
            number.CurrencyPositivePattern = positive;
            number.CurrencyNegativePattern = negative;
        });
        var formatter = new SwissTextFormatter(Options, format);
        var plus = formatter.Format("12,50 Franken");
        var minus = formatter.Format("-12,50 Franken");

        Assert.Equal(plus, formatter.Format("12 CHF und 50 Rappen"));
        Assert.Equal(plus, formatter.Format("12 CHF 50 Rappen"));
        Assert.Equal(plus + ".", formatter.Format("12 CHF 50."));
        Assert.Equal(plus, formatter.Format(formatter.Format("12 Franken") + " und 50 Rappen"));
        Assert.Equal(minus, formatter.Format("-12 CHF und 50 Rappen"));
        Assert.Equal(minus, formatter.Format("-12 Franken und 50 Rappen"));
    }

    [Theory]
    [InlineData("CHF 12.5", "CHF 12.500")]
    [InlineData("CHF 1250.500", "CHF 1’250.500")]
    [InlineData("CHF 12.500", "CHF 12.500")]
    public void UnfinishedAmountsAreNotLocked(string input, string expected)
    {
        var format = Regional(number => number.CurrencyDecimalDigits = 3);

        Assert.Equal(expected, new SwissTextFormatter(Options, format).Format(input));
    }

    [Fact]
    public void NegativeSignFollowsSettings()
    {
        var format = Regional(number =>
        {
            number.NegativeSign = "\u2212";
            number.CurrencyNegativePattern = 12;
        });

        Assert.Equal("CHF \u221212.50", new SwissTextFormatter(Options, format).Format("-12,50 Franken"));
    }

    [Theory]
    [InlineData("10-12 Franken", "10-CHF 12")]
    [InlineData("Konto-12 Franken", "Konto-CHF 12")]
    [InlineData("12 Franken - 3 Franken", "CHF 12 - CHF 3")]
    public void HyphenAfterWordIsNoSign(string input, string expected)
    {
        Assert.Equal(expected, new SwissTextFormatter(Options).Format(input));
    }

    [Fact]
    public void FromCultureTakesUserFormat()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat = Number(number =>
        {
            number.NumberGroupSeparator = "'";
            number.CurrencyGroupSeparator = "'";
            number.CurrencySymbol = "Fr.";
            number.CurrencyPositivePattern = 3;
        });
        culture.DateTimeFormat = DateTimeFormat(dateTime => dateTime.ShortDatePattern = "d.M.yyyy");

        var format = RegionalFormat.FromCulture(culture, "CHF");

        Assert.Equal(
            "am 1.3.2027 zahlt Strauß 1'250.50 Fr.",
            new SwissTextFormatter(Options, format).Format("am 1. März 2027 zahlt Strauß 1.250,50 Franken"));
    }

    [Fact]
    public void FromCultureCopiesFormat()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat = Number(number => number.NumberGroupSeparator = "'");

        var format = RegionalFormat.FromCulture(culture, "CHF");
        culture.NumberFormat.NumberGroupSeparator = "_";

        Assert.Equal("'", format.Number.NumberGroupSeparator);
        Assert.True(format.Number.IsReadOnly);
    }

    [Fact]
    public void FromCultureWithoutRegionUsesIsoCodes()
    {
        var format = RegionalFormat.FromCulture(CultureInfo.InvariantCulture);

        Assert.Equal("", format.CurrencyIsoCode);
    }

    [Fact]
    public void RejectsMissingFormat()
    {
        Assert.Throws<ArgumentNullException>(() => new RegionalFormat(null!, RegionalFormat.SwissDefault.DateTime, "CHF"));
        Assert.Throws<ArgumentNullException>(() => new SwissTextFormatter(null!, RegionalFormat.SwissDefault));
    }

    /// <summary>de-CH-Voreinstellung mit einzelnen Abweichungen, wie sie ein Benutzer unter „Region“ einstellt.</summary>
    private static RegionalFormat Regional(
        Action<NumberFormatInfo>? number = null,
        Action<DateTimeFormatInfo>? dateTime = null,
        string currencyIsoCode = "CHF") =>
        new(Number(number), DateTimeFormat(dateTime), currencyIsoCode);

    private static NumberFormatInfo Number(Action<NumberFormatInfo>? change)
    {
        var number = (NumberFormatInfo)RegionalFormat.SwissDefault.Number.Clone();
        change?.Invoke(number);
        return number;
    }

    private static DateTimeFormatInfo DateTimeFormat(Action<DateTimeFormatInfo>? change)
    {
        var dateTime = (DateTimeFormatInfo)RegionalFormat.SwissDefault.DateTime.Clone();
        change?.Invoke(dateTime);
        return dateTime;
    }
}
