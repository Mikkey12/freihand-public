using System.Text.Json.Nodes;
using Freihand.Core.Profiles;
using Freihand.Core.Routing;

namespace Freihand.Core.Tests.Integration;

/// <summary>
/// Importierte Profile (P2) gegen die Richtlinie (P1). Prüftests von Codex aus der ersten Nachprüfung
/// (N4), mit unveränderten Erwartungen übernommen.
/// </summary>
public sealed class ProfilePolicyIntegrationTests
{
    [Theory]
    [InlineData("oeffentlich", 0)]
    [InlineData("intern", 1)]
    [InlineData("vertraulich", 2)]
    public void ImportedStandardClassesStillWork(string id, int rank)
    {
        var root = JsonNode.Parse(BuiltInProfiles.Json)!;
        root["profiles"]![0]!["dataClass"] = id;
        var result = ProfileSerializer.Import(root.ToJsonString(), EndpointAllowList.None);
        Assert.True(result.Succeeded);
        var dataClass = result.Value[0].DataClass;
        Assert.Equal(rank, dataClass.Rank);
        var privatePolicy = new RoutePolicy(new(Edition.Private));
        var businessPolicy = new RoutePolicy(new(Edition.Business));
        var request = new RouteRequest(dataClass, RecognitionRoute.Device, RouteEndpoint.ThisDevice, TransformRoute.None, RouteEndpoint.ThisDevice);
        Assert.True(privatePolicy.Evaluate(request).Allowed);
        Assert.True(businessPolicy.Evaluate(request).Allowed);
        var online = request with { Recognition = RecognitionRoute.Online, RecognitionEndpoint = new(EndpointKind.Provider, "https://provider.example") };
        Assert.Equal(id == "oeffentlich", privatePolicy.Evaluate(online).Allowed);
        var home = request with { Recognition = RecognitionRoute.Home, RecognitionEndpoint = new(EndpointKind.Home, "https://home.example") };
        Assert.Equal(id != "vertraulich", privatePolicy.Evaluate(home).Allowed);
    }

    [Fact]
    public void ImportedClassResolvesToItRedefinedRankWithoutRejectingLocalRoute()
    {
        var result = ProfileSerializer.Import(BuiltInProfiles.Json, EndpointAllowList.None);
        Assert.True(result.Succeeded);
        var dataClass = result.Value[0].DataClass;
        var policy = new RoutePolicy(new(Edition.Business) { It = new() { DataClasses = [new(dataClass.Id, 10)] } });
        var decision = policy.Evaluate(new(dataClass, RecognitionRoute.Device, RouteEndpoint.ThisDevice, TransformRoute.None, RouteEndpoint.ThisDevice));
        Assert.True(decision.Allowed, $"Klasse {dataClass.Id}, Profilrang {dataClass.Rank}, IT-Rang 10: {decision.Denial}");
    }
}
