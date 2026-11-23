using System.Text.Json.Nodes;
using Freihand.Core.Profiles;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Ein abzuweisendes Dokument mit erwartetem Fehlerpfad und der Angabe, ob schon das JSON-Schema es erkennt.
/// <see cref="JsonV2"/> ist dieselbe Änderung am Beispiel der Version 2, wo es sie gibt (S14).
/// </summary>
internal sealed record InvalidCase(string Json, string Path, bool SchemaRejects, string? JsonV2 = null);

/// <summary>Erfundene Testdaten für Profile; Endpunkte nur unter example.com und der Loopback-Adresse.</summary>
internal static class ProfileSamples
{
    /// <summary>Ein eigenes Profil, das jedes Feld nutzt, auch beide Endpunkte und ein bestimmtes Gerät.</summary>
    public const string FullDocument = """
        {
          "kind": "freihand.profiles",
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "mail-online",
              "name": "Mail online",
              "description": "Erkennung beim Anbieter, Umformung auf diesem Gerät.",
              "recognition": {
                "route": "online",
                "endpoint": "https://stt.example.com/v1/listen"
              },
              "transform": {
                "route": "local",
                "endpoint": "http://127.0.0.1:1234/v1"
              },
              "dataClass": "oeffentlich",
              "language": "de-CH",
              "recordingMode": "hold",
              "audioSource": {
                "kind": "device",
                "deviceId": "{0.0.1.00000000}.{6f1c2a3b-0000-4000-8000-000000000001}",
                "displayName": "Headset"
              },
              "output": "clipboard",
              "formatRules": "ch-standard",
              "strictness": "standard"
            }
          ]
        }
        """;

    /// <summary>Das Beispiel der Version 1 in der Form der Version 2 (S02): sonst Zeichen für Zeichen gleich.</summary>
    public const string FullDocumentV2 = """
        {
          "kind": "freihand.profiles",
          "schemaVersion": 2,
          "profiles": [
            {
              "id": "mail-online",
              "name": "Mail online",
              "description": "Erkennung beim Anbieter, Umformung auf diesem Gerät.",
              "recognition": {
                "route": "online",
                "endpoint": "https://stt.example.com/v1/listen"
              },
              "transform": {
                "route": "local",
                "endpoint": "http://127.0.0.1:1234/v1"
              },
              "dataClass": "oeffentlich",
              "language": "de-CH",
              "recognitionMode": "live",
              "recordingMode": "hold",
              "audioSource": {
                "kind": "device",
                "deviceId": "{0.0.1.00000000}.{6f1c2a3b-0000-4000-8000-000000000001}",
                "displayName": "Headset"
              },
              "output": "clipboard",
              "formatRules": "ch-standard",
              "strictness": "standard"
            }
          ]
        }
        """;

    /// <summary>Das Beispiel aus Kapitel 3 des Konzepts: alle Felder der Version 2 (S03).</summary>
    public const string ExtendedDocumentV2 = """
        {
          "kind": "freihand.profiles",
          "schemaVersion": 2,
          "profiles": [
            {
              "id": "mail-online",
              "name": "Mail online",
              "description": "Erkennung beim Anbieter, Umformung auf diesem Gerät.",
              "recognition": {
                "route": "online",
                "endpoint": "https://stt.example.com/v1/listen"
              },
              "transform": {
                "route": "local",
                "endpoint": "http://127.0.0.1:1234/v1",
                "style": "mail"
              },
              "dataClass": "oeffentlich",
              "language": "de-CH",
              "recognitionMode": "sentence",
              "recordingMode": "hold",
              "audioSource": {
                "kind": "device",
                "deviceId": "{0.0.1.00000000}.{6f1c2a3b-0000-4000-8000-000000000001}",
                "displayName": "Headset"
              },
              "output": "clipboard",
              "formatRules": "ch-standard",
              "strictness": "standard",
              "vocabulary": [
                "Gerber",
                "Freihand",
                "Müller-Thurgau AG"
              ],
              "programs": [
                "outlook.exe",
                "olk.exe"
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Die eingebauten Profile im Stand von <c>main</c> vor dem Nachtrag P2 (Version 1); Kopie von
    /// <c>BuiltInProfiles.Json</c> für den Nachweis, dass sie nach der Migration gleich bleiben (S04).
    /// </summary>
    public const string BuiltInJsonVersion1 = """
        {
          "kind": "freihand.profiles",
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "standard",
              "name": "Standard",
              "description": "Diktat auf diesem Gerät direkt ins gebundene Ziel, ohne Umformung.",
              "recognition": {
                "route": "device"
              },
              "transform": {
                "route": "none"
              },
              "dataClass": "intern",
              "language": "de-CH",
              "recordingMode": "toggle",
              "audioSource": {
                "kind": "systemDefault"
              },
              "output": "boundTarget",
              "formatRules": "ch-standard",
              "strictness": "standard"
            },
            {
              "id": "strict",
              "name": "Streng",
              "description": "Exaktes Diktat auf diesem Gerät ins Diktatfenster; unbekannte Felder werden abgewiesen.",
              "recognition": {
                "route": "device"
              },
              "transform": {
                "route": "none"
              },
              "dataClass": "vertraulich",
              "language": "de-CH",
              "recordingMode": "toggle",
              "audioSource": {
                "kind": "systemDefault"
              },
              "output": "dictationWindow",
              "formatRules": "ch-standard",
              "strictness": "strict"
            }
          ]
        }
        """;

    /// <summary>Die Modelldatei des Eintrags (reference-desktop, de-CH) in <c>LiveApprovals.Default</c>.</summary>
    public const string ApprovedModel = "ggml-large-v3-turbo.bin";

    public const string OtherModel = "andere-modelldatei.bin";

    /// <summary>Das abgenommene Modell auf der Grafikkarte.</summary>
    public static RecognizerIdentity CudaRecognizer { get; } = new(ApprovedModel, KnownRuntimes.Cuda);

    public static EndpointAllowList AllowList { get; } = new(
    [
        new Uri("https://stt.example.com/v1"),
        new Uri("https://llm.example.com"),
        new Uri("https://sprichblitz.example.com"),
        new Uri("http://127.0.0.1:1234"),
    ]);

    /// <summary>Das volle Dokument der Version 2, geändert am ersten Profil.</summary>
    public static string WithProfileV2(Action<JsonObject> change)
    {
        var document = JsonNode.Parse(FullDocumentV2)!.AsObject();
        change(document["profiles"]![0]!.AsObject());
        return document.ToJsonString();
    }

    /// <summary>Das volle Dokument der Version 2, geändert an der Wurzel.</summary>
    public static string WithRootV2(Action<JsonObject> change)
    {
        var document = JsonNode.Parse(FullDocumentV2)!.AsObject();
        change(document);
        return document.ToJsonString();
    }

    /// <summary>Das volle Dokument, geändert am ersten Profil.</summary>
    public static string WithProfile(Action<JsonObject> change)
    {
        var document = JsonNode.Parse(FullDocument)!.AsObject();
        change(document["profiles"]![0]!.AsObject());
        return document.ToJsonString();
    }

    /// <summary>Das volle Dokument, geändert an der Wurzel.</summary>
    public static string WithRoot(Action<JsonObject> change)
    {
        var document = JsonNode.Parse(FullDocument)!.AsObject();
        change(document);
        return document.ToJsonString();
    }
}
