using System.Text.Json.Nodes;
using Freihand.Core.Commands;
using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Optional heisst weglassbar (OF-01, Codex-Prüfung PR #25, V01): Ein vorhandenes optionales Feld mit
/// <c>null</c> wird mit Feldpfad abgewiesen, wie es das JSON-Schema tut. Weglassen bleibt erlaubt.
/// </summary>
public class ExplicitNullTests
{
    /// <summary>Alle optionalen Felder von Version 1 und 2 mit ihrem Pfad unter dem Profil.</summary>
    public static TheoryData<string> OptionalFields =>
    [
        "description",
        "recognition.endpoint",
        "transform.endpoint",
        "audioSource.deviceId",
        "audioSource.displayName",
        "vocabulary",
        "programs",
        "transform.style",
    ];

    private static readonly string[] Version2Only = ["vocabulary", "programs", "transform.style"];

    private static void SetNull(JsonObject profile, string field)
    {
        var parts = field.Split('.');
        var target = parts.Length == 1 ? profile : profile[parts[0]]!.AsObject();
        target[parts[^1]] = null;
    }

    [Theory]
    [MemberData(nameof(OptionalFields))]
    public void ExplicitNullInVersion2IsRejectedLikeTheSchema(string field)
    {
        var json = WithProfileV2(p => SetNull(p, field));

        var schema = new MiniSchemaValidator(RepositoryFiles.ReadSchema("profiles.v2.schema.json"));
        var result = ProfileSerializer.Import(json, AllowList);

        Assert.NotEmpty(schema.Validate(JsonNode.Parse(json)));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == "$.profiles[0]." + field);
    }

    [Theory]
    [MemberData(nameof(OptionalFields))]
    public void ExplicitNullInVersion1IsRejectedLikeTheSchema(string field)
    {
        var json = WithProfile(p => SetNull(p, field));

        var schema = new MiniSchemaValidator(RepositoryFiles.ReadSchema("profiles.v1.schema.json"));
        var result = ProfileSerializer.Import(json, AllowList);

        Assert.NotEmpty(schema.Validate(JsonNode.Parse(json)));
        Assert.False(result.Succeeded);

        // Felder ab Version 2 gibt es in Version 1 nicht; die anderen werden mit ihrem Pfad abgewiesen.
        var expected = Version2Only.Contains(field)
            ? (field == "transform.style" ? "$.profiles[0].transform" : "$.profiles[0]")
            : "$.profiles[0]." + field;
        Assert.Contains(result.Errors, error => error.Path == expected);
    }

    [Fact]
    public void OmittingTheOptionalFieldsStaysAllowed()
    {
        var json = WithProfileV2(p =>
        {
            p.Remove("description");
            p["recognition"]!.AsObject().Remove("endpoint");
            p["recognition"]!["route"] = "device";
            p["transform"] = new JsonObject { ["route"] = "none" };
            p["audioSource"] = new JsonObject { ["kind"] = "systemDefault" };
        });

        var result = ProfileSerializer.Import(json, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
    }

    [Fact]
    public void ExplicitNullForTheCommandTextIsRejectedLikeTheSchema()
    {
        var root = JsonNode.Parse(BuiltInCommands.Json)!;
        var command = root["commands"]!.AsArray().First(node => node!["effect"]!.GetValue<string>() != "insertPunctuation")!;
        var index = root["commands"]!.AsArray().IndexOf(command);
        command["text"] = null;

        var schema = new MiniSchemaValidator(RepositoryFiles.ReadSchema("commands.v1.schema.json"));
        var result = CommandSetSerializer.Import(root.ToJsonString());

        Assert.NotEmpty(schema.Validate(root));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == $"$.commands[{index}].text");
    }

    [Fact]
    public void ErrorMessagesForNullNameOnlyTheField()
    {
        var result = ProfileSerializer.Import(WithProfileV2(p => p["vocabulary"] = null), AllowList);

        var error = Assert.Single(result.Errors);
        Assert.Equal("$.profiles[0].vocabulary", error.Path);
        Assert.Contains("null", error.Message, StringComparison.Ordinal);
    }
}
