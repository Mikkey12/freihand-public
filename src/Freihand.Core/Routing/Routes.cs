namespace Freihand.Core.Routing;

/// <summary>Wer das Audio erkennt (Kapitel 5, WE-01).</summary>
public enum RecognitionRoute
{
    /// <summary>Direkt zum Anbieter (WE-04).</summary>
    Online,

    /// <summary>Sprichblitz auf dem Mac über den Cloudflare Tunnel (WE-05).</summary>
    Home,

    /// <summary>Auf diesem Gerät (WE-07).</summary>
    Device,

    /// <summary>Genehmigter Erkennungsdienst im Firmen-Tenant, nur nach eigenem Freigabeentscheid (WE-15).</summary>
    CompanyService,
}

/// <summary>Wer den Text umformt (Kapitel 5, WE-01).</summary>
public enum TransformRoute
{
    /// <summary>Exaktes Diktat ohne Umformung.</summary>
    None,

    /// <summary>Sprachmodell auf diesem Gerät.</summary>
    Local,

    /// <summary>Sprachmodell auf dem Mac über Sprichblitz.</summary>
    Home,

    /// <summary>OpenAI, Gemini, Anthropic, OpenRouter, DeepSeek.</summary>
    PrivateProvider,

    /// <summary>Copilot oder Azure OpenAI, nur nach eigener Freigabe.</summary>
    CompanyService,
}

/// <summary>„Lokal“ heisst immer „auf diesem Gerät“; Heim ist nicht lokal (Kapitel 5, SI-07).</summary>
public static class RouteExtensions
{
    public static bool LeavesDevice(this RecognitionRoute route) => route != RecognitionRoute.Device;

    public static bool LeavesDevice(this TransformRoute route) =>
        route is not (TransformRoute.None or TransformRoute.Local);

    /// <summary>Die Art Endpunkt, an die dieser Weg das Audio schickt (WE-03).</summary>
    public static EndpointKind ExpectedEndpoint(this RecognitionRoute route) => route switch
    {
        RecognitionRoute.Online => EndpointKind.Provider,
        RecognitionRoute.Home => EndpointKind.Home,
        RecognitionRoute.Device => EndpointKind.Device,
        RecognitionRoute.CompanyService => EndpointKind.CompanyTenant,
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };

    /// <summary>Die Art Endpunkt, an die dieser Weg den Text schickt; ohne Umformen bleibt er auf dem Gerät (WE-03).</summary>
    public static EndpointKind ExpectedEndpoint(this TransformRoute route) => route switch
    {
        TransformRoute.None or TransformRoute.Local => EndpointKind.Device,
        TransformRoute.Home => EndpointKind.Home,
        TransformRoute.PrivateProvider => EndpointKind.Provider,
        TransformRoute.CompanyService => EndpointKind.CompanyTenant,
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };

    /// <summary>Bezeichnung wie im Katalog, für Begründungen und Anzeige (SI-06).</summary>
    public static string DisplayName(this RecognitionRoute route) => route switch
    {
        RecognitionRoute.Online => "Online",
        RecognitionRoute.Home => "Heim",
        RecognitionRoute.Device => "Gerät",
        RecognitionRoute.CompanyService => "Firmen-Erkennungsdienst",
        _ => route.ToString(),
    };

    /// <summary>Bezeichnung wie im Katalog, für Begründungen und Anzeige (SI-06).</summary>
    public static string DisplayName(this TransformRoute route) => route switch
    {
        TransformRoute.None => "keines",
        TransformRoute.Local => "lokal",
        TransformRoute.Home => "Heim",
        TransformRoute.PrivateProvider => "privater Anbieter",
        TransformRoute.CompanyService => "Firmendienst",
        _ => route.ToString(),
    };
}
