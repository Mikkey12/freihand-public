using Freihand.Core.Live;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;

namespace Freihand.Adapters.Recognition;

/// <summary>Laufzeit von whisper.cpp.</summary>
public enum RecognizerRuntime
{
    Cuda,
    Vulkan,
    Cpu,
}

/// <summary>Einstellungen von Silero VAD (P8-Konzept, Kapitel 4.4 und 5.6).</summary>
/// <param name="Threshold">Startwert 0,5.</param>
/// <param name="MinSpeech">Startwert 0,1 s (P3, T33: 180 ms müssen durchkommen).</param>
/// <param name="MinSilence">Startwert 0,3 s (wie gemessen).</param>
/// <param name="SpeechPad">Startwert 30 ms.</param>
public sealed record VadSettings(float Threshold, TimeSpan MinSpeech, TimeSpan MinSilence, TimeSpan SpeechPad)
{
    public static VadSettings Default { get; } =
        new(0.5f, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(30));
}

/// <summary>Konfiguration des Erkennungsprozesses, ohne Geheimnisse; geht mit <c>configure</c> über die Pipe.</summary>
public sealed record RecognizerConfiguration(
    string ModelPath,
    string ModelSha256,
    IReadOnlyList<RecognizerRuntime> RuntimeOrder,
    int Threads,
    int AudioContext,
    string VadModelPath,
    string VadSha256,
    VadSettings Vad);

/// <summary>Was der Erkenner tatsächlich geladen hat; Inhalt der Meldung <c>ready</c>.</summary>
/// <param name="ModelFile">Dateiname der geladenen Modelldatei ohne Pfad, wie im Inventar (z. B. ggml-large-v3-turbo.bin).</param>
/// <param name="ModelSha256">Geprüfte Prüfsumme.</param>
/// <param name="Runtime">Tatsächlich geladene Laufzeit, nicht die gewünschte.</param>
/// <param name="RuntimeFallback">Die Laufzeit ist nicht der erste Eintrag aus <see cref="RecognizerConfiguration.RuntimeOrder"/>.</param>
/// <param name="ModelLoad">Dauer von Prüfung und Laden.</param>
/// <param name="WhisperNetVersion">Version von Whisper.net.</param>
public sealed record RecognizerInfo(
    string ModelFile,
    string ModelSha256,
    RecognizerRuntime Runtime,
    bool RuntimeFallback,
    TimeSpan ModelLoad,
    string WhisperNetVersion)
{
    /// <summary>Schlüssel für die Live-Abnahme (P2 v2): Model = ModelFile, Runtime = „cuda“, „vulkan“ oder „cpu“.</summary>
    public RecognizerIdentity Identity => new(ModelFile, RecognizerRuntimeNames.ToApprovalName(Runtime));
}

/// <summary>Namen der Laufzeiten in der Live-Abnahme (<see cref="KnownRuntimes"/>).</summary>
public static class RecognizerRuntimeNames
{
    /// <summary>Cuda → „cuda“, Vulkan → „vulkan“, Cpu → „cpu“.</summary>
    public static string ToApprovalName(RecognizerRuntime runtime) => runtime switch
    {
        RecognizerRuntime.Cuda => KnownRuntimes.Cuda,
        RecognizerRuntime.Vulkan => KnownRuntimes.Vulkan,
        RecognizerRuntime.Cpu => KnownRuntimes.Cpu,
        _ => throw new ArgumentOutOfRangeException(nameof(runtime)),
    };
}

/// <summary>Art eines Prozessstarts des Erkenners.</summary>
public enum RecognizerLaunch
{
    /// <summary>Erster Start mit der App.</summary>
    First,

    /// <summary>Automatischer Neustart nach Absturz, Abbruch oder Zeitgrenze.</summary>
    Automatic,

    /// <summary>„Erkennung neu starten“ von Hand.</summary>
    Manual,
}

public enum RecognizerAvailability
{
    Starting,
    Ready,
    Restarting,
    Unavailable,
}

/// <summary>Fehlercodes des Protokolls (Kapitel 4.7); Meldungen tragen nur den Code (SI-01).</summary>
public enum RecognizerErrorCode
{
    InvalidRequest,
    AudioTooLong,
    UnsupportedLanguage,
    DecoderError,
    TokenAlignment,
    VadError,
    ModelMissing,
    ModelChecksum,
    RuntimeUnavailable,
    InvalidConfiguration,
    ProtocolVersion,
    ProtocolViolation,
}

/// <summary>Fehler mit Code; Meldung nur mit dem Code, nie Text oder Audio (SI-01).</summary>
public sealed class RecognizerException(RecognizerErrorCode code) : Exception($"Erkennung gescheitert: {code}.")
{
    public RecognizerErrorCode Code { get; } = code;
}

/// <summary>Erkenner nicht erreichbar: Prozess weg, Zeitgrenze, Verbindung getrennt, Neustart oder Unavailable.</summary>
public sealed class RecognizerUnavailableException : Exception
{
    public RecognizerUnavailableException()
        : base("Erkennung nicht verfügbar.")
    {
    }
}

/// <summary>Zeitgrenzen des Clients (P8-Konzept, Kapitel 4.6 und 5.7).</summary>
/// <param name="DecodeBase">Startwert 30 s; Grenze je Decode = DecodeBase + 5 × Audiodauer.</param>
/// <param name="Detect">Startwert 2 s.</param>
/// <param name="RestartWait">Startwert 15 s: so lange wartet ein Decode auf einen laufenden Neustart.</param>
/// <param name="ModelLoad">Startwert 120 s, samt Prüfsumme.</param>
public sealed record ClientTimeouts(TimeSpan DecodeBase, TimeSpan Detect, TimeSpan RestartWait, TimeSpan ModelLoad)
{
    public static ClientTimeouts Default { get; } =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(120));
}

/// <summary>Der Erkenner aus Sicht der App; umgesetzt von <see cref="RecognizerSupervisor"/>.</summary>
public interface IRecognizerHost : IAsyncDisposable
{
    RecognizerAvailability Availability { get; }

    /// <summary>Stand nach dem letzten <c>ready</c>; null vor dem ersten.</summary>
    RecognizerInfo? Info { get; }

    /// <summary>Code des bleibenden Fehlers bei <see cref="RecognizerAvailability.Unavailable"/>, sonst null.</summary>
    /// <remarks>Ergänzung zu Kapitel 4.4, damit P9 den Grund anzeigen kann (R28, R29, R45).</remarks>
    RecognizerErrorCode? LastError { get; }

    event EventHandler<RecognizerAvailability>? AvailabilityChanged;

    /// <summary>
    /// Startversuch eines Erkennerprozesses (Aufruf des Launchers, auch wenn der Prozess danach scheitert), genau einmal je
    /// Versuch und mit seiner Art; ein zurückgehaltener Neustart meldet sich erst, wenn er wirklich startet (Beta-Protokoll,
    /// Codex zu PR #69). Ohne Standardumsetzung, damit keine Umsetzung Neustarts still verliert.
    /// </summary>
    event EventHandler<RecognizerLaunch>? Launched;

    /// <summary>
    /// Wartet höchstens <see cref="ClientTimeouts.ModelLoad"/> auf Ready und gibt eine Sitzung aus (Kapitel 5.5).
    /// </summary>
    /// <exception cref="RecognizerUnavailableException">Unavailable oder Zeitgrenze.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="LiveOptions.Window"/> oder <see cref="LiveOptions.MaxUtterance"/> über 20 s.</exception>
    /// <exception cref="RecognizerException">Sprache nicht abbildbar (<see cref="RecognizerErrorCode.UnsupportedLanguage"/>).</exception>
    Task<RecognizerSession> StartSessionAsync(
        RecognitionOptions options, LiveOptions live, CancellationToken cancellationToken = default);

    /// <summary>Von Hand nach Unavailable.</summary>
    Task RestartAsync();
}

/// <summary>
/// Eine Erkennersitzung je Diktat; P3 gibt Decoder und Detektor nicht frei. Sie rechnet auf dem jeweils laufenden
/// Erkenner, solange dessen Modell und Laufzeit <see cref="Info"/> gleichen; ein Detektorfehler oder ein Wechsel der
/// Identität lässt sie scheitern (Kapitel 5.7).
/// </summary>
public sealed class RecognizerSession : IAsyncDisposable
{
    private readonly RecognizerSupervisor _owner;
    private readonly Lock _statistics = new();
    private DecodeStatistics _decodes;
    private int _failed;

    internal RecognizerSession(RecognizerSupervisor owner, RecognizerInfo info)
    {
        _owner = owner;
        Info = info;
        Decoder = new SessionDecoder(this);
        Detector = new SessionDetector(this);
        owner.Readied += OnReadied;
    }

    public IHypothesisDecoder Decoder { get; }

    public ISpeechDetector Detector { get; }

    /// <summary>Stand nach „ready“, auf dem diese Sitzung rechnet.</summary>
    public RecognizerInfo Info { get; }

    /// <summary>Gelungene Aufrufe der Erkennung dieser Sitzung mit ihrer Rechenzeit (P11, Kapitel 1); nur Zahlen.</summary>
    public DecodeStatistics Decodes
    {
        get
        {
            lock (_statistics)
            {
                return _decodes;
            }
        }
    }

    /// <summary>Nach einem Detektorfehler oder Identitätswechsel (Kapitel 5.7); danach wirft jeder Aufruf sofort.</summary>
    public bool Failed => Volatile.Read(ref _failed) == 1;

    public ValueTask DisposeAsync()
    {
        _owner.Readied -= OnReadied;
        return ValueTask.CompletedTask;
    }

    /// <summary>Ein Neustart mit anderem Modell oder anderer Laufzeit lässt die Sitzung sofort scheitern (Kapitel 5.9).</summary>
    private void OnReadied(RecognizerInfo current)
    {
        if (current.ModelFile != Info.ModelFile || current.Runtime != Info.Runtime)
        {
            Volatile.Write(ref _failed, 1);
        }
    }

    private RecognizerUnavailableException Fail()
    {
        Volatile.Write(ref _failed, 1);
        return new RecognizerUnavailableException();
    }

    /// <summary>Der Client des laufenden Erkenners; ein anderes Modell oder eine andere Laufzeit lässt die Sitzung scheitern.</summary>
    private RecognizerClient Matching(RecognizerInfo current, RecognizerClient client) =>
        !Failed && current.ModelFile == Info.ModelFile && current.Runtime == Info.Runtime ? client : throw Fail();

    private sealed class SessionDecoder(RecognizerSession session) : IHypothesisDecoder
    {
        public async Task<RecognitionHypothesis> DecodeAsync(
            ReadOnlyMemory<float> audio, TimeSpan offset, RecognitionOptions options, CancellationToken cancellationToken = default)
        {
            if (session.Failed)
            {
                throw new RecognizerUnavailableException();
            }

            // Während eines Neustarts höchstens RestartWait warten (Tabelle 5.7)
            var (info, client) = await session._owner.WaitForClientAsync(cancellationToken).ConfigureAwait(false);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var hypothesis = await session.Matching(info, client).DecodeAsync(audio, offset, options, cancellationToken).ConfigureAwait(false);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            lock (session._statistics)
            {
                var before = session._decodes;
                session._decodes = new(before.Count + 1, before.Total + elapsed, elapsed > before.Longest ? elapsed : before.Longest);
            }

            return hypothesis;
        }
    }

    private sealed class SessionDetector(RecognizerSession session) : ISpeechDetector
    {
        public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset)
        {
            if (session.Failed)
            {
                throw new RecognizerUnavailableException();
            }

            // Detect wartet nie auf einen Neustart: ohne laufenden Erkenner scheitert die Sitzung (Tabelle 5.7)
            if (session._owner.CurrentClient() is not var (info, client))
            {
                throw session.Fail();
            }

            try
            {
                return session.Matching(info, client).Detect(audio, offset);
            }
            catch (RecognizerUnavailableException)
            {
                throw session.Fail();
            }
        }
    }
}

/// <summary>Eine angenommene Verbindung samt Prozesskennung des Clients (<c>GetNamedPipeClientProcessId</c>).</summary>
public sealed record PipeConnection(Stream Stream, int ClientProcessId);

/// <summary>Pipe-Server und Prozessstart (Freihand.Windows; Stufe A: Attrappe).</summary>
public interface IRecognizerLauncher
{
    Task<LaunchedRecognizer> LaunchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Ein gestarteter Erkennungsprozess.</summary>
/// <param name="ProcessId">Prozesskennung des gestarteten Erkenners.</param>
/// <param name="Exited">Endet, wenn der Prozess endet.</param>
/// <param name="AcceptAsync">Nächste Verbindung samt Prozesskennung des Clients.</param>
/// <param name="Kill">Beendet den Prozess.</param>
public sealed record LaunchedRecognizer(
    int ProcessId,
    Task Exited,
    Func<CancellationToken, Task<PipeConnection>> AcceptAsync,
    Action Kill);

/// <summary>Anzahl, gesamte und längste Rechenzeit der Erkennung in einem Diktat (P11, Kapitel 1).</summary>
public readonly record struct DecodeStatistics(int Count, TimeSpan Total, TimeSpan Longest);
