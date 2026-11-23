using System.Globalization;

namespace Freihand.Core.Formatting;

/// <summary>
/// Zahl-, Datums-, Zeit- und Währungsformat aus den Ländereinstellungen von Windows (BA-17): Tausendertrennzeichen,
/// Dezimalzeichen, Ziffergruppierung, kurzes und langes Datum, kurze Zeit, Währungssymbol und Währungsmuster.
/// </summary>
/// <remarks>
/// Der Kern liest keine Systemeinstellungen. Die Windows-Schicht übergibt <c>CultureInfo.CurrentCulture</c> samt
/// Anpassungen des Benutzers an <see cref="FromCulture"/> und baut den Formatierer bei einer Änderung neu.
/// Ohne übergebenes Format gilt <see cref="SwissDefault"/>.
/// </remarks>
public sealed class RegionalFormat
{
    /// <param name="number">Zahl- und Währungsformat; wird kopiert.</param>
    /// <param name="dateTime">Datums- und Zeitformat; wird kopiert.</param>
    /// <param name="currencyIsoCode">
    /// ISO-Code der Landeswährung, z. B. CHF. Nur Beträge in dieser Währung erhalten <see cref="NumberFormatInfo.CurrencySymbol"/>;
    /// andere Währungen erhalten ihren ISO-Code.
    /// </param>
    public RegionalFormat(NumberFormatInfo number, DateTimeFormatInfo dateTime, string currencyIsoCode)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(dateTime);
        ArgumentNullException.ThrowIfNull(currencyIsoCode);

        Number = NumberFormatInfo.ReadOnly((NumberFormatInfo)number.Clone());
        DateTime = DateTimeFormatInfo.ReadOnly((DateTimeFormatInfo)dateTime.Clone());
        CurrencyIsoCode = currencyIsoCode;
    }

    /// <summary>
    /// de-CH, wie Windows es ausliefert: 1’250.5, 27.10.2026, „Dienstag, 27. Oktober 2026“, 14:30, CHF 1’250.50,
    /// CHF-1’250.50.
    /// Die Werte stehen ausdrücklich im Code, damit die Voreinstellung nicht von den ICU-Daten des Systems abhängt.
    /// </summary>
    public static RegionalFormat SwissDefault { get; } = CreateSwissDefault();

    public NumberFormatInfo Number { get; }

    public DateTimeFormatInfo DateTime { get; }

    public string CurrencyIsoCode { get; }

    /// <summary>Übernimmt das Format einer Kultur, z. B. <c>CultureInfo.CurrentCulture</c> mit Anpassungen des Benutzers.</summary>
    /// <param name="culture">Kultur samt allfälligen Anpassungen.</param>
    /// <param name="currencyIsoCode">ISO-Code der Landeswährung; ohne Angabe aus der Region der Kultur, sonst leer.</param>
    public static RegionalFormat FromCulture(CultureInfo culture, string? currencyIsoCode = null)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return new RegionalFormat(culture.NumberFormat, culture.DateTimeFormat, currencyIsoCode ?? RegionCurrency(culture));
    }

    private static string RegionCurrency(CultureInfo culture)
    {
        try
        {
            return new RegionInfo(culture.Name).ISOCurrencySymbol;
        }
        catch (ArgumentException)
        {
            // Neutrale oder unveränderliche Kultur ohne Region
            return "";
        }
    }

    private static RegionalFormat CreateSwissDefault()
    {
        var number = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        number.NumberGroupSeparator = "’";
        number.NumberDecimalSeparator = ".";
        number.NumberGroupSizes = [3];
        number.CurrencySymbol = "CHF";
        number.CurrencyGroupSeparator = "’";
        number.CurrencyDecimalSeparator = ".";
        number.CurrencyGroupSizes = [3];
        number.CurrencyDecimalDigits = 2;
        number.CurrencyPositivePattern = 2;
        number.CurrencyNegativePattern = 2;
        number.NegativeSign = "-";

        string[] months = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember", ""];
        string[] shortMonths = ["Jan.", "Feb.", "März", "Apr.", "Mai", "Juni", "Juli", "Aug.", "Sept.", "Okt.", "Nov.", "Dez.", ""];

        var dateTime = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
        dateTime.MonthNames = months;
        dateTime.MonthGenitiveNames = months;
        dateTime.AbbreviatedMonthNames = shortMonths;
        dateTime.AbbreviatedMonthGenitiveNames = shortMonths;
        dateTime.DayNames = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];
        dateTime.AbbreviatedDayNames = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];
        dateTime.ShortestDayNames = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];
        dateTime.DateSeparator = ".";
        dateTime.TimeSeparator = ":";
        dateTime.ShortDatePattern = "dd.MM.yyyy";
        dateTime.LongDatePattern = "dddd, d. MMMM yyyy";
        dateTime.ShortTimePattern = "HH:mm";
        dateTime.LongTimePattern = "HH:mm:ss";
        dateTime.AMDesignator = "";
        dateTime.PMDesignator = "";

        return new RegionalFormat(number, dateTime, "CHF");
    }
}
