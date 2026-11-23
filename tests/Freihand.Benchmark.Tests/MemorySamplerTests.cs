namespace Freihand.Benchmark.Tests;

/// <summary>RM07: Gegenproben ohne Modell, Audio oder echte Wartezeit.</summary>
public sealed class MemorySamplerTests
{
    private static MemoryReading Reading(long bytes = 100_000_000, int generation = 1) => new(42, 100, generation, bytes);
    private static List<MemoryPoint> Hour(long growth = 50_000_000) => Enumerable.Range(0, 721)
        .Select(i => new MemoryPoint(i * 5, Reading(i == 720 ? 100_000_000 + growth : 100_000_000), Reading())).ToList();

    [Theory]
    [InlineData(50_000_000, "WithinLimit")]
    [InlineData(50_000_001, "ExceedsLimit")]
    [InlineData(-1, "WithinLimit")]
    public void M01_DecimalLimitAndLastThirtyMinutes(long growth, string status)
    {
        var points = Hour(growth);
        points[0] = points[0] with { Benchmark = Reading(900_000_000) };
        var report = MemorySampler.Report(points);
        Assert.Equal(status, report.Benchmark.Status);
        Assert.Equal(Math.Max(0, growth), report.Benchmark.GrowthBytes);
        Assert.Equal("WithinLimit", report.Recognizer.Status);
        Assert.Equal(50_000_000, report.LimitBytes);
        Assert.Equal("WorkingSet64", report.Metric);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("generation")]
    [InlineData("gap")]
    public void M02_NoFalsePassOnMissingSamplesOrProcessChanges(string mutation)
    {
        var points = Hour();
        var point = points[500];
        points[500] = mutation switch
        {
            "missing" => point with { Recognizer = point.Recognizer with { WorkingSetBytes = null } },
            "pid" => point with { Recognizer = point.Recognizer with { ProcessId = 43 } },
            "start" => point with { Recognizer = point.Recognizer with { StartUtcTicks = 101 } },
            "generation" => point with { Recognizer = Reading(generation: 2) },
            _ => point,
        };
        if (mutation == "gap") points.RemoveRange(500, 3);
        var result = MemorySampler.Report(points);
        Assert.NotEqual("WithinLimit", result.Recognizer.Status);
        Assert.Null(result.Recognizer.GrowthBytes);
        Assert.Equal("WithinLimit", mutation == "gap" ? MemorySampler.Report(Hour()).Benchmark.Status : result.Benchmark.Status);
    }

    [Fact]
    public void M03_ShortRunHasNoAcceptanceAndSamplesAreCopied()
    {
        var points = Hour();
        points.RemoveAt(points.Count - 1);
        var report = MemorySampler.Report(points);
        Assert.Equal("InsufficientDuration", report.Benchmark.Status);
        Assert.Null(report.Benchmark.GrowthBytes);
        points.Clear();
        Assert.Equal(720, report.Samples.Count);
    }

    [Fact]
    public async Task M04_MonotonicTimeAndProviderAreInjectableAndCleanupIsAwaited()
    {
        var clock = new Clock();
        var reads = 0;
        await using var sampler = new MemorySampler(() => { reads++; return (Reading(), Reading()); }, clock);
        sampler.Take();
        clock.Timestamp = TimeSpan.FromMinutes(30).Ticks;
        sampler.Take();
        var result = await sampler.FinishAsync();
        Assert.Equal(1800, result.Samples[^1].ElapsedSeconds);
        Assert.Equal(3, reads);
        await sampler.FinishAsync();
        Assert.Equal(3, reads);
    }

    [Fact]
    public void M05_ExitedOrMissingProcessIsUnknown()
    {
        var value = MemorySampler.ReadProcess(int.MaxValue, 3);
        Assert.Null(value.WorkingSetBytes);
        Assert.Null(value.StartUtcTicks);
        Assert.Equal(3, value.Generation);
    }

    [Fact]
    public void M06_TemporaryGrowthCannotHideBehindLowerFinalValue()
    {
        var points = Hour(0);
        points[500] = points[500] with { Benchmark = Reading(151_000_000) };
        var result = MemorySampler.Report(points).Benchmark;
        Assert.Equal("ExceedsLimit", result.Status);
        Assert.Equal(51_000_000, result.GrowthBytes);
        Assert.Equal(100_000_000, result.EndBytes);
        Assert.Equal(151_000_000, result.MaximumBytes);
    }

    [Fact]
    public async Task M07_StartedSamplerCancelsAndDisposesItsTimerBeforeReturning()
    {
        var clock = new Clock();
        var reads = 0;
        await using var sampler = new MemorySampler(() => { reads++; return (Reading(), Reading()); }, clock);
        sampler.Start();
        Assert.Equal(1, clock.Timers);
        await sampler.FinishAsync();
        Assert.Equal(0, clock.Timers);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void M08_RegularSchedulerDriftDoesNotInvalidateTheWindowBoundary()
    {
        // RM07: 5,1-s-Abtastung bleibt innerhalb der dokumentierten 10-s-Lückengrenze.
        var points = Enumerable.Range(0, 706)
            .Select(i => new MemoryPoint(i * 5.1, Reading(), Reading())).ToList();
        points.Add(new MemoryPoint(3600.25, Reading(), Reading()));
        Assert.Equal("WithinLimit", MemorySampler.Report(points).Recognizer.Status);
        points.RemoveAll(p => p.ElapsedSeconds > 1788 && p.ElapsedSeconds < 1801);
        Assert.NotEqual("WithinLimit", MemorySampler.Report(points).Recognizer.Status);
    }

    private sealed class Clock : TimeProvider
    {
        public long Timestamp { get; set; }
        public int Timers { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers++;
            return new Timer(this);
        }
        private sealed class Timer(Clock clock) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() { if (!_disposed) { _disposed = true; clock.Timers--; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch; // Wanduhr bleibt stehen.
    }
}
