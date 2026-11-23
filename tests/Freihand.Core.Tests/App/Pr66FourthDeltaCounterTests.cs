using Freihand.Core.Commands;
using Freihand.Core.Live;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.App;

public sealed class Pr66FourthDeltaCounterTests
{
    [Theory]
    [InlineData("neue", "Zeile")]
    [InlineData("neuer", "Absatz")]
    public void LearningDiscardsAGapContainingASpokenBreak(string first, string second)
    {
        var listener = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());
        var result = Assert.IsType<InsertText>(Assert.Single(listener.Interpret(
            Utterance(0, "Heute", "habe", "ich", "Signal", first, second, "Rot", "erwähnt."),
            CommandScope.Everywhere)));

        Assert.Contains('\n', result.Segment.Text);
        Assert.Null(WordCandidates.InGap("Heute habe ich … erwähnt.", result.Segment.Text));
    }
}