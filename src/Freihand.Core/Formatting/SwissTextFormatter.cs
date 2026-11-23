using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Freihand.Core.Formatting;

/// <summary>
/// Nachbearbeitung von festem Text zu Hochdeutsch in Schweizer Schreibweise (BA-17, Grundentscheid 1): ß zu ss,
/// Datum, Uhrzeit, Zahlen, Beträge, Einheiten und Fachabkürzungen. Geschützte Wörter (BA-03) bleiben unverändert;
/// Text ohne Treffer bleibt Zeichen für Zeichen gleich.
/// </summary>
/// <remarks>
/// Läuft, bevor ein Abschnitt fest wird; ein fester Abschnitt wird danach nicht mehr verändert (ER-02). Im Modus
/// „wörtlich schreiben“ (BA-22) ruft der Aufrufer die Nachbearbeitung nicht auf. Zahl-, Datums-, Zeit- und
/// Währungsformat kommen aus dem übergebenen <see cref="RegionalFormat"/>, nie aus der Kultur des Systems; ohne
/// Angabe gilt de-CH. Ändert der Benutzer die Ländereinstellungen, baut der Aufrufer einen neuen Formatierer.
/// </remarks>
public sealed partial class SwissTextFormatter
{
    private const string Space = @"[  ]";
    private const string NotWordAfter = @"(?![\p{L}\p{N}\-])";
    private const string NotWordBefore = @"(?<![\p{L}\p{N}])";

    // Gesprochene Rappen nach dem Betrag: „ 50 Rappen“, „ und 5 Rappen“, „ 50“ am Satzende
    private const string SubunitTail =
        Space + "(?:und" + Space + ")?(?<sub>[0-9]{1,2})(?:" + Space + @"(?:Rappen|Cent)(?![\p{L}])|(?=" + Space + @"*(?:[.,;:!?)](?![0-9])|$)))";

    // Gesprochener Wochentag vor einem Datum: „Dienstag, “
    private const string WeekdayPrefix = "(?<wdpart>" + NotWordBefore + "(?<wd>Montag|Dienstag|Mittwoch|Donnerstag|Freitag|Samstag|Sonntag),?" + Space + ")?";

    // Währungsmuster wie NumberFormatInfo.CurrencyPositivePattern und CurrencyNegativePattern:
    // $ Währungssymbol, n Betrag, - negatives Vorzeichen
    private static readonly string[] PositiveCurrencyPatterns = ["$n", "n$", "$ n", "n $"];

    private static readonly string[] NegativeCurrencyPatterns =
    [
        "($n)", "-$n", "$-n", "$n-", "(n$)", "-n$", "n-$", "n$-", "-n $",
        "-$ n", "n $-", "$ n-", "$ -n", "n- $", "($ n)", "(n $)", "$- n",
    ];

    // Reihenfolge wie DayOfWeek
    private static readonly string[] Weekdays = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];

    private static readonly string[] CurrencyWords = ["Schweizer Franken", "Franken", "CHF", "SFr.", "sFr.", "Fr.", "Euro", "EUR", "€", "Rappen", "Cent"];

    private readonly SwissFormatOptions _options;
    private readonly RegionalFormat _regional;
    private readonly Regex? _protectedTerms;
    private readonly Regex? _abbreviations;
    private readonly Regex? _units;
    private readonly Regex _formattedAmount;
    private readonly HashSet<string> _numberTriggers;

    /// <param name="options">Regeln des Profils (Regelsatz, z. B. <c>ch-standard</c>).</param>
    /// <param name="regional">Ländereinstellungen des Benutzers; ohne Angabe <see cref="RegionalFormat.SwissDefault"/>.</param>
    public SwissTextFormatter(SwissFormatOptions options, RegionalFormat? regional = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _regional = regional ?? RegionalFormat.SwissDefault;
        _protectedTerms = WordAlternation(options.ProtectedTerms, "", NotWordAfter);
        _abbreviations = WordAlternation(options.Abbreviations.Keys, "", NotWordAfter);
        _units = WordAlternation(options.Units.Keys, $"(?<num>{SwissNumber.Pattern}){Space}(?<unit>", ")" + NotWordAfter);
        _formattedAmount = FormattedAmountRegex(_regional.Number);

        // Vor diesen Wörtern wird auch ein kleines Zahlwort zu Ziffern: „drei Kilometer“ → „3 km“
        _numberTriggers = new HashSet<string>(StringComparer.Ordinal) { "Uhr" };
        _numberTriggers.UnionWith(options.Units.Keys.Select(k => k.Split(' ')[0]));
        _numberTriggers.UnionWith(options.Units.Values);
        _numberTriggers.UnionWith(CurrencyWords);
    }

    /// <summary>Formatiert festen Text nach den Optionen des Profils.</summary>
    public string Format(string text) => Format(text, []);

    /// <summary>
    /// Wie <see cref="Format(string)"/>; die Bereiche <paramref name="unchanged"/> bleiben Zeichen für Zeichen (eingefügte
    /// gelernte Schreibweisen, P11 S4: „Maßmann“ bleibt „Maßmann“). Geschützt wird nach Lage, nicht nach Wortlaut: ein
    /// gleichlautendes Wort an anderer Stelle formatiert der Formatter wie sonst (Codex zu PR #66).
    /// </summary>
    public string Format(string text, IReadOnlyList<(int Start, int Length)> unchanged)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(unchanged);
        if (!_options.Enabled || text.Length == 0)
        {
            return text;
        }

        var buffer = new FormatBuffer(text);
        buffer.LockRanges(unchanged);

        // Adressen und geschützte Wörter zuerst sperren; keine spätere Regel ändert sie (BA-03, BA-17)
        buffer.Lock(AddressRegex());
        if (_protectedTerms is not null)
        {
            buffer.Lock(_protectedTerms);
        }

        if (_options.ReplaceSharpS)
        {
            buffer.Apply(SharpSRegex(), m => m.Value == "ß" ? "ss" : "SS", lockResult: false);
        }

        if (_abbreviations is not null)
        {
            buffer.Apply(_abbreviations, m => _options.Abbreviations[m.Value], lockResult: true);
        }

        if (_options.SpokenNumbersToDigits)
        {
            buffer.Apply(WordRegex(), ReplaceNumberWord, lockResult: false);
        }

        if (_options.DateStyle != DateStyle.Unchanged)
        {
            buffer.Apply(LongDateRegex(), ReplaceDate, lockResult: true);
            buffer.Apply(NumericDateRegex(), ReplaceDate, lockResult: true);
        }

        if (_options.FormatAmounts)
        {
            // Schon fertig formatierte Beträge sperren, damit ein zweiter Durchlauf nichts ändert
            buffer.Apply(_formattedAmount, KeepFormattedAmount, lockResult: true);
            // Code nach dem Betrag zuerst, damit „12 CHF 50 Rappen“ nicht als „CHF 50“ zerfällt
            buffer.Apply(AmountBeforeWordRegex(), ReplaceAmount, lockResult: true);
            buffer.Apply(AmountAfterCodeRegex(), ReplaceAmount, lockResult: true);
        }

        if (_options.AbbreviateUnits && _units is not null)
        {
            buffer.Apply(_units, ReplaceUnit, lockResult: true);
        }

        // Nach Beträgen und Einheiten, damit „14 Uhr 20 Franken“ keine Uhrzeit wird
        if (_options.TimeStyle != TimeStyle.Unchanged)
        {
            buffer.Apply(TimeRegex(), ReplaceTime, lockResult: true);
            buffer.Apply(SpokenTimeRegex(), ReplaceTime, lockResult: true);
        }

        buffer.Apply(NumberRegex(), ReplaceNumber, lockResult: false);

        return buffer.Text;
    }

    private string? ReplaceNumberWord(Match match)
    {
        if (GermanNumberWords.Parse(match.Groups["word"].Value) is not int value)
        {
            return null;
        }

        var next = match.Groups["next"].Value;
        var beforeTrigger = next.Length > 0 && (_numberTriggers.Contains(next) || _numberTriggers.Contains(next.TrimEnd('.')));

        // „hundert“ und „tausend“ allein bleiben Wörter („tausend Dank“), ausser vor einer Einheit
        var bare = value is 100 or 1000 && !match.Groups["word"].Value.StartsWith("ein", StringComparison.OrdinalIgnoreCase);
        if (beforeTrigger || (!bare && value >= _options.SpokenNumberDigitsFrom))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private string? ReplaceDate(Match match)
    {
        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        var month = match.Groups["m"].Value is var m && m.Length > 0 && char.IsAsciiDigit(m[0])
            ? int.Parse(m, CultureInfo.InvariantCulture)
            : MonthNumber(m);
        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);

        if (month is < 1 or > 12 || year < 1000 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var date = new DateTime(year, month, day);
        var weekdayPart = match.Groups["wdpart"].Value;
        var pattern = _options.DateStyle == DateStyle.Long ? _regional.DateTime.LongDatePattern : _regional.DateTime.ShortDatePattern;

        if (WeekdayInPatternRegex().IsMatch(pattern))
        {
            if (weekdayPart.Length == 0)
            {
                // Kein Wochentag gesprochen: keiner wird ergänzt
                pattern = WeekdayInPatternRegex().Replace(pattern, "").Trim();
            }
            else if (WeekdayNumber(match.Groups["wd"].Value) != (int)date.DayOfWeek)
            {
                // Wochentag passt nicht zum Datum: nichts still korrigieren (BA-18)
                return null;
            }
            else
            {
                weekdayPart = "";
            }
        }

        return weekdayPart + date.ToString(pattern, _regional.DateTime);
    }

    private string? ReplaceTime(Match match)
    {
        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59)
        {
            return null;
        }

        var pattern = _regional.DateTime.ShortTimePattern;
        var time = new DateTime(2000, 1, 1, hour, minute, 0).ToString(pattern, _regional.DateTime);

        // Mit Vormittag/Nachmittag (z. B. „2:30 PM“) entfällt „Uhr“
        return pattern.Contains('t', StringComparison.Ordinal) ? time : time + " Uhr";
    }

    private string? ReplaceAmount(Match match)
    {
        if (SwissNumber.Parse(match.Groups["num"].Value) is not SwissNumber number)
        {
            return null;
        }

        var sub = match.Groups["sub"];
        if (sub.Success)
        {
            // „12 Franken 50“, „12 Franken und 5 Rappen“: gesprochene Rappen sind Hundertstel
            if (number.Fraction.Length > 0)
            {
                return null;
            }

            number = number with { Fraction = sub.Value.PadLeft(2, '0') };
        }

        // Symbol der Landeswährung nach Ländereinstellung, andere Währungen mit ISO-Code
        var code = match.Groups["cur"].Value is "Euro" or "EUR" or "€" ? "EUR" : "CHF";
        return RenderAmount(number, code, match.Groups["sign"].Success, match.Groups["dash"].Success);
    }

    /// <summary>
    /// Lässt einen Betrag stehen und sperrt ihn, wenn er genau so aussieht, wie diese Regeln ihn schreiben würden,
    /// z. B. „(CHF12.50)“ bei negativem Muster 0. Sonst bleibt er für die Betragsregeln frei.
    /// </summary>
    private string? KeepFormattedAmount(Match match)
    {
        var format = _regional.Number;
        var text = match.Groups["num"].Value;
        var decimalAt = text.LastIndexOf(format.CurrencyDecimalSeparator, StringComparison.Ordinal);
        var integer = decimalAt >= 0 ? text[..decimalAt] : text;
        var fraction = decimalAt >= 0 ? text[(decimalAt + format.CurrencyDecimalSeparator.Length)..] : "";
        if (format.CurrencyGroupSeparator.Length > 0)
        {
            integer = integer.Replace(format.CurrencyGroupSeparator, "", StringComparison.Ordinal);
        }

        if (integer.Length == 0 || !integer.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit))
        {
            return null;
        }

        var currency = match.Groups["cur"].Value;
        var code = currency == format.CurrencySymbol && _regional.CurrencyIsoCode.Length > 0 ? _regional.CurrencyIsoCode : currency;
        if (code is not ("CHF" or "EUR"))
        {
            return null;
        }

        var number = new SwissNumber(integer, fraction, Grouped: false);
        var expected = RenderAmount(number, code, match.Groups["neg"].Success, match.Groups["dash"].Success);
        return expected == match.Value ? match.Value : null;
    }

    /// <summary>Schreibt einen Betrag nach Währungsformat der Ländereinstellungen (BA-17).</summary>
    private string RenderAmount(SwissNumber number, string code, bool negative, bool dash)
    {
        var format = _regional.Number;

        // Nachkommastellen nach Ländereinstellung nur mit Nullen ergänzen; nie runden oder abschneiden,
        // damit kein Rappen verloren geht. Ganze Beträge bleiben ohne Nachkommastellen, wie gesprochen.
        if (number.Fraction.Length > 0 && number.Fraction.Length < format.CurrencyDecimalDigits)
        {
            number = number with { Fraction = number.Fraction.PadRight(format.CurrencyDecimalDigits, '0') };
        }

        var amount = number.Format(format.CurrencyGroupSeparator, format.CurrencyDecimalSeparator, format.CurrencyGroupSizes, MinGroupingDigits);
        if (dash && number.Fraction.Length == 0)
        {
            amount += format.CurrencyDecimalSeparator + "–";
        }

        var symbol = code == _regional.CurrencyIsoCode ? format.CurrencySymbol : code;
        var pattern = negative
            ? NegativeCurrencyPatterns[format.CurrencyNegativePattern]
            : PositiveCurrencyPatterns[format.CurrencyPositivePattern];

        var result = new StringBuilder(amount.Length + symbol.Length + 4);
        foreach (var c in pattern)
        {
            result.Append(c switch
            {
                '$' => symbol,
                'n' => amount,
                '-' => format.NegativeSign,
                _ => c.ToString(),
            });
        }

        return result.ToString();
    }

    /// <summary>
    /// Beträge im positiven oder negativen Währungsmuster der Ländereinstellungen, z. B. „CHF 1’250.50“ oder
    /// „(CHF12.50)“. Ob ein Treffer wirklich fertig ist, prüft <see cref="KeepFormattedAmount"/>.
    /// </summary>
    private static Regex FormattedAmountRegex(NumberFormatInfo format)
    {
        var symbols = new[] { format.CurrencySymbol, "CHF", "EUR" }
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .Select(Regex.Escape);
        var currency = $"(?<cur>{string.Join('|', symbols)})";
        var decimalSeparator = Regex.Escape(format.CurrencyDecimalSeparator);
        var groups = format.CurrencyGroupSeparator.Length > 0 ? $"(?:{Regex.Escape(format.CurrencyGroupSeparator)}[0-9]+)*" : "";
        var number = $"(?<num>[0-9]+{groups}(?:{decimalSeparator}[0-9]+)?)(?<dash>{decimalSeparator}–)?";

        string Build(string pattern) => string.Concat(pattern.Select(c => c switch
        {
            '$' => currency,
            'n' => number,
            '-' => Regex.Escape(format.NegativeSign),
            _ => Regex.Escape(c.ToString()),
        }));

        var separators = string.Join('|', new[] { format.CurrencyGroupSeparator, format.CurrencyDecimalSeparator }
            .Where(s => s.Length > 0)
            .Select(Regex.Escape)
            .Append("[.,'’]"));
        // Kein Minus davor: „-CHF 12.50“ ist ein noch nicht formatierter negativer Betrag. Keine Zahl davor und keine
        // Rappen danach: „12 CHF und 50 Rappen“ und „12 CHF 50 Rappen“ sind erst mit den Rappen vollständig und
        // dürfen nicht als „12 CHF“ oder „CHF 50“ gesperrt werden (Codex-Nachprüfung 2, F3)
        var negative = Build(NegativeCurrencyPatterns[format.CurrencyNegativePattern]);
        var positive = Build(PositiveCurrencyPatterns[format.CurrencyPositivePattern]);
        return new Regex(
            $@"(?<![\p{{L}}\p{{N}}.,'’\-−])(?<!{Regex.Escape(format.NegativeSign)})(?<![0-9]{Space}?)(?:(?<neg>{negative})|{positive})(?![\p{{L}}\p{{N}}])(?!(?:{separators})[0-9])(?!{SubunitTail})(?!{Space}+(?:Rappen|Cent)(?![\p{{L}}]))",
            RegexOptions.CultureInvariant);
    }

    private int MinGroupingDigits => Math.Max(4, _options.GroupingMinDigits);

    private string FormatNumber(SwissNumber number, bool group) => number.Format(
        _regional.Number.NumberGroupSeparator,
        _regional.Number.NumberDecimalSeparator,
        _regional.Number.NumberGroupSizes,
        group ? MinGroupingDigits : int.MaxValue);

    private string? ReplaceUnit(Match match)
    {
        if (SwissNumber.Parse(match.Groups["num"].Value) is not SwissNumber number)
        {
            return null;
        }

        return $"{FormatNumber(number, group: true)} {_options.Units[match.Groups["unit"].Value]}";
    }

    private string? ReplaceNumber(Match match)
    {
        // Nur gegliederte Zahlen und Dezimalkomma: Ungegliederte ganze Zahlen (Jahre, Postleitzahlen, Kundennummern)
        // und Punkte ohne Tausendergruppe (1.12. als Datum, 2.5 als Versionsnummer) bleiben
        if (SwissNumber.Parse(match.Value) is not SwissNumber number || (!number.Grouped && !match.Value.Contains(',')))
        {
            return null;
        }

        return FormatNumber(number, group: number.Grouped);
    }

    private static int MonthNumber(string month) => month switch
    {
        "Januar" or "Jänner" => 1,
        "Februar" => 2,
        "März" => 3,
        "April" => 4,
        "Mai" => 5,
        "Juni" => 6,
        "Juli" => 7,
        "August" => 8,
        "September" => 9,
        "Oktober" => 10,
        "November" => 11,
        "Dezember" => 12,
        _ => 0,
    };

    private static int WeekdayNumber(string weekday) => Array.IndexOf(Weekdays, weekday);

    /// <summary>Ganze Wörter oder Wortfolgen aus einer Liste, längste zuerst; null bei leerer Liste.</summary>
    private static Regex? WordAlternation(IEnumerable<string> terms, string prefix, string suffix)
    {
        var alternatives = terms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(t => t.Length)
            .ThenBy(t => t, StringComparer.Ordinal)
            .Select(Regex.Escape)
            .ToList();

        if (alternatives.Count == 0)
        {
            return null;
        }

        var start = prefix.Length == 0 ? NotWordBefore + "(?:" : prefix;
        var end = prefix.Length == 0 ? ")" + suffix : suffix;
        return new Regex(start + string.Join('|', alternatives) + end, RegexOptions.CultureInvariant);
    }

    // URL und E-Mail-Adressen
    [GeneratedRegex(@"(?:https?://|www\.)\S+|[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.CultureInvariant)]
    private static partial Regex AddressRegex();

    [GeneratedRegex("[ßẞ]", RegexOptions.CultureInvariant)]
    private static partial Regex SharpSRegex();

    // Ein Wort samt dem folgenden Wort, das über Ziffern oder Zahlwort entscheidet
    [GeneratedRegex(@"(?<![\p{L}\p{N}\-])(?<word>\p{L}+)(?![\p{L}\p{N}\-])(?=(?:" + Space + @"+(?<next>[\p{L}%€‰°][\p{L}.]*))?)", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    // 27. Oktober 2026, Dienstag, 27. Oktober 2026
    [GeneratedRegex(WeekdayPrefix + @"(?<![0-9.,'’])(?<d>[0-9]{1,2})\." + Space + "?(?<m>Januar|Jänner|Februar|März|April|Mai|Juni|Juli|August|September|Oktober|November|Dezember)" + Space + "(?<y>[0-9]{4})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LongDateRegex();

    // 27.10.2026, 1.3.2026, Dienstag, 27.10.2026
    [GeneratedRegex(WeekdayPrefix + @"(?<![0-9.,'’])(?<d>[0-9]{1,2})\.(?<m>[0-9]{1,2})\.(?<y>[0-9]{4})(?![0-9]|[.,][0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDateRegex();

    // Wochentag im Datumsmuster samt Trennzeichen: „dddd, “ in „dddd, d. MMMM yyyy“
    [GeneratedRegex(@"d{3,}[,\s]*|[,\s]*d{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex WeekdayInPatternRegex();

    // 14:30 Uhr, 14.30 Uhr
    [GeneratedRegex(@"(?<![0-9.,:'’])(?<h>[0-9]{1,2})[:.](?<min>[0-5][0-9])" + Space + "?Uhr" + NotWordAfter, RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();

    // 14 Uhr 30
    [GeneratedRegex(@"(?<![0-9.,:'’])(?<h>[0-9]{1,2})" + Space + "Uhr" + Space + "(?<min>[0-9]{1,2})(?![0-9]|[.,:'’][0-9]|" + Space + @"+(?:Minuten?|Sekunden?)(?![\p{L}]))", RegexOptions.CultureInvariant)]
    private static partial Regex SpokenTimeRegex();

    // CHF 1250.50, Fr. 20.–, -CHF 12.50, CHF -12.50, CHF 12 und 50 Rappen
    [GeneratedRegex(NotWordBefore + @"(?<sign>[-−])?(?<cur>CHF|SFr\.|sFr\.|Fr\.|EUR|€)" + Space + "?(?<sign>[-−])?(?<num>" + SwissNumber.Core + @")(?<dash>\.[–-]{1,2})?(?:" + SubunitTail + ")?" + NotWordAfter, RegexOptions.CultureInvariant)]
    private static partial Regex AmountAfterCodeRegex();

    // 1.250,50 Franken, 12 Franken 50, 12 Franken und 50 Rappen, 30 Euro, -12,50 Franken
    [GeneratedRegex(
        "(?:" + NotWordBefore + "(?<sign>[-−]))?(?<num>" + SwissNumber.Pattern + @")(?<dash>\.[–-]{1,2})?" + Space + @"?(?<cur>Schweizer Franken|Franken|CHF|SFr\.|sFr\.|Fr\.|Euro|EUR|€)"
        + "(?:" + SubunitTail + ")?"
        + NotWordAfter,
        RegexOptions.CultureInvariant)]
    private static partial Regex AmountBeforeWordRegex();

    [GeneratedRegex(SwissNumber.Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();
}
