using System.Diagnostics.CodeAnalysis;

namespace Freihand.Core.Routing;

/// <summary>
/// Prüft jede Verarbeitung vor der ersten Anfrage (WE-03): Notschalter (SI-07), Endpunkt passend zum Weg,
/// Wege der Ausgabe (Kapitel 5), Vorgaben der IT (GE-04), Datenklasse und freigegebene Endpunkte. Die erste
/// verletzte Regel verweigert die Route; umgeleitet wird nie, einen Ausweichweg wählt nur
/// <see cref="RouteFallback"/> nach einem Ausfall (WE-10).
/// </summary>
public sealed class RoutePolicy : IRoutePolicy
{
    private readonly RoutePolicyOptions _options;
    private readonly LocalOnlySwitch? _localOnly;
    private readonly Dictionary<string, DataClass> _dataClasses;
    private readonly DataClass? _minimum;

    /// <exception cref="ArgumentException">Die IT definiert eine Klasse doppelt oder die Mindestklasse ist nicht definiert.</exception>
    public RoutePolicy(RoutePolicyOptions options, LocalOnlySwitch? localOnly = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _localOnly = localOnly;
        _dataClasses = DefineDataClasses(options.It);
        _minimum = options.It?.MinimumDataClass is { } minimum
            ? Resolve(minimum) ?? throw new ArgumentException(
                $"Die Mindestklasse „{minimum.Id}“ ist nicht definiert (GE-04).", nameof(options))
            : null;
    }

    public PolicyDecision Evaluate(RouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CheckLocalOnly(request)
            ?? CheckEndpointsMatchRoutes(request)
            ?? CheckEdition(request)
            ?? CheckItBlocks(request)
            ?? CheckDataClass(request)
            ?? CheckApprovedEndpoints(request)
            ?? PolicyDecision.Allow();
    }

    /// <summary>
    /// Die Datenklasse, die tatsächlich gilt: nach Kennung aus ihrer Definition aufgelöst und nie tiefer als die
    /// Mindestklasse der IT (WE-03, GE-04). Der mitgelieferte Rang wird ignoriert. <see langword="null"/>, wenn die
    /// Kennung nicht definiert ist.
    /// </summary>
    public DataClass? EffectiveDataClass(DataClass requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        if (Resolve(requested) is not { } resolved)
        {
            return null;
        }

        return _minimum is { } minimum ? DataClass.Highest(resolved, minimum) : resolved;
    }

    // Codex-Review R4, Nachprüfung N4: Der Rang zählt nur aus der Definition (Standardklassen, ergänzt oder neu
    // festgelegt durch die IT), nie aus der Anfrage; sonst hebelt ein erfundener Rang die Mindestklasse aus, und ein
    // Profil mit Standardrang passte nicht mehr zu einem Rang, den die IT neu festlegt
    private static Dictionary<string, DataClass> DefineDataClasses(ItDirectives? it)
    {
        var classes = new Dictionary<string, DataClass>(StringComparer.Ordinal)
        {
            [DataClass.Public.Id] = DataClass.Public,
            [DataClass.Internal.Id] = DataClass.Internal,
            [DataClass.Confidential.Id] = DataClass.Confidential,
        };

        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dataClass in it?.DataClasses ?? [])
        {
            if (!defined.Add(dataClass.Id))
            {
                throw new ArgumentException($"Die IT definiert die Datenklasse „{dataClass.Id}“ mehrfach (GE-04).", nameof(it));
            }

            classes[dataClass.Id] = dataClass;
        }

        return classes;
    }

    private DataClass? Resolve(DataClass dataClass) =>
        _dataClasses.TryGetValue(dataClass.Id, out var defined) ? defined : null;

    // SI-07: „nur lokal“ sperrt alles, was das Gerät verlässt, auch Heim, für beide Schritte
    private PolicyDecision? CheckLocalOnly(RouteRequest request) =>
        _localOnly is { IsEngaged: true } && request.LeavesDevice
            ? PolicyDecision.Deny(
                DenialReason.LocalOnly,
                "Notschalter „nur lokal“ ist an: keine Übertragung ausserhalb dieses Geräts, auch nicht über Heim (SI-07).")
            : null;

    // WE-03: geprüft wird der tatsächliche Endpunkt; ein Endpunkt, der nicht zum Weg passt, gilt als unzulässig
    private static PolicyDecision? CheckEndpointsMatchRoutes(RouteRequest request)
    {
        if (request.RecognitionEndpoint.Kind != request.Recognition.ExpectedEndpoint())
        {
            return PolicyDecision.Deny(
                DenialReason.EndpointMismatch,
                $"Erkennung {request.Recognition.DisplayName()}: Endpunkt {request.RecognitionEndpoint.Kind.DisplayName()} passt nicht zum Weg (WE-03).");
        }

        if (request.TransformEndpoint.Kind != request.Transform.ExpectedEndpoint())
        {
            return PolicyDecision.Deny(
                DenialReason.EndpointMismatch,
                $"Umformen {request.Transform.DisplayName()}: Endpunkt {request.TransformEndpoint.Kind.DisplayName()} passt nicht zum Weg (WE-03).");
        }

        return null;
    }

    // Kapitel 5, GE-02, WE-15: Wege, die die Ausgabe nicht kennt, fehlen; Freigabewege brauchen die IT
    private PolicyDecision? CheckEdition(RouteRequest request)
    {
        var it = _options.It;

        var recognition = EditionRoutes.Availability(_options.Edition, request.Recognition);
        if (Denied(recognition, it?.ApprovedRecognition.Contains(request.Recognition) == true) is { } recognitionDenial)
        {
            return PolicyDecision.Deny(
                recognitionDenial,
                $"Erkennung {request.Recognition.DisplayName()}: {Describe(recognitionDenial, _options.Edition)} (Kapitel 5).");
        }

        var transform = EditionRoutes.Availability(_options.Edition, request.Transform);
        if (Denied(transform, it?.ApprovedTransform.Contains(request.Transform) == true) is { } transformDenial)
        {
            return PolicyDecision.Deny(
                transformDenial,
                $"Umformen {request.Transform.DisplayName()}: {Describe(transformDenial, _options.Edition)} (Kapitel 5).");
        }

        return null;
    }

    private static DenialReason? Denied(RouteAvailability availability, bool approved) => availability switch
    {
        RouteAvailability.Available => null,
        RouteAvailability.RequiresApproval when approved => null,
        RouteAvailability.RequiresApproval => DenialReason.ApprovalMissing,
        _ => DenialReason.NotInEdition,
    };

    private static string Describe(DenialReason denial, Edition edition) => denial switch
    {
        DenialReason.ApprovalMissing => "braucht eine eigene Freigabe der IT",
        _ => edition == Edition.Business ? "gibt es in der Geschäftsausgabe nicht" : "gibt es in der privaten Ausgabe nicht",
    };

    // GE-04: Sperren der IT gehen jeder Freigabe, jedem Profil und jedem Import vor
    private PolicyDecision? CheckItBlocks(RouteRequest request)
    {
        if (_options.It is not { } it)
        {
            return null;
        }

        if (it.BlockedRecognition.Contains(request.Recognition))
        {
            return PolicyDecision.Deny(
                DenialReason.BlockedByIt,
                $"Erkennung {request.Recognition.DisplayName()} ist durch die IT gesperrt (GE-04).");
        }

        if (it.BlockedTransform.Contains(request.Transform))
        {
            return PolicyDecision.Deny(
                DenialReason.BlockedByIt,
                $"Umformen {request.Transform.DisplayName()} ist durch die IT gesperrt (GE-04).");
        }

        return null;
    }

    // WE-03: die Datenklasse gilt für beide Schritte und jeden Endpunkt; Regeln der IT ersetzen die Grundregeln
    private PolicyDecision? CheckDataClass(RouteRequest request)
    {
        if (EffectiveDataClass(request.DataClass) is not { } dataClass)
        {
            return PolicyDecision.Deny(
                DenialReason.UnknownDataClass,
                $"Die Datenklasse „{request.DataClass.Id}“ ist nicht definiert; die Route wird verweigert (WE-03).");
        }

        if (!TryGetRule(dataClass, out var rule))
        {
            return PolicyDecision.Deny(
                DenialReason.UnknownDataClass,
                $"Für die Datenklasse „{dataClass.Id}“ gibt es keine Regel; die Route wird verweigert (WE-03).");
        }

        if (!rule.Permits(request.Recognition))
        {
            return PolicyDecision.Deny(
                DenialReason.DataClass,
                $"Datenklasse „{dataClass.Id}“ erlaubt die Erkennung {request.Recognition.DisplayName()} nicht (WE-03).");
        }

        if (!rule.Permits(request.Transform))
        {
            return PolicyDecision.Deny(
                DenialReason.DataClass,
                $"Datenklasse „{dataClass.Id}“ erlaubt das Umformen {request.Transform.DisplayName()} nicht (WE-03).");
        }

        foreach (var endpoint in Endpoints(request))
        {
            if (!rule.Permits(endpoint))
            {
                return PolicyDecision.Deny(
                    DenialReason.DataClass,
                    $"Datenklasse „{dataClass.Id}“ erlaubt den Endpunkt {endpoint.Kind.DisplayName()} nicht (WE-03).");
            }
        }

        return null;
    }

    private bool TryGetRule(DataClass dataClass, [NotNullWhen(true)] out DataClassRule? rule)
    {
        if (_options.It?.DataClassRules?.TryGetValue(dataClass.Id, out var itRule) == true)
        {
            rule = itRule;
            return true;
        }

        if (_options.DataClassRules.TryGetValue(dataClass.Id, out var baseRule))
        {
            rule = baseRule;
            return true;
        }

        rule = null;
        return false;
    }

    // GE-01, GE-04, OF-01: Text nur an ausdrücklich freigegebene Endpunkte; im Geschäft ohne Vorgabe der IT keiner
    private PolicyDecision? CheckApprovedEndpoints(RouteRequest request)
    {
        foreach (var endpoint in Endpoints(request).Where(e => e.LeavesDevice))
        {
            if (!IsApproved(endpoint))
            {
                return PolicyDecision.Deny(
                    DenialReason.EndpointNotApproved,
                    $"Endpunkt {endpoint.Kind.DisplayName()} „{endpoint.Address}“ ist nicht freigegeben (GE-04).");
            }
        }

        return null;
    }

    private bool IsApproved(RouteEndpoint endpoint)
    {
        var itEndpoints = _options.It?.ApprovedEndpoints;

        if (itEndpoints is null && _options.Edition == Edition.Business)
        {
            return false;
        }

        return (itEndpoints?.Contains(endpoint.Address) ?? true)
            && (_options.ApprovedEndpoints?.Contains(endpoint.Address) ?? true);
    }

    private static IEnumerable<RouteEndpoint> Endpoints(RouteRequest request)
    {
        yield return request.RecognitionEndpoint;
        yield return request.TransformEndpoint;
    }
}
