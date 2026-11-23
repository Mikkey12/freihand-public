namespace Freihand.Core.Routing;

/// <summary>
/// Eine geplante Verarbeitung: Datenklasse, beide Wege und je Schritt der tatsächliche Endpunkt (WE-03).
/// Ohne Umformen ist der Endpunkt des Umformschritts <see cref="RouteEndpoint.ThisDevice"/>.
/// </summary>
public sealed record RouteRequest(
    DataClass DataClass,
    RecognitionRoute Recognition,
    RouteEndpoint RecognitionEndpoint,
    TransformRoute Transform,
    RouteEndpoint TransformEndpoint)
{
    /// <summary>Verlässt irgendein Teil dieser Verarbeitung das Gerät, auch über Heim (SI-07)?</summary>
    public bool LeavesDevice =>
        Recognition.LeavesDevice()
        || Transform.LeavesDevice()
        || RecognitionEndpoint.LeavesDevice
        || TransformEndpoint.LeavesDevice;
}

/// <summary>Welche Regel eine Route verweigert hat; für Tests, Anzeige und Protokoll (nur Metadaten, SI-01).</summary>
public enum DenialReason
{
    None,

    /// <summary>Der Notschalter „nur lokal“ ist an (SI-07).</summary>
    LocalOnly,

    /// <summary>Der Endpunkt passt nicht zum gewählten Weg (WE-03).</summary>
    EndpointMismatch,

    /// <summary>Den Weg gibt es in dieser Ausgabe nicht (Kapitel 5, GE-02).</summary>
    NotInEdition,

    /// <summary>Der Weg braucht eine Freigabe der IT, die fehlt (WE-08, WE-15).</summary>
    ApprovalMissing,

    /// <summary>Die IT sperrt den Weg (GE-04).</summary>
    BlockedByIt,

    /// <summary>Für die Datenklasse gibt es keine Regel; im Zweifel wird verweigert (WE-03).</summary>
    UnknownDataClass,

    /// <summary>Die Datenklasse erlaubt den Weg oder den Endpunkt nicht (WE-03).</summary>
    DataClass,

    /// <summary>Der Endpunkt ist nicht freigegeben (GE-04, OF-01).</summary>
    EndpointNotApproved,
}

/// <summary>Ergebnis der Prüfung. Eine unzulässige Route wird verweigert, nicht umgeleitet (WE-03).</summary>
public sealed record PolicyDecision(bool Allowed, DenialReason Denial, string Reason)
{
    public static PolicyDecision Allow() => new(true, DenialReason.None, string.Empty);

    public static PolicyDecision Deny(DenialReason denial, string reason) => new(false, denial, reason);
}

/// <summary>Prüft jede Verarbeitung vor der ersten Anfrage (WE-03, SI-07). Umsetzung: <see cref="RoutePolicy"/>.</summary>
public interface IRoutePolicy
{
    PolicyDecision Evaluate(RouteRequest request);
}

/// <summary>Eine Route wurde verweigert; es darf keine Anfrage entstehen (Abnahme WE-03).</summary>
public sealed class RouteDeniedException(PolicyDecision decision) : InvalidOperationException(decision.Reason)
{
    public PolicyDecision Decision { get; } = decision;
}

public static class RoutePolicyExtensions
{
    /// <summary>
    /// Prüft die Route vor der ersten Anfrage und wirft bei Verweigerung. Aufrufer rufen das unmittelbar vor dem
    /// Senden auf, damit eine verbotene Kombination keine Anfrage erzeugt (WE-03, SI-07).
    /// </summary>
    public static void EnsureAllowed(this IRoutePolicy policy, RouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var decision = policy.Evaluate(request);
        if (!decision.Allowed)
        {
            throw new RouteDeniedException(decision);
        }
    }
}
