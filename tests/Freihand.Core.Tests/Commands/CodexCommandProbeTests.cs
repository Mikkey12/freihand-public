using System.Text.Json.Nodes;
using Freihand.Core.Commands;
using Freihand.Core.Tests.Profiles;

namespace Freihand.Core.Tests.Commands;

/// <summary>
/// Prüftests von Codex für Befehle (Branch <c>codex/pruefproben</c>, Paket P2), mit unveränderten
/// Erwartungen übernommen: Review R8, Nachprüfung N2 (Anhang A, SI-09, SI-03).
/// </summary>
public sealed class CodexCommandProbeTests
{
    // Review R8
    [Fact]
    public void Review_ImportedDeletionCannotApplyEverywhere()
    {
        const string json = """
        {"kind":"freihand.commands","schemaVersion":1,"commands":[
          {"id":"delete-word","class":"command","phrases":["letztes Wort löschen"],
           "scope":"everywhere","effect":"deleteLastWord","description":"Letztes Wort löschen"}
        ]}
        """;
        Assert.False(CommandSetSerializer.Import(json).Succeeded);
    }

    // Nachprüfung N2
    [Fact]
    public void OrdinaryTaskIdentifierIsAcceptedForCommands()
    {
        var root = JsonNode.Parse(BuiltInCommands.Json)!;
        root["commands"]![0]!["id"] = "task-besprechungsnotizen";
        var result = CommandSetSerializer.Import(root.ToJsonString());
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
    }

    // Review R8
    [Fact]
    public void EveryEffectScopeCombinationAgreesBetweenSchemaAndImporter()
    {
        var validator = new MiniSchemaValidator(RepositoryFiles.ReadSchema("commands.v1.schema.json"));
        foreach (var effect in Enum.GetValues<CommandEffect>())
        foreach (var scope in Enum.GetValues<CommandScope>())
        {
            var original = JsonNode.Parse(BuiltInCommands.Json)!;
            var command = original["commands"]!.AsArray().First(c => c!["effect"]!.GetValue<string>() == CommandSchema.Effects.NameOf(effect))!.DeepClone();
            command["scope"] = CommandSchema.Scopes.NameOf(scope);
            original["commands"] = new JsonArray(command);
            bool expected = effect switch
            {
                CommandEffect.DeleteLastWord or CommandEffect.DeleteLastSentence or CommandEffect.Undo or CommandEffect.Commit => scope == CommandScope.DictationWindow,
                CommandEffect.EnterLiteralMode or CommandEffect.Cancel => scope == CommandScope.Everywhere,
                _ => true,
            };
            Assert.Equal(expected, validator.IsValid(original));
            Assert.Equal(expected, CommandSetSerializer.Import(original.ToJsonString()).Succeeded);
        }
    }
}
