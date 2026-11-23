using System.Text.Json.Nodes;
using Freihand.Core.Commands;
using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Regressionstests (Beobachtung zu PR #25): Ein Prüfmuster mit <c>$</c> als Endanker lässt in .NET einen
/// Zeilenvorschub am Schluss zu (<c>"abc\n"</c>), das JSON-Schema (<c>$</c> nach ECMA 262) und die Dokumente
/// des Datenformats nicht. Import und Schema müssen gleich entscheiden: Alle Muster enden auf <c>\z</c>.
/// </summary>
public class TrailingLineEndTests
{
    public static TheoryData<string> LineEnds => ["\n", "\r\n"];

    [Theory]
    [MemberData(nameof(LineEnds))]
    public void ProfileIdWithALineEndIsRejected(string lineEnd) =>
        AssertRejected(WithProfileV2(p => p["id"] = "abc" + lineEnd), "$.profiles[0].id", "Kein gültiger Bezeichner");

    [Theory]
    [MemberData(nameof(LineEnds))]
    public void ProfileFormatRulesWithALineEndAreRejected(string lineEnd) =>
        AssertRejected(WithProfileV2(p => p["formatRules"] = "ch-standard" + lineEnd), "$.profiles[0].formatRules", "Kein gültiger Bezeichner");

    [Theory]
    [MemberData(nameof(LineEnds))]
    public void ProfileLanguageWithALineEndIsRejected(string lineEnd) =>
        AssertRejected(WithProfileV2(p => p["language"] = "de-CH" + lineEnd), "$.profiles[0].language", "Keine gültige Sprachkennung");

    [Theory]
    [MemberData(nameof(LineEnds))]
    public void ProfileFieldsOfVersion1WithALineEndAreRejected(string lineEnd)
    {
        AssertRejected(WithProfile(p => p["id"] = "abc" + lineEnd), "$.profiles[0].id", "Kein gültiger Bezeichner");
        AssertRejected(WithProfile(p => p["language"] = "de-CH" + lineEnd), "$.profiles[0].language", "Keine gültige Sprachkennung");
    }

    [Theory]
    [MemberData(nameof(LineEnds))]
    public void ProgramWithALineEndIsRejected(string lineEnd) =>
        AssertRejected(
            WithProfileV2(p => p["programs"] = new JsonArray("olk.exe" + lineEnd)),
            "$.profiles[0].programs[0]",
            "Kein gültiger Programmname");

    [Theory]
    [MemberData(nameof(LineEnds))]
    public void CommandIdWithALineEndIsRejected(string lineEnd)
    {
        var root = JsonNode.Parse(BuiltInCommands.Json)!;
        root["commands"]![0]!["id"] = "abc" + lineEnd;

        var result = CommandSetSerializer.Import(root.ToJsonString());

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors, error => error.Path == "$.commands[0].id");
        Assert.Contains("Kein gültiger Bezeichner", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEnds))]
    public void CommandPhraseWithALineEndIsRejected(string lineEnd)
    {
        var root = JsonNode.Parse(BuiltInCommands.Json)!;
        root["commands"]![0]!["phrases"] = new JsonArray("Komma" + lineEnd);

        var result = CommandSetSerializer.Import(root.ToJsonString());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == "$.commands[0].phrases[0]");
    }

    [Theory]
    [MemberData(nameof(LineEnds))]
    public void LiveApprovalsRejectFieldsWithALineEnd(string lineEnd)
    {
        Assert.Throws<ArgumentException>(() => new LiveApprovals([Entry(device: "reference-desktop" + lineEnd)]));
        Assert.Throws<ArgumentException>(() => new LiveApprovals([Entry(language: "de-CH" + lineEnd)]));
        Assert.Throws<ArgumentException>(() => new LiveApprovals([Entry(model: ApprovedModel + lineEnd)]));
        Assert.Throws<ArgumentException>(() => new LiveApprovals([Entry(runtime: "cuda" + lineEnd)]));
    }

    [Theory]
    [MemberData(nameof(LineEnds))]
    public void SchemasRejectTheSameValuesWithALineEnd(string lineEnd)
    {
        // Codex-Prüfung PR #25, V02: Der Schema-Prüfer wertet „$“ wie JSON Schema (ECMA 262) aus.
        var version1 = new MiniSchemaValidator(RepositoryFiles.ReadSchema("profiles.v1.schema.json"));
        var version2 = new MiniSchemaValidator(RepositoryFiles.ReadSchema("profiles.v2.schema.json"));
        var commands = new MiniSchemaValidator(RepositoryFiles.ReadSchema("commands.v1.schema.json"));

        foreach (var change in new Action<JsonObject>[]
        {
            p => p["id"] = "abc" + lineEnd,
            p => p["language"] = "de-CH" + lineEnd,
            p => p["formatRules"] = "ch-standard" + lineEnd,
        })
        {
            Assert.NotEmpty(version1.Validate(JsonNode.Parse(WithProfile(change))));
            Assert.NotEmpty(version2.Validate(JsonNode.Parse(WithProfileV2(change))));
        }

        Assert.NotEmpty(version2.Validate(JsonNode.Parse(WithProfileV2(p => p["programs"] = new JsonArray("olk.exe" + lineEnd)))));

        var commandId = JsonNode.Parse(BuiltInCommands.Json)!;
        commandId["commands"]![0]!["id"] = "abc" + lineEnd;
        Assert.NotEmpty(commands.Validate(commandId));

        var commandPhrase = JsonNode.Parse(BuiltInCommands.Json)!;
        commandPhrase["commands"]![0]!["phrases"] = new JsonArray("Komma" + lineEnd);
        Assert.NotEmpty(commands.Validate(commandPhrase));
    }

    private static LiveApproval Entry(
        string device = KnownDevices.ReferenceDesktop,
        string language = "de-CH",
        string model = ApprovedModel,
        string runtime = KnownRuntimes.Cuda) =>
        new(device, language, model, runtime);

    private static void AssertRejected(string json, string path, string message)
    {
        var result = ProfileSerializer.Import(json, AllowList);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors, error => error.Path == path);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }
}
