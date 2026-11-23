namespace Freihand.Core.Quality;

/// <summary>
/// Zeitpunkte einer Testäusserung ab Sitzungsbeginn, nur als Metadaten (QM-02, ER-01).
/// </summary>
/// <param name="UtteranceId">Kennung der Äusserung im Referenzsatz, nie ihr Inhalt.</param>
/// <param name="SpeechStart">Beginn verständlicher Sprache laut Sprachaktivitätserkennung.</param>
/// <param name="SpeechEnd">Sprachende laut Sprachaktivitätserkennung.</param>
/// <param name="FirstPartialAt">Erstes Teilergebnis; <c>null</c>, wenn keines kam.</param>
/// <param name="FixedAt">Abschnitt fest; <c>null</c>, wenn er nie fest wurde.</param>
public sealed record UtteranceTiming(
    string UtteranceId,
    TimeSpan SpeechStart,
    TimeSpan SpeechEnd,
    TimeSpan? FirstPartialAt,
    TimeSpan? FixedAt);

/// <summary>Eine Latenz zum Zeitpunkt <paramref name="At"/>; <c>null</c> heisst: Ergebnis fehlt.</summary>
public readonly record struct LatencySample(TimeSpan At, TimeSpan? Latency);

/// <summary>Kennzahlen einer Latenzreihe. Fehlende Ergebnisse zählen nicht in die Zeiten, aber in <see cref="Missing"/>.</summary>
public sealed record LatencySummary(
    int Count,
    int Missing,
    TimeSpan? Mean,
    TimeSpan? Median,
    TimeSpan? P95,
    TimeSpan? Max);

/// <summary>Latenzkennzahlen eines Fensters.</summary>
public sealed record WindowedLatency(TimeWindow Window, LatencySummary Summary);

/// <summary>
/// Latenzen (QM-02, ER-01). Erstes Teilergebnis: ab Beginn verständlicher Sprache. Fest: ab Sprachende
/// plus Pausenschwelle, bei durchgehendem Sprechen spätestens ab 15 s nach Beginn der Äusserung.
/// Kommt ein Ergebnis vor dem Bezugspunkt, ist die Latenz 0.
/// </summary>
public static class Latency
{
    /// <summary>Längste Äusserung, bevor der Bezugspunkt für „fest“ greift (ER-01).</summary>
    public static TimeSpan DefaultMaxUtterance { get; } = TimeSpan.FromSeconds(15);

    public static TimeSpan? FirstPartial(UtteranceTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        return timing.FirstPartialAt is { } at ? NotNegative(at - timing.SpeechStart) : null;
    }

    public static TimeSpan? Fixed(UtteranceTiming timing, TimeSpan pauseThreshold, TimeSpan maxUtterance)
    {
        ArgumentNullException.ThrowIfNull(timing);

        if (timing.FixedAt is not { } at)
        {
            return null;
        }

        var reference = Min(timing.SpeechEnd + pauseThreshold, timing.SpeechStart + maxUtterance);
        return NotNegative(at - reference);
    }

    /// <summary>Mittelwert, Median und 95. Perzentil nach <see cref="Percentile"/>, dazu Maximum.</summary>
    public static LatencySummary Summarize(IEnumerable<TimeSpan?> latencies)
    {
        ArgumentNullException.ThrowIfNull(latencies);

        var all = latencies.ToArray();
        var present = all.Where(l => l is not null).Select(l => l!.Value).ToArray();
        var missing = all.Length - present.Length;

        if (present.Length == 0)
        {
            return new LatencySummary(0, missing, null, null, null, null);
        }

        var mean = TimeSpan.FromTicks((long)present.Average(l => (double)l.Ticks));
        return new LatencySummary(
            present.Length,
            missing,
            mean,
            Percentile.NearestRank(present, 50),
            Percentile.NearestRank(present, 95),
            present.Max());
    }

    /// <summary>Kennzahlen je gleitendem Fenster (<see cref="TimeWindows"/>), zugeordnet nach <see cref="LatencySample.At"/>.</summary>
    public static IReadOnlyList<WindowedLatency> PerWindow(
        IEnumerable<LatencySample> samples,
        TimeSpan sessionDuration,
        TimeSpan window,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var all = samples.ToArray();
        return
        [
            .. TimeWindows.Create(sessionDuration, window, step).Select(w => new WindowedLatency(
                w,
                Summarize(all.Where(s => TimeWindows.Includes(w, s.At, sessionDuration)).Select(s => s.Latency)))),
        ];
    }

    private static TimeSpan NotNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first <= second ? first : second;
}
