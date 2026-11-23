using System.Text;

namespace Freihand.Core.Formatting;

/// <summary>
/// Eine Zahl als Ziffernfolgen, ohne Umweg über <c>double</c> oder <c>decimal</c>: Es wird nichts gerundet, und die
/// Ausgabe hängt von keiner Kultur des Systems ab, damit Linux und Windows gleich formatieren (BA-17).
/// </summary>
/// <param name="Integer">Ziffern vor dem Dezimalzeichen.</param>
/// <param name="Fraction">Ziffern nach dem Dezimalzeichen; leer bei ganzen Zahlen.</param>
/// <param name="Grouped">Die Erkennung hat Tausenderzeichen geschrieben.</param>
internal readonly record struct SwissNumber(string Integer, string Fraction, bool Grouped)
{
    /// <summary>
    /// Kern einer Zahl, wie die Erkennung sie liefert: deutsch (1.250,50), schweizerisch (1’250.50) oder
    /// ungegliedert (1250,50). Nur ASCII-Ziffern, weil <c>\d</c> in .NET alle Unicode-Ziffern trifft.
    /// </summary>
    public const string Core = @"(?:[0-9]{1,3}(?:[.'’][0-9]{3})+(?:[.,][0-9]+)?|[0-9]+(?:[.,][0-9]+)?)(?![0-9]|[.,'’:][0-9])";

    /// <summary>Eine frei stehende Zahl: nicht Teil eines Worts, einer Versionsnummer, eines Pfads oder einer Uhrzeit.</summary>
    public const string Pattern = @"(?<![\p{L}0-9.,'’/:])" + Core;

    /// <summary>
    /// Liest eine Zahl nach deutscher Schreibweise, wie die Erkennung sie liefert. Ein Punkt vor genau drei Ziffern
    /// bei höchstens drei Ziffern davor gilt als Tausenderpunkt (1.250 = 1250), sonst als Dezimalpunkt.
    /// </summary>
    public static SwissNumber? Parse(string text)
    {
        var lastComma = text.LastIndexOf(',');
        var lastPoint = text.LastIndexOf('.');
        var hasApostrophe = text.Contains('\'') || text.Contains('’');

        string integerPart;
        var fraction = "";

        if (lastComma >= 0)
        {
            // Komma ist bei deutscher Eingabe immer das Dezimalzeichen
            if (text.IndexOf(',') != lastComma || lastComma < lastPoint)
            {
                return null;
            }

            integerPart = text[..lastComma];
            fraction = text[(lastComma + 1)..];
        }
        else if (lastPoint >= 0 && (hasApostrophe || !IsGroupedWithPoints(text)))
        {
            if (text.IndexOf('.') != lastPoint)
            {
                return null;
            }

            integerPart = text[..lastPoint];
            fraction = text[(lastPoint + 1)..];
        }
        else
        {
            integerPart = text;
        }

        var digits = new StringBuilder(integerPart.Length);
        foreach (var c in integerPart)
        {
            if (c is >= '0' and <= '9')
            {
                digits.Append(c);
            }
            else if (c is not ('.' or '\'' or '’'))
            {
                return null;
            }
        }

        if (digits.Length == 0 || fraction.Any(c => c is < '0' or > '9'))
        {
            return null;
        }

        return new SwissNumber(digits.ToString(), fraction, digits.Length != integerPart.Length);
    }

    /// <summary>Schreibt die Zahl mit Trennzeichen und Ziffergruppierung aus den Ländereinstellungen (BA-17).</summary>
    /// <param name="groupSeparator">Tausendertrennzeichen, z. B. ’.</param>
    /// <param name="decimalSeparator">Dezimalzeichen, z. B. Punkt.</param>
    /// <param name="groupSizes">Ziffergruppierung wie <see cref="System.Globalization.NumberFormatInfo.NumberGroupSizes"/>.</param>
    /// <param name="minDigits">Erst ab so vielen Stellen vor dem Dezimalzeichen wird gegliedert.</param>
    public string Format(string groupSeparator, string decimalSeparator, int[] groupSizes, int minDigits)
    {
        var integer = Integer.Length >= minDigits ? Group(Integer, groupSeparator, groupSizes) : Integer;
        return Fraction.Length > 0 ? integer + decimalSeparator + Fraction : integer;
    }

    /// <summary>
    /// Gliedert von rechts wie .NET: Jede Grösse gilt für eine Gruppe, die letzte wiederholt sich; eine 0 am Ende
    /// beendet die Gliederung, z. B. [3, 2] für 12,34,567 oder [0] für keine Gliederung.
    /// </summary>
    private static string Group(string digits, string separator, int[] sizes)
    {
        var groups = new List<string>();
        var end = digits.Length;
        var index = 0;
        var size = sizes.Length > 0 ? sizes[0] : 0;

        while (size > 0 && end > size)
        {
            groups.Add(digits[(end - size)..end]);
            end -= size;
            if (index < sizes.Length - 1)
            {
                index++;
                size = sizes[index];
            }
        }

        groups.Add(digits[..end]);
        groups.Reverse();
        return string.Join(separator, groups);
    }

    private static bool IsGroupedWithPoints(string text)
    {
        var groups = text.Split('.');
        return groups[0].Length is >= 1 and <= 3 && groups.Skip(1).All(g => g.Length == 3);
    }
}
