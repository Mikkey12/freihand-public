using Freihand.Recognizer;

namespace Freihand.Windows.Tests.Recognition;

/// <summary>
/// Sondertokens an der Textform (P8-Konzept, Kapitel 5.5 und 10.6): Messbefund RM01, Whisper.net 1.9.1 liefert Zeitstempel
/// als <c>[_TT_263]</c>; die frühere Form <c>[_…_]</c> liess sie als Wortteil stehen, und W3 verwarf jedes Segment.
/// </summary>
public sealed class SpecialTokenTests
{
    [Theory]
    [InlineData("[_BEG_]")]
    [InlineData("[_TT_263]")]
    [InlineData("[_TT_1500]")]
    [InlineData("[_SOT_]")]
    [InlineData("<|de|>")]
    [InlineData("<|endoftext|>")]
    public void SpecialTokensAreRecognized(string text) =>
        Assert.True(WhisperNetLoader.WhisperNetBackend.IsSpecial(text));

    [Theory]
    [InlineData(" Hallo")]
    [InlineData(".")]
    [InlineData("[")]
    [InlineData("[_")]
    [InlineData(" [Musik]")]
    [InlineData("<|")]
    [InlineData(null)]
    public void WordsAreNotSpecial(string? text) =>
        Assert.False(WhisperNetLoader.WhisperNetBackend.IsSpecial(text));
}
