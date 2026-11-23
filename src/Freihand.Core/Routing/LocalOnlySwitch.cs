namespace Freihand.Core.Routing;

/// <summary>
/// Notschalter „nur lokal“ (SI-07): Ist er an, verweigert <see cref="RoutePolicy"/> jede Verarbeitung, die das
/// Gerät verlässt, auch über Heim, für Erkennung und Umformen. Beim Einschalten meldet er alle wartenden externen
/// Aufträge zum Verwerfen und bricht ihr <see cref="PendingJob.DiscardToken"/> ab. Bereits gesendete Daten lassen
/// sich nicht zurückholen.
/// </summary>
public sealed class LocalOnlySwitch
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, PendingJob> _pending = [];
    private volatile bool _engaged;

    /// <summary>Meldet beim Einschalten die wartenden externen Aufträge, die zu verwerfen sind.</summary>
    public event EventHandler<DiscardRequestedEventArgs>? DiscardRequested;

    public bool IsEngaged => _engaged;

    /// <summary>
    /// Meldet einen Auftrag an, der geprüft ist und auf seine Ausführung wartet. Der Auftrag meldet sich mit
    /// <see cref="PendingJob.Dispose"/> ab, sobald er fertig ist. Ist der Schalter schon an und verlässt der Auftrag
    /// das Gerät, ist er sofort verworfen.
    /// </summary>
    public PendingJob Track(RouteRequest route)
    {
        ArgumentNullException.ThrowIfNull(route);

        var job = new PendingJob(this, route);
        lock (_gate)
        {
            if (!(_engaged && route.LeavesDevice))
            {
                _pending.Add(job.Id, job);
                return job;
            }
        }

        job.Discard();
        return job;
    }

    /// <summary>
    /// Schaltet „nur lokal“ ein und gibt die verworfenen externen Aufträge zurück (SI-07). Werfen Rückrufe oder
    /// Meldungen, sind trotzdem alle Aufträge verworfen und gemeldet; danach folgt <see cref="LocalOnlyDiscardException"/>.
    /// </summary>
    public IReadOnlyList<PendingJob> Engage()
    {
        List<PendingJob> discarded;
        lock (_gate)
        {
            _engaged = true;
            discarded = [.. _pending.Values.Where(j => j.Route.LeavesDevice)];
            foreach (var job in discarded)
            {
                _pending.Remove(job.Id);
            }
        }

        // Abbruch und Meldung ausserhalb der Sperre, damit Rückrufe den Schalter wieder nutzen können. Ein Fehler
        // in einem Rückruf darf weder weitere Abbrüche noch die Meldung verhindern (SI-07, Codex-Review R3)
        var errors = new List<Exception>();
        foreach (var job in discarded)
        {
            try
            {
                job.Discard();
            }
            catch (AggregateException e)
            {
                errors.AddRange(e.InnerExceptions);
            }
        }

        if (discarded.Count > 0 && DiscardRequested is { } handlers)
        {
            var args = new DiscardRequestedEventArgs(discarded);
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<DiscardRequestedEventArgs>>())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception e)
                {
                    errors.Add(e);
                }
            }
        }

        return errors.Count == 0 ? discarded : throw new LocalOnlyDiscardException(discarded, errors);
    }

    /// <summary>Schaltet „nur lokal“ aus. Verworfene Aufträge bleiben verworfen.</summary>
    public void Release() => _engaged = false;

    /// <summary>Aufträge, die noch warten; für Anzeige und Tests.</summary>
    public IReadOnlyList<PendingJob> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending.Values];
            }
        }
    }

    internal void Remove(PendingJob job)
    {
        lock (_gate)
        {
            _pending.Remove(job.Id);
        }
    }
}

/// <summary>Ein geprüfter, wartender Auftrag (SI-07). Adapter brechen ab, sobald <see cref="DiscardToken"/> auslöst.</summary>
public sealed class PendingJob : IDisposable
{
    private readonly LocalOnlySwitch _owner;

    // Ohne Zeitgeber und ohne Verknüpfung braucht die Quelle kein Dispose; Cancel bleibt so jederzeit sicher
    private readonly CancellationTokenSource _discard = new();

    internal PendingJob(LocalOnlySwitch owner, RouteRequest route)
    {
        _owner = owner;
        Route = route;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public RouteRequest Route { get; }

    public CancellationToken DiscardToken => _discard.Token;

    public bool IsDiscarded => _discard.IsCancellationRequested;

    /// <summary>Meldet den Auftrag ab, wenn er fertig ist.</summary>
    public void Dispose() => _owner.Remove(this);

    /// <summary>Bricht ab; alle Rückrufe laufen, ihre Fehler kommen gesammelt als <see cref="AggregateException"/>.</summary>
    internal void Discard() => _discard.Cancel(throwOnFirstException: false);
}

public sealed class DiscardRequestedEventArgs(IReadOnlyList<PendingJob> jobs) : EventArgs
{
    public IReadOnlyList<PendingJob> Jobs { get; } = jobs;
}

/// <summary>
/// Beim Einschalten von „nur lokal“ hat ein Rückruf oder eine Meldung einen Fehler geworfen. Der Schalter ist trotzdem
/// an, und alle <see cref="Discarded"/> Aufträge sind verworfen (SI-07); die Fehler stehen in den inneren Ausnahmen.
/// </summary>
public sealed class LocalOnlyDiscardException(IReadOnlyList<PendingJob> discarded, IEnumerable<Exception> errors)
    : AggregateException("Notschalter „nur lokal“ ist an; beim Verwerfen wartender Aufträge sind Fehler aufgetreten (SI-07).", errors)
{
    public IReadOnlyList<PendingJob> Discarded { get; } = discarded;
}
