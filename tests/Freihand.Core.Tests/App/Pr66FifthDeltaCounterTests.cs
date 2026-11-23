using Freihand.Core.Commands;
using Freihand.Core.Live;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.App;

public sealed class Pr66FifthDeltaCounterTests
{
    [Fact]
    public void LearningMustValidateTheOriginalWordBoundaries()
    {
        var commands = BuiltInCommands.Pilot;
        var original = Utterance(0, "Heute", "habe", "ich", "Visual", ".NET", "erwähnt.");
        var learning = new DictationInterpreter(commands, Interpreting());
        var heard = Assert.IsType<InsertText>(Assert.Single(
            learning.Interpret(original, CommandScope.Everywhere))).Segment.Text;

        // Angepasst (einzige Änderung): Nach dem Soll dieser Gegenprobe prüft das Lernen die ursprüngliche Äusserung, nicht
        // den Text; die Textschnittstelle FromSentences entfällt
        _ = heard;
        var proposal = Assert.Single(WordCandidates.FromUtterances("Zielname", [original], commands, _ => false).Candidates);
        var dictation = new DictationInterpreter(commands, Interpreting() with
        {
            Replacements = WordReplacements.From([new LearnedWord("Zielname", [proposal.Text])]),
        });
        var actual = Assert.IsType<InsertText>(Assert.Single(
            dictation.Interpret(original, CommandScope.Everywhere))).Segment.Text;
        Assert.Equal("Heute habe ich Zielname erwähnt.", actual);
    }
}