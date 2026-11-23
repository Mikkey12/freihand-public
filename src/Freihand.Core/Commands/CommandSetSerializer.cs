using Freihand.Core.Profiles;

namespace Freihand.Core.Commands;

/// <summary>
/// Import und Export der Befehlsliste nach OF-01: versioniertes Schema, Prüfung beim Import, keine
/// ausführbaren Inhalte, keine Geheimnisse. Ein Import gilt ganz oder gar nicht.
/// </summary>
public static partial class CommandSetSerializer
{
    /// <summary>Liest und prüft eine Befehlsliste.</summary>
    public static ImportResult<CommandSet> Import(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var errors = new ImportErrors();
        using var document = DataSafety.ParseEnvelope(
            json, CommandSchema.Kind, CommandSchema.CurrentVersion, CommandSchema.CurrentVersion, errors, out _);
        if (document is null)
        {
            return ImportResult<CommandSet>.Failure(errors.All);
        }

        // Optional heisst weglassbar: ein vorhandenes „text“ mit null wird wie im JSON-Schema abgewiesen.
        RejectExplicitNulls(document.RootElement, errors);
        if (errors.Any)
        {
            return ImportResult<CommandSet>.Failure(errors.All);
        }

        var data = DataSafety.Deserialize(document, CommandJsonContext.Default.CommandDocumentJson, errors);
        if (data is null)
        {
            return ImportResult<CommandSet>.Failure(errors.All);
        }

        if (data.Commands is null)
        {
            errors.Add("$.commands", "Fehlt.");
            return ImportResult<CommandSet>.Failure(errors.All);
        }

        if (data.Commands.Count > CommandSchema.MaxCommands)
        {
            errors.Add("$.commands", $"Höchstens {CommandSchema.MaxCommands} Einträge je Dokument.");
            return ImportResult<CommandSet>.Failure(errors.All);
        }

        var commands = new List<CommandDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var phrases = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < data.Commands.Count; index++)
        {
            var path = $"$.commands[{index}]";
            var command = ReadCommand(data.Commands[index], path, errors);
            if (command is null)
            {
                continue;
            }

            if (!ids.Add(command.Id))
            {
                errors.Add(path + ".id", $"„{command.Id}“ kommt mehrfach vor.");
            }

            // Grundentscheid 5: Eine Wendung darf nur eine Wirkung haben, sonst ist die Erkennung mehrdeutig.
            for (var phraseIndex = 0; phraseIndex < command.Phrases.Count; phraseIndex++)
            {
                var key = CommandSchema.NormalizePhrase(command.Phrases[phraseIndex]);
                if (!phrases.TryAdd(key, command.Id))
                {
                    errors.Add(
                        $"{path}.phrases[{phraseIndex}]",
                        $"„{command.Phrases[phraseIndex]}“ ist schon bei „{phrases[key]}“ vergeben.");
                }
            }

            commands.Add(command);
        }

        return errors.Any
            ? ImportResult<CommandSet>.Failure(errors.All)
            : ImportResult<CommandSet>.Success(new CommandSet(commands));
    }

    /// <summary>Schreibt eine Befehlsliste als Dokument (OF-01).</summary>
    public static string Export(CommandSet commandSet)
    {
        ArgumentNullException.ThrowIfNull(commandSet);

        var document = new CommandDocumentJson
        {
            Kind = CommandSchema.Kind,
            SchemaVersion = CommandSchema.CurrentVersion,
            Commands = [.. commandSet.Commands.Select(ToJson)],
        };
        return DataSafety.Serialize(document, CommandJsonContext.Default.CommandDocumentJson);
    }

    private static void RejectExplicitNulls(System.Text.Json.JsonElement root, ImportErrors errors)
    {
        if (!root.TryGetProperty("commands", out var commands) || commands.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var command in commands.EnumerateArray())
        {
            DataSafety.RejectExplicitNulls(command, $"$.commands[{index++}]", errors, "text");
        }
    }

    private static CommandDefinition? ReadCommand(CommandJson? json, string path, ImportErrors errors)
    {
        if (json is null)
        {
            errors.Add(path, "Ein Eintrag muss ein Objekt sein.");
            return null;
        }

        var before = errors.All.Count;

        DataSafety.CheckId(errors, path + ".id", json.Id);
        var commandClass = DataSafety.ReadEnum(CommandSchema.Classes, json.Class, path + ".class", errors);
        ReadPhrases(json.Phrases, path + ".phrases", errors);
        var scope = DataSafety.ReadEnum(CommandSchema.Scopes, json.Scope, path + ".scope", errors);
        var effect = DataSafety.ReadEnum(CommandSchema.Effects, json.Effect, path + ".effect", errors);
        DataSafety.CheckText(errors, path + ".description", json.Description, CommandSchema.MaxDescriptionLength, required: true);

        if (effect is { } knownEffect)
        {
            if (commandClass is { } knownClass && knownClass != CommandSchema.ClassOf(knownEffect))
            {
                errors.Add(path + ".class", knownClass == CommandClass.Action
                    ? "Aktionen sind im Pilot nicht vorgesehen (Anhang A, SI-09)."
                    : $"Die Wirkung „{json.Effect}“ gehört zur Klasse „{CommandSchema.Classes.NameOf(CommandSchema.ClassOf(knownEffect))}“ (Grundentscheid 5).");
            }

            if (scope is { } knownScope && CommandSchema.RequiredScope(knownEffect) is { } required && knownScope != required)
            {
                errors.Add(path + ".scope", required == CommandScope.DictationWindow
                    ? $"Die Wirkung „{json.Effect}“ gilt nur im Diktatfenster; ausserhalb wäre sie eine Aktion (Anhang A, SI-09)."
                    : $"Die Wirkung „{json.Effect}“ muss überall gelten (BA-22, BE-06).");
            }

            ReadText(json.Text, knownEffect, path + ".text", errors);
        }

        if (errors.All.Count > before)
        {
            return null;
        }

        return new CommandDefinition(
            json.Id!,
            commandClass!.Value,
            json.Phrases!.Select(phrase => phrase!),
            scope!.Value,
            effect!.Value,
            json.Text,
            json.Description!);
    }

    private static void ReadPhrases(List<string?>? phrases, string path, ImportErrors errors)
    {
        if (phrases is null || phrases.Count == 0)
        {
            errors.Add(path, "Mindestens eine gesprochene Wendung ist nötig.");
            return;
        }

        if (phrases.Count > CommandSchema.MaxPhrases)
        {
            errors.Add(path, $"Höchstens {CommandSchema.MaxPhrases} Wendungen je Eintrag.");
            return;
        }

        for (var index = 0; index < phrases.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            var phrase = phrases[index];
            var before = errors.All.Count;
            DataSafety.CheckText(errors, itemPath, phrase, CommandSchema.MaxPhraseLength, required: true);
            if (errors.All.Count == before && !CommandSchema.IsPhrase(phrase!))
            {
                errors.Add(itemPath, "Keine sprechbare Wendung (nur Buchstaben, einzelne Leerzeichen, Bindestriche, Apostrophe).");
            }
        }
    }

    private static void ReadText(string? text, CommandEffect effect, string path, ImportErrors errors)
    {
        if (effect != CommandEffect.InsertPunctuation)
        {
            if (text is not null)
            {
                errors.Add(path, "Nur ein Satzzeichen trägt einen Text.");
            }

            return;
        }

        // BA-20: Ein Diktatzeichen erzeugt nur ein Zeichen aus der festen Liste, nie Tasten oder Steuerzeichen.
        if (text is null || !CommandSchema.Punctuation.Contains(text))
        {
            errors.Add(path, $"Erlaubt sind nur die Satzzeichen {string.Join(" ", CommandSchema.Punctuation)}.");
        }
    }

    private static CommandJson ToJson(CommandDefinition command) => new()
    {
        Id = command.Id,
        Class = CommandSchema.Classes.NameOf(command.Class),
        Phrases = [.. command.Phrases],
        Scope = CommandSchema.Scopes.NameOf(command.Scope),
        Effect = CommandSchema.Effects.NameOf(command.Effect),
        Text = command.Text,
        Description = command.Description,
    };
}
