using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Entscheid 31.10.2026: Ende-Folge nur aus freiem Suffix oder unveränderten eigenen Tokenzeiten. Entscheid 05.11.2026 (W11):
/// Reicht das nicht, ordnet W11 die Wörter ein und das Wort davor gibt Dauer ab, auch wenn es W5, W7 oder W8 durchlaufen hat;
/// ein Wurf bleibt nur, wenn nicht einmal je Wort ein Tick Platz ist.
/// </summary>
public sealed class AudioEndBoundaryTests
{
    private static WhisperTokenData T(int i, long a, long b) => new(" w" + i, a, b, .8f, false);

    private static Freihand.Core.Recognition.RecognitionHypothesis Build(IEnumerable<WhisperTokenData> tokens, int samples)
    {
        var array = tokens.ToArray();
        return WhisperWordBuilder.Build([new WhisperSegmentData(string.Join(" ", array.Select(t => t.Text.Trim())), array)], TimeSpan.Zero, samples, WordBuilderOptions.Default);
    }

    [Theory]
    [InlineData(1, 16000)]
    [InlineData(3, 16000)]
    [InlineData(625, 1)]
    public void E01_NoPredecessorUsesFreeSuffix(int count, int samples)
    {
        var result = Build(Enumerable.Range(0, count).Select(i => T(i, 100, 100)), samples);
        Assert.Equal(count, result.Words.Count);
        Assert.Equal(result.AudioEnd, result.Words[^1].End);
        Assert.All(result.Words, w => Assert.True(w.Start < w.End));
        for (var i = 1; i < count; i++)
        {
            Assert.Equal(result.Words[i - 1].End, result.Words[i].Start);
        }

        if (samples == 1)
        {
            Assert.All(result.Words, w => Assert.Equal(1, (w.End - w.Start).Ticks));
        }
    }

    [Fact]
    public void E02_BorrowingOneTickKeepsPredecessorPositive()
    {
        var result = Build(new[] { T(0, 0, 1) }.Concat(Enumerable.Range(1, 624).Select(i => T(i, 1, 1))), 1);
        Assert.Equal(625, result.Words.Count);
        Assert.All(result.Words, w => Assert.Equal(1, (w.End - w.Start).Ticks));
    }

    /// <summary>W11: 626 Wörter in 625 Ticks haben nicht einmal je Wort einen Tick Platz, es gibt keinen Vorgänger zum Abgeben.</summary>
    [Fact]
    public void E03_TooLittleSpaceStillFails() => Assert.Throws<TokenAlignmentException>(() =>
        Build(Enumerable.Range(0, 626).Select(i => T(i, 1, 1)), 1));

    /// <summary>W11: Der Vorgänger (0, 625) hat nur 625 Ticks, für 625 Wörter und ihn selbst fehlt ein Tick.</summary>
    [Fact]
    public void E10_DonorOf625TicksCannotSupply625TailWords() => Assert.Throws<TokenAlignmentException>(() =>
        Build(new[] { T(0, 0, 1) }.Concat(Enumerable.Range(1, 625).Select(i => T(i, 1, 1))), 1));

    /// <summary>Prüft Reihenfolge und gültige Dauern aller Wörter und gibt die Zeiten als (Start, End) in Ticks zurück.</summary>
    private static (long Start, long End)[] AssertSound(Freihand.Core.Recognition.RecognitionHypothesis result, int count)
    {
        Assert.Equal(Enumerable.Range(0, count).Select(i => "w" + i), result.Words.Select(w => w.Text));
        Assert.All(result.Words, w => Assert.True(w.Start < w.End, $"{w}"));
        Assert.All(result.Words, w => Assert.True(w.Start >= result.AudioStart && w.End <= result.AudioEnd, $"{w}"));
        for (var i = 1; i < count; i++)
        {
            Assert.True(result.Words[i].Start >= result.Words[i - 1].Start, $"Reihenfolge bei {i}");
        }

        return result.Words.Select(w => (w.Start.Ticks, w.End.Ticks)).ToArray();
    }

    /// <summary>W11: Ein Vorgänger ohne eigene Tokenzeit (W5) gibt 20 ms ab; die zwei Randwörter erhalten je 10 ms am Audioende.</summary>
    [Fact]
    public void E04_W5PredecessorGivesUpDurationUnderW11()
    {
        var result = Build([T(0, -1, -1), T(1, 100, 100), T(2, 100, 100)], 16000);
        Assert.Equal([(0L, 9_800_000L), (9_800_000L, 9_900_000L), (9_900_000L, 10_000_000L)], AssertSound(result, 3));
    }

    /// <summary>W11: Ein von W7 angehobener Vorgänger gibt ab und behält seinen angehobenen Beginn; seine Überlappung mit dem Wort davor bleibt.</summary>
    [Fact]
    public void E05_W7RaisedPredecessorGivesUpDurationAndKeepsItsRaisedStart()
    {
        var result = Build([T(0, 50, 70), T(1, 40, 100), T(2, 100, 100), T(3, 100, 100)], 16000);
        Assert.Equal(
            [(5_000_000L, 7_000_000L), (5_000_000L, 9_800_000L), (9_800_000L, 9_900_000L), (9_900_000L, 10_000_000L)],
            AssertSound(result, 4));
    }

    /// <summary>W11: Ein von W8 und der Teilung des Folgeworts veränderter Vorgänger gibt ebenfalls ab.</summary>
    [Fact]
    public void E06_W8ChangedPredecessorGivesUpDurationUnderW11()
    {
        var result = Build([T(0, 50, 50), T(1, 50, 100), T(2, 100, 100), T(3, 100, 100)], 16000);
        Assert.Equal(
            [(5_000_000L, 5_100_000L), (5_100_000L, 9_800_000L), (9_800_000L, 9_900_000L), (9_900_000L, 10_000_000L)],
            AssertSound(result, 4));
    }

    /// <summary>W11: Die Abgabe verdeckt eine echte positive Überlappung nicht; „w0“ bleibt [0, 1 s), „w1“ beginnt unverändert bei 0,5 s.</summary>
    [Fact]
    public void E07_DonationMustNotHideRealPositiveOverlap()
    {
        var result = Build([T(0, 0, 100), T(1, 50, 100), T(2, 100, 100), T(3, 100, 100)], 16000);
        var times = AssertSound(result, 4);
        Assert.Equal(
            [(0L, 10_000_000L), (5_000_000L, 9_800_000L), (9_800_000L, 9_900_000L), (9_900_000L, 10_000_000L)], times);
        Assert.True(result.Words[0].End > result.Words[1].Start, "echte Überlappung muss sichtbar bleiben");
        Assert.Equal(5_000_000, result.Words[1].Start.Ticks);
    }

    [Fact]
    public void E08_MixedOriginalZeroPointsKeepNormalDistribution()
    {
        var result = Build([T(0, 99, 99), T(1, 100, 100), T(2, 100, 100)], 16000);
        Assert.Equal(9_900_000, result.Words[0].Start.Ticks);
        Assert.Equal(9_933_333, result.Words[0].End.Ticks);
        Assert.Equal(10_000_000, result.Words[^1].End.Ticks);
    }

    [Fact]
    public void E09_ClampedEndOutsideRasterIsSharedExactly()
    {
        var result = Build([T(0, 199, 200), T(1, 200, 210)], 31920);
        Assert.Equal(19_900_000, result.Words[0].Start.Ticks);
        Assert.Equal(19_925_000, result.Words[0].End.Ticks);
        Assert.Equal(19_925_000, result.Words[1].Start.Ticks);
        Assert.Equal(19_950_000, result.Words[1].End.Ticks);
    }
}
