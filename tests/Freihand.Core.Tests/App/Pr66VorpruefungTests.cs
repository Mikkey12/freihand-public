using Freihand.Core.Commands;
using Freihand.Core.Live;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Eigene Vorprüfung zu PR #66 (Subagent, 18.11.2026), als feste Tests: Gelernt wird genau die Form, die das Ersetzen sieht
/// (Lernen ohne Formatter, <see cref="Freihand.Adapters.Dictation.RecognizerDictationFactory.ForLearning"/>), und eine
/// gehörte Folge passt nur ohne Satzzeichen innen.
/// </summary>
public sealed class Pr66VorpruefungTests
{
    // Lernen prüft die Äusserung, wie die Erkennung sie liefert (Regel 4)
    private static string Learned(string spelling, params string[] heardWords) =>
        Assert.Single(WordCandidates.FromUtterances(spelling, [Utterance(0, heardWords)], BuiltInCommands.Pilot, _ => false)
            .Candidates).Text;

    private static string Dictate(LearnedWord word, params string[] words)
    {
        var options = Interpreting(formatter: Formatter()) with { Replacements = WordReplacements.From([word]) };
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, options);
        return Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
            Utterance(0, words), CommandScope.Everywhere))).Segment.Text;
    }

    [Fact]
    public void AWordLearnedWithSharpSIsReplacedWhenWhisperHearsItTheSameWayAgain()
    {
        var heard = Learned("Maßmann", "Heute", "habe", "ich", "Maßman", "erwähnt.");
        Assert.Equal("Maßmann", Dictate(new LearnedWord("Maßmann", [heard]), "Maßman"));
    }

    [Fact]
    public void AWordLearnedFromANumberWordIsReplacedWhenWhisperHearsItTheSameWayAgain()
    {
        var heard = Learned("Dreissigacker", "Heute", "habe", "ich", "dreissig", "Acker", "erwähnt.");
        Assert.Equal("Dreissigacker", Dictate(new LearnedWord("Dreissigacker", [heard]), "dreissig", "Acker"));
    }

    [Fact]
    public void AReplacementDoesNotSwallowASentenceEndInsideTheHeardSequence()
    {
        Assert.Equal("Es ist Mass. Man weiss es.",
            Dictate(new LearnedWord("Maßmann", ["Mass man"]), "Es", "ist", "Mass.", "Man", "weiss", "es."));
    }

    [Fact]
    public void AReplacementDoesNotSwallowACommaOrAnOpeningQuoteInside()
    {
        Assert.Equal("Blüt, Park", Dictate(new LearnedWord("Bluetenpark", ["Blüt Park"]), "Blüt,", "Park"));
        Assert.Equal("Blüt „Park“", Dictate(new LearnedWord("Bluetenpark", ["Blüt Park"]), "Blüt", "„Park“"));
        Assert.Equal("im Bluetenpark.", Dictate(new LearnedWord("Bluetenpark", ["Blüt Park"]), "im", "Blüt", "Park."));
    }

    [Fact]
    public void TheLearningFactoryHasNeitherFormatterNorReplacements()
    {
        var interpreting = Interpreting(formatter: Formatter()) with
        {
            Replacements = WordReplacements.From([new LearnedWord("Maßmann", ["Masman"])]),
        };
        var factory = new Freihand.Adapters.Dictation.RecognizerDictationFactory(
            new FakeRecognizerHost(), Freihand.Core.Profiles.BuiltInProfiles.Standard, null, Freihand.Core.Profiles.LiveApprovals.None,
            DefaultRecognition, Options(LiveMode.Sentence), BuiltInCommands.Pilot, interpreting)
        {
            Replacements = () => WordReplacements.From([new LearnedWord("Maßmann", ["Masman"])]),
        };

        var learning = factory.ForLearning();

        Assert.Null(learning.InterpreterOptions.Formatter);
        Assert.Equal(0, learning.InterpreterOptions.Replacements.Count);
        Assert.Null(learning.Replacements);
        Assert.True(learning.ReportsHeard);
        Assert.False(factory.ReportsHeard);
        Assert.NotNull(factory.InterpreterOptions.Formatter);
    }
}
