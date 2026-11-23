using System.Text.Json.Nodes;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Abzuweisende Dokumente der Version 2 für S05 bis S07, S09 und S10, mit Fehlerpfad und der Angabe, ob schon
/// das JSON-Schema den Fehler erkennt (S13). Die Pfade sind Anfänge: ein Fehlerpfad beginnt mit ihnen.
/// </summary>
internal static class ProfileV2Cases
{
    /// <summary>S05: Dokumente der Version 1 mit je einem neuen Feld. Das Schema ist hier das der Version 1.</summary>
    public static readonly Dictionary<string, InvalidCase> Version1WithNewFields = new()
    {
        ["recognition-mode"] = new(WithProfile(p => p["recognitionMode"] = "live"), "$.profiles[0]", true),
        ["vocabulary"] = new(WithProfile(p => p["vocabulary"] = new JsonArray("Gerber")), "$.profiles[0]", true),
        ["programs"] = new(WithProfile(p => p["programs"] = new JsonArray("olk.exe")), "$.profiles[0]", true),
        ["transform-style"] = new(WithProfile(p => p["transform"]!["style"] = "mail"), "$.profiles[0].transform", true),
    };

    /// <summary>S06: Version und Erkennungsmodus.</summary>
    public static readonly Dictionary<string, InvalidCase> VersionAndMode = new()
    {
        ["version-3"] = new(WithRootV2(root => root["schemaVersion"] = 3), "$.schemaVersion", true),
        ["version-0"] = new(WithRootV2(root => root["schemaVersion"] = 0), "$.schemaVersion", true),
        ["version-text"] = new(WithRootV2(root => root["schemaVersion"] = "2"), "$.schemaVersion", true),
        ["mode-missing"] = new(WithProfileV2(p => p.Remove("recognitionMode")), "$.profiles[0].recognitionMode", true),
        ["mode-auto"] = new(WithProfileV2(p => p["recognitionMode"] = "auto"), "$.profiles[0].recognitionMode", true),
        ["unknown-field-vokabular"] = new(WithProfileV2(p => p["vokabular"] = new JsonArray("Gerber")), "$.profiles[0]", true),
    };

    /// <summary>S07: Vokabular, das abgewiesen wird.</summary>
    public static readonly Dictionary<string, InvalidCase> Vocabulary = new()
    {
        ["empty"] = Vocab(new JsonArray(), "$.profiles[0].vocabulary", true),
        ["501-entries"] = Vocab(
            new JsonArray([.. Enumerable.Range(0, 501).Select(i => (JsonNode?)JsonValue.Create("wort" + i))]),
            "$.profiles[0].vocabulary",
            true),
        ["entry-empty"] = Vocab(new JsonArray(""), "$.profiles[0].vocabulary[0]", true),
        ["entry-101-characters"] = Vocab(new JsonArray(new string('a', 101)), "$.profiles[0].vocabulary[0]", true),
        ["entry-leading-space"] = Vocab(new JsonArray(" Gerber"), "$.profiles[0].vocabulary[0]", true),
        ["entry-tab"] = Vocab(new JsonArray("Frau\tGerber"), "$.profiles[0].vocabulary[0]", false),
        ["entry-not-nfc"] = Vocab(new JsonArray("Mu\u0308ller"), "$.profiles[0].vocabulary[0]", false),
        ["entry-secret"] = Vocab(new JsonArray(ProfileImportTests.FakeKey), "$.profiles[0].vocabulary[0]", false),
        ["entry-script"] = Vocab(new JsonArray("<script>"), "$.profiles[0].vocabulary[0]", false),
        ["entry-null"] = Vocab(new JsonArray((JsonNode?)null), "$.profiles[0].vocabulary[0]", true),
        ["not-a-list"] = Vocab(JsonValue.Create(42), "$.profiles[0].vocabulary", true),
        ["entry-twice"] = Vocab(new JsonArray("Gerber", "Gerber"), "$.profiles[0].vocabulary[1]", false),
    };

    /// <summary>S09: Umformstil, der abgewiesen wird.</summary>
    public static readonly Dictionary<string, InvalidCase> Style = new()
    {
        ["style-without-transform"] = new(
            WithProfileV2(p => p["transform"] = new JsonObject { ["route"] = "none", ["style"] = "mail" }),
            "$.profiles[0].transform.style",
            true),
        ["style-unknown"] = new(
            WithProfileV2(p => p["transform"]!["style"] = "formal"),
            "$.profiles[0].transform.style",
            true),
    };

    /// <summary>S10: Programme, die abgewiesen werden.</summary>
    public static readonly Dictionary<string, InvalidCase> Programs = new()
    {
        ["empty"] = Progs(new JsonArray(), "$.profiles[0].programs", true),
        ["33-entries"] = Progs(
            new JsonArray([.. Enumerable.Range(0, 33).Select(i => (JsonNode?)JsonValue.Create($"programm{i}.exe"))]),
            "$.profiles[0].programs",
            true),
        ["upper-case"] = Progs(new JsonArray("OUTLOOK.EXE"), "$.profiles[0].programs[0]", true),
        ["without-extension"] = Progs(new JsonArray("outlook"), "$.profiles[0].programs[0]", true),
        ["with-drive-path"] = Progs(new JsonArray(@"c:\office\outlook.exe"), "$.profiles[0].programs[0]", true),
        ["with-folder"] = Progs(new JsonArray("office/outlook.exe"), "$.profiles[0].programs[0]", true),
        ["extension-only"] = Progs(new JsonArray(".exe"), "$.profiles[0].programs[0]", true),
        ["65-characters"] = Progs(new JsonArray(new string('a', 61) + ".exe"), "$.profiles[0].programs[0]", true),
        ["secret"] = Progs(new JsonArray(ProfileImportTests.FakeKey + ".exe"), "$.profiles[0].programs[0]", false),
        ["twice-in-profile"] = Progs(new JsonArray("olk.exe", "olk.exe"), "$.profiles[0].programs[1]", false),
        ["twice-in-document"] = new(
            WithRootV2(root =>
            {
                var first = root["profiles"]![0]!;
                first["programs"] = new JsonArray("olk.exe");
                var second = first.DeepClone();
                second["id"] = "zweites";
                root["profiles"]!.AsArray().Add(second);
            }),
            "$.profiles[1].programs[0]",
            false),
    };

    /// <summary>Alle Fälle der Version 2, die das Schema abweisen muss (S13).</summary>
    public static IEnumerable<KeyValuePair<string, InvalidCase>> SchemaRejectedVersion2 =>
        new[] { VersionAndMode, Vocabulary, Style, Programs }
            .SelectMany((table, index) => table.Select(entry =>
                new KeyValuePair<string, InvalidCase>($"{TableNames[index]}/{entry.Key}", entry.Value)))
            .Where(entry => entry.Value.SchemaRejects);

    private static readonly string[] TableNames = ["version-and-mode", "vocabulary", "style", "programs"];

    private static InvalidCase Vocab(JsonNode vocabulary, string path, bool schemaRejects) =>
        new(WithProfileV2(p => p["vocabulary"] = vocabulary.DeepClone()), path, schemaRejects);

    private static InvalidCase Progs(JsonNode programs, string path, bool schemaRejects) =>
        new(WithProfileV2(p => p["programs"] = programs.DeepClone()), path, schemaRejects);
}
