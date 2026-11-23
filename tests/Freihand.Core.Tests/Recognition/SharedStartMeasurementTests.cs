using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.Recognition;

/// <summary>RM01: freigegebene W8-Erweiterung für Nullwörter am gemeinsamen Beginn eines echten Folgeworts.</summary>
public sealed class SharedStartMeasurementTests
{
    private static WhisperTokenData Token(string text, long start, long end) => new(" " + text, start, end, .8f, false);
    private static Freihand.Core.Recognition.RecognitionHypothesis Build(IReadOnlyList<WhisperTokenData> tokens, int samples = 16000) =>
        WhisperWordBuilder.Build([new WhisperSegmentData(string.Join(" ", tokens.Select(t => t.Text.Trim())), tokens)],
            TimeSpan.Zero, samples, WordBuilderOptions.Default);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void S01_OneOrSeveralZeroWordsShareTheFollowingInterval(int count)
    {
        var tokens = Enumerable.Range(0, count).Select(i => Token("w" + i, 20, 20)).Append(Token("next", 20, 50)).ToArray();
        var result = Build(tokens);
        Assert.Equal(count + 1, result.Words.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(2_000_000 + i * 100_000, result.Words[i].Start.Ticks);
            Assert.Equal(result.Words[i].End, result.Words[i + 1].Start);
            Assert.Equal("w" + i, result.Words[i].Text);
            Assert.Equal(.8f, result.Words[i].Probability);
        }
        Assert.Equal(5_000_000, result.Words[^1].End.Ticks);
    }

    [Theory]
    [InlineData(1, 312)]
    [InlineData(2, 208)]
    [InlineData(624, 1)]
    public void S02_SubTenMillisecondsAndOneTickRemainPositive(int count, long duration)
    {
        var tokens = Enumerable.Range(0, count).Select(i => Token("w" + i, 0, 0)).Append(Token("next", 0, 1)).ToArray();
        var result = Build(tokens, 1); // Audioende 625 Ticks, ausserhalb des 10-ms-Rasters.
        Assert.Equal(count + 1, result.Words.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(duration, (result.Words[i].End - result.Words[i].Start).Ticks);
            Assert.Equal(result.Words[i].End, result.Words[i + 1].Start);
        }
        Assert.Equal(625, result.Words[^1].End.Ticks);
        Assert.True(result.Words[^1].Start < result.Words[^1].End);
    }

    [Fact]
    public void S03_TooLittleSpaceStillFails()
    {
        var tokens = Enumerable.Range(0, 625).Select(i => Token("w" + i, 0, 0)).Append(Token("next", 0, 1)).ToArray();
        Assert.Throws<TokenAlignmentException>(() => Build(tokens, 1));
    }

    /// <summary>
    /// W8 teilt kein belegtes Wort davor (Vorgänger endet nach dem gemeinsamen Beginn). Seit W11 wirft das nicht mehr:
    /// „zero“ erhält 10 ms, „prev“ gibt sie ab und endet bei 0,04 s; „next“ bleibt unverändert.
    /// </summary>
    [Fact]
    public void S04_PreviousRealWordGivesUpDurationUnderW11()
    {
        var result = Build([Token("prev", 0, 10), Token("zero", 5, 5), Token("next", 5, 20)]);
        Assert.Equal(["prev", "zero", "next"], result.Words.Select(w => w.Text));
        Assert.Equal([(0L, 400_000L), (400_000L, 500_000L), (500_000L, 2_000_000L)],
            result.Words.Select(w => (w.Start.Ticks, w.End.Ticks)));
        Assert.All(result.Words, w => Assert.True(w.Start < w.End));
    }

    /// <summary>
    /// Verschiedene Nullpunkte teilen das Folgewort nicht (W8). Seit W11 wirft das nicht mehr: „other“ liegt vor „next“ und
    /// teilt sich mit „zero“ den Platz vor dessen Beginn (hier 0,01 s, je 5 ms); „next“ bleibt bei 0,01 s.
    /// </summary>
    [Fact]
    public void S05_DifferentZeroStartsAreSplitBeforeTheNextWordUnderW11()
    {
        var result = Build([Token("zero", 0, 0), Token("other", 1, 1), Token("next", 0, 5)]);
        Assert.Equal(["zero", "other", "next"], result.Words.Select(w => w.Text));
        Assert.Equal([(0L, 50_000L), (50_000L, 100_000L), (100_000L, 500_000L)],
            result.Words.Select(w => (w.Start.Ticks, w.End.Ticks)));
        Assert.All(result.Words, w => Assert.True(w.Start < w.End));
    }

    [Fact]
    public void S06_NoFollowingWordUsesFreeAudioEndSuffix()
    {
        // Entscheid 31.10.2026: ohne Vorgänger wird der freie Suffix genutzt, statt eine Ausnahme zu werfen.
        var result = Build([Token("zero", 100, 100), Token("other", 100, 100)]);
        Assert.Equal(9_800_000, result.Words[0].Start.Ticks);
        Assert.Equal(9_900_000, result.Words[0].End.Ticks);
        Assert.Equal(9_900_000, result.Words[1].Start.Ticks);
        Assert.Equal(10_000_000, result.Words[1].End.Ticks);
    }

    [Fact]
    public void S07_NormalDistributionKeepsFollowingWordStart()
    {
        var result = Build([Token("zero", 20, 20), Token("next", 30, 50)]);
        Assert.Equal(2_000_000, result.Words[0].Start.Ticks);
        Assert.Equal(2_100_000, result.Words[0].End.Ticks);
        Assert.Equal(3_000_000, result.Words[1].Start.Ticks);
    }

    [Fact]
    public void S08_PositiveTimesAndRealOverlapsRemainUnchanged()
    {
        var result = Build([Token("first", 10, 50), Token("second", 10, 50)]);
        Assert.All(result.Words, word => { Assert.Equal(1_000_000, word.Start.Ticks); Assert.Equal(5_000_000, word.End.Ticks); });
    }

    [Fact]
    public void RM01_ZeroDurationWordSharesFollowingIntervalWithoutLosingWords()
    {
        var result = WhisperWordBuilder.Build(
            [new WhisperSegmentData(" eins zwei", [
                new WhisperTokenData(" eins", 198, 198, .9f, false),
                new WhisperTokenData(" zwei", 198, 215, .8f, false)])],
            TimeSpan.FromSeconds(11.46), 160000, WordBuilderOptions.Default);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal("eins", result.Words[0].Text);
        Assert.Equal("zwei", result.Words[1].Text);
        Assert.Equal(TimeSpan.FromTicks(134400000), result.Words[0].Start);
        Assert.Equal(TimeSpan.FromTicks(134500000), result.Words[0].End);
        Assert.Equal(result.Words[0].End, result.Words[1].Start);
        Assert.Equal(TimeSpan.FromTicks(136100000), result.Words[1].End);
        Assert.Equal(.9f, result.Words[0].Probability);
        Assert.Equal(.8f, result.Words[1].Probability);
    }
}
