using Freihand.Core.Commands;
using Freihand.Core.Live;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.App;

public sealed class Pr66ThirdDeltaCounterTests
{
    [Fact]
    public void APunctuationRejectedLongMatchDoesNotHideTheValidShortMatch()
    {
        var options = Interpreting() with
        {
            Replacements = WordReplacements.From(
                [new LearnedWord("Langziel", ["Signal Rot"]), new LearnedWord("Kurzziel", ["Signal"])]),
        };
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, options);
        var step = Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
            Utterance(0, "Signal", ",", "Rot"), CommandScope.Everywhere)));
        Assert.Equal("Kurzziel, Rot", step.Segment.Text);
    }

    [Fact]
    public void LearningDoesNotEraseInnerPunctuationFromAnUnmatchableGap()
    {
        const string prompt = "Heute habe ich … erwähnt.";
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());
        var result = Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
            Utterance(0, "Heute", "habe", "ich", "Signal", ",", "Rot", "erwähnt."), CommandScope.Everywhere)));
        Assert.Null(WordCandidates.InGap(prompt, result.Segment.Text));
    }
}