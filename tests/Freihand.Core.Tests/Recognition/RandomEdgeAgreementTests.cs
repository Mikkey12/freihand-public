using Freihand.Adapters.Recognition;
using static Freihand.Core.Tests.Recognition.WordBuilderCases;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// W11: Produkt und unabhängige Nachrechnung (<see cref="WordBuilderReference"/>) stimmen auch bei zufälligen, dichten Randfällen
/// überein, in Ausnahme und Ergebnis. Fester Startwert, damit jeder Lauf dieselben Fälle prüft.
/// </summary>
public sealed class RandomEdgeAgreementTests
{
    /// <summary>
    /// Befund Codex 05.11.2026: Zwei Wörter mit eigener positiver Zeit, die sich nach W7 und W9 noch überlappen, überlappen sich
    /// auch im Ergebnis. Geprüft auf dichten Folgen mit Wörtern ohne Dauer dazwischen.
    /// </summary>
    [Fact]
    public void W11_NeverHidesARealOverlapBetweenWordsWithOwnTimes()
    {
        var random = new Random(20261106);
        var checkedPairs = 0;
        for (var run = 0; run < 60_000; run++)
        {
            var count = random.Next(3, 7);
            var raw = Enumerable.Range(0, count).Select(i =>
            {
                var a = random.Next(0, 90);
                return (Text: " w" + i, Start: (long)a, End: random.Next(3) == 0 ? (long)a : a + random.Next(1, 40));
            }).ToArray();
            var tokens = raw.Select(word => new WhisperTokenData(word.Text, word.Start, word.End, .8f, false)).ToArray();
            var segment = new WhisperSegmentData(string.Join(" ", tokens.Select(token => token.Text.Trim())), tokens);
            IReadOnlyList<Freihand.Core.Recognition.RecognizedWord> words;
            try
            {
                words = WhisperWordBuilder.Build([segment], TimeSpan.Zero, 16_000, Options).Words;
            }
            catch (TokenAlignmentException)
            {
                continue;
            }

            // W7 hebt den Beginn auf den des Vorgängers; mehr als die Toleranz muss übrig bleiben, damit W9 nicht kürzt
            var lifted = 0L;
            for (var i = 0; i < count; i++)
            {
                lifted = Math.Max(lifted, raw[i].Start);
                for (var j = i + 1; j < count; j++)
                {
                    var laterStart = Enumerable.Range(0, j + 1).Max(k => raw[k].Start);
                    if (raw[i].End > raw[i].Start && raw[j].End > raw[j].Start && raw[j].End > laterStart
                        && raw[i].End - laterStart > 20 && lifted < laterStart)
                    {
                        checkedPairs++;
                        Assert.True(words[i].End > words[j].Start, $"Lauf {run}: {string.Join(' ', raw)} -> {WordBuilderReference.Format(words)}");
                    }
                }
            }
        }

        Assert.True(checkedPairs > 1000, $"nur {checkedPairs} Paare");
    }

    [Fact]
    public void W11_ProductAndReferenceAgreeOnSeededRandomEdgeCases()
    {
        var random = new Random(20261105);
        int[] sampleCounts = [0, 1, 40, 160, 800, 1_600, 1_920, 3_200];
        var differences = new List<string>();
        var hypotheses = 0;
        var failures = 0;

        for (var run = 0; run < 60_000; run++)
        {
            var samples = sampleCounts[random.Next(sampleCounts.Length)];
            var limit = (samples / 160) + 3;
            var count = random.Next(1, 8);
            var tokens = Enumerable.Range(0, count).Select(i =>
            {
                var a = random.Next(-2, limit);
                var b = random.Next(4) == 0 ? a : random.Next(-2, limit);
                return new WhisperTokenData(" w" + i, a, b, .8f, false);
            }).ToArray();
            var segment = new WhisperSegmentData(string.Join(" ", tokens.Select(token => token.Text.Trim())), tokens);

            var expected = WordBuilderReference.Build([segment], Offset, samples, Options.NeighborOverlapTolerance);
            string? actualText;
            try
            {
                actualText = WordBuilderReference.Format(WhisperWordBuilder.Build([segment], Offset, samples, Options).Words);
            }
            catch (TokenAlignmentException)
            {
                actualText = null;
            }

            var expectedText = expected.Failure is null ? WordBuilderReference.Format(expected.Hypothesis!.Words) : null;
            if (expectedText is null)
            {
                failures++;
            }
            else
            {
                hypotheses++;
            }

            if (actualText != expectedText)
            {
                differences.Add($"{samples} Samples, {string.Join(" ", tokens.Select(t => $"({t.Start},{t.End})"))}: Produkt {actualText ?? "Ausnahme"}, Nachrechnung {expectedText ?? "Ausnahme"}");
            }
        }

        Assert.True(differences.Count == 0, $"{differences.Count} Abweichungen, die ersten:\n{string.Join("\n", differences.Take(8))}");
        Assert.True(hypotheses > 1_000 && failures > 10, $"Zufallsfälle zu einseitig: {hypotheses} Hypothesen, {failures} Ausnahmen");
    }
}
