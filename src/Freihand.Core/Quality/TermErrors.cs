using System.Text;

namespace Freihand.Core.Quality;

/// <summary>Art eines im Testsatz markierten Begriffs (QM-01, QM-02).</summary>
public enum TermCategory
{
    /// <summary>Eigenname, z. B. Person, Firma, Ort.</summary>
    Name,

    /// <summary>Zahl in der erwarteten Schreibweise, z. B. „1'250“, „3,5“, „12.30“ (BA-17).</summary>
    Number,
}

/// <summary>Ein Begriff, der im Ergebnis genau so vorkommen soll; jeder Eintrag ist ein Vorkommen.</summary>
public sealed record ExpectedTerm(TermCategory Category, string Text);

/// <summary>Fehler einer Begriffsart. <see cref="Rate"/> ist <c>null</c>, wenn nichts erwartet war.</summary>
public sealed record CategoryErrors(TermCategory Category, int Expected, int Errors)
{
    public double? Rate => Expected > 0 ? (double)Errors / Expected : null;
}

/// <summary>
/// Fehler bei Namen und Zahlen (QM-02). Gesucht wird die erwartete Schreibweise wörtlich und mit
/// Gross-/Kleinschreibung (Unicode-Normalform C, typografische Apostrophe vereinheitlicht), nur an
/// Wortgrenzen: Vor und nach dem Fund steht kein Buchstabe und keine Ziffer, auch kein Trennzeichen
/// „, . : '“ direkt vor bzw. nach einer Ziffer. So zählt „3“ nicht in „3,5“ und „Max“ nicht in „Maxi“.
/// Fehler je Begriff = erwartete minus gefundene Vorkommen, nie unter 0. Überzählige Vorkommen zählen
/// hier nicht; sie erscheinen in der Wortfehlerrate als Einfügungen.
/// </summary>
public static class TermErrors
{
    private const string DigitSeparators = ",.:'";

    /// <summary>Gibt für jede Begriffsart einen Eintrag zurück, auch ohne erwartete Begriffe.</summary>
    public static IReadOnlyList<CategoryErrors> Evaluate(string hypothesis, IEnumerable<ExpectedTerm> expected)
    {
        ArgumentNullException.ThrowIfNull(hypothesis);
        ArgumentNullException.ThrowIfNull(expected);

        var terms = expected.ToArray();
        return
        [
            .. Enum.GetValues<TermCategory>().Select(category =>
            {
                var ofCategory = terms.Where(t => t.Category == category).ToArray();
                var errors = ofCategory
                    .GroupBy(t => t.Text, StringComparer.Ordinal)
                    .Sum(g => Math.Max(0, g.Count() - CountOccurrences(hypothesis, g.Key)));
                return new CategoryErrors(category, ofCategory.Length, errors);
            }),
        ];
    }

    /// <summary>Zählt nicht überlappende Vorkommen an Wortgrenzen.</summary>
    public static int CountOccurrences(string text, string term)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(term);

        var haystack = Prepare(text);
        var needle = Prepare(term).Trim();
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            var end = index + needle.Length;
            if (IsBoundaryBefore(haystack, index) && IsBoundaryAfter(haystack, end))
            {
                count++;
                index = haystack.IndexOf(needle, end, StringComparison.Ordinal);
            }
            else
            {
                index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
            }
        }

        return count;
    }

    private static string Prepare(string text) =>
        TextNormalizer.UnifyApostrophes(text.Normalize(NormalizationForm.FormC));

    private static bool IsBoundaryBefore(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = text[index - 1];
        if (char.IsLetterOrDigit(previous))
        {
            return false;
        }

        return !(DigitSeparators.Contains(previous) && index >= 2 && char.IsDigit(text[index - 2]));
    }

    private static bool IsBoundaryAfter(string text, int end)
    {
        if (end >= text.Length)
        {
            return true;
        }

        var next = text[end];
        if (char.IsLetterOrDigit(next))
        {
            return false;
        }

        return !(DigitSeparators.Contains(next) && end + 1 < text.Length && char.IsDigit(text[end + 1]));
    }
}
