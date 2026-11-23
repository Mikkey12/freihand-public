using Freihand.Core.Recognition;

namespace Freihand.Core.Live;

/// <summary>
/// Schutz vor erfundenem Text (ER-16, Kapitel 5.5): Sprachbereich, Geräuschmarken und Floskeln. Gefiltert wird je
/// Einheit aus Wort und getrennt geliefertem Satzzeichen; ein Satzzeichen entfällt mit seinem Wort.
/// </summary>
internal sealed class HallucinationGuard
{
    private readonly List<string[]> _phrases;
    private readonly float _maxPhraseProbability;
    private readonly TimeSpan _pauseThreshold;

    public HallucinationGuard(HallucinationFilter filter, TimeSpan pauseThreshold)
    {
        _pauseThreshold = pauseThreshold;
        _phrases = [.. filter.KnownPhrases
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .Select(WordComparison.PhraseKeys)
            .Where(keys => keys.Length > 0)];
        _maxPhraseProbability = filter.MaxPhraseProbability;
    }

    /// <summary>
    /// Wörter, die vorläufig wie fest bleiben dürfen: mindestens zur Hälfte in Sprache und nicht Teil einer Geräuschmarke. Eine
    /// Lücke unter der Pausenschwelle zwischen zwei Sprachbereichen beendet keine Äusserung und zählt darum als Sprache
    /// (T77): Whisper legt das erste Wort nach einer kurzen Pause oft in die Pause (Konzept unsichere Ränder, Regel 1).
    /// </summary>
    public List<RecognizedWord> InSpeech(IReadOnlyList<RecognizedWord> words, IReadOnlyList<SpeechRegion> speech)
    {
        var regions = SpeechRegions.BridgeShortGaps(speech, _pauseThreshold);
        var units = WordComparison.Units(words);
        var marks = NoiseMarks(units);
        return [.. units
            .Where((unit, i) => !marks[i] && SpeechRegions.HalfInSpeech(regions, unit.First))
            .SelectMany(unit => unit.Words)];
    }

    /// <summary>
    /// Geräuschmarken, auch über mehrere Wörter (T07, T87): eine Einheit nach <see cref="IsNoiseMark"/>, oder alle Einheiten
    /// von einem Wort, das mit „[“ bzw. „*“ beginnt, bis zum nächsten, das mit „]“ bzw. „*“ endet. Ohne Abschluss ist es
    /// keine Marke. Satzzeichen der Randliste zählen nicht.
    /// </summary>
    private static bool[] NoiseMarks(IReadOnlyList<WordUnit> units)
    {
        var marks = new bool[units.Count];
        for (var i = 0; i < units.Count; i++)
        {
            if (IsNoiseMark(units[i].First.Text))
            {
                marks[i] = true;
                continue;
            }

            var core = WordComparison.TrimMarks(units[i].First.Text);
            if (core.Length == 0 || core[0] is not ('[' or '*'))
            {
                continue;
            }

            var closer = core[0] == '[' ? ']' : '*';
            for (var j = i + 1; j < units.Count; j++)
            {
                var end = WordComparison.TrimMarks(units[j].First.Text);
                if (end.Length > 0 && end[^1] == closer)
                {
                    Array.Fill(marks, true, i, j - i + 1);
                    i = j;
                    break;
                }
            }
        }

        return marks;
    }

    /// <summary>
    /// Die Wörter einer abgeschlossenen Äusserung: wie <see cref="InSpeech"/>, dazu entfällt eine bekannte Floskel, wenn
    /// sie die ganze Äusserung bildet und ihre mittlere Wahrscheinlichkeit unter der Schwelle liegt (T07, T26).
    /// </summary>
    public List<RecognizedWord> Final(IReadOnlyList<RecognizedWord> words, IReadOnlyList<SpeechRegion> speech)
    {
        var kept = InSpeech(words, speech);
        if (kept.Count == 0 || _phrases.Count == 0)
        {
            return kept;
        }

        var keys = WordComparison.Units(kept).Select(unit => unit.Key).ToArray();
        var isPhrase = _phrases.Any(phrase => phrase.SequenceEqual(keys, StringComparer.Ordinal));
        return isPhrase && kept.Average(word => word.Probability) < _maxPhraseProbability ? [] : kept;
    }

    /// <summary>
    /// Geräuschmarke: ganz in eckigen Klammern, zwischen Sternchen oder nur aus Musiknoten (z. B. „[Musik]“, „*Applaus*“).
    /// Satzzeichen der Randliste zählen dabei nicht.
    /// </summary>
    public static bool IsNoiseMark(string text)
    {
        var core = WordComparison.TrimMarks(text);
        if (core.Length >= 2 && ((core[0] == '[' && core[^1] == ']') || (core[0] == '*' && core[^1] == '*')))
        {
            return true;
        }

        return core.Length > 0 && core.EnumerateRunes().All(rune => IsMusicNote(rune.Value));
    }

    // ♩ ♪ ♫ ♬ sowie 🎵 🎶 🎼
    private static bool IsMusicNote(int codePoint) =>
        codePoint is (>= 0x2669 and <= 0x266C) or 0x1F3B5 or 0x1F3B6 or 0x1F3BC;
}
