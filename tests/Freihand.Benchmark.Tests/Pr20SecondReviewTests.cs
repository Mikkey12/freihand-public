namespace Freihand.Benchmark.Tests;

// Reale Benchmark-Aufrufe; alle Daten sind künstlich.
public sealed class Pr20SecondReviewTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    public void D01_HalfSampleToleranceDoesNotSwallowAWholeSample(int delta, int count)
    {
        var nextStart = (64000 + 9600 + delta) / 16000.0;
        var parts = Sentences([(1, 4), (nextStart, 6)], 25, 7);
        Assert.Equal(count, parts.Count);
    }

    [Fact]
    public void D02_LengthCutLatencyIsNotAGeneralUpperBound()
    {
        var part = Sentences([(0, 20)], 10, 21)[0];
        const double decode = 0.5;
        var benchmarkLatency = part.Available + decode - part.End;
        // P3/T30: Wörter [k,k+1), C=10, CutGuard=0.3 => letzter übernommener Wortschluss bei 9.
        var p3Latency = 10 + decode - 9;
        Assert.Equal(0.5, benchmarkLatency);
        Assert.Equal(1.5, p3Latency);
        Assert.True(benchmarkLatency < p3Latency);
    }

    [Fact]
    public void D03_KnownSleepDoesNotEraseUnknownMeasurementStatus()
    {
        var result = new SimulationResult(10,
            [new StepRecord(1, 1, 2, 1, 0, null, 0.2), new StepRecord(2, 2, 3, 1, 0, null, null)],
            [], null);
        var report = Report.Create(result, null);
        Assert.True(report.StandbyUnknown);
        Assert.Equal(0.2, report.SuspendedSeconds);
    }

    private static List<SentenceSimulation.Sentence> Sentences(IReadOnlyList<(double Start, double End)> regions, double max, double total) =>
        [.. new SentenceSimulation(new FakeTranscriber(), new SentenceOptions(0.6, max), new FakeSegmenter()).Sentences(regions, total)];

    private sealed class FakeSegmenter : ISpeechSegmenter
    {
        public string Name => "synthetisch";
        public IReadOnlyList<(double Start, double End)> Detect(float[] audio) => [];
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public Task<IReadOnlyList<string>> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
