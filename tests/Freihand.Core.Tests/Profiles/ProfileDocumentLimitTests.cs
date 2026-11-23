using System.Text;
using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Dokumentgrenze beim Export von Version 1 nach Version 2 (Nachtrag P2, Kapitel 5.2 und S25): Der Export wächst
/// um die Zeile <c>recognitionMode</c> je Profil und kann die Grenze von 256 KB überschreiten. Dann bricht er ab,
/// kürzt nie still und lässt das Original unberührt.
/// </summary>
public class ProfileDocumentLimitTests
{
    /// <summary><c>DataSafety.MaxDocumentLength</c> (256 × 1024); der Typ ist intern, darum hier als Zahl.</summary>
    private const int DocumentLimit = 262_144;

    /// <summary>Länge der Zeile <c>      "recognitionMode": "live",</c> samt Zeilenende, je Profil.</summary>
    private const int ModeLineLength = 33;

    /// <summary>G: Version 1, Länge genau an der Grenze.</summary>
    private static int[] BoundaryDescriptions { get; } = Spread(7476);

    /// <summary>E: Version 1, so lang, dass die Version-2-Fassung genau an der Grenze liegt.</summary>
    private static int[] ExportBoundaryDescriptions { get; } = Spread(5364);

    /// <summary>
    /// Beschreibungslängen der Grenzfamilie: <c>d_i = 1 + x ÷ 64</c>, die ersten <c>x mod 64</c> Profile ein
    /// Zeichen mehr.
    /// </summary>
    private static int[] Spread(int rest) =>
        [.. Enumerable.Range(0, ProfileSchema.MaxProfiles)
            .Select(index => 1 + (rest / ProfileSchema.MaxProfiles) + (index < rest % ProfileSchema.MaxProfiles ? 1 : 0))];

    /// <summary>
    /// Die Grenzfamilie als Text aus einem Erzeuger (nicht über <c>Export</c>, der an der Grenze selbst scheitern
    /// kann): 64 gültige Profile mit Höchstlängen bei Id, Name, beiden Endpunkten und Audioquelle, in der Form
    /// des Exports (zwei Leerzeichen, LF, abschliessendes LF).
    /// </summary>
    private static string Family(int[] descriptionLengths, bool version2)
    {
        var text = new StringBuilder();
        text.Append("{\n  \"kind\": \"freihand.profiles\",\n  \"schemaVersion\": ").Append(version2 ? 2 : 1).Append(",\n  \"profiles\": [\n");
        for (var index = 0; index < descriptionLengths.Length; index++)
        {
            var id = "p" + index.ToString("D63", System.Globalization.CultureInfo.InvariantCulture);
            text.Append("    {\n");
            text.Append("      \"id\": \"").Append(id).Append("\",\n");
            text.Append("      \"name\": \"").Append('n', 64).Append("\",\n");
            text.Append("      \"description\": \"").Append('a', descriptionLengths[index]).Append("\",\n");
            text.Append("      \"recognition\": {\n        \"route\": \"online\",\n        \"endpoint\": \"https://stt.example.com/v1/")
                .Append('a', 1473).Append("\"\n      },\n");
            text.Append("      \"transform\": {\n        \"route\": \"local\",\n        \"endpoint\": \"http://127.0.0.1:1234/")
                .Append('a', 1478).Append("\"\n      },\n");
            text.Append("      \"dataClass\": \"oeffentlich\",\n");
            text.Append("      \"language\": \"de-CH\",\n");
            if (version2)
            {
                text.Append("      \"recognitionMode\": \"live\",\n");
            }

            text.Append("      \"recordingMode\": \"hold\",\n");
            text.Append("      \"audioSource\": {\n        \"kind\": \"device\",\n        \"deviceId\": \"").Append('d', 256)
                .Append("\",\n        \"displayName\": \"").Append('h', 64).Append("\"\n      },\n");
            text.Append("      \"output\": \"clipboard\",\n");
            text.Append("      \"formatRules\": \"ch-standard\",\n");
            text.Append("      \"strictness\": \"standard\"\n");
            text.Append(index < descriptionLengths.Length - 1 ? "    },\n" : "    }\n");
        }

        text.Append("  ]\n}\n");
        return text.ToString();
    }

    /// <summary>E+: wie E, aber Profil 63 mit einer Beschreibung von 85 statt 84 Zeichen.</summary>
    private static int[] ExportBoundaryPlusOne()
    {
        var lengths = (int[])ExportBoundaryDescriptions.Clone();
        lengths[63]++;
        return lengths;
    }

    private static string BoundaryPlusOneVersion1()
    {
        var lengths = (int[])BoundaryDescriptions.Clone();
        lengths[63]++;
        return Family(lengths, version2: false);
    }

    [Fact]
    public void S25_TheFixtureFamilyHasTheDocumentedLengths()
    {
        Assert.Equal(DocumentLimit, Family(BoundaryDescriptions, version2: false).Length);
        Assert.Equal(DocumentLimit + 1, BoundaryPlusOneVersion1().Length);
        Assert.Equal(254_668, Family([.. Enumerable.Repeat(1, ProfileSchema.MaxProfiles)], version2: false).Length);
        Assert.Equal(260_032, Family(ExportBoundaryDescriptions, version2: false).Length);
        Assert.Equal(260_033, Family(ExportBoundaryPlusOne(), version2: false).Length);
        Assert.Equal(DocumentLimit + (ProfileSchema.MaxProfiles * ModeLineLength), Family(BoundaryDescriptions, version2: true).Length);
        Assert.Equal(264_256, Family(BoundaryDescriptions, version2: true).Length);
        Assert.Equal(DocumentLimit, Family(ExportBoundaryDescriptions, version2: true).Length);
        Assert.Equal(DocumentLimit + 1, Family(ExportBoundaryPlusOne(), version2: true).Length);
        Assert.Equal(118, BoundaryDescriptions[51]);
        Assert.Equal(117, BoundaryDescriptions[52]);
        Assert.Equal(85, ExportBoundaryDescriptions[51]);
        Assert.Equal(84, ExportBoundaryDescriptions[52]);
    }

    [Fact]
    public void S25_1_ADocumentExactlyAtTheLimitIsImportedAndOneCharacterMoreIsRejected()
    {
        var boundary = Family(BoundaryDescriptions, version2: false);

        var result = ProfileSerializer.Import(boundary, AllowList);

        Assert.Equal(DocumentLimit, boundary.Length);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(ProfileSchema.MaxProfiles, result.Value.Count);

        var catalog = new ProfileCatalog();
        var tooLong = catalog.Import(BoundaryPlusOneVersion1(), AllowList);

        Assert.False(tooLong.Succeeded);
        Assert.Contains(tooLong.Errors, error => error.Path == "$");
        Assert.Equal(BuiltInProfiles.All, catalog.Profiles);
    }

    [Fact]
    public void S25_2_ExportOfAnImportedDocumentAtTheLimitFailsBecauseVersion2IsLonger()
    {
        var boundary = Family(BoundaryDescriptions, version2: false);
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(boundary, AllowList).Succeeded);

        Assert.Equal(DocumentLimit + (ProfileSchema.MaxProfiles * ModeLineLength), Family(BoundaryDescriptions, version2: true).Length);
        var error = Assert.Throws<InvalidOperationException>(() => catalog.ExportImported());

        Assert.Contains("256 KB", error.Message, StringComparison.Ordinal);
        foreach (var part in new[] { "p000000", "nnnnnnnnnn", "aaaaaaaaaa", "stt.example.com", "127.0.0.1", "dddddddddd", "hhhhhhhhhh" })
        {
            Assert.DoesNotContain(part, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void S25_3_AFailedExportLeavesTheCatalogAndTheOriginalUntouched()
    {
        var boundary = Family(BoundaryDescriptions, version2: false);
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(boundary, AllowList).Succeeded);
        var before = catalog.Profiles.ToList();
        var ids = before.Select(profile => profile.Id).ToList();

        Assert.Throws<InvalidOperationException>(() => catalog.ExportImported());
        Assert.Throws<InvalidOperationException>(() => catalog.ExportImported());

        Assert.Equal(before, catalog.Profiles);
        Assert.Equal(ProfileSchema.MaxProfiles + 2, catalog.Profiles.Count);
        foreach (var id in ids)
        {
            Assert.Same(before.Single(profile => profile.Id == id), catalog.Find(id));
        }

        Assert.Equal(Family(BoundaryDescriptions, version2: false), boundary);
    }

    [Fact]
    public void S25_4_TheExportLimitIsExactlyTheVersion2Length()
    {
        var export = Family(ExportBoundaryDescriptions, version2: false);
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(export, AllowList).Succeeded);

        var exported = catalog.ExportImported();

        Assert.Equal(Family(ExportBoundaryDescriptions, version2: true), exported);
        Assert.Equal(DocumentLimit, exported.Length);
        var reimported = ProfileSerializer.Import(exported, AllowList);
        Assert.True(reimported.Succeeded, string.Join("\n", reimported.Errors));
        Assert.Equal(catalog.Profiles.Skip(2), reimported.Value);

        var over = new ProfileCatalog();
        Assert.True(over.Import(Family(ExportBoundaryPlusOne(), version2: false), AllowList).Succeeded);
        Assert.Throws<InvalidOperationException>(() => over.ExportImported());
    }

    [Fact]
    public void S25_5_AFailedExportNeverOverwritesAnExistingFile()
    {
        var boundary = Family(BoundaryDescriptions, version2: false);
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(boundary, AllowList).Succeeded);
        var directory = Directory.CreateTempSubdirectory("freihand-s25-");
        try
        {
            var file = Path.Combine(directory.FullName, "profile.json");
            File.WriteAllText(file, boundary);

            try
            {
                File.WriteAllText(file, catalog.ExportImported());
                Assert.Fail("Der Export hätte abbrechen müssen.");
            }
            catch (InvalidOperationException)
            {
                // erwartet: die Ausnahme tritt vor jedem Schreiben auf
            }

            Assert.Equal(boundary, File.ReadAllText(file));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
