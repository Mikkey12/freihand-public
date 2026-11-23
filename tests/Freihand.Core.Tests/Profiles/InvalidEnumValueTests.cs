using Freihand.Core.Profiles;
using Freihand.Core.Routing;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Ungültige Enum-Werte in im Code gebauten Profilen (Codex-Prüfung PR #25, V03/V04): <c>Export</c> wirft die
/// zugesagte <see cref="InvalidOperationException"/>, <c>Replace</c> gibt einen Fehler mit Feldpfad zurück, der
/// Katalog bleibt gleich, und der Wert erscheint in keiner Meldung. Geprüft werden alle Enums, die ein Profil
/// serialisiert.
/// </summary>
public class InvalidEnumValueTests
{
    public static TheoryData<string> Fields =>
    [
        "recognition.route",
        "transform.route",
        "transform.style",
        "recognitionMode",
        "recordingMode",
        "audioSource.kind",
        "output",
        "strictness",
    ];

    private static Profile WithInvalid(Profile profile, string field) => field switch
    {
        "recognition.route" => profile with { Recognition = profile.Recognition with { Route = (RecognitionRoute)999 } },
        "transform.route" => profile with { Transform = profile.Transform with { Route = (TransformRoute)999 } },
        "transform.style" => profile with { Transform = profile.Transform with { Style = (TransformStyle)999 } },
        "recognitionMode" => profile with { RecognitionMode = (RecognitionMode)999 },
        "recordingMode" => profile with { RecordingMode = (RecordingMode)999 },
        "audioSource.kind" => profile with { AudioSource = profile.AudioSource with { Kind = (AudioSourceKind)999 } },
        "output" => profile with { Output = (OutputMode)999 },
        "strictness" => profile with { Strictness = (ProfileStrictness)999 },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static Profile Mail()
    {
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(ExtendedDocumentV2, AllowList).Succeeded);
        return catalog.Find("mail-online")!;
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void ExportThrowsTheDocumentedExceptionForAnInvalidEnumValue(string field)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProfileSerializer.Export([WithInvalid(Mail(), field)]));

        Assert.DoesNotContain("999", error.Message, StringComparison.Ordinal);
        Assert.Contains("$.profiles[0]." + field, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void ReplaceReturnsAnErrorWithTheFieldPathAndLeavesTheCatalogUnchanged(string field)
    {
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(ExtendedDocumentV2, AllowList).Succeeded);
        var before = catalog.Profiles.ToArray();
        var exportBefore = catalog.ExportImported();

        var result = catalog.Replace(WithInvalid(catalog.Find("mail-online")!, field), AllowList);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path.StartsWith("$.profiles[0]." + field, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Message.Contains("999", StringComparison.Ordinal));
        Assert.Equal(before, catalog.Profiles);
        Assert.Equal(exportBefore, catalog.ExportImported());
    }
}
