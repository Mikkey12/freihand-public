using System.Text.Json.Nodes;
using Freihand.Core.Commands;
using Freihand.Core.Tests.Profiles;

namespace Freihand.Core.Tests.Commands;

/// <summary>Die Pilot-Befehlsliste entspricht Anhang A (Grundentscheid 5, BA-20, BA-22).</summary>
public class BuiltInCommandsTests
{
    /// <summary>Anhang A Zeile für Zeile: gesprochen, Klasse, Geltungsbereich, Wirkung, Zeichen.</summary>
    public static TheoryData<string, CommandClass, CommandScope, CommandEffect, string?> AppendixA => new()
    {
        { "Komma", CommandClass.DictationMark, CommandScope.Everywhere, CommandEffect.InsertPunctuation, "," },
        { "Punkt", CommandClass.DictationMark, CommandScope.Everywhere, CommandEffect.InsertPunctuation, "." },
        { "Fragezeichen", CommandClass.DictationMark, CommandScope.Everywhere, CommandEffect.InsertPunctuation, "?" },
        { "Ausrufezeichen", CommandClass.DictationMark, CommandScope.Everywhere, CommandEffect.InsertPunctuation, "!" },
        { "Doppelpunkt", CommandClass.DictationMark, CommandScope.Everywhere, CommandEffect.InsertPunctuation, ":" },
        { "neue Zeile", CommandClass.DictationMark, CommandScope.Everywhere, CommandEffect.LineBreak, null },
        { "neuer Absatz", CommandClass.DictationMark, CommandScope.Everywhere, CommandEffect.ParagraphBreak, null },
        { "wörtlich schreiben", CommandClass.Command, CommandScope.Everywhere, CommandEffect.EnterLiteralMode, null },
        { "letztes Wort löschen", CommandClass.Command, CommandScope.DictationWindow, CommandEffect.DeleteLastWord, null },
        { "letzten Satz löschen", CommandClass.Command, CommandScope.DictationWindow, CommandEffect.DeleteLastSentence, null },
        { "rückgängig", CommandClass.Command, CommandScope.DictationWindow, CommandEffect.Undo, null },
        { "übernehmen", CommandClass.Command, CommandScope.DictationWindow, CommandEffect.Commit, null },
        { "neu binden", CommandClass.Command, CommandScope.Everywhere, CommandEffect.Rebind, null },
        { "abbrechen", CommandClass.Command, CommandScope.Everywhere, CommandEffect.Cancel, null },
    };

    [Theory]
    [MemberData(nameof(AppendixA))]
    public void ContainsEachLineOfAppendixA(string phrase, CommandClass commandClass, CommandScope scope, CommandEffect effect, string? text)
    {
        var command = Assert.Single(BuiltInCommands.Pilot.Commands, candidate => candidate.Phrases.Contains(phrase));

        Assert.Equal(commandClass, command.Class);
        Assert.Equal(scope, command.Scope);
        Assert.Equal(effect, command.Effect);
        Assert.Equal(text, command.Text);
    }

    [Fact]
    public void ContainsNothingBeyondAppendixA()
    {
        Assert.Equal(AppendixA.Count, BuiltInCommands.Pilot.Commands.Count);
    }

    [Fact]
    public void HasNoActionsInThePilot()
    {
        // Anhang A: Aktionen folgen ab dem Kern mit eigener Freigabe (SI-09).
        Assert.Empty(BuiltInCommands.Pilot.OfClass(CommandClass.Action));
    }

    [Fact]
    public void LiteralModeCanBeEnteredButNotLeftBySpeech()
    {
        // BA-22: „wörtlich schreiben“ gilt überall; aus nur über die Befehlstaste.
        var literal = Assert.Single(BuiltInCommands.Pilot.Commands, command => command.Effect == CommandEffect.EnterLiteralMode);
        Assert.Equal(CommandScope.Everywhere, literal.Scope);
        Assert.DoesNotContain(Enum.GetNames<CommandEffect>(), name => name.Contains("Exit", StringComparison.Ordinal) || name.Contains("Leave", StringComparison.Ordinal));
    }

    [Fact]
    public void DictationMarksOnlyProduceCharacters()
    {
        // BA-20: Diktatzeichen erzeugen nur Zeichen im Text, nie Tasten oder Aktionen.
        Assert.All(BuiltInCommands.Pilot.OfClass(CommandClass.DictationMark), command =>
            Assert.Contains(command.Effect, new[] { CommandEffect.InsertPunctuation, CommandEffect.LineBreak, CommandEffect.ParagraphBreak }));
    }

    [Fact]
    public void ApplicableCommandsDependOnTheScope()
    {
        // BE-16: Die Einblendung entsteht aus denselben Definitionen.
        var everywhere = BuiltInCommands.Pilot.ApplicableIn(CommandScope.Everywhere).Select(command => command.Id).ToList();
        var window = BuiltInCommands.Pilot.ApplicableIn(CommandScope.DictationWindow).Select(command => command.Id).ToList();

        Assert.DoesNotContain("commit", everywhere);
        Assert.Contains("commit", window);
        Assert.Contains("cancel", everywhere);
        Assert.Equal(BuiltInCommands.Pilot.Commands.Count, window.Count);
    }

    [Fact]
    public void EveryCommandHasADescriptionForTheOverlay()
    {
        Assert.All(BuiltInCommands.Pilot.Commands, command => Assert.False(string.IsNullOrWhiteSpace(command.Description)));
    }

    [Fact]
    public void ConformsToTheJsonSchema()
    {
        var validator = new MiniSchemaValidator(RepositoryFiles.ReadSchema("commands.v1.schema.json"));

        Assert.Empty(validator.Validate(JsonNode.Parse(BuiltInCommands.Json)));
    }
}
