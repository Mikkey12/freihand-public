using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;

namespace Freihand.Adapters.Audio;

/// <summary>
/// Aufnahmesitzung als Zustandsmaschine (P8-Konzept, Kapitel 5.2 und 10.4): Opening bis zu den ersten Daten, dann
/// Recording; jeder Stopp schliesst das Mikrofon und lässt <see cref="ReadAsync"/> nach dem letzten Block regulär enden.
/// Der erste Stoppgrund bleibt; Daten nach dem Stopp werden verworfen.
/// </summary>
public sealed class CaptureSession : ICaptureSession
{
    private const long TicksPerSample = TimeSpan.TicksPerSecond / 16_000;

    private readonly object _gate = new();
    private readonly CaptureOptions _options;
    private readonly TimeProvider _time;
    private readonly long _openedAt;
    private readonly Channel<AudioChunk> _queue = Channel.CreateUnbounded<AudioChunk>(new() { SingleReader = true });
    private readonly ICaptureBackend? _backend;
    private readonly ITimer _noSignal;
    private CaptureConverter? _converter;
    private CaptureStatus _status = new(CapturePhase.Opening, null, null, false, TimeSpan.Zero);
    private long _queuedSamples;
    private long _zeroFrames;
    private bool _sawData;
    private long? _lastDataAt;
    private bool _readStarted;
    private bool _disposed;
    private readonly Queue<CaptureStatus> _notifications = new();
    private bool _notifying;
    private bool _muteReported;
    private TaskCompletionSource? _stopCompleted;
    private TaskCompletionSource? _disposeCompleted;

    /// <param name="device">Das aufgelöste Gerät.</param>
    /// <param name="open">Erzeugt den Zugang; genau ein Aufruf, nie für ein anderes Gerät. Darf <see cref="CaptureBackendException"/> werfen.</param>
    /// <param name="options">Zeitwerte.</param>
    /// <param name="time">Uhr für Startlatenz, fehlendes Signal und Nullen.</param>
    public CaptureSession(AudioDevice device, Func<AudioDevice, ICaptureBackend> open, CaptureOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        Device = device;
        _options = options;
        _time = time;
        _openedAt = time.GetTimestamp();
        _noSignal = time.CreateTimer(_ => StopBy(CaptureStopReason.NoSignal), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        try
        {
            _backend = open(device);
            _converter = new CaptureConverter(_backend.Format, options.ChunkDuration);
            _backend.DataAvailable += OnData;
            _backend.Stopped += OnBackendStopped;

            // Stummschalter von Windows: zuerst abonnieren, dann lesen, damit kein Wechsel dazwischen verloren geht
            // (Anzeige „Mikrofon stumm“); ein späterer Wechsel überschreibt den gelesenen Stand
            _backend.MuteChanged += OnMuteChanged;
            var muted = _backend.EndpointMuted == true;
            lock (_gate)
            {
                if (!_muteReported)
                {
                    _status = _status with { EndpointMuted = muted };
                }
            }
            // Bis zu den ersten Daten gilt die längere Wartezeit, danach NoSignalTimeout (RM10)
            _noSignal.Change(options.FirstDataTimeout, Timeout.InfiniteTimeSpan);
            _backend.Start();
        }
        catch (Exception exception)
        {
            // Das Mikrofon ist nicht offen; ein Stop() des Zugangs entfällt (Kapitel 10.4)
            var reason = exception is CaptureBackendException backendFailure ? backendFailure.Reason : CaptureStopReason.Failure;
            Finish(reason, stopBackend: false);
        }
    }

    public AudioDevice Device { get; }

    public CaptureStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public event EventHandler<CaptureStatus>? StatusChanged;

    public IAsyncEnumerable<AudioChunk> ReadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_readStarted)
            {
                throw new InvalidOperationException("ReadAsync ist nur einmal aufrufbar.");
            }

            _readStarted = true;
        }

        return Read(cancellationToken);
    }

    public void Stop() => StopBy(CaptureStopReason.Requested);

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        bool stopBackend;
        Task stopCompleted;
        lock (_gate)
        {
            if (_disposeCompleted is not null)
            {
                return new ValueTask(_disposeCompleted.Task);
            }

            _disposeCompleted = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposed = true;
            stopBackend = FinishLocked(CaptureStopReason.Requested, stopBackend: true, notify: false);
            stopCompleted = _stopCompleted?.Task ?? Task.CompletedTask;

            // Zustand, Abschluss und Verlustspanne werden gemeinsam festgelegt.
            var unread = 0L;
            while (_queue.Reader.TryRead(out var chunk))
            {
                unread += chunk.Pcm16Mono.Length / 2;
            }

            _queuedSamples = 0;
            _status = _status with { Unread = TimeSpan.FromTicks(unread * TicksPerSample) };
            _notifications.Enqueue(_status);
        }

        _ = DisposeCoreAsync(completion, stopBackend, stopCompleted);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion, bool stopBackend, Task stopCompleted)
    {
        Exception? failure = null;
        try
        {
            if (stopBackend) StopBackend();
            await stopCompleted.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            // Jede Freigabe wird versucht; der erste Fehler bleibt die Ursache.
            try { _noSignal.Dispose(); }
            catch (Exception exception) { failure ??= exception; }
            finally
            {
                try { _backend?.Dispose(); }
                catch (Exception exception) { failure ??= exception; }
            }
        }

        if (failure is null) completion.TrySetResult();
        else completion.TrySetException(failure);
        try { DrainNotifications(); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private async IAsyncEnumerable<AudioChunk> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (true)
            {
                AudioChunk chunk;
                lock (_gate)
                {
                    if (!_queue.Reader.TryRead(out chunk!)) break;
                    _queuedSamples -= chunk.Pcm16Mono.Length / 2;
                }

                yield return chunk;
            }
        }
    }

    private void OnData(object? sender, CaptureData data)
    {
        var stopReason = (CaptureStopReason?)null;
        var stopBackend = false;
        lock (_gate)
        {
            if (_disposed || _status.Phase == CapturePhase.Stopped || _converter is null)
            {
                return;
            }

            var format = _backend!.Format;
            var frameBytes = FrameBytes(format);
            if (data.Buffer.Length < frameBytes)
            {
                return;
            }

            _lastDataAt = _time.GetTimestamp();

            // Ein Flag auf dem ersten Puffer vor jeglichen Daten bleibt ohne Wirkung (Kapitel 10.4)
            if (data.Discontinuity && _sawData)
            {
                if (data.MissingFrames is not { } missing)
                {
                    stopReason = CaptureStopReason.Discontinuity;
                }
                else
                {
                    Enqueue(_converter.Gap(missing));
                }
            }

            if (stopReason is null)
            {
                _noSignal.Change(_options.NoSignalTimeout, Timeout.InfiniteTimeSpan);
                var status = _status;
                if (!_sawData)
                {
                    _sawData = true;
                    status = status with { Phase = CapturePhase.Recording, StartLatency = _time.GetElapsedTime(_openedAt) };
                }

                var silent = UpdateSilence(data.Buffer.Span, format, frameBytes);
                status = status with { SilentSignal = silent };
                Enqueue(_converter.Push(data.Buffer.Span));
                if (status != _status)
                {
                    _status = status;
                    _notifications.Enqueue(status);
                }

                if (_queuedSamples * TicksPerSample >= _options.MaxQueuedAudio.Ticks)
                {
                    stopReason = CaptureStopReason.QueueFull;
                }
            }

            if (stopReason is { } reason)
            {
                stopBackend = FinishLocked(reason, stopBackend: true, notify: true);
            }
        }

        if (stopBackend) StopBackend();
        DrainNotifications();
    }

    private void OnBackendStopped(object? sender, CaptureStopReason? reason)
    {
        // null bestätigt nur ein Stop() der Sitzung; ein Gerätefehler wird ohne weiteres Stop() übernommen (Kapitel 10.4)
        if (reason is { } failure)
        {
            Finish(failure, stopBackend: false);
        }
    }

    private void StopBy(CaptureStopReason reason) => Finish(reason, stopBackend: true);

    /// <summary>Erster Stopp: Mikrofon zu, Rest samt Nachlauf in die Warteschlange, Lesen endet danach regulär.</summary>
    private void Finish(CaptureStopReason reason, bool stopBackend)
    {
        bool stop;
        lock (_gate)
        {
            if (_status.Phase == CapturePhase.Stopped) return;
            stop = FinishLocked(reason, stopBackend, notify: true);
        }

        if (stop) StopBackend();
        DrainNotifications();
    }

    // Nur unter _gate. Der erste Grund und sein Backend-Auftrag werden atomar beansprucht.
    private bool FinishLocked(CaptureStopReason reason, bool stopBackend, bool notify)
    {
        if (_status.Phase == CapturePhase.Stopped) return false;
        _noSignal.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_converter is not null)
        {
            Enqueue(_converter.Flush());
            _converter = null;
        }

        _status = _status with
        {
            Phase = CapturePhase.Stopped,
            StopReason = reason,
            SinceLastData = _lastDataAt is { } last ? _time.GetElapsedTime(last) : null,
        };
        _queue.Writer.TryComplete();
        if (notify) _notifications.Enqueue(_status);
        if (stopBackend) _stopCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return stopBackend;
    }

    private void StopBackend()
    {
        try
        {
            _backend?.Stop();
        }
        finally
        {
            _stopCompleted!.TrySetResult();
        }
    }

    /// <summary>Unter der Sperre: zusammenhängende exakte Nullen in Audiodauer (Kapitel 10.4).</summary>
    private bool UpdateSilence(ReadOnlySpan<byte> buffer, CaptureFormat format, int frameBytes)
    {
        var frames = buffer.Length / frameBytes;
        var allZero = !buffer[..(frames * frameBytes)].ContainsAnyExcept((byte)0);
        if (!allZero)
        {
            var last = frames - 1;
            for (; last >= 0; last--)
            {
                if (buffer.Slice(last * frameBytes, frameBytes).ContainsAnyExcept((byte)0))
                {
                    break;
                }
            }

            _zeroFrames = frames - 1 - last;
        }
        else
        {
            _zeroFrames += frames;
        }

        return _zeroFrames * TimeSpan.TicksPerSecond / format.SampleRate >= _options.SilentSignalHint.Ticks;
    }

    /// <summary>Unter der Sperre.</summary>
    private void Enqueue(IReadOnlyList<AudioChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            _queuedSamples += chunk.Pcm16Mono.Length / 2;
            _queue.Writer.TryWrite(chunk);
        }
    }

    private static int FrameBytes(CaptureFormat format) => format.Channels * format.SampleFormat switch
    {
        CaptureSampleFormat.Pcm16 => 2,
        CaptureSampleFormat.Pcm24 => 3,
        _ => 4,
    };

    private void OnMuteChanged(object? sender, bool muted)
    {
        lock (_gate)
        {
            if (_status.Phase == CapturePhase.Stopped)
            {
                return;
            }

            _muteReported = true;
            if (_status.EndpointMuted == muted)
            {
                return;
            }

            _status = _status with { EndpointMuted = muted };
            _notifications.Enqueue(_status);
        }

        DrainNotifications();
    }

    private void DrainNotifications()
    {
        lock (_gate)
        {
            if (_notifying) return;
            _notifying = true;
        }

        try
        {
            while (true)
            {
                CaptureStatus status;
                lock (_gate)
                {
                    if (!_notifications.TryDequeue(out status!))
                    {
                        _notifying = false;
                        return;
                    }
                }

                // Keine Sitzungssperre bei fremdem Code; reentrante Änderungen kommen hinten an.
                StatusChanged?.Invoke(this, status);
            }
        }
        catch
        {
            lock (_gate) _notifying = false;
            throw;
        }
    }
}

/// <summary>
/// <see cref="IAudioCapture"/> aus Geräteliste und Zugangsfabrik; erlaubt höchstens eine nicht freigegebene Sitzung.
/// Ein Gerät, das nicht in der Liste steht, ergibt eine Sitzung mit <see cref="CaptureStopReason.DeviceMissing"/>.
/// </summary>
/// <remarks>Ergänzung zu Kapitel 4.3: die Windows-Schicht liefert nur Katalog und Fabrik (R42, R47).</remarks>
public sealed class AudioCapture : IAudioCapture
{
    private readonly IAudioDeviceCatalog _catalog;
    private readonly Func<AudioDevice, ICaptureBackend> _open;
    private readonly CaptureOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private OpenSession? _current;

    public AudioCapture(
        IAudioDeviceCatalog catalog, Func<AudioDevice, ICaptureBackend> open, CaptureOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _catalog = catalog;
        _open = open;
        _options = options;
        _time = time;
        catalog.DevicesChanged += (_, _) => OnDevicesChanged();
    }

    public IReadOnlyList<AudioDevice> Devices => _catalog.Devices;

    public AudioDevice? DefaultDevice => _catalog.DefaultDevice;

    public event EventHandler? DevicesChanged;

    public ICaptureSession Open(AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (_current is not null)
            {
                throw new InvalidOperationException("Eine frühere Aufnahmesitzung ist noch nicht freigegeben.");
            }

            // Gesucht wird nur über die Kennung; ein fehlendes Gerät öffnet nie ein anderes (ER-13)
            var known = _catalog.Devices.Any(candidate => string.Equals(candidate.Id, device.Id, StringComparison.Ordinal));
            Func<AudioDevice, ICaptureBackend> open = known ? _open : _ => throw new CaptureBackendException(CaptureStopReason.DeviceMissing);
            var session = new OpenSession(this, new CaptureSession(device, open, _options, _time));
            _current = session;
            return session;
        }
    }

    private void Released(OpenSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, session))
            {
                _current = null;
            }
        }
    }

    private void OnDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Meldet die Freigabe an <see cref="AudioCapture"/> zurück.</summary>
    private sealed class OpenSession(AudioCapture owner, CaptureSession inner) : ICaptureSession
    {
        public AudioDevice Device => inner.Device;

        public CaptureStatus Status => inner.Status;

        public event EventHandler<CaptureStatus>? StatusChanged
        {
            add => inner.StatusChanged += value;
            remove => inner.StatusChanged -= value;
        }

        public IAsyncEnumerable<AudioChunk> ReadAsync(CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);

        public void Stop() => inner.Stop();

        /// <summary>Der Platz wird auch frei, wenn das Schliessen wirft; der Fehler geht weiter (Codex zu PR #66).</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                owner.Released(this);
            }
        }
    }
}
