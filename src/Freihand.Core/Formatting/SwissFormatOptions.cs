namespace Freihand.Core.Formatting;

/// <summary>Wie Datumsangaben geschrieben werden (BA-17).</summary>
public enum DateStyle
{
    /// <summary>Datumsangaben bleiben, wie die Erkennung sie liefert.</summary>
    Unchanged,

    /// <summary>Kurzes Datum der Ländereinstellungen, in de-CH 27.10.2026.</summary>
    Short,

    /// <summary>
    /// Langes Datum der Ländereinstellungen, in de-CH „Dienstag, 27. Oktober 2026“. Der Wochentag erscheint nur, wenn er
    /// gesprochen wurde und zum Datum passt; die App ergänzt keine Angabe, die nicht gesagt wurde.
    /// </summary>
    Long,
}

/// <summary>Wie Uhrzeiten vor „Uhr“ geschrieben werden (BA-17).</summary>
public enum TimeStyle
{
    /// <summary>Uhrzeiten bleiben, wie die Erkennung sie liefert.</summary>
    Unchanged,

    /// <summary>Kurze Zeit der Ländereinstellungen, in de-CH 14:30 Uhr.</summary>
    Regional,
}

/// <summary>
/// Schweizer Schreib- und Formatregeln eines Profils (BA-17, Grundentscheid 1). Das Profil (P2) verweist mit
/// <c>formatRules</c> auf die Kennung <see cref="Id"/>. Zahl-, Datums-, Zeit- und Währungsformat kommen nicht von hier,
/// sondern aus den Ländereinstellungen (<see cref="RegionalFormat"/>); hier stehen nur Regeln ohne Windows-Einstellung.
/// </summary>
public sealed record SwissFormatOptions
{
    /// <summary>Kennung des voreingestellten Regelsatzes; darauf verweisen die eingebauten Profile.</summary>
    public const string StandardId = "ch-standard";

    /// <summary>Kennung des Regelsatzes, auf die ein Profil verweist.</summary>
    public string Id { get; init; } = StandardId;

    /// <summary>Schaltet die ganze Nachbearbeitung ein oder aus.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>ß und ẞ werden zu ss und SS.</summary>
    public bool ReplaceSharpS { get; init; } = true;

    public DateStyle DateStyle { get; init; } = DateStyle.Short;

    public TimeStyle TimeStyle { get; init; } = TimeStyle.Regional;

    /// <summary>
    /// Ab so vielen Stellen vor dem Dezimalzeichen wird gegliedert; kleinere Werte als 4 gelten als 4. Wie gegliedert
    /// wird, bestimmen die Ländereinstellungen.
    /// </summary>
    public int GroupingMinDigits { get; init; } = 4;

    /// <summary>Beträge in Franken oder Euro erhalten Währungssymbol und Währungsmuster der Ländereinstellungen.</summary>
    public bool FormatAmounts { get; init; } = true;

    /// <summary>Ausgeschriebene Einheiten nach einer Zahl werden abgekürzt (<see cref="Units"/>).</summary>
    public bool AbbreviateUnits { get; init; } = true;

    /// <summary>Gesprochene Zahlwörter werden ab <see cref="SpokenNumberDigitsFrom"/> zu Ziffern.</summary>
    public bool SpokenNumbersToDigits { get; init; } = true;

    /// <summary>
    /// Kleinste Zahl, die als Ziffern geschrieben wird; darunter bleibt das Zahlwort, ausser vor einer Einheit,
    /// einer Währung oder „Uhr“. Voreinstellung 13: eins bis zwölf bleiben ausgeschrieben.
    /// </summary>
    public int SpokenNumberDigitsFrom { get; init; } = 13;

    /// <summary>Ausgeschriebene Einheit → Abkürzung. Schlüssel gelten genau so, wie sie geschrieben sind.</summary>
    public IReadOnlyDictionary<string, string> Units { get; init; } = DefaultUnits;

    /// <summary>Fachabkürzungen: Schreibweise → Schweizer Schreibweise. Schlüssel gelten genau so, wie sie geschrieben sind.</summary>
    public IReadOnlyDictionary<string, string> Abbreviations { get; init; } = DefaultAbbreviations;

    /// <summary>
    /// Geschützte Wörter, z. B. Namen aus dem persönlichen Vokabular (BA-03). Sie werden nie umgeschrieben;
    /// verglichen wird als ganzes Wort mit gleicher Gross- und Kleinschreibung.
    /// </summary>
    public IReadOnlyList<string> ProtectedTerms { get; init; } = [];

    /// <summary>Einheiten der Voreinstellung.</summary>
    public static IReadOnlyDictionary<string, string> DefaultUnits { get; } = new Dictionary<string, string>
    {
        ["Kilometer pro Stunde"] = "km/h",
        ["Stundenkilometer"] = "km/h",
        ["Kilometer"] = "km",
        ["Kilometern"] = "km",
        ["Meter"] = "m",
        ["Metern"] = "m",
        ["Zentimeter"] = "cm",
        ["Zentimetern"] = "cm",
        ["Millimeter"] = "mm",
        ["Millimetern"] = "mm",
        ["Quadratmeter"] = "m²",
        ["Quadratmetern"] = "m²",
        ["Kubikmeter"] = "m³",
        ["Kubikmetern"] = "m³",
        ["Kilogramm"] = "kg",
        ["Kilo"] = "kg",
        ["Gramm"] = "g",
        ["Tonnen"] = "t",
        ["Tonne"] = "t",
        ["Liter"] = "l",
        ["Litern"] = "l",
        ["Deziliter"] = "dl",
        ["Zentiliter"] = "cl",
        ["Milliliter"] = "ml",
        ["Prozent"] = "%",
        ["Promille"] = "‰",
        ["Grad Celsius"] = "°C",
        ["Kilowattstunden"] = "kWh",
        ["Kilowattstunde"] = "kWh",
        ["Kilowatt"] = "kW",
        ["Watt"] = "W",
        ["Megabyte"] = "MB",
        ["Gigabyte"] = "GB",
        ["Terabyte"] = "TB",
    };

    /// <summary>Fachabkürzungen der Voreinstellung: Schweizer Formen und Abkürzungen mit Leerschlag nach Duden.</summary>
    public static IReadOnlyDictionary<string, string> DefaultAbbreviations { get; } = new Dictionary<string, string>
    {
        ["MwSt."] = "MWST",
        ["MwSt"] = "MWST",
        ["Mwst."] = "MWST",
        ["Mwst"] = "MWST",
        ["z.B."] = "z. B.",
        ["d.h."] = "d. h.",
        ["u.a."] = "u. a.",
        ["z.T."] = "z. T.",
        ["o.Ä."] = "o. Ä.",
        ["i.d.R."] = "i. d. R.",
        ["u.U."] = "u. U.",
    };

    /// <summary>Regelsatz <c>ch-standard</c> für die eingebauten Profile „Standard“ und „Streng“ (BA-21).</summary>
    /// <remarks>Steht nach den Listen, weil statische Felder in Reihenfolge des Quelltexts entstehen.</remarks>
    public static SwissFormatOptions Default { get; } = new();
}
