using Freihand.Adapters.Fakes;
using Freihand.Adapters.Selection;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;

namespace Freihand.Core.Tests.Adapters;

public class AdapterRegistryTests
{
    private static readonly VirtualClock Clock = new();
    private static readonly RouteEndpoint Provider = new(EndpointKind.Provider, "api.example");
    private static readonly RouteEndpoint Sprichblitz = new(EndpointKind.Home, "sprichblitz.example");
    private static readonly RouteEndpoint Tenant = new(EndpointKind.CompanyTenant, "firma.example");

    [Fact]
    public void BusinessEditionOmitsPrivateRoutesEntirely()
    {
        var registry = new AdapterRegistry(Edition.Business, new SpyPolicy());

        Assert.Throws<InvalidOperationException>(() => registry.Register(Recognition("online", RecognitionRoute.Online)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Recognition("heim", RecognitionRoute.Home)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Transform("anbieter", TransformRoute.PrivateProvider)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Transform("heim-llm", TransformRoute.Home)));
        Assert.Empty(registry.RecognitionAdapters);
        Assert.Empty(registry.TransformAdapters);

        registry.Register(Recognition("geraet", RecognitionRoute.Device));
        Assert.Single(registry.RecognitionAdapters);
    }

    [Fact]
    public void RoutesNeedingApprovalCanBeRegistered()
    {
        // Kapitel 5: im Geschäft lokales Umformen und Firmendienste nur nach Freigabe, aber vorhanden
        var business = new AdapterRegistry(Edition.Business, new SpyPolicy());
        business.Register(Recognition("firma-asr", RecognitionRoute.CompanyService));
        business.Register(Transform("lokal-llm", TransformRoute.Local));
        business.Register(Transform("copilot", TransformRoute.CompanyService));

        // Die private Ausgabe kennt die Firmendienste nicht (WE-11)
        var @private = new AdapterRegistry(Edition.Private, new SpyPolicy());
        Assert.Throws<InvalidOperationException>(() => @private.Register(Recognition("firma-asr", RecognitionRoute.CompanyService)));
        Assert.Throws<InvalidOperationException>(() => @private.Register(Transform("copilot", TransformRoute.CompanyService)));
    }

    [Fact]
    public void RejectsDuplicateIdsAndTransformerForExactDictation()
    {
        var registry = new AdapterRegistry(Edition.Private, new SpyPolicy());
        registry.Register(Recognition("a", RecognitionRoute.Device));

        Assert.Throws<InvalidOperationException>(() => registry.Register(Recognition("a", RecognitionRoute.Online)));
        Assert.Throws<ArgumentException>(() => registry.Register(Transform("keiner", TransformRoute.None)));
    }

    [Fact]
    public void AsksPolicyOnceWithBothActualEndpoints()
    {
        var policy = new SpyPolicy();
        var registry = PrivateRegistry(policy);

        var selection = registry.Select(new SelectionRequest(DataClass.Internal, RecognitionRoute.Device, TransformRoute.Home));

        var expected = new RouteRequest(DataClass.Internal, RecognitionRoute.Device, RouteEndpoint.ThisDevice, TransformRoute.Home, Sprichblitz);
        Assert.True(selection.IsSelected);
        Assert.False(selection.IsFallback);
        Assert.Equal("geraet", selection.Recognition!.Id);
        Assert.Equal("heim-llm", selection.Transform!.Id);
        Assert.Equal(RecognitionRoute.Device, selection.ActiveRecognition);
        Assert.Equal(TransformRoute.Home, selection.ActiveTransform);
        Assert.Equal(expected, Assert.Single(policy.Requests));
        Assert.Equal(expected, selection.Route);
    }

    [Fact]
    public void ExactDictationKeepsTextOnDevice()
    {
        var policy = new SpyPolicy();

        var selection = PrivateRegistry(policy)
            .Select(new SelectionRequest(DataClass.Public, RecognitionRoute.Online, TransformRoute.None));

        Assert.True(selection.IsSelected);
        Assert.Null(selection.Transform);
        Assert.Equal(TransformRoute.None, selection.ActiveTransform);
        Assert.Equal(
            new RouteRequest(DataClass.Public, RecognitionRoute.Online, Provider, TransformRoute.None, RouteEndpoint.ThisDevice),
            Assert.Single(policy.Requests));
    }

    [Fact]
    public void ChoosesAdapterByCapabilities()
    {
        var registry = new AdapterRegistry(Edition.Private, new SpyPolicy());
        registry.Register(Recognition("satzweise", RecognitionRoute.Device, partialResults: false));
        registry.Register(Recognition("live", RecognitionRoute.Device, partialResults: true));

        var batch = registry.Select(new SelectionRequest(DataClass.Public, RecognitionRoute.Device, TransformRoute.None));
        var live = registry.Select(new SelectionRequest(
            DataClass.Public,
            RecognitionRoute.Device,
            TransformRoute.None,
            new RecognitionRequirements(PartialResults: true)));

        Assert.Equal("satzweise", batch.Recognition!.Id);
        Assert.Equal("live", live.Recognition!.Id);
    }

    [Fact]
    public void RefusesWhenNoAdapterHasRequiredCapabilities()
    {
        var registry = new AdapterRegistry(Edition.Private, new SpyPolicy());
        registry.Register(Recognition("kurz", RecognitionRoute.Online, maxSession: TimeSpan.FromMinutes(5), region: "us"));
        registry.Register(Recognition("geraet", RecognitionRoute.Device));

        var selection = registry.Select(new SelectionRequest(
            DataClass.Public,
            RecognitionRoute.Online,
            TransformRoute.None,
            new RecognitionRequirements(MinSessionDuration: TimeSpan.FromMinutes(30)),
            RecognitionFallbacks: [RecognitionRoute.Device]));

        // Fehlende Fähigkeiten sind kein Ausfall: kein Ausweichen
        Assert.False(selection.IsSelected);
        Assert.Equal(CandidateIssue.NoMatchingAdapter, selection.RefusalIssue);
        Assert.Contains("kurz", selection.RefusalReason, StringComparison.Ordinal);
    }

    [Fact]
    public void RequirementsCompareRegionAndDuration()
    {
        var capabilities = new RecognitionCapabilities(true, true, false, true, TimeSpan.FromMinutes(5), "EU", "v1");

        Assert.True(new RecognitionRequirements(DataRegion: "eu").IsSatisfiedBy(capabilities));
        Assert.False(new RecognitionRequirements(DataRegion: "us").IsSatisfiedBy(capabilities));
        Assert.True(new RecognitionRequirements(MinSessionDuration: TimeSpan.FromMinutes(5)).IsSatisfiedBy(capabilities));
        Assert.False(new RecognitionRequirements(MinSessionDuration: TimeSpan.FromMinutes(6)).IsSatisfiedBy(capabilities));
        Assert.False(new RecognitionRequirements(VocabularyHints: true).IsSatisfiedBy(capabilities));
        Assert.True(RecognitionRequirements.None.IsSatisfiedBy(capabilities));
    }

    [Fact]
    public void DeniedProfileRouteIsRefusedNotRedirected()
    {
        var policy = new SpyPolicy(r => r.Recognition == RecognitionRoute.Online ? "Online für vertraulich gesperrt" : null);
        var registry = PrivateRegistry(policy);

        var selection = registry.Select(new SelectionRequest(
            DataClass.Confidential,
            RecognitionRoute.Online,
            TransformRoute.None,
            RecognitionFallbacks: [RecognitionRoute.Device]));

        Assert.False(selection.IsSelected);
        Assert.Null(selection.Route);
        Assert.Equal(CandidateIssue.PolicyDenied, selection.RefusalIssue);
        Assert.Equal(DenialReason.DataClass, selection.RefusalDenial);
        Assert.Contains("Online für vertraulich gesperrt", selection.RefusalReason, StringComparison.Ordinal);
        Assert.DoesNotContain(policy.Requests, r => r.Recognition == RecognitionRoute.Device);
    }

    [Fact]
    public void PolicyIsAskedBeforeAvailability()
    {
        var policy = new SpyPolicy(_ => "gesperrt");
        var registry = PrivateRegistry(policy);

        var selection = registry.Select(new SelectionRequest(
            DataClass.Confidential,
            RecognitionRoute.Online,
            TransformRoute.None,
            RecognitionFallbacks: [RecognitionRoute.Device],
            Unavailable: new HashSet<string> { "online" }));

        Assert.Equal(CandidateIssue.PolicyDenied, selection.RefusalIssue);
    }

    [Fact]
    public void FallsBackOnOutageWhenPolicyAllows()
    {
        var registry = PrivateRegistry(new SpyPolicy());

        var selection = registry.Select(new SelectionRequest(
            DataClass.Internal,
            RecognitionRoute.Online,
            TransformRoute.None,
            RecognitionFallbacks: [RecognitionRoute.Home, RecognitionRoute.Device],
            Unavailable: new HashSet<string> { "online" }));

        Assert.True(selection.IsSelected);
        Assert.True(selection.IsFallback);
        Assert.Equal(RecognitionRoute.Home, selection.ActiveRecognition);
        Assert.Equal(Sprichblitz, selection.Route!.RecognitionEndpoint);
        var skipped = Assert.Single(selection.Skipped);
        Assert.Equal(CandidateIssue.Unavailable, skipped.Issue);
        Assert.Equal("online", skipped.AdapterId);
    }

    [Fact]
    public void SkipsFallbackThatDataClassForbids()
    {
        // Mit der Richtlinie aus P1: „vertraulich“ erlaubt nur das Gerät, nicht Heim (WE-03, WE-10)
        var registry = PrivateRegistry(new RoutePolicy(new RoutePolicyOptions(Edition.Private)));
        registry.Register(Recognition("geraet-2", RecognitionRoute.Device));

        var selection = registry.Select(new SelectionRequest(
            DataClass.Confidential,
            RecognitionRoute.Device,
            TransformRoute.None,
            RecognitionFallbacks: [RecognitionRoute.Home],
            Unavailable: new HashSet<string> { "geraet", "geraet-2" }));

        Assert.False(selection.IsSelected);
        Assert.Equal(CandidateIssue.Unavailable, selection.RefusalIssue);
        Assert.Equal(
            [CandidateIssue.Unavailable, CandidateIssue.Unavailable, CandidateIssue.PolicyDenied],
            selection.Skipped.Select(s => s.Issue));
        Assert.Equal(DenialReason.DataClass, selection.Skipped[^1].Denial);
    }

    [Fact]
    public void FallbackRouteMissingInEditionIsSkipped()
    {
        var registry = new AdapterRegistry(Edition.Business, new SpyPolicy());
        registry.Register(Recognition("geraet", RecognitionRoute.Device));

        var selection = registry.Select(new SelectionRequest(
            DataClass.Internal,
            RecognitionRoute.Device,
            TransformRoute.None,
            RecognitionFallbacks: [RecognitionRoute.Online],
            Unavailable: new HashSet<string> { "geraet" }));

        Assert.False(selection.IsSelected);
        Assert.Equal(CandidateIssue.RouteNotInEdition, selection.Skipped[^1].Issue);
    }

    [Fact]
    public void ProfileRouteMissingInEditionIsRefused()
    {
        var registry = new AdapterRegistry(Edition.Business, new SpyPolicy());
        registry.Register(Recognition("geraet", RecognitionRoute.Device));

        var selection = registry.Select(new SelectionRequest(DataClass.Internal, RecognitionRoute.Device, TransformRoute.PrivateProvider));

        Assert.Equal(CandidateIssue.RouteNotInEdition, selection.RefusalIssue);
    }

    [Fact]
    public void RouteNeedingApprovalIsSelectedOnlyWithItApproval()
    {
        // Kapitel 5, WE-08: lokales Umformen im Geschäft erst nach Freigabe der IT (ItDirectives, P1)
        var withoutApproval = BusinessRegistry(new ItDirectives());
        var withApproval = BusinessRegistry(new ItDirectives
        {
            ApprovedTransform = new HashSet<TransformRoute> { TransformRoute.Local },
        });
        var request = new SelectionRequest(DataClass.Internal, RecognitionRoute.Device, TransformRoute.Local);

        var refused = withoutApproval.Select(request);
        var selected = withApproval.Select(request);

        Assert.Equal(CandidateIssue.PolicyDenied, refused.RefusalIssue);
        Assert.Equal(DenialReason.ApprovalMissing, refused.RefusalDenial);
        Assert.True(selected.IsSelected);
        Assert.Equal("lokal-llm", selected.Transform!.Id);
    }

    [Fact]
    public void FallbackNeedingApprovalIsSkippedWithoutIt()
    {
        var registry = BusinessRegistry(new ItDirectives());

        var selection = registry.Select(new SelectionRequest(
            DataClass.Internal,
            RecognitionRoute.Device,
            TransformRoute.None,
            RecognitionFallbacks: [RecognitionRoute.CompanyService],
            Unavailable: new HashSet<string> { "geraet" }));

        Assert.False(selection.IsSelected);
        var skipped = selection.Skipped[^1];
        Assert.Equal(CandidateIssue.PolicyDenied, skipped.Issue);
        Assert.Equal(DenialReason.ApprovalMissing, skipped.Denial);
    }

    [Fact]
    public void TransformFallsBackBeforeRecognitionChanges()
    {
        var registry = PrivateRegistry(new SpyPolicy());

        var selection = registry.Select(new SelectionRequest(
            DataClass.Internal,
            RecognitionRoute.Device,
            TransformRoute.Home,
            TransformFallbacks: [TransformRoute.Local],
            Unavailable: new HashSet<string> { "heim-llm" }));

        Assert.True(selection.IsFallback);
        Assert.Equal(RecognitionRoute.Device, selection.ActiveRecognition);
        Assert.Equal(TransformRoute.Local, selection.ActiveTransform);
    }

    [Fact]
    public void AnotherAdapterOnTheSameRouteIsNoFallback()
    {
        var registry = PrivateRegistry(new SpyPolicy());
        registry.Register(Recognition("geraet-2", RecognitionRoute.Device));

        var selection = registry.Select(new SelectionRequest(
            DataClass.Internal,
            RecognitionRoute.Device,
            TransformRoute.None,
            Unavailable: new HashSet<string> { "geraet" }));

        Assert.False(selection.IsFallback);
        Assert.Equal("geraet-2", selection.Recognition!.Id);
    }

    [Fact]
    public void AllUnavailableIsRefusedVisibly()
    {
        var selection = PrivateRegistry(new SpyPolicy()).Select(new SelectionRequest(
            DataClass.Internal,
            RecognitionRoute.Device,
            TransformRoute.None,
            Unavailable: new HashSet<string> { "geraet" }));

        Assert.False(selection.IsSelected);
        Assert.Null(selection.ActiveRecognition);
        Assert.Null(selection.ActiveTransform);
        Assert.Equal(CandidateIssue.Unavailable, selection.RefusalIssue);
    }

    private static AdapterRegistry PrivateRegistry(IRoutePolicy policy)
    {
        var registry = new AdapterRegistry(Edition.Private, policy);
        registry.Register(Recognition("online", RecognitionRoute.Online));
        registry.Register(Recognition("heim", RecognitionRoute.Home));
        registry.Register(Recognition("geraet", RecognitionRoute.Device));
        registry.Register(Transform("lokal-llm", TransformRoute.Local));
        registry.Register(Transform("heim-llm", TransformRoute.Home));
        return registry;
    }

    private static AdapterRegistry BusinessRegistry(ItDirectives it)
    {
        var registry = new AdapterRegistry(Edition.Business, new RoutePolicy(new RoutePolicyOptions(Edition.Business) { It = it }));
        registry.Register(Recognition("geraet", RecognitionRoute.Device));
        registry.Register(Recognition("firma-asr", RecognitionRoute.CompanyService));
        registry.Register(Transform("lokal-llm", TransformRoute.Local));
        return registry;
    }

    /// <summary>Der Endpunkt, an den der Weg tatsächlich geht (WE-03).</summary>
    private static RouteEndpoint EndpointOf(EndpointKind kind) => kind switch
    {
        EndpointKind.Device => RouteEndpoint.ThisDevice,
        EndpointKind.Home => Sprichblitz,
        EndpointKind.Provider => Provider,
        _ => Tenant,
    };

    private static RecognitionAdapter Recognition(
        string id,
        RecognitionRoute route,
        bool partialResults = true,
        TimeSpan? maxSession = null,
        string? region = null) => new(
        id,
        EndpointOf(route.ExpectedEndpoint()),
        new SimulatedRecognitionEngine(Clock, [], new SimulatedRecognitionOptions
        {
            Route = route,
            Capabilities = new RecognitionCapabilities(partialResults, true, false, true, maxSession, region, id),
        }));

    private static TransformAdapter Transform(string id, TransformRoute route) =>
        new(id, EndpointOf(route.ExpectedEndpoint()), new SimulatedTransformer(Clock, new SimulatedTransformOptions { Route = route }));

    /// <summary>Stellvertreter für die Richtlinie aus Paket P1: zeichnet Anfragen auf, verweigert nach Vorgabe.</summary>
    private sealed class SpyPolicy(Func<RouteRequest, string?>? deny = null) : IRoutePolicy
    {
        public List<RouteRequest> Requests { get; } = [];

        public PolicyDecision Evaluate(RouteRequest request)
        {
            Requests.Add(request);
            return deny?.Invoke(request) is { } reason
                ? PolicyDecision.Deny(DenialReason.DataClass, reason)
                : PolicyDecision.Allow();
        }
    }
}
