using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Freihand.Benchmark;

/// <summary>
/// Misst die verstrichene Zeit eines Schritts und erkennt, ob das Gerät währenddessen geschlafen hat. Stopwatch
/// zählt Standby und Ruhezustand mit, die unverzerrte Interrupt-Zeit von Windows nicht; die Differenz ist die
/// Schlafzeit im Schritt. Ob Modern Standby ebenso erfasst wird, ist nicht geprüft.
/// </summary>
internal readonly partial struct StepTimer
{
    private readonly long _stopwatchStart;
    private readonly ulong _unbiasedStart;

    private StepTimer(long stopwatchStart, ulong unbiasedStart)
    {
        _stopwatchStart = stopwatchStart;
        _unbiasedStart = unbiasedStart;
    }

    public static StepTimer Start() => new(Stopwatch.GetTimestamp(), UnbiasedTicks());

    /// <returns>
    /// Verstrichene Zeit samt Schlafzeit und die Schlafzeit allein, in Sekunden; die Schlafzeit ist leer, wenn Windows
    /// die Interrupt-Zeit nicht liefert. Dann ist Standby unbekannt, nicht ausgeschlossen.
    /// </returns>
    public (double Elapsed, double? Suspended) Stop()
    {
        var elapsed = Stopwatch.GetElapsedTime(_stopwatchStart).TotalSeconds;
        var unbiasedEnd = UnbiasedTicks();
        if (_unbiasedStart == 0 || unbiasedEnd == 0)
        {
            return (elapsed, null);
        }

        var awake = (unbiasedEnd - _unbiasedStart) / 1e7;
        // Unterschiedliche Auflösung der beiden Uhren: kleine Abweichungen sind kein Schlaf
        var suspended = elapsed - awake;
        return (elapsed, suspended > 0.05 ? suspended : 0);
    }

    private static ulong UnbiasedTicks() => QueryUnbiasedInterruptTime(out var ticks) ? ticks : 0;

    // 100-ns-Einheiten seit dem Start, ohne Zeit in Standby und Ruhezustand
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
