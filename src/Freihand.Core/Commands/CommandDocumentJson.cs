using System.Text.Json.Serialization;
using Freihand.Core.Profiles;

namespace Freihand.Core.Commands;

// Abbild des JSON-Formats (schemas/commands.v1.schema.json). Alle Felder sind nullable, damit die Prüfung
// fehlende Angaben mit Pfad melden kann (OF-01). Die Reihenfolge bestimmt die Reihenfolge im Export.

internal sealed class CommandDocumentJson
{
    public string? Kind { get; set; }

    public int? SchemaVersion { get; set; }

    public List<CommandJson?>? Commands { get; set; }
}

internal sealed class CommandJson
{
    public string? Id { get; set; }

    public string? Class { get; set; }

    public List<string?>? Phrases { get; set; }

    public string? Scope { get; set; }

    public string? Effect { get; set; }

    public string? Text { get; set; }

    public string? Description { get; set; }
}

/// <summary>Quellgenerator für System.Text.Json, mit denselben strengen Regeln wie für Profile (OF-01).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict,
    PropertyNameCaseInsensitive = false,
    MaxDepth = DataSafety.MaxDepth)]
[JsonSerializable(typeof(CommandDocumentJson))]
internal sealed partial class CommandJsonContext : JsonSerializerContext;
