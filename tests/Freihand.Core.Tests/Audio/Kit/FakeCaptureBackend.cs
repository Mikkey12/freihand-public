using Freihand.Adapters.Audio;
using Freihand.Core.Platform;

namespace Freihand.Core.Tests.Audio.Kit;

/// <summary>Wie die Attrappe einen Aufruf von <see cref="FakeCaptureBackend.Stop"/> mit <c>Stopped(null)</c> bestätigt.</summary>
internal enum StopConfirmation
{
    /// <summary>Wie NAudio: später, von einem anderen Thread (Standard).</summary>
    Later,

    /// <summary>Noch innerhalb von <see cref="FakeCaptureBackend.Stop"/>.</summary>
    Immediately,

    /// <summary>Gar nicht.</summary>
    Never,
}

/// <summary>
/// WASAPI-Attrappe für Stufe A (P8-Konzept, Kapitel 4.3 und 6). Der Test liefert Puffer mit <see cref="Deliver"/>, meldet
/// Fehler mit <see cref="Fail"/> und lässt <see cref="Start"/> auf Wunsch mit <see cref="CaptureBackendException"/>
/// scheitern. Die Attrappe zählt Start, Stop und Dispose und zeichnet jeden gelieferten Puffer als Kopie auf.
/// <para>Wie ein echtes Gerät liefert sie nur, solange sie läuft: nach <see cref="Stop"/>, nach einem eigenen Fehler und
/// nach <see cref="Dispose"/> weist <see cref="Deliver"/> den Puffer ab. Wie bei NAudio gilt ein Puffer nur während des
/// Ereignisses; danach überschreibt die Attrappe ihn (<see cref="ReuseBuffers"/>), damit eine Sitzung, die ihn später
/// liest, auffällt.</para>
/// </summary>
internal sealed class FakeCaptureBackend : ICaptureBackend
{
    /// <summary>Füllwert für einen zurückgegebenen Puffer; als Float32, PCM16, PCM24 und PCM32 deutlich nicht still.</summary>
    public const byte Garbage = 0x5A;

    private readonly Lock _gate = new();
    private readonly List<CaptureData> _delivered = [];
    private readonly List<Task> _confirmations = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _startCalls;
    private int _stopCalls;
    private int _disposeCalls;
    private int _stopTransitions;
    private bool _running;
    private long _framesDelivered;

    public FakeCaptureBackend(AudioDevice device, CaptureFormat format, CaptureStopReason? startFailure = null)
    {
        Device = device;
        Format = format;
        StartFailure = startFailure;
    }

    public event EventHandler<CaptureData>? DataAvailable;

    public event EventHandler<CaptureStopReason?>? Stopped;

    /// <summary>Stummschalter von Windows beim Öffnen (Anzeige „Mikrofon stumm“).</summary>
    public bool? EndpointMuted { get; set; }

    public event EventHandler<bool>? MuteChanged;

    /// <summary>Simuliert das Umschalten des Stummschalters in Windows.</summary>
    public void SetMuted(bool muted)
    {
        EndpointMuted = muted;
        MuteChanged?.Invoke(this, muted);
    }

    /// <summary>Gerät, für das die Fabrik diesen Zugang erzeugt hat.</summary>
    public AudioDevice Device { get; }

    public CaptureFormat Format { get; }

    /// <summary>Grund, mit dem <see cref="Start"/> scheitert; null heisst, der Start gelingt.</summary>
    public CaptureStopReason? StartFailure { get; }

    public StopConfirmation Confirmation { get; init; } = StopConfirmation.Later;

    /// <summary>Überschreibt jeden Puffer nach dem Ereignis mit <see cref="Garbage"/> (wie NAudio den Puffer wiederverwendet).</summary>
    public bool ReuseBuffers { get; init; } = true;

    /// <summary>Endet, sobald <see cref="Start"/> gelungen ist.</summary>
    public Task Started => _started.Task;

    public int StartCalls => Read(ref _startCalls);

    public int StopCalls => Read(ref _stopCalls);

    public int DisposeCalls => Read(ref _disposeCalls);

    /// <summary>
    /// Übergänge von „läuft“ zu „gestoppt“, gleich wodurch (Stop, eigener Fehler, Dispose). Genau einer, wenn der Start
    /// gelang; ein zweiter Stop-Aufruf auf dem gestoppten Zugang ist kein Übergang, zählt aber in <see cref="StopCalls"/>.
    /// </summary>
    public int StopTransitions => Read(ref _stopTransitions);

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>Frames aller gelieferten Puffer; wird vor dem Ereignis erhöht.</summary>
    public long FramesDelivered
    {
        get
        {
            lock (_gate)
            {
                return _framesDelivered;
            }
        }
    }

    /// <summary>Kopien aller gelieferten Puffer in Reihenfolge, samt Aussetzer-Angaben (auch Puffer mit 0 Byte).</summary>
    public IReadOnlyList<CaptureData> Delivered
    {
        get
        {
            lock (_gate)
            {
                return [.. _delivered];
            }
        }
    }

    public int FrameBytes => TestSignals.FrameBytes(Format);

    public void Start()
    {
        lock (_gate)
        {
            _startCalls++;
            if (_disposeCalls > 0)
            {
                throw new ObjectDisposedException(nameof(FakeCaptureBackend));
            }

            if (StartFailure is { } reason)
            {
                throw new CaptureBackendException(reason);
            }

            _running = true;
        }

        _started.TrySetResult();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopCalls++;
            if (!_running)
            {
                return;
            }

            _running = false;
            _stopTransitions++;
        }

        switch (Confirmation)
        {
            case StopConfirmation.Immediately:
                Stopped?.Invoke(this, null);
                break;
            case StopConfirmation.Later:
                var confirmation = Task.Run(() => Stopped?.Invoke(this, null));
                lock (_gate)
                {
                    _confirmations.Add(confirmation);
                }

                break;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposeCalls++;
            if (_running)
            {
                // Freigeben schliesst das Mikrofon auch ohne Stop
                _running = false;
                _stopTransitions++;
            }
        }
    }

    /// <summary>
    /// Liefert einen Puffer, sofern der Zugang läuft, und gibt zurück, ob er geliefert wurde. Der Puffer muss ganze Frames
    /// im <see cref="Format"/> enthalten.
    /// </summary>
    public bool Deliver(ReadOnlySpan<byte> buffer, bool discontinuity = false, long? missingFrames = null)
    {
        if (buffer.Length % FrameBytes != 0)
        {
            throw new ArgumentException("Puffer ohne ganze Frames.", nameof(buffer));
        }

        var lent = buffer.ToArray();
        lock (_gate)
        {
            if (!_running)
            {
                return false;
            }

            _framesDelivered += lent.Length / FrameBytes;
            _delivered.Add(new CaptureData(buffer.ToArray(), discontinuity, missingFrames));
        }

        DataAvailable?.Invoke(this, new CaptureData(lent, discontinuity, missingFrames));
        if (ReuseBuffers)
        {
            lent.AsSpan().Fill(Garbage);
        }

        return true;
    }

    /// <summary>
    /// Meldet einen Fehler wie WASAPI (z. B. Gerät entfernt): der Zugang hört auf zu liefern, dann <c>Stopped(reason)</c>.
    /// Auch auf einem schon gestoppten Zugang wird die Meldung ausgelöst, um späte Fehlermeldungen zu prüfen.
    /// </summary>
    public void Fail(CaptureStopReason reason)
    {
        lock (_gate)
        {
            if (_running)
            {
                _running = false;
                _stopTransitions++;
            }
        }

        Stopped?.Invoke(this, reason);
    }

    /// <summary>Wartet, bis jede spätere Bestätigung von <see cref="Stop"/> ausgelöst ist; Fehler darin schlagen durch.</summary>
    public async Task ConfirmationsDoneAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _confirmations];
        }

        await Task.WhenAll(pending).WaitAsync(CaptureRig.Guard);
    }

    private int Read(ref int field)
    {
        lock (_gate)
        {
            return field;
        }
    }
}

/// <summary>
/// Zugangsfabrik für <see cref="CaptureSession"/> und <see cref="AudioCapture"/>: zählt Aufrufe, zeichnet die Geräte auf
/// und erzeugt je Aufruf eine <see cref="FakeCaptureBackend"/>. Auf Wunsch wirft sie selbst
/// <see cref="CaptureBackendException"/> (<see cref="OpenFailure"/>) oder lässt den Start scheitern (<see cref="StartFailure"/>).
/// </summary>
internal sealed class FakeCaptureFactory(CaptureFormat format)
{
    private readonly Lock _gate = new();
    private readonly List<AudioDevice> _devices = [];
    private readonly List<FakeCaptureBackend> _backends = [];
    private readonly List<TaskCompletionSource<FakeCaptureBackend>> _created = [];

    /// <summary>Format der nächsten erzeugten Zugänge.</summary>
    public CaptureFormat Format { get; set; } = format;

    /// <summary>Grund, mit dem die Fabrik selbst wirft; null heisst, sie erzeugt einen Zugang.</summary>
    public CaptureStopReason? OpenFailure { get; set; }

    /// <summary>Grund, mit dem <see cref="ICaptureBackend.Start"/> der nächsten Zugänge scheitert.</summary>
    public CaptureStopReason? StartFailure { get; set; }

    public StopConfirmation Confirmation { get; set; } = StopConfirmation.Later;

    public bool ReuseBuffers { get; set; } = true;

    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _devices.Count;
            }
        }
    }

    /// <summary>Geräte aller Aufrufe in Reihenfolge, auch solcher, bei denen die Fabrik warf.</summary>
    public IReadOnlyList<AudioDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _devices];
            }
        }
    }

    public IReadOnlyList<FakeCaptureBackend> Backends
    {
        get
        {
            lock (_gate)
            {
                return [.. _backends];
            }
        }
    }

    /// <summary>Frames, die alle erzeugten Zugänge zusammen geliefert haben.</summary>
    public long FramesDelivered => Backends.Sum(backend => backend.FramesDelivered);

    /// <summary>Die Fabrik als Delegat, wie <see cref="CaptureSession"/> und <see cref="AudioCapture"/> sie erwarten.</summary>
    public Func<AudioDevice, ICaptureBackend> Open => Create;

    /// <summary>Stummschalter von Windows, den jeder neue Zugang beim Öffnen meldet.</summary>
    public bool? EndpointMuted { get; set; }

    /// <summary>Wartet, bis der Zugang mit dieser Nummer (ab 0) erzeugt ist.</summary>
    public Task<FakeCaptureBackend> CreatedAsync(int index = 0)
    {
        lock (_gate)
        {
            return Slot(index).Task.WaitAsync(CaptureRig.Guard);
        }
    }

    private ICaptureBackend Create(AudioDevice device)
    {
        FakeCaptureBackend backend;
        lock (_gate)
        {
            _devices.Add(device);
            if (OpenFailure is { } reason)
            {
                throw new CaptureBackendException(reason);
            }

            backend = new FakeCaptureBackend(device, Format, StartFailure)
            {
                Confirmation = Confirmation,
                ReuseBuffers = ReuseBuffers,
                EndpointMuted = EndpointMuted,
            };
            _backends.Add(backend);
            Slot(_backends.Count - 1).TrySetResult(backend);
        }

        return backend;
    }

    private TaskCompletionSource<FakeCaptureBackend> Slot(int index)
    {
        while (_created.Count <= index)
        {
            _created.Add(new TaskCompletionSource<FakeCaptureBackend>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return _created[index];
    }
}

/// <summary>Geräteliste ohne Aufnahmezugang (Kapitel 4.8); der Test ändert sie und meldet die Änderung.</summary>
internal sealed class FakeDeviceCatalog(IReadOnlyList<AudioDevice> devices, AudioDevice? defaultDevice) : IAudioDeviceCatalog
{
    private readonly Lock _gate = new();
    private IReadOnlyList<AudioDevice> _devices = devices;
    private AudioDevice? _default = defaultDevice;
    private int _reads;

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                _reads++;
                return _devices;
            }
        }
    }

    public AudioDevice? DefaultDevice
    {
        get
        {
            lock (_gate)
            {
                _reads++;
                return _default;
            }
        }
    }

    /// <summary>Lesezugriffe auf <see cref="Devices"/> und <see cref="DefaultDevice"/>.</summary>
    public int Reads
    {
        get
        {
            lock (_gate)
            {
                return _reads;
            }
        }
    }

    /// <summary>Ersetzt Liste und Standardgerät und meldet <see cref="DevicesChanged"/>.</summary>
    public void Change(IReadOnlyList<AudioDevice> devices, AudioDevice? defaultDevice)
    {
        lock (_gate)
        {
            _devices = devices;
            _default = defaultDevice;
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }
}
