using Freihand.Adapters.Recognition;
using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Diktat auf dem P8-Erkenner (P8-Konzept, Kapitel 5.9): Erkennersitzung, dann der Modus aus
/// <see cref="RecognitionModeResolver"/> mit dem tatsächlich geladenen Modell und der tatsächlichen Laufzeit, dann die
/// P3-Sitzung mit diesem Modus.
/// </summary>
public sealed class RecognizerDictationFactory : IDictationFactory
{
    private readonly IRecognizerHost _recognizer;
    private readonly Profile _profile;
    private readonly string? _deviceId;
    private readonly LiveApprovals _approvals;
    private readonly RecognitionOptions _recognition;
    private readonly LiveOptions _live;
    private readonly CommandSet _commands;
    private readonly InterpreterOptions _interpreting;
    private readonly bool _reportHeard;

    public RecognizerDictationFactory(
        IRecognizerHost recognizer,
        Profile profile,
        string? deviceId,
        LiveApprovals approvals,
        RecognitionOptions recognition,
        LiveOptions live,
        CommandSet commands,
        InterpreterOptions interpreting)
        : this(recognizer, profile, deviceId, approvals, recognition, live, commands, interpreting, reportHeard: false)
    {
    }

    private RecognizerDictationFactory(
        IRecognizerHost recognizer,
        Profile profile,
        string? deviceId,
        LiveApprovals approvals,
        RecognitionOptions recognition,
        LiveOptions live,
        CommandSet commands,
        InterpreterOptions interpreting,
        bool reportHeard)
    {
        _reportHeard = reportHeard;
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(recognition);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(interpreting);
        _recognizer = recognizer;
        _profile = profile;
        _deviceId = deviceId;
        _approvals = approvals;
        _recognition = recognition;
        _live = live;
        _commands = commands;
        _interpreting = interpreting;
    }

    /// <summary>
    /// Gelernte Wörter (P11 S4), bei jedem Diktatbeginn neu gelesen, damit Änderungen ab dem nächsten Diktat gelten. Darf
    /// nicht werfen; ohne Angabe gilt keine Ersetzung.
    /// </summary>
    public Func<WordReplacements>? Replacements { get; init; }

    /// <summary>Auslegung, mit der diese Fabrik Diktate baut (ohne die je Diktat gelesenen Ersetzungen).</summary>
    public InterpreterOptions InterpreterOptions => _interpreting;

    /// <summary>
    /// Dieselbe Kette fürs Lernen (P11 S4): ohne Ersetzungen und ohne Formatter. Regel: Gelernt wird genau die Form, die
    /// das Ersetzen sieht, und ersetzt wird vor dem Formatter; sonst würde z. B. „Maßman“ als „Massman“ gelernt und beim
    /// Diktat nie gefunden (eigene Vorprüfung zu PR #66).
    /// </summary>
    public RecognizerDictationFactory ForLearning() =>
        new(_recognizer, _profile, _deviceId, _approvals, _recognition, _live, _commands,
            _interpreting with { Formatter = null, Replacements = WordReplacements.Empty }, reportHeard: true);

    /// <summary>Die Lernfabrik meldet jede Äusserung mit ihren Wörtern (<see cref="HeardOutput"/>).</summary>
    public bool ReportsHeard => _reportHeard;

    /// <summary>Modus und P3-Optionen für ein Diktat mit diesem Erkenner (X13); der Modus gilt für das ganze Diktat.</summary>
    public static (ModeDecision Mode, LiveOptions Live) Options(
        Profile profile, string? deviceId, RecognizerIdentity? recognizer, LiveApprovals approvals, LiveOptions live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var mode = RecognitionModeResolver.Resolve(profile, deviceId, recognizer, approvals);
        return (mode, live with { Mode = mode.Mode });
    }

    /// <summary>Wartet auf den Erkenner, löst den Modus mit der Identität der Sitzung auf und baut die P3-Sitzung darauf.</summary>
    public Task<IDictationRun> StartAsync(CancellationToken cancellationToken) => StartAsync(_deviceId, cancellationToken);

    /// <summary>Wie oben mit dem Gerät dieses Diktats (B3: Mikrofon im Menü, gilt ab dem nächsten Diktat).</summary>
    public async Task<IDictationRun> StartAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var session = await _recognizer.StartSessionAsync(_recognition, _live, cancellationToken).ConfigureAwait(false);
        try
        {
            var (mode, live) = Options(_profile, deviceId, session.Info.Identity, _approvals, _live);
            var transcription = new LiveTranscription(session.Decoder, session.Detector, _recognition, live);
            var dictation = new DictationSession(
                transcription, new DictationInterpreter(_commands, Interpreting()), () => CommandScope.Everywhere, _reportHeard);
            return new Run(session, dictation, mode);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private InterpreterOptions Interpreting() =>
        Replacements is { } load ? _interpreting with { Replacements = load() } : _interpreting;

    private sealed class Run(RecognizerSession session, DictationSession dictation, ModeDecision mode) : IDictationRun
    {
        public ModeDecision Mode { get; } = mode;

        public IAsyncEnumerable<DictationOutput> RunAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken) =>
            dictation.RunAsync(audio, cancellationToken);

        public void PressCommandKey() => dictation.PressCommandKey();

        public DecodeStatistics Decodes => session.Decodes;

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}
