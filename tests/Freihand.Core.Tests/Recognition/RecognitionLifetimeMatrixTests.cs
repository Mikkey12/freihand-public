using System.Text.Json.Nodes;
using Freihand.Adapters.Recognition;
using Freihand.Core.Tests.Recognition.Kit;
using Freihand.Core.Tests.TestKit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

public sealed class RecognitionLifetimeMatrixTests
{
    public static IEnumerable<object[]> WriteCases() =>
        from flush in new[] { false, true }
        from mode in new[] { "cancel", "decode-timeout", "detect-timeout", "decode-release", "detect-release" }
        select new object[] { flush, mode };

    [Theory]
    [MemberData(nameof(WriteCases))]
    public async Task WriteAndFlushShareTheRequestDeadline(bool flush, string mode)
    {
        var decode = new DuplexPipe("lifetime-decode");
        var detect = new DuplexPipe("lifetime-detect");
        var clock = new ManualClock();
        var detecting = mode.StartsWith("detect");
        var stream = new GatedStream(detecting ? detect.App : decode.App, flush);
        await using var client = new RecognizerClient(detecting ? decode.App : stream,
            detecting ? stream : detect.App, ClientTimeouts.Default, clock);
        using var cancellation = new CancellationTokenSource();
        Task call = detecting ? DetectAsync(client, Samples(16_000), Sec(1))
            : client.DecodeAsync(Samples(16_000), Sec(1), German(), cancellation.Token);
        await stream.Entered.Task.WaitAsync(Guard);
        if (mode == "cancel")
        {
            await cancellation.CancelAsync();
            await Throws<OperationCanceledException>(call);
        }
        else if (mode.EndsWith("timeout"))
        {
            var limit = detecting ? ClientTimeouts.Default.Detect : ClientTimeouts.Default.DecodeBase + Sec(5);
            clock.Advance(limit - Tick);
            Assert.False(call.IsCompleted);
            clock.Advance(Tick);
            await Throws<RecognizerUnavailableException>(call);
        }
        else
        {
            // Kontrolle zu P8 5.7: Dieselbe freigegebene Barriere erlaubt eine reguläre Antwort.
            stream.Release.TrySetResult();
            var peer = new FrameChannel(detecting ? detect.Recognizer : decode.Recognizer);
            var request = await peer.ExpectAsync(detecting ? "detect" : "decode");
            await peer.SendAsync(detecting
                ? new JsonObject { ["type"] = "regions", ["id"] = request.Long("id"), ["regions"] = new JsonArray() }
                : new JsonObject { ["type"] = "hypothesis", ["id"] = request.Long("id"),
                    ["audioStartTicks"] = Ticks(1), ["audioEndTicks"] = Ticks(2), ["words"] = new JsonArray() });
            await call.WaitAsync(Guard);
        }
    }

    [Theory]
    [InlineData(false, "regions")]
    [InlineData(false, "hello")]
    [InlineData(true, "hypothesis")]
    [InlineData(true, "cancelled")]
    public async Task WrongLaneResponseAlsoRejectsTheNextRequest(bool detecting, string response)
    {
        var decode = new DuplexPipe("lane-decode");
        var detect = new DuplexPipe("lane-detect");
        await using var client = new RecognizerClient(decode.App, detect.App, ClientTimeouts.Default, new ManualClock());
        var peer = new FrameChannel(detecting ? detect.Recognizer : decode.Recognizer);
        Task call = detecting ? DetectAsync(client, Samples(16_000), Sec(1))
            : client.DecodeAsync(Samples(16_000), Sec(1), German());
        var request = await peer.ExpectAsync(detecting ? "detect" : "decode");
        await peer.SendAsync(new JsonObject { ["type"] = response, ["id"] = request.Long("id") });
        await Throws<RecognizerUnavailableException>(call);
        await Throws<RecognizerUnavailableException>(client.DecodeAsync(Samples(16_000), Sec(1), German()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BothChannelsAreReleasedOnceEvenIfEitherReleaseThrows(bool decodeThrows, bool detectThrows)
    {
        var decode = new GatedStream(new DuplexPipe("dispose-decode").App, false, decodeThrows);
        var detect = new GatedStream(new DuplexPipe("dispose-detect").App, false, detectThrows);
        var client = new RecognizerClient(decode, detect, ClientTimeouts.Default, new ManualClock());
        var error = await Record.ExceptionAsync(async () => await client.DisposeAsync());
        Assert.Equal(decodeThrows || detectThrows, error is not null);
        Assert.Equal(1, decode.DisposeCalls);
        Assert.Equal(1, detect.DisposeCalls);
        var repeated = await Record.ExceptionAsync(async () => await client.DisposeAsync());
        Assert.Same(error, repeated);
        Assert.Equal(1, decode.DisposeCalls);
        Assert.Equal(1, detect.DisposeCalls);
    }

    [Fact]
    public async Task DisposeWakesInitialReadyWaitWithoutAdvancingTheClock()
    {
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = SupervisorHarness.Create(_ => LaunchPlan.Service(loader => loader.Gate = loading.Task));
        await harness.StartAsync();
        var process = await harness.Launcher.LaunchedAsync(1);
        await Until(() => process.Loader.Calls.Count == 1, "erstes Laden erreicht");
        var waiting = harness.SessionAsync();
        await harness.Clock.WaitForTimerDueInAsync(harness.Timeouts.ModelLoad);
        try
        {
            await harness.Supervisor.DisposeAsync();
            await Throws<RecognizerUnavailableException>(waiting);
        }
        finally { loading.TrySetResult(); }
    }

    /// <summary>P8 5.4/5.7: Alle Aufrufer warten denselben Abschluss; beide Kanäle starten ihre Freigabe.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentDisposeWaitsForBothIndependentReleases(bool blockDetect, bool synchronous)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decode = new GatedStream(new DuplexPipe("parallel-dispose-decode").App, false);
        var detect = new GatedStream(new DuplexPipe("parallel-dispose-detect").App, false);
        var blocking = blockDetect ? detect : decode;
        blocking.DisposeBarrier = barrier.Task;
        blocking.BlockDisposeSynchronously = synchronous;
        var client = new RecognizerClient(decode, detect, ClientTimeouts.Default, new ManualClock());
        var first = client.DisposeAsync().AsTask();
        try
        {
            await Task.WhenAll(decode.Disposing.Task, detect.Disposing.Task).WaitAsync(Guard);
            var second = client.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, decode.DisposeCalls);
            Assert.Equal(1, detect.DisposeCalls);
        }
        finally { barrier.TrySetResult(); }
        await first.WaitAsync(Guard);
    }

    /// <summary>P8 5.4/5.7: Synchrone Kill-/Dispose-Callbacks sehen den schon publizierten Abschluss.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationReleasePublishesItsTaskBeforeCallingForeignCode(bool fromStream)
    {
        // Gezielte Gegenprobe der privaten Besitzerklasse, ohne ihre Produktionssichtbarkeit zu erweitern.
        var type = typeof(RecognizerSupervisor).GetNestedType("Generation", System.Reflection.BindingFlags.NonPublic)!;
        var generation = Activator.CreateInstance(type, nonPublic: true)!;
        Task End() => (Task)type.GetMethod("EndAsync")!.Invoke(generation, null)!;
        Task? reentered = null;
        Task? repeated = null;
        var callbacks = 0;
        void Callback()
        {
            if (Interlocked.Increment(ref callbacks) != 1) return;
            reentered = End();
            repeated = End();
        }
        var stream = new GatedStream(new DuplexPipe("reentrant-dispose").App, false);
        if (fromStream)
        {
            stream.OnDispose = Callback;
            await (Task<bool>)type.GetMethod("OwnAsync")!.Invoke(generation, new object[] { stream })!;
        }
        else
        {
            var launched = new LaunchedRecognizer(1, Task.CompletedTask,
                _ => throw new NotSupportedException(), Callback);
            Assert.True((bool)type.GetMethod("Own")!.Invoke(generation, new object[] { launched })!);
        }
        var ending = End();
        await ending.WaitAsync(Guard);
        Assert.Equal(1, callbacks);
        Assert.Same(ending, reentered);
        Assert.Same(ending, repeated);
        Assert.Same(ending, End());
        if (fromStream) Assert.Equal(1, stream.DisposeCalls);
        else await stream.DisposeAsync();
    }

    private sealed class GatedStream(Stream inner, bool gateFlush, bool disposeThrows = false) : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!gateFlush) await WaitAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (gateFlush) await WaitAsync(cancellationToken);
            await inner.FlushAsync(cancellationToken);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public int DisposeCalls { get; private set; }
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? DisposeBarrier { get; set; }
        public bool BlockDisposeSynchronously { get; set; }
        public Action? OnDispose { get; set; }
        public override async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            OnDispose?.Invoke();
            Disposing.TrySetResult();
            if (DisposeBarrier is { } barrier)
            {
                if (BlockDisposeSynchronously) barrier.GetAwaiter().GetResult();
                else await barrier;
            }
            await inner.DisposeAsync();
            if (disposeThrows) throw new IOException("synthetic-release-failure");
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
