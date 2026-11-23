using System.Runtime.CompilerServices;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

public sealed class ImplementationReviewTests
{
    // ER-02: Bereits übernommenes Audio darf nicht erneut abgefragt werden. Seit E1 (03.11.2026) übernimmt live nur der
    // Längenschnitt; darum schneidet hier ein kurzes MaxUtterance hinter dem nicht auf ein Sample fallenden Wortende.
    [Fact]
    public async Task U01_DecodeNeverStartsBeforeNonSampleAlignedAdoptedEnd()
    {
        var word = Word("Alpha", 0.1, 0.20001);
        var decoder = FakeHypothesisDecoder.ByInterval([word]);
        var options = Options(LiveMode.Live) with { Window = Sec(1.1), MaxUtterance = Sec(1.1) };
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 3)), options);
        var cut = (await run.Steps(0.5, 1, 1.1))[^1];
        await run.EndAt(3.6);

        // Der Längenschnitt bei 1,1 s übernimmt „Alpha“; jeder spätere Auftrag beginnt frühestens an dessen Ende (T45)
        var later = decoder.Requests.Where(request => request.Index > cut.Request.Index).ToList();
        Assert.NotEmpty(later);
        Assert.All(later, next => Assert.True(next.Start >= word.End,
            $"Decode ab {next.Start.Ticks}, übernommen bis {word.End.Ticks} Ticks."));
    }

    // ER-05: Auch ein gültiger grosser Eingabeblock darf die feste Puffergrenze nicht umgehen.
    [Fact]
    public async Task U02_LargeChunkCannotExceedBacklogLimit()
    {
        using var stop = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoder = FakeHypothesisDecoder.Silent();
        decoder.Hold = _ => gate.Task;
        var options = Options(LiveMode.Live);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 130)), options, cancellationToken: stop.Token);
        TimeSpan backlog;
        try
        {
            run.Feed.Enqueue(TestAudio.Chunk(Sec(0), Sec(121)));
            await Until(() => run.Source.AcceptedEnd >= Sec(120), "Puffergrenze erreicht");
            backlog = run.Source.Backlog;
        }
        finally
        {
            await stop.CancelAsync();
            gate.TrySetResult();
            await run.Events.Finish();
        }
        Assert.True(backlog <= options.BacklogLimit, $"Puffer: {backlog.TotalSeconds} s.");
    }

    // BE-06: Eine Quelle ohne Abbruchunterstützung darf den Sitzungsabbruch nicht blockieren.
    [Fact]
    public async Task U03_CancelDoesNotWaitForUncooperativeEngine_TenRuns()
    {
        var blocked = 0;
        for (var i = 0; i < 10; i++)
        {
            using var stop = new CancellationTokenSource();
            var engine = new DelayedEngine();
            var source = new EngineTranscription(engine, FakeSpeechDetector.Speech((0, 1)), DefaultRecognition, Sec(0.6));
            var run = new SourceRun(source, stop.Token);
            await engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await run.FeedUntil(0.1);
            await stop.CancelAsync();
            try
            {
                if (await Task.WhenAny(run.Events.Completion, Task.Delay(200)) != run.Events.Completion)
                {
                    blocked++;
                }
            }
            finally
            {
                engine.Release.TrySetResult();
                await run.Events.Finish();
            }
            Assert.Empty(run.Events.Items);
        }
        Assert.Equal(0, blocked);
    }

    // ER-03: Vorgeschlagene neue Grenze: höchstens 30 s je Detektoraufruf, ohne Äusserungsgrenze.
    [Fact]
    public async Task U04_ProposedRollingDetectorWindowIsAtMostThirtySeconds()
    {
        var detector = new WindowDetector();
        var engine = new FakeRecognitionEngine();
        var source = new EngineTranscription(engine, detector, DefaultRecognition, Sec(0.6));
        var run = new SourceRun(source);
        await run.EndAt(40);
        Assert.True(detector.MaximumSamples <= 30 * 16000,
            $"Grösster Detektoraufruf: {detector.MaximumSamples} Samples.");
    }

    private sealed class WindowDetector : ISpeechDetector
    {
        public int MaximumSamples { get; private set; }
        public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset)
        {
            MaximumSamples = Math.Max(MaximumSamples, audio.Length);
            return [new SpeechRegion(offset, offset + TimeSpan.FromTicks(audio.Length * 625L))];
        }
    }

    private sealed class DelayedEngine : IRecognitionEngine
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RecognitionRoute Route => RecognitionRoute.Device;
        public RecognitionCapabilities Capabilities { get; } = new(false, true, false, false, null, null, "review");
        public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio, RecognitionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task; // Die Quelle unterstützt keinen Abbruch; die Freigabe gehört dem Test.
            yield return new RecognitionUpdate([], []);
        }
    }
}
