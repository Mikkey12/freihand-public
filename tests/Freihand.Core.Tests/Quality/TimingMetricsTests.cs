using Freihand.Core.Quality;

namespace Freihand.Core.Tests.Quality;

public class PercentileTests
{
    [Fact]
    public void NearestRankPicksValueAtCeilingRank()
    {
        var oneToTwenty = Enumerable.Range(1, 20).Select(i => (double)i);
        var oneToTen = Enumerable.Range(1, 10).Select(i => (double)i).Reverse();

        // ⌈0,95 · 20⌉ = 19, ⌈0,95 · 10⌉ = 10, ⌈0,5 · 10⌉ = 5
        Assert.Equal(19, Percentile.NearestRank(oneToTwenty, 95));
        Assert.Equal(10, Percentile.NearestRank(oneToTen, 95));
        Assert.Equal(5, Percentile.NearestRank(oneToTen, 50));
        Assert.Equal(1, Percentile.NearestRank(oneToTen, 1));
    }

    [Fact]
    public void SingleValueAndEmptyInput()
    {
        Assert.Equal(7, Percentile.NearestRank([7.0], 95));
        Assert.Null(Percentile.NearestRank(Array.Empty<double>(), 95));
        Assert.Null(Percentile.NearestRank(Array.Empty<TimeSpan>(), 95));
    }

    [Fact]
    public void WorksForDurations()
    {
        var durations = Enumerable.Range(1, 20).Select(i => TimeSpan.FromMilliseconds(100 * i));

        Assert.Equal(TimeSpan.FromMilliseconds(1900), Percentile.NearestRank(durations, 95));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100.1)]
    public void RejectsPercentileOutsideRange(double percentile) =>
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => Percentile.NearestRank([1.0], percentile));
}

public class TimeWindowsTests
{
    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

    [Fact]
    public void SlidingWindowsEndExactlyAtSessionEnd()
    {
        var windows = TimeWindows.Create(TimeSpan.FromMinutes(25), Ten, Five);

        Assert.Equal([0, 5, 10, 15], windows.Select(w => w.Start.TotalMinutes));
        Assert.All(windows, w => Assert.Equal(Ten, w.Duration));
    }

    [Fact]
    public void AddsLastWindowWhenStepDoesNotFit()
    {
        var windows = TimeWindows.Create(TimeSpan.FromMinutes(23), Ten, Five);

        Assert.Equal([0, 5, 10, 13], windows.Select(w => w.Start.TotalMinutes));
        Assert.Equal(TimeSpan.FromMinutes(23), windows[^1].End);
    }

    [Fact]
    public void ShortSessionIsOneWindow()
    {
        Assert.Equal([new TimeWindow(TimeSpan.Zero, TimeSpan.FromMinutes(4))], TimeWindows.Create(TimeSpan.FromMinutes(4), Ten, Five));
        Assert.Empty(TimeWindows.Create(TimeSpan.Zero, Ten, Five));
    }

    [Fact]
    public void SessionEndBelongsToLastWindowOnly()
    {
        var total = TimeSpan.FromMinutes(20);
        var first = new TimeWindow(TimeSpan.Zero, Ten);
        var last = new TimeWindow(Ten, total);

        Assert.True(TimeWindows.Includes(first, TimeSpan.Zero, total));
        Assert.False(TimeWindows.Includes(first, Ten, total));
        Assert.True(TimeWindows.Includes(last, Ten, total));
        Assert.True(TimeWindows.Includes(last, total, total));
    }
}

public class RealTimeFactorTests
{
    [Fact]
    public void DividesTotalComputeTimeByAudioTime()
    {
        var compute = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) };

        Assert.Equal(0.6, RealTimeFactor.Compute(compute, TimeSpan.FromSeconds(10)), 10);
    }

    [Fact]
    public void IncludesRecomputation()
    {
        // 10 Stücke à 1 s; jedes berechnet zusätzlich 2 s altes Audio neu, Faktor 0,1 je Sekunde:
        // Rechenzeit 0,1 + 0,2 + 8 · 0,3 = 2,7 s auf 10 s Audio
        var compute = Enumerable.Range(0, 10)
            .Select(i => TimeSpan.FromSeconds(0.1 * (1 + Math.Min(i, 2))));

        Assert.Equal(0.27, RealTimeFactor.Compute(compute, TimeSpan.FromSeconds(10)), 10);
    }

    [Fact]
    public void RejectsEmptyAudio() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RealTimeFactor.Compute([TimeSpan.FromSeconds(1)], TimeSpan.Zero));

    [Fact]
    public void ComputesPerWindowByStartOfNewAudio()
    {
        // 20 Minuten in Stücken à 1 Minute: erste 10 Minuten je 18 s Rechenzeit (0,3), dann je 48 s (0,8)
        var samples = Enumerable.Range(0, 20)
            .Select(i => new ComputeSample(TimeSpan.FromMinutes(i), TimeSpan.FromSeconds(i < 10 ? 18 : 48)))
            .ToArray();
        var total = TimeSpan.FromMinutes(20);

        var disjoint = RealTimeFactor.PerWindow(samples, total, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        var sliding = RealTimeFactor.PerWindow(samples, total, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));

        Assert.Equal([0.3, 0.8], disjoint.Select(w => Math.Round(w.Value, 10)));
        Assert.Equal([0.3, 0.55, 0.8], sliding.Select(w => Math.Round(w.Value, 10)));
        Assert.Equal(0.55, RealTimeFactor.Compute(samples.Select(s => s.ComputeTime), total), 10);
    }
}

public class LatencyTests
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void FirstPartialCountsFromSpeechStart()
    {
        var timing = Utterance(speechStart: 2, speechEnd: 5, firstPartial: 3.2, @fixed: null);

        Assert.Equal(TimeSpan.FromSeconds(1.2), Latency.FirstPartial(timing));
        Assert.Null(Latency.Fixed(timing, Pause, Latency.DefaultMaxUtterance));
    }

    [Fact]
    public void FixedCountsFromSpeechEndPlusPause()
    {
        var timing = Utterance(speechStart: 2, speechEnd: 5, firstPartial: null, @fixed: 6.5);

        // Bezugspunkt 5 s + 0,5 s = 5,5 s
        Assert.Equal(TimeSpan.FromSeconds(1), Latency.Fixed(timing, Pause, Latency.DefaultMaxUtterance));
        Assert.Null(Latency.FirstPartial(timing));
    }

    [Fact]
    public void FixedCountsFromFifteenSecondsWhenSpeakingContinuously()
    {
        var timing = Utterance(speechStart: 0, speechEnd: 20, firstPartial: null, @fixed: 16);

        // Bezugspunkt min(20,5 s, 0 s + 15 s) = 15 s (ER-01, Entscheid 03.11.2026)
        Assert.Equal(TimeSpan.FromSeconds(1), Latency.Fixed(timing, Pause, Latency.DefaultMaxUtterance));
    }

    [Fact]
    public void ResultBeforeReferenceIsZero()
    {
        var timing = Utterance(speechStart: 2, speechEnd: 5, firstPartial: 1.5, @fixed: 5.2);

        Assert.Equal(TimeSpan.Zero, Latency.FirstPartial(timing));
        Assert.Equal(TimeSpan.Zero, Latency.Fixed(timing, Pause, Latency.DefaultMaxUtterance));
    }

    [Fact]
    public void SummarizesWithNearestRankAndCountsMissing()
    {
        var latencies = Enumerable.Range(1, 20).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i)).Append(null);

        var summary = Latency.Summarize(latencies);

        Assert.Equal(20, summary.Count);
        Assert.Equal(1, summary.Missing);
        Assert.Equal(TimeSpan.FromSeconds(10.5), summary.Mean);
        Assert.Equal(TimeSpan.FromSeconds(10), summary.Median);
        Assert.Equal(TimeSpan.FromSeconds(19), summary.P95);
        Assert.Equal(TimeSpan.FromSeconds(20), summary.Max);
    }

    [Fact]
    public void SummaryWithoutValues()
    {
        var summary = Latency.Summarize([null, null]);

        Assert.Equal(new LatencySummary(0, 2, null, null, null, null), summary);
    }

    [Fact]
    public void SummarizesPerWindow()
    {
        var samples = new LatencySample[]
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3)),
            new(TimeSpan.FromMinutes(12), TimeSpan.FromSeconds(2)),
            new(TimeSpan.FromMinutes(15), null),
        };

        var windows = Latency.PerWindow(samples, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

        Assert.Equal(2, windows.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), windows[0].Summary.P95);
        Assert.Equal(2, windows[0].Summary.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), windows[1].Summary.P95);
        Assert.Equal(1, windows[1].Summary.Missing);
    }

    private static UtteranceTiming Utterance(double speechStart, double speechEnd, double? firstPartial, double? @fixed) => new(
        "u1",
        TimeSpan.FromSeconds(speechStart),
        TimeSpan.FromSeconds(speechEnd),
        firstPartial is null ? null : TimeSpan.FromSeconds(firstPartial.Value),
        @fixed is null ? null : TimeSpan.FromSeconds(@fixed.Value));
}
