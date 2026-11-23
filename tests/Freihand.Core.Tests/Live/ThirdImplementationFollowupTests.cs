using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;
namespace Freihand.Core.Tests.Live;
public sealed class ThirdImplementationFollowupTests
{
    // Kapitel 5.4: Reguläres Ende wartet auf Freigabe und meldet deren Fehler.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegularEngineEndWaitsForDisposeAndReportsFailure(bool fail)
    {
        var engine = new EndingEngine(fail);
        var source = new EngineTranscription(engine, FakeSpeechDetector.Speech((0, 1)), DefaultRecognition, Sec(0.6));
        var events = new Recorder<TranscriptEvent>(() => source.RunAsync(Empty()));
        bool endedEarly;
        Exception? error;
        try
        {
            await engine.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            endedEarly = await Task.WhenAny(events.Completion, Task.Delay(200)) == events.Completion;
        }
        finally { engine.Release.TrySetResult(); }
        error = await events.Finish();
        Assert.False(endedEarly, "Reguläres Ende muss auf Dispose warten.");
        Assert.Equal(1, engine.DisposeCount);
        if (fail) Assert.IsType<IOException>(error?.GetBaseException());
        else Assert.Null(error);
    }
    [Fact]
    public async Task RegularEngineDisposeFailureIsNotSwallowed()
    {
        var engine = new EndingEngine(true);
        engine.Release.TrySetResult();
        var source = new EngineTranscription(engine, FakeSpeechDetector.Speech((0, 1)), DefaultRecognition, Sec(0.6));
        var events = new Recorder<TranscriptEvent>(() => source.RunAsync(Empty()));
        var error = await events.Finish();
        Assert.Equal(1, engine.DisposeCount);
        Assert.IsType<IOException>(error?.GetBaseException());
    }
    private static async IAsyncEnumerable<AudioChunk> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
    private sealed class EndingEngine(bool fail) : IRecognitionEngine, IAsyncEnumerable<RecognitionUpdate>, IAsyncEnumerator<RecognitionUpdate>
    {
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount;
        public RecognitionRoute Route => RecognitionRoute.Device;
        public RecognitionCapabilities Capabilities { get; } = new(false, true, false, true, null, null, "test");
        public RecognitionUpdate Current => throw new InvalidOperationException();
        public IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(IAsyncEnumerable<AudioChunk> audio, RecognitionOptions options, CancellationToken cancellationToken = default) => this;
        public IAsyncEnumerator<RecognitionUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            Disposing.TrySetResult();
            await Release.Task;
            if (fail) throw new IOException("synthetischer Freigabefehler");
        }
    }
}
