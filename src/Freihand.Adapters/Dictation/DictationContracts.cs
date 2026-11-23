using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;

namespace Freihand.Adapters.Dictation;

// Verträge des P9-Durchstichs.

/// <summary>Ein laufendes Diktat: P3-Sitzung auf einer Erkennersitzung, Modus fest für das ganze Diktat (ER-20).</summary>
public interface IDictationRun : IAsyncDisposable
{
    /// <summary>Wirksamer Modus samt Grund, aufgelöst mit dem tatsächlich geladenen Erkenner (ER-19, P8 Kapitel 5.9).</summary>
    ModeDecision Mode { get; }

    /// <summary>Einmal aufrufbar; endet regulär, wenn das Audio endet und alles verarbeitet ist (P3 Vertrag 6).</summary>
    IAsyncEnumerable<DictationOutput> RunAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken);

    /// <summary>Befehlstaste (BE-19).</summary>
    void PressCommandKey();

    /// <summary>Bisherige Aufrufe der Erkennung und ihre Rechenzeit (P11, Kapitel 1); leer, wo nicht gemessen.</summary>
    DecodeStatistics Decodes => default;
}

/// <summary>Startet je Diktat eine Erkennersitzung und die P3-Sitzung darauf.</summary>
public interface IDictationFactory
{
    /// <exception cref="RecognizerUnavailableException">Erkenner nicht bereit oder Zeitgrenze.</exception>
    /// <exception cref="RecognizerException">Erkenner lehnt die Sitzung ab.</exception>
    Task<IDictationRun> StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Wie <see cref="StartAsync(CancellationToken)"/> mit dem Gerät, das die Steuerung beim Start dieses Diktats gewählt hat
    /// (B3): Modus und Mikrofon gehören so immer zum selben Gerät.
    /// </summary>
    Task<IDictationRun> StartAsync(string? deviceId, CancellationToken cancellationToken) => StartAsync(cancellationToken);
}

/// <summary>
/// Schreibfläche des Diktatfensters als Ziel. Aufrufbar aus jedem
/// Thread; die Umsetzung leitet in den UI-Thread. Liest nur den eigenen Text, nie fremde Inhalte (SI-12).
/// </summary>
public interface IDictationPad
{
    /// <summary>Ist das Diktatfenster aktiv und hat seine Schreibfläche den Tastaturfokus?</summary>
    bool HasFocus { get; }

    /// <summary>Fügt festen Text an der Schreibmarke ein (Fügeregel <see cref="PadText"/>); liefert das Eingefügte.</summary>
    string Insert(string text);
}

/// <summary>
/// Der ganze Text der Schreibfläche, austauschbar und mit Rückgängig (Kapitel 7: Platz für „Umformen“, BA-10). Der
/// Ausbau ersetzt den Text nur über <see cref="ReplaceAll"/>, damit Strg+Z ihn wiederherstellt.
/// </summary>
public interface IDictationPadText
{
    string Text { get; }

    /// <summary>Ersetzt den ganzen Text als ein Schritt, den Rückgängig zurücknimmt.</summary>
    void ReplaceAll(string text);
}

/// <summary>Zwischenablage; nur auf ausdrücklichen Befehl für einen gehaltenen Abschnitt (D4 b, AU-03).</summary>
public interface IClipboard
{
    void SetText(string text);
}

public enum ControllerPhase
{
    /// <summary>Kein Diktat.</summary>
    Idle,

    /// <summary>Binden, Wächter, Erkennersitzung, Mikrofon öffnen; noch nicht „aufnehmend“.</summary>
    Starting,

    /// <summary>Ab <see cref="CapturePhase.Recording"/> (P8 Kapitel 5.2).</summary>
    Recording,

    /// <summary>Mikrofon zu; schon Aufgenommenes wird zu Ende verarbeitet und ausgegeben.</summary>
    Stopping,
}

/// <summary>Was die Anzeige meldet; Gründe nur als Code (SI-01).</summary>
public enum ControllerMessage
{
    None,
    RecognizerNotReady,
    RecognizerUnavailable,
    BindRefused,
    GuardStopped,
    GuardFailed,
    NoDevice,
    CaptureFailed,
    CaptureStopped,
    DictationFailed,
    Cancelled,
    HotkeyConflict,
    ClipboardRefused,
    Notice,

    /// <summary>Ein erkannter Befehl; der Pilot führt Befehle noch nicht aus. Grund ist die Kennung des Befehls.</summary>
    CommandNotSupported,

    /// <summary>Befehlstaste ohne gültigen Befehl (P3 Kapitel 5.6).</summary>
    CommandHint,

    /// <summary>Sichern oder Löschen der Sicherung gescheitert (ER-06); Grund ist die Art, nie Text oder Pfad.</summary>
    RecoveryFailed,

    /// <summary>Übernehmen aus dem Diktatfenster nicht oder nicht ganz geschrieben (AU-10); der Text bleibt dort.</summary>
    HandOverRefused,
}

public enum EntryStatus
{
    Written,
    Held,
    Refused,
}

/// <summary>Ein fester Abschnitt und was mit ihm geschah (D1 a).</summary>
/// <param name="Written">Ins Ziel eingefügter Text (<see cref="OutputReport.Written"/>).</param>
/// <param name="Held">Gehaltener Rest (<see cref="OutputReport.Held"/>); leer bei <see cref="EntryStatus.Written"/>.</param>
/// <param name="CheckEnd">Der Abschnitt endet an einem Längenschnitt; dort können Wörter fehlen oder doppelt sein (T78).</param>
/// <param name="Taken">Der gehaltene Rest ist kopiert oder ins Diktatfenster geholt (P9 Diktatfenster, Kapitel 3.4).</param>
public sealed record OutputEntry(
    Guid SegmentId, EntryStatus Status, OutputReason Reason, string Written, string Held, bool CheckEnd = false, bool Taken = false);

/// <summary>
/// Zustand des Mikrofons während der Aufnahme (Anzeige „Mikrofon stumm“, Bedienregel vom 19.11.2026). Nur der
/// Stummschalter von Windows zählt: Exakte Nullen sind kein verlässliches Zeichen, weil das Monitormikrofon Geräusche selbst
/// wegschaltet und in Pausen Nullen liefert, und ein Headset mit hochgeklapptem Bügel rauscht statt Nullen zu liefern
/// (Messungen vom 09.11.2026).
/// </summary>
public enum MicrophoneSignal
{
    /// <summary>Nicht stumm geschaltet, soweit Windows es meldet.</summary>
    Ok,

    /// <summary>In Windows stumm geschaltet (Mikrofontaste, Lautstärkemenü, manche Headsets).</summary>
    Muted,
}

/// <summary>
/// Stand für die Anzeige. Enthält Text für das Statusfenster (Vorschau, Einträge), aber <see cref="LogLine"/> nie: Das
/// Protokoll trägt nur Phase, Erkenner, Modus, Gründe und Zahlen (SI-01).
/// </summary>
/// <param name="Target">Anzeige der Bindung (<see cref="TargetInfo.Display"/>), null ohne Bindung.</param>
/// <param name="Reason">Code des Grunds zur <see cref="Message"/> (z. B. <c>PasswordField</c>), nie Text.</param>
/// <param name="Notice">
/// Letzter Hinweis von P3 samt Verlustspanne (U35-02). Ein abschliessender Hinweis (Decoder- oder Detektorfehler, voller
/// Puffer) bleibt bis zum nächsten Diktat stehen; eine Entwarnung ersetzt die Rückstandswarnung.
/// </param>
/// <param name="RecognizerInfo">Was der Erkenner geladen hat: Modell, tatsächliche Laufzeit, Rückfall (U35-03).</param>
/// <param name="RecognizerError">Bleibender Fehler des Erkenners, nur solange er nicht verfügbar ist (U35-03).</param>
/// <param name="StartMs">Messwert RM10: Millisekunden vom Tastendruck bis <see cref="ControllerPhase.Recording"/> im laufenden Diktat; null ohne Tastendruck oder vor Recording.</param>
/// <param name="StopMs">Messwert RM11: Millisekunden vom letzten Audioblock bis zum Stopp der Aufnahme bei einem Fehlerstopp; null sonst.</param>
/// <param name="FocusWait">Restzeit, solange die Ausgabe auf die Rückkehr des Fokus wartet (Pille „Fokus weg“); sonst null.</param>
/// <param name="ToDictationWindow">Das laufende oder letzte Diktat schreibt ins Diktatfenster (E2).</param>
/// <param name="Dictation">Nummer des laufenden oder letzten Diktatversuchs, ab 1; 0 vor dem ersten.</param>
/// <param name="MessageNumber">Zählt jede gesetzte Meldung; die Anzeige erkennt so eine neue gleiche Meldung.</param>
/// <param name="Written">Ganz geschriebene Abschnitte des laufenden oder letzten Diktats.</param>
/// <param name="HeldOpen">Gehaltene Abschnitte des letzten Diktats, die weder kopiert noch ins Diktatfenster geholt sind.</param>
/// <param name="Microphone">Name des Mikrofons im laufenden Diktat (Details der Pille); null ohne Aufnahme.</param>
/// <param name="Signal">Stummschalter von Windows im laufenden Diktat; im Leerlauf immer <see cref="MicrophoneSignal.Ok"/>.</param>
public sealed record ControllerUpdate(
    ControllerPhase Phase,
    RecognizerAvailability Recognizer,
    string? Target,
    ModeDecision? Mode,
    string Provisional,
    IReadOnlyList<OutputEntry> Entries,
    ControllerMessage Message,
    string? Reason,
    LiveNotice? Notice = null,
    RecognizerInfo? RecognizerInfo = null,
    RecognizerErrorCode? RecognizerError = null,
    int? StartMs = null,
    int? StopMs = null,
    TimeSpan? FocusWait = null,
    bool ToDictationWindow = false,
    int Dictation = 0,
    int MessageNumber = 0,
    int Written = 0,
    int HeldOpen = 0,
    string? Microphone = null,
    MicrophoneSignal Signal = MicrophoneSignal.Ok)
{
    /// <summary>Protokollzeile ohne Text, ohne Zielanzeige (sie nennt Programme) und ohne Inhalte (SI-01).</summary>
    public string LogLine() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"phase={Phase} erkenner={Recognizer} modus={Mode?.Mode.ToString() ?? "-"}/{Mode?.Reason.ToString() ?? "-"} " +
        $"ziel={(Target is null ? "nein" : "ja")} vorschau={(Provisional.Length > 0 ? "ja" : "nein")} " +
        $"geschrieben={Entries.Count(entry => entry.Status == EntryStatus.Written)} " +
        $"gehalten={Entries.Count(entry => entry.Status != EntryStatus.Written)} meldung={Message} grund={Reason ?? "-"} " +
        $"fenster={(ToDictationWindow ? "ja" : "nein")} fokus_warten={(FocusWait is null ? "nein" : "ja")} " +
        $"hinweis={Notice?.Kind.ToString() ?? "-"} erkennerfehler={RecognizerError?.ToString() ?? "-"} " +
        $"start_ms={StartMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} " +
        $"stopp_ms={StopMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}" +
        $"{(Signal == MicrophoneSignal.Muted ? " mikrofon=stumm" : string.Empty)}");
}

/// <param name="Strictness">Strenge des Profils: Binden (SI-04) und Zwischenablage (AU-03).</param>
/// <param name="DeviceId">Gewähltes Gerät; null heisst Standardgerät von Windows.</param>
/// <param name="Hotkeys">Belegung aus der Einstellung (D2, D6).</param>
public sealed record ControllerOptions(
    ProfileStrictness Strictness,
    string? DeviceId,
    IReadOnlyDictionary<HotkeyAction, HotkeyGesture> Hotkeys)
{
    /// <summary>
    /// So lange wartet „Übernehmen“, bis nach dem Ausblenden des Diktatfensters das gemerkte Feld den Fokus hat; danach
    /// bleibt der Text im Diktatfenster (AU-10).
    /// </summary>
    public TimeSpan HandOverSettle { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Nach einem Diktat in ein fremdes Feld ein Leerzeichen, damit das nächste Diktat nicht anklebt (Handtest P9 vom
    /// 11.11.2026). Nur wenn der letzte Abschnitt ganz geschrieben ist; die App schaltet es ein.
    /// </summary>
    public bool SpaceAfterDictation { get; init; }

    /// <summary>
    /// So lange darf das Leerzeichen nach dem Diktat warten (eigene Tasten loslassen, Fokus zurück); danach entfällt es
    /// still, damit es das Ende des Diktats nie bis zur Frist der Fokusrückkehr aufhält.
    /// </summary>
    public TimeSpan SpaceAfterDictationLimit { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Was „Übernehmen“ erreicht hat (AU-10).</summary>
public enum HandOverResult
{
    /// <summary>Ganz ins gemerkte Feld geschrieben.</summary>
    Written,

    /// <summary>Teilweise geschrieben; <see cref="HandOverReport.Held"/> bleibt im Diktatfenster.</summary>
    Partial,

    /// <summary>Nichts geschrieben; der Text bleibt im Diktatfenster.</summary>
    Refused,
}

/// <param name="Held">Was nicht geschrieben wurde und im Diktatfenster bleibt.</param>
/// <param name="Reason">Code des Grunds (<see cref="BindRefusal"/>, <see cref="OutputReason"/> oder „Busy“), nie Text.</param>
public sealed record HandOverReport(HandOverResult Result, string Held, string? Reason);

/// <summary>Taste fürs Diktatfenster: das Feld, das dabei den Fokus hatte, falls es bindbar ist (AU-10).</summary>
public sealed class DictationWindowRequest(MarkResult mark) : EventArgs
{
    public MarkResult Mark { get; } = mark;
}

/// <summary>
/// Taste fürs Korrigieren oder Glätten: das Feld mit dem Fokus und seine Markierung, beide
/// im Moment des Tastendrucks. Aus dem Diktatfenster heraus (<see cref="FromPad"/>) wird nichts gemerkt und nichts gelesen.
/// </summary>
public sealed class CorrectionRequest(Transform.CorrectionKind kind, MarkResult mark, SelectionRead selection, bool fromPad) : EventArgs
{
    public Transform.CorrectionKind Kind { get; } = kind;

    public MarkResult Mark { get; } = mark;

    /// <summary>Fremdtext nur im Arbeitsspeicher (SI-12); nie protokolliert.</summary>
    public SelectionRead Selection { get; } = selection;

    public bool FromPad { get; } = fromPad;
}
