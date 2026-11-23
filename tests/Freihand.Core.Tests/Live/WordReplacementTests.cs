using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Segments;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>
/// „Wort lernen“ (P11 S4, Entscheid vom 18.11.2026): nur Ersetzungen nach der Erkennung, ganze Wortfolgen, Satzzeichen am
/// Rand bleiben, nicht im Literalmodus und nicht in Befehlen.
/// </summary>
public sealed class WordReplacementTests
{
    private static DictationInterpreter With(params LearnedWord[] words) =>
        new(BuiltInCommands.Pilot, Interpreting() with { Replacements = WordReplacements.From(words) });

    private static LearnedWord Word(string spelling, params string[] heard) => new(spelling, heard);

    private static string InsertedText(DictationInterpreter interpreter, Utterance utterance) =>
        Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(utterance, CommandScope.DictationWindow))).Segment.Text;

    [Fact]
    public void AHeardSpellingIsReplacedAndKeepsItsPunctuation()
    {
        var interpreter = With(Word("Nivella", "Niwela"), Word("Nilo", "Mark"));

        Assert.Equal("Paul und Nivella fahren nach Zürich.",
            InsertedText(interpreter, Utterance(0, "Paul", "und", "Niwela", "fahren", "nach", "Zürich.")));
        Assert.Equal("Nilo, bitte.", InsertedText(interpreter, Utterance(5, "Mark,", "bitte.")));
        Assert.Equal("„Nivella“ kommt", InsertedText(interpreter, Utterance(9, "„Niwela“", "kommt")));
    }

    [Fact]
    public void ComparisonIgnoresCaseAndSeparatePunctuationTokens()
    {
        var interpreter = With(Word("Nivella", "Niwela"));

        Assert.Equal("mit Nivella.", InsertedText(interpreter, Utterance(0, "mit", "niwela", ".")));
    }

    [Fact]
    public void OnlyWholeWordsAreReplaced()
    {
        var interpreter = With(Word("Nilo", "Mark"));

        Assert.Equal("Die Markthalle und das Knochenmark",
            InsertedText(interpreter, Utterance(0, "Die", "Markthalle", "und", "das", "Knochenmark")));
    }

    [Fact]
    public void TheLongestHeardSequenceWins()
    {
        var interpreter = With(Word("Bluetenpark", "Blüt Park", "Blütpark"), Word("Parkplatz", "Park"));

        Assert.Equal("im Bluetenpark, beim Parkplatz",
            InsertedText(interpreter, Utterance(0, "im", "Blüt", "Park,", "beim", "Park")));
        Assert.Equal("im Bluetenpark", InsertedText(interpreter, Utterance(5, "im", "Blütpark")));
    }

    [Fact]
    public void TheSameHeardSpellingForTwoWordsKeepsTheFirst()
    {
        var replacements = WordReplacements.From([Word("Nivella", "Niwela"), Word("Nevina", "Niwela")]);

        Assert.Equal(1, replacements.Count);
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting() with { Replacements = replacements });
        Assert.Equal("Nivella", InsertedText(interpreter, Utterance(0, "Niwela")));
    }

    // Nur genau die Schreibweise (bis auf Randzeichen) ist keine Ersetzung; Gross-/Kleinschreibung zählt
    [Fact]
    public void OnlyTheExactSpellingIsNoReplacementAndCaseCounts()
    {
        Assert.Equal(0, WordReplacements.From([Word("Nilo", "Nilo.", " Nilo ")]).Count);

        var interpreter = With(Word("iPhone", "Iphone"));
        Assert.Equal("Mein iPhone, dein iPhone.", InsertedText(interpreter, Utterance(0, "Mein", "Iphone,", "dein", "iPhone.")));
    }

    // Eigene Vorprüfung zu PR #66: innen am Wortanfang gehört nur ein Punkt zum Wort; ein Komma trennt
    [Fact]
    public void AnInnerCommaAtAWordStartIsNeverSwallowed()
    {
        var interpreter = With(Word("Signalrot", "Signal Rot"), Word("Zielname", "Visual .NET"));

        Assert.Equal("Das Signal,Rot leuchtet.", InsertedText(interpreter, Utterance(0, "Das", "Signal", ",Rot", "leuchtet.")));
        Assert.Equal("mit Zielname", InsertedText(interpreter, Utterance(5, "mit", "Visual", ".NET")));
    }

    [Fact]
    public void DictationMarksStayCommandsAndTheyAreNotReplaced()
    {
        // „Komma“ bleibt Diktatzeichen, auch wenn es als Fehlschreibung eingetragen ist
        var interpreter = With(Word("Komma-Gruppe", "Komma"));

        Assert.Equal("gut, danke", InsertedText(interpreter, Utterance(0, "gut", "Komma", "danke")));
    }

    [Fact]
    public void AHeardSequenceContainingADictationMarkIsNotReplaced()
    {
        var interpreter = With(Word("Grüessli", "Grüss Komma"));

        Assert.Equal("Grüss, Paul", InsertedText(interpreter, Utterance(0, "Grüss", "Komma", "Paul")));
    }

    [Fact]
    public void LiteralModeWritesWhatWasHeard()
    {
        var interpreter = With(Word("Nivella", "Niwela"));
        interpreter.PressCommandKey(Sec(0));
        var enter = interpreter.Interpret(Utterance(0.5, "wörtlich", "schreiben"), CommandScope.DictationWindow);
        Assert.Equal(CommandEffect.EnterLiteralMode, Assert.IsType<RunCommand>(Assert.Single(enter)).Command.Effect);

        Assert.Equal("Niwela", InsertedText(interpreter, Utterance(2, "Niwela")));
    }

    [Fact]
    public void WithoutLearnedWordsTheTextIsUnchanged()
    {
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());

        Assert.Equal("Paul und Niwela", InsertedText(interpreter, Utterance(0, "Paul", "und", "Niwela")));
    }
}
