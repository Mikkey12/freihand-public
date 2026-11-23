using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;
namespace Freihand.Core.Tests.Live;
public sealed class FourthImplementationFollowupTests
{
    // Kapitel 5.4: Reguläre Audiofreigabe bleibt regulär, auch wenn danach die Engine endet.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EngineEndDoesNotCancelRegularAudioRelease(bool full, bool fail)
    {
        using var stop = new CancellationTokenSource();
        var audio = new HeldAudio(full ? 120.5 : 0, fail);
        var engine = new FakeRecognitionEngine { FinishWithAudio = false };
        var source = new EngineTranscription(engine, FakeSpeechDetector.Speech((0, 121)), DefaultRecognition, Sec(0.6));
        var events = new Recorder<TranscriptEvent>(() => source.RunAsync(audio, stop.Token));
        bool early;
        Exception? error;
        try
        {
            await audio.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            engine.Finish();
            early = await Task.WhenAny(events.Completion, Task.Delay(300)) == events.Completion;
            audio.Release.TrySetResult();
            error = await events.Finish();
        }
        finally { audio.Release.TrySetResult(); await stop.CancelAsync(); await events.Finish(); }
        if (fail) Assert.IsType<IOException>(error?.GetBaseException());
        else Assert.Null(error);
        Assert.False(early, "Reguläre Audiofreigabe darf durch Engine-EOF nicht abgebrochen werden.");
        Assert.Equal(1, audio.DisposeCount);
    }

    // Kapitel 5.4: Ein Freigabefehler beendet auch noch laufende Geschwisteraufgaben.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioReleaseFailureAbortsRemainingRecognition(bool engineMode)
    {
        using var stop = new CancellationTokenSource();
        var audio = new HeldAudio(0.5, true);
        var decoderGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoder = FakeHypothesisDecoder.Silent();
        decoder.Hold = _ => decoderGate.Task;
        var engine = new FakeRecognitionEngine { FinishWithAudio = false };
        ITranscriptSource source = engineMode
            ? new EngineTranscription(engine, FakeSpeechDetector.Speech((0, 1)), DefaultRecognition, Sec(0.6))
            : new LiveTranscription(decoder, FakeSpeechDetector.Speech((0, 1)), DefaultRecognition, Options(LiveMode.Live));
        var events = new Recorder<TranscriptEvent>(() => source.RunAsync(audio, stop.Token));
        bool ended;
        Exception? error = null;
        try
        {
            await audio.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!engineMode) await Until(() => decoder.Open > 0, "Decoder wartet");
            audio.Release.TrySetResult();
            await audio.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ended = await Task.WhenAny(events.Completion, Task.Delay(500)) == events.Completion;
            if (ended) error = await events.Finish();
        }
        finally
        {
            await stop.CancelAsync();
            decoderGate.TrySetResult();
            engine.Finish();
            await events.Finish();
        }
        Assert.True(ended, "Freigabefehler muss den Lauf ohne weiteren externen Abbruch beenden.");
        Assert.IsType<IOException>(error?.GetBaseException());
        Assert.Equal(1, audio.DisposeCount);
    }

    private sealed class HeldAudio(double seconds, bool fail) : IAsyncEnumerable<AudioChunk>, IAsyncEnumerator<AudioChunk>
    {
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount;
        private bool _sent;
        public AudioChunk Current => TestAudio.Chunk(Sec(0), Sec(seconds));
        public IAsyncEnumerator<AudioChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            var more = !_sent && seconds > 0;
            _sent = true;
            return ValueTask.FromResult(more);
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            Disposing.TrySetResult();
            await Release.Task;
            if (fail)
            {
                Failed.TrySetResult();
                throw new IOException("synthetischer Freigabefehler");
            }
        }
    }
}
