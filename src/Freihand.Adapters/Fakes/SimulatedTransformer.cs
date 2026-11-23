using Freihand.Core.Routing;
using Freihand.Core.Transform;

namespace Freihand.Adapters.Fakes;

/// <summary>Verhalten der Umformattrappe; alle Zeiten auf der Uhr der Simulation.</summary>
public sealed record SimulatedTransformOptions
{
    public TransformRoute Route { get; init; } = TransformRoute.Local;

    /// <summary>Dauer jedes Auftrags.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    /// <summary>Zeiträume, in denen der Adapter ausfällt; danach ist er wieder da (WE-10).</summary>
    public IReadOnlyList<OutageWindow> Outages { get; init; } = [];

    /// <summary>Nach so vielen Aufträgen bricht der Adapter jeden weiteren ab.</summary>
    public int? AbortAfterCalls { get; init; }

    /// <summary>Erfundene Umformung; ohne Angabe bleibt der Text unverändert.</summary>
    public Func<TransformRequest, string>? Transform { get; init; }
}

/// <summary>
/// Umformattrappe mit einstellbarer Verzögerung, Ausfall und Wiederkehr sowie Abbruch, deterministisch
/// mit <see cref="VirtualClock"/>.
/// </summary>
public sealed class SimulatedTransformer(ISimulationClock clock, SimulatedTransformOptions? options = null) : ITextTransformer
{
    private readonly ISimulationClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly SimulatedTransformOptions _options = options ?? new SimulatedTransformOptions();
    private int _calls;

    public TransformRoute Route => _options.Route;

    public bool IsAvailable => !_options.Outages.Any(o => o.Covers(_clock.Now));

    /// <summary>Anzahl Aufträge, auch abgewiesener.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public async Task<TransformResult> TransformAsync(TransformRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var call = Interlocked.Increment(ref _calls);
        if (call > _options.AbortAfterCalls)
        {
            throw new AdapterFailureException(AdapterFailureKind.Aborted, $"Auftrag {call} vom Adapter abgebrochen.");
        }

        EnsureAvailable();
        await _clock.DelayAsync(_options.Delay, cancellationToken).ConfigureAwait(false);
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();

        return new TransformResult(_options.Transform?.Invoke(request) ?? request.Text);
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new AdapterFailureException(
                AdapterFailureKind.Unavailable,
                $"Umformweg {Route} um {_clock.Now} nicht erreichbar.");
        }
    }
}
