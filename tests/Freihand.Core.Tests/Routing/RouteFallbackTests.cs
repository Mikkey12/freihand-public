using Freihand.Core.Routing;
using static Freihand.Core.Tests.Routing.RouteTestData;

namespace Freihand.Core.Tests.Routing;

/// <summary>Ausweichweg nur, wenn die Richtlinie ihn erlaubt; nie als Umleitung einer verweigerten Route (WE-10).</summary>
public class RouteFallbackTests
{
    private static RouteFallback Fallback(Edition edition = Edition.Private, LocalOnlySwitch? localOnly = null) =>
        new(new RoutePolicy(new RoutePolicyOptions(edition), localOnly));

    [Fact]
    public void PicksTheFirstAllowedCandidateInProfileOrder()
    {
        var failed = Request(DataClass.Public, RecognitionRoute.Online, TransformRoute.None);
        var home = Request(DataClass.Public, RecognitionRoute.Home, TransformRoute.None);
        var device = Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.None);

        var result = Fallback().Select(failed, [home, device]);

        Assert.True(result.Found);
        Assert.Equal(home, result.Route);
        Assert.Equal(failed, result.Failed);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void SkipsCandidatesTheDataClassForbids()
    {
        var failed = Request(DataClass.Internal, RecognitionRoute.Home, TransformRoute.None);
        var online = Request(DataClass.Internal, RecognitionRoute.Online, TransformRoute.None);
        var device = Request(DataClass.Internal, RecognitionRoute.Device, TransformRoute.None);

        var result = Fallback().Select(failed, [online, device]);

        Assert.Equal(device, result.Route);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(DenialReason.DataClass, rejected.Decision.Denial);
    }

    [Fact]
    public void CandidateKeepsTheHighestDataClass()
    {
        // WE-03: Ein Ausweichweg mit tieferer Einstufung im Profil gilt trotzdem als „vertraulich“
        var failed = Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.Local);
        var online = Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.PrivateProvider);
        var plain = Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.None);

        var result = Fallback().Select(failed, [online, plain]);

        Assert.Equal(plain with { DataClass = DataClass.Confidential }, result.Route);
        Assert.Equal(DataClass.Confidential, Assert.Single(result.Rejected).Route.DataClass);
    }

    [Fact]
    public void ForgedCandidateRankCannotLowerTheDataClass()
    {
        // Codex-Nachprüfung N4: Der mitgelieferte Rang zählt nicht; ein Kandidat „öffentlich“ mit Rang 99 darf
        // „vertraulich“ nicht auf „öffentlich“ herabsetzen (WE-03)
        var failed = Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.Local);
        var forged = Request(new DataClass(DataClass.Public.Id, 99), RecognitionRoute.Device, TransformRoute.PrivateProvider);

        var result = Fallback().Select(failed, [forged]);

        Assert.False(result.Found);
        Assert.Equal(DenialReason.DataClass, Assert.Single(result.Rejected).Decision.Denial);
    }

    [Fact]
    public void CandidateWithStricterDataClassMustAlsoAllowTheRoute()
    {
        var failed = Request(DataClass.Internal, RecognitionRoute.Device, TransformRoute.Local);
        var home = Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.Home);

        var result = Fallback().Select(failed, [home]);

        Assert.False(result.Found);
    }

    [Fact]
    public void ResultCarriesTheStricterDefinedClassForTheNextFailure()
    {
        // Codex-Nachprüfung 2, F4: Das Ergebnis trägt die strengere Klasse samt definiertem Rang, damit ein weiterer
        // Ausweichvorgang nicht wieder mit der tieferen Klasse rechnet (WE-03)
        var confidentialByIt = new DataClass(DataClass.Confidential.Id, 20);
        var fallback = new RouteFallback(new RoutePolicy(new RoutePolicyOptions(Edition.Private)
        {
            It = new ItDirectives { DataClasses = [new(DataClass.Public.Id, 5), confidentialByIt] },
        }));
        var home = Request(DataClass.Public, RecognitionRoute.Home, TransformRoute.None);
        var device = Request(new DataClass(DataClass.Confidential.Id, 0), RecognitionRoute.Device, TransformRoute.None);

        var first = fallback.Select(home, [device]);
        var second = fallback.Select(first.Route!, [home]);

        Assert.Equal(confidentialByIt, first.Route!.DataClass);
        Assert.False(second.Found);
        Assert.Equal(DenialReason.DataClass, Assert.Single(second.Rejected).Decision.Denial);
    }

    [Fact]
    public void DeniedRouteIsNeverRerouted()
    {
        var denied = Request(DataClass.Confidential, RecognitionRoute.Online, TransformRoute.None);
        var device = Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.None);

        var result = Fallback().Select(denied, [device]);

        Assert.False(result.Found);
        Assert.Equal(denied, Assert.Single(result.Rejected).Route);
    }

    [Fact]
    public void NoAllowedCandidateMeansNoFallback()
    {
        // WE-16: Fällt ein Weg aus, weicht die App nicht still aus
        var failed = Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.None);
        var companyService = Request(DataClass.Public, RecognitionRoute.CompanyService, TransformRoute.None);

        var result = Fallback(Edition.Business).Select(failed, [failed, companyService]);

        Assert.False(result.Found);
        Assert.Null(result.Route);
        Assert.Equal(DenialReason.ApprovalMissing, Assert.Single(result.Rejected).Decision.Denial);
    }

    [Fact]
    public void LocalOnlyLeavesOnlyDeviceFallbacks()
    {
        var localOnly = new LocalOnlySwitch();
        var fallback = Fallback(localOnly: localOnly);
        var failed = Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.Local);
        var home = Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.Home);
        var none = Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.None);

        localOnly.Engage();
        var result = fallback.Select(failed, [home, none]);

        Assert.Equal(none, result.Route);
        Assert.Equal(DenialReason.LocalOnly, Assert.Single(result.Rejected).Decision.Denial);
    }
}
