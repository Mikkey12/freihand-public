using System.Runtime.CompilerServices;
using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

public sealed class SecondImplementationFollowupTests
{
    // ER-05: Auch nach ausgeschöpftem Vorauslesen muss ein Aufnahmestopp zum regulären Ende führen.
    [Fact]
    public async Task R01_FullEngineWithExhaustedLookaheadStillObservesStop()
    {
        using var stop = new CancellationTokenSource();
        var source = new EngineTranscription(new FakeRecognitionEngine(),
            FakeSpeechDetector.Speech((0, 130)), DefaultRecognition, Sec(0.6));
        var feed = new CountingAudio();
        var events = new Recorder<TranscriptEvent>(() => source.RunAsync(feed, stop.Token));
        bool ended;
        TimeSpan fetchedEnd;
        try
        {
            await events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BufferFull }, "voll");
            // Das Vorauslesen darf seine eigene Grenze erreichen; die Prüfaussage schreibt deren Grösse nicht vor.
            await Settle();
            feed.Stop = true;
            fetchedEnd = Sec(Volatile.Read(ref feed.Delivered) * 0.1);
            ended = await Task.WhenAny(events.Completion, Task.Delay(1000)) == events.Completion;
            if (ended) await events.FinishOk();
        }
        finally
        {
            await stop.CancelAsync();
            await events.Finish();
        }
        Assert.True(ended, "Stopp nach gefülltem Vorauslesen darf nicht auf eine Fortschrittszusage warten.");
        Assert.Equal(1, feed.Disposed);
        Assert.Equal(UtteranceEnd.StreamEnd, Assert.Single(Completed(events.Items)).Ending);
        if (fetchedEnd > Sec(120))
            Assert.Contains(Notices(events.Items, LiveNoticeKind.BufferFull), n => n.Start == Sec(120) && n.End == fetchedEnd);
    }

    // Kapitel 5.4: Eine Lese- oder Format-Ausnahme muss vor dem Warten auf fremdes Dispose den Fehlerpfad aktivieren.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task R02_AudioFailureDoesNotWaitForDispose(bool engine, bool invalidFormat)
    {
        using var stop = new CancellationTokenSource();
        var audio = new FailingAudio(invalidFormat);
        ITranscriptSource source = engine
            ? new EngineTranscription(new FakeRecognitionEngine(), FakeSpeechDetector.Speech((0, 1)), DefaultRecognition, Sec(0.6))
            : new LiveTranscription(FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech((0, 1)), DefaultRecognition, Options(LiveMode.Live));
        var events = new Recorder<TranscriptEvent>(() => source.RunAsync(audio, stop.Token));
        bool ended;
        Exception? error;
        try
        {
            await Until(() => audio.Disposed == 1, "Freigabe begonnen");
            ended = await Task.WhenAny(events.Completion, Task.Delay(500)) == events.Completion;
        }
        finally
        {
            audio.Release.TrySetResult();
        }
        error = await events.Finish();
        Assert.True(ended, "Fehler darf nicht hinter einer hängenden Freigabe verborgen bleiben.");
        if (invalidFormat) Assert.IsType<ArgumentException>(error);
        else Assert.IsType<IOException>(error?.GetBaseException());
        Assert.Equal(1, audio.Disposed);
    }

    // BE-06: Der Abbruchbefehl muss auch eine nicht kooperative Sitzungsquelle ohne Warten verwerfen.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R03_SessionAbortOrFailureDoesNotWaitForSourceDispose(bool failScope)
    {
        var source = new CancelSource();
        var run = new SessionRun(source, scope: () => failScope ? throw new IOException("synthetischer Bereichsfehler") : CommandScope.DictationWindow);
        run.Session.PressCommandKey();
        source.Deliver.TrySetResult();
        bool ended;
        try
        {
            if (!failScope) await run.Outputs.WaitFor(o => o is CommandOutput, "Abbruchbefehl");
            await Until(() => source.Disposed == 1, "Quellenfreigabe begonnen");
            ended = await Task.WhenAny(run.Outputs.Completion, Task.Delay(500)) == run.Outputs.Completion;
        }
        finally { source.Release.TrySetResult(); }
        var error = await run.Outputs.Finish();
        var outputs = run.Outputs.Items;
        Assert.True(ended, "Cancel oder Fehler darf nicht auf Dispose der Quelle warten.");
        Assert.Equal(1, source.Disposed);
        if (failScope) Assert.IsType<IOException>(error?.GetBaseException());
        else
        {
            Assert.Null(error);
            Assert.Equal([CommandEffect.Cancel], Effects(outputs));
            Assert.IsType<TextOutput>(outputs[^2]);
            Assert.IsType<CommandOutput>(outputs[^1]);
        }
    }

    // Kapitel 5.4: Auch eine Ausnahme beim Lesen der Engine muss vor deren Freigabe sichtbar werden.
    [Fact]
    public async Task R02_EngineReadFailureDoesNotWaitForDispose()
    {
        var engine = new FailingEngine();
        var source = new EngineTranscription(engine, FakeSpeechDetector.Speech(), DefaultRecognition, Sec(0.6));
        var run = new SourceRun(source);
        bool ended;
        try
        {
            await Until(() => engine.Disposed == 1, "Engine-Freigabe begonnen");
            ended = await Task.WhenAny(run.Events.Completion, Task.Delay(500)) == run.Events.Completion;
        }
        finally { engine.Release.TrySetResult(); }
        var error = await run.Events.Finish();
        Assert.True(ended, "Engine-Lesefehler darf nicht hinter Dispose hängen bleiben.");
        Assert.IsType<IOException>(Assert.IsType<InvalidOperationException>(error).InnerException);
        Assert.Equal(1, engine.Disposed);
    }

    // ER-05, S05: Nicht abgeholtes Audio bleibt gehalten, auch wenn der Detektor Nicht-Sprache freigibt.
    [Fact]
    public async Task R04_BacklogNoticeIncludesTheOldestQueuedAudio()
    {
        using var stop = new CancellationTokenSource();
        var engine = new WaitingEngine();
        var source = new EngineTranscription(engine, FakeSpeechDetector.Speech(), DefaultRecognition, Sec(0.6));
        var run = new SourceRun(source, stop.Token);
        LiveNotice warning;
        try
        {
            await run.FeedUntil(10);
            warning = (LiveNotice)await run.Events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BacklogWarning }, "10 s noch nicht abgeholt");
            Assert.Equal(Sec(10), source.Backlog);
        }
        finally
        {
            await stop.CancelAsync();
            await run.Events.Finish();
            engine.Release.TrySetResult();
            await engine.Released.Task;
        }
        Assert.Equal((Sec(0), Sec(10)), (warning.Start, warning.End));
    }

    private sealed class FailingEngine : IRecognitionEngine, IAsyncEnumerable<RecognitionUpdate>, IAsyncEnumerator<RecognitionUpdate>
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposed;
        public Freihand.Core.Routing.RecognitionRoute Route => Freihand.Core.Routing.RecognitionRoute.Device;
        public RecognitionCapabilities Capabilities { get; } = new(false, true, false, false, null, null, "synthetic");
        public RecognitionUpdate Current => new([], []);
        public IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(IAsyncEnumerable<AudioChunk> audio, RecognitionOptions options, CancellationToken cancellationToken = default) => this;
        public IAsyncEnumerator<RecognitionUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(new IOException("synthetischer Enginefehler"));
        public async ValueTask DisposeAsync() { Interlocked.Increment(ref Disposed); await Release.Task; }
    }

    private sealed class WaitingEngine : IRecognitionEngine
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Freihand.Core.Routing.RecognitionRoute Route => Freihand.Core.Routing.RecognitionRoute.Device;
        public RecognitionCapabilities Capabilities { get; } = new(false, true, false, false, null, null, "synthetic");
        public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(IAsyncEnumerable<AudioChunk> audio, RecognitionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try { await Release.Task; yield return new RecognitionUpdate([], []); }
            finally { Released.TrySetResult(); }
        }
    }

    private sealed class CountingAudio : IAsyncEnumerable<AudioChunk>, IAsyncEnumerator<AudioChunk>
    {
        public volatile bool Stop;
        public int Delivered;
        public int Disposed;
        public AudioChunk Current { get; private set; }
        public IAsyncEnumerator<AudioChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            if (Stop) return ValueTask.FromResult(false);
            var i = Volatile.Read(ref Delivered);
            Current = TestAudio.Chunk(Sec(i * 0.1), Sec((i + 1) * 0.1));
            Interlocked.Increment(ref Delivered);
            return ValueTask.FromResult(true);
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposed); return ValueTask.CompletedTask; }
    }

    private sealed class FailingAudio(bool invalidFormat) : IAsyncEnumerable<AudioChunk>, IAsyncEnumerator<AudioChunk>
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposed;
        public AudioChunk Current => new(new byte[2], 8000, TimeSpan.Zero);
        public IAsyncEnumerator<AudioChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => invalidFormat
            ? ValueTask.FromResult(true)
            : ValueTask.FromException<bool>(new IOException("synthetischer Lesefehler"));
        public async ValueTask DisposeAsync() { Interlocked.Increment(ref Disposed); await Release.Task; }
    }

    private sealed class CancelSource : ITranscriptSource
    {
        public TaskCompletionSource Deliver { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposed;
        public TimeSpan AcceptedEnd => Deliver.Task.IsCompleted ? Sec(0.5) : TimeSpan.Zero;
        public TimeSpan Backlog => TimeSpan.Zero;
        public void MarkBoundary(TimeSpan at) { }
        public async IAsyncEnumerable<TranscriptEvent> RunAsync(IAsyncEnumerable<AudioChunk> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Deliver.Task;
                yield return new UtteranceCompleted(Utterance(0, "abbrechen"));
            }
            finally { Interlocked.Increment(ref Disposed); await Release.Task; }
        }
    }
}
