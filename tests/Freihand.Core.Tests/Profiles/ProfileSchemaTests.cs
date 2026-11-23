using System.Text.Json.Nodes;
using Freihand.Core.Profiles;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Das JSON-Schema der Version 2 unter <c>schemas/</c> und die Prüfung im Code bleiben deckungsgleich (OF-01,
/// Nachtrag P2, S13). Das Schema der Version 1 prüft <see cref="ProfileSchemaVersion1Tests"/>.
/// </summary>
public class ProfileSchemaTests
{
    private static readonly JsonObject Schema = RepositoryFiles.ReadSchema("profiles.v2.schema.json");

    /// <summary>
    /// Alle Fälle, die das Schema ausdrücken kann: die Fälle aus <c>ProfileImportTests.Invalid</c> als Dokument
    /// der Version 2 (S14) und die Fälle aus S06, S07, S09 und S10. S05 prüft das Schema der Version 1.
    /// </summary>
    private static readonly Dictionary<string, string> SchemaRejected = BuildSchemaRejected();

    public static TheoryData<string> SchemaRejectedCases => [.. SchemaRejected.Keys];

    private static Dictionary<string, string> BuildSchemaRejected()
    {
        var cases = new Dictionary<string, string>();
        foreach (var (name, invalid) in ProfileImportTests.Invalid)
        {
            if (invalid.SchemaRejects && invalid.JsonV2 is not null)
            {
                cases["s14/" + name] = invalid.JsonV2;
            }
        }

        foreach (var (name, invalid) in ProfileV2Cases.SchemaRejectedVersion2)
        {
            cases[name] = invalid.Json;
        }

        return cases;
    }

    [Fact]
    public void S13_IdentifiesKindAndVersion()
    {
        Assert.Equal(ProfileSchema.SchemaId, Schema["$id"]!.GetValue<string>());
        Assert.Equal(ProfileSchema.Kind, Schema["properties"]!["kind"]!["const"]!.GetValue<string>());
        Assert.Equal(ProfileSchema.CurrentVersion, Schema["properties"]!["schemaVersion"]!["const"]!.GetValue<int>());
        Assert.Equal(2, ProfileSchema.CurrentVersion);
        Assert.Equal(1, ProfileSchema.OldestReadableVersion);
        Assert.Equal("urn:freihand:schema:profiles:2", ProfileSchema.SchemaId);
        Assert.Equal("urn:freihand:schema:profiles:1", ProfileSchema.SchemaIdVersion1);
    }

    [Fact]
    public void S13_EnumerationsAndLimitsMatchTheCode()
    {
        var defs = Schema["$defs"]!;
        var profile = defs["profile"]!["properties"]!;
        var transform = defs["transform"]!["properties"]!;

        // die bisherigen Abgleiche
        Assert.Equal(ProfileSchema.RecognitionRoutes.Names, Names(defs["recognition"]!["properties"]!["route"]));
        Assert.Equal(ProfileSchema.TransformRoutes.Names, Names(transform["route"]));
        Assert.Equal(ProfileSchema.AudioSourceKinds.Names, Names(defs["audioSource"]!["properties"]!["kind"]));
        Assert.Equal(ProfileSchema.RecordingModes.Names, Names(profile["recordingMode"]));
        Assert.Equal(ProfileSchema.OutputModes.Names, Names(profile["output"]));
        Assert.Equal(ProfileSchema.Strictness.Names, Names(profile["strictness"]));
        Assert.Equal(ProfileSchema.DataClasses.Select(dataClass => dataClass.Id), Names(profile["dataClass"]));
        Assert.Equal(ProfileSchema.LanguagePattern, profile["language"]!["pattern"]!.GetValue<string>());
        Assert.Equal(ProfileSchema.MaxProfiles, Schema["properties"]!["profiles"]!["maxItems"]!.GetValue<int>());

        // neu in Version 2
        Assert.Equal(ProfileSchema.RecognitionModes.Names, Names(profile["recognitionMode"]));
        Assert.Equal(ProfileSchema.TransformStyles.Names, Names(transform["style"]));
        Assert.Equal(["live", "sentence"], ProfileSchema.RecognitionModes.Names);
        Assert.Equal(["mail", "polite", "summary"], ProfileSchema.TransformStyles.Names);

        var vocabulary = profile["vocabulary"]!;
        Assert.Equal(1, vocabulary["minItems"]!.GetValue<int>());
        Assert.Equal(ProfileSchema.MaxVocabularyEntries, vocabulary["maxItems"]!.GetValue<int>());
        Assert.Equal(ProfileSchema.MaxVocabularyEntryLength, MaxLength(vocabulary["items"]!));

        var programs = profile["programs"]!;
        Assert.Equal(1, programs["minItems"]!.GetValue<int>());
        Assert.Equal(ProfileSchema.MaxPrograms, programs["maxItems"]!.GetValue<int>());
        Assert.Equal(ProfileSchema.MaxProgramLength, programs["items"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(ProfileSchema.ProgramPattern, programs["items"]!["pattern"]!.GetValue<string>());

        Assert.Contains("recognitionMode", defs["profile"]!["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.DoesNotContain("vocabulary", defs["profile"]!["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.DoesNotContain("programs", defs["profile"]!["required"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void S13_AcceptsTheVersion2Samples()
    {
        var validator = new MiniSchemaValidator(Schema);
        var fromVersion1 = ProfileSerializer.Import(ProfileSamples.FullDocument, ProfileSamples.AllowList).Value!;
        var extended = ProfileSerializer.Import(ProfileSamples.ExtendedDocumentV2, ProfileSamples.AllowList).Value!;

        Assert.Empty(validator.Validate(JsonNode.Parse(ProfileSamples.FullDocumentV2)));
        Assert.Empty(validator.Validate(JsonNode.Parse(ProfileSamples.ExtendedDocumentV2)));
        Assert.Empty(validator.Validate(JsonNode.Parse(BuiltInProfiles.Json)));
        Assert.Empty(validator.Validate(JsonNode.Parse(ProfileSerializer.Export(fromVersion1))));
        Assert.Empty(validator.Validate(JsonNode.Parse(ProfileSerializer.Export(extended))));
    }

    [Theory]
    [MemberData(nameof(SchemaRejectedCases))]
    public void S13_RejectsWhatTheSchemaCanExpress(string name)
    {
        var node = JsonNode.Parse(SchemaRejected[name]);

        Assert.False(new MiniSchemaValidator(Schema).IsValid(node));
    }

    private static IEnumerable<string> Names(JsonNode? enumSchema) =>
        enumSchema!["enum"]!.AsArray().Select(node => node!.GetValue<string>());

    /// <summary>Höchstlänge eines Eintrags, der als <c>allOf</c> aus Text und Länge beschrieben ist.</summary>
    private static int MaxLength(JsonNode itemSchema) =>
        itemSchema["allOf"]!.AsArray().Select(part => part!["maxLength"]).Single(length => length is not null)!.GetValue<int>();
}
