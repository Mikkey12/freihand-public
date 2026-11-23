namespace Freihand.Core.Routing;

/// <summary>Ein Kandidat, den die Richtlinie abgelehnt hat, mit Begründung.</summary>
public sealed record RejectedRoute(RouteRequest Route, PolicyDecision Decision);

/// <summary>
/// Ergebnis der Suche nach einem Ausweichweg. Ist <see cref="Route"/> leer, gibt es keinen zulässigen Ersatz und
/// die App meldet den Ausfall, statt still auszuweichen (WE-10, WE-16).
/// </summary>
public sealed record FallbackResult(RouteRequest Failed, RouteRequest? Route, IReadOnlyList<RejectedRoute> Rejected)
{
    public bool Found => Route is not null;
}

/// <summary>
/// Wählt nach dem Ausfall eines Wegs einen Ersatzweg, nur wenn die Richtlinie ihn erlaubt (WE-10). Ein Ausweichweg
/// ist kein Umleiten: War schon die ursprüngliche Route unzulässig, gibt es keinen Ersatz (WE-03). Der Ersatzweg trägt
/// die höhere der beiden Datenklassen, verglichen nach den definierten Rängen der Richtlinie, nie nach mitgelieferten.
/// </summary>
public sealed class RouteFallback(RoutePolicy policy)
{
    private readonly RoutePolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    /// <param name="failed">Die ausgefallene Route.</param>
    /// <param name="candidates">Die Ausweichwege des Profils in seiner Reihenfolge.</param>
    public FallbackResult Select(RouteRequest failed, IEnumerable<RouteRequest> candidates)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(candidates);

        var original = _policy.Evaluate(failed);
        if (!original.Allowed)
        {
            return new FallbackResult(failed, null, [new RejectedRoute(failed, original)]);
        }

        // Eine erlaubte Route hat immer eine definierte Klasse
        var failedClass = _policy.EffectiveDataClass(failed.DataClass)!;
        var rejected = new List<RejectedRoute>();
        foreach (var candidate in candidates)
        {
            // WE-03, Codex-Nachprüfung 2 (F4): Die höchste Einstufung bleibt bestehen und steht im Ergebnis, damit
            // auch ein weiterer Ausweichvorgang mit ihr rechnet. Unbekannte Klassen verweigert die Richtlinie
            var route = _policy.EffectiveDataClass(candidate.DataClass) is { } candidateClass
                ? candidate with { DataClass = DataClass.Highest(failedClass, candidateClass) }
                : candidate;
            if (route == failed with { DataClass = route.DataClass })
            {
                continue;
            }

            var decision = _policy.Evaluate(route);
            if (decision.Allowed)
            {
                return new FallbackResult(failed, route, rejected);
            }

            rejected.Add(new RejectedRoute(route, decision));
        }

        return new FallbackResult(failed, null, rejected);
    }
}
