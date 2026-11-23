using System.Text.Json.Nodes;
using Freihand.Core.Profiles;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Das JSON-Schema der Version 1 unter <c>schemas/</c> bleibt unverändert und gilt weiter für Dokumente der
/// Version 1 (OF-01). Die Prüfungen sind die bisherigen; Version 2 steht in <see cref="ProfileSchemaTests"/>.
/// </summary>
public class ProfileSchemaVersion1Tests
{
    private static readonly JsonObject Schema = RepositoryFiles.ReadSchema("profiles.v1.schema.json");

    public static TheoryData<string> SchemaRejectedCases =>
        [.. ProfileImportTests.Invalid.Where(entry => entry.Value.SchemaRejects).Select(entry => entry.Key)];

    [Fact]
    public void IdentifiesKindAndVersion()
    {
        Assert.Equal(ProfileSchema.SchemaIdVersion1, Schema["$id"]!.GetValue<string>());
        Assert.Equal(ProfileSchema.Kind, Schema["properties"]!["kind"]!["const"]!.GetValue<string>());
        Assert.Equal(ProfileSchema.OldestReadableVersion, Schema["properties"]!["schemaVersion"]!["const"]!.GetValue<int>());
    }

    [Fact]
    public void EnumerationsMatchTheCode()
    {
        var defs = Schema["$defs"]!;
        var profile = defs["profile"]!["properties"]!;

        Assert.Equal(ProfileSchema.RecognitionRoutes.Names, Names(defs["recognition"]!["properties"]!["route"]));
        Assert.Equal(ProfileSchema.TransformRoutes.Names, Names(defs["transform"]!["properties"]!["route"]));
        Assert.Equal(ProfileSchema.AudioSourceKinds.Names, Names(defs["audioSource"]!["properties"]!["kind"]));
        Assert.Equal(ProfileSchema.RecordingModes.Names, Names(profile["recordingMode"]));
        Assert.Equal(ProfileSchema.OutputModes.Names, Names(profile["output"]));
        Assert.Equal(ProfileSchema.Strictness.Names, Names(profile["strictness"]));
        Assert.Equal(ProfileSchema.DataClasses.Select(dataClass => dataClass.Id), Names(profile["dataClass"]));
        Assert.Equal(ProfileSchema.LanguagePattern, profile["language"]!["pattern"]!.GetValue<string>());
        Assert.Equal(ProfileSchema.MaxProfiles, Schema["properties"]!["profiles"]!["maxItems"]!.GetValue<int>());
    }

    [Fact]
    public void AcceptsTheFullSample()
    {
        Assert.Empty(new MiniSchemaValidator(Schema).Validate(JsonNode.Parse(ProfileSamples.FullDocument)));
    }

    [Theory]
    [MemberData(nameof(SchemaRejectedCases))]
    public void RejectsWhatTheSchemaCanExpress(string name)
    {
        var node = JsonNode.Parse(ProfileImportTests.Invalid[name].Json);

        Assert.False(new MiniSchemaValidator(Schema).IsValid(node));
    }

    [Theory]
    [InlineData("recognition-mode")]
    [InlineData("vocabulary")]
    [InlineData("programs")]
    [InlineData("transform-style")]
    public void S13_Version1SchemaRejectsEveryVersion2Field(string name)
    {
        // S05: Das Schema der Version 1 kennt die neuen Felder nicht.
        var node = JsonNode.Parse(ProfileV2Cases.Version1WithNewFields[name].Json);

        Assert.False(new MiniSchemaValidator(Schema).IsValid(node));
    }

    [Fact]
    public void S13_Version1SchemaFileIsUnchanged()
    {
        // SHA-256 der Datei mit Zeilenende LF im Stand von main vor dem Nachtrag P2.
        const string expected = "a364c88386da1558d204f9cb37f6021fd69168f42f6ef7a40d906d414869ccd2";
        var text = RepositoryFiles.Read("schemas", "profiles.v1.schema.json").Replace("\r\n", "\n", StringComparison.Ordinal);

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

        Assert.Equal(expected, hash, ignoreCase: true);
    }

    private static IEnumerable<string> Names(JsonNode? enumSchema) =>
        enumSchema!["enum"]!.AsArray().Select(node => node!.GetValue<string>());
}
