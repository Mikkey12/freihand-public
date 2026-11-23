using System.Text;
using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>„Wort lernen“ (P11 S4): eine Datei, ein Schreiber; Zuhören bis zum Stopp, Mikrofon nur einmal.</summary>
public sealed class WordLearningTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "freihand-woerter-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static TextSegment Fixed(Guid id, string text) => new(id, text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    private static readonly UTF8Encoding Utf8 = new(false);

    // Regel „eine Datei, ein Schreiber“ (Codex zu PR #66): Lernen schreibt nur woerter-gelernt.txt, nie woerter.txt
    [Fact]
    public void LearningWritesOnlyItsOwnFileAndTheOwnListIsNeverTouched()
    {
        var lines = new List<string>();
        var store = new WordStore(Path.Combine(_folder, "woerter.txt"), lines.Add);
        Assert.Equal(0, store.Load().Count);
        Directory.CreateDirectory(_folder);
        const string own = "# eigene Notiz\r\nNilo: Mark\r\nkaputt\r\n\r\n";
        File.WriteAllText(store.Path, own, Utf8);
        var written = File.GetLastWriteTimeUtc(store.Path);

        Assert.Equal(LearnOutcome.Saved, store.Add(new LearnedWord("Nivella", ["Niwela"])));
        Assert.Equal(LearnOutcome.Saved, store.Add(new LearnedWord("Nivella", ["Nivela"])));

        Assert.Equal(own, File.ReadAllText(store.Path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(store.Path));
        Assert.EndsWith("Nivella: Niwela, Nivela\n", File.ReadAllText(store.LearnedPath), StringComparison.Ordinal);
        Assert.StartsWith(WordListFile.LearnedHeader, File.ReadAllText(store.LearnedPath), StringComparison.Ordinal);
        Assert.False(File.Exists(store.LearnedPath + ".neu"));
        Assert.Equal(3, store.Load().Count);
        Assert.Single(store.Problems());
        Assert.StartsWith("woerter.txt, Zeile 3:", store.Problems()[0], StringComparison.Ordinal);

        // Ins Protokoll gehen nur Anzahlen, nie Wörter
        Assert.All(lines, line => Assert.DoesNotContain("Niwela", line, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.DoesNotContain("Mark", line, StringComparison.Ordinal));
    }

    [Fact]
    public void TheOwnListOverridesAndCanSwitchOffALearnedWord()
    {
        var store = new WordStore(Path.Combine(_folder, "woerter.txt"));
        store.Add(new LearnedWord("Nivella", ["Niwela", "Nivela"]));
        store.Add(new LearnedWord("Bluetenpark", ["Blütpark"]));
        Assert.Equal(3, store.Load().Count);

        // Eine Zeile mit derselben Schreibweise ersetzt die gelernte ganz; nur „Schreibweise:“ schaltet sie ab
        File.WriteAllText(store.Path, "Nivella: Nivela\nBluetenpark:\n", Utf8);

        Assert.Equal(1, store.Load().Count);
        Assert.Empty(store.Learned());
        var interpreter = new DictationInterpreter(Freihand.Core.Commands.BuiltInCommands.Pilot,
            Live.LiveTestKit.Interpreting() with { Replacements = store.Load() });
        var step = Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
            Live.LiveTestKit.Utterance(0, "Niwela", "Nivela", "Blütpark"), Freihand.Core.Commands.CommandScope.DictationWindow)));
        Assert.Equal("Niwela Nivella Blütpark", step.Segment.Text);
    }

    // Eigene Vorprüfung zu PR #66: gespeichert heisst wirksam; eine gehörte Form, die schon ein anderes Wort ersetzt,
    // wird nicht gespeichert, und Freihand sagt, welches Wort sie belegt
    [Fact]
    public void AHeardFormTakenByAnotherWordIsNotSaved()
    {
        var store = new WordStore(Path.Combine(_folder, "woerter.txt"));
        Assert.Equal(LearnOutcome.Saved, store.Add(new LearnedWord("Nevila", ["Nivela"])));
        var before = File.ReadAllText(store.LearnedPath);

        Assert.Equal(LearnOutcome.TakenByOther, store.Add(new LearnedWord("Nivella", ["Nivela"])));

        Assert.Equal(before, File.ReadAllText(store.LearnedPath));
        Assert.Equal("Nevila", store.Owner("Nivela"));

        // Ebenso, wenn die eigene Liste die Form einem anderen Wort zuordnet
        File.WriteAllText(store.Path, "Anders: Niwela\n", Utf8);
        Assert.Equal(LearnOutcome.TakenByOther, store.Add(new LearnedWord("Nivella", ["Niwela"])));
        Assert.Equal("Anders", store.Owner("Niwela"));

        // Nach dem Abschalten in der eigenen Liste lässt sich neu lernen
        File.WriteAllText(store.Path, "Nevila:\n", Utf8);
        Assert.Equal(LearnOutcome.Saved, store.Add(new LearnedWord("Nivella", ["Nivela"])));
        Assert.Equal("Nivella", store.Owner("Nivela"));
    }

    // Was in woerter.txt steht, hätte gelernt keine Wirkung: nicht scheinbar erfolgreich speichern
    [Fact]
    public void LearningASpellingFromTheOwnListSavesNothing()
    {
        var store = new WordStore(Path.Combine(_folder, "woerter.txt"));
        Directory.CreateDirectory(_folder);
        File.WriteAllText(store.Path, "Nivella: Nivela\n", Utf8);

        Assert.Equal(LearnOutcome.InOwnList, store.Add(new LearnedWord("Nivella", ["Niwela"])));
        Assert.False(File.Exists(store.LearnedPath));
    }

    [Fact]
    public void CreatingTheOwnListNeverOverwritesIt()
    {
        var store = new WordStore(Path.Combine(_folder, "woerter.txt"));
        store.EnsureExists();
        Assert.StartsWith("# Freihand: eigene Wörter.", File.ReadAllText(store.Path), StringComparison.Ordinal);

        File.WriteAllText(store.Path, "Nilo: Mark\n", Utf8);
        store.EnsureExists();

        Assert.Equal("Nilo: Mark\n", File.ReadAllText(store.Path));
    }

    [Fact]
    public void ALockedFileMeansNoReplacementsAndNoThrow()
    {
        var store = new WordStore(Path.Combine(_folder, "woerter.txt"));
        store.Add(new LearnedWord("Nilo", ["Mark"]));
        store.EnsureExists();

        // Sperren mit FileShare.None verhindern Lesen nur unter Windows verlässlich
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (new FileStream(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Ein Abschalten in der eigenen Liste darf nie übergangen werden: lieber einmal gar keine Ersetzung
            Assert.Equal(0, store.Load().Count);
            Assert.ThrowsAny<IOException>(() => store.Add(new LearnedWord("Nivella", ["Niwela"])));
        }

        Assert.Equal(1, store.Load().Count);
    }

    [Fact]
    public void AFullLearnedListIsRefusedBeforeWriting()
    {
        var store = new WordStore(Path.Combine(_folder, "woerter.txt"));
        Directory.CreateDirectory(_folder);
        var full = WordListFile.Format(Enumerable.Range(0, WordListFile.MaxWords).Select(k => new LearnedWord($"W{k}", [$"V{k}"])));
        File.WriteAllText(store.LearnedPath, full, Utf8);

        Assert.Throws<InvalidOperationException>(() => store.Add(new LearnedWord("Neu", ["Nöi"])));
        Assert.Equal(full, File.ReadAllText(store.LearnedPath));
        Assert.False(File.Exists(store.LearnedPath + ".neu"));

        // Eine wirksame Schreibweise darf auch bei voller Liste neue gehörte Schreibweisen bekommen
        store.Add(new LearnedWord("W1", ["V1b"]));
        Assert.Contains("W1: V1, V1b\n", File.ReadAllText(store.LearnedPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListenerReportsEachFixedUtteranceOnceUntilStopped()
    {
        var log = new EventLog();
        var factory = new FakeDictationFactory(log);
        var audio = new FakeAudioCapture(log);
        var first = Live.LiveTestKit.Utterance(0, "Niwela.");
        var second = Live.LiveTestKit.Utterance(1, "Blüt", "Park");
        factory.Configure = run =>
        {
            // Nur HeardOutput zählt; fester Text allein und eine doppelte Kennung nicht
            run.Emit(new TextOutput(new RecognitionUpdate([Fixed(Guid.NewGuid(), "Text")], [])));
            run.Emit(new HeardOutput(first));
            run.Emit(new HeardOutput(first));
            run.Emit(new HeardOutput(second));
        };
        var heard = new List<string>();
        var twice = new TaskCompletionSource();
        using var stop = new CancellationTokenSource();

        var listening = new WordListener(factory, audio).ListenAsync(null, text =>
        {
            heard.Add(text);
            if (heard.Count == 2)
            {
                twice.TrySetResult();
            }
        }, stop.Token, CancellationToken.None);
        await twice.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        factory.LastRun!.Complete();

        Assert.Equal([first, second], await listening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["Niwela.", "Blüt Park"], heard);
        Assert.True(log.Contains("Capture.Stop"));
        Assert.Equal(1, audio.Session!.DisposeCount);
        Assert.Equal(1, factory.LastRun.DisposeCount);
    }

    [Fact]
    public async Task ABusyMicrophoneFailsAndReleasesTheRecognizerSession()
    {
        var log = new EventLog();
        var factory = new FakeDictationFactory(log);
        var audio = new FakeAudioCapture(log) { OpenFailure = new InvalidOperationException("belegt") };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WordListener(factory, audio).ListenAsync(null, null, CancellationToken.None, CancellationToken.None));
        Assert.Equal(1, factory.LastRun!.DisposeCount);
    }

    [Fact]
    public async Task WithoutAMicrophoneNoSessionStarts()
    {
        var log = new EventLog();
        var factory = new FakeDictationFactory(log);
        var audio = new FakeAudioCapture(log) { DefaultDevice = null };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WordListener(factory, audio).ListenAsync(null, null, CancellationToken.None, CancellationToken.None));
        Assert.Equal(0, factory.StartCount);
    }
}
