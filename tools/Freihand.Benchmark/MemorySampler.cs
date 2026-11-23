using System.ComponentModel;
using System.Diagnostics;

namespace Freihand.Benchmark;

internal sealed record MemoryReading(int ProcessId, long? StartUtcTicks, int Generation, long? WorkingSetBytes);
internal sealed record MemoryPoint(double ElapsedSeconds, MemoryReading Benchmark, MemoryReading Recognizer);
internal sealed record MemoryGrowth(string Status, long? GrowthBytes, long? StartBytes = null, long? EndBytes = null, long? MaximumBytes = null);
internal sealed record MemoryReport(double IntervalSeconds, string Metric, string Unit, long LimitBytes,
    double WindowSeconds, MemoryGrowth Benchmark, MemoryGrowth Recognizer,
    string BenchmarkLimitation, IReadOnlyList<MemoryPoint> Samples)
{
    public string GrowthDefinition => "Maximum sampled increase from any preceding minimum within the last 30 minutes; boundary sample at most 10 seconds earlier.";
    public double MaximumGapSeconds => 10;
    public double MinimumDurationSeconds => 3600;
    public string Scope => "Memory criterion only; sampling begins after model loading and audio preparation.";
}

/// <summary>RM07: aktuelle Belegung, nicht Spitzenwerte. Fehlende Werte bleiben unbekannt.</summary>
internal sealed class MemorySampler(Func<(MemoryReading Benchmark, MemoryReading Recognizer)> read,
    TimeProvider time) : IAsyncDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly long _start = time.GetTimestamp();
    private readonly List<MemoryPoint> _points = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;
    private bool _finished;

    public void Start()
    {
        if (_running is not null || _finished) throw new InvalidOperationException();
        Take();
        _running = RunAsync();
    }

    // RM07: getrennt aufrufbar mit Testuhr; keine echte Wartezeit in Gegenproben.
    internal void Take()
    {
        var elapsed = time.GetElapsedTime(_start).TotalSeconds;
        var values = read();
        _points.Add(new MemoryPoint(elapsed, values.Benchmark, values.Recognizer));
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                // Kein Nachholen verpasster Takte: höchstens ein Wert je Intervall.
                await Task.Delay(Interval, time, _stop.Token);
                Take();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task<MemoryReport> FinishAsync()
    {
        if (!_finished)
        {
            _finished = true;
            await _stop.CancelAsync();
            if (_running is not null) await _running;
            Take();
        }
        return Report(_points);
    }

    internal static MemoryReport Report(IReadOnlyList<MemoryPoint> points) => new(
        Interval.TotalSeconds, "WorkingSet64", "MB = 1,000,000 bytes", 50_000_000, 1800,
        Evaluate(points, point => point.Benchmark), Evaluate(points, point => point.Recognizer),
        "Benchmark retains decoder steps and fixed text; this is not isolated application memory. RM07 also requires text/error checks.",
        points.ToArray());

    private static MemoryGrowth Evaluate(IReadOnlyList<MemoryPoint> points, Func<MemoryPoint, MemoryReading> select)
    {
        if (points.Count < 2 || points[^1].ElapsedSeconds < 3600) return new("InsufficientDuration", null);
        var end = points[^1].ElapsedSeconds;
        // Eine Abtastung unmittelbar vor der Fenstergrenze wird mit ausgewiesen, nie interpoliert.
        var first = -1;
        for (var i = 0; i < points.Count; i++)
            if (points[i].ElapsedSeconds <= end - 1800) first = i;
        if (first < 0 || end - 1800 - points[first].ElapsedSeconds > Interval.TotalSeconds * 2)
            return new("MissingBoundary", null);
        var initial = select(points[first]);
        for (var i = first; i < points.Count; i++)
        {
            var current = select(points[i]);
            if (current.WorkingSetBytes is null || current.StartUtcTicks is null) return new("UnknownSample", null);
            if (current.ProcessId != initial.ProcessId || current.StartUtcTicks != initial.StartUtcTicks || current.Generation != initial.Generation)
                return new("ProcessChanged", null);
            if (i > first && (points[i].ElapsedSeconds < points[i - 1].ElapsedSeconds ||
                points[i].ElapsedSeconds - points[i - 1].ElapsedSeconds > Interval.TotalSeconds * 2))
                return new("SamplingGap", null);
        }
        var startBytes = initial.WorkingSetBytes!.Value;
        var endBytes = select(points[^1]).WorkingSetBytes!.Value;
        var minimum = startBytes;
        var maximum = startBytes;
        long growth = 0;
        foreach (var point in points.Skip(first))
        {
            var bytes = select(point).WorkingSetBytes!.Value;
            minimum = Math.Min(minimum, bytes);
            maximum = Math.Max(maximum, bytes);
            growth = Math.Max(growth, bytes - minimum);
        }
        // RM07: MB ausdrücklich dezimal (SI); nur das Speicherkriterium, keine gesamte RM07-Abnahme.
        return new(growth > 50_000_000 ? "ExceedsLimit" : "WithinLimit", growth, startBytes, endBytes, maximum);
    }

    internal static MemoryReading ReadProcess(int processId, int generation)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var start = process.StartTime.ToUniversalTime().Ticks;
            process.Refresh();
            return new(processId, start, generation, process.WorkingSet64);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return new(processId, null, generation, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await FinishAsync(); }
        finally { _stop.Dispose(); }
    }
}
