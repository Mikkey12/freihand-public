using Freihand.Adapters.Dictation;
using Freihand.Core.Commands;
using Freihand.Core.Live;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.App;

public sealed class Pr66SixthDeltaCounterTests
{
    [Fact]
    public void ASpokenDictationMarkIsNotReportedAsLiteralSpellingAlreadyWritten()
    {
        var utterance = Utterance(0, "Heute", "habe", "ich", "Komma", "erwähnt.");
        var commands = BuiltInCommands.Pilot;
        var interpreter = new DictationInterpreter(commands, Interpreting());
        var actual = Assert.IsType<InsertText>(Assert.Single(
            interpreter.Interpret(utterance, CommandScope.Everywhere))).Segment.Text;
        Assert.Equal("Heute habe ich, erwähnt.", actual);

        var result = WordCandidates.FromUtterances("Komma", [utterance], commands, _ => false);
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Kind == CandidateKind.Same);
    }

    [Fact]
    public void ALiteralSpellingMayCoincideWithADictationMarkName()
    {
        var utterance = Utterance(0, "Heute", "habe", "ich", "Koma", "erwähnt.");
        var commands = BuiltInCommands.Pilot;
        var options = Interpreting() with
        {
            Replacements = WordReplacements.From([new LearnedWord("Komma", ["Koma"])]),
        };
        var interpreter = new DictationInterpreter(commands, options);
        var actual = Assert.IsType<InsertText>(Assert.Single(
            interpreter.Interpret(utterance, CommandScope.Everywhere))).Segment.Text;
        Assert.Equal("Heute habe ich Komma erwähnt.", actual);

        var result = WordCandidates.FromUtterances("Komma", [utterance], commands, _ => false);
        Assert.Equal("Koma", Assert.Single(result.Candidates).Text);
    }

    [Fact]
    public void ExtendingAnEarlierWordDoesNotTakeAnAliasAlreadyOwnedByALaterWord()
    {
        var directory = Directory.CreateTempSubdirectory("freihand-pr66-");
        var store = new WordStore(Path.Combine(directory.FullName, "woerter.txt"));
        try
        {
            Assert.Equal(LearnOutcome.Saved, store.Add(new LearnedWord("Erstziel", ["Erstform"])));
            Assert.Equal(LearnOutcome.Saved, store.Add(new LearnedWord("Zweitziel", ["Zweitform"])));
            var before = File.ReadAllText(store.LearnedPath);

            Assert.Equal(LearnOutcome.TakenByOther, store.Add(new LearnedWord("Erstziel", ["Zweitform"])));
            Assert.Equal("Zweitziel", store.Owner("Zweitform"));
            Assert.Equal(before, File.ReadAllText(store.LearnedPath));
        }
        finally
        {
            File.Delete(store.Path);
            File.Delete(store.LearnedPath);
            directory.Delete();
        }
    }
}