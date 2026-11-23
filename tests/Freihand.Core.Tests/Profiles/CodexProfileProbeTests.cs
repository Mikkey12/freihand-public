using System.Text.Json.Nodes;
using Freihand.Core.Commands;
using Freihand.Core.Profiles;
using Freihand.Core.Routing;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Prüftests von Codex für Profile (Branch <c>codex/pruefproben</c>, Paket P2), mit unveränderten
/// Erwartungen übernommen: Review R7, Nachprüfung N2, zweite Nachprüfung F2 (OF-01, SI-03).
/// </summary>
public sealed class CodexProfileProbeTests
{
    public static TheoryData<string> TokenExamples => new(
        new[] { "sk-" + new string('a', 24), "ghp_" + new string('a', 24), "AKIAABCDEFGHIJKLMNOP", "AIza" + new string('a', 32), "xoxb-1234567890-abcdef" });

    // Review R7
    [Fact]
    public void Review_ProfileRejectsKnownSecretInId()
    {
        var json = BuiltInProfiles.Json.Replace("\"standard\",", "\"sk-aaaaaaaaaaaaaaaaaaaaaaaa\",", StringComparison.Ordinal);
        Assert.False(ProfileSerializer.Import(json, EndpointAllowList.None).Succeeded);
    }

    // Review R7
    [Fact]
    public void Review_ProfileExportRejectsKnownSecretInId()
    {
        var profile = BuiltInProfiles.Standard with { Id = "sk-aaaaaaaaaaaaaaaaaaaaaaaa" };
        Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([profile]));
    }

    // Nachprüfung N2
    [Theory]
    [InlineData("standard")]
    [InlineData("ch-standard")]
    [InlineData("mail-intern-2026")]
    [InlineData("token-verwaltung")]
    [InlineData("task-besprechungsnotizen")]
    [InlineData("desk-besprechungsnotizen")]
    public void OrdinaryIdentifiersAreAccepted(string id)
    {
        var root = JsonNode.Parse(BuiltInProfiles.Json)!;
        root["profiles"]![0]!["id"] = id;
        root["profiles"]![0]!["formatRules"] = id;
        var imported = ProfileSerializer.Import(root.ToJsonString(), EndpointAllowList.None);
        Assert.True(imported.Succeeded, string.Join("; ", imported.Errors));
        var commands = JsonNode.Parse(BuiltInCommands.Json)!;
        commands["commands"]![0]!["id"] = id;
        Assert.True(CommandSetSerializer.Import(commands.ToJsonString()).Succeeded);
    }

    // Nachprüfung N2
    [Fact]
    public void OrdinaryTaskIdentifierCanBeExported()
    {
        var profile = BuiltInProfiles.Standard with { Id = "task-besprechungsnotizen" };
        Assert.NotEmpty(ProfileSerializer.Export([profile]));
    }

    // Nachprüfung N2, zweite Nachprüfung F2
    [Theory]
    [MemberData(nameof(TokenExamples))]
    public void TokensAtWordStartAfterPunctuationAndInAddressesAreRejected(string token)
    {
        foreach (var text in new[] { token, "(" + token + ")", "notiz-" + token, "https://example.test/" + token })
        {
            var root = JsonNode.Parse(BuiltInProfiles.Json)!;
            root["profiles"]![0]!["description"] = text;
            Assert.False(ProfileSerializer.Import(root.ToJsonString(), EndpointAllowList.None).Succeeded);
        }

        Assert.False(ImportEndpoint("https://example.test/" + token).Succeeded);
    }

    // Zweite Nachprüfung F2
    [Theory]
    [InlineData("/")]
    [InlineData("/%2F")]
    [InlineData("/%28")]
    public void EndpointEncodingCannotHideTokenBoundary(string separator)
    {
        var result = ImportEndpoint("https://example.test" + separator + "sk-" + new string('a', 24));
        Assert.False(result.Succeeded, "Ein erfundener Schlüssel im Endpunkt wurde importiert.");
    }

    // Zweite Nachprüfung F2
    [Fact]
    public void ExportRejectsTokenAfterEncodedAddressSeparator()
    {
        var profile = BuiltInProfiles.Standard with
        {
            Id = "eigenes",
            Recognition = new(RecognitionRoute.Online, new Uri("https://example.test/%2Fsk-" + new string('a', 24))),
        };
        Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([profile]));
    }

    private static ImportResult<IReadOnlyList<Profile>> ImportEndpoint(string endpoint)
    {
        var root = JsonNode.Parse(BuiltInProfiles.Json)!;
        root["profiles"]![0]!["dataClass"] = "oeffentlich";
        root["profiles"]![0]!["recognition"] = new JsonObject { ["route"] = "online", ["endpoint"] = endpoint };
        return ProfileSerializer.Import(root.ToJsonString(), new([new Uri("https://example.test/")]));
    }
}
