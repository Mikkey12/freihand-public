using System.Collections.Frozen;

namespace Freihand.Core.Routing;

/// <summary>
/// Welche Wege und Endpunkte eine Datenklasse nutzen darf (WE-03). Die Regel gilt für beide Schritte: Eine
/// lokale Erkennung macht einen nachgeschalteten Online-Schritt nicht zulässig (Kapitel 5).
/// </summary>
public sealed record DataClassRule
{
    public DataClassRule(
        IEnumerable<RecognitionRoute> recognition,
        IEnumerable<TransformRoute> transform,
        IEnumerable<EndpointKind> endpoints)
    {
        Recognition = recognition.ToFrozenSet();
        Transform = transform.ToFrozenSet();
        Endpoints = endpoints.ToFrozenSet();
    }

    public IReadOnlySet<RecognitionRoute> Recognition { get; }

    public IReadOnlySet<TransformRoute> Transform { get; }

    public IReadOnlySet<EndpointKind> Endpoints { get; }

    /// <summary>„öffentlich“: alle Wege, die die Ausgabe kennt.</summary>
    public static DataClassRule PublicDefault { get; } = new(
        Enum.GetValues<RecognitionRoute>(),
        Enum.GetValues<TransformRoute>(),
        Enum.GetValues<EndpointKind>());

    /// <summary>
    /// „intern“: nur eigene oder vertraglich gebundene Infrastruktur (Gerät, Heim, Firmen-Tenant), keine
    /// Online-Anbieter, weil dort kein Training nicht keine Speicherung heisst (SI-08).
    /// </summary>
    public static DataClassRule InternalDefault { get; } = new(
        [RecognitionRoute.Device, RecognitionRoute.Home, RecognitionRoute.CompanyService],
        [TransformRoute.None, TransformRoute.Local, TransformRoute.Home, TransformRoute.CompanyService],
        [EndpointKind.Device, EndpointKind.Home, EndpointKind.CompanyTenant]);

    /// <summary>„vertraulich“: nichts verlässt das Gerät; Erkennung auf dem Gerät, Umformen keines oder lokal.</summary>
    public static DataClassRule ConfidentialDefault { get; } = new(
        [RecognitionRoute.Device],
        [TransformRoute.None, TransformRoute.Local],
        [EndpointKind.Device]);

    /// <summary>Vorgeschlagene Standardregeln für die drei Klassen der privaten Ausgabe (WE-03).</summary>
    public static IReadOnlyDictionary<string, DataClassRule> Defaults { get; } =
        new Dictionary<string, DataClassRule>(StringComparer.Ordinal)
        {
            [DataClass.Public.Id] = PublicDefault,
            [DataClass.Internal.Id] = InternalDefault,
            [DataClass.Confidential.Id] = ConfidentialDefault,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public bool Permits(RecognitionRoute route) => Recognition.Contains(route);

    public bool Permits(TransformRoute route) => Transform.Contains(route);

    public bool Permits(RouteEndpoint endpoint) => Endpoints.Contains(endpoint.Kind);
}
