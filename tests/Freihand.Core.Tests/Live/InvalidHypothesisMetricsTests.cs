using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>RM01: P3 zählt jede verworfene Hypothese, auch vor einer erfolgreichen Wiederholung.</summary>
public sealed class InvalidHypothesisMetricsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RM01_RejectsCountBeforeRetryOutcome(int rejects)
    {
        var decoder = FakeHypothesisDecoder.Silent();
        decoder.RawResponse = request => new RecognitionHypothesis(request.Start, request.End,
            request.Index < rejects
                ? [new RecognizedWord("kaputt", request.Start, request.Start, 0.9f)]
                : [Word("Gültig", 1, 2)]);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((1, 3)), Options(LiveMode.Sentence));
        var source = Assert.IsType<LiveTranscription>(run.Source);
        Assert.Equal(0, source.InvalidHypotheses);

        run.Feed.ReleaseUntil(Sec(5));
        run.Feed.Complete();
        var failure = await run.Events.Finish();

        Assert.Equal(rejects, source.InvalidHypotheses);
        Assert.Equal(rejects == 0 ? 1 : 2, decoder.Requests.Count);
        if (rejects == 2)
        {
            Assert.NotNull(failure);
            Assert.Single(Notices(run.Events.Items, LiveNoticeKind.DecoderFailed));
            Assert.Empty(CompletedWords(run.Events.Items));
        }
        else
        {
            Assert.Null(failure);
            Assert.Empty(Notices(run.Events.Items, LiveNoticeKind.DecoderFailed));
            Assert.Equal(["Gültig"], CompletedWords(run.Events.Items));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RM01_DecoderExceptionsAreNotP3Rejections(bool invalidException)
    {
        // Auch derselbe Ausnahmetyp aus dem Decoder ist keine Ablehnung durch HypothesisCheck.
        var decoder = new FakeHypothesisDecoder(_ => throw (invalidException
            ? new InvalidHypothesisException("Decoderfehler")
            : new InvalidOperationException("Decoderfehler")));
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((1, 3)), Options(LiveMode.Sentence));
        var source = Assert.IsType<LiveTranscription>(run.Source);

        run.Feed.ReleaseUntil(Sec(5));
        run.Feed.Complete();
        var failure = await run.Events.Finish();

        Assert.NotNull(failure);
        Assert.Equal(2, decoder.Requests.Count);
        Assert.Single(Notices(run.Events.Items, LiveNoticeKind.DecoderFailed));
        Assert.Empty(CompletedWords(run.Events.Items));
        Assert.Equal(0, source.InvalidHypotheses);
    }
}
