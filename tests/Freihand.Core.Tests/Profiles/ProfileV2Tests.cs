using System.Text.Json.Nodes;
using Freihand.Core.Profiles;
using Freihand.Core.Routing;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Profilschema Version 2 (Nachtrag P2): Lesen, Migration von
/// Version 1, Export ohne Verlust, Vokabular, Umformstil und Programme (OF-01, ER-20, BA-02, BA-03, BA-10).
/// </summary>
public class ProfileV2Tests
{
    public static TheoryData<string> Version1WithNewFieldsCases => [.. ProfileV2Cases.Version1WithNewFields.Keys];

    public static TheoryData<string> VersionAndModeCases => [.. ProfileV2Cases.VersionAndMode.Keys];

    public static TheoryData<string> VocabularyCases => [.. ProfileV2Cases.Vocabulary.Keys];

    public static TheoryData<string> StyleCases => [.. ProfileV2Cases.Style.Keys];

    public static TheoryData<string> ProgramsCases => [.. ProfileV2Cases.Programs.Keys];

    [Fact]
    public void S01_Version1DocumentIsReadWithDefaults()
    {
        var result = ProfileSerializer.Import(FullDocument, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var profile = Assert.Single(result.Value);

        // alle bisherigen Angaben wie in ProfileImportTests.FullSampleIsValid
        Assert.Equal("mail-online", profile.Id);
        Assert.Equal(new RecognitionStep(RecognitionRoute.Online, new Uri("https://stt.example.com/v1/listen")), profile.Recognition);
        Assert.Equal(new TransformStep(TransformRoute.Local, new Uri("http://127.0.0.1:1234/v1")), profile.Transform);
        Assert.Equal(DataClass.Public, profile.DataClass);
        Assert.Equal(RecordingMode.Hold, profile.RecordingMode);
        Assert.Equal(AudioSourceSelection.Device("{0.0.1.00000000}.{6f1c2a3b-0000-4000-8000-000000000001}", "Headset"), profile.AudioSource);
        Assert.Equal(OutputMode.Clipboard, profile.Output);
        Assert.False(profile.IsExact);
        Assert.False(profile.RejectsUnknownFields);

        // neu: die Vorgaben der Migration (P3-Konzept, Entscheid 4)
        Assert.Equal(RecognitionMode.Live, profile.RecognitionMode);
        Assert.Empty(profile.Vocabulary);
        Assert.Empty(profile.Programs);
        Assert.Null(profile.Transform.Style);
    }

    [Fact]
    public void S02_Version1DocumentExportsAsVersion2WithoutLoss()
    {
        var profiles = ProfileSerializer.Import(FullDocument, AllowList).Value!;

        var exported = ProfileSerializer.Export(profiles);
        var reimported = ProfileSerializer.Import(exported, AllowList);

        Assert.Equal(FullDocumentV2 + "\n", exported);
        Assert.True(reimported.Succeeded, string.Join("\n", reimported.Errors));
        Assert.Equal(profiles, reimported.Value);
    }

    [Fact]
    public void S03_ExtendedDocumentVersion2IsReadAndExportedByteForByte()
    {
        var result = ProfileSerializer.Import(ExtendedDocumentV2, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var profile = Assert.Single(result.Value);
        Assert.Equal(RecognitionMode.Sentence, profile.RecognitionMode);
        Assert.Equal(TransformStyle.Mail, profile.Transform.Style);
        Assert.Equal(["Gerber", "Freihand", "Müller-Thurgau AG"], profile.Vocabulary);
        Assert.Equal(["outlook.exe", "olk.exe"], profile.Programs);

        var exported = ProfileSerializer.Export(result.Value);
        Assert.Equal(ExtendedDocumentV2 + "\n", exported);

        var reimported = ProfileSerializer.Import(exported, AllowList);
        Assert.True(reimported.Succeeded, string.Join("\n", reimported.Errors));
        Assert.Equal(result.Value, reimported.Value);
    }

    [Fact]
    public void S04_BuiltInProfilesAreStoredAsVersion2AndStayTheSame()
    {
        var document = JsonNode.Parse(BuiltInProfiles.Json)!;

        Assert.Equal(2, document["schemaVersion"]!.GetValue<int>());
        foreach (var profile in document["profiles"]!.AsArray())
        {
            Assert.Equal("live", profile!["recognitionMode"]!.GetValue<string>());
            Assert.False(profile.AsObject().ContainsKey("vocabulary"));
            Assert.False(profile.AsObject().ContainsKey("programs"));
            Assert.False(profile["transform"]!.AsObject().ContainsKey("style"));
        }

        var fromVersion1 = ProfileSerializer.Import(BuiltInJsonVersion1, EndpointAllowList.None);

        Assert.True(fromVersion1.Succeeded, string.Join("\n", fromVersion1.Errors));
        Assert.Equal(BuiltInProfiles.All, fromVersion1.Value);
    }

    [Theory]
    [MemberData(nameof(Version1WithNewFieldsCases))]
    public void S05_Version1WithAVersion2FieldIsRejected(string name)
    {
        var invalid = ProfileV2Cases.Version1WithNewFields[name];
        var catalog = new ProfileCatalog();

        var result = catalog.Import(invalid.Json, AllowList);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path.StartsWith(invalid.Path, StringComparison.Ordinal));
        Assert.Equal(BuiltInProfiles.All, catalog.Profiles);
    }

    [Theory]
    [MemberData(nameof(VersionAndModeCases))]
    public void S06_VersionAndRecognitionModeAreChecked(string name)
    {
        var invalid = ProfileV2Cases.VersionAndMode[name];

        var result = ProfileSerializer.Import(invalid.Json, AllowList);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path.StartsWith(invalid.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void S06_MessagesNameTheVersionAndTheAllowedModesButNotTheRejectedValue()
    {
        var newer = ProfileSerializer.Import(ProfileV2Cases.VersionAndMode["version-3"].Json, AllowList);
        var unknownMode = ProfileSerializer.Import(ProfileV2Cases.VersionAndMode["mode-auto"].Json, AllowList);

        var versionError = Assert.Single(newer.Errors);
        Assert.Equal("$.schemaVersion", versionError.Path);
        Assert.Contains("2", versionError.Message, StringComparison.Ordinal);

        var modeError = Assert.Single(unknownMode.Errors);
        Assert.Equal("$.profiles[0].recognitionMode", modeError.Path);
        Assert.Contains("live, sentence", modeError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("auto", modeError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(VocabularyCases))]
    public void S07_InvalidVocabularyIsRejected(string name)
    {
        var invalid = ProfileV2Cases.Vocabulary[name];

        var result = ProfileSerializer.Import(invalid.Json, AllowList);

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, error => error.Path.StartsWith(invalid.Path, StringComparison.Ordinal));
        foreach (var error in result.Errors)
        {
            Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Gerber", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("script", error.Message, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string> AcceptedVocabularies => new()
    {
        "Gerber|gerber",
        "Frau Gerber",
        "Müller-Thurgau AG",
        new string('a', ProfileSchema.MaxVocabularyEntryLength),
        string.Join('|', Enumerable.Range(0, ProfileSchema.MaxVocabularyEntries).Select(i => "wort" + i)),
    };

    [Theory]
    [MemberData(nameof(AcceptedVocabularies))]
    public void S08_ValidVocabularyIsAcceptedAndSurvivesExportAndImport(string entries)
    {
        // Die Einträge stehen mit „|“ getrennt, damit ein Fall in einer Zeile stehen kann.
        string[] vocabulary = [.. entries.Split('|')];
        var json = WithProfileV2(p => p["vocabulary"] = new JsonArray([.. vocabulary.Select(entry => (JsonNode?)JsonValue.Create(entry))]));

        var result = ProfileSerializer.Import(json, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var profile = Assert.Single(result.Value);
        Assert.Equal(vocabulary, profile.Vocabulary);

        var exported = ProfileSerializer.Export(result.Value);
        var reimported = ProfileSerializer.Import(exported, AllowList);
        Assert.True(reimported.Succeeded, string.Join("\n", reimported.Errors));
        Assert.Equal(result.Value, reimported.Value);
        Assert.Equal(vocabulary, Assert.Single(reimported.Value).Vocabulary);
    }

    [Theory]
    [MemberData(nameof(StyleCases))]
    public void S09_InvalidStyleIsRejected(string name)
    {
        var invalid = ProfileV2Cases.Style[name];

        var result = ProfileSerializer.Import(invalid.Json, AllowList);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path.StartsWith(invalid.Path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("polite", TransformStyle.Polite)]
    [InlineData("summary", TransformStyle.Summary)]
    public void S09_PoliteAndSummaryAreAcceptedWithATransform(string wire, TransformStyle expected)
    {
        var result = ProfileSerializer.Import(WithProfileV2(p => p["transform"]!["style"] = wire), AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(expected, Assert.Single(result.Value).Transform.Style);
    }

    [Fact]
    public void S09_AStyleIsOptionalWithATransformRoute()
    {
        var json = WithProfileV2(p => p["transform"] = new JsonObject
        {
            ["route"] = "privateProvider",
            ["endpoint"] = "https://llm.example.com/v1",
        });

        var result = ProfileSerializer.Import(json, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var transform = Assert.Single(result.Value).Transform;
        Assert.Equal(TransformRoute.PrivateProvider, transform.Route);
        Assert.Null(transform.Style);
    }

    [Theory]
    [MemberData(nameof(ProgramsCases))]
    public void S10_InvalidProgramsAreRejected(string name)
    {
        var invalid = ProfileV2Cases.Programs[name];

        var result = ProfileSerializer.Import(invalid.Json, AllowList);

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, error => error.Path.StartsWith(invalid.Path, StringComparison.Ordinal));
        foreach (var error in result.Errors)
        {
            Assert.DoesNotContain("outlook", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("olk", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("outlook.exe")]
    [InlineData("olk.exe")]
    [InlineData("ms-teams.exe")]
    [InlineData("winword.exe")]
    [InlineData("notepad++.exe")]
    public void S10_ValidProgramsAreAccepted(string program)
    {
        var result = ProfileSerializer.Import(WithProfileV2(p => p["programs"] = new JsonArray(program)), AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal([program], Assert.Single(result.Value).Programs);
    }

    [Fact]
    public void S19_RecognitionOptionsCarryLanguageAndVocabularyInOrder()
    {
        var extended = Assert.Single(ProfileSerializer.Import(ExtendedDocumentV2, AllowList).Value!);

        var options = extended.ToRecognitionOptions();

        Assert.Equal("de-CH", options.Language);
        Assert.Equal(["Gerber", "Freihand", "Müller-Thurgau AG"], options.VocabularyHints);
        Assert.Empty(BuiltInProfiles.Standard.ToRecognitionOptions().VocabularyHints);
        Assert.Equal("de-CH", BuiltInProfiles.Standard.ToRecognitionOptions().Language);
    }

    [Fact]
    public void S20_ProfilesWithEqualContentAreEqualEvenWithDifferentListObjects()
    {
        var first = BuiltInProfiles.Standard with
        {
            Vocabulary = new TermList(["a", "b"]),
            Programs = new TermList(["x.exe"]),
        };
        var second = BuiltInProfiles.Standard with
        {
            Vocabulary = new TermList(["a", "b"]),
            Programs = new TermList(["x.exe"]),
        };
        var reordered = first with { Vocabulary = new TermList(["b", "a"]) };

        Assert.NotSame(first.Vocabulary, second.Vocabulary);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(first.Vocabulary, second.Vocabulary);
        Assert.True(first.Vocabulary == second.Vocabulary);
        Assert.NotEqual(first, reordered);
        Assert.True(first.Vocabulary != reordered.Vocabulary);
    }

    [Fact]
    public void S20_TermListRejectsNullAndCopiesItsSource()
    {
        Assert.Throws<ArgumentNullException>(() => new TermList(["a", null!]));
        Assert.Throws<ArgumentNullException>(() => new TermList(null!));

        List<string> source = ["a"];
        var terms = new TermList(source);
        source.Add("b");
        source[0] = "c";

        Assert.Equal(["a"], terms);
        Assert.Empty(TermList.Empty);
    }

    [Fact]
    public void S21_ExportRefusesInvalidVocabularyWithoutRepeatingTheValue()
    {
        var profile = BuiltInProfiles.Standard with { Id = "eigenes", Vocabulary = new TermList([ProfileImportTests.FakeKey]) };

        var error = Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([profile]));

        Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S21_ExportRefusesAnInvalidProgramWithoutRepeatingTheValue()
    {
        var profile = BuiltInProfiles.Standard with { Id = "eigenes", Programs = new TermList(["OUTLOOK.EXE"]) };

        var error = Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([profile]));

        Assert.DoesNotContain("outlook", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void S21_ExportRefusesAStyleWithoutATransform()
    {
        var profile = BuiltInProfiles.Standard with
        {
            Id = "eigenes",
            Transform = new TransformStep(TransformRoute.None, null, TransformStyle.Mail),
        };

        var error = Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([profile]));

        Assert.DoesNotContain("mail", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
