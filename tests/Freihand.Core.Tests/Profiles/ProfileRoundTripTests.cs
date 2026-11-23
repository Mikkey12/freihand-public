using Freihand.Core.Profiles;
using Freihand.Core.Routing;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>Hin- und Rückweg ohne Verlust: Export und erneuter Import ergeben dieselben Profile (OF-01).</summary>
public class ProfileRoundTripTests
{
    [Fact]
    public void BuiltInProfilesExportByteForByte()
    {
        Assert.Equal(BuiltInProfiles.Json + "\n", ProfileSerializer.Export(BuiltInProfiles.All));
    }

    [Fact]
    public void FullProfileSurvivesExportAndImport()
    {
        var original = ProfileSerializer.Import(FullDocument, AllowList).Value!;

        var exported = ProfileSerializer.Export(original);
        var reimported = ProfileSerializer.Import(exported, AllowList);

        Assert.True(reimported.Succeeded, string.Join("\n", reimported.Errors));
        Assert.Equal(original, reimported.Value);
        Assert.Equal(FullDocumentV2 + "\n", exported);
    }

    [Fact]
    public void ProfileBuiltInCodeSurvivesExportAndImport()
    {
        var profile = new Profile(
            "heim-mail",
            "Heim – Mail für Müller & Söhne",
            Description: null,
            new RecognitionStep(RecognitionRoute.Home, new Uri("https://sprichblitz.example.com/api")),
            new TransformStep(TransformRoute.PrivateProvider, new Uri("https://llm.example.com/v1/chat")),
            DataClass.Internal,
            "gsw-CH",
            RecordingMode.Toggle,
            AudioSourceSelection.SystemDefault,
            OutputMode.DictationWindow,
            "ch-mundart",
            ProfileStrictness.Strict);

        var result = ProfileSerializer.Import(ProfileSerializer.Export([profile]), AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(profile, Assert.Single(result.Value));
    }

    [Fact]
    public void ExportedDocumentsConformToTheJsonSchema()
    {
        var validator = new MiniSchemaValidator(RepositoryFiles.ReadSchema("profiles.v2.schema.json"));
        var profiles = ProfileSerializer.Import(FullDocument, AllowList).Value!;

        Assert.Empty(validator.Validate(System.Text.Json.Nodes.JsonNode.Parse(ProfileSerializer.Export(profiles))));
    }

    [Theory]
    [InlineData("Schlüssel sk-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("token=abc")]
    [InlineData("<script>")]
    public void ExportRefusesSecretsAndCode(string description)
    {
        // OF-01: Auch ein im Code gebautes Profil gelangt mit Geheimnis oder Skript nie in einen Export.
        var profile = BuiltInProfiles.Standard with { Id = "eigenes", Description = description };

        Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([profile]));
    }

    [Fact]
    public void ExportRefusesSecretsInIdentifiers()
    {
        // Codex-Review R7: Bezeichner werden wie freier Text auf Geheimnisse geprüft, auch im Export (OF-01).
        var withId = BuiltInProfiles.Standard with { Id = ProfileImportTests.FakeKey };
        var withFormatRules = BuiltInProfiles.Standard with { Id = "eigenes", FormatRules = ProfileImportTests.FakeKey };

        var idError = Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([withId]));
        var formatRulesError = Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([withFormatRules]));

        Assert.DoesNotContain(ProfileImportTests.FakeKey, idError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ProfileImportTests.FakeKey, formatRulesError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ProfileImportTests.OrdinaryIdentifiers), MemberType = typeof(ProfileImportTests))]
    public void ExportAcceptsOrdinaryIdentifiers(string identifier)
    {
        // Codex-Nachprüfung N2: „task-…“ und „desk-…“ sind keine Schlüssel.
        var profile = BuiltInProfiles.Standard with { Id = identifier, FormatRules = identifier };

        var result = ProfileSerializer.Import(ProfileSerializer.Export([profile]), AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(profile, Assert.Single(result.Value));
    }

    [Fact]
    public void ExportRefusesEndpointWithCredentials()
    {
        var profile = BuiltInProfiles.Standard with
        {
            Id = "eigenes",
            Recognition = new RecognitionStep(RecognitionRoute.Online, new Uri("https://user:pass@stt.example.com/v1")),
        };

        Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([profile]));
    }
}
