using Freihand.Core.Routing;

namespace Freihand.Core.Profiles;

/// <summary>
/// Das Datenformat für Profile, Version 2 (OF-01). Das JSON-Schema dazu liegt unter
/// <c>schemas/profiles.v2.schema.json</c>; die Prüfung in <see cref="ProfileSerializer"/> ist strenger
/// als das Schema (zugelassene Endpunkte, Geheimnisse, ausführbare Inhalte, Eindeutigkeit). Dokumente der
/// Version 1 (<c>schemas/profiles.v1.schema.json</c>) bleiben lesbar und werden beim Lesen auf Version 2
/// gehoben; ein Export schreibt immer Version 2.
/// </summary>
public static class ProfileSchema
{
    public const string Kind = "freihand.profiles";

    /// <summary>Die Version, die ein Export schreibt.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Die älteste Version, die ein Import liest; ältere Dokumente werden beim Lesen migriert.</summary>
    public const int OldestReadableVersion = 1;

    public const string SchemaId = "urn:freihand:schema:profiles:2";

    public const string SchemaIdVersion1 = "urn:freihand:schema:profiles:1";

    public const int MaxVocabularyEntries = 500;

    public const int MaxVocabularyEntryLength = 100;

    public const int MaxPrograms = 32;

    public const int MaxProgramLength = 64;

    private const string ProgramPatternBody = "^[a-z0-9][a-z0-9 ._+-]*\\.exe";

    /// <summary>
    /// Programmname: Dateiname ohne Pfad, in Kleinbuchstaben, mit der Endung <c>.exe</c> (BA-02). Die Fassung
    /// im JSON-Schema (<c>$</c> nach ECMA 262); der Code prüft mit <see cref="ProgramPatternStrict"/>.
    /// </summary>
    public const string ProgramPattern = ProgramPatternBody + "$";

    /// <summary>Wie <see cref="ProgramPattern"/>, aber mit <c>\z</c> als Endanker (siehe <c>DataSafety.IdPatternStrict</c>).</summary>
    internal const string ProgramPatternStrict = ProgramPatternBody + @"\z";

    public const int MaxProfiles = 64;

    public const int MaxNameLength = 64;

    public const int MaxDescriptionLength = 500;

    public const int MaxDeviceIdLength = 256;

    private const string LanguagePatternBody = "^[a-z]{2,3}(-[A-Z][a-z]{3})?(-([A-Z]{2}|[0-9]{3}))?";

    /// <summary>
    /// Vereinfachte BCP-47-Kennung: Sprache, optional Schrift und Region, z. B. <c>de-CH</c>, <c>gsw-CH</c>. Die
    /// Fassung im JSON-Schema (<c>$</c> nach ECMA 262); der Code prüft mit <see cref="LanguagePatternStrict"/>.
    /// </summary>
    public const string LanguagePattern = LanguagePatternBody + "$";

    /// <summary>Wie <see cref="LanguagePattern"/>, aber mit <c>\z</c> als Endanker (siehe <c>DataSafety.IdPatternStrict</c>).</summary>
    internal const string LanguagePatternStrict = LanguagePatternBody + @"\z";

    public static WireNameMap<RecognitionRoute> RecognitionRoutes { get; } = new(
    [
        new(RecognitionRoute.Online, "online"),
        new(RecognitionRoute.Home, "home"),
        new(RecognitionRoute.Device, "device"),
        new(RecognitionRoute.CompanyService, "companyService"),
    ]);

    public static WireNameMap<TransformRoute> TransformRoutes { get; } = new(
    [
        new(TransformRoute.None, "none"),
        new(TransformRoute.Local, "local"),
        new(TransformRoute.Home, "home"),
        new(TransformRoute.PrivateProvider, "privateProvider"),
        new(TransformRoute.CompanyService, "companyService"),
    ]);

    public static WireNameMap<RecognitionMode> RecognitionModes { get; } = new(
    [
        new(RecognitionMode.Live, "live"),
        new(RecognitionMode.Sentence, "sentence"),
    ]);

    public static WireNameMap<TransformStyle> TransformStyles { get; } = new(
    [
        new(TransformStyle.Mail, "mail"),
        new(TransformStyle.Polite, "polite"),
        new(TransformStyle.Summary, "summary"),
    ]);

    public static WireNameMap<RecordingMode> RecordingModes { get; } = new(
    [
        new(RecordingMode.Toggle, "toggle"),
        new(RecordingMode.Hold, "hold"),
    ]);

    public static WireNameMap<OutputMode> OutputModes { get; } = new(
    [
        new(OutputMode.DictationWindow, "dictationWindow"),
        new(OutputMode.BoundTarget, "boundTarget"),
        new(OutputMode.Clipboard, "clipboard"),
    ]);

    public static WireNameMap<ProfileStrictness> Strictness { get; } = new(
    [
        new(ProfileStrictness.Standard, "standard"),
        new(ProfileStrictness.Strict, "strict"),
    ]);

    public static WireNameMap<AudioSourceKind> AudioSourceKinds { get; } = new(
    [
        new(AudioSourceKind.SystemDefault, "systemDefault"),
        new(AudioSourceKind.Device, "device"),
    ]);

    /// <summary>
    /// Datenklassen, die ein Import kennt: die drei privaten Standardklassen (WE-03). Klassen der IT
    /// (GE-04) kommen mit der Geschäftsausgabe dazu.
    /// </summary>
    public static IReadOnlyList<DataClass> DataClasses { get; } =
        [DataClass.Public, DataClass.Internal, DataClass.Confidential];
}
