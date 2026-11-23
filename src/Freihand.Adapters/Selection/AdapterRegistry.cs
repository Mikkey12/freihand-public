using Freihand.Core.Routing;

namespace Freihand.Adapters.Selection;

/// <summary>
/// Adapterverwaltung (WE-02, WE-10, WE-11): meldet Erkennungs- und Umformadapter an und wählt sie nach
/// Weg und Fähigkeiten aus. Welche Wege eine Ausgabe kennt, steht in <see cref="EditionRoutes"/>; ob eine
/// Kombination erlaubt ist, entscheidet allein die Richtlinie (<see cref="IRoutePolicy"/>, Paket P1). Die
/// Verwaltung fragt sie je Kombination aus Erkennung und Umformen mit einer <see cref="RouteRequest"/> samt
/// beiden tatsächlichen Endpunkten (WE-03) und ruft selbst nie einen Adapter auf.
/// </summary>
/// <remarks>
/// Ablauf der Auswahl: Geprüft werden Wegkombinationen in fester Reihenfolge, zuerst der Erkennungsweg
/// des Profils mit dem Umformweg des Profils, dann dieselbe Erkennung mit den Ausweichwegen des
/// Umformens, danach jeder Ausweichweg der Erkennung in derselben Art.
/// <list type="bullet">
/// <item>Die Kombination des Profils muss in der Ausgabe vorkommen, mit passendem Adapter angemeldet und
/// von der Richtlinie erlaubt sein; sonst wird verweigert, nie umgeleitet (WE-03, WE-11). Die Richtlinie
/// wird vor der Verfügbarkeit gefragt. Wege, die eine Freigabe brauchen
/// (<see cref="RouteAvailability.RequiresApproval"/>), gelten nur, wenn die Richtlinie sie erlaubt, also
/// die IT sie freigegeben hat (<see cref="ItDirectives"/>, WE-15).</item>
/// <item>Ausgewichen wird nur, wenn die Adapter des Profilwegs ausgefallen sind. Ein Ausweichweg, den die
/// Richtlinie nicht erlaubt, wird übergangen (WE-10).</item>
/// <item>Jede übergangene Kombination steht mit Grund in <see cref="AdapterSelection.Skipped"/>.</item>
/// </list>
/// </remarks>
public sealed class AdapterRegistry(Edition edition, IRoutePolicy policy)
{
    private readonly IRoutePolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly List<RecognitionAdapter> _recognition = [];
    private readonly List<TransformAdapter> _transform = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public Edition Edition { get; } = edition;

    public IReadOnlyList<RecognitionAdapter> RecognitionAdapters => _recognition;

    public IReadOnlyList<TransformAdapter> TransformAdapters => _transform;

    /// <summary>
    /// Meldet einen Erkennungsadapter an. Ein Weg, der in der Ausgabe fehlt, ist nicht anmeldbar (WE-11);
    /// ein Weg, der eine Freigabe braucht, schon, er wird aber erst mit Freigabe ausgewählt.
    /// </summary>
    public void Register(RecognitionAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        if (!Contains(adapter.Route))
        {
            throw new InvalidOperationException(
                $"Den Erkennungsweg {adapter.Route.DisplayName()} gibt es in der Ausgabe {Edition} nicht (WE-11).");
        }

        AddId(adapter.Id);
        _recognition.Add(adapter);
    }

    /// <summary>Meldet einen Umformadapter an; wie bei der Erkennung (WE-11).</summary>
    public void Register(TransformAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        if (adapter.Route == TransformRoute.None)
        {
            throw new ArgumentException("Für exaktes Diktat braucht es keinen Umformadapter.", nameof(adapter));
        }

        if (!Contains(adapter.Route))
        {
            throw new InvalidOperationException(
                $"Den Umformweg {adapter.Route.DisplayName()} gibt es in der Ausgabe {Edition} nicht (WE-11).");
        }

        AddId(adapter.Id);
        _transform.Add(adapter);
    }

    /// <summary>Ob die Ausgabe den Weg kennt, mit oder ohne nötige Freigabe (Kapitel 5).</summary>
    public bool Contains(RecognitionRoute route) =>
        EditionRoutes.Availability(Edition, route) != RouteAvailability.Unavailable;

    /// <summary>Ob die Ausgabe den Weg kennt, mit oder ohne nötige Freigabe (Kapitel 5).</summary>
    public bool Contains(TransformRoute route) =>
        EditionRoutes.Availability(Edition, route) != RouteAvailability.Unavailable;

    public AdapterSelection Select(SelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requirements = request.Requirements ?? RecognitionRequirements.None;
        var unavailable = request.Unavailable ?? new HashSet<string>();
        var recognitionRoutes = Ordered(request.Recognition, request.RecognitionFallbacks);
        var transformRoutes = Ordered(request.Transform, request.TransformFallbacks);
        var skipped = new List<SkippedCandidate>();

        foreach (var recognitionRoute in recognitionRoutes)
        {
            foreach (var transformRoute in transformRoutes)
            {
                var isProfileRoute = recognitionRoute == request.Recognition && transformRoute == request.Transform;

                AdapterSelection? Skip(string? adapterId, CandidateIssue issue, string detail, DenialReason denial = DenialReason.None)
                {
                    if (isProfileRoute && issue != CandidateIssue.Unavailable)
                    {
                        return new AdapterSelection(null, null, null, false, issue, denial, detail, [.. skipped]);
                    }

                    skipped.Add(new SkippedCandidate(recognitionRoute, transformRoute, adapterId, issue, denial, detail));
                    return null;
                }

                if (!Contains(recognitionRoute) || !Contains(transformRoute))
                {
                    var missing = !Contains(recognitionRoute)
                        ? $"Erkennungsweg {recognitionRoute.DisplayName()}"
                        : $"Umformweg {transformRoute.DisplayName()}";
                    if (Skip(null, CandidateIssue.RouteNotInEdition, $"{missing} gibt es in der Ausgabe {Edition} nicht") is { } refusal)
                    {
                        return refusal;
                    }

                    continue;
                }

                var recognitionCandidates = _recognition
                    .Where(a => a.Route == recognitionRoute && requirements.IsSatisfiedBy(a.Engine.Capabilities))
                    .ToArray();
                TransformAdapter?[] transformCandidates = transformRoute == TransformRoute.None
                    ? [null]
                    : [.. _transform.Where(a => a.Route == transformRoute)];

                if (recognitionCandidates.Length == 0 || transformCandidates.Length == 0)
                {
                    var detail = recognitionCandidates.Length == 0
                        ? NoRecognitionDetail(recognitionRoute, requirements)
                        : $"kein Adapter für den Umformweg {transformRoute.DisplayName()}";
                    if (Skip(null, CandidateIssue.NoMatchingAdapter, detail) is { } refusal)
                    {
                        return refusal;
                    }

                    continue;
                }

                foreach (var recognition in recognitionCandidates)
                {
                    foreach (var transform in transformCandidates)
                    {
                        var adapterIds = transform is null ? recognition.Id : $"{recognition.Id}+{transform.Id}";

                        // WE-03: eine Anfrage für beide Schritte; ohne Umformen bleibt der Text auf dem Gerät
                        var route = new RouteRequest(
                            request.DataClass,
                            recognitionRoute,
                            recognition.Endpoint,
                            transformRoute,
                            transform?.Endpoint ?? RouteEndpoint.ThisDevice);
                        var decision = _policy.Evaluate(route);

                        if (!decision.Allowed)
                        {
                            if (Skip(adapterIds, CandidateIssue.PolicyDenied, decision.Reason, decision.Denial) is { } refusal)
                            {
                                return refusal;
                            }

                            continue;
                        }

                        var down = unavailable.Contains(recognition.Id) ? recognition.Id
                            : transform is not null && unavailable.Contains(transform.Id) ? transform.Id
                            : null;
                        if (down is not null)
                        {
                            Skip(adapterIds, CandidateIssue.Unavailable, $"{down} ist ausgefallen");
                            continue;
                        }

                        return new AdapterSelection(
                            recognition, transform, route, !isProfileRoute, null, DenialReason.None, string.Empty, [.. skipped]);
                    }
                }
            }
        }

        return new AdapterSelection(
            null,
            null,
            null,
            false,
            CandidateIssue.Unavailable,
            DenialReason.None,
            "Kein erlaubter Weg verfügbar; die App weicht nicht still aus (WE-10).",
            [.. skipped]);
    }

    private string NoRecognitionDetail(RecognitionRoute route, RecognitionRequirements requirements)
    {
        var gaps = _recognition
            .Where(a => a.Route == route)
            .Select(a => $"{a.Id}: {requirements.FindGap(a.Engine.Capabilities)}")
            .ToArray();
        return gaps.Length == 0
            ? $"kein Adapter für den Erkennungsweg {route.DisplayName()}"
            : $"kein Adapter mit den verlangten Fähigkeiten ({string.Join("; ", gaps)})";
    }

    private void AddId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!_ids.Add(id))
        {
            throw new InvalidOperationException($"Ein Adapter mit der Kennung „{id}“ ist schon angemeldet.");
        }
    }

    private static T[] Ordered<T>(T primary, IReadOnlyList<T>? fallbacks) => [.. new[] { primary }.Concat(fallbacks ?? []).Distinct()];
}
