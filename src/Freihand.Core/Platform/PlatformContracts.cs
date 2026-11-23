using Freihand.Core.Output;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;

namespace Freihand.Core.Platform;

// ---- Tastenkombinationen (P7-Konzept, Kapitel 4.1 und 5.1–5.3) ---------------------------------------------------

/// <summary>Modifikatoren einer Tastenkombination; Werte wie bei <c>RegisterHotKey</c>.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>Eine Tastenkombination; <see cref="VirtualKey"/> ist der virtuelle Tastencode.</summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey);

/// <summary>Wofür eine Tastenkombination steht.</summary>
public enum HotkeyAction
{
    /// <summary>Aufnahme Ein/Aus (BE-03) bzw. Halten (BE-02).</summary>
    Record,

    /// <summary>Befehlstaste: die nächste Äusserung ist ein Befehl (BE-19).</summary>
    Command,

    /// <summary>Abbrechen (BE-06).</summary>
    Cancel,

    /// <summary>Diktatfenster öffnen und schliessen.</summary>
    DictationWindow,

    /// <summary>Markierten Text im Programm mit dem Fokus korrigieren lassen.</summary>
    Correct,

    /// <summary>Markierten Text im Programm mit dem Fokus glätten lassen (B9).</summary>
    Smooth,

    /// <summary>„Problem merken“: Marke ins Protokoll, ohne Text.</summary>
    Mark,
}

/// <summary>Auslösen oder Loslassen einer eigenen Kombination; <see cref="Timestamp"/> ist ein Stopwatch-Zeitstempel beim Empfang (ER-17).</summary>
public sealed record HotkeyEvent(HotkeyAction Action, long Timestamp);

/// <summary>Warum eine Kombination nicht angemeldet wurde (BF-02, SI-05); Prüfreihenfolge in Kapitel 5.1.</summary>
public enum HotkeyConflict
{
    None,

    /// <summary>Keine gültige Taste, z. B. nur ein Modifikator oder eine Maustaste.</summary>
    Invalid,

    /// <summary>Schreibtaste ohne Modifikator ausser Umschalt; würde das Schreiben überall blockieren.</summary>
    TypingKey,

    /// <summary>F12, laut Windows dem Debugger vorbehalten.</summary>
    Reserved,

    /// <summary>Schon für eine andere eigene Aktion angemeldet.</summary>
    OwnDuplicate,

    /// <summary>Strg+Alt+Taste erzeugt im aktiven Layout ein Zeichen (AltGr), z. B. „@“ in de-CH.</summary>
    AltGrCharacter,

    /// <summary><c>RegisterHotKey</c> scheitert.</summary>
    TakenBySystemOrOtherApp,
}

/// <summary>
/// Eigene Tastenkombinationen, ohne globalen Hook und ohne Mitlesen (SI-05). <see cref="Released"/> stammt aus der
/// Abfrage genau der Haupttaste nach dem Auslösen (BE-02).
/// </summary>
public interface IHotkeyService : IDisposable
{
    event EventHandler<HotkeyEvent>? Pressed;

    /// <summary>Nur für Aktionen, die mit <c>trackRelease</c> angemeldet sind (BE-02).</summary>
    event EventHandler<HotkeyEvent>? Released;

    HotkeyConflict Register(HotkeyAction action, HotkeyGesture gesture, bool trackRelease = false);

    void Unregister(HotkeyAction action);

    /// <summary>Sind die Tasten der zuletzt ausgelösten Kombination losgelassen (Kapitel 5.7)?</summary>
    bool OwnKeysReleased();
}

/// <summary>Windows: <c>RegisterHotKey</c> auf einem Nachrichtenfenster; Stufe A: Attrappe.</summary>
public interface IHotkeyApi
{
    bool Register(int id, uint modifiers, int virtualKey);

    void Unregister(int id);
}

/// <summary>Windows: <c>GetAsyncKeyState</c>; nur Tasten eigener Kombinationen (SI-05).</summary>
public interface IKeyStateProbe
{
    bool IsDown(int virtualKey);
}

/// <summary>Windows: aktives Layout über <c>ToUnicodeEx</c> mit eigenem Tastenzustand; Stufe A: Attrappe.</summary>
public interface IKeyboardLayoutInfo
{
    /// <summary>Zeichen von Strg+Alt(+Umschalt)+Taste, sonst <c>null</c>.</summary>
    string? AltGrCharacter(int virtualKey, bool shift);
}

/// <param name="PollInterval">Abstand der Abfragen beim Halten, Startwert 15 ms.</param>
public sealed record HoldOptions(TimeSpan PollInterval);

public enum RecordCommand
{
    Start,
    Stop,
}

// ---- Zielbindung (Kapitel 4.2, 5.4, 5.5) -------------------------------------------------------------------------

/// <summary>Feldarten der Zieltabelle (Grundentscheid 4).</summary>
public static class FieldKinds
{
    public const string DocumentBody = "Dokumenttext";
    public const string MessageBody = "Nachrichtentext";
    public const string Subject = "Betreff";
    public const string ChatInput = "Chat-Eingabe";
    public const string SingleLine = "Textfeld einzeilig";
    public const string MultiLine = "Textfeld mehrzeilig";
    public const string Unknown = "unbekannt";
}

/// <summary>Das Ziel einer Ausgabe: Programm und Eingabefeld (AU-10).</summary>
/// <param name="ProcessName">Name des Zielprogramms, z. B. „outlook“.</param>
/// <param name="FieldKind">Feldart nach <see cref="FieldKinds"/>.</param>
/// <param name="Display">Anzeige nur aus Tabelle bzw. Prozessname, nie aus Fenstertitel, Name oder Wert (SI-12).</param>
/// <param name="IsPassword">Erkanntes Passwortfeld; sperrt Aufnahme und Ausgabe (SI-04).</param>
public sealed record TargetInfo(string ProcessName, string FieldKind, string Display, bool IsPassword)
{
    /// <summary>Jede Bindung erhält eine eigene Kennung, auch beim erneuten Binden desselben Felds.</summary>
    public Guid BindingId { get; init; }
}

/// <summary>
/// Eigenschaften des fokussierten Elements; nie Name, Wert, Text oder Fenstertitel (SI-12). <c>null</c> heisst: die
/// Eigenschaft liess sich nicht lesen; das gilt nie als sicherer Wert (Kapitel 5.4, 5.5).
/// </summary>
public sealed record TargetFacts(
    string ProcessName,
    string ControlType,
    string ClassName,
    string FrameworkId,
    bool? IsPassword,
    bool? IsReadOnly,
    bool? IsEnabled,
    bool? IsMultiLine,
    bool? IsElevated,
    bool IsOwnProcess);

/// <param name="AutomationId">UIA-Eigenschaft AutomationId (Kennung des Entwicklers, kein Inhalt); leer, wenn nicht gesetzt.</param>
public sealed record ElementIdentity(
    int ProcessId,
    long ProcessStartTicks,
    long TopLevelWindow,
    string RuntimeId,
    string ControlType,
    string ClassName,
    string AutomationId);

/// <summary>Windows: UI Automation über COM (UIA3) hinter einer protokollierbaren Hülle; Stufe A: Attrappe.</summary>
public interface ITargetInspector
{
    /// <summary><c>null</c> ohne Fokus.</summary>
    (ElementIdentity Identity, TargetFacts Facts)? Focused();

    bool Exists(ElementIdentity identity);

    /// <summary>Elemente dieses Fensters mit dieser Kennung.</summary>
    int CountByAutomationId(long topLevelWindow, string automationId);

    bool OnSecureDesktop();

    /// <summary>UIA-Fokusereignis; abonniert nur, solange eine Bindung besteht.</summary>
    event EventHandler? FocusChanged;
}

public enum LineBreakMode
{
    Refuse,
    UnicodeReturn,
    Enter,
    ShiftEnter,
}

/// <summary>
/// <see cref="RuntimeId"/>: gleiche Prozesskennung, gleicher Prozessstart, gleiche RuntimeId. <see cref="AutomationId"/>:
/// Ersatz nur für Ziele, deren RuntimeId sich laut WM03 beim Schreiben ändert und deren AutomationId laut WM03 gesetzt,
/// im Fenster eindeutig und stabil ist (Kapitel 5.4).
/// </summary>
public enum IdentityMode
{
    RuntimeId,
    AutomationId,
}

/// <summary>Eine Zeile der Zieltabelle; leere Musterfelder (<c>null</c>) passen auf jeden Wert.</summary>
public sealed record TargetRule(
    string ProcessName,
    string? ControlType,
    string? ClassName,
    string? FrameworkId,
    bool? IsMultiLine,
    string FieldKind,
    string Display,
    LineBreakMode LineBreak,
    int MaxPartLength,
    IdentityMode Identity);

public enum BindRefusal
{
    NoFocus,
    OwnProcess,
    Elevated,
    PasswordField,
    ReadOnly,
    UnknownFieldStrict,
    SecureDesktop,
    IdentityUnclear,

    /// <summary>Übernehmen aus dem Diktatfenster: Der Fokus liegt nicht im gemerkten Feld (AU-10).</summary>
    NotMarkedField,
}

public abstract record TargetDecision;

public sealed record Bindable(TargetRule Rule, bool Known) : TargetDecision;

public sealed record NotBindable(BindRefusal Reason) : TargetDecision;

public sealed record BindResult(TargetInfo? Target, BindRefusal? Refusal);

/// <summary>Ergebnis der Prüfung vor jedem Teil der Ausgabe (Kapitel 5.4).</summary>
public enum TargetCheck
{
    Focused,
    FocusElsewhere,
    PasswordField,
    TargetGone,
    NotBound,
    SecureDesktop,
    IdentityUnclear,

    /// <summary>Sicherheitsmerkmale vor jedem Teil neu gelesen; nicht lesbar zählt wie unsicher.</summary>
    Elevated,

    ReadOnly,
}

/// <summary>Bindung an genau ein Feld (AU-10). Liest keine fremden Inhalte (SI-12).</summary>
public interface ITargetBinding
{
    TargetInfo? Bound { get; }

    TargetRule? BoundRule { get; }

    /// <summary>Wartet auf die Sperre (siehe <see cref="CheckAndSend"/>).</summary>
    BindResult BindFocused(ProfileStrictness strictness);

    /// <summary><see cref="TargetCheck.NotBound"/>, wenn keine Bindung besteht oder ihre Kennung nicht <paramref name="bindingId"/> ist.</summary>
    TargetCheck CheckBound(Guid bindingId);

    /// <summary>
    /// Prüft wie <see cref="CheckBound"/> unter der Sperre der Bindung und ruft <paramref name="send"/> nur bei
    /// <see cref="TargetCheck.Focused"/> auf, noch unter der Sperre. <see cref="BindFocused"/> und <see cref="Release"/>
    /// warten auf diese Sperre; so wechselt die Bindung nie zwischen Prüfung und Senden.
    /// </summary>
    TargetCheck CheckAndSend(Guid bindingId, Action send);

    /// <summary>Wartet auf die Sperre.</summary>
    void Release();
}

/// <summary>
/// Ein gemerktes Feld für „Übernehmen“ aus dem Diktatfenster: Identität und
/// Zeile der Zieltabelle, gelesen wie beim Binden, aber ohne Bindung und ohne Abonnement von Fokusereignissen.
/// </summary>
/// <param name="Display">Anzeige aus der Tabelle, nie aus Fenstertitel, Name oder Wert (SI-12).</param>
public sealed record TargetMark(string ProcessName, string FieldKind, string Display, ElementIdentity Identity, TargetRule Rule);

public sealed record MarkResult(TargetMark? Mark, BindRefusal? Refusal);

/// <summary>Merken und späteres Binden genau dieses Felds (AU-10); <see cref="TargetBinding"/> setzt es um.</summary>
public interface ITargetMarker
{
    /// <summary>Merkt das fokussierte Feld nach denselben Regeln wie <see cref="ITargetBinding.BindFocused"/>; löst keine Bindung.</summary>
    MarkResult MarkFocused(ProfileStrictness strictness);

    /// <summary>
    /// Bindet nur, wenn das fokussierte Feld genau das gemerkte ist und nach den Regeln noch bindbar; sonst mit Grund,
    /// <see cref="BindRefusal.NotMarkedField"/> für ein anderes Feld. Eine abgewiesene Bindung löst auch die bisherige.
    /// </summary>
    BindResult BindMarked(TargetMark mark, ProfileStrictness strictness);

    /// <summary>Hat genau dieses gemerkte Feld jetzt den Fokus? Nach der Feldidentität der Regel; bindet nichts (Beta B9).</summary>
    bool IsFocused(TargetMark mark);
}

/// <summary>
/// Zählt, wie oft der Fokus das gebundene Feld verlassen hat, seit es gebunden ist; die Zahl sinkt nie, auch wenn der Fokus
/// zurückkehrt (Codex zu PR #55). So erkennt eine Ausgabe mit Bedingung jede Unterbrechung zwischen zwei Teilen.
/// </summary>
public interface IFocusDepartures
{
    /// <summary>Zahl der Wechsel weg vom Feld dieser Bindung; null, wenn <paramref name="bindingId"/> nicht gebunden ist.</summary>
    int? Departures(Guid bindingId);
}

// ---- Einfügen (Kapitel 4.3, 5.6–5.8) -----------------------------------------------------------------------------

/// <summary>Sicherung für echte Tasten (Kapitel 5.8, Entscheid in Kapitel 10.1).</summary>
public enum ControlKeySafety
{
    NotApproved,
    Neutralize,
    Check,
}

public abstract record InputStep;

/// <summary>Je UTF-16-Einheit <c>KEYEVENTF_UNICODE</c> ab und auf.</summary>
public sealed record TypeText(string Text) : InputStep;

/// <summary>Enter bzw. Umschalt+Enter als echte Taste.</summary>
public sealed record PressKey(int VirtualKey, bool Shift, ControlKeySafety Safety) : InputStep;

/// <param name="Text">Ausgangstext des Teils mit LF für Umbrüche; ein LF zählt als eine Einheit.</param>
public sealed record InjectionPart(IReadOnlyList<InputStep> Steps, string Text);

public abstract record InjectionPlan;

public sealed record PlannedParts(IReadOnlyList<InjectionPart> Parts) : InjectionPlan;

public sealed record PlanRefused(OutputReason Reason) : InjectionPlan;

public enum InputEventKind
{
    Unicode,
    VirtualKey,
}

/// <param name="Code">UTF-16-Einheit bei <see cref="InputEventKind.Unicode"/>, virtueller Tastencode bei <see cref="InputEventKind.VirtualKey"/>.</param>
/// <param name="Unit">
/// Index der Texteinheit in <see cref="InjectionPart.Text"/>, zu der das Ereignis gehört; <c>null</c> für die
/// vorbereitenden Auf-Ereignisse von <see cref="ControlKeySafety.Neutralize"/>, die keine Taste drücken.
/// </param>
public sealed record InputEvent(InputEventKind Kind, int Code, bool Down, int? Unit);

/// <summary>Windows: <c>SendInput</c>; Stufe A: Attrappe mit steuerbarem Rückgabewert.</summary>
public interface ISendInputApi
{
    /// <returns>Zahl der von vorn in den Eingabestrom eingefügten Ereignisse, wie <c>SendInput</c> sie meldet.</returns>
    int Send(IReadOnlyList<InputEvent> events);
}

/// <param name="Units">Einheiten von <see cref="InjectionPart.Text"/> von vorn, die eingefügt wurden (erfolgreiche Injektion).</param>
/// <param name="KeysMaybeDown">Tasten, deren Freigabe nicht eingefügt werden konnte; leer im Normalfall.</param>
public sealed record InjectionResult(int Units, IReadOnlyList<int> KeysMaybeDown);

/// <summary>Sendet einen geplanten Teil nach seinem Ereignisplan (AU-01, AU-13).</summary>
public interface ITextInjector
{
    InjectionResult Inject(InjectionPart part);
}

// ---- Wächter und fremde Inhalte (Kapitel 4.4, 5.5, 5.9) ----------------------------------------------------------

public enum GuardReason
{
    SessionLocked,
    SessionDisconnected,
    SecureDesktop,
    PasswordField,
}

/// <summary>Beendet die Aufnahme bei Sperre, Benutzerwechsel, geschütztem Desktop und Passwortfeld (SI-04).</summary>
public interface IRecordingGuard : IDisposable
{
    event EventHandler<GuardReason>? StopRequired;

    /// <summary>Bei Aufnahmebeginn.</summary>
    void Arm();

    /// <summary>Bei Aufnahmeende.</summary>
    void Disarm();
}

/// <summary>Windows: WTS-Sitzungsmeldungen, Eingabedesktop, UIA-Fokusereignis; Stufe A: Attrappe.</summary>
public interface IGuardSignals
{
    /// <summary><see cref="GuardReason.SessionLocked"/> oder <see cref="GuardReason.SessionDisconnected"/>.</summary>
    event EventHandler<GuardReason>? SessionEvent;

    /// <summary><c>true</c> oder <c>null</c> (nicht lesbar): Der neue Fokus gilt als Passwortfeld.</summary>
    event EventHandler<bool?>? FocusChanged;

    /// <summary><c>null</c>, wenn <c>IsPassword</c> nicht lesbar ist.</summary>
    bool? FocusIsPassword();

    /// <summary><c>null</c>, wenn der Eingabedesktop nicht lesbar ist.</summary>
    string? InputDesktopName();

    /// <summary>Abonnements nur zwischen <see cref="Start"/> und <see cref="Stop"/>.</summary>
    void Start();

    void Stop();
}

/// <param name="DesktopPoll">Abstand der Abfrage des Eingabedesktops, Startwert 250 ms.</param>
public sealed record GuardOptions(TimeSpan DesktopPoll);

/// <summary>Zweck eines Lesens fremder Inhalte: AU-02, AU-10 bzw. BA-08, BA-13, BA-15 (SI-12).</summary>
public enum ReadPurpose
{
    TargetCheck,
    ExplicitCommand,
}

/// <summary>UIA3 (Kapitel 5.9). Gelesen wird nur auf ausdrücklichen Befehl oder zur Zielprüfung (SI-12).</summary>
public interface IForeignContentReader
{
    ForeignText? ReadFocused(ReadPurpose purpose);

    /// <summary>Die Markierung im Feld mit dem Fokus (Beta B9), geprüft nach <see cref="SelectionText.From"/>.</summary>
    SelectionRead ReadSelection(ReadPurpose purpose);
}

/// <summary>Warum keine Markierung gelesen wurde (B9).</summary>
public enum SelectionRefusal
{
    None,

    /// <summary>Nichts markiert oder nur Leerraum.</summary>
    NoSelection,

    /// <summary>Das Programm gibt die Markierung nicht preis (kein UIA-Textmuster, Fehler, Zeitgrenze).</summary>
    Unreadable,

    /// <summary>Länger als <see cref="SelectionText.MaxLength"/> Zeichen.</summary>
    TooLong,

    /// <summary>Tabelle, Feld, Bild, mehrere Stellen oder Steuerzeichen: Zurückschreiben würde die Struktur zerstören.</summary>
    SpecialContent,

    /// <summary>Passwortfeld: wird nie gelesen.</summary>
    PasswordField,
}

/// <summary>Ergebnis des Lesens einer Markierung: Text nur bei <see cref="SelectionRefusal.None"/>.</summary>
public sealed record SelectionRead(ForeignText? Text, SelectionRefusal Refusal)
{
    public static SelectionRead Refused(SelectionRefusal refusal) => new(null, refusal);
}

// ---- Audio (P8) --------------------------------------------------------------------------------------------------

/// <summary>Ein Audioeingang, z. B. Headset oder Notebook-Mikrofon (ER-12).</summary>
public sealed record AudioDevice(string Id, string Name);

/// <summary>Phase einer Aufnahmesitzung (P8-Konzept, Kapitel 4.1).</summary>
public enum CapturePhase
{
    Opening,
    Recording,
    Stopped,
}

/// <summary>Warum eine Aufnahme endete (P8-Konzept, Kapitel 4.1 und 5.2).</summary>
public enum CaptureStopReason
{
    /// <summary>Regulärer Stopp.</summary>
    Requested,

    /// <summary>Gewähltes Gerät beim Öffnen nicht vorhanden (ER-13).</summary>
    DeviceMissing,

    /// <summary>Gerät während der Aufnahme verschwunden, z. B. Headset abgezogen (ER-13).</summary>
    DeviceRemoved,

    /// <summary>Keine Audiodaten mehr vom Gerät (ER-13).</summary>
    NoSignal,

    /// <summary>Mikrofonzugriff gesperrt, z. B. Datenschutzeinstellung (ER-13).</summary>
    AccessDenied,

    /// <summary>Gerät von einem anderen Programm exklusiv belegt (ER-13).</summary>
    DeviceInUse,

    /// <summary>Abnehmer liest nicht mehr, z. B. Puffer von P3 voll (ER-05).</summary>
    QueueFull,

    /// <summary>Aussetzer des Datenstroms mit unbekannter Dauer (Kapitel 5.2).</summary>
    Discontinuity,

    /// <summary>Anderer Fehler der Aufnahme.</summary>
    Failure,
}

/// <summary>Zustand einer Aufnahmesitzung.</summary>
/// <param name="Phase">Aktuelle Phase.</param>
/// <param name="StopReason">Grund des Stopps, nur in <see cref="CapturePhase.Stopped"/>.</param>
/// <param name="StartLatency">Zeit von <c>Open</c> bis zu den ersten Audiodaten; nur ab Recording.</param>
/// <param name="SilentSignal">Seit <c>SilentSignalHint</c> nur exakte Nullen; Hinweis, kein Stopp.</param>
/// <param name="EndpointMuted">
/// Das Gerät ist in Windows stumm geschaltet (Mikrofontaste, Lautstärkemenü, manche Headsets); Hinweis, kein Stopp.
/// </param>
/// <param name="Unread">Aufgenommenes Audio, das beim Schliessen nicht gelesen war.</param>
/// <param name="SinceLastData">
/// Nur in <see cref="CapturePhase.Stopped"/>: Zeit vom Eintreffen des letzten Audioblocks des Geräts bis zum Stopp der
/// Sitzung; null, wenn nie ein Block ankam. Annäherung für die Stoppdauer (RM11), keine Messung ab dem Ereignis am Gerät.
/// </param>
public sealed record CaptureStatus(
    CapturePhase Phase, CaptureStopReason? StopReason, TimeSpan? StartLatency, bool SilentSignal, TimeSpan Unread,
    TimeSpan? SinceLastData = null, bool EndpointMuted = false);

/// <summary>Audioaufnahme. Das Mikrofon ist nur während der Aufnahme offen (ER-17); kein stiller Gerätewechsel (ER-13).</summary>
public interface IAudioCapture
{
    /// <summary>Aktive Aufnahmegeräte; <see cref="AudioDevice.Id"/> ist die Kennung des Endpunkts. Öffnet kein Mikrofon.</summary>
    IReadOnlyList<AudioDevice> Devices { get; }

    /// <summary>Standardgerät von Windows für die Rolle „Konsole“. Öffnet kein Mikrofon.</summary>
    AudioDevice? DefaultDevice { get; }

    /// <summary>Geräte sind hinzugekommen oder verschwunden.</summary>
    event EventHandler? DevicesChanged;

    /// <summary>Öffnet das Mikrofon sofort (ER-17); höchstens eine nicht freigegebene Sitzung.</summary>
    /// <exception cref="InvalidOperationException">Eine frühere Sitzung ist noch nicht freigegeben.</exception>
    ICaptureSession Open(AudioDevice device);
}

/// <summary>Eine Aufnahme; <see cref="IAsyncDisposable.DisposeAsync"/> schliesst das Mikrofon.</summary>
public interface ICaptureSession : IAsyncDisposable
{
    AudioDevice Device { get; }

    CaptureStatus Status { get; }

    event EventHandler<CaptureStatus>? StatusChanged;

    /// <summary>
    /// Einmal aufrufbar. Endet bei jedem Stopp regulär, nachdem alles Aufgenommene geliefert ist; der Token bricht nur
    /// das Lesen ab.
    /// </summary>
    IAsyncEnumerable<AudioChunk> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Regulärer Stopp: Mikrofon zu, schon Aufgenommenes bleibt lesbar, dann endet ReadAsync ohne Fehler.</summary>
    void Stop();
}
