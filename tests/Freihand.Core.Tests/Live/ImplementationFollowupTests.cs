using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

public sealed class ImplementationFollowupTests
{
    // BE-06: Auch wenn MoveNext zwischen Abbruch und Freigabe endet, darf langsames Dispose nicht blockieren.
    [Fact]
    public async Task N01_CompletedReadAfterCancelDoesNotMakeDisposeBlocking()
    {
        var blocked = 0;
        for (var i = 0; i < 10; i++)
        {
            using var stop = new CancellationTokenSource();
            var inner = new ControlledSequence();
            var reader = new CancellableEnumerator<int>(inner, stop.Token);
            var move = reader.MoveNextAsync(stop.Token);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            inner.Read.TrySetResult(false); // kontrolliertes Rennen: vor Dispose, aber nach Cancel fertig
            var dispose = reader.DisposeAsync().AsTask();
            try
            {
                if (await Task.WhenAny(dispose, Task.Delay(200)) != dispose) blocked++;
            }
            finally
            {
                inner.ReleaseDispose.TrySetResult();
                await dispose;
                await reader.DisposeAsync();
            }
            Assert.Equal(1, inner.DisposeCalls);
        }
        Assert.Equal(0, blocked);
    }

    // ER-05: Der Aufnahmestopp am vollen Puffer muss das bereits angenommene Audio abschliessen können.
    [Fact]
    public async Task N02_EngineAtExactLimitCanFinishAfterRecordingStops()
    {
        using var stop = new CancellationTokenSource();
        var source = new EngineTranscription(new FakeRecognitionEngine(),
            FakeSpeechDetector.Speech((0, 120)), DefaultRecognition, Sec(0.6));
        var run = new SourceRun(source, stop.Token);
        run.Feed.ReleaseUntil(Sec(120));
        await run.Events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BufferFull }, "Puffer voll");
        run.Feed.Stop();
        var completed = await Task.WhenAny(run.Events.Completion, Task.Delay(500)) == run.Events.Completion;
        try
        {
            if (completed) await run.Events.FinishOk();
        }
        finally
        {
            await stop.CancelAsync();
            await run.Events.Finish();
        }
        Assert.True(completed, "Aufnahmestopp muss EOF an die Engine liefern, auch ohne CompleteUntil.");
        Assert.Equal(UtteranceEnd.StreamEnd, Assert.Single(Completed(run.Events.Items)).Ending);
    }

    // ER-02/ER-05: Ein aufgerundeter Kürzungspunkt ohne restliches Sample darf den Längenschnitt nicht blockieren.
    [Fact]
    public async Task N03_LengthCutCompletesWhenNoWholeSampleRemains()
    {
        using var stop = new CancellationTokenSource();
        var decoder = FakeHypothesisDecoder.ByInterval([Word("Langwort", 0, 9.99990)]);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 20)),
            // Seit E1 verlangt live Window >= MaxUtterance; der volle Auftrag ist der Längenschnitt bei C
            Options(LiveMode.Live) with { Window = Sec(9.99999), MaxUtterance = Sec(9.99999) }, cancellationToken: stop.Token);
        bool completed;
        try
        {
            for (var half = 1; half <= 19; half++) await run.StepTo(half * 0.5);
            await run.FeedUntil(10);
            await Until(() => decoder.Calls.Any(c => c.Request.End == Sec(9.9999375) && c.Returned), "voller Auftrag beendet");
            var last = decoder.Calls.Last(c => c.Request.End == Sec(9.9999375) && c.Returned);
            await UntilProcessed(run.Source, last.Request.Index + 1);
            await Settle();
            completed = Completed(run.Events.Items).Any(u => u.Ending == UtteranceEnd.LengthCut);
        }
        finally
        {
            await stop.CancelAsync();
            await run.Events.Finish();
        }
        Assert.True(completed, "C ist erreicht; es fehlt kein decodierbares Sample mehr.");
    }

    // ER-05: Fehlendes Audio darf nicht als gehaltener Puffer die nächste echte Eingabe blockieren.
    [Fact]
    public async Task N04_AudioGapDoesNotConsumeEngineBacklogCapacity()
    {
        using var stop = new CancellationTokenSource();
        var engine = new FakeRecognitionEngine();
        var source = new EngineTranscription(engine, FakeSpeechDetector.Speech((0, 0.1), (200, 201)),
            DefaultRecognition, Sec(0.6));
        var run = new SourceRun(source, stop.Token);
        bool delivered;
        TimeSpan backlog;
        try
        {
            await run.FeedUntil(0.1);
            engine.Push(FakeRecognitionEngine.Completion(0.1));
            await UntilProcessed(source, 1);
            run.Feed.SkipTo(Sec(200));
            run.Feed.ReleaseUntil(Sec(200.1));
            await Until(() => source.AcceptedEnd >= Sec(200), "Lücke erkannt");
            await Settle();
            delivered = engine.ReceivedEnd == Sec(200.1);
            backlog = source.Backlog;
        }
        finally
        {
            await stop.CancelAsync();
            await run.Events.Finish();
        }
        Assert.True(delivered, $"Der erste echte Block nach der Lücke muss ankommen; Rückstand: {backlog.TotalSeconds} s.");
        Assert.True(backlog <= Sec(0.1), $"Nur 0,1 s unbestätigtes Audio, gemeldet: {backlog.TotalSeconds} s.");
    }

    // BE-06: Spätes Ergebnis oder spätere Ausnahme wird verworfen; keine parallele oder doppelte Freigabe.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G02_PendingReadIsReleasedExactlyOnce(bool failRead)
    {
        for (var i = 0; i < 100; i++)
        {
            using var stop = new CancellationTokenSource();
            var inner = new ControlledSequence();
            var reader = new CancellableEnumerator<int>(inner, stop.Token);
            var move = reader.MoveNextAsync(stop.Token);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            await reader.DisposeAsync();
            Assert.Equal(0, inner.DisposeCalls);
            if (failRead) inner.Read.TrySetException(new InvalidOperationException("synthetischer Fehler"));
            else inner.Read.TrySetResult(false);
            await Until(() => Volatile.Read(ref inner.DisposeCalls) == 1, "späte Freigabe");
            inner.ReleaseDispose.TrySetResult();
            await reader.DisposeAsync();
            Assert.Equal(1, inner.DisposeCalls);
        }
    }

    // ER-05: Der noch nicht angenommene Rest eines grossen Blocks wird später vollständig verarbeitet.
    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task G01_LargeBlockKeepsEveryWordAcrossBackpressure(LiveMode mode)
    {
        var words = Enumerable.Range(0, 130).Select(i => Word("Wort" + i, i + 0.1, i + 0.4)).ToArray();
        var decoder = FakeHypothesisDecoder.ByInterval(words);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 130)), Options(mode));
        run.Feed.Enqueue(TestAudio.Chunk(Sec(0), Sec(130.6)));
        run.Feed.Complete();
        try
        {
            await run.Events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BufferFull }, "Puffer voll");
            Assert.True(run.Source.Backlog <= Sec(120));
        }
        finally { gate.TrySetResult(); }
        var events = await run.Events.FinishOk();
        Assert.Equal(words.Select(w => w.Text), CompletedWords(events));
        Assert.Equal(Sec(130.6), run.Source.AcceptedEnd);
        Assert.All(decoder.Calls, c => Assert.True(c.AudioIntact));
    }

    private sealed class ControlledSequence : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public TaskCompletionSource<bool> Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCalls;
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(Read.Task);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            await ReleaseDispose.Task;
        }
    }
}
