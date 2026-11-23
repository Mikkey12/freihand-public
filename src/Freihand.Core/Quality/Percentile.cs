namespace Freihand.Core.Quality;

/// <summary>
/// Perzentile nach dem Rangverfahren (nearest rank, QM-02): Wert an Rang ⌈p/100 · n⌉ der aufsteigend
/// sortierten Werte. Das 95. Perzentil ist damit der kleinste gemessene Wert, den mindestens 95 % der
/// Messungen nicht überschreiten, passend zu „in 95 % der Testäusserungen“ (Kapitel 16). Es wird nie
/// zwischen Werten interpoliert.
/// </summary>
public static class Percentile
{
    /// <summary>Gibt <c>null</c> zurück, wenn keine Werte vorliegen.</summary>
    public static double? NearestRank(IEnumerable<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);

        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    /// <summary>Wie <see cref="NearestRank(IEnumerable{double}, double)"/>, für Zeitdauern.</summary>
    public static TimeSpan? NearestRank(IEnumerable<TimeSpan> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);

        var ticks = NearestRank(values.Select(v => (double)v.Ticks), percentile);
        return ticks is null ? null : TimeSpan.FromTicks((long)ticks.Value);
    }
}
