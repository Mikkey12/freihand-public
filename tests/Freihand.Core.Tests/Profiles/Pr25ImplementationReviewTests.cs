using System.Text.Json.Nodes;
using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

public sealed class Pr25ImplementationReviewTests
{
    // OF-01: Optional heisst weglassbar; null ist weder Liste noch erlaubter Stil.
    [Theory]
    [InlineData("vocabulary")]
    [InlineData("programs")]
    [InlineData("style")]
    public void V01_ExplicitNullIsRejectedLikeTheSchema(string field)
    {
        var root = JsonNode.Parse(FullDocumentV2)!;
        var profile = root["profiles"]![0]!;
        if (field == "style") profile["transform"]!["style"] = null;
        else profile[field] = null;
        var json = root.ToJsonString();
        var validator = new MiniSchemaValidator(RepositoryFiles.ReadSchema("profiles.v2.schema.json"));
        Assert.NotEmpty(validator.Validate(root));
        var result = ProfileSerializer.Import(json, AllowList);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path ==
            "$.profiles[0]." + (field == "style" ? "transform.style" : field));
    }

    // OF-01: Auch die Testhilfe muss den tatsächlichen Endanker des Schemas prüfen.
    [Theory]
    [InlineData("id", "abc\n")]
    [InlineData("language", "de-CH\n")]
    [InlineData("formatRules", "ch-standard\n")]
    [InlineData("programs", "olk.exe\n")]
    public void V02_SchemaValidatorRejectsFinalLineFeed(string field, string value)
    {
        var root = JsonNode.Parse(FullDocumentV2)!;
        var profile = root["profiles"]![0]!;
        profile[field] = field == "programs" ? new JsonArray(value) : JsonValue.Create(value);
        Assert.False(ProfileSerializer.Import(root.ToJsonString(), AllowList).Succeeded);
        var validator = new MiniSchemaValidator(RepositoryFiles.ReadSchema("profiles.v2.schema.json"));
        Assert.NotEmpty(validator.Validate(root));
    }

    // OF-01: Der Exportvertrag verspricht bei ungültigen Profilen InvalidOperationException.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V03_InvalidNewEnumUsesDocumentedExportError(bool style)
    {
        var profile = Assert.Single(ProfileSerializer.Import(FullDocumentV2, AllowList).Value!);
        profile = style
            ? profile with { Transform = profile.Transform with { Style = (TransformStyle)999 } }
            : profile with { RecognitionMode = (RecognitionMode)999 };
        Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([profile]));
    }

    // BA-01, OF-01: Replace meldet einen Importfehler mit Pfad und lässt den Katalog unverändert.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V04_InvalidNewEnumReturnsReplaceError(bool style)
    {
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(FullDocumentV2, AllowList).Succeeded);
        var before = catalog.Profiles.ToArray();
        var profile = catalog.Find("mail-online")!;
        profile = style
            ? profile with { Transform = profile.Transform with { Style = (TransformStyle)999 } }
            : profile with { RecognitionMode = (RecognitionMode)999 };
        var result = catalog.Replace(profile, AllowList);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path ==
            "$.profiles[0]." + (style ? "transform.style" : "recognitionMode"));
        Assert.Equal(before, catalog.Profiles);
    }
}
