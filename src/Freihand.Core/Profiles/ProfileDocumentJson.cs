using System.Text.Json.Serialization;

namespace Freihand.Core.Profiles;

// Abbild des JSON-Formats, Version 2 (schemas/profiles.v2.schema.json). Alle Felder sind nullable, damit die
// Prüfung fehlende Angaben mit Pfad melden kann, statt an einer Ausnahme des Serialisierers zu scheitern
// (OF-01). Die Reihenfolge der Eigenschaften bestimmt die Reihenfolge im Export. Dokumente der Version 1 werden
// mit demselben Abbild gelesen; die Felder ab Version 2 weist die Prüfung dort vorher ab (ProfileSerializer).

internal sealed class ProfileDocumentJson
{
    public string? Kind { get; set; }

    public int? SchemaVersion { get; set; }

    public List<ProfileJson?>? Profiles { get; set; }
}

internal sealed class ProfileJson
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public RouteStepJson? Recognition { get; set; }

    public TransformStepJson? Transform { get; set; }

    public string? DataClass { get; set; }

    public string? Language { get; set; }

    public string? RecognitionMode { get; set; }

    public string? RecordingMode { get; set; }

    public AudioSourceJson? AudioSource { get; set; }

    public string? Output { get; set; }

    public string? FormatRules { get; set; }

    public string? Strictness { get; set; }

    public List<string?>? Vocabulary { get; set; }

    public List<string?>? Programs { get; set; }
}

internal sealed class RouteStepJson
{
    public string? Route { get; set; }

    public string? Endpoint { get; set; }
}

internal sealed class TransformStepJson
{
    public string? Route { get; set; }

    public string? Endpoint { get; set; }

    public string? Style { get; set; }
}

internal sealed class AudioSourceJson
{
    public string? Kind { get; set; }

    public string? DeviceId { get; set; }

    public string? DisplayName { get; set; }
}

/// <summary>
/// Quellgenerator für System.Text.Json (Technikentscheid, Konfiguration). Unbekannte Felder werden
/// abgewiesen, Gross- und Kleinschreibung zählt, Zahlen in Anführungszeichen gelten nicht (OF-01).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict,
    PropertyNameCaseInsensitive = false,
    MaxDepth = DataSafety.MaxDepth)]
[JsonSerializable(typeof(ProfileDocumentJson))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
