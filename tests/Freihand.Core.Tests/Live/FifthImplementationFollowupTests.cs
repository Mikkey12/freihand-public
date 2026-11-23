using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;
namespace Freihand.Core.Tests.Live;
public sealed class FifthImplementationFollowupTests
{
    // Kapitel 5.4: Auch nach Engine-EOF macht ein Freigabefehler den Lauf zum Fehler-/Abbruchlauf.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseFailureAfterEngineEofAbortsOtherRelease(bool engineFails)
    {
        using var stop = new CancellationTokenSource();
        var audio = new EmptyAudio(!engineFails);
        var engine = new EndingEngine(engineFails);
        var source = new EngineTranscription(engine, FakeSpeechDetector.Speech((0, 1)), DefaultRecognition, Sec(0.6));
        var events = new Recorder<TranscriptEvent>(() => source.RunAsync(audio, stop.Token));
        bool ended;
        Exception? error = null;
        try
        {
            await audio.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            engine.End.TrySetResult();
            await engine.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(events.Completion.IsCompleted);
            var failing = engineFails ? engine.Release : audio.Release;
            failing.TrySetResult();
            ended = await Task.WhenAny(events.Completion, Task.Delay(500)) == events.Completion;
            if (ended) error = await events.Finish();
        }
        finally
        {
            await stop.CancelAsync();
            audio.Release.TrySetResult();
            engine.End.TrySetResult();
            engine.Release.TrySetResult();
            await events.Finish();
        }
        Assert.True(ended, "Nach Freigabefehler darf der Lauf nicht auf die andere Freigabe warten.");
        Assert.IsType<IOException>(error?.GetBaseException());
        Assert.Equal(1, audio.Disposed);
        Assert.Equal(1, engine.Disposed);
    }
    private sealed class EmptyAudio(bool fail) : IAsyncEnumerable<AudioChunk>, IAsyncEnumerator<AudioChunk>
    {
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposed;
        public AudioChunk Current => throw new InvalidOperationException();
        public IAsyncEnumerator<AudioChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposed); Disposing.TrySetResult(); await Release.Task;
            if (fail) throw new IOException("synthetischer Audio-Freigabefehler");
        }
    }
    private sealed class EndingEngine(bool fail) : IRecognitionEngine, IAsyncEnumerable<RecognitionUpdate>, IAsyncEnumerator<RecognitionUpdate>
    {
        public TaskCompletionSource End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposed;
        public RecognitionRoute Route => RecognitionRoute.Device;
        public RecognitionCapabilities Capabilities { get; } = new(false, true, false, true, null, null, "synthetic");
        public RecognitionUpdate Current => throw new InvalidOperationException();
        public IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(IAsyncEnumerable<AudioChunk> audio, RecognitionOptions options, CancellationToken cancellationToken = default) => this;
        public IAsyncEnumerator<RecognitionUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public async ValueTask<bool> MoveNextAsync() { await End.Task; return false; }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposed); Disposing.TrySetResult(); await Release.Task;
            if (fail) throw new IOException("synthetischer Engine-Freigabefehler");
        }
    }
}
