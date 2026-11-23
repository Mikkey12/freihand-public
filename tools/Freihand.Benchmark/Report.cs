using System.Globalization;

namespace Freihand.Benchmark;

/// <summary>Kennzahlen eines Laufs. Die Wortfehlerrate rechnet P6.</summary>
/// <param name="RealTimeFactor">Rechenzeit der Erkennung geteilt durch Audiozeit, ohne Bestimmung der Sprachbereiche.</param>
/// <param name="RealTimeFactorWithSegmentation">Dasselbe samt der vorab bestimmten Sprachbereiche (satzweise).</param>
/// <param name="SuspendedSeconds">Standby oder Ruhezustand während der Rechenschritte; mehr als 0 heisst wiederholen.</param>
/// <param name="StandbyUnknown">Mindestens ein Schritt konnte Standby nicht messen; „kein Standby“ ist dann nicht belegt.</param>
internal sealed record Report(
    double RealTimeFactor,
    double RealTimeFactorWithSegmentation,
    double? FirstPartialSeconds,
    double? FixedLatencyMedianSeconds,
    double? FixedLatencyP95Seconds,
    double MaxBacklogSeconds,
    int Steps,
    int FixedSegments,
    int LengthCuts,
    double SuspendedSeconds,
    bool StandbyUnknown)
{
    public static Report Create(SimulationResult result, double? speechStart)
    {
        var decode = result.Steps.Sum(s => s.DecodeSeconds);
        var latencies = result.Fixed
            .Where(s => !s.AtEnd)
            .Select(s => s.FixedAtSeconds - s.EndSeconds)
            .Order()
            .ToArray();

        // Erster Text nach dem Sprachbeginn: Es zählt nur ein Ergebnis, das über den Sprachbeginn hinaus reicht; Text,
        // den Whisper schon in der Stille davor erfindet (ER-16), zählt nicht
        var firstAfterSpeech = speechStart is { } start
            ? result.Steps.FirstOrDefault(s => s.LastSegmentEndSeconds > start)?.FinishSeconds - start
            : null;

        return new Report(
            RealTimeFactor: result.AudioSeconds > 0 ? decode / result.AudioSeconds : 0,
            RealTimeFactorWithSegmentation: result.AudioSeconds > 0 ? (decode + result.SegmentationSeconds) / result.AudioSeconds : 0,
            FirstPartialSeconds: firstAfterSpeech,
            FixedLatencyMedianSeconds: Percentile(latencies, 0.50),
            FixedLatencyP95Seconds: Percentile(latencies, 0.95),
            MaxBacklogSeconds: result.Steps.Count == 0 ? 0 : result.Steps.Max(s => s.FinishSeconds - s.AudioSeconds),
            Steps: result.Steps.Count,
            FixedSegments: result.Fixed.Count,
            LengthCuts: result.Fixed.Count(s => s.LengthCut),
            SuspendedSeconds: result.Steps.Sum(s => s.SuspendedSeconds ?? 0) + (result.SegmentationSuspendedSeconds ?? 0),
            StandbyUnknown: result.SegmentationSuspendedSeconds is null || result.Steps.Any(s => s.SuspendedSeconds is null));
    }

    public string Summary() => string.Create(CultureInfo.InvariantCulture, $"""
        Echtzeitfaktor:          {RealTimeFactor:F3} (mit Sprachbereichen {RealTimeFactorWithSegmentation:F3})
        erstes Teilergebnis:     {Seconds(FirstPartialSeconds)}
        fest nach Abschnittsende: Median {Seconds(FixedLatencyMedianSeconds)}, p95 {Seconds(FixedLatencyP95Seconds)}
        Rückstand max.:          {MaxBacklogSeconds:F2} s
        Durchläufe / Abschnitte: {Steps} / {FixedSegments}, davon Längenschnitte {LengthCuts}
        {Standby()}
        """);

    private string Standby() =>
        SuspendedSeconds > 0 ? string.Create(CultureInfo.InvariantCulture, $"ACHTUNG: {SuspendedSeconds:F1} s Standby während der Messung, Lauf wiederholen")
        : StandbyUnknown ? "Standby nicht messbar: unbekannt, ob das Gerät geschlafen hat"
        : "kein Standby während der Messung";

    // Perzentil nach dem Rangverfahren (nearest rank)
    private static double? Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static string Seconds(double? value) =>
        value is { } v ? string.Create(CultureInfo.InvariantCulture, $"{v:F2} s") : "–";
}
