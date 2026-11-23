using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// W11, Produktregel vom 05.11.2026: Zeitfehler am Rand eines Ausschnitts kippen nie die ganze Erkennung.
/// Muster aus dem Dauerlauf RM07: Whisper drängt erfundene Wörter auf das Audioende, das Wort davor hat schon W8 durchlaufen.
/// </summary>
public sealed class UncertainEdgeTests
{
    private static WhisperTokenData T(string text, long a, long b) => new(text, a, b, .8f, false);

    private static Freihand.Core.Recognition.RecognitionHypothesis Build(string text, IEnumerable<WhisperTokenData> tokens, int samples) =>
        WhisperWordBuilder.Build([new WhisperSegmentData(text, [.. tokens])], TimeSpan.Zero, samples, WordBuilderOptions.Default);

    private static void AssertSound(Freihand.Core.Recognition.RecognitionHypothesis result, int count)
    {
        Assert.Equal(count, result.Words.Count);
        Assert.All(result.Words, word => Assert.True(word.Start < word.End, $"{word}"));
        Assert.All(result.Words, word => Assert.True(word.End <= result.AudioEnd, $"{word}"));
        for (var i = 1; i < count; i++)
        {
            Assert.True(result.Words[i].Start >= result.Words[i - 1].Start, $"Reihenfolge bei {i}");
        }
    }

    [Fact]
    public void W11_TailAtAudioEndAfterAWordThatAlreadySharedItsStart()
    {
        var result = Build(
            " vorher Das Feature ist ein sehr guter Punkt.",
            [
                T(" vorher", 1000, 1060), T(" Das", 1064, 1064), T(" Fe", 1064, 1064), T("ature", 1067, 1116),
                T(" ist", 1116, 1116), T(" ein", 1116, 1116), T(" sehr", 1116, 1116), T(" gut", 1116, 1116), T("er", 1116, 1116),
                T(" Punkt", 1116, 1116), T(".", 1116, 1116),
            ],
            178_560);

        AssertSound(result, 8);
        Assert.Equal(["vorher", "Das", "Feature", "ist", "ein", "sehr", "guter", "Punkt."], result.Words.Select(word => word.Text));
        Assert.Equal(result.AudioEnd, result.Words[^1].End);

        // Die Rede davor behält ihre Zeit, nur der Rand wird zusammengelegt
        Assert.Equal(TimeSpan.FromSeconds(10), result.Words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(10.64), result.Words[1].Start);
    }

    [Fact]
    public void W11_ZeroWordsInTheMiddleWithoutRoomDoNotKillTheHypothesis()
    {
        // Zwei Wörter ohne Dauer zwischen zwei Wörtern, die sich berühren: kein freier Platz, aber Text bleibt
        var result = Build(
            " eins zwei drei vier",
            [T(" eins", 0, 100), T(" zwei", 100, 100), T(" drei", 100, 100), T(" vier", 100, 150)],
            32_000);

        AssertSound(result, 4);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result.Words[^1].End);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void W11_WordsBeforeTheEdgeKeepTheirOrderForEveryPredecessorKind(int kind)
    {
        WhisperTokenData[] tokens = kind switch
        {
            0 => [T(" a", -1, -1), T(" b", 100, 100), T(" c", 100, 100)],
            1 => [T(" a", 50, 70), T(" b", 40, 100), T(" c", 100, 100), T(" d", 100, 100)],
            _ => [T(" a", 50, 50), T(" b", 50, 100), T(" c", 100, 100), T(" d", 100, 100)],
        };

        AssertSound(Build(string.Concat(tokens.Select(token => token.Text)), tokens, 16_000), tokens.Length);
    }

    [Fact]
    public void W11_RealOverlapStaysVisibleAndIsNotHidden()
    {
        var result = Build(
            " a b c d",
            [T(" a", 0, 100), T(" b", 50, 100), T(" c", 100, 100), T(" d", 100, 100)],
            16_000);

        AssertSound(result, 4);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Words[0].End);
        Assert.Equal(TimeSpan.FromSeconds(0.5), result.Words[1].Start);
    }

    [Fact]
    public void W11_DonorKeepsItsEndWhenItReallyOverlapsTheNextWord()
    {
        // Gegenfall Codex 05.11.2026: Das erste Wort überlappt das dritte echt; das Wort ohne Dauer dazwischen darf das nicht verdecken
        var result = Build(" a b c", [T(" a", 0, 100), T(" b", 50, 50), T(" c", 40, 80)], 16_000);

        Assert.Equal(TimeSpan.FromSeconds(1), result.Words[0].End);
        Assert.Equal(TimeSpan.FromSeconds(0.5), result.Words[2].Start);
        Assert.Equal(TimeSpan.FromSeconds(0.8), result.Words[2].End);
        Assert.True(result.Words[1].Start >= TimeSpan.Zero && result.Words[1].End <= result.Words[2].Start);
    }

    [Fact]
    public void W3_TextMismatchStillFails() => Assert.Throws<TokenAlignmentException>(() =>
        Build(" Hallo", [T(" Hello", 0, 40)], 16_000));

    [Fact]
    public void W11_NoRoomAtAllStillFails() => Assert.Throws<TokenAlignmentException>(() =>
        Build(string.Join(" ", Enumerable.Range(0, 626).Select(i => "w" + i)), Enumerable.Range(0, 626).Select(i => T(" w" + i, 1, 1)), 1));
}
