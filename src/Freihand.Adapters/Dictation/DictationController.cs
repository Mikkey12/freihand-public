using System.Diagnostics;
using System.Threading.Channels;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Ablauf des P9-Durchstichs: Aufnahmetaste, Binden, Wächter, Erkennersitzung,
/// Mikrofon, fester Text ins gebundene Ziel, Stopp, Abbruch und Fehler. Ohne Windows; die Anzeige liest nur
/// <see cref="Updated"/>. Jeder Pfad, der den Start abbricht, gibt frei, was schon gehalten wird: Wächter, Erkennersitzung,
/// Mikrofon.
/// </summary>
/// <remarks>
/// Verlustschutz und Diktatfenster: Jedes Diktat sichert seinen festen
/// Text, bevor er geschrieben wird (E1). Sind alle Abschnitte geschrieben, gilt der Entwurf als übernommen; bleibt etwas
/// gehalten, bleibt er stehen, bis der gehaltene Text kopiert oder ins Diktatfenster geholt ist. Abbrechen verwirft nie
/// (E4). Hat beim Start die Schreibfläche des Diktatfensters den Fokus, geht der feste Text dorthin statt in ein fremdes
/// Feld (E2). „Übernehmen“ schreibt nur ins gemerkte Feld (AU-10).
/// </remarks>
public sealed class DictationController : IAsyncDisposable
{
    /// <summary>So viele Einträge zeigt das Statusfenster höchstens.</summary>
    private const int MaxEntries = 100;

    private readonly object _gate = new();
    private readonly IRecognizerHost _recognizer;
    private readonly IDictationFactory _factory;
    private readonly IAudioCapture _audio;
    private readonly ITargetBinding _binding;
    private readonly ITextOutput _output;
    private readonly IRecordingGuard _guard;
    private readonly IHotkeyService _hotkeys;
    private readonly IClipboard _clipboard;
    private readonly ControllerOptions _options;
    private readonly Action<string>? _log;
    private readonly RecoveryStore? _recovery;
    private readonly IDictationPad? _pad;
    private readonly ITargetMarker? _marker;
    private readonly IForeignContentReader? _foreign;

    /// <summary>Prüfaufnahmen (P11, Entscheid P11-E1); null ohne.</summary>
    private readonly IReviewRecorder? _review;

    /// <summary>Gewähltes Mikrofon für die folgenden Diktate (B3).</summary>
    private string? _deviceId;
    private readonly IFocusWaitReporter? _focusWaits;
    private readonly List<OutputEntry> _entries = [];

    /// <summary>
    /// Ereigniszeilen fürs Protokoll (Beta B4/B6, Codex zu PR #56), unter <see cref="_gate"/> gesammelt und von
    /// <see cref="Publish()"/> vor der Zustandszeile geschrieben: jedes Ereignis genau einmal, nur Codes. Zustandszeilen sind
    /// Momentaufnahmen und eignen sich nicht zum Zählen.
    /// </summary>
    private readonly List<string> _events = [];

    /// <summary>Entwürfe beendeter Diktate mit gehaltenem Text, je gehaltenem Abschnitt (Kapitel 3.2).</summary>
    private readonly Dictionary<Guid, HeldDraft> _heldDrafts = [];

    /// <summary>Warten der Ausgabe auf die Rückkehr des Fokus: Bindung, Restzeit beim Beginn, Stopwatch-Zeitstempel.</summary>
    private (Guid BindingId, TimeSpan Remaining, long Since)? _focusWait;

    private int _dictationNumber;
    private int _messageNumber;
    private int _written;
    private bool _toPad;
    private string? _microphone;

    /// <summary>Reihe des Erfassens bei Tasten (<see cref="Capture"/>), unabhängig von der Arbeit der Tasten.</summary>
    private Task _captureWork = Task.CompletedTask;

    /// <summary>Laufendes „Übernehmen“; null sonst. Ein Diktat startet währenddessen nicht.</summary>
    private CancellationTokenSource? _handOver;

    /// <summary>Ende des laufenden „Übernehmen“ samt Lösen der Bindung; Beenden wartet darauf (Codex zu PR #52).</summary>
    private Task _handOverDone = Task.CompletedTask;

    private ControllerPhase _phase = ControllerPhase.Idle;
    private int? _startMs;
    private MicrophoneSignal _signal;
    private int? _stopMs;
    private TargetInfo? _target;
    private ModeDecision? _mode;
    private string _provisional = string.Empty;
    private ControllerMessage _message = ControllerMessage.None;
    private string? _reason;
    private LiveNotice? _notice;

    /// <summary>Längenschnitte, deren Abschnitt noch nicht in der Liste steht (T78), in Sitzungszeit.</summary>
    private readonly HashSet<TimeSpan> _cuts = [];

    /// <summary>Veröffentlichungen nacheinander: Stand und Zustellung bilden eine Einheit, kein älterer Stand überholt (U35-04).</summary>
    private readonly object _publishing = new();

    /// <summary>Tastenaktionen geordnet ausserhalb des Nachrichtenfenster-Threads (U35-06, P7 Kapitel 5.1).</summary>
    private Task _keyWork = Task.CompletedTask;

    /// <summary>Gehört zum laufenden Diktat; null im Leerlauf.</summary>
    private Dictation? _current;
    private Task _completion = Task.CompletedTask;
    private bool _hotkeysSubscribed;
    private bool _disposed;

    public DictationController(
        IRecognizerHost recognizer,
        IDictationFactory factory,
        IAudioCapture audio,
        ITargetBinding binding,
        ITextOutput output,
        IRecordingGuard guard,
        IHotkeyService hotkeys,
        IClipboard clipboard,
        ControllerOptions options,
        Action<string>? log = null,
        RecoveryStore? recovery = null,
        IDictationPad? pad = null,
        ITargetMarker? marker = null,
        IForeignContentReader? foreign = null,
        IReviewRecorder? review = null)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(options);
        _recognizer = recognizer;
        _factory = factory;
        _audio = audio;
        _binding = binding;
        _output = output;
        _guard = guard;
        _hotkeys = hotkeys;
        _clipboard = clipboard;
        _options = options;
        _log = log;
        _recovery = recovery;
        _pad = pad;
        _marker = marker;
        _foreign = foreign;
        _review = review;
        _deviceId = options.DeviceId;
        _focusWaits = output as IFocusWaitReporter;
        _recognizer.AvailabilityChanged += OnAvailabilityChanged;
        _recognizer.Launched += OnRecognizerLaunched;

        // Zuerst abonnieren, dann lesen: Ein Ausfall vor dem Bau der Steuerung (früher Startfehler) kommt einmal ins
        // Protokoll (Codex zu PR #69)
        LogOutage(_recognizer.Availability, direct: true);
        if (_recovery is not null)
        {
            _recovery.IssueDetected += OnRecoveryIssue;
        }

        if (_focusWaits is not null)
        {
            _focusWaits.FocusWaitChanged += OnFocusWait;
        }
    }

    /// <summary>Jede Änderung des Stands; Abonnenten kehren sofort zurück.</summary>
    public event EventHandler<ControllerUpdate>? Updated;

    /// <summary>Pegel der Aufnahme, 0 bis 1, höchstens alle 50 ms (Pille); nicht im Protokoll.</summary>
    public event EventHandler<float>? LevelChanged;

    /// <summary>Taste fürs Diktatfenster, mit dem Feld, das dabei den Fokus hatte (Kapitel 3.4).</summary>
    public event EventHandler<DictationWindowRequest>? DictationWindowRequested;

    /// <summary>Taste fürs Korrigieren oder Glätten, mit Feld und Markierung beim Tastendruck (Beta B9).</summary>
    public event EventHandler<CorrectionRequest>? CorrectionRequested;

    /// <summary>Taste „Problem merken“ (Beta B5); die Anzeige schreibt die Marke und bestätigt sie.</summary>
    public event EventHandler? MarkRequested;

    /// <summary>
    /// Gewähltes Mikrofon (B3); null heisst Standardgerät von Windows. Gilt ab dem nächsten Diktat: ein laufendes behält
    /// das Gerät, mit dem es gestartet ist.
    /// </summary>
    public string? DeviceId
    {
        get
        {
            lock (_gate)
            {
                return _deviceId;
            }
        }
    }

    /// <summary>Wählt das Mikrofon für die folgenden Diktate; ein fehlendes Gerät wird beim Start gemeldet, nie ersetzt (ER-13).</summary>
    public void SelectDevice(string? deviceId)
    {
        lock (_gate)
        {
            _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        }
    }

    /// <summary>Vorhandene Aufnahmegeräte, ohne ein Mikrofon zu öffnen; bei einem Fehler keine.</summary>
    public IReadOnlyList<AudioDevice> Devices
    {
        get
        {
            try
            {
                return _audio.Devices;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return [];
            }
        }
    }

    public ControllerUpdate Current
    {
        get
        {
            lock (_gate)
            {
                return Snapshot();
            }
        }
    }

    /// <summary>Ende des laufenden Diktats samt Freigabe; erledigt, wenn keins läuft.</summary>
    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _completion;
            }
        }
    }

    /// <summary>Meldet die Tasten aus <see cref="ControllerOptions.Hotkeys"/> an; Konflikte mit Grund, die übrigen wirken (X16).</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyConflict> RegisterHotkeys()
    {
        var results = new Dictionary<HotkeyAction, HotkeyConflict>();
        foreach (var (action, gesture) in _options.Hotkeys)
        {
            results[action] = _hotkeys.Register(action, gesture);
        }

        lock (_gate)
        {
            if (!_hotkeysSubscribed)
            {
                _hotkeys.Pressed += OnHotkeyPressed;
                _hotkeysSubscribed = true;
            }
        }

        // Je Konflikt eine Meldung mit seinem Grund; welche Taste betroffen ist, steht im Ergebnis
        foreach (var conflict in results.Values.Where(value => value != HotkeyConflict.None))
        {
            Publish(ControllerMessage.HotkeyConflict, conflict.ToString());
        }

        return results;
    }

    /// <summary>
    /// Aufnahmetaste (BE-03): im Leerlauf Diktatstart, während der Aufnahme Stopp; beim Starten und Stoppen ohne Wirkung,
    /// dann beendet Abbrechen. Ein zweites Diktat startet nie. Die Aufgabe endet, sobald der Start entschieden ist
    /// (Mikrofon offen oder abgewiesen); das ganze Diktat zeigt <see cref="Completion"/>.
    /// </summary>
    public Task RecordAsync() => RecordAsync(null);

    /// <param name="pressedAt">Stopwatch-Zeitstempel des Tastendrucks (<see cref="HotkeyEvent.Timestamp"/>); null ohne Taste.</param>
    /// <param name="mayStart">Falsch, wenn beim Druck schon ein Diktat lief: dann startet dieser Druck nie ein neues.</param>
    /// <param name="pressedField">Beim Druck gemerktes Feld (Audit F1); null ohne Taste oder ohne Merker.</param>
    private Task RecordAsync(long? pressedAt, bool mayStart = true, (MarkResult Mark, bool FromPad)? pressedField = null)
    {
        Dictation? stop = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            switch (_phase)
            {
                case ControllerPhase.Idle when _handOver is not null:
                    // Während „Übernehmen“ schreibt, startet kein Diktat
                    return Task.CompletedTask;

                case ControllerPhase.Idle when !mayStart:
                    // Gedacht als Stopp, das Diktat ist inzwischen von selbst zu Ende: kein neues starten
                    return Task.CompletedTask;

                case ControllerPhase.Idle:
                    _phase = ControllerPhase.Starting;
                    var dictation = new Dictation { PressedAt = pressedAt, DeviceId = _deviceId, PressedField = pressedField };
                    _startMs = null;
                    _stopMs = null;
                    _signal = MicrophoneSignal.Ok;
                    _dictationNumber++;
                    _events.Add("ereignis diktat_start");
                    _written = 0;
                    _toPad = false;
                    _microphone = null;
                    _current = dictation;
                    _completion = StartAsync(dictation);
                    return dictation.Started.Task;

                case ControllerPhase.Recording:
                    stop = _current;
                    break;

                default:
                    return Task.CompletedTask;
            }
        }

        Stop(stop!, ControllerMessage.None, null);
        return Task.CompletedTask;
    }

    /// <summary>Abbrechen (BE-06): nach dem laufenden Teil geht nichts mehr ins Ziel; ein Start bricht vor dem Mikrofon ab.</summary>
    public Task CancelAsync()
    {
        Dictation? dictation;
        lock (_gate)
        {
            _handOver?.Cancel();
            dictation = _current;
            if (dictation is null)
            {
                return Task.CompletedTask;
            }

            SetMessage(ControllerMessage.Cancelled, null, dictation);
            dictation.FinalMessage = true;
            if (_phase != ControllerPhase.Starting || dictation.Capture is not null)
            {
                _phase = ControllerPhase.Stopping;
            }
        }

        dictation.Cancel.Cancel();
        StopCapture(dictation);
        Publish();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Abbruchabsicht sofort, noch im Thread der Taste: Ab hier beginnt kein Schreibauftrag mehr (BE-06). Das Stoppen der
    /// Aufnahme folgt geordnet ausserhalb dieses Threads.
    /// </summary>
    private void MarkCancelled()
    {
        Dictation? dictation;
        lock (_gate)
        {
            _handOver?.Cancel();
            dictation = _current;
            if (dictation is null)
            {
                return;
            }

            SetMessage(ControllerMessage.Cancelled, null, dictation);
            dictation.FinalMessage = true;
        }

        dictation.Cancel.Cancel();
    }

    /// <summary>Befehlstaste (BE-19): nur während der Aufnahme.</summary>
    public void CommandKey()
    {
        IDictationRun? run;
        lock (_gate)
        {
            run = _phase == ControllerPhase.Recording ? _current?.Run : null;
        }

        run?.PressCommandKey();
    }

    /// <summary>
    /// Kopiert den gehaltenen Rest eines Eintrags in die Zwischenablage (D4 b): nur im Profil „Standard“, nur für
    /// <see cref="EntryStatus.Held"/> oder <see cref="EntryStatus.Refused"/>; sonst falsch ohne Zugriff (AU-03).
    /// </summary>
    public bool CopyHeld(Guid segmentId)
    {
        if (_options.Strictness == ProfileStrictness.Strict)
        {
            Publish(ControllerMessage.ClipboardRefused, nameof(ProfileStrictness.Strict));
            return false;
        }

        OutputEntry? entry;
        lock (_gate)
        {
            entry = _entries.FirstOrDefault(item => item.SegmentId == segmentId);
        }

        if (entry is not { Status: EntryStatus.Held or EntryStatus.Refused } || entry.Held.Length == 0)
        {
            return false;
        }

        // Eine belegte oder gesperrte Zwischenablage meldet sich mit Grund, statt die Anzeige abstürzen zu lassen
        try
        {
            _clipboard.SetText(entry.Held);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Publish(ControllerMessage.ClipboardRefused, exception.GetType().Name);
            return false;
        }

        // Kopiert heisst erledigt: der Entwurf dieses Diktats gilt als übernommen, sobald nichts mehr offen ist
        Take([segmentId]);
        Publish();
        return true;
    }

    /// <summary>
    /// Holt den gehaltenen Rest ins Diktatfenster (Kapitel 3.4): liefert den Text der genannten Einträge, ohne Kennungen
    /// den aller offenen, in ihrer Reihenfolge, und vermerkt sie als erledigt. Auch im strengen Profil, weil der Text
    /// Freihand nicht verlässt. Leer, wenn nichts offen ist.
    /// </summary>
    public string TakeHeld(IReadOnlyCollection<Guid>? segmentIds = null)
    {
        List<OutputEntry> taken;
        lock (_gate)
        {
            taken = [.. _entries.Where(entry => segmentIds is null
                ? IsOpenHeld(entry)
                : entry.Status != EntryStatus.Written && entry.Held.Length > 0 && segmentIds.Contains(entry.SegmentId))];
        }

        if (taken.Count == 0)
        {
            return string.Empty;
        }

        Take([.. taken.Select(entry => entry.SegmentId)]);
        Publish();
        return TextJoin.Join(taken.Select(entry => entry.Held));
    }

    /// <summary>
    /// „Erkennung neu starten“ von Hand (Gegencheck vom 10.11.2026, Vorstufe zur Beta): nur wenn der Erkenner nicht
    /// verfügbar ist, sonst ohne Wirkung; ein laufendes oder ladendes Modell wird nie abgebrochen.
    /// </summary>
    public Task RestartRecognizerAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return _recognizer.Availability == RecognizerAvailability.Unavailable ? _recognizer.RestartAsync() : Task.CompletedTask;
    }

    /// <summary>
    /// Merkt das Feld mit dem Fokus für „Übernehmen“, ohne zu binden (Kapitel 3.4). Ohne Merker oder aus der eigenen
    /// Schreibfläche heraus kein Feld (<see cref="BindRefusal.OwnProcess"/>).
    /// </summary>
    public MarkResult MarkTarget()
    {
        if (_marker is null || _pad?.HasFocus == true)
        {
            return new MarkResult(null, BindRefusal.OwnProcess);
        }

        try
        {
            return _marker.MarkFocused(_options.Strictness);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // UIA gescheitert: kein Feld gemerkt, Übernehmen bleibt gesperrt
            return new MarkResult(null, BindRefusal.IdentityUnclear);
        }
    }

    private const string SelectionChanged = "SelectionChanged";

    private bool SelectionIs(string expected) => ReadSelection().Text?.Reveal() == expected;

    /// <summary>Die Markierung im Feld mit dem Fokus, auf ausdrücklichen Befehl (SI-12); ohne Leser „nicht lesbar“.</summary>
    public SelectionRead ReadSelection()
    {
        if (_foreign is null)
        {
            return SelectionRead.Refused(SelectionRefusal.Unreadable);
        }

        try
        {
            return _foreign.ReadSelection(ReadPurpose.ExplicitCommand);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return SelectionRead.Refused(SelectionRefusal.Unreadable);
        }
    }

    /// <summary>
    /// „Übernehmen“ (AU-04, AU-10): bindet nur das gemerkte Feld, sobald es nach dem Ausblenden des Diktatfensters den
    /// Fokus hat, längstens <see cref="ControllerOptions.HandOverSettle"/>, und schreibt den Text über die gemessene
    /// Ausgabe. Hat ein anderes Feld den Fokus, wird nichts geschrieben. Nur im Leerlauf; Abbrechen beendet es.
    /// </summary>
    public Task<HandOverReport> HandOverAsync(string text, TargetMark mark) => HandOverAsync(text, mark, null);

    /// <summary>
    /// Wie <see cref="HandOverAsync(string, TargetMark)"/>; mit <paramref name="replacing"/> ersetzt es eine Markierung
    /// (Beta B9). Geschrieben wird nur, wenn im gebundenen Feld genau dieser Text noch markiert ist, sonst nichts (Grund
    /// „SelectionChanged“): Der neue Text ersetzt so immer die geprüfte Markierung und landet nie zusätzlich anderswo. Durch
    /// nichts wird eine Markierung nie ersetzt (Grund „Empty“).
    /// </summary>
    public async Task<HandOverReport> HandOverAsync(string text, TargetMark mark, string? replacing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(mark);
        var marker = _marker ?? throw new InvalidOperationException("Ohne Merker gibt es kein Übernehmen.");
        CancellationTokenSource handOver;
        TaskCompletionSource done;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_phase != ControllerPhase.Idle || _handOver is not null)
            {
                return new HandOverReport(HandOverResult.Refused, text, "Busy");
            }

            handOver = _handOver = new CancellationTokenSource();
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _handOverDone = done.Task;
        }

        var core = PadText.ToCore(text);
        try
        {
            if (core.Length == 0 && replacing is null)
            {
                return new HandOverReport(HandOverResult.Written, string.Empty, null);
            }

            if (core.Trim().Length == 0)
            {
                Publish(ControllerMessage.HandOverRefused, "Empty");
                return new HandOverReport(HandOverResult.Refused, text, "Empty");
            }

            var bound = await BindMarkedAsync(marker, mark, handOver.Token).ConfigureAwait(false);
            if (bound.Target is not { } target)
            {
                var refusal = bound.Refusal?.ToString() ?? nameof(OutputReason.Cancelled);
                Publish(ControllerMessage.HandOverRefused, refusal);
                return new HandOverReport(HandOverResult.Refused, text, refusal);
            }

            // Früh prüfen erspart das Warten; massgeblich ist die Prüfung der Ausgabe vor dem ersten Zeichen
            var expected = replacing is null ? null : PadText.ToCore(replacing);
            if (expected is not null && !SelectionIs(expected))
            {
                Publish(ControllerMessage.HandOverRefused, SelectionChanged);
                return new HandOverReport(HandOverResult.Refused, text, SelectionChanged);
            }

            var segment = new TextSegment(Guid.NewGuid(), core, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.Zero);
            OutputReport report;
            if (expected is null)
            {
                report = await _output.WriteAsync(segment, target, handOver.Token).ConfigureAwait(false);
            }
            else if (_output is IConditionalTextOutput conditional)
            {
                report = await conditional.WriteAsync(segment, target, () => SelectionIs(expected), handOver.Token).ConfigureAwait(false);
            }
            else
            {
                // Ohne Prüfung unmittelbar vor dem Schreiben wird keine Markierung ersetzt
                Publish(ControllerMessage.HandOverRefused, nameof(IConditionalTextOutput));
                return new HandOverReport(HandOverResult.Refused, text, nameof(IConditionalTextOutput));
            }

            if (report.Reason == OutputReason.ConditionFailed)
            {
                Publish(ControllerMessage.HandOverRefused, SelectionChanged);
                return new HandOverReport(HandOverResult.Refused, text, SelectionChanged);
            }

            if (report.Result == OutputResult.Written && report.Held.Length == 0)
            {
                return new HandOverReport(HandOverResult.Written, string.Empty, null);
            }

            Publish(ControllerMessage.HandOverRefused, report.Reason.ToString());
            return report.Written.Length == 0
                ? new HandOverReport(HandOverResult.Refused, text, report.Reason.ToString())
                : new HandOverReport(HandOverResult.Partial, report.Held, report.Reason.ToString());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Unbekannt, wie viel ankam: der ganze Text bleibt im Diktatfenster
            Publish(ControllerMessage.HandOverRefused, exception.GetType().Name);
            return new HandOverReport(HandOverResult.Refused, text, exception.GetType().Name);
        }
        finally
        {
            Try(_binding.Release);
            lock (_gate)
            {
                _handOver = null;
            }

            handOver.Dispose();
            Publish();
            done.TrySetResult();
        }
    }

    /// <summary>Wartet, bis das gemerkte Feld den Fokus hat; harte Gründe (Passwortfeld, erhöhte Rechte …) gelten sofort.</summary>
    private async Task<BindResult> BindMarkedAsync(ITargetMarker marker, TargetMark mark, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var bound = marker.BindMarked(mark, _options.Strictness);
            var settling = bound.Refusal is BindRefusal.NoFocus or BindRefusal.OwnProcess or BindRefusal.NotMarkedField;
            if (bound.Target is not null || !settling || Stopwatch.GetElapsedTime(started) >= _options.HandOverSettle)
            {
                return bound;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new BindResult(null, null);
            }
        }
    }

    /// <summary>Vermerkt gehaltene Einträge als erledigt; ein Entwurf ohne offenen Abschnitt gilt als übernommen.</summary>
    private void Take(IReadOnlyCollection<Guid> segmentIds)
    {
        var handedOver = new List<DraftRecorder>();
        lock (_gate)
        {
            for (var index = 0; index < _entries.Count; index++)
            {
                if (segmentIds.Contains(_entries[index].SegmentId))
                {
                    _entries[index] = _entries[index] with { Taken = true };
                }
            }

            foreach (var id in segmentIds)
            {
                if (_heldDrafts.Remove(id, out var held) && held.Open.Remove(id) && held.Open.Count == 0)
                {
                    handedOver.Add(held.Recorder);
                }
            }
        }

        foreach (var recorder in handedOver)
        {
            HandOver(recorder);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dictation? dictation;
        Task completion;
        Task handOver;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dictation = _current;
            completion = _completion;

            // Regel: Beenden bricht laufende Arbeit ab und wartet ihr Ende ab, auch ein Übernehmen (Codex zu PR #52)
            _handOver?.Cancel();
            handOver = _handOverDone;
        }

        if (dictation is not null)
        {
            dictation.Cancel.Cancel();
            StopCapture(dictation);
        }

        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Das Diktat meldet seine Fehler selbst; das Beenden geht weiter
        }

        // Übernehmen wirft nie; danach ist die Bindung gelöst und kein Schreibauftrag mehr offen
        await handOver.ConfigureAwait(false);

        Task keys;
        lock (_gate)
        {
            keys = _keyWork;
        }

        await keys.ConfigureAwait(false);

        _recognizer.AvailabilityChanged -= OnAvailabilityChanged;
        _recognizer.Launched -= OnRecognizerLaunched;
        if (_hotkeysSubscribed)
        {
            _hotkeys.Pressed -= OnHotkeyPressed;
        }

        if (_focusWaits is not null)
        {
            _focusWaits.FocusWaitChanged -= OnFocusWait;
        }

        // Offene Entwürfe bleiben wiederherstellbar (BE-18, AU-12): sichern, nicht verwerfen
        List<DraftRecorder> open;
        lock (_gate)
        {
            open = [.. _heldDrafts.Values.Select(held => held.Recorder).Distinct()];
            _heldDrafts.Clear();
        }

        foreach (var recorder in open)
        {
            Try(recorder.Dispose);
        }

        if (_recovery is not null)
        {
            _recovery.IssueDetected -= OnRecoveryIssue;
        }
    }

    // ---- Start -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Erkenner bereit → Binden → Wächter → Erkennersitzung → Mikrofon (Kapitel 2); jeder Abbruch gibt frei. Ein
    /// unerwarteter Fehler einer Schnittstelle beendet den Start ebenso, statt ihn hängen zu lassen.
    /// </summary>
    private async Task StartAsync(Dictation dictation)
    {
        // Rückmeldungen erst nach dem ersten Warten, damit RecordAsync nie unter der Sperre meldet
        await Task.Yield();
        try
        {
            await StartCoreAsync(dictation).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StopCapture(dictation);
            await ReleaseAsync(dictation).ConfigureAwait(false);
            EndStart(dictation, (ControllerMessage.DictationFailed, exception.GetType().Name));
        }
    }

    private async Task StartCoreAsync(Dictation dictation)
    {
        var availability = _recognizer.Availability;
        if (availability != RecognizerAvailability.Ready)
        {
            EndStart(dictation, availability == RecognizerAvailability.Unavailable
                ? (ControllerMessage.RecognizerUnavailable, _recognizer.LastError?.ToString())
                : (ControllerMessage.RecognizerNotReady, availability.ToString()));
            return;
        }

        // Schreibfläche des Diktatfensters im Fokus: dorthin, nie in ein fremdes Feld (E2, D05)
        TargetInfo target;
        if (dictation.PressedField is { } pressed && _marker is { } marker)
        {
            // Audit F1: nur das beim Druck gemerkte Ziel; hat der Fokus seither gewechselt, startet nichts
            if (pressed.FromPad)
            {
                if (_pad?.HasFocus != true)
                {
                    EndStart(dictation, (ControllerMessage.BindRefused, nameof(BindRefusal.NotMarkedField)));
                    return;
                }

                target = DictationWindowTarget();
                dictation.ToPad = true;
            }
            else
            {
                var bound = pressed.Mark.Mark is { } mark
                    ? marker.BindMarked(mark, _options.Strictness)
                    : new BindResult(null, pressed.Mark.Refusal);
                if (bound.Target is not { } marked)
                {
                    EndStart(dictation, (ControllerMessage.BindRefused, bound.Refusal?.ToString()));
                    return;
                }

                target = marked;
            }
        }
        else if (_pad?.HasFocus == true)
        {
            target = DictationWindowTarget();
            dictation.ToPad = true;
        }
        else
        {
            var bound = _binding.BindFocused(_options.Strictness);
            if (bound.Target is not { } focused)
            {
                EndStart(dictation, (ControllerMessage.BindRefused, bound.Refusal?.ToString()));
                return;
            }

            target = focused;
        }

        lock (_gate)
        {
            _toPad = dictation.ToPad;
            _target = target;
            _provisional = string.Empty;
            _mode = null;
            _notice = null;
            _cuts.Clear();
        }

        // Wächter vor dem Mikrofon; eine Meldung schon beim Scharfschalten verhindert den Start (SI-04)
        dictation.Target = target;
        _guard.StopRequired += dictation.GuardHandler = (_, reason) => OnGuardStop(dictation, reason);
        try
        {
            _guard.Arm();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Freigeben wie jeder andere Startabbruch nach dem Binden: Handler weg, Bindung gelöst (Codex zu PR #47)
            await ReleaseAsync(dictation).ConfigureAwait(false);
            EndStart(dictation, (ControllerMessage.GuardFailed, exception.GetType().Name));
            return;
        }

        dictation.Armed = true;
        if (await AbortedAsync(dictation).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            dictation.Run = await _factory.StartAsync(dictation.DeviceId, dictation.Cancel.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var failure = exception switch
            {
                OperationCanceledException when dictation.Cancel.IsCancellationRequested => (ControllerMessage.Cancelled, (string?)null),
                RecognizerException recognizer => (ControllerMessage.RecognizerUnavailable, recognizer.Code.ToString()),
                RecognizerUnavailableException => (ControllerMessage.RecognizerUnavailable, _recognizer.LastError?.ToString()),
                _ => (ControllerMessage.DictationFailed, exception.GetType().Name),
            };
            await ReleaseAsync(dictation).ConfigureAwait(false);
            EndStart(dictation, failure);
            return;
        }

        lock (_gate)
        {
            _mode = dictation.Run.Mode;
        }

        if (await AbortedAsync(dictation).ConfigureAwait(false))
        {
            return;
        }

        var device = dictation.DeviceId is { } id ? _audio.Devices.FirstOrDefault(item => item.Id == id) : _audio.DefaultDevice;

        // Ein Stopp, der während der Geräteauflösung entschieden wurde, lässt kein Mikrofon mehr öffnen (U35-05)
        if (await AbortedAsync(dictation).ConfigureAwait(false))
        {
            return;
        }

        if (device is null)
        {
            await ReleaseAsync(dictation).ConfigureAwait(false);
            EndStart(dictation, (ControllerMessage.NoDevice, dictation.DeviceId is null ? "Standardgerät" : "Gerät"));
            return;
        }

        ICaptureSession capture;
        try
        {
            capture = _audio.Open(device);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ReleaseAsync(dictation).ConfigureAwait(false);
            EndStart(dictation, (ControllerMessage.CaptureFailed, exception.GetType().Name));
            return;
        }

        capture.StatusChanged += dictation.CaptureHandler = (_, status) => OnCaptureStatus(dictation, status);
        lock (_gate)
        {
            dictation.Capture = capture;
            _microphone = device.Name;
        }

        // Jedes Diktat sichert seinen festen Text (E1); ohne Sicherung läuft es trotzdem
        dictation.Draft = StartDraft();

        // Abbruch oder Wächter, während das Mikrofon öffnete: sofort wieder zu, mit Grund
        if (dictation.StopReason is { } guardReason && !dictation.Cancel.IsCancellationRequested)
        {
            Stop(dictation, ControllerMessage.GuardStopped, guardReason.ToString());
        }
        else if (dictation.Cancel.IsCancellationRequested)
        {
            StopCapture(dictation);
        }

        OnCaptureStatus(dictation, capture.Status);
        Publish();
        dictation.Started.TrySetResult();
        await RunAsync(dictation).ConfigureAwait(false);
    }

    /// <summary>Abbruch oder Wächter während des Starts: freigeben und mit Grund in den Leerlauf.</summary>
    private async Task<bool> AbortedAsync(Dictation dictation)
    {
        (ControllerMessage, string?)? reason = dictation.Cancel.IsCancellationRequested
            ? (ControllerMessage.Cancelled, null)
            : dictation.StopReason is { } guard ? (ControllerMessage.GuardStopped, guard.ToString()) : null;
        if (reason is not { } value)
        {
            return false;
        }

        await ReleaseAsync(dictation).ConfigureAwait(false);
        EndStart(dictation, value);
        return true;
    }

    private void EndStart(Dictation dictation, (ControllerMessage Message, string? Reason) failure)
    {
        dictation.Started.TrySetResult();
        lock (_gate)
        {
            if (_current == dictation)
            {
                _current = null;
                _phase = ControllerPhase.Idle;
            }

            SetMessage(failure.Message, failure.Reason, dictation);
        }

        Publish();
    }

    // ---- Diktat ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fester Text geht in Reihenfolge ins gebundene Ziel, vorläufiger nur in die Anzeige. Endet die P3-Sitzung mit Fehler,
    /// geht das Mikrofon sofort zu (P8 Kapitel 5.7); danach Wächter aus, Sitzung und Aufnahme frei.
    /// </summary>
    private async Task RunAsync(Dictation dictation)
    {
        var token = dictation.Cancel.Token;
        IAsyncEnumerator<DictationOutput>? outputs = null;

        // Schreiben läuft für sich, in der Reihenfolge der festen Abschnitte; Meldungen, Befehle und beendende Hinweise
        // verarbeitet diese Schleife sofort, auch während ein Abschnitt langsam geschrieben wird oder auf die Rückkehr des
        // Fokus wartet (Codex zu PR #50)
        var pending = Channel.CreateUnbounded<TextSegment>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var writer = Task.Run(() => WriteAllAsync(dictation, pending.Reader, token), CancellationToken.None);
        dictation.Review = Review(dictation);
        try
        {
            outputs = dictation.Run!.RunAsync(Levels(dictation, dictation.Capture!.ReadAsync(token), token), token).GetAsyncEnumerator(token);
            while (await outputs.MoveNextAsync().ConfigureAwait(false))
            {
                switch (outputs.Current)
                {
                    case TextOutput { Update: var update }:
                        lock (_gate)
                        {
                            _provisional = TextJoin.Join(update.Provisional.Select(segment => segment.Text));
                        }

                        Publish();

                        // Erst sichern, dann schreiben (D02): auch was danach gehalten oder abgebrochen wird, ist gesichert
                        Save(dictation, update);

                        // Jeder feste Abschnitt geht in den Kanal; der Schreibablauf entscheidet (Abbruch: verwerfen wie bisher,
                        // nach einem Schreibfehler: gehalten)
                        foreach (var segment in update.Fixed)
                        {
                            lock (_gate)
                            {
                                dictation.Fixed.Add(segment.Id);
                                dictation.LastFixed = segment;
                            }

                            pending.Writer.TryWrite(segment);
                        }

                        break;

                    // Befehle führt der Pilot noch nicht aus: sichtbar melden statt still verwerfen; nur Codes, nie Text
                    case CommandOutput { Command: var command }:
                        Inform(dictation, ControllerMessage.CommandNotSupported, command.Command.Id);
                        break;

                    case HintOutput hint:
                        if (hint.Hint is not null)
                        {
                            Inform(dictation, ControllerMessage.CommandHint, null);
                        }

                        if (hint.Notice is { } notice)
                        {
                            OnNotice(dictation, notice);
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Abbruch: die Meldung steht schon
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_gate)
            {
                SetMessage(ControllerMessage.DictationFailed, exception.GetType().Name, dictation);
                dictation.FinalMessage = true;
            }
        }
        finally
        {
            // Das Mikrofon geht zu, bevor die Ausgabefolge oder irgendetwas anderes freigegeben wird (P8 Kapitel 5.7)
            StopCapture(dictation);
            await StopFinished(dictation).ConfigureAwait(false);

            // Alle festen Abschnitte sind geschrieben oder gehalten, bevor die Bindung gelöst wird
            pending.Writer.TryComplete();
            await writer.ConfigureAwait(false);
            await SpaceAfterAsync(dictation).ConfigureAwait(false);
            if (outputs is not null)
            {
                await TryAsync(outputs.DisposeAsync).ConfigureAwait(false);
            }

            // Rechenzeit vor der Freigabe lesen; ReleaseAsync gibt den Lauf ab
            var decodes = dictation.Run?.Decodes ?? default;
            await ReleaseAsync(dictation).ConfigureAwait(false);
            FinishDraft(dictation);
            FinishReview(dictation, decodes);
            lock (_gate)
            {
                if (_current == dictation)
                {
                    _current = null;
                    _phase = ControllerPhase.Idle;
                    _provisional = string.Empty;
                    _microphone = null;
                    _focusWait = null;
                }
            }

            Publish();
        }
    }

    /// <summary>
    /// Regel (Handtest P9 vom 11.11.2026): Ist der letzte feste Abschnitt eines Diktats ganz in ein fremdes Feld
    /// geschrieben und endet das Diktat regulär, folgt ein Leerzeichen, damit das nächste Diktat nicht anklebt. Regulär heisst:
    /// ohne abschliessenden Grund (<see cref="Dictation.FinalMessage"/>), also nicht nach Abbrechen, Fehler, beendendem
    /// Hinweis, Wächter oder Mikrofonausfall (Codex zu PR #53). Nicht im Diktatfenster (dort trennt die Fügeregel selbst).
    /// Kommt es nicht innert der Frist an, entfällt es still; es ist kein diktierter Text, darum weder Eintrag noch Sicherung.
    /// </summary>
    private async Task SpaceAfterAsync(Dictation dictation)
    {
        TextSegment? last;
        bool ended;
        lock (_gate)
        {
            last = dictation.LastFixed;
            ended = dictation.FinalMessage;
        }

        if (!_options.SpaceAfterDictation || dictation.ToPad || dictation.Target is not { } target || last is null ||
            ended || dictation.Cancel.IsCancellationRequested || !IsWritten(dictation, last.Id))
        {
            return;
        }

        // Abbrechen, ein später abschliessender Grund (Stop) oder die Frist beenden das wartende Leerzeichen
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(dictation.Cancel.Token, dictation.Ended.Token);
        limit.CancelAfter(_options.SpaceAfterDictationLimit);
        var space = new TextSegment(Guid.NewGuid(), " ", SegmentState.Fixed, last.End, last.End);
        await TryAsync(async () => await _output.WriteAsync(space, target, limit.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private bool IsWritten(Dictation dictation, Guid segmentId)
    {
        lock (_gate)
        {
            return dictation.WrittenIds.Contains(segmentId);
        }
    }

    // ---- Verlustschutz (Kapitel 3.2) -----------------------------------------------------------------------------

    private DraftRecorder? StartDraft()
    {
        if (_recovery is null)
        {
            return null;
        }

        try
        {
            return _recovery.StartDraft();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Publish(ControllerMessage.RecoveryFailed, exception.GetType().Name);
            return null;
        }
    }

    /// <summary>Jede Aktualisierung an den Entwurf, bevor geschrieben wird; er nimmt nur festen Text (D02).</summary>
    private void Save(Dictation dictation, RecognitionUpdate update)
    {
        if (dictation.Draft is not { } draft)
        {
            return;
        }

        try
        {
            draft.Apply(update);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Die Sicherung dieses Diktats bleibt aus, das Diktat läuft weiter; gemeldet nur mit Typ (SI-01)
            Publish(ControllerMessage.RecoveryFailed, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Regel (Codex zu PR #52): Ein Entwurf gilt erst als übernommen, wenn jeder seiner festen Abschnitte geschrieben oder
    /// von dir erledigt (kopiert, ins Diktatfenster geholt) ist. Steht jeder offene Abschnitt als gehaltener Eintrag in der
    /// Liste, wartet der Entwurf darauf. Kann ein offener Abschnitt hier nicht erledigt werden (nie geschrieben, etwa nach
    /// Abbrechen, oder nicht kopierbar), bleibt der ganze Entwurf bis zur Frist stehen und wird angeboten (E4); nie verworfen.
    /// </summary>
    private void FinishDraft(Dictation dictation)
    {
        if (dictation.Draft is not { } draft)
        {
            return;
        }

        dictation.Draft = null;
        bool handOver;
        lock (_gate)
        {
            var open = dictation.Fixed
                .Where(id => !dictation.WrittenIds.Contains(id) && !_entries.Any(entry => entry.SegmentId == id && entry.Taken))
                .ToHashSet();
            var takeable = open.All(id => _entries.Any(entry => entry.SegmentId == id && IsOpenHeld(entry)));
            if (open.Count > 0 && takeable)
            {
                var holder = new HeldDraft(draft, open);
                foreach (var id in open)
                {
                    _heldDrafts[id] = holder;
                }

                return;
            }

            handOver = dictation.Fixed.Count > 0 && open.Count == 0;
        }

        if (handOver)
        {
            HandOver(draft);
        }
        else
        {
            Try(draft.Dispose);
        }
    }

    private void HandOver(DraftRecorder draft)
    {
        try
        {
            draft.MarkHandedOver();
        }
        catch (RecoveryException exception)
        {
            // Der Takt versucht es weiter (P5); gemeldet nur mit der Art
            Publish(ControllerMessage.RecoveryFailed, exception.Issue.Kind.ToString());
        }
        catch (InvalidOperationException)
        {
            // Schon übernommen, gelöscht oder beendet: nichts mehr zu tun
        }
    }

    private void OnRecoveryIssue(object? sender, RecoveryIssueEventArgs e) =>
        Publish(ControllerMessage.RecoveryFailed, e.Issue.Kind.ToString());

    private static TargetInfo DictationWindowTarget() =>
        new("Freihand", FieldKinds.MultiLine, "Diktatfenster", IsPassword: false) { BindingId = Guid.NewGuid() };

    /// <summary>Reicht das Audio unverändert weiter und meldet höchstens alle 50 ms den Pegel; behält nichts.</summary>
    /// <summary>
    /// Pegel für die Pille; dazu beim ersten Block der Nullpunkt der Audiozeit auf der Stoppuhr (<see cref="Dictation.AudioZero"/>):
    /// Ein Block kommt an, wenn sein Ton zu Ende ist, also liegt Audiozeit 0 um Versatz plus Dauer vor der Ankunft.
    /// </summary>
    private async IAsyncEnumerable<AudioChunk> Levels(
        Dictation dictation, IAsyncEnumerable<AudioChunk> audio,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        long last = 0;
        await foreach (var chunk in audio.WithCancellation(token).ConfigureAwait(false))
        {
            if (dictation.AudioZero is null && chunk.SampleRate > 0)
            {
                var length = TimeSpan.FromSeconds(chunk.Pcm16Mono.Length / 2.0 / chunk.SampleRate) + chunk.Offset;
                dictation.AudioZero = Stopwatch.GetTimestamp() - (long)(length.TotalSeconds * Stopwatch.Frequency);
            }

            dictation.Review?.Audio(chunk);

            if (LevelChanged is { } handler && (last == 0 || Stopwatch.GetElapsedTime(last) >= TimeSpan.FromMilliseconds(50)))
            {
                last = Stopwatch.GetTimestamp();
                var level = AudioLevel.Of(chunk.Pcm16Mono.Span);
                Notify(() => handler(this, level));
            }

            yield return chunk;
        }
    }

    private void OnFocusWait(object? sender, FocusWait wait)
    {
        lock (_gate)
        {
            if (_target?.BindingId != wait.BindingId)
            {
                return;
            }

            _focusWait = wait.Remaining is { } remaining ? (wait.BindingId, remaining, Stopwatch.GetTimestamp()) : null;
        }

        Publish();
    }

    /// <summary>
    /// Schreibt die festen Abschnitte nacheinander und liest den Kanal bis zum Ende. Ein Abbruch verwirft, was noch nicht
    /// begonnen hat, wie bisher. Ein Fehler beendet das ganze Diktat sichtbar: Mikrofon zu, Leseablauf abgebrochen, der
    /// betroffene und jeder weitere Abschnitt bleibt gehalten im Statusfenster (Codex zu PR #50). Wirft nie.
    /// </summary>
    private async Task WriteAllAsync(Dictation dictation, ChannelReader<TextSegment> segments, CancellationToken token)
    {
        var failed = false;
        await foreach (var segment in segments.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (failed)
            {
                Hold(dictation, segment, OutputReason.PreviousHeld);
                continue;
            }

            if (token.IsCancellationRequested)
            {
                continue;
            }

            try
            {
                await WriteAsync(dictation, segment, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Abbruch: die Meldung steht schon
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failed = true;
                lock (_gate)
                {
                    SetMessage(ControllerMessage.DictationFailed, exception.GetType().Name, dictation);
                    dictation.FinalMessage = true;
                }

                // Wie viel davon ankam, ist unbekannt: der ganze Abschnitt bleibt zum Kopieren stehen
                Hold(dictation, segment, OutputReason.InjectionBlocked);
                StopCapture(dictation);
                await TryAsync(() => new ValueTask(dictation.Cancel.CancelAsync())).ConfigureAwait(false);
                Publish();
            }
        }
    }

    /// <summary>Nicht geschriebener Abschnitt sichtbar und kopierbar im Statusfenster.</summary>
    private void Hold(Dictation dictation, TextSegment segment, OutputReason reason)
    {
        dictation.Review?.Section(segment, new OutputReport(OutputResult.HeldInDictationWindow, string.Empty, segment.Text, reason));
        DraftRecorder? released;
        lock (_gate)
        {
            released = AddEntry(new OutputEntry(segment.Id, EntryStatus.Held, reason, string.Empty, segment.Text, _cuts.Remove(segment.End)));
            _events.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ereignis abschnitt_gehalten status={EntryStatus.Held} grund={reason}"));
        }

        Release(released);
        Publish();
    }

    /// <summary>
    /// Unter <see cref="_gate"/>: neuer Eintrag, höchstens <see cref="MaxEntries"/>. Fällt ein gehaltener Eintrag hinaus,
    /// lässt er sich hier nicht mehr erledigen: Sein Entwurf wird nach derselben Regel wie in <see cref="FinishDraft"/>
    /// ganz stehen gelassen und angeboten (Codex zu PR #52); der Aufrufer gibt ihn ausserhalb der Sperre frei.
    /// </summary>
    private DraftRecorder? AddEntry(OutputEntry entry)
    {
        _entries.Add(entry);
        if (_entries.Count <= MaxEntries)
        {
            return null;
        }

        var evicted = _entries[0];
        _entries.RemoveAt(0);
        if (!_heldDrafts.TryGetValue(evicted.SegmentId, out var holder))
        {
            return null;
        }

        foreach (var id in holder.Open)
        {
            _heldDrafts.Remove(id);
        }

        return holder.Recorder;
    }

    /// <summary>Sichert und lässt stehen: der Entwurf wird angeboten, nie übernommen oder verworfen.</summary>
    private static void Release(DraftRecorder? recorder)
    {
        if (recorder is not null)
        {
            Try(recorder.Dispose);
        }
    }

    /// <summary>Gehaltener Rest, den du noch kopieren oder ins Diktatfenster holen kannst.</summary>
    private static bool IsOpenHeld(OutputEntry entry) =>
        entry.Status != EntryStatus.Written && entry.Held.Length > 0 && !entry.Taken;

    /// <summary>
    /// Hinweis von P3 mit Verlustspanne in die Anzeige (U35-02). Decoder- und Detektorfehler und ein voller Puffer beenden
    /// die Aufnahme sofort, noch bevor P3 aufräumt (U35-01, ER-05).
    /// </summary>
    private void OnNotice(Dictation dictation, LiveNotice notice)
    {
        lock (_gate)
        {
            if (notice.Kind == LiveNoticeKind.LengthCutUncertain)
            {
                _cuts.Add(notice.Start);
            }

            var keep = _notice is { } current && Ends(current.Kind) && !Ends(notice.Kind);
            if (!keep)
            {
                _notice = notice;
            }
        }

        if (Ends(notice.Kind))
        {
            Stop(dictation, ControllerMessage.Notice, notice.Kind.ToString());
            return;
        }

        Inform(dictation, ControllerMessage.Notice, notice.Kind.ToString());
    }

    private static bool Ends(LiveNoticeKind kind) =>
        kind is LiveNoticeKind.DecoderFailed or LiveNoticeKind.DetectorFailed or LiveNoticeKind.BufferFull;

    private async Task WriteAsync(Dictation dictation, TextSegment segment, CancellationToken token)
    {
        // Ins Diktatfenster an die Schreibmarke; in fremde Felder geht dabei nichts (D05)
        var report = dictation.ToPad
            ? new OutputReport(OutputResult.Written, _pad!.Insert(segment.Text), string.Empty, OutputReason.None)
            : await _output.WriteAsync(segment, dictation.Target!, token).ConfigureAwait(false);
        dictation.Review?.Section(segment, report);
        var status = report.Result switch
        {
            OutputResult.Written => EntryStatus.Written,
            OutputResult.Refused => EntryStatus.Refused,
            _ => EntryStatus.Held,
        };
        DraftRecorder? released;
        lock (_gate)
        {
            // Die letzten Einträge bleiben über Diktate hinweg sichtbar, damit gehaltener Text kopierbar bleibt (D4 b)
            released = AddEntry(new OutputEntry(segment.Id, status, report.Reason, report.Written, report.Held, _cuts.Remove(segment.End)));
            if (status == EntryStatus.Written && report.Held.Length == 0 && dictation.AudioZero is { } zero)
            {
                // Verzug: vom Ende des Gesprochenen bis der Text im Ziel steht (ER-01, QM-08); nur Zahlen. Eine Näherung:
                // Audiotakt und Stoppuhr sind nur beim ersten Block gekoppelt. Unter null ist kein Messwert, sondern zeigt,
                // dass beide auseinanderlaufen; das wird als ungültig protokolliert, nie als 0 (Codex-Prüfung PR #57)
                var delay = Stopwatch.GetElapsedTime(zero) - segment.End;
                _events.Add(delay >= TimeSpan.Zero
                    ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ereignis abschnitt_geschrieben verzug_ms={(long)delay.TotalMilliseconds}")
                    : "ereignis abschnitt_geschrieben verzug_ms=ungueltig");
            }

            if (status != EntryStatus.Written || report.Held.Length > 0)
            {
                _events.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ereignis abschnitt_gehalten status={status} grund={report.Reason}"));
            }

            if (status == EntryStatus.Written && report.Held.Length == 0)
            {
                dictation.WrittenIds.Add(segment.Id);
                _written++;
            }
        }

        Release(released);
        Publish();
    }

    // ---- Stopp und Freigabe ----------------------------------------------------------------------------------------

    /// <summary>Regulärer Stopp: Mikrofon zu, schon Aufgenommenes wird noch verarbeitet und ausgegeben.</summary>
    private void Stop(Dictation dictation, ControllerMessage message, string? reason)
    {
        lock (_gate)
        {
            if (_current != dictation)
            {
                return;
            }

            if (_phase is ControllerPhase.Recording or ControllerPhase.Starting && dictation.Capture is not null)
            {
                _phase = ControllerPhase.Stopping;
            }

            if (message != ControllerMessage.None)
            {
                SetMessage(message, reason, dictation);
                dictation.FinalMessage = true;
            }
        }

        StopCapture(dictation);
        Publish();

        // Regel (Codex zu PR #53): Ein abschliessender Grund beendet auch, was danach noch freiwillig käme, etwa ein
        // schon wartendes Leerzeichen nach dem Diktat. Ausserhalb der Sperre, weil Abbruchrückrufe gleich weiterlaufen können
        if (message != ControllerMessage.None)
        {
            dictation.Ended.Cancel();
        }
    }

    /// <summary>Stoppt die Aufnahme genau einmal, auch wenn Taste, Wächter, Gerät und Fehler zusammentreffen.</summary>
    private static void StopCapture(Dictation dictation)
    {
        ICaptureSession? capture;
        TaskCompletionSource stopping;
        lock (dictation)
        {
            if (dictation.Stopping is not null || dictation.Capture is not { } open)
            {
                return;
            }

            capture = open;
            stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dictation.Stopping = stopping;
        }

        try
        {
            capture.Stop();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Stopp scheitert: DisposeAsync schliesst das Mikrofon dennoch
        }
        finally
        {
            stopping.TrySetResult();
        }
    }

    /// <summary>
    /// Ein Stopp, der auf einem anderen Thread noch läuft (Taste, Wächter), muss zurückgekehrt sein, bevor die Aufnahme
    /// freigegeben wird und das Diktat als beendet gilt.
    /// </summary>
    private static Task StopFinished(Dictation dictation)
    {
        lock (dictation)
        {
            return dictation.Stopping?.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Wächter aus, Erkennersitzung und Aufnahme frei, zuletzt die Zielbindung; jeder Schritt auch, wenn ein anderer
    /// scheitert. Ohne Bindung liest P7 keine Fokusereignisse fremder Fenster (P7 Kapitel 5.4, Gegencheck vom 10.11.2026).
    /// </summary>
    private async Task ReleaseAsync(Dictation dictation)
    {
        if (dictation.Armed)
        {
            dictation.Armed = false;
            Try(_guard.Disarm);
        }

        if (dictation.GuardHandler is { } guardHandler)
        {
            _guard.StopRequired -= guardHandler;
            dictation.GuardHandler = null;
        }

        if (dictation.Run is { } run)
        {
            dictation.Run = null;
            await TryAsync(run.DisposeAsync).ConfigureAwait(false);
        }

        if (dictation.Capture is { } capture)
        {
            await StopFinished(dictation).ConfigureAwait(false);
            if (dictation.CaptureHandler is { } captureHandler)
            {
                capture.StatusChanged -= captureHandler;
            }

            await TryAsync(capture.DisposeAsync).ConfigureAwait(false);
        }

        // Nach dem letzten Schreibauftrag; das nächste Diktat bindet neu
        Try(_binding.Release);
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Weiter freigeben
        }
    }

    private static async Task TryAsync(Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Weiter freigeben
        }
    }

    // ---- Ereignisse ------------------------------------------------------------------------------------------------

    private void OnGuardStop(Dictation dictation, GuardReason reason)
    {
        bool running;
        lock (_gate)
        {
            dictation.StopReason ??= reason;
            running = _current == dictation && dictation.Capture is not null;
        }

        if (running)
        {
            Stop(dictation, ControllerMessage.GuardStopped, reason.ToString());
        }
    }

    private void OnCaptureStatus(Dictation dictation, CaptureStatus status)
    {
        lock (_gate)
        {
            if (_current != dictation)
            {
                return;
            }

            if (status.Phase == CapturePhase.Recording && _phase == ControllerPhase.Starting)
            {
                _phase = ControllerPhase.Recording;
                _startMs = dictation.PressedAt is { } pressedAt
                    ? (int)Math.Round(Stopwatch.GetElapsedTime(pressedAt).TotalMilliseconds)
                    : null;
            }

            if (status.Phase == CapturePhase.Stopped && status.StopReason is { } stopped && stopped != CaptureStopReason.Requested)
            {
                _stopMs = status.SinceLastData is { } since ? (int)Math.Round(since.TotalMilliseconds) : null;
            }

            // Stummschalter von Windows, immer am neuesten Stand der Aufnahme: ein älterer Stand, der später ankommt (erster
            // Aufruf nach dem Öffnen), überschreibt keinen neueren; jede Änderung einmal ins Protokoll (ohne Inhalt)
            var latest = dictation.Capture?.Status ?? status;
            var signal = latest.Phase == CapturePhase.Stopped ? _signal
                : latest.EndpointMuted ? MicrophoneSignal.Muted
                : MicrophoneSignal.Ok;
            if (signal != _signal)
            {
                _signal = signal;
                _events.Add(signal == MicrophoneSignal.Muted ? "ereignis mikrofon=stumm" : "ereignis mikrofon=frei");
            }
        }

        if (status.Phase == CapturePhase.Stopped && status.StopReason is { } reason && reason != CaptureStopReason.Requested)
        {
            Stop(dictation, ControllerMessage.CaptureStopped, reason.ToString());
            return;
        }

        Publish();
    }

    private void OnAvailabilityChanged(object? sender, RecognizerAvailability availability)
    {
        LogOutage(availability, direct: false);
        Publish();
    }

    /// <summary>
    /// Regel fürs Beta-Protokoll (Codex zu PR #69): je tatsächlich gestartetem Erkennerprozess ausser dem ersten eine Zeile
    /// „ereignis erkenner_neustart“ (automatisch oder von Hand), je Wechsel in „nicht verfügbar“ eine Zeile
    /// „ereignis erkenner_nicht_verfuegbar“.
    /// </summary>
    private void OnRecognizerLaunched(object? sender, RecognizerLaunch kind)
    {
        if (kind == RecognizerLaunch.First)
        {
            return;
        }

        lock (_gate)
        {
            _events.Add("ereignis erkenner_neustart");
        }

        Publish();
    }

    private RecognizerAvailability? _loggedAvailability;

    /// <summary>Ein Ausfall einmal je Wechsel; direkt geschrieben beim Bau, sonst mit dem nächsten Stand.</summary>
    private void LogOutage(RecognizerAvailability availability, bool direct)
    {
        bool outage;
        lock (_gate)
        {
            outage = availability == RecognizerAvailability.Unavailable && _loggedAvailability != RecognizerAvailability.Unavailable;
            _loggedAvailability = availability;
            if (outage && !direct)
            {
                _events.Add("ereignis erkenner_nicht_verfuegbar");
            }
        }

        if (outage && direct)
        {
            // Unter derselben Sperre wie Publish, damit das Protokoll nie zwei Zeilen zugleich erhält
            lock (_publishing)
            {
                Notify(() => _log?.Invoke("ereignis erkenner_nicht_verfuegbar"));
            }
        }
    }

    /// <summary>
    /// Tasten kehren sofort zurück (P7 Kapitel 5.1): Die Aktion läuft geordnet ausserhalb des Nachrichtenfenster-Threads.
    /// Abbrechen setzt seine Absicht vorher sofort, damit danach kein Schreibauftrag mehr beginnt (U35-06).
    /// </summary>
    private void OnHotkeyPressed(object? sender, HotkeyEvent pressed)
    {
        switch (pressed.Action)
        {
            case HotkeyAction.Record:
            {
                // Audit F1: das Ziel gehört zum Druck, nicht zum späteren Abarbeiten der Reihe. Nur ein Druck im Leerlauf
                // kann starten; nur er merkt das Feld, der Stopp-Druck wirkt unabhängig vom Fokus
                var startsDictation = _phase == ControllerPhase.Idle;
                var field = startsDictation && _marker is not null ? PressedField() : ((MarkResult Mark, bool FromPad)?)null;
                Enqueue(() => RecordAsync(pressed.Timestamp, startsDictation, field));
                break;
            }
            case HotkeyAction.Cancel:
                MarkCancelled();
                Enqueue(CancelAsync);
                break;
            case HotkeyAction.Command:
                Enqueue(() =>
                {
                    CommandKey();
                    return Task.CompletedTask;
                });
                break;
            case HotkeyAction.Correct:
            case HotkeyAction.Smooth:
            {
                // Feld beim Druck (PressedField); die Markierung gleich danach, und nur, solange dasselbe Feld den Fokus hat
                var kind = pressed.Action == HotkeyAction.Correct ? Transform.CorrectionKind.Correct : Transform.CorrectionKind.Smooth;
                var (pressedMark, fromPad) = PressedField();
                Capture(() =>
                {
                    var mark = pressedMark;
                    var selection = mark.Mark is null ? SelectionRead.Refused(SelectionRefusal.Unreadable) : ReadSelection();
                    if (mark.Mark is { } field && !StillFocused(field))
                    {
                        // Der Fokus ist inzwischen woanders: die Markierung könnte zu einem anderen Feld gehören
                        mark = new MarkResult(null, BindRefusal.NotMarkedField);
                        selection = SelectionRead.Refused(SelectionRefusal.Unreadable);
                    }

                    var handler = CorrectionRequested;
                    Notify(() => handler?.Invoke(this, new CorrectionRequest(kind, mark, selection, fromPad)));
                });
                break;
            }

            case HotkeyAction.Mark:
            {
                var handler = MarkRequested;
                Notify(() => handler?.Invoke(this, EventArgs.Empty));
                break;
            }

            case HotkeyAction.DictationWindow:
            {
                // Das Feld beim Druck; erst dann zeigt die Anzeige das Fenster
                var (mark, _) = PressedField();
                var handler = DictationWindowRequested;
                Notify(() => handler?.Invoke(this, new DictationWindowRequest(mark)));
                break;
            }
        }
    }

    /// <summary>
    /// Das Feld mit dem Fokus im Moment des Tastendrucks, auf dem Thread der Taste (Codex zu PR #55): Nur so gehört es sicher
    /// zum Druck, auch wenn eine frühere Abfrage noch hängt. Die UIA-Zeitgrenze (2 s) begrenzt, wie lange das dauern kann.
    /// </summary>
    private (MarkResult Mark, bool FromPad) PressedField()
    {
        var fromPad = PadHasFocus();
        return (fromPad ? new MarkResult(null, BindRefusal.OwnProcess) : MarkTarget(), fromPad);
    }

    /// <summary>Hat das beim Druck erfasste Feld noch den Fokus? Ein Fehler heisst: nein.</summary>
    private bool StillFocused(TargetMark field)
    {
        try
        {
            return _marker?.IsFocused(field) == true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Hat das Diktatfenster den Fokus? Ein Fehler der Anzeige heisst: nein.</summary>
    private bool PadHasFocus()
    {
        try
        {
            return _pad?.HasFocus == true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lesen der Markierung zu einem beim Druck erfassten Feld, in eigener Reihe: Sie wartet nie hinter einem Diktatstart
    /// oder Übernehmen (Codex zu PR #55). Die Reihenfolge der Tasten bleibt; jeder Fehler wird beobachtet.
    /// </summary>
    private void Capture(Action action)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _captureWork = _captureWork.ContinueWith(
                _ =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // Ohne Erfassung keine Anfrage; die nächste Taste versucht es neu
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>Hängt eine Tastenaktion an die geordnete Arbeit an; jeder Fehler wird beobachtet.</summary>
    private void Enqueue(Func<Task> action)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _keyWork = _keyWork.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await action().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // Ein Diktat meldet seine Fehler selbst; nach dem Beenden wirkt keine Taste mehr
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    // ---- Anzeige ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Meldung aus dem laufenden Diktat (Hinweis, Befehl). Sie überschreibt nie den Grund, mit dem dieses Diktat endet
    /// oder abgebrochen wurde; die Anzeige wird trotzdem nachgeführt (Codex zu PR #47).
    /// </summary>
    private void Inform(Dictation dictation, ControllerMessage message, string? reason)
    {
        lock (_gate)
        {
            if (!dictation.FinalMessage)
            {
                SetMessage(message, reason, dictation);
            }
        }

        Publish();
    }

    /// <summary>Meldung ohne Diktat (Übernehmen, Zwischenablage, Sicherung, Tasten): jeder Aufruf ist ein Ereignis.</summary>
    private void Publish(ControllerMessage message, string? reason)
    {
        lock (_gate)
        {
            SetMessage(message, reason, null);
        }

        Publish();
    }

    /// <summary>
    /// Unter <see cref="_gate"/>: Jede gesetzte Meldung zählt für die Pille, auch eine gleiche. Fürs Protokoll gilt
    /// (Codex zu PR #56): Jede Meldung, die zu einem Diktat gehört, ist je Diktat (Code und Grund) genau ein Ereignis,
    /// gleich wie viele Schritte sie setzen und in welcher Phase (Abbrechen markiert und bricht dann ab, das Startende
    /// meldet nochmals). Darum gibt jede Stelle ihr Diktat ausdrücklich mit; <see cref="_current"/> ist dafür ungeeignet,
    /// weil es beim Startende schon leer ist. Ohne Diktat (<paramref name="dictation"/> null) ist jeder Aufruf ein Ereignis.
    /// </summary>
    private void SetMessage(ControllerMessage message, string? reason, Dictation? dictation)
    {
        _message = message;
        _reason = reason;
        _messageNumber++;
        if (message == ControllerMessage.None || (dictation is not null && !dictation.LoggedMessages.Add((message, reason))))
        {
            return;
        }

        _events.Add($"ereignis meldung code={message} grund={(string.IsNullOrWhiteSpace(reason) ? "-" : reason.Replace(' ', '_'))}");
    }

    /// <summary>
    /// Stand und Zustellung als Einheit nacheinander: Wer später veröffentlicht, liest den Stand später und stellt ihn
    /// später zu; so überholt nie ein älterer Stand einen neueren (U35-04). Der Zustandslock wird dabei nicht gehalten.
    /// </summary>
    private void Publish()
    {
        lock (_publishing)
        {
            ControllerUpdate update;
            string[] events;
            lock (_gate)
            {
                update = Snapshot();
                events = [.. _events];
                _events.Clear();
            }

            // Ein Fehler der Anzeige oder des Protokolls beendet kein Diktat; jeder Abonnent für sich
            foreach (var line in events)
            {
                Notify(() => _log?.Invoke(line));
            }

            Notify(() => _log?.Invoke(update.LogLine()));
            foreach (var handler in Updated?.GetInvocationList().Cast<EventHandler<ControllerUpdate>>() ?? [])
            {
                Notify(() => handler(this, update));
            }
        }
    }

    private static void Notify(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Abonnent fehlerhaft; der Stand bleibt gültig
        }
    }

    private ControllerUpdate Snapshot()
    {
        var availability = _recognizer.Availability;
        return new(
            _phase, availability, _target?.Display, _mode, _provisional, [.. _entries], _message, _reason, _notice,
            _recognizer.Info, availability == RecognizerAvailability.Unavailable ? _recognizer.LastError : null, _startMs, _stopMs,
            _focusWait is { } wait ? Max(wait.Remaining - Stopwatch.GetElapsedTime(wait.Since), TimeSpan.Zero) : null,
            _toPad, _dictationNumber, _messageNumber, _written,
            _entries.Count(IsOpenHeld),
            _microphone,
            _current is null ? MicrophoneSignal.Ok : _signal);
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    /// <summary>Entwurf eines beendeten Diktats und seine noch offenen gehaltenen Abschnitte.</summary>
    private sealed record HeldDraft(DraftRecorder Recorder, HashSet<Guid> Open);

    /// <summary>Was zu einem Diktat gehört; freigegeben genau einmal.</summary>
    /// <summary>Prüfaufnahme dieses Diktats; ein Fehler beim Beginnen lässt das Diktat ohne Aufnahme laufen.</summary>
    private IReviewRecording? Review(Dictation dictation)
    {
        try
        {
            int number;
            lock (_gate)
            {
                number = _dictationNumber;
            }

            return _review?.Begin(number) is { } recording ? new ShieldedRecording(recording) : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Nach dem Diktat, nur Zahlen und der Dateiname (SI-01): Rechenzeit der Erkennung (P11, Kapitel 1, „Warmhalten“) und
    /// die Prüfaufnahme, damit eine folgende Marke („Problem merken“) ihr zugeordnet werden kann.
    /// </summary>
    private void FinishReview(Dictation dictation, DecodeStatistics decodes)
    {
        string? name = null;
        try
        {
            name = dictation.Review?.Complete();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Die Prüfaufnahme darf kein Diktat stören
        }

        lock (_gate)
        {
            if (decodes.Count > 0)
            {
                _events.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"ereignis erkennung decodes={decodes.Count} rechenzeit_ms={(long)decodes.Total.TotalMilliseconds} laengste_ms={(long)decodes.Longest.TotalMilliseconds}"));
            }

            if (name is not null)
            {
                _events.Add("ereignis pruefaufnahme datei=" + name);
            }
        }
    }

    /// <summary>
    /// Regel (Codex-Prüfung PR #60): Die Prüfaufnahme ist nur Beobachter. Jeder Aufruf ist abgeschirmt; ein Fehler darin
    /// ändert weder das Diktat noch das Ergebnis eines Abschnitts, er beendet nur die Aufnahme.
    /// </summary>
    private sealed class ShieldedRecording(IReviewRecording inner) : IReviewRecording
    {
        private volatile bool _failed;

        public void Audio(AudioChunk chunk) => Guard(() => inner.Audio(chunk));

        public void Section(TextSegment segment, OutputReport report) => Guard(() => inner.Section(segment, report));

        public string? Complete()
        {
            if (_failed)
            {
                return null;
            }

            try
            {
                return inner.Complete();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return null;
            }
        }

        private void Guard(Action call)
        {
            if (_failed)
            {
                return;
            }

            try
            {
                call();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _failed = true;
            }
        }
    }

    private sealed class Dictation
    {
        public CancellationTokenSource Cancel { get; } = new();

        /// <summary>Erledigt, sobald der Start entschieden ist.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Stopwatch-Zeitstempel des Tastendrucks, der dieses Diktat startete; null ohne Taste.</summary>
        public long? PressedAt { get; init; }

        /// <summary>Beim Druck gemerktes Feld oder Schreibfläche (Audit F1); null ohne Taste.</summary>
        public (MarkResult Mark, bool FromPad)? PressedField { get; init; }

        /// <summary>Gerät beim Start dieses Diktats (B3); null heisst Standardgerät.</summary>
        public string? DeviceId { get; init; }

        /// <summary>Stoppuhr-Zeitpunkt der Audiozeit 0 (erster Block); null, bis Ton kommt.</summary>
        public long? AudioZero { get; set; }

        /// <summary>Schon protokollierte Meldungen dieses Diktats (Code, Grund); nur unter <c>_gate</c>.</summary>
        public HashSet<(ControllerMessage Message, string? Reason)> LoggedMessages { get; } = [];

        public TargetInfo? Target { get; set; }

        public bool Armed { get; set; }

        /// <summary>Der Grund, mit dem dieses Diktat endet, steht fest; Meldungen aus dem Diktat überschreiben ihn nicht.</summary>
        public bool FinalMessage { get; set; }

        public EventHandler<GuardReason>? GuardHandler { get; set; }

        public GuardReason? StopReason { get; set; }

        public IDictationRun? Run { get; set; }

        public ICaptureSession? Capture { get; set; }

        /// <summary>Stopp der Aufnahme ausgelöst; vollendet, sobald er zurückgekehrt ist.</summary>
        public TaskCompletionSource? Stopping { get; set; }

        public EventHandler<CaptureStatus>? CaptureHandler { get; set; }

        /// <summary>Schreibt ins Diktatfenster statt in ein fremdes Feld (E2).</summary>
        public bool ToPad { get; set; }

        /// <summary>Sicherung dieses Diktats (E1); null ohne Sicherung.</summary>
        public DraftRecorder? Draft { get; set; }

        /// <summary>Kennungen aller festen Abschnitte dieses Diktats; unter der Sperre der Steuerung.</summary>
        public HashSet<Guid> Fixed { get; } = [];

        /// <summary>Ganz geschriebene Abschnitte; unter der Sperre der Steuerung.</summary>
        public HashSet<Guid> WrittenIds { get; } = [];

        /// <summary>Ein abschliessender Grund ist gesetzt (<see cref="Stop"/>); beendet freiwillige Arbeit nach dem Diktat.</summary>
        public CancellationTokenSource Ended { get; } = new();

        /// <summary>Der zuletzt feste Abschnitt; unter der Sperre der Steuerung.</summary>
        public TextSegment? LastFixed { get; set; }
        /// <summary>Prüfaufnahme dieses Diktats (P11-E1); null ohne.</summary>
        public IReviewRecording? Review { get; set; }
    }
}
