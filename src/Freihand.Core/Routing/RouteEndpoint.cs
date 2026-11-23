namespace Freihand.Core.Routing;

/// <summary>Wohin Audio oder Text tatsächlich gehen (WE-03). Heim ist ein eigener Endpunkt, nicht lokal (Kapitel 5).</summary>
public enum EndpointKind
{
    /// <summary>Dieses Gerät; nichts verlässt den PC.</summary>
    Device,

    /// <summary>Sprichblitz auf dem Mac über den Cloudflare Tunnel (WE-05, WE-16).</summary>
    Home,

    /// <summary>Ein Online-Anbieter, für Erkennung oder Umformen (WE-04, WE-13).</summary>
    Provider,

    /// <summary>Ein genehmigter Dienst im Firmen-Tenant (WE-08, WE-15).</summary>
    CompanyTenant,
}

/// <summary>
/// Ein tatsächlicher Endpunkt eines Verarbeitungsschritts. <see cref="Address"/> ist der Host oder die Kennung
/// des Dienstes, gegen die freigegebene Endpunkte geprüft werden (GE-04, OF-01); sie enthält nie Zugangsdaten (SI-03).
/// </summary>
public sealed record RouteEndpoint(EndpointKind Kind, string Address)
{
    public static RouteEndpoint ThisDevice { get; } = new(EndpointKind.Device, "geraet");

    public bool LeavesDevice => Kind != EndpointKind.Device;
}

public static class EndpointKindExtensions
{
    /// <summary>Bezeichnung für Begründungen und Anzeige (SI-06).</summary>
    public static string DisplayName(this EndpointKind kind) => kind switch
    {
        EndpointKind.Device => "Gerät",
        EndpointKind.Home => "Heim",
        EndpointKind.Provider => "Anbieter",
        EndpointKind.CompanyTenant => "Firmen-Tenant",
        _ => kind.ToString(),
    };
}
