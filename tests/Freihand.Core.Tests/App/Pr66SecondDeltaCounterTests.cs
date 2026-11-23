using Freihand.Adapters.Dictation;
using Freihand.Core.Commands;
using Freihand.Core.Live;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.App;

public sealed class Pr66SecondDeltaCounterTests
{
    [Fact]
    public void AReplacementMustNotRemoveProtectionFromTheRestOfAnAddress()
    {
        const string address = "synthetisch.straße@example.test";
        var formatter = Formatter();
        Assert.Equal(address, formatter.Format(address));
        var options = Interpreting(formatter: formatter) with
        {
            Replacements = WordReplacements.From([new LearnedWord("synthetisch", ["testwort"])]),
        };
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, options);
        var step = Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
            Utterance(0, "testwort", ".straße@example.test"), CommandScope.DictationWindow)));
        Assert.Equal(address, step.Segment.Text);
    }

    [Fact]
    public void ADisabledReplacementDoesNotReviveDuringAnInPlaceEditorSave()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        InOwnFolder(store =>
        {
            Assert.Equal(LearnOutcome.Saved, store.Add(new LearnedWord("Ziel", ["Variante"])));
            File.WriteAllText(store.Path, "Ziel:\n");
            Assert.Equal(0, store.Load().Count);
            // Some editors truncate the file and write the new version on the same open handle.
            using var editor = new FileStream(store.Path, FileMode.Truncate, FileAccess.Write, FileShare.Read);
            Assert.Equal(0, store.Load().Count);
        });
    }

    [Fact]
    public void TheEffectiveLearningListOmitsAnAliasOwnedByADifferentManualSpelling()
    {
        InOwnFolder(store =>
        {
            Assert.Equal(LearnOutcome.Saved, store.Add(new LearnedWord("Ziel", ["Variante"])));
            File.WriteAllText(store.Path, "AnderesZiel: Variante\n");
            var options = Interpreting() with { Replacements = store.Load() };
            var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, options);
            var step = Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
                Utterance(0, "Variante"), CommandScope.DictationWindow)));
            Assert.Equal("AnderesZiel", step.Segment.Text);
            Assert.Empty(store.Learned());
        });
    }

    private static void InOwnFolder(Action<WordStore> test)
    {
        var directory = Directory.CreateTempSubdirectory("freihand-pr66-");
        var store = new WordStore(Path.Combine(directory.FullName, "woerter.txt"));
        try
        {
            test(store);
        }
        finally
        {
            File.Delete(store.Path);
            File.Delete(store.LearnedPath);
            directory.Delete();
        }
    }
}