using System.Globalization;
using System.Text;

namespace Freihand.Core.Quality;

/// <summary>
/// Normalisierung für die Wortfehlerrate (QM-02). Referenz und Erkennung durchlaufen dieselben Schritte:
/// <list type="number">
/// <item>Unicode-Normalform C; Zeilenumbrüche gelten als Leerraum.</item>
/// <item>Kleinschreibung (invariante Kultur); „ß“ wird zu „ss“ (Schweizer Schreibweise, BA-17).</item>
/// <item>Typografische Apostrophe (’ ‘ ʼ ´ `) werden zu „'“.</item>
/// <item>Zwischen zwei Ziffern bleiben „,“ „.“ und „:“ erhalten (3,5 · 12.30 · 12:30); ein Apostroph als
/// Tausendertrennzeichen fällt weg (1'000 → 1000).</item>
/// <item>Alle übrigen Satzzeichen werden zu Leerraum, ausser „@ % &amp; #“. Buchstaben, Ziffern, Zeichen der
/// Kategorie Symbol (z. B. €, +) und kombinierende Zeichen bleiben.</item>
/// <item>Getrennt wird an Leerraum.</item>
/// </list>
/// Satzzeichen und Umbrüche zählen damit nicht zur Wortfehlerrate; sie werden als Diktatzeichen
/// getrennt gemessen (<see cref="DictationMarks"/>).
/// </summary>
public static class TextNormalizer
{
    private const string KeptPunctuation = "@%&#";
    private const string DigitSeparators = ",.:";
    private const string Apostrophes = "’‘ʼ´`";

    public static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var prepared = UnifyApostrophes(text.Normalize(NormalizationForm.FormC))
            .ToLowerInvariant()
            .Replace("ß", "ss", StringComparison.Ordinal);

        var builder = new StringBuilder(prepared.Length);
        for (var i = 0; i < prepared.Length; i++)
        {
            var c = prepared[i];
            var betweenDigits = i > 0 && i < prepared.Length - 1
                && char.IsDigit(prepared[i - 1]) && char.IsDigit(prepared[i + 1]);

            if (betweenDigits && c == '\'')
            {
                continue;
            }

            builder.Append(IsKept(c) || (betweenDigits && DigitSeparators.Contains(c)) ? c : ' ');
        }

        return builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Normalisierter Text, Wörter mit je einem Leerschlag getrennt.</summary>
    public static string Normalize(string text) => string.Join(' ', Tokenize(text));

    /// <summary>Vereinheitlicht typografische Apostrophe, auch für die Suche nach Namen und Zahlen.</summary>
    internal static string UnifyApostrophes(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Apostrophes.Contains(chars[i]))
            {
                chars[i] = '\'';
            }
        }

        return new string(chars);
    }

    private static bool IsKept(char c)
    {
        if (char.IsLetterOrDigit(c) || KeptPunctuation.Contains(c))
        {
            return true;
        }

        return CharUnicodeInfo.GetUnicodeCategory(c) is
            UnicodeCategory.MathSymbol or
            UnicodeCategory.CurrencySymbol or
            UnicodeCategory.ModifierSymbol or
            UnicodeCategory.OtherSymbol or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark;
    }
}
