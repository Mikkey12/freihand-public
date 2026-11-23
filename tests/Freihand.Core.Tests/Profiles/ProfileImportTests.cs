using System.Text.Json.Nodes;
using Freihand.Core.Profiles;
using Freihand.Core.Routing;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>Import mit Prüfung nach OF-01: fehlerhafte und unsichere Dokumente werden abgewiesen.</summary>
public class ProfileImportTests
{
    /// <summary>
    /// Abzuweisende Dokumente: Name, Dokument, erwarteter Fehlerpfad und ob schon das JSON-Schema den
    /// Fehler erkennt. Was das Schema nicht ausdrücken kann (Geheimnisse, Zulassung, Eindeutigkeit),
    /// prüft nur die App. Fälle, die mit <c>WithProfile</c> oder <c>WithRoot</c> gebaut sind, tragen dieselbe
    /// Änderung auch als Dokument der Version 2 (S14).
    /// </summary>
    internal static readonly Dictionary<string, InvalidCase> Invalid = new()
    {
        // Aufbau und Version
        ["no-json"] = Raw("{ profiles: [] }", "$", false),
        ["comment"] = Raw("{ /* x */ \"kind\": \"freihand.profiles\", \"schemaVersion\": 1, \"profiles\": [] }", "$", false),
        ["trailing-comma"] = Raw("{ \"kind\": \"freihand.profiles\", \"schemaVersion\": 1, \"profiles\": [], }", "$", false),
        ["duplicate-property"] = Raw("{ \"kind\": \"freihand.profiles\", \"kind\": \"freihand.profiles\", \"schemaVersion\": 1, \"profiles\": [] }", "$", false),
        ["array-root"] = Raw("[]", "$", true),
        ["kind-missing"] = OnRoot(root => root.Remove("kind"), "$.kind", true),
        ["kind-commands"] = OnRoot(root => root["kind"] = "freihand.commands", "$.kind", true),
        ["version-missing"] = OnRoot(root => root.Remove("schemaVersion"), "$.schemaVersion", true),
        ["version-newer"] = OnRoot(root => root["schemaVersion"] = 3, "$.schemaVersion", true) with { JsonV2 = null },
        ["version-zero"] = OnRoot(root => root["schemaVersion"] = 0, "$.schemaVersion", true),
        ["version-string"] = OnRoot(root => root["schemaVersion"] = "1", "$.schemaVersion", true),
        ["version-fraction"] = OnRoot(root => root["schemaVersion"] = 1.5, "$.schemaVersion", true),
        ["profiles-missing"] = OnRoot(root => root.Remove("profiles"), "$.profiles", true),
        ["profile-null"] = OnRoot(root => root["profiles"] = new JsonArray((JsonNode?)null), "$.profiles[0]", true),
        ["too-deep"] = OnProfile(p => p["name"] = Nest(10), "$", false),

        // Unbekannte Felder, auch als Versteck für Geheimnisse oder Skripte (OF-01)
        ["unknown-root-field"] = OnRoot(root => root["script"] = "calc.exe", "$", true),
        ["unknown-profile-field"] = OnProfile(p => p["apiKey"] = "abc", "$.profiles[0]", true),
        ["unknown-nested-field"] = OnProfile(p => p["transform"]!["token"] = "abc", "$.profiles[0].transform", true),
        ["wrong-case-field"] = OnProfile(p => { p["Name"] = p["name"]!.DeepClone(); p.Remove("name"); }, "$.profiles[0]", true),
        ["wrong-type"] = OnProfile(p => p["name"] = 42, "$.profiles[0].name", true),

        // Pflichtfelder und Werte
        ["id-missing"] = OnProfile(p => p.Remove("id"), "$.profiles[0].id", true),
        ["id-invalid"] = OnProfile(p => p["id"] = "Mail Online", "$.profiles[0].id", true),
        ["name-missing"] = OnProfile(p => p.Remove("name"), "$.profiles[0].name", true),
        ["name-blank"] = OnProfile(p => p["name"] = "  ", "$.profiles[0].name", true),
        ["name-too-long"] = OnProfile(p => p["name"] = new string('a', 65), "$.profiles[0].name", true),
        ["recognition-missing"] = OnProfile(p => p.Remove("recognition"), "$.profiles[0].recognition", true),
        ["recognition-route-unknown"] = OnProfile(p => p["recognition"]!["route"] = "cloud", "$.profiles[0].recognition.route", true),
        ["transform-route-unknown"] = OnProfile(p => p["transform"]!["route"] = "script", "$.profiles[0].transform.route", true),
        ["data-class-unknown"] = OnProfile(p => p["dataClass"] = "geheim", "$.profiles[0].dataClass", true),
        ["language-invalid"] = OnProfile(p => p["language"] = "Deutsch", "$.profiles[0].language", true),
        ["recording-mode-unknown"] = OnProfile(p => p["recordingMode"] = "voice", "$.profiles[0].recordingMode", true),
        ["output-unknown"] = OnProfile(p => p["output"] = "email", "$.profiles[0].output", true),
        // Codex-Review R7: auch syntaktisch gültige Bezeichner dürfen kein Geheimnis sein (SI-03).
        ["id-secret"] = OnProfile(p => p["id"] = FakeKey, "$.profiles[0].id", false),
        ["format-rules-secret"] = OnProfile(p => p["formatRules"] = FakeKey, "$.profiles[0].formatRules", false),
        ["format-rules-invalid"] = OnProfile(p => p["formatRules"] = "../regeln.json", "$.profiles[0].formatRules", true),
        ["strictness-unknown"] = OnProfile(p => p["strictness"] = "lax", "$.profiles[0].strictness", true),
        ["audio-device-without-id"] = OnProfile(p => p["audioSource"]!.AsObject().Remove("deviceId"), "$.profiles[0].audioSource.deviceId", true),
        ["audio-default-with-id"] = OnProfile(p => p["audioSource"] = new JsonObject { ["kind"] = "systemDefault", ["deviceId"] = "x" }, "$.profiles[0].audioSource", true),

        // AU-03: strenge Profile ohne Zwischenablage
        ["strict-clipboard"] = OnProfile(p => p["strictness"] = "strict", "$.profiles[0].output", true),

        // Endpunkte (WE-03, WE-16, OF-01)
        ["endpoint-missing-remote"] = OnProfile(p => p["recognition"]!.AsObject().Remove("endpoint"), "$.profiles[0].recognition.endpoint", true),
        ["endpoint-on-device"] = OnProfile(p => p["recognition"]!["route"] = "device", "$.profiles[0].recognition.endpoint", true),
        ["endpoint-on-none"] = OnProfile(p => p["transform"]!["route"] = "none", "$.profiles[0].transform.endpoint", true),
        ["endpoint-not-allowed"] = OnProfile(p => p["recognition"]!["endpoint"] = "https://evil.example.org/v1", "$.profiles[0].recognition.endpoint", false),
        ["endpoint-other-path"] = OnProfile(p => p["recognition"]!["endpoint"] = "https://stt.example.com/v2", "$.profiles[0].recognition.endpoint", false),
        ["endpoint-path-prefix-trick"] = OnProfile(p => p["recognition"]!["endpoint"] = "https://stt.example.com/v1evil", "$.profiles[0].recognition.endpoint", false),
        ["endpoint-http-remote"] = OnProfile(p => p["recognition"]!["endpoint"] = "http://stt.example.com/v1", "$.profiles[0].recognition.endpoint", false),
        ["endpoint-user-info"] = OnProfile(p => p["recognition"]!["endpoint"] = "https://user:pass@stt.example.com/v1", "$.profiles[0].recognition.endpoint", true),
        ["endpoint-query-key"] = OnProfile(p => p["recognition"]!["endpoint"] = "https://stt.example.com/v1?key=abc", "$.profiles[0].recognition.endpoint", true),
        ["endpoint-fragment"] = OnProfile(p => p["recognition"]!["endpoint"] = "https://stt.example.com/v1#x", "$.profiles[0].recognition.endpoint", true),
        ["endpoint-javascript"] = OnProfile(p => p["recognition"]!["endpoint"] = "javascript:alert(1)", "$.profiles[0].recognition.endpoint", true),
        ["endpoint-file"] = OnProfile(p => p["recognition"]!["endpoint"] = "file:///C:/Windows/System32/calc.exe", "$.profiles[0].recognition.endpoint", true),
        ["endpoint-relative"] = OnProfile(p => p["recognition"]!["endpoint"] = "/v1/listen", "$.profiles[0].recognition.endpoint", true),
        ["endpoint-loopback-remote"] = OnProfile(p => p["recognition"]!["endpoint"] = "http://127.0.0.1:1234/v1", "$.profiles[0].recognition.endpoint", false),
        ["endpoint-local-remote"] = OnProfile(p => p["transform"]!["endpoint"] = "https://llm.example.com/v1", "$.profiles[0].transform.endpoint", false),
        ["endpoint-secret-in-path"] = OnProfile(p => p["recognition"]!["endpoint"] = "https://stt.example.com/v1/sk-abcdefghijklmnopqrstuvwx", "$.profiles[0].recognition.endpoint", false),

        // Keine Geheimnisse und keine ausführbaren Inhalte im freien Text (OF-01, SI-03)
        ["secret-openai"] = OnProfile(p => p["description"] = "Schlüssel sk-proj-abcdefghijklmnopqrstuvwx", "$.profiles[0].description", false),
        ["secret-assignment"] = OnProfile(p => p["description"] = "apiKey = 123", "$.profiles[0].description", false),
        ["secret-bearer"] = OnProfile(p => p["description"] = "Bearer abcdefghijklmnop123", "$.profiles[0].description", false),
        ["secret-private-key"] = OnProfile(p => p["description"] = "-----BEGIN RSA PRIVATE KEY----- abc", "$.profiles[0].description", false),
        ["secret-in-device-name"] = OnProfile(p => p["audioSource"]!["displayName"] = "password: geheim", "$.profiles[0].audioSource.displayName", false),
        ["script-tag"] = OnProfile(p => p["name"] = "<script>alert(1)</script>", "$.profiles[0].name", false),
        ["template-placeholder"] = OnProfile(p => p["description"] = "Pfad ${env:USERPROFILE}", "$.profiles[0].description", false),
        ["command-substitution"] = OnProfile(p => p["description"] = "Ergebnis $(Get-Content x)", "$.profiles[0].description", false),
        ["control-character"] = OnProfile(p => p["name"] = "Mail\u0007online", "$.profiles[0].name", false),
        ["bidi-override"] = OnProfile(p => p["name"] = "Mail\u202Eenilno", "$.profiles[0].name", false),
        ["not-nfc"] = OnProfile(p => p["name"] = "Mu\u0308ller", "$.profiles[0].name", false),

        // Eindeutigkeit und Grenzen
        ["duplicate-id"] = OnRoot(root => root["profiles"]!.AsArray().Add(root["profiles"]![0]!.DeepClone()), "$.profiles[1].id", false),
        ["too-many-profiles"] = OnRoot(root => root["profiles"] = new JsonArray([.. Enumerable.Range(0, 65).Select(_ => root["profiles"]![0]!.DeepClone())]), "$.profiles", true),
        ["too-large"] = OnProfile(p => p["description"] = new string('a', 300_000), "$", true),
    };

    public static TheoryData<string> InvalidCases => [.. Invalid.Keys];

    /// <summary>Die Fälle, die es auch als Dokument der Version 2 gibt (S14); <c>version-newer</c> gehört nicht dazu.</summary>
    public static TheoryData<string> InvalidCasesVersion2 =>
        [.. Invalid.Where(entry => entry.Value.JsonV2 is not null).Select(entry => entry.Key)];

    private static InvalidCase OnProfile(Action<JsonObject> change, string path, bool schemaRejects) =>
        new(WithProfile(change), path, schemaRejects, WithProfileV2(change));

    private static InvalidCase OnRoot(Action<JsonObject> change, string path, bool schemaRejects) =>
        new(WithRoot(change), path, schemaRejects, WithRootV2(change));

    private static InvalidCase Raw(string json, string path, bool schemaRejects) => new(json, path, schemaRejects);

    /// <summary>Erfundener Wert im Format eines Schlüssels, zugleich ein syntaktisch gültiger Bezeichner.</summary>
    internal static string FakeKey => "sk-" + new string('a', 24);

    /// <summary>Teile abgewiesener Werte, die in keiner Meldung erscheinen dürfen (SI-03).</summary>
    internal static readonly string[] RejectedValueMarkers =
        ["sk-", "abcdefghijklmnop", "PRIVATE KEY", "evil.example.org", "Mail Online", "Deutsch", "regeln.json"];

    [Theory]
    [MemberData(nameof(InvalidCasesVersion2))]
    public void S14_Version2RejectsEveryVersion1CaseWithTheSameChange(string name)
    {
        var invalid = Invalid[name];

        var result = ProfileSerializer.Import(invalid.JsonV2!, AllowList);

        Assert.False(result.Succeeded, "Abgewiesen sein sollte: " + name);
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, error => error.Path.StartsWith(invalid.Path, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidCasesVersion2))]
    public void S14_Version2ErrorMessagesNeverRepeatTheRejectedValue(string name)
    {
        var result = ProfileSerializer.Import(Invalid[name].JsonV2!, AllowList);

        Assert.False(result.Succeeded);
        foreach (var error in result.Errors)
        {
            Assert.DoesNotContain(RejectedValueMarkers, marker => error.Message.Contains(marker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void FullSampleIsValid()
    {
        var result = ProfileSerializer.Import(FullDocument, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var profile = Assert.Single(result.Value);
        Assert.Equal("mail-online", profile.Id);
        Assert.Equal(new RecognitionStep(RecognitionRoute.Online, new Uri("https://stt.example.com/v1/listen")), profile.Recognition);
        Assert.Equal(new TransformStep(TransformRoute.Local, new Uri("http://127.0.0.1:1234/v1")), profile.Transform);
        Assert.Equal(DataClass.Public, profile.DataClass);
        Assert.Equal(RecordingMode.Hold, profile.RecordingMode);
        Assert.Equal(AudioSourceSelection.Device("{0.0.1.00000000}.{6f1c2a3b-0000-4000-8000-000000000001}", "Headset"), profile.AudioSource);
        Assert.Equal(OutputMode.Clipboard, profile.Output);
        Assert.False(profile.IsExact);
        Assert.False(profile.RejectsUnknownFields);
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void RejectsInvalidOrUnsafeDocument(string name)
    {
        var invalid = Invalid[name];

        var result = ProfileSerializer.Import(invalid.Json, AllowList);

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, error => error.Path.StartsWith(invalid.Path, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void ErrorMessagesNeverRepeatTheRejectedValue(string name)
    {
        // Codex-Review R7: Eine Meldung landet in Anzeige und Protokoll und darf kein Geheimnis wiedergeben.
        var result = ProfileSerializer.Import(Invalid[name].Json, AllowList);

        foreach (var error in result.Errors)
        {
            Assert.DoesNotContain(RejectedValueMarkers, marker => error.Message.Contains(marker, StringComparison.Ordinal));
        }
    }

    /// <summary>Gewöhnliche Bezeichner, die ein Tokenmuster nur im Wortinneren enthalten (Codex-Nachprüfung N2).</summary>
    public static TheoryData<string> OrdinaryIdentifiers =>
    [
        "standard",
        "ch-standard",
        "mail-intern-2026",
        "token-verwaltung",
        "task-besprechungsnotizen",
        "desk-besprechungsnotizen",
        "risk-assessment-abcdefghijklmnop",
    ];

    [Theory]
    [MemberData(nameof(OrdinaryIdentifiers))]
    public void AcceptsOrdinaryIdentifiersAsIdAndFormatRules(string identifier)
    {
        var json = WithProfile(p =>
        {
            p["id"] = identifier;
            p["formatRules"] = identifier;
        });

        var result = ProfileSerializer.Import(json, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(identifier, result.Value[0].Id);
        Assert.Equal(identifier, result.Value[0].FormatRules);
    }

    [Theory]
    [InlineData("Notizen zur task-besprechungsnotizen-Liste")]
    [InlineData("Ablage desk-besprechungsnotizen-archiv")]
    [InlineData("Kennung xghp_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("Ordner myAKIAABCDEFGHIJKLMNOP")]
    [InlineData("Vorlage für mytoken-Verwaltung: neu")]
    public void TokenPatternsInsideWordsAreNoSecrets(string description)
    {
        var result = ProfileSerializer.Import(WithProfile(p => p["description"] = description), AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
    }

    [Theory]
    [InlineData("sk-aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("desk-sk-aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("Schlüssel (ghp_abcdefghijklmnopqrstuvwxyz)")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("Wert:xoxb-1234567890-abcdef")]
    [InlineData("API-Key: 123")]
    public void SecretsAtAWordStartAreStillRejected(string description)
    {
        var result = ProfileSerializer.Import(WithProfile(p => p["description"] = description), AllowList);

        Assert.Contains(result.Errors, error => error.Path == "$.profiles[0].description");
    }

    [Theory]
    [InlineData("https://stt.example.com/v1/%2Fsk-aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://stt.example.com/v1/%28sk-aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://stt.example.com/v1/%252Fsk-aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://stt.example.com/v1/%73k-aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://stt.example.com/v1/%24%7Benv:HOME%7D")]
    public void EncodingCannotHideSecretsOrCodeInEndpoints(string endpoint)
    {
        // Codex-Nachprüfung 2, F2: geprüft wird auch die dekodierte Adresse, auch bei Mehrfachkodierung.
        var result = ProfileSerializer.Import(WithProfile(p => p["recognition"]!["endpoint"] = endpoint), AllowList);

        Assert.Contains(result.Errors, error => error.Path == "$.profiles[0].recognition.endpoint");
    }

    [Theory]
    [InlineData("https://stt.example.com/v1/task-besprechungsnotizen")]
    [InlineData("https://stt.example.com/v1/%2Fdesk-besprechungsnotizen")]
    [InlineData("https://stt.example.com/v1/risk-assessment-abcdefghijklmnop")]
    public void OrdinaryEndpointPathsAreAccepted(string endpoint)
    {
        var result = ProfileSerializer.Import(WithProfile(p => p["recognition"]!["endpoint"] = endpoint), AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
    }

    [Fact]
    public void WithoutAllowListNoEndpointIsAccepted()
    {
        var result = ProfileSerializer.Import(FullDocument, EndpointAllowList.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == "$.profiles[0].recognition.endpoint");
        Assert.Contains(result.Errors, error => error.Path == "$.profiles[0].transform.endpoint");
    }

    [Fact]
    public void LocalTransformMayOmitEndpoint()
    {
        var json = WithProfile(p =>
        {
            p["recognition"] = new JsonObject { ["route"] = "device" };
            p["transform"] = new JsonObject { ["route"] = "local" };
        });

        var result = ProfileSerializer.Import(json, EndpointAllowList.None);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Null(result.Value[0].Transform.Endpoint);
    }

    [Fact]
    public void ReportsAllErrorsAtOnce()
    {
        var json = WithProfile(p =>
        {
            p["id"] = "Falsch";
            p["dataClass"] = "geheim";
            p["output"] = "email";
        });

        var result = ProfileSerializer.Import(json, AllowList);

        Assert.Equal(
            ["$.profiles[0].id", "$.profiles[0].dataClass", "$.profiles[0].output"],
            result.Errors.Select(error => error.Path));
    }

    [Fact]
    public void ErrorMessagesNameTheAllowedValues()
    {
        var result = ProfileSerializer.Import(WithProfile(p => p["recognition"]!["route"] = "cloud"), AllowList);

        var error = Assert.Single(result.Errors);
        Assert.Contains("online, home, device, companyService", error.Message, StringComparison.Ordinal);
    }

    private static JsonNode Nest(int depth)
    {
        JsonNode node = new JsonObject();
        for (var level = 0; level < depth; level++)
        {
            node = new JsonObject { ["x"] = node };
        }

        return node;
    }
}
