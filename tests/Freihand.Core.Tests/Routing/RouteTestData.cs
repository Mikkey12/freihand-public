using Freihand.Core.Routing;

namespace Freihand.Core.Tests.Routing;

/// <summary>Erfundene Endpunkte und Hilfen für die Richtlinientests; keine echten Adressen oder Zugänge.</summary>
internal static class RouteTestData
{
    public static readonly RouteEndpoint Home = new(EndpointKind.Home, "sprichblitz.example");

    public static readonly RouteEndpoint Provider = new(EndpointKind.Provider, "anbieter.example");

    public static readonly RouteEndpoint CompanyTenant = new(EndpointKind.CompanyTenant, "firma.example");

    /// <summary>Eine Klasse, die nur die IT kennt (GE-04).</summary>
    public static readonly DataClass StrictlyConfidential = new("streng-vertraulich", 3);

    public static readonly string[] AllAddresses = [Home.Address, Provider.Address, CompanyTenant.Address];

    public static RouteEndpoint EndpointFor(EndpointKind kind) => kind switch
    {
        EndpointKind.Device => RouteEndpoint.ThisDevice,
        EndpointKind.Home => Home,
        EndpointKind.Provider => Provider,
        EndpointKind.CompanyTenant => CompanyTenant,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Eine Anfrage, deren Endpunkte zu den Wegen passen.</summary>
    public static RouteRequest Request(DataClass dataClass, RecognitionRoute recognition, TransformRoute transform) =>
        new(
            dataClass,
            recognition,
            EndpointFor(recognition.ExpectedEndpoint()),
            transform,
            EndpointFor(transform.ExpectedEndpoint()));

    /// <summary>IT-Vorgaben, die alle Freigabewege und alle Test-Endpunkte freigeben.</summary>
    public static ItDirectives ApproveEverything() => new()
    {
        ApprovedRecognition = new HashSet<RecognitionRoute>(Enum.GetValues<RecognitionRoute>()),
        ApprovedTransform = new HashSet<TransformRoute>(Enum.GetValues<TransformRoute>()),
        ApprovedEndpoints = new HashSet<string>(AllAddresses, StringComparer.Ordinal),
    };
}
