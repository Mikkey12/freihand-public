
using Freihand.Adapters.Recognition;
using Xunit;

namespace Freihand.Core.Tests.Recognition;

public sealed class AudioEndMeasurementTests
{
    [Theory]
    [InlineData(95500000L, 948L, 996L, 195100000L, 80000L)]
    [InlineData(21800000L, 973L, 1000L, 121300000L, 100000L)]
    public void RM01_TailWordsRemainOrderedAndKeepEveryWord(
        long offset, long previousStart, long previousEnd, long tailStart, long duration)
    {
        var tokens = new[] { new WhisperTokenData(" vorher", previousStart, previousEnd, .9f, false) }
            .Concat(Enumerable.Range(0, 5).Select(i =>
                new WhisperTokenData(" wort" + i, 1000, 1000, .8f, false))).ToArray();
        var result = WhisperWordBuilder.Build(
            [new WhisperSegmentData("vorher wort0 wort1 wort2 wort3 wort4", tokens)],
            TimeSpan.FromTicks(offset), 160000, WordBuilderOptions.Default);
        Assert.Equal(6, result.Words.Count);
        Assert.Equal(offset + previousStart * 100000, result.Words[0].Start.Ticks);
        Assert.Equal(tailStart, result.Words[0].End.Ticks);
        Assert.Equal("vorher", result.Words[0].Text);
        Assert.Equal(.9f, result.Words[0].Probability);
        for (var i = 0; i < 5; i++)
        {
            var word = result.Words[i + 1];
            Assert.Equal("wort" + i, word.Text);
            Assert.Equal(.8f, word.Probability);
            Assert.Equal(tailStart + i * duration, word.Start.Ticks);
            Assert.Equal(tailStart + (i + 1) * duration, word.End.Ticks);
        }
        Assert.Equal(result.AudioEnd, result.Words[^1].End);
    }
}
