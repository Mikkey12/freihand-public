using Freihand.Core.Routing;
using static Freihand.Core.Tests.Routing.RouteTestData;

namespace Freihand.Core.Tests.Routing;

/// <summary>
/// Abnahme WE-03, SI-07: alle Kombinationen von Datenklasse, Erkennung, Umformen, Endpunkt (je Schritt, Heim als
/// eigener Endpunkt) und Ausgabe, je mit Notschalter aus und an und mit und ohne Freigaben der IT. Erwartet wird
/// gegen die Tabellen unten, die Kapitel 5 und die vorgeschlagenen Standardregeln wörtlich abschreiben.
/// </summary>
public class RoutePolicyMatrixTests
{
    private const string Allowed = "erlaubt";
    private const string Approval = "nach Freigabe";
    private const string Missing = "fehlt";

    // Kapitel 5, Spalten „Privat“ und „Geschäft“; „–“ und „gesperrt“ heissen hier „fehlt“
    private static readonly Dictionary<(Edition, RecognitionRoute), string> RecognitionTable = new()
    {
        [(Edition.Private, RecognitionRoute.Online)] = Allowed,
        [(Edition.Private, RecognitionRoute.Home)] = Allowed,
        [(Edition.Private, RecognitionRoute.Device)] = Allowed,
        [(Edition.Private, RecognitionRoute.CompanyService)] = Missing,
        [(Edition.Business, RecognitionRoute.Online)] = Missing,
        [(Edition.Business, RecognitionRoute.Home)] = Missing,
        [(Edition.Business, RecognitionRoute.Device)] = Allowed,
        [(Edition.Business, RecognitionRoute.CompanyService)] = Approval,
    };

    private static readonly Dictionary<(Edition, TransformRoute), string> TransformTable = new()
    {
        [(Edition.Private, TransformRoute.None)] = Allowed,
        [(Edition.Private, TransformRoute.Local)] = Allowed,
        [(Edition.Private, TransformRoute.Home)] = Allowed,
        [(Edition.Private, TransformRoute.PrivateProvider)] = Allowed,
        [(Edition.Private, TransformRoute.CompanyService)] = Missing,
        [(Edition.Business, TransformRoute.None)] = Allowed,
        [(Edition.Business, TransformRoute.Local)] = Approval,
        [(Edition.Business, TransformRoute.Home)] = Missing,
        [(Edition.Business, TransformRoute.PrivateProvider)] = Missing,
        [(Edition.Business, TransformRoute.CompanyService)] = Approval,
    };

    private static readonly Dictionary<RecognitionRoute, EndpointKind> RecognitionEndpoints = new()
    {
        [RecognitionRoute.Online] = EndpointKind.Provider,
        [RecognitionRoute.Home] = EndpointKind.Home,
        [RecognitionRoute.Device] = EndpointKind.Device,
        [RecognitionRoute.CompanyService] = EndpointKind.CompanyTenant,
    };

    private static readonly Dictionary<TransformRoute, EndpointKind> TransformEndpoints = new()
    {
        [TransformRoute.None] = EndpointKind.Device,
        [TransformRoute.Local] = EndpointKind.Device,
        [TransformRoute.Home] = EndpointKind.Home,
        [TransformRoute.PrivateProvider] = EndpointKind.Provider,
        [TransformRoute.CompanyService] = EndpointKind.CompanyTenant,
    };

    // Vorgeschlagene Standardregeln je Datenklasse (WE-03)
    private static readonly Dictionary<string, (RecognitionRoute[] Recognition, TransformRoute[] Transform, EndpointKind[] Endpoints)> ClassTable = new()
    {
        ["oeffentlich"] = (
            [RecognitionRoute.Online, RecognitionRoute.Home, RecognitionRoute.Device, RecognitionRoute.CompanyService],
            [TransformRoute.None, TransformRoute.Local, TransformRoute.Home, TransformRoute.PrivateProvider, TransformRoute.CompanyService],
            [EndpointKind.Device, EndpointKind.Home, EndpointKind.Provider, EndpointKind.CompanyTenant]),
        ["intern"] = (
            [RecognitionRoute.Home, RecognitionRoute.Device, RecognitionRoute.CompanyService],
            [TransformRoute.None, TransformRoute.Local, TransformRoute.Home, TransformRoute.CompanyService],
            [EndpointKind.Device, EndpointKind.Home, EndpointKind.CompanyTenant]),
        ["vertraulich"] = (
            [RecognitionRoute.Device],
            [TransformRoute.None, TransformRoute.Local],
            [EndpointKind.Device]),
    };

    private static readonly DataClass[] DataClasses =
        [DataClass.Public, DataClass.Internal, DataClass.Confidential, StrictlyConfidential];

    public static TheoryData<Edition, bool, bool> Scenarios()
    {
        var data = new TheoryData<Edition, bool, bool>();
        foreach (var edition in Enum.GetValues<Edition>())
        {
            foreach (var localOnly in new[] { false, true })
            {
                foreach (var itApproves in new[] { false, true })
                {
                    data.Add(edition, localOnly, itApproves);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void EveryCombinationMatchesTheTables(Edition edition, bool localOnly, bool itApproves)
    {
        var (policy, _) = CreatePolicy(edition, localOnly, itApproves);
        var failures = new List<string>();
        var combinations = 0;

        foreach (var request in AllRequests())
        {
            combinations++;
            var decision = policy.Evaluate(request);
            var expected = Expected(request, edition, localOnly, itApproves);

            if (decision.Allowed != expected)
            {
                failures.Add($"{Describe(request)}: erwartet {(expected ? "erlaubt" : "verweigert")}, war {decision}");
            }
            else if (!decision.Allowed && string.IsNullOrWhiteSpace(decision.Reason))
            {
                failures.Add($"{Describe(request)}: verweigert ohne Begründung");
            }
            else if (localOnly && LeavesDevice(request) && decision.Denial != DenialReason.LocalOnly)
            {
                failures.Add($"{Describe(request)}: Notschalter nicht als Grund gemeldet, war {decision.Denial}");
            }
        }

        Assert.Equal(DataClasses.Length * 4 * 4 * 5 * 4, combinations);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Zählt die zulässigen Kombinationen von Hand nach; prüft auch die Tabellen dieser Testklasse.</summary>
    [Theory]
    [InlineData(Edition.Private, false, false, "oeffentlich", 12)] // 3 Erkennungswege × 4 Umformwege
    [InlineData(Edition.Private, false, false, "intern", 6)] // Heim, Gerät × keines, lokal, Heim
    [InlineData(Edition.Private, false, false, "vertraulich", 2)] // Gerät × keines, lokal
    [InlineData(Edition.Private, false, false, "streng-vertraulich", 0)] // ohne Regel verweigert
    [InlineData(Edition.Private, true, false, "oeffentlich", 2)] // nur lokal: Gerät × keines, lokal
    [InlineData(Edition.Private, true, false, "intern", 2)]
    [InlineData(Edition.Business, false, false, "oeffentlich", 1)] // Gerät × keines
    [InlineData(Edition.Business, false, false, "vertraulich", 1)]
    [InlineData(Edition.Business, false, true, "oeffentlich", 6)] // Gerät, Firmendienst × keines, lokal, Firmendienst
    [InlineData(Edition.Business, false, true, "intern", 6)]
    [InlineData(Edition.Business, false, true, "vertraulich", 2)] // Gerät × keines, lokal
    [InlineData(Edition.Business, true, true, "oeffentlich", 2)]
    public void AllowedCombinationsAreCountedByHand(
        Edition edition, bool localOnly, bool itApproves, string dataClassId, int expectedAllowed)
    {
        var (policy, _) = CreatePolicy(edition, localOnly, itApproves);

        var allowed = AllRequests()
            .Where(r => r.DataClass.Id == dataClassId)
            .Count(r => policy.Evaluate(r).Allowed);

        Assert.Equal(expectedAllowed, allowed);
    }

    private static (RoutePolicy Policy, LocalOnlySwitch Switch) CreatePolicy(Edition edition, bool localOnly, bool itApproves)
    {
        var localOnlySwitch = new LocalOnlySwitch();
        if (localOnly)
        {
            localOnlySwitch.Engage();
        }

        var options = new RoutePolicyOptions(edition) { It = itApproves ? ApproveEverything() : null };
        return (new RoutePolicy(options, localOnlySwitch), localOnlySwitch);
    }

    private static IEnumerable<RouteRequest> AllRequests() =>
        from dataClass in DataClasses
        from recognition in Enum.GetValues<RecognitionRoute>()
        from recognitionEndpoint in Enum.GetValues<EndpointKind>()
        from transform in Enum.GetValues<TransformRoute>()
        from transformEndpoint in Enum.GetValues<EndpointKind>()
        select new RouteRequest(
            dataClass,
            recognition,
            EndpointFor(recognitionEndpoint),
            transform,
            EndpointFor(transformEndpoint));

    private static bool LeavesDevice(RouteRequest r) =>
        r.Recognition != RecognitionRoute.Device
        || r.Transform is not (TransformRoute.None or TransformRoute.Local)
        || r.RecognitionEndpoint.Kind != EndpointKind.Device
        || r.TransformEndpoint.Kind != EndpointKind.Device;

    private static bool Expected(RouteRequest r, Edition edition, bool localOnly, bool itApproves)
    {
        if (localOnly && LeavesDevice(r))
        {
            return false;
        }

        if (RecognitionEndpoints[r.Recognition] != r.RecognitionEndpoint.Kind
            || TransformEndpoints[r.Transform] != r.TransformEndpoint.Kind)
        {
            return false;
        }

        if (!EditionPermits(RecognitionTable[(edition, r.Recognition)], itApproves)
            || !EditionPermits(TransformTable[(edition, r.Transform)], itApproves))
        {
            return false;
        }

        if (!ClassTable.TryGetValue(r.DataClass.Id, out var rule)
            || !rule.Recognition.Contains(r.Recognition)
            || !rule.Transform.Contains(r.Transform)
            || !rule.Endpoints.Contains(r.RecognitionEndpoint.Kind)
            || !rule.Endpoints.Contains(r.TransformEndpoint.Kind))
        {
            return false;
        }

        // GE-01: im Geschäft nur an Endpunkte, die die IT freigegeben hat
        return !(edition == Edition.Business && !itApproves && LeavesDevice(r));
    }

    private static bool EditionPermits(string cell, bool itApproves) =>
        cell == Allowed || (cell == Approval && itApproves);

    private static string Describe(RouteRequest r) =>
        $"{r.DataClass.Id} / Erkennung {r.Recognition}@{r.RecognitionEndpoint.Kind} / Umformen {r.Transform}@{r.TransformEndpoint.Kind}";
}
