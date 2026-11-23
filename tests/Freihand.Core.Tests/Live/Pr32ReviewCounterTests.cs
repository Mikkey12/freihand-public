using Freihand.Core.Live;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>Gegenproben zu E1/E2 (ER-03, ER-05), PR #32; nur synthetische Fixtures.</summary>
public sealed class Pr32ReviewCounterTests
{
    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task C01_SampleRoundingStillReachesEmptyCutAtG(LiveMode mode)
    {
        // Regel 1 rundet das Auftragsende ab. Trotz MaxUtterance > CutGuard kann der reale Auftrag
        // genau CutGuard lang sein: Fall c muss dann bei G schneiden, nicht bei K stehenbleiben.
        var options = Options(mode) with
        {
            Window = Sec(1.10001), MaxUtterance = Sec(1.10001), CutGuard = Sec(1.1),
        };
        Assert.True(options.MaxUtterance > options.CutGuard);
        var decoder = FakeHypothesisDecoder.Silent();
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 3)), options);
        var events = await run.EndAt(3);
        var first = Completed(events).First();
        Assert.Equal(UtteranceEnd.LengthCut, first.Ending);
        Assert.Equal(Sec(1.1), first.End);
        Assert.Contains(decoder.Requests, request => request.Start == Sec(0) && request.End == Sec(1.1));
        Assert.Contains(decoder.Requests, request => request.Start == Sec(1.1));
        Assert.Contains(Notices(events, LiveNoticeKind.SpeechWithoutText),
            notice => notice.Start == Sec(0) && notice.End == Sec(1.1));
        Assert.Empty(CompletedWords(events));
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task C02_QueuedBoundariesAndAudioGapCountOnlyHeldAudioAfterDuePoint(LiveMode mode)
    {
        // Zwei per Taste geschlossene Äusserungen warten hinter einem blockierten Abschluss.
        // Satzweise steht Arbeit seit 1 s aus; live seit K=0, weil noch kein Schritt verarbeitet ist.
        using var stop = new CancellationTokenSource();
        var gate = new TestGate();
        var decoder = FakeHypothesisDecoder.Silent();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 2), (100, 120)),
            Options(mode), cancellationToken: stop.Token);
        try
        {
            await run.FeedUntil(1);
            run.Source.MarkBoundary(Sec(1));
            await Until(() => decoder.Calls.Count == 1, "blockierter erster Auftrag");
            Assert.False(decoder.Calls[0].Completed);
            await run.FeedUntil(2);
            run.Source.MarkBoundary(Sec(2));
            run.Feed.SkipTo(Sec(100));
            var threshold = mode == LiveMode.Live ? 108 : 109;
            await run.FeedUntil(threshold - 0.1);
            Assert.Empty(Notices(run.Events.Items, LiveNoticeKind.BacklogWarning));
            await run.FeedUntil(threshold);
            var warning = (LiveNotice)await run.Events.WaitFor(
                item => item is LiveNotice { Kind: LiveNoticeKind.BacklogWarning }, "Warnung ohne Lückenzeit");
            Assert.Equal((Sec(0), Sec(threshold)), (warning.Start, warning.End));
            gate.Open();
            await run.Events.WaitFor(item => item is LiveNotice { Kind: LiveNoticeKind.BacklogCleared }, "Entwarnung nach Verarbeitung");
            await run.EndAt(120);
        }
        finally
        {
            gate.Open();
            stop.Cancel();
            await run.Events.Finish();
        }
    }
    [Fact]
    public async Task C03_SentenceBacklogWarningStartsAtTwentySecondsNotAtTen()
    {
        // E2/ER-05: Der erste Satzdecode ist erst bei C=10 s fällig.
        // Vor 20 s darf er noch keine Warnung wegen 10 s Verarbeitungsrückstand auslösen.
        using var stop = new CancellationTokenSource();
        var gate = new TestGate();
        var decoder = FakeHypothesisDecoder.Silent();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 30)),
            Options(LiveMode.Sentence), cancellationToken: stop.Token);
        try
        {
            await run.FeedUntil(10);
            await Until(() => decoder.Calls.Count == 1, "erster Satzdecode blockiert");
            Assert.False(decoder.Calls[0].Completed);
            await run.FeedUntil(19.9);
            Assert.Empty(Notices(run.Events.Items, LiveNoticeKind.BacklogWarning));
            await run.FeedUntil(20);
            var warning = (LiveNotice)await run.Events.WaitFor(
                item => item is LiveNotice { Kind: LiveNoticeKind.BacklogWarning }, "Warnung bei genau 20 s");
            Assert.Equal((Sec(0), Sec(20)), (warning.Start, warning.End));
        }
        finally
        {
            stop.Cancel();
            gate.Open();
            await run.Events.Finish();
        }
    }
}
