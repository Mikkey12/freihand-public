using System.Text.Json;
using System.Text.Json.Nodes;
using Freihand.Core.Live;
using Freihand.Core.Recognition;

namespace Freihand.Adapters.Recognition;

/// <summary>
/// Handshake, Prüfung der Prozesskennung, Sitzungen und Wiederanlauf (P8-Konzept, Kapitel 5.4, 5.5, 5.7, 10.4): Neustart
/// sofort nach Absturz, Verbindungsabbruch, Zeitgrenze oder Detektorfehler; beim vierten solchen Ereignis innerhalb von
/// 5 Minuten (Grenze eingeschlossen) Unavailable. Bleibende Fehler setzen sofort Unavailable ohne Neustart.
/// </summary>
public sealed class RecognizerSupervisor : IRecognizerHost
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxLiveSpan = TimeSpan.FromSeconds(20);

    private static readonly RecognizerErrorCode[] Permanent =
    [
        RecognizerErrorCode.ModelMissing,
        RecognizerErrorCode.ModelChecksum,
        RecognizerErrorCode.RuntimeUnavailable,
        RecognizerErrorCode.InvalidConfiguration,
        RecognizerErrorCode.ProtocolVersion,
    ];

    private readonly IRecognizerLauncher _launcher;
    private readonly RecognizerConfiguration _configuration;
    private readonly ClientTimeouts _timeouts;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<DateTimeOffset> _events = [];
    private TaskCompletionSource _changed = NewSignal();
    private RecognizerAvailability _availability = RecognizerAvailability.Starting;
    private RecognizerInfo? _info;
    private RecognizerErrorCode? _lastError;
    private Generation? _current;
    private bool _started;
    private bool _disposed;

    /// <summary>Windows beendet die Sitzung: kein neuer Erkennerprozess (Fehler 0xC0000142 beim Herunterfahren, 18.11.2026).</summary>
    private bool _restartsHeld;

    /// <summary>Während der Sperre fälliger Start; läuft bei <see cref="ResumeRestarts"/>.</summary>
    private Generation? _waiting;

    public RecognizerSupervisor(
        IRecognizerLauncher launcher, RecognizerConfiguration configuration, ClientTimeouts timeouts, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(time);

        _launcher = launcher;
        _configuration = configuration;
        _timeouts = timeouts;
        _time = time;
    }

    public RecognizerAvailability Availability
    {
        get
        {
            lock (_gate)
            {
                return _availability;
            }
        }
    }

    public RecognizerInfo? Info
    {
        get
        {
            lock (_gate)
            {
                return _info;
            }
        }
    }

    public RecognizerErrorCode? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    public event EventHandler<RecognizerAvailability>? AvailabilityChanged;

    public event EventHandler<RecognizerLaunch>? Launched;

    /// <summary>Ein Erkenner ist bereit; offene Sitzungen prüfen damit Modell und Laufzeit (Kapitel 5.7, 5.9).</summary>
    internal event Action<RecognizerInfo>? Readied;

    /// <summary>Startet den ersten Erkenner (beim Start der App).</summary>
    /// <remarks>Ergänzung zu Kapitel 4.4: der Konstruktor startet nichts.</remarks>
    public void Start()
    {
        Generation generation;
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            generation = NewGeneration(RecognizerLaunch.First);
        }

        _ = RunAsync(generation);
    }

    public async Task<RecognizerSession> StartSessionAsync(
        RecognitionOptions options, LiveOptions live, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(live);

        // Jeder Auftrag von P3 bleibt so unter den 30 s eines Decodes (Kapitel 5.5)
        if (live.Window > MaxLiveSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(live), "Window über 20 s.");
        }

        if (live.MaxUtterance > MaxLiveSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(live), "MaxUtterance über 20 s.");
        }

        LanguageMapping.ToWhisper(options.Language);
        var info = await WaitForReadyAsync(_timeouts.ModelLoad, cancellationToken).ConfigureAwait(false);
        return new RecognizerSession(this, info);
    }

    /// <summary>
    /// Regel (Neustart unmittelbar vor dem Sitzungsende):
    /// Sobald Windows das Ende der Sitzung ankündigt, startet Freihand keinen Erkennerprozess mehr. Beendet Windows den
    /// Erkenner dabei, wird der Neustart nur vorgemerkt; ein Prozess, der während des Herunterfahrens startet, scheitert mit
    /// 0xC0000142 und Windows zeigt eine Fehlermeldung.
    /// </summary>
    public void HoldRestarts()
    {
        lock (_gate)
        {
            _restartsHeld = true;
        }
    }

    /// <summary>Das Sitzungsende ist abgebrochen (oder das Gerät aufgewacht): ein vorgemerkter Neustart läuft jetzt.</summary>
    public void ResumeRestarts()
    {
        Generation? waiting;
        lock (_gate)
        {
            _restartsHeld = false;
            waiting = _waiting;
            _waiting = null;
            if (waiting is null || _current != waiting || _disposed)
            {
                return;
            }
        }

        _ = RunAsync(waiting);
    }

    public Task RestartAsync()
    {
        Generation generation;
        lock (_gate)
        {
            if (_disposed || (_started && _availability != RecognizerAvailability.Unavailable))
            {
                return Task.CompletedTask;
            }

            _started = true;
            _events.Clear();
            _lastError = null;
            // Unter derselben Sperre (Codex zu PR #69): ein zweiter Klick während des Starts startet keinen zweiten Prozess
            _availability = RecognizerAvailability.Starting;
            generation = NewGeneration(RecognizerLaunch.Manual);
        }

        Raise(RecognizerAvailability.Starting);
        _ = RunAsync(generation);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Generation? generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            generation = _current;
            _current = null;
            _changed.TrySetResult();
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (generation is not null)
        {
            await generation.EndAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    /// <summary>Für Decode: der Client des laufenden Erkenners; bei einem Neustart höchstens <c>RestartWait</c> warten.</summary>
    internal async Task<(RecognizerInfo Info, RecognizerClient Client)> WaitForClientAsync(CancellationToken cancellationToken)
    {
        if (CurrentClient() is { } ready)
        {
            return ready;
        }

        await WaitForReadyAsync(_timeouts.RestartWait, cancellationToken).ConfigureAwait(false);
        return CurrentClient() ?? throw new RecognizerUnavailableException();
    }

    /// <summary>Für Detect: der Client des laufenden Erkenners, sonst null; nie Warten.</summary>
    internal (RecognizerInfo Info, RecognizerClient Client)? CurrentClient()
    {
        lock (_gate)
        {
            return _availability == RecognizerAvailability.Ready && _current is { Client: { IsBroken: false } client, Info: { } info }
                ? (info, client)
                : null;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static JsonObject Header(string type) => new() { ["type"] = type };

    /// <summary>Wartet auf Ready; Unavailable oder Ablauf ergibt <see cref="RecognizerUnavailableException"/>.</summary>
    private async Task<RecognizerInfo> WaitForReadyAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(limit, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_disposed || _availability == RecognizerAvailability.Unavailable)
                {
                    throw new RecognizerUnavailableException();
                }

                if (_availability == RecognizerAvailability.Ready && _current is { Client.IsBroken: false, Info: { } info })
                {
                    return info;
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RecognizerUnavailableException();
            }
        }
    }

    /// <summary>Unter der Sperre.</summary>
    private Generation NewGeneration(RecognizerLaunch kind)
    {
        _current = new Generation { Kind = kind };
        return _current;
    }

    /// <summary>Start, Handshake, Laden und Annahme von <c>detect</c> für einen Erkennerprozess (Kapitel 5.4).</summary>
    private async Task RunAsync(Generation generation)
    {
        Task<LaunchedRecognizer> launching;
        // Regel (Codex-Prüfung PR #64): Jeder Start – erster, automatischer und von Hand – prüft unmittelbar vor dem
        // Prozessstart unter derselben Sperre, ob Starts zurückgehalten sind, und merkt sich dann vor
        lock (_gate)
        {
            if (_current != generation || _disposed)
            {
                return;
            }

            if (_restartsHeld)
            {
                _waiting = generation;
                return;
            }

            // Der Launcher startet den Prozess synchron, bevor er die Aufgabe zurückgibt: unter derselben Sperre, damit
            // HoldRestarts nicht zwischen Prüfung und Start fallen kann (Codex-Deltaprüfung PR #64); erwartet wird ausserhalb
            launching = Launch(_lifetime.Token);
        }

        // Genau einmal je tatsächlichem Prozessstart, ausserhalb der Sperre
        try
        {
            Launched?.Invoke(this, generation.Kind);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nur fürs Protokoll; ein Fehler dort hält keinen Start auf
        }

        var token = _lifetime.Token;
        try
        {
            var launched = await launching.ConfigureAwait(false);
            if (!generation.Own(launched)) return;
            _ = launched.Exited.ContinueWith(
                exited => { _ = exited.Exception; OnEvent(generation); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (!IsCurrent(generation))
            {
                launched.Kill();
                return;
            }

            var decode = await AcceptOwnAsync(launched, token).ConfigureAwait(false);
            if (!await generation.OwnAsync(decode).ConfigureAwait(false)) return;
            var write = new SemaphoreSlim(1, 1);
            var hello = await PipeFraming.ReadAsync(decode, token).ConfigureAwait(false);
            if (hello?.Type != "hello" || hello.String("channel") != "decode")
            {
                // Regel 4: die erste Verbindung muss decode sein; detect vor ready beendet die Verbindung
                OnEvent(generation);
                return;
            }

            if (hello.Long("protocol") != PipeFraming.Protocol)
            {
                await PipeFraming.WriteAsync(decode, write, PipeFraming.Failed(null, RecognizerErrorCode.ProtocolVersion), default, token)
                    .ConfigureAwait(false);
                SetUnavailable(generation, RecognizerErrorCode.ProtocolVersion);
                return;
            }

            var configure = Header("configure");
            configure["config"] = JsonSerializer.SerializeToNode(_configuration, PipeFraming.Json);
            await PipeFraming.WriteAsync(decode, write, configure, default, token).ConfigureAwait(false);

            PipeFrame? reply;
            try
            {
                var reading = PipeFraming.ReadAsync(decode, token);
                _ = ObserveAsync(reading);
                reply = await reading.WaitAsync(_timeouts.ModelLoad, _time, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                OnEvent(generation);
                return;
            }

            if (reply?.Type == "failed")
            {
                var code = PipeFraming.Code(reply) ?? RecognizerErrorCode.ProtocolViolation;
                if (Array.IndexOf(Permanent, code) >= 0)
                {
                    SetUnavailable(generation, code);
                }
                else
                {
                    OnEvent(generation);
                }

                return;
            }

            var info = reply?.Type == "ready" ? PipeFraming.Read<RecognizerInfo>(reply.Header["info"]) : null;
            if (info is null)
            {
                OnEvent(generation);
                return;
            }

            var detect = await AcceptOwnAsync(launched, token).ConfigureAwait(false);
            if (!await generation.OwnAsync(detect).ConfigureAwait(false)) return;
            var detectHello = await PipeFraming.ReadAsync(detect, token).ConfigureAwait(false);
            if (detectHello?.Type != "hello" || detectHello.String("channel") != "detect"
                || detectHello.Long("protocol") != PipeFraming.Protocol)
            {
                OnEvent(generation);
                return;
            }

            var client = generation.CreateClient(decode, detect, _timeouts, _time);
            if (client is null) return;
            client.Faulted += _ => OnEvent(generation);
            lock (_gate)
            {
                if (_current != generation || generation.Ended)
                {
                    generation.Client = client;
                }
                else
                {
                    generation.Client = client;
                    generation.Info = info;
                    _info = info;
                }
            }

            if (client.IsBroken || !IsCurrent(generation))
            {
                OnEvent(generation);
                await generation.EndAsync().ConfigureAwait(false);
                return;
            }

            Readied?.Invoke(info);
            SetAvailability(generation, RecognizerAvailability.Ready);
        }
        catch (Exception exception) when (exception is OperationCanceledException && token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Rahmenfehler, getrennte Verbindung oder gescheiterter Start: wie ein Absturz
            OnEvent(generation);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>Nimmt die nächste Verbindung des eigenen Erkenners an; fremde werden ungelesen getrennt (Kapitel 5.4, R30).</summary>
    private static async Task<Stream> AcceptOwnAsync(LaunchedRecognizer launched, CancellationToken token)
    {
        while (true)
        {
            var connection = await launched.AcceptAsync(token).ConfigureAwait(false);
            if (connection.ClientProcessId == launched.ProcessId)
            {
                return connection.Stream;
            }

            // Nur das Ereignis wird festgehalten, nie Inhalt
            await connection.Stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Ein synchroner Fehler des Launchers kommt als fehlgeschlagene Aufgabe in denselben Fehlerpfad.</summary>
    private Task<LaunchedRecognizer> Launch(CancellationToken token)
    {
        try
        {
            return _launcher.LaunchAsync(token);
        }
        catch (Exception exception)
        {
            return Task.FromException<LaunchedRecognizer>(exception);
        }
    }

    private bool IsCurrent(Generation generation)
    {
        lock (_gate)
        {
            return _current == generation && !generation.Ended && !_disposed;
        }
    }

    /// <summary>Absturz, Abbruch, Zeitgrenze oder Detektorfehler: Erkenner beenden und neu starten, beim vierten in 5 Minuten Unavailable.</summary>
    private void OnEvent(Generation generation)
    {
        Generation? next = null;
        RecognizerAvailability availability;
        lock (_gate)
        {
            if (_current != generation || generation.Ended || _disposed)
            {
                return;
            }

            generation.Ended = true;
            var now = _time.GetUtcNow();
            _events.Add(now);
            _events.RemoveAll(at => now - at > Window);
            if (_events.Count >= 4)
            {
                _availability = RecognizerAvailability.Unavailable;
                _lastError = null;
            }
            else
            {
                _availability = RecognizerAvailability.Restarting;
                next = NewGeneration(RecognizerLaunch.Automatic);
            }

            availability = _availability;
        }

        _ = generation.EndAsync();
        Raise(availability);
        if (next is not null)
        {
            _ = RunAsync(next);
        }
    }

    /// <summary>Bleibender Fehler: LastError vor dem Ereignis setzen, Erkenner beenden, kein Neustart (Kapitel 5.4, 10.4).</summary>
    private void SetUnavailable(Generation generation, RecognizerErrorCode code)
    {
        lock (_gate)
        {
            if (_current != generation || generation.Ended || _disposed)
            {
                return;
            }

            generation.Ended = true;
            _lastError = code;
            _availability = RecognizerAvailability.Unavailable;
        }

        _ = generation.EndAsync();
        Raise(RecognizerAvailability.Unavailable);
    }

    private void SetAvailability(Generation generation, RecognizerAvailability availability)
    {
        lock (_gate)
        {
            if (_disposed || _current != generation || generation.Ended) return;
            _availability = availability;
        }

        Raise(availability);
    }

    private void Raise(RecognizerAvailability availability)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult();
        AvailabilityChanged?.Invoke(this, availability);
    }

    /// <summary>Ein Erkennerprozess mit seinen Verbindungen.</summary>
    private sealed class Generation
    {
        private readonly object _resources = new();
        private Task? _ending;
        private LaunchedRecognizer? _launched;
        private readonly List<Stream> _streams = [];

        public RecognizerClient? Client { get; set; }
        public RecognizerInfo? Info { get; set; }

        /// <summary>Art des Starts, für <see cref="Launched"/>.</summary>
        public RecognizerLaunch Kind { get; init; }

        /// <summary>Nur unter der Sperre des Supervisors (P8 5.7).</summary>
        public bool Ended { get; set; }

        public bool Own(LaunchedRecognizer launched)
        {
            lock (_resources)
            {
                if (_ending is null)
                {
                    _launched = launched;
                    return true;
                }
            }
            try { launched.Kill(); } catch (Exception) { }
            return false;
        }

        public async Task<bool> OwnAsync(Stream stream)
        {
            lock (_resources)
            {
                if (_ending is null)
                {
                    _streams.Add(stream);
                    return true;
                }
            }
            try { await stream.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            return false;
        }

        public RecognizerClient? CreateClient(Stream decode, Stream detect, ClientTimeouts timeouts, TimeProvider time)
        {
            lock (_resources)
            {
                if (_ending is not null) return null;
                Client = new RecognizerClient(decode, detect, timeouts, time);
                _streams.Clear(); // P8 5.4: Der Besitz geht genau einmal an den Client.
                return Client;
            }
        }

        /// <summary>Gemeinsamer Abschluss; Task vor fremden Callbacks publizieren (P8 5.4/5.7).</summary>
        public Task EndAsync()
        {
            TaskCompletionSource completion;
            LaunchedRecognizer? launched;
            RecognizerClient? client;
            Stream[] streams;
            lock (_resources)
            {
                if (_ending is not null) return _ending;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ending = completion.Task;
                launched = _launched;
                client = Client;
                streams = [.. _streams];
                _streams.Clear();
            }

            // Kein fremder Callback unter der Ressourcensperre; Wiedereintritt sieht bereits _ending.
            _ = CompleteReleaseAsync(completion, launched, client, streams);
            return completion.Task;
        }

        private static async Task CompleteReleaseAsync(TaskCompletionSource completion,
            LaunchedRecognizer? launched, RecognizerClient? client, Stream[] streams)
        {
            try
            {
                await ReleaseAsync(launched, client, streams).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }

        private static async Task ReleaseAsync(LaunchedRecognizer? launched, RecognizerClient? client, Stream[] streams)
        {
            try { launched?.Kill(); } catch (Exception) { }
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            }
            foreach (var stream in streams)
            {
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            }
        }
    }
}
