using Freihand.Core.Recognition;

namespace Freihand.Core.Live;

/// <summary>
/// Prüfung einer Hypothese nach Vertrag 2, in dieser Reihenfolge: Grundregeln, zeitliche Wiederholungen entfernen
/// (Kapitel 5.5), keine Überlappung. Verstösse gegen die Grundregeln oder Überlappungen sind Decoderfehler.
/// </summary>
internal static class HypothesisCheck
{
    /// <summary>Kürzeste Folge, die als Wiederholung zählt (Kapitel 5.5).</summary>
    private const int MinRepeat = 5;

    /// <summary>Die geprüften Wörter einer Hypothese über das Audio [<paramref name="audioStart"/>, <paramref name="audioEnd"/>).</summary>
    /// <exception cref="InvalidHypothesisException">Grundregel verletzt oder Wörter überlappen.</exception>
    public static IReadOnlyList<RecognizedWord> Accept(RecognitionHypothesis? hypothesis, TimeSpan audioStart, TimeSpan audioEnd)
    {
        if (hypothesis?.Words is null)
        {
            throw new InvalidHypothesisException("Der Decoder lieferte keine Hypothese.");
        }

        if (hypothesis.AudioStart != audioStart || hypothesis.AudioEnd != audioEnd)
        {
            throw new InvalidHypothesisException(
                $"Hypothese über [{hypothesis.AudioStart}, {hypothesis.AudioEnd}) statt [{audioStart}, {audioEnd}).");
        }

        RecognizedWord? previous = null;
        foreach (var word in hypothesis.Words)
        {
            if (word is null || string.IsNullOrEmpty(word.Text)
                || char.IsWhiteSpace(word.Text[0]) || char.IsWhiteSpace(word.Text[^1]))
            {
                throw new InvalidHypothesisException("Ein Wort ist leer oder hat Leerraum am Rand.");
            }

            if (word.Start >= word.End || word.Start < audioStart || word.End > audioEnd)
            {
                throw new InvalidHypothesisException($"Wort [{word.Start}, {word.End}) liegt nicht gültig im Audio.");
            }

            if (previous is not null && word.Start < previous.Start)
            {
                throw new InvalidHypothesisException("Die Wörter stehen nicht nach ihrem Beginn.");
            }

            previous = word;
        }

        var kept = RemoveRepeats(hypothesis.Words);
        for (var i = 1; i < kept.Count; i++)
        {
            if (kept[i].Start < kept[i - 1].End)
            {
                throw new InvalidHypothesisException($"Wörter überlappen bei {kept[i].Start}.");
            }
        }

        return kept;
    }

    /// <summary>
    /// Wiederholungssperre (Kapitel 5.5): Folgt auf eine Folge von mindestens fünf Einheiten unmittelbar dieselbe Folge
    /// und überlappen sich ihre Zeitspannen, entfällt die zweite. Längste Folge zuerst, bei gleicher Länge von vorn; nach
    /// jedem Entfernen beginnt die Suche neu.
    /// </summary>
    public static IReadOnlyList<RecognizedWord> RemoveRepeats(IReadOnlyList<RecognizedWord> words)
    {
        var units = WordComparison.Units(words);
        while (RemoveFirstRepeat(units))
        {
            // jede Entfernung verkürzt die Liste, die Suche endet also
        }

        return [.. units.SelectMany(unit => unit.Words)];
    }

    private static bool RemoveFirstRepeat(List<WordUnit> units)
    {
        for (var length = units.Count / 2; length >= MinRepeat; length--)
        {
            for (var i = 0; i + (2 * length) <= units.Count; i++)
            {
                if (IsRepeat(units, i, length))
                {
                    units.RemoveRange(i + length, length);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsRepeat(List<WordUnit> units, int first, int length)
    {
        var firstEnd = TimeSpan.MinValue;
        for (var k = 0; k < length; k++)
        {
            if (units[first + k].Key != units[first + length + k].Key)
            {
                return false;
            }

            firstEnd = units[first + k].End > firstEnd ? units[first + k].End : firstEnd;
        }

        // Dasselbe Audio ein zweites Mal erkannt, wenn die zweite Folge vor dem Ende der ersten beginnt
        return units[first + length].First.Start < firstEnd;
    }
}
