using Freihand.Core.Recognition;

namespace Freihand.Benchmark.Tests;

public sealed class DirectMetricsTests
{
    private static readonly RecognitionOptions Options = new("de-CH", []);

    [Fact]
    public async Task RM02SelectedOriginalSamplesReachAllThreeDecodesUnchanged()
    {
        var original = Enumerable.Range(0, 528000).Select(i => i / 528000f).ToArray();
        var selected = DirectMetrics.SelectOffsetAudio(original, Arguments.Parse(
            ["direct-metrics", "--start-sample", "16001", "--end-sample", "496001"]));
        Assert.Equal(16001, selected.Start);
        Assert.Equal(496001, selected.End);
        Assert.Equal(original[16001..496001], selected.Audio.ToArray());
        var count = 0;
        await DirectMetrics.OffsetAsync(new Decoder((samples, offset) =>
        {
            Assert.Equal(original[16001..496001], samples.ToArray());
            count++;
            return Hypothesis(offset, samples.Length);
        }), selected.Audio, Options);
        Assert.Equal(3, count);
    }

    [Fact]
    public void RM02DefaultSelectsWholeOriginal()
    {
        var selected = DirectMetrics.SelectOffsetAudio(new float[20], Arguments.Parse(["direct-metrics"]));
        Assert.Equal(0, selected.Start);
        Assert.Equal(20, selected.End);
        Assert.Equal(20, selected.Audio.Length);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("10", "5")]
    [InlineData("-1", "10")]
    [InlineData("0", "21")]
    [InlineData("0.5", "10")]
    [InlineData("0", "1.5")]
    [InlineData("", "10")]
    [InlineData("0", "2147483648")]
    [InlineData("NaN", "10")]
    public void RM02RejectsInvalidSampleBounds(string start, string end) =>
        Assert.Throws<ArgumentException>(() => DirectMetrics.SelectOffsetAudio(new float[20],
            Arguments.Parse(["direct-metrics", "--start-sample", start, "--end-sample", end])));

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[[0,10],null]")]
    [InlineData("[]")]
    [InlineData("[[0]]")]
    [InlineData("[[-1,10]]")]
    [InlineData("[[0,21]]")]
    public void RM03RejectsMissingOrInvalidSentenceMarkers(string json) =>
        Assert.Throws<ArgumentException>(() => DirectMetrics.ParseIntervals(json, 20));

    [Fact]
    public void RM03KeepsExplicitSampleIntervals()
    {
        var intervals = DirectMetrics.ParseIntervals("[[1,10],[10,20]]", 20);
        Assert.Equal(new[] { 1, 10 }, intervals[0]);
        Assert.Equal(new[] { 10, 20 }, intervals[1]);
    }

    [Fact]
    public async Task RM02UsesSameCompleteAudioAtZero600Zero()
    {
        var audio = Enumerable.Range(0, 480000).Select(i => i / 480000f).ToArray();
        var calls = new List<TimeSpan>();
        var decoder = new Decoder((samples, offset) =>
        {
            Assert.True(samples.Span.SequenceEqual(audio));
            calls.Add(offset);
            return Hypothesis(offset, samples.Length);
        });
        var result = await DirectMetrics.OffsetAsync(decoder, audio, Options);
        Assert.True(result.ShiftExact);
        Assert.True(result.RepeatExact);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(600), TimeSpan.Zero }, calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => DirectMetrics.OffsetAsync(decoder, new float[480001], Options));
        Assert.Equal(3, calls.Count);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("probability")]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("audioStart")]
    [InlineData("audioEnd")]
    [InlineData("count")]
    public void RM02EveryHypothesisFieldMatters(string field)
    {
        var first = Hypothesis(TimeSpan.Zero, 20);
        var shifted = Hypothesis(TimeSpan.FromSeconds(600), 20);
        var word = shifted.Words[0];
        shifted = field switch
        {
            "text" => shifted with { Words = [word with { Text = "anders" }] },
            "probability" => shifted with { Words = [word with { Probability = .4f }] },
            "start" => shifted with { Words = [word with { Start = word.Start + TimeSpan.FromTicks(1) }] },
            "end" => shifted with { Words = [word with { End = word.End + TimeSpan.FromTicks(1) }] },
            "audioStart" => shifted with { AudioStart = shifted.AudioStart + TimeSpan.FromTicks(1) },
            "audioEnd" => shifted with { AudioEnd = shifted.AudioEnd + TimeSpan.FromTicks(1) },
            _ => shifted with { Words = [] },
        };
        Assert.False(DirectMetrics.Equivalent(first, shifted, TimeSpan.FromSeconds(600)));
    }

    [Fact]
    public async Task RM03SlicesAtWordEndWithoutLosingSamplesOrOffset()
    {
        var audio = Enumerable.Range(0, 30).Select(i => (float)i).ToArray();
        var calls = new List<(float[] Samples, TimeSpan Offset)>();
        var decoder = new Decoder((samples, offset) =>
        {
            calls.Add((samples.ToArray(), offset));
            return Hypothesis(offset, samples.Length);
        });
        var result = await DirectMetrics.SplitAsync(decoder, audio, 5, 25, Options);
        Assert.Equal(15, result.SplitSample);
        Assert.Equal(audio[5..15], calls[1].Samples);
        Assert.Equal(audio[15..25], calls[2].Samples);
        Assert.Equal(TimeSpan.FromTicks(15 * 625), calls[2].Offset);
        Assert.Equal(1, result.Words.Edits);
    }

    [Fact]
    public async Task RM03RejectsNonSampleWordEnd()
    {
        var decoder = new Decoder((samples, offset) => Hypothesis(offset, samples.Length) with
        { Words = [new("wort", offset, offset + TimeSpan.FromTicks(626), .5f)] });
        await Assert.ThrowsAsync<InvalidDataException>(() => DirectMetrics.SplitAsync(decoder, new float[20], 0, 20, Options));
    }

    [Theory]
    [InlineData("a b c", "a x c", 1, 2)]
    [InlineData("a b c", "a c", 1, 2)]
    [InlineData("a b c", "a b x c", 1, 3)]
    public void RM03CountsAllEditKinds(string reference, string hypothesis, int edits, int equal)
    {
        var result = DirectMetrics.Compare(reference.Split(' '), hypothesis.Split(' '));
        Assert.Equal(edits, result.Edits);
        Assert.Equal(equal, result.EqualWords);
        Assert.Equal(edits / 3d, result.WordErrorRate);
    }

    private static RecognitionHypothesis Hypothesis(TimeSpan offset, int length) => new(offset,
        offset + TimeSpan.FromTicks(length * 625L), [new("wort", offset, offset + TimeSpan.FromTicks(length / 2 * 625L), .5f)]);

    private sealed class Decoder(Func<ReadOnlyMemory<float>, TimeSpan, RecognitionHypothesis> decode) : IHypothesisDecoder
    {
        public Task<RecognitionHypothesis> DecodeAsync(ReadOnlyMemory<float> audio, TimeSpan offset, RecognitionOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult(decode(audio, offset));
    }
}
