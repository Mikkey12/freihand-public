namespace Freihand.Adapters.Fakes;

/// <summary>Uhr der Attrappen. Zeiten zählen ab Beginn der Simulation.</summary>
public interface ISimulationClock
{
    TimeSpan Now { get; }

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>
/// Virtuelle Uhr für deterministische Tests: Eine Verzögerung stellt die Uhr sofort vor, ohne zu
/// warten. Teilen sich Audioquelle und Adapter eine Uhr, laufen sie nacheinander ab wie eine
/// Verarbeitung, die das nächste Audiostück erst nach dem vorherigen holt; so entsteht bei langsamer
/// Verarbeitung ein Rückstand (ER-05).
/// </summary>
public sealed class VirtualClock : ISimulationClock
{
    private long _ticks;

    public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Add(ref _ticks, delay.Ticks);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Echte Uhr, z. B. für eine Vorführung ohne Modell; Verzögerungen warten wirklich.</summary>
public sealed class SystemClock(TimeProvider? timeProvider = null) : ISimulationClock
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly long _start = (timeProvider ?? TimeProvider.System).GetTimestamp();

    public TimeSpan Now => _time.GetElapsedTime(_start);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        new(Task.Delay(delay, _time, cancellationToken));
}

public static class SimulationClockExtensions
{
    /// <summary>Wartet bis zum Zeitpunkt <paramref name="time"/>; ist er vorbei, sofort.</summary>
    public static ValueTask WaitUntilAsync(this ISimulationClock clock, TimeSpan time, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var remaining = time - clock.Now;
        return clock.DelayAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
    }
}

/// <summary>Zeitraum, in dem ein Adapter ausfällt; ab <see cref="Until"/> ist er wieder da (WE-10).</summary>
public readonly record struct OutageWindow(TimeSpan From, TimeSpan Until)
{
    public bool Covers(TimeSpan time) => time >= From && time < Until;
}
