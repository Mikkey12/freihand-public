using System.Runtime.CompilerServices;
using Freihand.Core.Quality;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;

namespace Freihand.Adapters.Fakes;

/// <summary>Verhalten der Erkennungsattrappe; alle Zeiten auf der Uhr der Simulation.</summary>
public sealed record SimulatedRecognitionOptions
{
    public RecognitionRoute Route { get; init; } = RecognitionRoute.Device;

    public RecognitionCapabilities Capabilities { get; init; } = new(
        PartialResults: true,
        FixedSegments: true,
        VocabularyHints: false,
        Cancellation: true,
        MaxSessionDuration: null,
        DataRegion: null,
        ModelVersion: "simulated");

    /// <summary>Feste Verzögerung vor jedem Zwischenstand, z. B. Netz oder Modellstart; zählt nicht als Rechenzeit.</summary>
    public TimeSpan ResponseDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Rechenzeit je Sekunde berechnetem Audio. Über 1 ist langsamer als Echtzeit und erzeugt Rückstand (ER-05).
    /// </summary>
    public double ProcessingFactor { get; init; }

    /// <summary>
    /// Wie viel bereits gehörtes Audio jedes Stück erneut berechnet wird, wie bei einer Live-Erkennung mit
    /// gleitendem Fenster. Zählt zur Rechenzeit (QM-02: inklusive Neuberechnungen).
    /// </summary>
    public TimeSpan Recompute { get; init; } = TimeSpan.Zero;

    /// <summary>Zeiträume, in denen der Adapter ausfällt; danach ist er wieder da (WE-10).</summary>
    public IReadOnlyList<OutageWindow> Outages { get; init; } = [];

    /// <summary>Nach so vielen Stücken bricht der Adapter die Sitzung von sich aus ab.</summary>
    public int? AbortAfterChunks { get; init; }
}

/// <summary>Eine Rechnung der Attrappe, nur Metadaten.</summary>
/// <param name="ComputedAudio">Berechnetes Audio: neues Stück plus erneut berechnetes.</param>
public sealed record ProcessingRecord(
    int Session,
    int ChunkIndex,
    TimeSpan AudioStart,
    TimeSpan AudioEnd,
    TimeSpan ComputedAudio,
    TimeSpan StartedAt,
    TimeSpan FinishedAt)
{
    public TimeSpan ComputeTime => FinishedAt - StartedAt;

    /// <summary>Rückstand: wie weit das Ergebnis hinter dem Ende des gesprochenen Audios liegt (ER-05).</summary>
    public TimeSpan Backlog => FinishedAt - AudioEnd;

    public ComputeSample ToComputeSample() => new(AudioStart, ComputeTime);
}

/// <summary>
/// Erkennungsattrappe mit einstellbarer Verzögerung, langsamer Verarbeitung, Ausfall und Wiederkehr
/// sowie Abbruch, deterministisch mit <see cref="VirtualClock"/>. Liefert je Audiostück den nächsten
/// Zwischenstand aus dem Skript; ist es zu Ende, rechnet sie weiter, ohne etwas zu liefern.
/// </summary>
public sealed class SimulatedRecognitionEngine : IRecognitionEngine
{
    private readonly ISimulationClock _clock;
    private readonly IReadOnlyList<RecognitionUpdate> _script;
    private readonly SimulatedRecognitionOptions _options;
    private readonly List<ProcessingRecord> _trace = [];
    private readonly Lock _gate = new();
    private int _sessions;

    public SimulatedRecognitionEngine(
        ISimulationClock clock,
        IEnumerable<RecognitionUpdate> script,
        SimulatedRecognitionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(script);

        _clock = clock;
        _script = [.. script];
        _options = options ?? new SimulatedRecognitionOptions();
    }

    public RecognitionRoute Route => _options.Route;

    public RecognitionCapabilities Capabilities => _options.Capabilities;

    /// <summary>Ob der Adapter jetzt erreichbar ist.</summary>
    public bool IsAvailable => !_options.Outages.Any(o => o.Covers(_clock.Now));

    /// <summary>Anzahl begonnener Sitzungen, auch abgewiesener.</summary>
    public int Sessions => Volatile.Read(ref _sessions);

    /// <summary>Alle Rechnungen aller Sitzungen, in zeitlicher Reihenfolge.</summary>
    public IReadOnlyList<ProcessingRecord> Trace
    {
        get
        {
            lock (_gate)
            {
                return [.. _trace];
            }
        }
    }

    public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        RecognitionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var session = Interlocked.Increment(ref _sessions);
        EnsureAvailable();

        var index = 0;
        await foreach (var chunk in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Abbrechen wirkt auch, wenn die Audioquelle den Token nicht beachtet (BE-06)
            cancellationToken.ThrowIfCancellationRequested();

            if (index >= _options.AbortAfterChunks)
            {
                throw new AdapterFailureException(
                    AdapterFailureKind.Aborted,
                    $"Sitzung nach {index} Stücken vom Adapter abgebrochen.");
            }

            EnsureAvailable();

            var duration = SimulatedAudio.DurationOf(chunk);
            var recomputed = chunk.Offset < _options.Recompute ? chunk.Offset : _options.Recompute;
            var computed = duration + recomputed;
            var startedAt = _clock.Now;

            await _clock.DelayAsync(computed * _options.ProcessingFactor, cancellationToken).ConfigureAwait(false);

            // Ausfall während der Rechnung: kein Ergebnis für dieses Stück (WE-10)
            EnsureAvailable();

            lock (_gate)
            {
                _trace.Add(new ProcessingRecord(
                    session, index, chunk.Offset, chunk.Offset + duration, computed, startedAt, _clock.Now));
            }

            if (_options.ResponseDelay > TimeSpan.Zero)
            {
                await _clock.DelayAsync(_options.ResponseDelay, cancellationToken).ConfigureAwait(false);
            }

            if (index < _script.Count)
            {
                yield return _script[index];
            }

            index++;
        }
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new AdapterFailureException(
                AdapterFailureKind.Unavailable,
                $"Erkennungsweg {Route} um {_clock.Now} nicht erreichbar.");
        }
    }
}
