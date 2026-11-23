using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Segments;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>Auslegung je ganzer Äusserung: Diktatzeichen, Befehle, Befehlswort, Literalmodus (Kapitel 5.6).</summary>
public sealed class InterpreterTests
{
    private const CommandScope Window = CommandScope.DictationWindow;

    [Fact]
    public void T13_SpokenCommaBecomesOneComma()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());

        // „Komma“ wird „,“
        Assert.Equal("Das ist gut, danke", InsertedText(interpreter, Utterance(0, "Das", "ist", "gut", "Komma", "danke")));

        // Nach einem vom Modell gesetzten Komma ergibt „Komma“ eines, nicht zwei
        Assert.Equal("Das ist gut, danke", InsertedText(interpreter, Utterance(5, "Das", "ist", "gut,", "Komma", "danke")));
    }

    [Fact]
    public void T13_MaintainedVariantCountsAsComma()
    {
        var options = Interpreting() with { PhraseVariants = new Dictionary<string, string> { ["Koma"] = "Komma" } };
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, options);

        Assert.Equal("Das ist gut, danke", InsertedText(interpreter, Utterance(0, "Das", "ist", "gut", "Koma", "danke")));
    }

    [Fact]
    public void T14_LiteralModeWritesCommaAsWordAndRunsNoCommand()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting("Pilotwort"));
        interpreter.PressCommandKey(Sec(0));
        var enter = interpreter.Interpret(Utterance(0.5, "wörtlich", "schreiben"), Window);
        Assert.Equal(CommandEffect.EnterLiteralMode, Assert.IsType<RunCommand>(Assert.Single(enter)).Command.Effect);
        Assert.True(interpreter.LiteralMode);

        Assert.Equal("Komma", InsertedText(interpreter, Utterance(3, "Komma")));
        Assert.Equal("fünf Komma zwei", InsertedText(interpreter, Utterance(5, "fünf", "Komma", "zwei")));

        // Auch ein Befehlswort führt im Literalmodus nichts aus
        Assert.Equal("Pilotwort rückgängig", InsertedText(interpreter, Utterance(8, "Pilotwort", "rückgängig")));
        Assert.True(interpreter.LiteralMode);
    }

    [Fact]
    public void T15_CommandOnlyAfterKeyOrCommandWordAtStart()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting("Pilotwort"));

        // Ohne Taste und ohne Befehlswort: Text
        Assert.Equal("rückgängig", InsertedText(interpreter, Utterance(0, "rückgängig")));

        // Nach der Befehlstaste: Befehl
        interpreter.PressCommandKey(Sec(2));
        Assert.Equal(CommandEffect.Undo, SingleCommand(interpreter.Interpret(Utterance(2.5, "rückgängig"), Window)));

        // Befehlswort am Äusserungsbeginn: Befehl
        Assert.Equal(CommandEffect.Undo, SingleCommand(interpreter.Interpret(Utterance(5, "Pilotwort", "rückgängig"), Window)));

        // Befehlswort mitten im Satz: Text
        Assert.Equal(
            "Bitte Pilotwort rückgängig",
            InsertedText(interpreter, Utterance(8, "Bitte", "Pilotwort", "rückgängig")));
    }

    [Fact]
    public void T16_WithoutActivationCommandPhraseIsText()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());

        Assert.Equal("letztes Wort löschen", InsertedText(interpreter, Utterance(0, "letztes", "Wort", "löschen")));
    }

    [Fact]
    public void T16_ArmedOutsideDictationWindowGivesOnlyAHint()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());
        interpreter.PressCommandKey(Sec(0));

        // „letztes Wort löschen“ gilt nur im Diktatfenster; ausserhalb: Hinweis, keine Ausführung, kein Text
        var steps = interpreter.Interpret(Utterance(0.5, "letztes", "Wort", "löschen"), CommandScope.Everywhere);

        Assert.IsType<CommandHint>(Assert.Single(steps));
    }

    [Fact]
    public void T37_CommandWordInsideASentenceStaysText()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting("Pilotwort"));

        Assert.Equal(
            "Die Kennung lautet Pilotwort übernehmen",
            InsertedText(interpreter, Utterance(0, "Die", "Kennung", "lautet", "Pilotwort", "übernehmen")));
    }

    [Fact]
    public void T37_CommandWordAsOwnUtteranceGivesExactlyOneCommit()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting("Pilotwort"));

        var steps = interpreter.Interpret(Utterance(0, "Pilotwort", "übernehmen"), Window);

        Assert.Equal(CommandEffect.Commit, SingleCommand(steps));
    }

    [Fact]
    public void T38_CommandKeyEndsLiteralModeWithoutArming()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());
        interpreter.PressCommandKey(Sec(0));
        interpreter.Interpret(Utterance(0.5, "wörtlich", "schreiben"), Window);
        Assert.True(interpreter.LiteralMode);

        // Im Literalmodus bleibt „Komma“ Wort, und „wörtlich schreiben“ bewirkt nichts
        Assert.Equal("Komma", InsertedText(interpreter, Utterance(3, "Komma")));
        Assert.Equal("wörtlich schreiben", InsertedText(interpreter, Utterance(5, "wörtlich", "schreiben")));
        Assert.True(interpreter.LiteralMode);

        // Die Taste beendet den Modus und schaltet nichts scharf: „Komma“ ergibt danach genau ein „,“
        interpreter.PressCommandKey(Sec(7));
        Assert.Equal(",", InsertedText(interpreter, Utterance(8, "Komma")));
        Assert.False(interpreter.LiteralMode);
    }

    [Fact]
    public void T48_KeyExpiresWhenNoUtteranceBeginsWithinArmTimeout()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());
        interpreter.PressCommandKey(Sec(1));

        // ArmTimeout 5 s: die nächste Äusserung beginnt erst bei 6,5 s, also verfallen; sie wird Text
        Assert.Equal("letztes Wort löschen", InsertedText(interpreter, Utterance(6.5, "letztes", "Wort", "löschen")));
    }

    [Fact]
    public void T48_ArmedUtteranceWithoutMatchingPhraseGivesHintWithTheWords()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());
        interpreter.PressCommandKey(Sec(1));
        var utterance = Utterance(1.5, "bitte", "Kaffee", "kochen");

        var hint = Assert.IsType<CommandHint>(Assert.Single(interpreter.Interpret(utterance, Window)));

        Assert.Equal(["bitte", "Kaffee", "kochen"], hint.Utterance.Words.Select(word => word.Text));
    }

    /// <summary>Genau ein <see cref="InsertText"/>, fest und mit der Kennung der Äusserung (Kapitel 4, T41).</summary>
    private static string InsertedText(DictationInterpreter interpreter, Utterance utterance)
    {
        var steps = interpreter.Interpret(utterance, Window);
        var insert = Assert.IsType<InsertText>(Assert.Single(steps));
        Assert.Equal(SegmentState.Fixed, insert.Segment.State);
        Assert.Equal(utterance.Id, insert.Segment.Id);
        return insert.Segment.Text;
    }

    private static CommandEffect SingleCommand(IReadOnlyList<DictationStep> steps) =>
        Assert.IsType<RunCommand>(Assert.Single(steps)).Command.Effect;
}
