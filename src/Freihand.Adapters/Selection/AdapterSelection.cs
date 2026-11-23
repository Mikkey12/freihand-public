using Freihand.Core.Recognition;
using Freihand.Core.Routing;
using Freihand.Core.Transform;

namespace Freihand.Adapters.Selection;

/// <summary>Ein angemeldeter Erkennungsadapter samt tatsächlichem Endpunkt für die Prüfung (WE-03).</summary>
/// <param name="Id">Eindeutige Kennung, z. B. „whisper-cpp-geraet“.</param>
/// <param name="Endpoint">Tatsächlicher Endpunkt, z. B. <see cref="RouteEndpoint.ThisDevice"/> oder ein Anbieter mit Host.</param>
public sealed record RecognitionAdapter(string Id, RouteEndpoint Endpoint, IRecognitionEngine Engine)
{
    public RecognitionRoute Route => Engine.Route;
}

/// <summary>Ein angemeldeter Umformadapter samt tatsächlichem Endpunkt für die Prüfung (WE-03).</summary>
public sealed record TransformAdapter(string Id, RouteEndpoint Endpoint, ITextTransformer Transformer)
{
    public TransformRoute Route => Transformer.Route;
}

/// <summary>
/// Was für eine Verarbeitung gebraucht wird. Ausweichwege gelten nur bei Ausfall und nur, wenn die
/// Richtlinie sie für die Datenklasse erlaubt (WE-10).
/// </summary>
/// <param name="DataClass">Datenklasse des Profils (WE-03).</param>
/// <param name="Recognition">Gewählter Erkennungsweg des Profils (WE-01).</param>
/// <param name="Transform">Gewählter Umformweg des Profils (WE-01).</param>
/// <param name="Requirements">Verlangte Fähigkeiten der Erkennung (WE-02).</param>
/// <param name="RecognitionFallbacks">Ausweichwege der Erkennung in dieser Reihenfolge.</param>
/// <param name="TransformFallbacks">Ausweichwege des Umformens in dieser Reihenfolge.</param>
/// <param name="Unavailable">Kennungen der Adapter, die gerade ausgefallen sind.</param>
public sealed record SelectionRequest(
    DataClass DataClass,
    RecognitionRoute Recognition,
    TransformRoute Transform,
    RecognitionRequirements? Requirements = null,
    IReadOnlyList<RecognitionRoute>? RecognitionFallbacks = null,
    IReadOnlyList<TransformRoute>? TransformFallbacks = null,
    IReadOnlySet<string>? Unavailable = null);

/// <summary>Warum eine Wegkombination nicht genommen wurde.</summary>
public enum CandidateIssue
{
    /// <summary>Der Weg fehlt in dieser Ausgabe (WE-11).</summary>
    RouteNotInEdition,

    /// <summary>Kein angemeldeter Adapter für den Weg oder keiner mit den verlangten Fähigkeiten (WE-02).</summary>
    NoMatchingAdapter,

    /// <summary>Die Richtlinie verweigert die Kombination (WE-03), z. B. fehlende Freigabe der IT (WE-15).</summary>
    PolicyDenied,

    /// <summary>Der Adapter ist ausgefallen (WE-10).</summary>
    Unavailable,
}

/// <summary>
/// Eine übergangene Wegkombination; nur Metadaten, für Anzeige und Protokoll. <see cref="Denial"/> nennt bei
/// <see cref="CandidateIssue.PolicyDenied"/> die Regel der Richtlinie, sonst <see cref="DenialReason.None"/>.
/// </summary>
public sealed record SkippedCandidate(
    RecognitionRoute Recognition,
    TransformRoute Transform,
    string? AdapterId,
    CandidateIssue Issue,
    DenialReason Denial,
    string Detail);

/// <summary>
/// Ergebnis der Auswahl. Bei Erfolg zeigt es, welcher Weg läuft und ob es ein Ausweichweg ist (WE-10),
/// und nennt die geprüfte <see cref="Route"/>; sie wird unmittelbar vor dem Senden nochmals mit
/// <see cref="RoutePolicyExtensions.EnsureAllowed"/> geprüft, weil sich z. B. der Notschalter inzwischen
/// geändert haben kann (SI-07). Sonst steht hier, warum verweigert wurde.
/// </summary>
public sealed record AdapterSelection(
    RecognitionAdapter? Recognition,
    TransformAdapter? Transform,
    RouteRequest? Route,
    bool IsFallback,
    CandidateIssue? RefusalIssue,
    DenialReason RefusalDenial,
    string RefusalReason,
    IReadOnlyList<SkippedCandidate> Skipped)
{
    public bool IsSelected => Recognition is not null;

    /// <summary>Laufender Erkennungsweg, für die Anzeige (WE-10).</summary>
    public RecognitionRoute? ActiveRecognition => Recognition?.Route;

    /// <summary>Laufender Umformweg; <see cref="TransformRoute.None"/> ohne Umformen.</summary>
    public TransformRoute? ActiveTransform => IsSelected ? Transform?.Route ?? TransformRoute.None : null;
}
