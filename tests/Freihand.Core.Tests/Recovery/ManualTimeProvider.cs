namespace Freihand.Core.Tests.Recovery;

/// <summary>
/// Zeit für Tests, die nur mit <see cref="Advance"/> weiterläuft. Fällige Zeitgeber laufen dabei der
/// Reihe nach im Aufrufer, ohne echte Wartezeit und ohne zusätzliches Paket.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        var target = _now + delta;
        while (_timers.Where(t => t.DueAt is { } due && due <= target).MinBy(t => t.DueAt) is { } next)
        {
            _now = next.DueAt!.Value;
            next.Fire();
        }

        _now = target;
    }

    /// <summary>Stellt einen harten Abbruch nach: alle Zeitgeber verschwinden, ohne dass noch etwas läuft.</summary>
    public void DropAllTimers() => _timers.Clear();

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            _period = period;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : DueAt + _period;
            callback(state);
        }

        public void Dispose()
        {
            DueAt = null;
            owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
