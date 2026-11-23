using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.TestKit;

namespace Freihand.Core.Tests.Audio.Kit;

/// <summary>
/// Aufbau für eine <see cref="CaptureSession"/> mit WASAPI-Attrappe und Testuhr (Kapitel 6), auch für die
/// Integrationsabnahme R51. Die Sitzung entsteht beim Stand 0 der Uhr; <see cref="Feed()"/> stellt die Uhr um die Dauer
/// eines Puffers vor und liefert ihn danach, wie WASAPI ein Paket an dessen Ende liefert. Alle Zeitwerte des Produkts
/// laufen über <see cref="Clock"/>; echte Zeit dient nur als Schutz gegen Hänger (<see cref="Guard"/>).
/// </summary>
internal sealed class CaptureRig : IAsyncDisposable
{
    /// <summary>Schutz gegen Hänger, nie eine Prüfung.</summary>
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private bool _closed;

    private CaptureRig(
        ManualClock clock, FakeCaptureFactory factory, CaptureSession session, AudioDevice device, CaptureOptions options, StatusLog log)
    {
        Clock = clock;
        Factory = factory;
        Session = session;
        Device = device;
        Options = options;
        Statuses = log;
        Signal = new SignalSource(factory.Format);
    }

    /// <summary>Das gewählte Gerät der Tests.</summary>
    public static AudioDevice Headset { get; } = new("{0.0.1.00000000}.{6c1f0b1e-headset}", "Headset");

    /// <summary>Übliches Format im gemeinsamen Modus von WASAPI: 48 kHz, Stereo, Float32.</summary>
    public static CaptureFormat DeviceFormat { get; } = new(48_000, 2, CaptureSampleFormat.Float32);

    /// <summary>16 kHz mono PCM16: wird bitgenau durchgereicht, ohne Nachlauf (Kapitel 5.2).</summary>
    public static CaptureFormat NativeFormat { get; } = new(16_000, 1, CaptureSampleFormat.Pcm16);

    /// <summary>Länge eines Gerätepuffers in den Tests.</summary>
    public static TimeSpan BufferLength { get; } = TimeSpan.FromMilliseconds(100);

    public ManualClock Clock { get; }

    public FakeCaptureFactory Factory { get; }

    public CaptureSession Session { get; }

    public AudioDevice Device { get; }

    public CaptureOptions Options { get; }

    public StatusLog Statuses { get; }

    /// <summary>Fortlaufender Sinus 440 Hz, Amplitude 0,5, im Format der Fabrik.</summary>
    public SignalSource Signal { get; }

    public CaptureFormat Format => Factory.Format;

    /// <summary>Der einzige erzeugte Zugang.</summary>
    public FakeCaptureBackend Backend => Assert.Single(Factory.Backends);

    /// <summary>Öffnet eine Sitzung (Open, Kapitel 5.2) auf <see cref="Headset"/>.</summary>
    public static CaptureRig Open(
        CaptureFormat? format = null, Action<FakeCaptureFactory>? configure = null, CaptureOptions? options = null)
    {
        var clock = new ManualClock();
        var factory = new FakeCaptureFactory(format ?? DeviceFormat);
        configure?.Invoke(factory);
        var log = new StatusLog(factory);
        var chosen = options ?? CaptureOptions.Default;
        var session = new CaptureSession(Headset, factory.Open, chosen, clock);
        log.Attach(session);
        return new CaptureRig(clock, factory, session, Headset, chosen, log);
    }

    /// <summary>Wartet, bis der Zugang erzeugt und gestartet ist.</summary>
    public async Task<FakeCaptureBackend> StartedAsync()
    {
        var backend = await Factory.CreatedAsync();
        await backend.Started.WaitAsync(Guard);
        return backend;
    }

    /// <summary>Uhr um einen Puffer vor, dann ein Puffer Sinus; gibt zurück, ob das Gerät ihn lieferte.</summary>
    public bool Feed() => Feed(Signal.Next(BufferLength), BufferLength);

    /// <summary>Uhr um <paramref name="advance"/> vor, dann diesen Puffer.</summary>
    public bool Feed(byte[] buffer, TimeSpan advance, bool discontinuity = false, long? missingFrames = null)
    {
        Clock.Advance(advance);
        return Backend.Deliver(buffer, discontinuity, missingFrames);
    }

    /// <summary>Sinus für <paramref name="duration"/> in Puffern von <see cref="BufferLength"/>; alle müssen ankommen.</summary>
    public void FeedFor(TimeSpan duration)
    {
        for (var fed = TimeSpan.Zero; fed < duration; fed += BufferLength)
        {
            Assert.True(Feed(), $"Das Gerät nahm bei {fed} keinen Puffer mehr an.");
        }
    }

    /// <summary>
    /// Wartet, bis der laufende Leser alles bis auf höchstens zwei Blöcke gelesen hat, gemessen an den bisher gelieferten
    /// Geräteframes. So bleibt die Warteschlange bei langen Läufen mit Leser sicher unter <c>MaxQueuedAudio</c>, auch wenn
    /// der Test schneller einspeist, als der Leser im Hintergrund abholt (die Latenz des Resamplers liegt weit unter zwei
    /// Blöcken).
    /// </summary>
    public Task KeepUpAsync(ChunkReader reader)
    {
        var nominal = Factory.FramesDelivered * SignalAnalysis.SampleRate / Format.SampleRate;
        return reader.WaitForSamplesAsync(nominal - (2 * SignalAnalysis.BlockSamples));
    }

    /// <summary>Uhr um einen Puffer vor, dann ein Puffer exakter Nullen.</summary>
    public bool FeedZeros() => Feed(TestSignals.Zeros(Format, TestSignals.Frames(Format, BufferLength)), BufferLength);

    /// <summary>Wartet über <see cref="ICaptureSession.StatusChanged"/>, bis der Zustand die Bedingung erfüllt.</summary>
    public Task<CaptureStatus> WaitForAsync(Func<CaptureStatus, bool> condition, string what) =>
        StatusLog.WaitForAsync(Session, condition, what);

    public Task<CaptureStatus> StoppedAsync() => WaitForAsync(status => status.Phase == CapturePhase.Stopped, "Stopped");

    /// <summary>Was die Sitzung aus den gelieferten Puffern liefern muss (<see cref="ReferenceConversion"/>).</summary>
    public IReadOnlyList<AudioChunk> Reference() =>
        ReferenceConversion.Of(Format, Options.ChunkDuration, Factory.Backends.SelectMany(backend => backend.Delivered));

    /// <summary>Gibt die Sitzung genau einmal frei.</summary>
    public async Task CloseAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        await Session.DisposeAsync().AsTask().WaitAsync(Guard);
    }

    /// <summary>Gibt frei und wartet auf jede spätere Bestätigung der Attrappe, damit nichts nach dem Test läuft.</summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        foreach (var backend in Factory.Backends)
        {
            await backend.ConfirmationsDoneAsync();
        }
    }
}

/// <summary>
/// Protokoll von <see cref="ICaptureSession.StatusChanged"/>; hält zu jedem Zustand fest, wie viele Frames das Gerät bis
/// dahin geliefert hatte. Der Handler liest nichts von der Sitzung, damit er keine Sperre der Sitzung braucht.
/// </summary>
internal sealed class StatusLog(FakeCaptureFactory factory)
{
    private readonly Lock _gate = new();
    private readonly List<(CaptureStatus Status, long FramesBefore)> _entries = [];

    public IReadOnlyList<(CaptureStatus Status, long FramesBefore)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public bool SawRecording => Entries.Any(entry => entry.Status.Phase == CapturePhase.Recording);

    /// <summary>Wartet über das Ereignis, bis der Zustand die Bedingung erfüllt; prüft zuerst den aktuellen Zustand.</summary>
    public static async Task<CaptureStatus> WaitForAsync(ICaptureSession session, Func<CaptureStatus, bool> condition, string what)
    {
        var reached = new TaskCompletionSource<CaptureStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CaptureStatus status)
        {
            if (condition(status))
            {
                reached.TrySetResult(status);
            }
        }

        session.StatusChanged += Handler;
        try
        {
            var now = session.Status;
            if (condition(now))
            {
                return now;
            }

            try
            {
                return await reached.Task.WaitAsync(CaptureRig.Guard);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"Zeitüberschreitung beim Warten auf {what}; Zustand {session.Status}.", exception);
            }
        }
        finally
        {
            session.StatusChanged -= Handler;
        }
    }

    public void Attach(ICaptureSession session) => session.StatusChanged += (_, status) =>
    {
        var frames = factory.FramesDelivered;
        lock (_gate)
        {
            _entries.Add((status, frames));
        }
    };

    /// <summary>
    /// Phasen nie rückwärts; <c>Recording</c> nie vor Daten mit mindestens einem Sample (Kapitel 5.2); jeder gemeldete
    /// Stopp mit genau dem erwarteten ersten Grund, kein späterer Grund überschreibt ihn.
    /// </summary>
    public void AssertConsistent(CaptureStopReason expectedReason, string context)
    {
        var previous = CapturePhase.Opening;
        foreach (var (status, framesBefore) in Entries)
        {
            Assert.True(status.Phase >= previous, $"{context}: Phase {status.Phase} nach {previous} gemeldet.");
            previous = status.Phase;
            if (status.Phase == CapturePhase.Recording)
            {
                Assert.True(framesBefore > 0, $"{context}: Recording gemeldet, bevor das Gerät ein Sample geliefert hatte.");
            }

            if (status.Phase == CapturePhase.Stopped)
            {
                Assert.True(
                    status.StopReason == expectedReason,
                    $"{context}: Stopp mit {status.StopReason?.ToString() ?? "null"} gemeldet, erwartet {expectedReason}.");
            }
            else
            {
                Assert.True(status.StopReason is null, $"{context}: Grund {status.StopReason} in Phase {status.Phase}.");
            }
        }
    }
}

/// <summary>Liest <see cref="ICaptureSession.ReadAsync"/> im Hintergrund, wie P3 es täte, und behält Kopien der Blöcke.</summary>
internal sealed class ChunkReader
{
    private readonly Lock _gate = new();
    private readonly List<AudioChunk> _chunks = [];
    private TaskCompletionSource _changed = NewSignal();

    public ChunkReader(ICaptureSession session, CancellationToken cancellationToken = default) =>
        Completion = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in session.ReadAsync(cancellationToken))
                {
                    lock (_gate)
                    {
                        _chunks.Add(chunk with { Pcm16Mono = chunk.Pcm16Mono.ToArray() });
                    }

                    Signal();
                }
            }
            finally
            {
                Signal();
            }
        });

    /// <summary>Endet, wenn <c>ReadAsync</c> endet; eine Ausnahme daraus schlägt beim Abwarten durch.</summary>
    public Task Completion { get; }

    public IReadOnlyList<AudioChunk> Chunks
    {
        get
        {
            lock (_gate)
            {
                return [.. _chunks];
            }
        }
    }

    /// <summary>Liest eine Sitzung ganz und liefert die Blöcke; <c>ReadAsync</c> muss ohne Ausnahme enden.</summary>
    public static async Task<IReadOnlyList<AudioChunk>> ReadAllAsync(ICaptureSession session)
    {
        var reader = new ChunkReader(session);
        return await reader.CompleteAsync();
    }

    /// <summary>Wartet auf das reguläre Ende von <c>ReadAsync</c>.</summary>
    public async Task<IReadOnlyList<AudioChunk>> CompleteAsync()
    {
        await Completion.WaitAsync(CaptureRig.Guard);
        return Chunks;
    }

    /// <summary>Wartet, bis mindestens <paramref name="count"/> Blöcke gelesen sind, während die Aufnahme läuft.</summary>
    public Task WaitForChunksAsync(int count) => WaitUntilAsync(() => _chunks.Count >= count, $"{count} Blöcke");

    /// <summary>Wartet, bis mindestens <paramref name="samples"/> Samples gelesen sind, während die Aufnahme läuft.</summary>
    public Task WaitForSamplesAsync(long samples) =>
        WaitUntilAsync(() => SignalAnalysis.TotalSamples(_chunks) >= samples, $"{samples} Samples");

    /// <summary>Bedingung unter der Sperre; endet <c>ReadAsync</c> vorher, schlägt das Warten fehl.</summary>
    private async Task WaitUntilAsync(Func<bool> reached, string what)
    {
        using var guard = new CancellationTokenSource(CaptureRig.Guard);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (reached())
                {
                    return;
                }

                changed = _changed.Task;
            }

            Assert.False(Completion.IsCompleted, $"ReadAsync endete nach {Chunks.Count} Blöcken vor {what}.");
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
}

/// <summary>
/// Unbeobachtete Fehler der Aufnahme. Ergänzt <see cref="UnobservedFailureLog"/> (Meldungstexte) um den vollen Text
/// jeder Ausnahme samt Typ und Aufrufstapel; so fällt jeder Fehler auf, der in <c>Freihand.Adapters.Audio</c> oder in
/// dieser Testhilfe entstand, auch mit fremder Meldung. Meldet sich wie das Vorbild beim Laden der Assembly an.
/// </summary>
internal static class CaptureFailureWatch
{
    /// <summary>Anfang der Meldung jeder <see cref="CaptureBackendException"/>.</summary>
    public const string CaptureMarker = "Aufnahme gescheitert";

    private static readonly ConcurrentQueue<string> Seen = new();

    [ModuleInitializer]
    internal static void Start() => TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        foreach (var inner in e.Exception.Flatten().InnerExceptions)
        {
            Seen.Enqueue(inner.ToString());
        }
    };

    /// <summary>Räumt auf (über <see cref="UnobservedFailureLog"/>) und prüft, dass kein Fehler der Aufnahme unbeobachtet blieb.</summary>
    public static async Task AssertNoneAsync()
    {
        Assert.False(
            await UnobservedFailureLog.SeenAsync(CaptureMarker),
            "Eine CaptureBackendException blieb unbeobachtet.");
        string[] mine =
        [
            .. Seen.Where(text => text.Contains("Freihand.Adapters.Audio", StringComparison.Ordinal)
                || text.Contains("Freihand.Core.Tests.Audio", StringComparison.Ordinal)),
        ];
        Assert.True(mine.Length == 0, "Unbeobachteter Fehler der Aufnahme: " + string.Join(" | ", mine));
    }
}
