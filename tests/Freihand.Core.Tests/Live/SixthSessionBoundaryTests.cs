using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Xunit;
using static Freihand.Core.Tests.Live.LiveTestKit;
namespace Freihand.Core.Tests.Live;
public sealed class SixthSessionBoundaryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SourceOpenAndCurrentFailuresPreserveCauseWithoutWaiting(bool openFails, bool releaseFails)
    {
        var source = new Source(openFails, releaseFails);
        var session = new DictationSession(source,
            new DictationInterpreter(BuiltInCommands.Pilot, Interpreting()),
            () => CommandScope.DictationWindow);
        async Task Drain() { await foreach (var item in session.RunAsync(Audio())) Assert.Fail("Keine Ausgabe erwartet."); }
        try
        {
            var error = await Record.ExceptionAsync(() => Drain().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Same(source.Error, error);
            Assert.Equal(openFails ? 0 : 1, source.Disposals);
            Assert.False(source.Gate.Task.IsCompleted);
        }
        finally { source.Gate.TrySetResult(); }
        if (!openFails) await source.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await UnobservedFailureLog.SeenAsync(source.Marker));
        Assert.Equal(openFails ? 0 : 1, source.Disposals);
    }
    private static async IAsyncEnumerable<AudioChunk> Audio() { await Task.CompletedTask; yield break; }
    private sealed class Source(bool openFails, bool releaseFails) : ITranscriptSource,
        IAsyncEnumerable<TranscriptEvent>, IAsyncEnumerator<TranscriptEvent>
    {
        public string Marker { get; } = Guid.NewGuid().ToString("N");
        public Exception Error { get; } = new InvalidDataException("synthetischer Quellenfehler");
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals;
        public TimeSpan AcceptedEnd => TimeSpan.Zero;
        public TimeSpan Backlog => TimeSpan.Zero;
        public void MarkBoundary(TimeSpan at) { }
        public IAsyncEnumerable<TranscriptEvent> RunAsync(IAsyncEnumerable<AudioChunk> audio,
            CancellationToken cancellationToken = default) => this;
        public IAsyncEnumerator<TranscriptEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => openFails ? throw Error : this;
        public TranscriptEvent Current => throw Error;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(true);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            try
            {
                await Gate.Task;
                if (releaseFails) throw new IOException(Marker);
            }
            finally { Released.TrySetResult(); }
        }
    }
}
