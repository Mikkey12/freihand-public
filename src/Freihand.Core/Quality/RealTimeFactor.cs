namespace Freihand.Core.Quality;

/// <summary>
/// Eine Rechnung der Erkennung, nur als Metadaten (QM-02).
/// </summary>
/// <param name="AudioStart">
/// Beginn des neuen Audios, das diese Rechnung erstmals abdeckt, ab Sitzungsbeginn. Danach richtet sich
/// das Fenster, zu dem die Rechnung zählt.
/// </param>
/// <param name="ComputeTime">Rechenzeit dieser Rechnung, auch wenn sie älteres Audio erneut berechnet.</param>
public readonly record struct ComputeSample(TimeSpan AudioStart, TimeSpan ComputeTime);

/// <summary>Echtzeitfaktor eines Fensters.</summary>
public sealed record WindowedRealTimeFactor(TimeWindow Window, double Value);

/// <summary>
/// Echtzeitfaktor (QM-02): gesamte Rechenzeit inklusive Neuberechnungen geteilt durch Audiozeit. Ein
/// Wert unter 1 heisst schneller als Echtzeit; die Abnahme verlangt im Dauerbetrieb unter 0,7 im
/// 95. Perzentil über 10-Minuten-Fenster (Kapitel 16).
/// </summary>
public static class RealTimeFactor
{
    /// <summary>Summe aller Rechenzeiten geteilt durch die Audiozeit der Sitzung.</summary>
    public static double Compute(IEnumerable<TimeSpan> computeTimes, TimeSpan audioDuration)
    {
        ArgumentNullException.ThrowIfNull(computeTimes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(audioDuration, TimeSpan.Zero);

        var total = computeTimes.Aggregate(TimeSpan.Zero, (sum, t) => sum + t);
        return total / audioDuration;
    }

    /// <summary>
    /// Echtzeitfaktor je gleitendem Fenster (<see cref="TimeWindows"/>): Rechenzeit aller Rechnungen,
    /// deren <see cref="ComputeSample.AudioStart"/> im Fenster liegt, geteilt durch die Fensterlänge.
    /// </summary>
    public static IReadOnlyList<WindowedRealTimeFactor> PerWindow(
        IEnumerable<ComputeSample> samples,
        TimeSpan audioDuration,
        TimeSpan window,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var all = samples.ToArray();
        return
        [
            .. TimeWindows.Create(audioDuration, window, step).Select(w => new WindowedRealTimeFactor(
                w,
                Compute(
                    all.Where(s => TimeWindows.Includes(w, s.AudioStart, audioDuration)).Select(s => s.ComputeTime),
                    w.Duration))),
        ];
    }
}
