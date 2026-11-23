namespace Freihand.Core.Tests.TestKit;

/// <summary>
/// Threadsichere Uhr für Tests, die nur mit <see cref="Advance"/> weiterläuft (P8), Wanduhr und monotone Zeitbasis
/// gemeinsam. Fällige Zeitgeber laufen der Reihe
/// nach im Aufrufer von <see cref="Advance"/>, ohne echte Wartezeit; so hängt kein Test von der Rechnergeschwindigkeit
/// ab. Mit <see cref="WaitForTimersAsync"/> wartet ein Test, bis der Prüfling seine Zeitgeber gestellt hat, bevor er die
/// Uhr vorstellt.
/// </summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Timer> _timers = [];
    private readonly DateTimeOffset _start = start;
    private DateTimeOffset _now = start;
    private TaskCompletionSource _changed = NewSignal();

    public ManualClock()
        : this(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero))
    {
    }

    /// <summary>Zeit seit dem Start der Uhr.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _now - _start;
            }
        }
    }

    /// <summary>Zahl der gestellten Zeitgeber (mit Fälligkeit).</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.DueAt is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>Monotone Zeitbasis in Ticks, läuft mit <see cref="Advance"/> mit (Codex-Gegenprobe C02).</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Stellt die Uhr vor; fällige Zeitgeber laufen in zeitlicher Reihenfolge im Aufrufer.</summary>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + delta;
        }

        while (true)
        {
            Timer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueAt is { } due && due <= target).MinBy(t => t.DueAt);
                if (next is null)
                {
                    _now = target;
                    break;
                }

                _now = next.DueAt!.Value;
                next.Advance();
            }

            next.Invoke();
        }

        Signal();
    }

    /// <summary>
    /// Wartet, bis mindestens <paramref name="count"/> Zeitgeber gestellt sind, die bis <paramref name="dueWithin"/> ab jetzt
    /// fällig werden (ohne Angabe: beliebig spät). Die echte Zeit dient nur als Schutz vor einem Hänger, nie als Prüfung.
    /// </summary>
    public async Task WaitForTimersAsync(int count = 1, TimeSpan? dueWithin = null)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var limit = dueWithin is { } within ? _now + within : DateTimeOffset.MaxValue;
                if (_timers.Count(t => t.DueAt is { } due && due <= limit) >= count)
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(guard.Token);
        }
    }

    /// <summary>
    /// Wartet, bis ein Zeitgeber gestellt ist, der genau <paramref name="dueIn"/> ab jetzt fällig wird. Anders als
    /// <see cref="WaitForTimersAsync"/> zählt dabei kein früher gestellter Zeitgeber mit, der vorher fällig wird, etwa ein
    /// nicht freigegebener Zeitgeber eines schon beantworteten Aufrufs (R51). Die echte Zeit dient nur als Schutz vor
    /// einem Hänger.
    /// </summary>
    public async Task WaitForTimerDueInAsync(TimeSpan dueIn)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var due = _now + dueIn;
                if (_timers.Any(t => t.DueAt == due))
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(guard.Token);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _changed;
            _changed = NewSignal();
        }

        old.TrySetResult();
    }

    private sealed class Timer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        /// <summary>Nur unter der Sperre der Uhr lesen und schreiben.</summary>
        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                _period = period;
            }

            owner.Signal();
            return true;
        }

        /// <summary>Unter der Sperre: nächste Fälligkeit setzen, bevor der Rückruf läuft.</summary>
        public void Advance() =>
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : DueAt + _period;

        public void Invoke() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }

            owner.Signal();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
