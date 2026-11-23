namespace Freihand.Core.Routing;

/// <summary>Die beiden Ausgaben aus einem Kern (Kapitel 2, Grundsatz „Zwei Ausgaben, ein Kern“).</summary>
public enum Edition
{
    Private,

    Business,
}

/// <summary>Ob ein Weg in einer Ausgabe vorhanden ist (Kapitel 5).</summary>
public enum RouteAvailability
{
    /// <summary>Der Baustein fehlt in dieser Ausgabe; auch die IT kann ihn nicht einschalten (GE-02, WE-11).</summary>
    Unavailable,

    /// <summary>Nur nach eigener Freigabe durch die IT, z. B. Firmendienst oder lokales Umformen nach Messung (WE-15).</summary>
    RequiresApproval,

    Available,
}

/// <summary>Die Tabelle aus Kapitel 5: welche Wege welche Ausgabe kennt.</summary>
public static class EditionRoutes
{
    public static RouteAvailability Availability(Edition edition, RecognitionRoute route) => (edition, route) switch
    {
        (Edition.Private, RecognitionRoute.Online or RecognitionRoute.Home or RecognitionRoute.Device) =>
            RouteAvailability.Available,
        (Edition.Private, RecognitionRoute.CompanyService) => RouteAvailability.Unavailable,

        // GE-01, GE-02: im Geschäft kein Audio nach aussen; ein Firmen-Erkennungsdienst nur nach WE-15
        (Edition.Business, RecognitionRoute.Device) => RouteAvailability.Available,
        (Edition.Business, RecognitionRoute.CompanyService) => RouteAvailability.RequiresApproval,
        (Edition.Business, _) => RouteAvailability.Unavailable,
        _ => RouteAvailability.Unavailable,
    };

    public static RouteAvailability Availability(Edition edition, TransformRoute route) => (edition, route) switch
    {
        (Edition.Private, TransformRoute.CompanyService) => RouteAvailability.Unavailable,
        (Edition.Private, _) => RouteAvailability.Available,

        // Grundentscheid 6, WE-08: lokal erst nach Messung, Firmendienst erst nach eigener Freigabe
        (Edition.Business, TransformRoute.None) => RouteAvailability.Available,
        (Edition.Business, TransformRoute.Local or TransformRoute.CompanyService) => RouteAvailability.RequiresApproval,
        (Edition.Business, _) => RouteAvailability.Unavailable,
        _ => RouteAvailability.Unavailable,
    };
}
