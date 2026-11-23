namespace Freihand.Core.Live;

/// <summary>Ein gelerntes Wort: die richtige Schreibweise und was Freihand dafür gehört hat (P11 S4).</summary>
public sealed record LearnedWord(string Spelling, IReadOnlyList<string> Heard);

/// <summary>
/// Ersetzungen nach der Erkennung (P11 S4, Entscheid vom 18.11.2026: nur Ersetzungen, keine Hinweise an Whisper, weil
/// Whisper Hinweiswörter in Geräuschen erfindet). Regel: Eine gehörte Fehlschreibung wird nur als ganze Wortfolge
/// ersetzt, verglichen wie Befehlswendungen (Kleinschreibung, ohne Satzzeichen am Rand); die längste passende Folge gilt.
/// Satzzeichen am Rand bleiben stehen. Steht dieselbe Fehlschreibung bei zwei Wörtern, gilt die erste.
/// </summary>
public sealed class WordReplacements
{
    private readonly Dictionary<string, List<(string[] Keys, string Spelling)>> _byFirstKey;

    private WordReplacements(Dictionary<string, List<(string[] Keys, string Spelling)>> byFirstKey, int count)
    {
        _byFirstKey = byFirstKey;
        Count = count;
    }

    public static WordReplacements Empty { get; } = new([], 0);

    /// <summary>Anzahl wirksamer Ersetzungen.</summary>
    public int Count { get; }

    public static WordReplacements From(IEnumerable<LearnedWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var byFirstKey = new Dictionary<string, List<(string[] Keys, string Spelling)>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var word in words)
        {
            // Nur wenn das Gehörte genau die Schreibweise ist, gibt es nichts zu ersetzen; „Iphone“ → „iPhone“ ist eine
            // Ersetzung (eigene Vorprüfung zu PR #66)
            var own = WordComparison.Plain(word.Spelling);
            foreach (var heard in word.Heard)
            {
                var keys = WordComparison.PhraseKeys(heard).Where(key => key.Length > 0).ToArray();
                var joined = string.Join(' ', keys);
                if (keys.Length == 0 || WordComparison.Plain(heard) == own || !seen.Add(joined))
                {
                    continue;
                }

                if (!byFirstKey.TryGetValue(keys[0], out var list))
                {
                    byFirstKey[keys[0]] = list = [];
                }

                list.Add((keys, word.Spelling));
                count++;
            }
        }

        foreach (var list in byFirstKey.Values)
        {
            list.Sort((a, b) => b.Keys.Length.CompareTo(a.Keys.Length));
        }

        return new WordReplacements(byFirstKey, count);
    }

    /// <summary>
    /// Regel für Ersetzen und Lernen gleichermassen (eigene Vorprüfung und Codex zu PR #66): Eine Wortfolge passt nur ohne
    /// Satzzeichen innen; Randzeichen gibt es nur vor dem ersten und nach dem letzten Wort. Sonst würde „Mass. Man“ über
    /// ein Satzende hinweg ersetzt, oder es würde „Signal, Rot“ als „Signal Rot“ gelernt, was beim Diktat nie passt.
    /// </summary>
    internal static bool WithoutInnerMarks(IReadOnlyList<WordUnit> units, int start, int length)
    {
        for (var k = start; k < start + length; k++)
        {
            var text = string.Concat(units[k].Words.Select(word => word.Text));
            // Am Anfang eines inneren Worts gehört nur ein Punkt zum Wort („.NET“); jedes andere Zeichen trennt
            if ((k > start && WordComparison.LeadingMarks(text).Any(mark => mark != '.'))
                || (k < start + length - 1 && WordComparison.TrailingMarks(text).Length > 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Wird <paramref name="heard"/> tatsächlich durch <paramref name="spelling"/> ersetzt? Dieselbe Regel wie beim Diktat
    /// (für die Anzeige der wirksamen gelernten Wörter, Codex zu PR #66).
    /// </summary>
    public bool Applies(string spelling, string heard)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        ArgumentNullException.ThrowIfNull(heard);
        var keys = WordComparison.PhraseKeys(heard).Where(key => key.Length > 0).ToArray();
        return keys.Length > 0 && _byFirstKey.TryGetValue(keys[0], out var candidates)
            && candidates.Any(candidate => candidate.Spelling == spelling && candidate.Keys.SequenceEqual(keys, StringComparer.Ordinal));
    }

    /// <summary>Schreibweise, durch die <paramref name="heard"/> ersetzt wird, sonst null.</summary>
    public string? SpellingFor(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var keys = WordComparison.PhraseKeys(heard).Where(key => key.Length > 0).ToArray();
        return keys.Length > 0 && _byFirstKey.TryGetValue(keys[0], out var candidates)
            ? candidates.FirstOrDefault(candidate => candidate.Keys.SequenceEqual(keys, StringComparer.Ordinal)).Spelling
            : null;
    }

    /// <summary>
    /// Längste zulässige Ersetzung, die bei <paramref name="index"/> beginnt (Codex zu PR #66): Eine längere Folge, die
    /// <paramref name="allowed"/> ablehnt, verdeckt keine kürzere zulässige.
    /// </summary>
    /// <param name="allowed">Darf eine Folge dieser Länge ab <paramref name="index"/> ersetzt werden?</param>
    internal (int Length, string Spelling)? At(IReadOnlyList<string> keys, int index, Func<int, bool> allowed)
    {
        if (!_byFirstKey.TryGetValue(keys[index], out var candidates))
        {
            return null;
        }

        foreach (var (candidate, spelling) in candidates)
        {
            if (index + candidate.Length > keys.Count)
            {
                continue;
            }

            var match = true;
            for (var k = 1; k < candidate.Length && match; k++)
            {
                match = string.Equals(keys[index + k], candidate[k], StringComparison.Ordinal);
            }

            if (match && allowed(candidate.Length))
            {
                return (candidate.Length, spelling);
            }
        }

        return null;
    }
}
