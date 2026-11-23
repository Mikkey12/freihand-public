using Freihand.Adapters.Recognition;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;

namespace Freihand.Benchmark.Tests;

/// <summary>QM-01, RM05/RM07/RM09 und ER-17: Messhilfen mit synthetischen Daten und vorgegebenen Zeitwerten.</summary>
public sealed class ChainFollowupTests
{
    [Fact]
    public void PreviewBeforeSpeechDoesNotHideTheFirstActualPreview()
    {
        var first = Chain.FirstPartial(null, "synthetisch", 0.5, 1);
        Assert.Null(first);
        first = Chain.FirstPartial(first, string.Empty, 1.2, 1);
        Assert.Null(first);
        first = Chain.FirstPartial(first, "synthetisch", 1.8, 1);
        first = Chain.FirstPartial(first, "synthetisch", 2.0, 1);
        Assert.Equal(0.8, Chain.CreateReport(new SimulationResult(3, [], [], first), 1).FirstPartialSeconds!.Value, 8);
    }

    [Fact]
    public async Task SynchronousReaderFailureIsInsideTheCaptureLifetime()
    {
        var capture = new Capture();
        var error = await Record.ExceptionAsync(() => Chain.WithCaptureAsync(capture,
            () => { _ = capture.ReadAsync(); return Task.CompletedTask; },
            token => Task.Delay(Timeout.InfiniteTimeSpan, token)));
        Assert.IsType<NotSupportedException>(error);
        Assert.True(capture.StopCalls > 0);
        Assert.Equal(1, capture.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryDecodeAttemptContributesItsTime(bool unavailable)
    {
        var now = TimeSpan.Zero;
        var calls = 0;
        var inner = new Decoder(() =>
        {
            now += TimeSpan.FromMilliseconds(++calls == 1 ? 100 : 20);
            if (calls == 1)
                throw unavailable ? new RecognizerUnavailableException() : new RecognizerException(RecognizerErrorCode.DecoderError);
            return new RecognitionHypothesis(TimeSpan.Zero, TimeSpan.FromSeconds(1), []);
        });
        var timed = new Chain.TimedDecoder(inner, () => now);
        var options = new RecognitionOptions("de-CH", []);
        Assert.NotNull(await Record.ExceptionAsync(() => timed.DecodeAsync(new float[16_000], TimeSpan.Zero, options)));
        await timed.DecodeAsync(new float[16_000], TimeSpan.Zero, options);
        Assert.Equal(2, timed.Steps.Count);
        Assert.Equal(0.12, timed.Steps.Sum(step => step.DecodeSeconds), 8);
        Assert.Equal(1, timed.Failures);
        Assert.Null(timed.Steps[0].LastSegmentEndSeconds);
        Assert.Equal(0, timed.Steps[0].Segments);
    }

    [Fact]
    public void EveryDetectAttemptContributesItsTimeAndTheCombinedFactor()
    {
        var now = TimeSpan.Zero;
        var calls = 0;
        var timed = new Chain.TimedDetector(new Detector(() =>
        {
            now += TimeSpan.FromMilliseconds(++calls == 1 ? 100 : 20);
            if (calls == 1) throw new RecognizerUnavailableException();
            return [];
        }), () => now);
        Assert.Throws<RecognizerUnavailableException>(() => timed.Detect(new float[16_000], TimeSpan.Zero));
        timed.Detect(new float[16_000], TimeSpan.Zero);
        var summary = timed.Summary(1);
        Assert.Equal(2, summary.Calls);
        Assert.Equal(1, summary.Failures);
        Assert.Equal(0.12, summary.TotalSeconds, 8);
        Assert.Equal(0.12, summary.ShareOfAudio, 8);
        var report = Chain.CreateReport(new SimulationResult(1, [], [], null, summary.TotalSeconds), null);
        Assert.Equal(0, report.RealTimeFactor);
        Assert.Equal(0.12, report.RealTimeFactorWithSegmentation, 8);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 1.8)]
    [InlineData(0.5, 1.3)]
    [InlineData(2.0, null)]
    public void FirstPartialUsesTheVisibleP3ResultAndAMarkedSpeechStart(double? start, double? expected)
    {
        // Eine Decoderantwort nach 0,7 s war noch nicht als Text freigegeben. Sichtbar erst bei 1,8 s.
        var result = new SimulationResult(3, [new StepRecord(1, 0.6, 0.7, 0.1, 1, 0.8, null)], [], 1.8);
        var report = Chain.CreateReport(result, start);
        if (expected is { } value) Assert.Equal(value, report.FirstPartialSeconds!.Value, 8);
        else Assert.Null(report.FirstPartialSeconds);
    }

    [Fact]
    public void RepetitionIsClippedAndHasExactlyTwoSecondsOfSilence()
    {
        float[] source = [0.5f, -0.5f];
        var chunks = Chain.PlaybackChunks(source, 32_005).ToList();
        var samples = chunks.SelectMany(chunk => Enumerable.Range(0, chunk.Pcm16Mono.Length / 2)
            .Select(i => BitConverter.ToInt16(chunk.Pcm16Mono.Span.Slice(i * 2, 2)))).ToArray();
        Assert.Equal(32_005, samples.Length);
        Assert.Equal(new short[] { 16_384, -16_384 }, samples[..2]);
        Assert.All(samples[2..32_002], sample => Assert.Equal(0, sample));
        Assert.Equal(new short[] { 16_384, -16_384, 0 }, samples[32_002..]);
        Assert.Equal(TimeSpan.FromTicks(32_000 * 625L), chunks[^1].Offset);
        // Der kürzere Zielwert wird ebenfalls exakt eingehalten, ohne vollständige Quellwiederholung.
        Assert.Equal(1, Assert.Single(Chain.PlaybackChunks(source, 1)).Pcm16Mono.Length / 2);
        Assert.Equal(3_200, Assert.Single(Chain.PlaybackChunks(source, 60L * 60 * 16_000).Take(1)).Pcm16Mono.Length / 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureIsAlwaysStoppedAndReleasedAndItsStopTaskIsCancelled(bool consumerFails)
    {
        var capture = new Capture();
        var cancelled = false;
        async Task Signal(CancellationToken token)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        }

        var error = await Record.ExceptionAsync(() => Chain.WithCaptureAsync(capture,
            () => consumerFails ? Task.FromException(new InvalidDataException("synthetisch")) : Task.CompletedTask, Signal));
        Assert.Equal(consumerFails, error is InvalidDataException);
        Assert.True(cancelled);
        Assert.True(capture.StopCalls > 0);
        Assert.Equal(1, capture.DisposeCalls);
    }

    [Fact]
    public async Task CaptureIsReleasedEvenWhenStopFails()
    {
        var capture = new Capture { StopFails = true };
        var error = await Record.ExceptionAsync(() => Chain.WithCaptureAsync(capture, () => Task.CompletedTask,
            token => Task.Delay(Timeout.InfiniteTimeSpan, token)));
        Assert.IsType<IOException>(error);
        Assert.Equal(1, capture.DisposeCalls);
    }

    private sealed class Decoder(Func<RecognitionHypothesis> run) : IHypothesisDecoder
    {
        public Task<RecognitionHypothesis> DecodeAsync(ReadOnlyMemory<float> audio, TimeSpan offset,
            RecognitionOptions options, CancellationToken cancellationToken = default) => Task.FromResult(run());
    }

    private sealed class Detector(Func<IReadOnlyList<SpeechRegion>> run) : ISpeechDetector
    {
        public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset) => run();
    }

    private sealed class Capture : ICaptureSession
    {
        public AudioDevice Device { get; } = new("test", "test");
        public CaptureStatus Status => new(CapturePhase.Stopped, CaptureStopReason.Requested, null, false, TimeSpan.Zero);
        public event EventHandler<CaptureStatus>? StatusChanged { add { } remove { } }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool StopFails { get; init; }
        public IAsyncEnumerable<AudioChunk> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Stop()
        {
            StopCalls++;
            if (StopFails) throw new IOException("synthetisch");
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
