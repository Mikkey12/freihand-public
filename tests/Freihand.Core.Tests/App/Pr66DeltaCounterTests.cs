using Freihand.Adapters.Dictation;
using Freihand.Core.Commands;
using Freihand.Core.Live;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.App;

public sealed class Pr66DeltaCounterTests
{
    [Fact]
    public void ALearnedSpellingContainingAnAddressStaysExact()
    {
        const string spelling = "Maß team@example.test";
        var options = Interpreting(formatter: Formatter()) with
        {
            Replacements = WordReplacements.From([new LearnedWord(spelling, ["Testvariante"])]),
        };
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, options);
        var step = Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
            Utterance(0, "Testvariante"), CommandScope.DictationWindow)));
        Assert.Equal(spelling, step.Segment.Text);
    }

    [Fact]
    public void AReplacementProtectsOnlyTheInsertedOccurrence()
    {
        var options = Interpreting(formatter: Formatter()) with
        {
            Replacements = WordReplacements.From([new LearnedWord("Maß", ["Testvariante"])]),
        };
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, options);
        var step = Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
            Utterance(0, "Das", "Maß", "ist", "Testvariante"), CommandScope.DictationWindow)));
        Assert.Equal("Das Mass ist Maß", step.Segment.Text);
    }

    [Fact]
    public void ASuffixlessPromptDoesNotLearnAnEntireFollowingSentence()
    {
        Assert.Null(WordCandidates.InGap(
            "Gestern ging es um …",
            "Gestern ging es um Fantasium. Heute habe ich Fantasium erwähnt."));
    }

    [Fact]
    public async Task AReadyFactoryReturningAfterCloseDoesNotOpenTheMicrophone()
    {
        var log = new EventLog();
        using var closed = new CancellationTokenSource();
        var factory = new FakeDictationFactory(log)
        {
            Configure = run =>
            {
                closed.Cancel();
                run.Complete();
            },
        };
        var audio = new FakeAudioCapture(log);
        try
        {
            await new WordListener(factory, audio).ListenAsync(null, null, CancellationToken.None, closed.Token);
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested)
        {
        }

        Assert.Equal(1, factory.LastRun!.DisposeCount);
        Assert.Empty(audio.Sessions);
    }

    [Fact]
    public void EditingAnIgnoredEntryInAFullListCannotReportSuccess()
    {
        var existing = WordListFile.Format(Enumerable.Range(0, WordListFile.MaxWords + 1)
            .Select(i => new LearnedWord("Ziel" + i, ["Variante" + i])));
        Assert.Throws<InvalidOperationException>(() =>
            WordListFile.Add(existing, new LearnedWord("Ziel500", ["NeueVariante"])));
    }
}