namespace Freihand.Core.Quality;

/// <summary>
/// Ergebnis der Wortfehlerrate: Ersetzungen, Auslassungen und Einfügungen gegenüber der Referenz.
/// </summary>
public sealed record WordErrorCounts(int ReferenceWords, int Substitutions, int Deletions, int Insertions)
{
    public int Errors => Substitutions + Deletions + Insertions;

    /// <summary>
    /// (S + D + I) / N. Bei leerer Referenz: 0 ohne Einfügungen, sonst 1, damit der Wert endlich bleibt.
    /// </summary>
    public double Rate => ReferenceWords > 0 ? (double)Errors / ReferenceWords : Insertions == 0 ? 0 : 1;

    public static WordErrorCounts Zero { get; } = new(0, 0, 0, 0);

    public static WordErrorCounts operator +(WordErrorCounts left, WordErrorCounts right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return new WordErrorCounts(
            left.ReferenceWords + right.ReferenceWords,
            left.Substitutions + right.Substitutions,
            left.Deletions + right.Deletions,
            left.Insertions + right.Insertions);
    }

    public static WordErrorCounts Add(WordErrorCounts left, WordErrorCounts right) => left + right;
}

/// <summary>
/// Wortfehlerrate im Referenzsatz (QM-02, QM-06): Levenshtein-Distanz auf Wörtern nach
/// <see cref="TextNormalizer"/>. Bei gleich langen Wegen zählt die Rückverfolgung zuerst Treffer und
/// Ersetzungen, dann Auslassungen, dann Einfügungen; so sind die Anteile eindeutig. Über mehrere Sätze
/// wird summiert, nicht gemittelt: Fehler aller Sätze geteilt durch Referenzwörter aller Sätze.
/// </summary>
public static class WordErrorRate
{
    public static WordErrorCounts Compute(string reference, string hypothesis) =>
        Compute(TextNormalizer.Tokenize(reference), TextNormalizer.Tokenize(hypothesis));

    /// <summary>Für bereits normalisierte Wörter; verglichen wird ordinal.</summary>
    public static WordErrorCounts Compute(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);

        var n = reference.Count;
        var m = hypothesis.Count;
        var distance = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++)
        {
            distance[i, 0] = i;
        }

        for (var j = 0; j <= m; j++)
        {
            distance[0, j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var substitution = distance[i - 1, j - 1] + (Same(reference[i - 1], hypothesis[j - 1]) ? 0 : 1);
                var deletion = distance[i - 1, j] + 1;
                var insertion = distance[i, j - 1] + 1;
                distance[i, j] = Math.Min(substitution, Math.Min(deletion, insertion));
            }
        }

        int substitutions = 0, deletions = 0, insertions = 0;
        var (row, column) = (n, m);
        while (row > 0 || column > 0)
        {
            if (row > 0 && column > 0)
            {
                var same = Same(reference[row - 1], hypothesis[column - 1]);
                if (distance[row, column] == distance[row - 1, column - 1] + (same ? 0 : 1))
                {
                    substitutions += same ? 0 : 1;
                    (row, column) = (row - 1, column - 1);
                    continue;
                }
            }

            if (row > 0 && distance[row, column] == distance[row - 1, column] + 1)
            {
                deletions++;
                row--;
            }
            else
            {
                insertions++;
                column--;
            }
        }

        return new WordErrorCounts(n, substitutions, deletions, insertions);
    }

    private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.Ordinal);
}
