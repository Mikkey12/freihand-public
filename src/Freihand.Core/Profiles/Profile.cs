using Freihand.Core.Recognition;
using Freihand.Core.Routing;

namespace Freihand.Core.Profiles;

/// <summary>
/// Ein Profil als Daten (OF-01, BA-01): legt fest, was mit dem Gesprochenen geschieht. Profile sind
/// unveränderlich; ein geändertes Profil ist ein neues Objekt. Eingelesen und ausgegeben wird nur über
/// <see cref="ProfileSerializer"/>, damit jede Prüfung greift.
/// </summary>
/// <param name="Id">Fester Bezeichner, z. B. <c>standard</c>.</param>
/// <param name="Name">Anzeigename, z. B. „Standard“.</param>
/// <param name="Description">Kurze Beschreibung für die Einblendung (BE-16), optional.</param>
/// <param name="Recognition">Erkennungsweg samt Endpunkt (WE-01).</param>
/// <param name="Transform">Umformweg samt Endpunkt (WE-01).</param>
/// <param name="DataClass">Datenklasse des Profils (WE-03).</param>
/// <param name="Language">Sprache als BCP-47-Kennung, z. B. <c>de-CH</c> (Grundentscheid 1).</param>
/// <param name="RecordingMode">Standard-Aufnahmeart (BE-05).</param>
/// <param name="AudioSource">Audioquelle (ER-12).</param>
/// <param name="Output">Wohin der feste Text geht (AU-04, AU-05).</param>
/// <param name="FormatRules">Verweis auf einen Satz Formatregeln, z. B. Schweizer Schreibweise (BA-17).</param>
/// <param name="Strictness">Strenge des Profils (SI-04, AU-03).</param>
/// <remarks>
/// Dazu kommen die Angaben ab Version 2 des Datenformats als Eigenschaften mit Startwert, damit bestehende
/// Aufrufe mit den zwölf Parametern gültig bleiben: <see cref="RecognitionMode"/>, <see cref="Vocabulary"/>
/// und <see cref="Programs"/>.
/// </remarks>
public sealed record Profile(
    string Id,
    string Name,
    string? Description,
    RecognitionStep Recognition,
    TransformStep Transform,
    DataClass DataClass,
    string Language,
    RecordingMode RecordingMode,
    AudioSourceSelection AudioSource,
    OutputMode Output,
    string FormatRules,
    ProfileStrictness Strictness)
{
    /// <summary>
    /// Gewünschter Erkennungsmodus (ER-20); Profile der Version 1 gelten als live. Wirksam wird er erst mit
    /// <see cref="RecognitionModeResolver.Resolve"/> (ER-19).
    /// </summary>
    public RecognitionMode RecognitionMode { get; init; } = RecognitionMode.Live;

    /// <summary>Persönliches Vokabular (BA-03), in der Reihenfolge der Eingabe; ausdrücklich angelegt und löschbar (SI-11).</summary>
    public TermList Vocabulary { get; init; } = TermList.Empty;

    /// <summary>Programme (Dateinamen in Kleinbuchstaben) für den automatischen Profilwechsel (BA-02).</summary>
    public TermList Programs { get; init; } = TermList.Empty;

    /// <summary>Exakt-Profil: ohne nachgeschaltete KI-Umformung (BA-12).</summary>
    public bool IsExact => Transform.Route == TransformRoute.None;

    /// <summary>Unbekannte Eingabefelder werden abgewiesen (SI-04).</summary>
    public bool RejectsUnknownFields => Strictness == ProfileStrictness.Strict;

    /// <summary>Sprache und Vokabular für die Erkennung (T43 im P3-Konzept); Hinweise in der Reihenfolge des Vokabulars.</summary>
    public RecognitionOptions ToRecognitionOptions() => new(Language, Vocabulary);
}

/// <summary>Erkennungsweg eines Profils. Ein Endpunkt ist nötig, sobald das Audio das Gerät verlässt (WE-03).</summary>
public sealed record RecognitionStep(RecognitionRoute Route, Uri? Endpoint = null);

/// <summary>
/// Umformweg eines Profils. Ein Endpunkt ist nötig, sobald der Text das Gerät verlässt (WE-03). Der Stil
/// (BA-10) ist nur bei einem Umformweg erlaubt; ohne Stil gilt die Standardanweisung des Umformpakets.
/// </summary>
public sealed record TransformStep(TransformRoute Route, Uri? Endpoint = null, TransformStyle? Style = null);

/// <summary>
/// ER-20: gewünschter Modus im Profil. Bewusst getrennt vom wirksamen <see cref="Freihand.Core.Live.LiveMode"/> (P3), damit
/// ein späterer Wert wie ein automatischer Modus (ER-21) keinen P3-Vertrag ändert.
/// </summary>
public enum RecognitionMode
{
    /// <summary>Vorläufiger Text während des Sprechens, wo es abgenommen ist (ER-19).</summary>
    Live,

    /// <summary>Satzweise, ohne vorläufigen Text; gleichwertig (ER-19).</summary>
    Sentence,
}

/// <summary>BA-10: Umformstil aus einer festen Liste; die Anweisung je Stil legt das Umformpaket fest.</summary>
public enum TransformStyle
{
    Mail,
    Polite,
    Summary,
}

/// <summary>Aufnahmeart (BE-02, BE-03).</summary>
public enum RecordingMode
{
    /// <summary>Ein/Aus, damit nie eine Taste gehalten werden muss; Standard (BE-03, BF-06).</summary>
    Toggle,

    /// <summary>Halten (BE-02).</summary>
    Hold,
}

/// <summary>Wohin fester Text geht (AU-05).</summary>
public enum OutputMode
{
    /// <summary>Ins eigene Diktatfenster, Übernahme ins Ziel am Schluss (AU-04).</summary>
    DictationWindow,

    /// <summary>Direkt ins gebundene Ziel, nur fester Text (BE-01, AU-10).</summary>
    BoundTarget,

    /// <summary>In die Zwischenablage; in strengen Profilen gesperrt (AU-03).</summary>
    Clipboard,
}

/// <summary>Strenge eines Profils.</summary>
public enum ProfileStrictness
{
    Standard,

    /// <summary>Unbekannte Eingabefelder werden abgewiesen (SI-04); keine Zwischenablage (AU-03).</summary>
    Strict,
}

/// <summary>Art der Audioquelle (ER-12).</summary>
public enum AudioSourceKind
{
    /// <summary>Das Standardgerät von Windows zum Zeitpunkt des Starts.</summary>
    SystemDefault,

    /// <summary>Ein bestimmtes Gerät; fehlt es, pausiert die Aufnahme statt still zu wechseln (ER-13).</summary>
    Device,
}

/// <summary>Audioquelle eines Profils (ER-12). Ein Gerät wird über seine Kennung gewählt, nicht über den Namen.</summary>
/// <param name="Kind">Standardgerät oder bestimmtes Gerät.</param>
/// <param name="DeviceId">Kennung des Geräts, nur bei <see cref="AudioSourceKind.Device"/>.</param>
/// <param name="DisplayName">Anzeige, z. B. „Headset“, nur bei <see cref="AudioSourceKind.Device"/>.</param>
public sealed record AudioSourceSelection(AudioSourceKind Kind, string? DeviceId = null, string? DisplayName = null)
{
    public static AudioSourceSelection SystemDefault { get; } = new(AudioSourceKind.SystemDefault);

    public static AudioSourceSelection Device(string deviceId, string displayName) =>
        new(AudioSourceKind.Device, deviceId, displayName);
}
