using System.Text.Json.Nodes;
using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Katalog mit Programmen und <c>Replace</c> (Nachtrag P2, Kapitel 7): Zuordnung eines Programms zu einem
/// Profil (BA-02), Ersetzen und Löschen von Vokabular (BA-01, SI-11), eingebaute Profile bleiben (BA-21).
/// </summary>
public class ProfileCatalogV2Tests
{
    private static readonly string[] AcceptedPrograms = ["outlook.exe", "olk.exe", "ms-teams.exe", "winword.exe", "notepad++.exe"];

    /// <summary>„notizen“: sonst wie in <c>FullDocumentV2</c>, mit den angegebenen Programmen.</summary>
    private static string NotizenDocument(params string[] programs) =>
        WithProfileV2(p =>
        {
            p["id"] = "notizen";
            p["programs"] = new JsonArray([.. programs.Select(program => (JsonNode?)JsonValue.Create(program))]);
        });

    /// <summary>Katalog mit „mail-online“ (aus <c>ExtendedDocumentV2</c>) und danach „notizen“ mit winword.exe.</summary>
    private static ProfileCatalog CatalogWithMailAndNotes()
    {
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(ExtendedDocumentV2, AllowList).Succeeded);
        Assert.True(catalog.Import(NotizenDocument("winword.exe"), AllowList).Succeeded);
        return catalog;
    }

    [Fact]
    public void S11_WithoutImportsNoProgramBelongsToAProfile()
    {
        var catalog = new ProfileCatalog();

        foreach (var program in AcceptedPrograms)
        {
            Assert.Null(catalog.FindForProgram(program));
        }
    }

    [Fact]
    public void S11_AnImportedProfileIsFoundByItsProgramsIgnoringCase()
    {
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(ExtendedDocumentV2, AllowList).Succeeded);

        Assert.Equal("mail-online", catalog.FindForProgram("OUTLOOK.EXE")?.Id);
        Assert.Equal("mail-online", catalog.FindForProgram("olk.exe")?.Id);
        Assert.Null(catalog.FindForProgram("excel.exe"));
        Assert.Throws<ArgumentNullException>(() => catalog.FindForProgram(null!));
    }

    [Fact]
    public void S11_AProgramBelongsToOneProfileOnly()
    {
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(ExtendedDocumentV2, AllowList).Succeeded);
        var before = catalog.Profiles.ToList();

        var result = catalog.Import(NotizenDocument("olk.exe"), AllowList);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == "$.profiles[0].programs[0]");
        Assert.Equal(before, catalog.Profiles);
        Assert.Null(catalog.Find("notizen"));
    }

    [Fact]
    public void S11_AfterRemoveTheProgramIsFree()
    {
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(ExtendedDocumentV2, AllowList).Succeeded);

        Assert.True(catalog.Remove("mail-online"));

        Assert.Null(catalog.FindForProgram("olk.exe"));
        Assert.True(catalog.Import(NotizenDocument("olk.exe"), AllowList).Succeeded);
        Assert.Equal("notizen", catalog.FindForProgram("olk.exe")?.Id);
    }

    [Fact]
    public void S12_ReplaceWithoutVocabularyKeepsPositionAndPrograms()
    {
        var catalog = CatalogWithMailAndNotes();
        var original = catalog.Find("mail-online")!;
        var withoutVocabulary = original with { Vocabulary = TermList.Empty };

        var result = catalog.Replace(withoutVocabulary, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(withoutVocabulary, result.Value);
        Assert.Equal(["standard", "strict", "mail-online", "notizen"], catalog.Profiles.Select(profile => profile.Id));
        Assert.Empty(catalog.Find("mail-online")!.Vocabulary);

        var exported = JsonNode.Parse(catalog.ExportImported())!;
        Assert.False(exported["profiles"]![0]!.AsObject().ContainsKey("vocabulary"));
        Assert.Equal(withoutVocabulary, catalog.FindForProgram("olk.exe"));
    }

    public static TheoryData<string> RejectedReplacements =>
    [
        "builtin-unchanged",
        "unknown-id",
        "vocabulary-secret",
        "endpoint-not-allowed",
        "program-of-another-profile",
    ];

    [Theory]
    [MemberData(nameof(RejectedReplacements))]
    public void S12_ReplaceIsRejectedAndLeavesTheCatalogUnchanged(string name)
    {
        var catalog = CatalogWithMailAndNotes();
        var mail = catalog.Find("mail-online")!;
        var before = catalog.Profiles.ToList();
        var exportBefore = catalog.ExportImported();
        var allowList = AllowList;

        var (profile, path) = name switch
        {
            "builtin-unchanged" => (BuiltInProfiles.Standard, "$.profiles[0].id"),
            "unknown-id" => (mail with { Id = "unbekannt" }, "$.profiles[0].id"),
            "vocabulary-secret" => (mail with { Vocabulary = new TermList([ProfileImportTests.FakeKey]) }, "$.profiles[0].vocabulary[0]"),
            "endpoint-not-allowed" => (mail, "$.profiles[0].recognition.endpoint"),
            "program-of-another-profile" => (mail with { Programs = new TermList(["winword.exe"]) }, "$.profiles[0].programs[0]"),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        if (name == "endpoint-not-allowed")
        {
            allowList = EndpointAllowList.None;
        }

        var result = catalog.Replace(profile, allowList);

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, error => error.Path.StartsWith(path, StringComparison.Ordinal));
        foreach (var error in result.Errors)
        {
            Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(before, catalog.Profiles);
        Assert.Equal(exportBefore, catalog.ExportImported());
    }

    [Fact]
    public void S24_ReplacingAProfileWithItsOwnProgramsIsNoConflict()
    {
        // (1) vollständig unverändert ersetzen
        var catalog = CatalogWithMailAndNotes();
        var original = catalog.Find("mail-online")!;
        var exportBefore = catalog.ExportImported();

        var unchanged = catalog.Replace(original, AllowList);

        Assert.True(unchanged.Succeeded, string.Join("\n", unchanged.Errors));
        Assert.Equal(original, unchanged.Value);
        Assert.Equal(exportBefore, catalog.ExportImported());
        Assert.Equal(original, catalog.FindForProgram("outlook.exe"));
        Assert.Equal(original, catalog.FindForProgram("olk.exe"));

        // (2) geänderter Name, unveränderte Programme
        catalog = CatalogWithMailAndNotes();
        var renamed = catalog.Find("mail-online")! with { Name = "Mail neu" };
        var renamedResult = catalog.Replace(renamed, AllowList);
        Assert.True(renamedResult.Succeeded, string.Join("\n", renamedResult.Errors));
        Assert.Equal("Mail neu", catalog.Find("mail-online")!.Name);

        // (3) ein eigenes Programm weglassen
        catalog = CatalogWithMailAndNotes();
        var fewer = catalog.Find("mail-online")! with { Programs = new TermList(["olk.exe"]) };
        var fewerResult = catalog.Replace(fewer, AllowList);
        Assert.True(fewerResult.Succeeded, string.Join("\n", fewerResult.Errors));
        Assert.Null(catalog.FindForProgram("outlook.exe"));
        Assert.Equal("mail-online", catalog.FindForProgram("olk.exe")?.Id);

        // (4) eigenes Programm behalten, ein freies neu dazu
        catalog = CatalogWithMailAndNotes();
        var more = catalog.Find("mail-online")! with { Programs = new TermList(["olk.exe", "excel.exe"]) };
        var moreResult = catalog.Replace(more, AllowList);
        Assert.True(moreResult.Succeeded, string.Join("\n", moreResult.Errors));
        Assert.Equal("mail-online", catalog.FindForProgram("excel.exe")?.Id);
    }

    [Fact]
    public void S24_ReplacingAnotherProfilesProgramIsRejected()
    {
        // (5) „notizen“ mit olk.exe, das „mail-online“ gehört
        var catalog = CatalogWithMailAndNotes();
        var before = catalog.Profiles.ToList();
        var notes = catalog.Find("notizen")! with { Programs = new TermList(["olk.exe"]) };

        var result = catalog.Replace(notes, AllowList);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == "$.profiles[0].programs[0]");
        Assert.Equal(before, catalog.Profiles);
        Assert.Equal("mail-online", catalog.FindForProgram("olk.exe")?.Id);
    }
}
