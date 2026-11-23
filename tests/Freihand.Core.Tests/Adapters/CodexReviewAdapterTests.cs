using Freihand.Adapters.Fakes;
using Freihand.Adapters.Selection;
using Freihand.Core.Routing;

namespace Freihand.Core.Tests.Adapters;

/// <summary>
/// Prüftests von Codex für P6 mit P1, unverändert übernommen aus <c>codex-probes/SecondFollowupTests.cs</c>
/// (Branch <c>codex/pruefproben</c>), Befund N4: Auch beim Ausweichen gelten von der IT neu definierte
/// Datenklassen und die Mindestklasse (WE-03, WE-10, GE-04).
/// </summary>
public sealed class CodexReviewAdapterTests
{
    private static RouteRequest Request(DataClass dataClass, RecognitionRoute recognition, TransformRoute transform = TransformRoute.None) =>
        new(dataClass, recognition, Endpoint(recognition.ExpectedEndpoint()), transform, Endpoint(transform.ExpectedEndpoint()));

    private static RouteEndpoint Endpoint(EndpointKind kind) => kind == EndpointKind.Device
        ? RouteEndpoint.ThisDevice : new(kind, "https://example.test");

    private static RoutePolicy Policy(DataClass? minimum = null) => new(new(Edition.Private)
    {
        It = new() { DataClasses = [new("oeffentlich", 5), new("intern", 10), new("vertraulich", 20)], MinimumDataClass = minimum },
    });

    [Fact]
    public void HelperRequestMatchesPolicyExpectations()
    {
        // Die Hilfe Request aus den Prüftests: Endpunkte passen zum Weg, die Richtlinie prüft also nur die Klasse
        Assert.True(Policy().Evaluate(Request(new("intern", 10), RecognitionRoute.Home)).Allowed);
        Assert.False(Policy().Evaluate(Request(new("vertraulich", 20), RecognitionRoute.Home)).Allowed);
    }

    [Theory]
    [InlineData("intern", false, true)]
    [InlineData("vertraulich", false, false)]
    [InlineData("oeffentlich", true, false)]
    public void AdapterFallbackHonoursRedefinedClassesAndMinimum(string id, bool minimum, bool homeAllowed)
    {
        var policy = Policy(minimum ? new("vertraulich", -1) : null);
        var registry = new AdapterRegistry(Edition.Private, policy);
        foreach (var route in new[] { RecognitionRoute.Device, RecognitionRoute.Home })
        {
            registry.Register(new RecognitionAdapter(route.ToString(), Endpoint(route.ExpectedEndpoint()),
                new SimulatedRecognitionEngine(new VirtualClock(), [], new SimulatedRecognitionOptions { Route = route })));
        }
        var selected = registry.Select(new(new(id, int.MaxValue), RecognitionRoute.Device, TransformRoute.None,
            RecognitionFallbacks: [RecognitionRoute.Home], Unavailable: new HashSet<string> { "Device" }));
        Assert.Equal(homeAllowed, selected.IsSelected);
        if (homeAllowed)
        {
            Assert.Equal(RecognitionRoute.Home, selected.ActiveRecognition);
            Assert.True(policy.Evaluate(selected.Route!).Allowed);
        }
        else
        {
            Assert.Contains(selected.Skipped, candidate => candidate.Issue == CandidateIssue.PolicyDenied && candidate.Denial == DenialReason.DataClass);
        }
    }

    [Theory]
    [InlineData("intern", false, true)]
    [InlineData("vertraulich", false, false)]
    [InlineData("oeffentlich", true, false)]
    public void AdapterTransformFallbackHonoursRedefinedClassesAndMinimum(string id, bool minimum, bool homeAllowed)
    {
        var registry = new AdapterRegistry(Edition.Private, Policy(minimum ? new("vertraulich", -1) : null));
        registry.Register(new RecognitionAdapter("device", RouteEndpoint.ThisDevice,
            new SimulatedRecognitionEngine(new VirtualClock(), [], new SimulatedRecognitionOptions { Route = RecognitionRoute.Device })));
        foreach (var route in new[] { TransformRoute.Local, TransformRoute.Home })
        {
            registry.Register(new TransformAdapter(route.ToString(), Endpoint(route.ExpectedEndpoint()),
                new SimulatedTransformer(new VirtualClock(), new SimulatedTransformOptions { Route = route })));
        }
        var selected = registry.Select(new(new(id, int.MinValue), RecognitionRoute.Device, TransformRoute.Local,
            TransformFallbacks: [TransformRoute.Home], Unavailable: new HashSet<string> { "Local" }));
        Assert.Equal(homeAllowed, selected.IsSelected);
        if (homeAllowed) Assert.Equal(TransformRoute.Home, selected.ActiveTransform);
        else Assert.Contains(selected.Skipped, candidate => candidate.Issue == CandidateIssue.PolicyDenied);
    }
}
