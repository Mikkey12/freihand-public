using Freihand.Core.Routing;

namespace Freihand.Core.Tests.Routing;

/// <summary>
/// Prüftests von Codex für P1, übernommen aus <c>codex-probes/</c> (Branch <c>codex/pruefproben</c>) mit unveränderten
/// Erwartungen: Review R3 und R4, Nachprüfung N4, zweite Nachprüfung F4 (WE-03, WE-10, SI-07, GE-04).
/// </summary>
public sealed class CodexProbeTests
{
    private static RouteRequest Request(DataClass dataClass, RecognitionRoute recognition, TransformRoute transform = TransformRoute.None) =>
        new(dataClass, recognition, Endpoint(recognition.ExpectedEndpoint()), transform, Endpoint(transform.ExpectedEndpoint()));

    private static RouteEndpoint Endpoint(EndpointKind kind) => kind == EndpointKind.Device
        ? RouteEndpoint.ThisDevice : new(kind, "https://example.test");

    private static RoutePolicy Policy(DataClass? minimum = null) => new(new(Edition.Private)
    {
        It = new() { DataClasses = [new("oeffentlich", 5), new("intern", 10), new("vertraulich", 20)], MinimumDataClass = minimum },
    });

    // ReviewRegressionTests.cs, R3
    [Fact]
    public void Review_LocalOnlyCancelsEveryJobWhenCallbackThrows()
    {
        var localOnly = new LocalOnlySwitch();
        var route = new RouteRequest(DataClass.Internal, RecognitionRoute.Home,
            new RouteEndpoint(EndpointKind.Home, "https://home.example"), TransformRoute.None, RouteEndpoint.ThisDevice);
        using var first = localOnly.Track(route);
        using var second = localOnly.Track(route);
        using var registration = first.DiscardToken.Register(() => throw new InvalidOperationException("Test callback"));
        try
        {
            localOnly.Engage();
        }
        catch (AggregateException)
        {
            // Der Fehler des Rückrufs ist erwartet; geprüft wird, dass der zweite Auftrag trotzdem verworfen ist
        }

        Assert.True(second.IsDiscarded);
    }

    // ReviewRegressionTests.cs, R4
    [Fact]
    public void Review_ItMinimumCannotBeBypassedByCallerRank()
    {
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Private)
        {
            It = new ItDirectives { MinimumDataClass = DataClass.Confidential },
        });
        var route = new RouteRequest(new DataClass("oeffentlich", 99), RecognitionRoute.Online,
            new RouteEndpoint(EndpointKind.Provider, "https://provider.example"), TransformRoute.None, RouteEndpoint.ThisDevice);
        Assert.False(policy.Evaluate(route).Allowed);
    }

    // FollowupBoundaryTests.cs, R4 und N4
    [Fact]
    public void CustomItClassWorksWithMatchingDefinitionAndRule()
    {
        var definition = new DataClass("firmengeheim", 10);
        var policy = new RoutePolicy(new(Edition.Business)
        {
            It = new ItDirectives
            {
                DataClasses = [definition],
                DataClassRules = new Dictionary<string, DataClassRule>
                {
                    [definition.Id] = new([RecognitionRoute.Device], [TransformRoute.None], [EndpointKind.Device]),
                },
            },
        });
        Assert.True(policy.Evaluate(new(new DataClass("firmengeheim", 10), RecognitionRoute.Device, RouteEndpoint.ThisDevice, TransformRoute.None, RouteEndpoint.ThisDevice)).Allowed);
    }

    // SecondFollowupTests.cs, R4 und N4
    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(int.MaxValue)]
    public void SuppliedRanksNeverBypassMinimumOrLowerClass(int rank)
    {
        var policy = Policy(new("vertraulich", int.MinValue));
        var supplied = new DataClass("oeffentlich", rank);
        Assert.Equal(new("vertraulich", 20), policy.EffectiveDataClass(supplied));
        Assert.False(policy.Evaluate(Request(supplied, RecognitionRoute.Online)).Allowed);
        Assert.False(Policy().Evaluate(Request(new("vertraulich", rank), RecognitionRoute.Home)).Allowed);
        Assert.Null(policy.EffectiveDataClass(new("unbekannt", rank)));
    }

    // SecondFollowupTests.cs, F4
    [Fact]
    public void FallbackPreservesStricterCandidateAcrossAnotherFailure()
    {
        var fallback = new RouteFallback(Policy());
        var first = fallback.Select(Request(DataClass.Public, RecognitionRoute.Home),
            [Request(DataClass.Confidential, RecognitionRoute.Device)]);
        Assert.True(first.Found);
        var second = fallback.Select(first.Route!, [Request(DataClass.Public, RecognitionRoute.Home)]);
        Assert.False(second.Found, $"Erste Ersatzroute trägt {first.Route!.DataClass.Id}; zweiter Heim-Ausweichweg wurde erlaubt.");
    }

    // SecondFollowupTests.cs, N4
    [Fact]
    public void ForgedFallbackCannotBypassRedefinedMinimum()
    {
        var fallback = new RouteFallback(Policy(new("vertraulich", -1)));
        var result = fallback.Select(Request(new("oeffentlich", int.MaxValue), RecognitionRoute.Device),
            [Request(new("oeffentlich", int.MaxValue), RecognitionRoute.Home)]);
        Assert.False(result.Found);
        Assert.Equal(DenialReason.DataClass, Assert.Single(result.Rejected).Decision.Denial);
    }
}
