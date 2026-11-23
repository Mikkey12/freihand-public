namespace Freihand.Core.Quality;

/// <summary>Ein Zeitfenster in der Sitzung, gemessen ab Beginn der Audiozeit.</summary>
public readonly record struct TimeWindow(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Gleitende Fenster für Dauermessungen (QM-02, Kapitel 16: „über 10-Minuten-Fenster“). Die Fenster
/// beginnen bei 0 und rücken um <c>step</c> vor, solange sie ganz in der Sitzung liegen; ein letztes
/// Fenster endet genau am Sitzungsende, damit das Ende immer abgedeckt ist. Ist die Sitzung kürzer als
/// ein Fenster, gibt es genau ein Fenster über die ganze Sitzung.
/// </summary>
public static class TimeWindows
{
    /// <summary>Fensterlänge für Dauermessungen (QM-02).</summary>
    public static TimeSpan DefaultWindow { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Vorschub der gleitenden Fenster.</summary>
    public static TimeSpan DefaultStep { get; } = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<TimeWindow> Create(TimeSpan total, TimeSpan window, TimeSpan step)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);

        if (total <= TimeSpan.Zero)
        {
            return [];
        }

        if (total <= window)
        {
            return [new TimeWindow(TimeSpan.Zero, total)];
        }

        var windows = new List<TimeWindow>();
        for (var start = TimeSpan.Zero; start + window <= total; start += step)
        {
            windows.Add(new TimeWindow(start, start + window));
        }

        if (windows[^1].End < total)
        {
            windows.Add(new TimeWindow(total - window, total));
        }

        return windows;
    }

    /// <summary>
    /// Ein Zeitpunkt gehört zum Fenster, wenn Start ≤ t &lt; Ende; das Sitzungsende selbst gehört zum
    /// Fenster, das dort endet.
    /// </summary>
    public static bool Includes(TimeWindow window, TimeSpan time, TimeSpan total) =>
        time >= window.Start && (time < window.End || (time == window.End && window.End == total));
}
