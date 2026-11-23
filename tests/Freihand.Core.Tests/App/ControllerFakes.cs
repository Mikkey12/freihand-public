using System.Diagnostics;
using System.Threading.Channels;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Attrappen für die Tests des <see cref="DictationController"/>.
/// Alle Attrappen schreiben in eine gemeinsame, threadsichere <see cref="EventLog"/>, damit Tests die
/// Reihenfolge der Aufrufe prüfen können. Erfundene Texte, kein Windows.
/// </summary>
internal sealed class EventLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _events = [];

    public void Add(string entry)
    {
        lock (_gate)
        {
            _events.Add(entry);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }

    /// <summary>Index des ersten Eintrags, der genau so lautet, sonst -1.</summary>
    public int IndexOf(string entry) => Snapshot().ToList().IndexOf(entry);

    /// <summary>Zahl der Einträge, die mit diesem Anfang beginnen.</summary>
    public int CountStarting(string prefix) => Snapshot().Count(entry => entry.StartsWith(prefix, StringComparison.Ordinal));

    public bool Contains(string entry) => IndexOf(entry) >= 0;
}

/// <summary>
/// Erkenner-Attrappe. Die Steuerung ruft <see cref="StartSessionAsync"/> nie selbst, nur über <see cref="IDictationFactory"/>;
/// <see cref="RestartAsync"/> nur auf ausdrücklichen Wunsch (Menü „Erkennung neu starten“, Vorstufe zur Beta).
/// </summary>
internal sealed class FakeRecognizerHost : IRecognizerHost
{
    private volatile int _availability = (int)RecognizerAvailability.Ready;
    private RecognizerErrorCode? _lastError;

    public RecognizerAvailability Availability => (RecognizerAvailability)_availability;

    public RecognizerInfo? Info { get; set; } = new(
        "ggml-test-modell.bin", new string('a', 64), RecognizerRuntime.Cpu, false, TimeSpan.FromSeconds(1), "0.0.0-test");

    public RecognizerErrorCode? LastError => _lastError;

    public event EventHandler<RecognizerAvailability>? AvailabilityChanged;

    public event EventHandler<RecognizerLaunch>? Launched;

    /// <summary>Meldet einen tatsächlichen Prozessstart.</summary>
    public void Launch(RecognizerLaunch kind) => Launched?.Invoke(this, kind);

    /// <summary>Setzt Zustand und Fehlercode und meldet die Änderung.</summary>
    public void Set(RecognizerAvailability availability, RecognizerErrorCode? lastError = null)
    {
        _lastError = lastError;
        _availability = (int)availability;
        AvailabilityChanged?.Invoke(this, availability);
    }

    /// <summary>Setzt Zustand und Fehlercode ohne Meldung (Ausgangslage vor dem Bau der Steuerung).</summary>
    public void Preset(RecognizerAvailability availability, RecognizerErrorCode? lastError = null)
    {
        _lastError = lastError;
        _availability = (int)availability;
    }

    public Task<RecognizerSession> StartSessionAsync(
        RecognitionOptions options, LiveOptions live, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Die Steuerung startet Sitzungen nur über IDictationFactory.");

    private int _restarts;

    public int RestartCount => Volatile.Read(ref _restarts);

    public Task RestartAsync()
    {
        Interlocked.Increment(ref _restarts);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Ein laufendes Diktat aus Sicht der Steuerung: Der Test füllt über <see cref="Emit"/> die Ausgabe und schliesst sie mit
/// <see cref="Complete"/> oder <see cref="Fail"/>. Das Audio wird auf Wunsch im Hintergrund gelesen.
/// </summary>
internal sealed class FakeDictationRun(EventLog log, ModeDecision mode) : IDictationRun
{
    private readonly Channel<DictationOutput> _outputs = Channel.CreateUnbounded<DictationOutput>();
    private int _commandKeys;
    private int _disposed;
    private int _audioReceived;

    public ModeDecision Mode { get; set; } = mode;

    /// <summary>Liefert weiter, auch wenn der Token abgebrochen ist (eine Quelle, die den Abbruch übergeht, wie P3 T35).</summary>
    public bool IgnoreCancellation { get; set; }

    /// <summary>Liest das übergebene Audio im Hintergrund bis zu seinem Ende (Standard).</summary>
    public bool ConsumeAudio { get; set; } = true;

    public int CommandKeyCount => Volatile.Read(ref _commandKeys);

    public int DisposeCount => Volatile.Read(ref _disposed);

    /// <summary>Das Audio wurde dem Lauf übergeben.</summary>
    public bool AudioReceived => Volatile.Read(ref _audioReceived) == 1;

    /// <summary>Der Token, den die Steuerung <see cref="RunAsync"/> übergab.</summary>
    public CancellationToken ReceivedToken { get; private set; }

    public void Emit(DictationOutput output) => _outputs.Writer.TryWrite(output);

    public void Complete() => _outputs.Writer.TryComplete();

    public void Fail(Exception error) => _outputs.Writer.TryComplete(error);

    public IAsyncEnumerable<DictationOutput> RunAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken)
    {
        log.Add("Run.RunAsync");
        ReceivedToken = cancellationToken;
        Volatile.Write(ref _audioReceived, audio is null ? 0 : 1);
        return Iterate(audio, cancellationToken);
    }

    /// <summary>Rechenzeit, die der Lauf meldet (P11); Standard leer.</summary>
    public Freihand.Adapters.Recognition.DecodeStatistics Decodes { get; set; }

    public void PressCommandKey()
    {
        log.Add("Run.CommandKey");
        Interlocked.Increment(ref _commandKeys);
    }

    public ValueTask DisposeAsync()
    {
        log.Add("Run.Dispose");
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<DictationOutput> Iterate(
        IAsyncEnumerable<AudioChunk>? audio, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (ConsumeAudio && audio is not null)
        {
            var readToken = IgnoreCancellation ? CancellationToken.None : cancellationToken;
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in audio.WithCancellation(readToken))
                    {
                        _ = chunk;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Abbruch beendet das Lesen
                }
            });
        }

        var token = IgnoreCancellation ? CancellationToken.None : cancellationToken;
        await foreach (var output in _outputs.Reader.ReadAllAsync(token))
        {
            yield return output;
        }
    }
}

/// <summary>Fabrik-Attrappe: liefert je Start eine <see cref="FakeDictationRun"/> oder wirft eine vorgegebene Ausnahme.</summary>
internal sealed class FakeDictationFactory(EventLog log) : IDictationFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakeDictationRun> _runs = [];
    private int _starts;

    /// <summary>Modus, den jeder neue Lauf meldet.</summary>
    public ModeDecision Mode { get; set; } = new(LiveMode.Sentence, RecognitionMode.Live, ModeReason.NotApproved);

    /// <summary>Wird bei jedem Start geworfen, wenn gesetzt.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Hält <see cref="StartAsync"/> an, bis der Test sie erfüllt (Phase Starting bleibt stehen).</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Stellt jeden neuen Lauf ein, bevor die Steuerung ihn sieht.</summary>
    public Action<FakeDictationRun>? Configure { get; set; }

    public int StartCount => Volatile.Read(ref _starts);

    public IReadOnlyList<FakeDictationRun> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    public FakeDictationRun? LastRun => Runs.LastOrDefault();

    public async Task<IDictationRun> StartAsync(CancellationToken cancellationToken)
    {
        log.Add("Factory.StartAsync");
        Interlocked.Increment(ref _starts);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        var run = new FakeDictationRun(log, Mode);
        Configure?.Invoke(run);
        lock (_gate)
        {
            _runs.Add(run);
        }

        return run;
    }
}

/// <summary>Aufnahme-Attrappe: Status und Ende der Sitzung steuert der Test.</summary>
internal sealed class FakeCaptureSession(EventLog log, AudioDevice device) : ICaptureSession
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CaptureStatus _status = new(CapturePhase.Opening, null, null, false, TimeSpan.Zero);
    private int _stops;
    private int _disposed;

    public AudioDevice Device { get; } = device;

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

    public int StopCount => Volatile.Read(ref _stops);

    public int DisposeCount => Volatile.Read(ref _disposed);

    /// <summary>Aufnahme mit Hinweisen zum Mikrofon (Windows-Stummschalter, nur Nullen).</summary>
    public void SetRecording(bool muted, bool silent) =>
        Change(new CaptureStatus(CapturePhase.Recording, null, TimeSpan.FromMilliseconds(50), silent, TimeSpan.Zero, EndpointMuted: muted), ended: false);

    /// <summary>Die Aufnahme meldet die ersten Audiodaten (Phase Recording).</summary>
    public void SetRecording() =>
        Change(new CaptureStatus(CapturePhase.Recording, null, TimeSpan.FromMilliseconds(40), false, TimeSpan.Zero), ended: false);

    /// <summary>Die Aufnahme endet von sich aus (Phase Stopped mit Grund); <see cref="ReadAsync"/> endet regulär.</summary>
    public void Fail(CaptureStopReason reason, TimeSpan? sinceLastData = null) =>
        Change(new CaptureStatus(CapturePhase.Stopped, reason, null, false, TimeSpan.Zero, sinceLastData), ended: true);

    public IAsyncEnumerable<AudioChunk> ReadAsync(CancellationToken cancellationToken = default)
    {
        log.Add("Capture.ReadAsync");
        return Read(cancellationToken);
    }

    public void Stop()
    {
        log.Add("Capture.Stop");
        Interlocked.Increment(ref _stops);
        bool alreadyStopped;
        lock (_gate)
        {
            alreadyStopped = _status.Phase == CapturePhase.Stopped;
        }

        if (alreadyStopped)
        {
            _ended.TrySetResult();
            return;
        }

        Change(new CaptureStatus(CapturePhase.Stopped, CaptureStopReason.Requested, null, false, TimeSpan.Zero, TimeSpan.FromMilliseconds(50)), ended: true);
    }

    public ValueTask DisposeAsync()
    {
        log.Add("Capture.Dispose");
        Interlocked.Increment(ref _disposed);
        _ended.TrySetResult();
        return ValueTask.CompletedTask;
    }

    private void Change(CaptureStatus status, bool ended)
    {
        lock (_gate)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
        if (ended)
        {
            _ended.TrySetResult();
        }
    }

    /// <summary>Blöcke, die das Lesen zuerst liefert (Verzug, G1); sonst keine Daten.</summary>
    public List<AudioChunk> Chunks { get; } = [];

    private async IAsyncEnumerable<AudioChunk> Read([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var chunk in Chunks.ToList())
        {
            yield return chunk;
            log.Add("Capture.Chunk");
        }

        // Danach keine Daten; endet nach Stop, Ausfall oder Abbruch des Lesens
        await _ended.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>Audio-Attrappe mit zwei Geräten; Open protokolliert und kann scheitern.</summary>
internal sealed class FakeAudioCapture(EventLog log) : IAudioCapture
{
    private readonly Lock _gate = new();
    private readonly List<FakeCaptureSession> _sessions = [];

    public static AudioDevice MicA { get; } = new("mikro-a", "Testmikrofon A");

    public static AudioDevice MicB { get; } = new("mikro-b", "Testmikrofon B");

    public IReadOnlyList<AudioDevice> Devices { get; set; } = [MicA, MicB];

    public AudioDevice? DefaultDevice { get; set; } = MicA;

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    /// <summary>Wird bei jedem Open geworfen, wenn gesetzt.</summary>
    public Exception? OpenFailure { get; set; }

    public IReadOnlyList<FakeCaptureSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    public FakeCaptureSession? Session => Sessions.LastOrDefault();

    public ICaptureSession Open(AudioDevice device)
    {
        log.Add($"Capture.Open:{device.Id}");
        if (OpenFailure is { } failure)
        {
            throw failure;
        }

        var session = new FakeCaptureSession(log, device);
        OnOpen?.Invoke(session);
        lock (_gate)
        {
            _sessions.Add(session);
        }

        return session;
    }

    /// <summary>Läuft beim Öffnen jeder Aufnahme, z. B. um Blöcke vorzugeben (G1, Verzug).</summary>
    public Action<FakeCaptureSession>? OnOpen { get; set; }
}

/// <summary>Bindungs-Attrappe: <see cref="Next"/> ist das Ergebnis des nächsten <see cref="BindFocused"/>.</summary>
internal sealed class FakeBinding(EventLog log) : ITargetBinding, ITargetMarker
{
    private int _binds;

    private MarkResult? _nextMark;

    /// <summary>
    /// Feld, das <see cref="MarkFocused"/> merkt (P9 Diktatfenster, Kapitel 3.4). Ohne eigene Vorgabe das Feld von
    /// <see cref="Next"/>: Die Aufnahmetaste merkt seit Audit F1 beim Druck und bindet später genau dieses Feld.
    /// </summary>
    public MarkResult NextMark { get => _nextMark ?? FromNext(); set => _nextMark = value; }

    private MarkResult FromNext() => Next.Target is { } target ? new(MarkOf(target), null) : new(null, Next.Refusal);

    private static TargetMark MarkOf(TargetInfo target)
    {
        var mark = NewMark(target.Display);
        return mark with { ProcessName = target.ProcessName, FieldKind = target.FieldKind, Rule = mark.Rule with { ProcessName = target.ProcessName } };
    }

    /// <summary>
    /// Ergebnisse von <see cref="BindMarked"/> nacheinander; der letzte gilt weiter. Leer: bindet das gemerkte Feld.
    /// </summary>
    public Queue<BindRefusal?> MarkedFocus { get; } = new();

    public int MarkedBinds { get; private set; }

    public static TargetMark NewMark(string display = "Testeditor Dokumenttext") =>
        new("testeditor", FieldKinds.DocumentBody, display,
            new ElementIdentity(42, 4242, 7, "1.2.3", "Document", "TestEdit", ""),
            new TargetRule("testeditor", null, null, null, null, FieldKinds.DocumentBody, display, LineBreakMode.Refuse, 20, IdentityMode.RuntimeId));

    public MarkResult MarkFocused(ProfileStrictness strictness)
    {
        log.Add($"Binding.MarkFocused:{strictness}");
        return NextMark;
    }

    /// <summary>Das gemerkte Feld hat den Fokus, solange <see cref="NextMark"/> genau dieses Feld liefert.</summary>
    public bool IsFocused(TargetMark mark) => Equals(NextMark.Mark, mark);

    public BindResult BindMarked(TargetMark mark, ProfileStrictness strictness)
    {
        if (_nextMark is null && MarkedFocus.Count == 0 && Equals(FromNext().Mark, mark))
        {
            // Gemerkt aus Next und weiter im Fokus: dasselbe wie früher das Binden des fokussierten Felds
            return BindFocused(strictness);
        }

        log.Add($"Binding.BindMarked:{mark.Display}");
        MarkedBinds++;
        var refusal = MarkedFocus.Count > 1 ? MarkedFocus.Dequeue() : MarkedFocus.Count == 1 ? MarkedFocus.Peek() : null;
        if (refusal is { } reason)
        {
            Bound = null;
            return new BindResult(null, reason);
        }

        var target = new TargetInfo(mark.ProcessName, mark.FieldKind, mark.Display, false) { BindingId = Guid.NewGuid() };
        Bound = target;
        return new BindResult(target, null);
    }

    public static TargetInfo NewTarget(string display = "Testeditor Dokumenttext") =>
        new("testeditor", FieldKinds.DocumentBody, display, false) { BindingId = Guid.NewGuid() };

    public BindResult Next { get; set; } = new(NewTarget(), null);

    public TargetInfo? Bound { get; private set; }

    public TargetRule? BoundRule => null;

    public int BindCount => Volatile.Read(ref _binds);

    public ProfileStrictness? LastStrictness { get; private set; }

    public BindResult BindFocused(ProfileStrictness strictness)
    {
        log.Add($"Binding.BindFocused:{strictness}");
        Interlocked.Increment(ref _binds);
        LastStrictness = strictness;
        var result = Next;
        Bound = result.Target;
        return result;
    }

    public TargetCheck CheckBound(Guid bindingId) =>
        throw new NotSupportedException("Die Prüfung vor dem Schreiben macht die Ausgabe (P7), nicht die Steuerung.");

    public TargetCheck CheckAndSend(Guid bindingId, Action send) =>
        throw new NotSupportedException("Die Prüfung vor dem Schreiben macht die Ausgabe (P7), nicht die Steuerung.");

    public void Release()
    {
        log.Add("Binding.Release");
        Bound = null;
    }
}

/// <summary>Ein Aufruf von <see cref="ITextOutput.WriteAsync"/>.</summary>
internal sealed record WriteCall(TextSegment Segment, TargetInfo Target, CancellationToken Token);

/// <summary>
/// Ausgabe-Attrappe: protokolliert Abschnitt, Ziel und Token je Aufruf, liefert gesteuerte Berichte (Standard: ganz
/// geschrieben) und kann das Schreiben eines Texts anhalten. Ein angehaltener Aufruf endet mit dem Abbruch des Tokens
/// wie bei P7 mit einem gehaltenen Teil (<see cref="OutputReason.Cancelled"/>).
/// </summary>
internal sealed class FakeTextOutput(EventLog log) : IConditionalTextOutput, IFocusWaitReporter
{
    /// <summary>Mit Bedingung (Beta B9): erst die Bedingung, dann wie ohne; gilt sie nicht, wird nichts geschrieben.</summary>
    public Task<OutputReport> WriteAsync(TextSegment segment, TargetInfo target, Func<bool> condition, CancellationToken cancellationToken = default)
    {
        var holds = condition();
        log.Add($"Output.Condition:{holds}");
        return holds
            ? WriteAsync(segment, target, cancellationToken)
            : Task.FromResult(new OutputReport(OutputResult.Refused, string.Empty, segment.Text, OutputReason.ConditionFailed));
    }

    /// <summary>Warten auf die Rückkehr des Fokus wie bei P7 (P9 Diktatfenster, Pille „Fokus weg“).</summary>
    public event EventHandler<FocusWait>? FocusWaitChanged;

    /// <summary>Läuft am Anfang jedes Schreibauftrags, bevor etwas geschrieben ist (D02).</summary>
    public Action<TextSegment>? BeforeWrite { get; set; }

    public void RaiseFocusWait(FocusWait wait) => FocusWaitChanged?.Invoke(this, wait);

    private readonly Lock _gate = new();
    private readonly List<WriteCall> _calls = [];
    private readonly Dictionary<Guid, OutputReport> _reports = [];
    private readonly Dictionary<string, OutputReport> _textReports = [];
    private TaskCompletionSource? _hold;
    private string? _heldText;

    public IReadOnlyList<WriteCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<string> Texts => [.. Calls.Select(call => call.Segment.Text)];

    /// <summary>Gibt für diesen Abschnitt diesen Bericht zurück statt „ganz geschrieben“.</summary>
    public void SetReport(Guid segmentId, OutputReport report)
    {
        lock (_gate)
        {
            _reports[segmentId] = report;
        }
    }

    /// <summary>Gibt für diesen Text diesen Bericht zurück, wenn die Kennung unbekannt ist (Übernehmen, D06).</summary>
    public void SetReportForText(string text, OutputReport report)
    {
        lock (_gate)
        {
            _textReports[text] = report;
        }
    }

    /// <summary>Hält das Schreiben dieses Texts an, bis <see cref="ReleaseHold"/> oder der Token abbricht.</summary>
    public void HoldWritesOf(string text)
    {
        lock (_gate)
        {
            _heldText = text;
            _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ReleaseHold()
    {
        lock (_gate)
        {
            _hold?.TrySetResult();
        }
    }

    /// <summary>Schreiben dieses Texts wirft (Codex zu PR #50).</summary>
    public string? FailOn { get; set; }

    public async Task<OutputReport> WriteAsync(TextSegment segment, TargetInfo target, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? hold;
        OutputReport? report;
        lock (_gate)
        {
            _calls.Add(new WriteCall(segment, target, cancellationToken));
            hold = segment.Text == _heldText ? _hold : null;
            if (!_reports.TryGetValue(segment.Id, out report))
            {
                _textReports.TryGetValue(segment.Text, out report);
            }
        }

        BeforeWrite?.Invoke(segment);
        log.Add($"Output.Write:{segment.Text}");
        if (segment.Text == FailOn)
        {
            throw new InvalidOperationException("erfundener Schreibfehler");
        }

        if (hold is not null)
        {
            try
            {
                await hold.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                log.Add($"Output.Done:{segment.Text}");
                return new OutputReport(OutputResult.HeldInDictationWindow, "", segment.Text, OutputReason.Cancelled);
            }
        }

        log.Add($"Output.Done:{segment.Text}");
        return report ?? new OutputReport(OutputResult.Written, segment.Text, "", OutputReason.None);
    }
}

/// <summary>Wächter-Attrappe: kann bei <see cref="Arm"/> oder später <see cref="StopRequired"/> auslösen, Arm kann werfen.</summary>
internal sealed class FakeGuard(EventLog log) : IRecordingGuard
{
    private int _arms;
    private int _disarms;

    public event EventHandler<GuardReason>? StopRequired;

    /// <summary>Löst während <see cref="Arm"/> sofort <see cref="StopRequired"/> aus.</summary>
    public GuardReason? RaiseOnArm { get; set; }

    /// <summary>Wird von <see cref="Arm"/> geworfen, wenn gesetzt.</summary>
    public Exception? ArmFailure { get; set; }

    public int ArmCount => Volatile.Read(ref _arms);

    public int DisarmCount => Volatile.Read(ref _disarms);

    public void Arm()
    {
        log.Add("Guard.Arm");
        Interlocked.Increment(ref _arms);
        if (ArmFailure is { } failure)
        {
            throw failure;
        }

        if (RaiseOnArm is { } reason)
        {
            StopRequired?.Invoke(this, reason);
        }
    }

    public void Disarm()
    {
        log.Add("Guard.Disarm");
        Interlocked.Increment(ref _disarms);
    }

    /// <summary>Löst später <see cref="StopRequired"/> aus.</summary>
    public void Raise(GuardReason reason) => StopRequired?.Invoke(this, reason);

    public void Dispose()
    {
    }
}

/// <summary>Tasten-Attrappe: <see cref="Conflicts"/> bestimmt je Aktion das Ergebnis von <see cref="Register"/>.</summary>
internal sealed class FakeHotkeys(EventLog log) : IHotkeyService
{
    private readonly Lock _gate = new();
    private readonly List<(HotkeyAction Action, HotkeyGesture Gesture, bool TrackRelease)> _registered = [];

    public Dictionary<HotkeyAction, HotkeyConflict> Conflicts { get; } = [];

    public IReadOnlyList<(HotkeyAction Action, HotkeyGesture Gesture, bool TrackRelease)> Registered
    {
        get
        {
            lock (_gate)
            {
                return [.. _registered];
            }
        }
    }

    public event EventHandler<HotkeyEvent>? Pressed;

    public event EventHandler<HotkeyEvent>? Released
    {
        add { }
        remove { }
    }

    public HotkeyConflict Register(HotkeyAction action, HotkeyGesture gesture, bool trackRelease = false)
    {
        log.Add($"Hotkeys.Register:{action}");
        lock (_gate)
        {
            _registered.Add((action, gesture, trackRelease));
        }

        return Conflicts.GetValueOrDefault(action, HotkeyConflict.None);
    }

    public void Unregister(HotkeyAction action) => log.Add($"Hotkeys.Unregister:{action}");

    public bool OwnKeysReleased() => true;

    /// <summary>Löst die Taste aus, mit einem Zeitstempel wie beim Empfang.</summary>
    public void Press(HotkeyAction action) => Press(action, Stopwatch.GetTimestamp());

    /// <summary>Löst die Taste mit vorgegebenem Zeitstempel aus (wie lange sie schon zurückliegt).</summary>
    public void Press(HotkeyAction action, long timestamp) => Pressed?.Invoke(this, new HotkeyEvent(action, timestamp));

    public void Dispose()
    {
    }
}

/// <summary>
/// Schreibfläche des Diktatfensters (P9 Diktatfenster, Kapitel 3.4): Fokus steuerbar, Einfügen an der Schreibmarke nach
/// <see cref="PadText"/>; die Schreibmarke steht am Ende, wenn <see cref="Caret"/> negativ ist.
/// </summary>
internal sealed class FakePad(EventLog log) : IDictationPad
{
    private readonly Lock _gate = new();
    private string _text = string.Empty;

    public bool Focused { get; set; }

    public int Caret { get; set; } = -1;

    /// <summary>Einfügen wirft (Fenster geschlossen).</summary>
    public bool Fail { get; set; }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text;
            }
        }

        set
        {
            lock (_gate)
            {
                _text = value;
            }
        }
    }

    public bool HasFocus
    {
        get
        {
            log.Add("Pad.HasFocus");
            return Focused;
        }
    }

    public string Insert(string text)
    {
        if (Fail)
        {
            throw new InvalidOperationException("erfundenes geschlossenes Fenster");
        }

        lock (_gate)
        {
            var caret = Caret < 0 ? _text.Length : Caret;
            var insertion = PadText.Insertion(_text, caret, text);
            _text = _text.Insert(caret, insertion);
            if (Caret >= 0)
            {
                Caret += insertion.Length;
            }

            log.Add($"Pad.Insert:{text}");
            return insertion;
        }
    }
}

/// <summary>Markierung im fremden Feld (Beta B9): Ergebnisse nacheinander, das letzte gilt weiter.</summary>
internal sealed class FakeForeignContent(EventLog log) : IForeignContentReader
{
    private readonly Lock _gate = new();
    private readonly Queue<SelectionRead> _next = new();
    private SelectionRead _last = SelectionRead.Refused(SelectionRefusal.NoSelection);

    public int Reads { get; private set; }

    public void Select(params string[] texts)
    {
        lock (_gate)
        {
            foreach (var text in texts)
            {
                _next.Enqueue(SelectionText.From(text, ReadPurpose.ExplicitCommand, Freihand.Core.Routing.DataClass.Confidential));
            }
        }
    }

    public void Refuse(SelectionRefusal refusal)
    {
        lock (_gate)
        {
            _next.Enqueue(SelectionRead.Refused(refusal));
        }
    }

    public ForeignText? ReadFocused(ReadPurpose purpose) => null;

    public SelectionRead ReadSelection(ReadPurpose purpose)
    {
        lock (_gate)
        {
            Reads++;
            log.Add($"Foreign.ReadSelection:{purpose}");
            if (_next.Count > 0)
            {
                _last = _next.Dequeue();
            }

            return _last;
        }
    }
}

/// <summary>Zwischenablage-Attrappe.</summary>
internal sealed class FakeClipboard(EventLog log) : IClipboard
{
    private readonly Lock _gate = new();
    private readonly List<string> _texts = [];

    public IReadOnlyList<string> Texts
    {
        get
        {
            lock (_gate)
            {
                return [.. _texts];
            }
        }
    }

    /// <summary>Wird bei SetText geworfen, wenn gesetzt (belegte Zwischenablage).</summary>
    public Exception? Failure { get; set; }

    public void SetText(string text)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            _texts.Add(text);
        }

        log.Add($"Clipboard.SetText:{text}");
    }
}
