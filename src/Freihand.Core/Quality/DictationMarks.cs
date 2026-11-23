namespace Freihand.Core.Quality;

/// <summary>Diktatzeichen aus Anhang A (BA-20).</summary>
public enum DictationMarkKind
{
    Comma,
    Period,
    QuestionMark,
    ExclamationMark,
    Colon,

    /// <summary>„neue Zeile“: genau ein Zeilenumbruch.</summary>
    LineBreak,

    /// <summary>„neuer Absatz“: zwei oder mehr Zeilenumbrüche hintereinander.</summary>
    ParagraphBreak,
}

/// <summary>
/// Erwartete und gefundene Anzahl einer Zeichenart samt Fehlern. In einem Satz sind die Fehler der Betrag der
/// Differenz; über mehrere Sätze sind sie die Summe der Fehler je Satz und damit nicht aus den summierten
/// Anzahlen ableitbar: Ein fehlendes Komma in einem Satz und ein überzähliges in einem anderen sind zwei
/// Fehler, nicht null (QM-02, BA-20).
/// </summary>
public sealed record DictationMarkCount(DictationMarkKind Kind, int Expected, int Found, int Errors)
{
    /// <summary>Anzahlen und Fehler für einen einzelnen Satz.</summary>
    public static DictationMarkCount ForSentence(DictationMarkKind kind, int expected, int found) =>
        new(kind, expected, found, Math.Abs(expected - found));
}

/// <summary>Fehler bei Diktatzeichen eines oder mehrerer Testsätze.</summary>
public sealed record DictationMarkErrors(IReadOnlyList<DictationMarkCount> Kinds)
{
    public int Expected => Kinds.Sum(k => k.Expected);

    public int Errors => Kinds.Sum(k => k.Errors);

    /// <summary><c>null</c>, wenn kein Zeichen erwartet war.</summary>
    public double? Rate => Expected > 0 ? (double)Errors / Expected : null;

    public static DictationMarkErrors Sum(IEnumerable<DictationMarkErrors> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var all = items.ToArray();
        // Fehler je Satz summieren, nie aus summierten Anzahlen neu bilden; sonst heben sich Fehler auf (QM-02)
        return new DictationMarkErrors(
        [
            .. Enum.GetValues<DictationMarkKind>().Select(kind => new DictationMarkCount(
                kind,
                all.Sum(e => e.Kinds.Where(k => k.Kind == kind).Sum(k => k.Expected)),
                all.Sum(e => e.Kinds.Where(k => k.Kind == kind).Sum(k => k.Found)),
                all.Sum(e => e.Kinds.Where(k => k.Kind == kind).Sum(k => k.Errors)))),
        ]);
    }
}

/// <summary>
/// Fehler bei Diktatzeichen (QM-02, BA-20): Je Satz und Zeichenart wird gezählt, wie oft sie in Referenz
/// und Ergebnis vorkommt; Fehler ist der Betrag der Differenz, über mehrere Sätze die Summe dieser Fehler.
/// Ein fehlendes Zeichen zählt also ebenso wie ein doppeltes, denn ein gesprochenes Diktatzeichen soll
/// genau einmal erscheinen (Kapitel 16). „, . :“ zwischen zwei Ziffern gehören zur Zahl und zählen nicht. „\r\n“ gilt als ein Umbruch; eine
/// Folge von Umbrüchen zählt als ein Absatz, sobald sie mindestens zwei umfasst.
/// </summary>
public static class DictationMarks
{
    public static DictationMarkErrors Evaluate(string reference, string hypothesis)
    {
        var expected = Count(reference);
        var found = Count(hypothesis);
        return new DictationMarkErrors(
        [
            .. Enum.GetValues<DictationMarkKind>().Select(kind => DictationMarkCount.ForSentence(
                kind,
                expected.GetValueOrDefault(kind),
                found.GetValueOrDefault(kind))),
        ]);
    }

    public static IReadOnlyDictionary<DictationMarkKind, int> Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var counts = new Dictionary<DictationMarkKind, int>();

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '\n')
            {
                var run = 1;
                while (i + 1 < normalized.Length && normalized[i + 1] == '\n')
                {
                    run++;
                    i++;
                }

                Increment(counts, run >= 2 ? DictationMarkKind.ParagraphBreak : DictationMarkKind.LineBreak);
                continue;
            }

            var betweenDigits = i > 0 && i < normalized.Length - 1
                && char.IsDigit(normalized[i - 1]) && char.IsDigit(normalized[i + 1]);

            DictationMarkKind? kind = c switch
            {
                ',' when !betweenDigits => DictationMarkKind.Comma,
                '.' when !betweenDigits => DictationMarkKind.Period,
                ':' when !betweenDigits => DictationMarkKind.Colon,
                '?' => DictationMarkKind.QuestionMark,
                '!' => DictationMarkKind.ExclamationMark,
                _ => null,
            };

            if (kind is { } k)
            {
                Increment(counts, k);
            }
        }

        return counts;
    }

    private static void Increment(Dictionary<DictationMarkKind, int> counts, DictationMarkKind kind) =>
        counts[kind] = counts.GetValueOrDefault(kind) + 1;
}
