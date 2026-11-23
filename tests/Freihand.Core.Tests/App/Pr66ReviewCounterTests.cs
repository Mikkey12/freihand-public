using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using Freihand.Adapters.Dictation;
using Freihand.Core.Commands;
using Freihand.Core.Live;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.App;

public sealed class Pr66ReviewCounterTests
{
    [Fact]
    public void LearnedSpellingSurvivesTheProductionFormatter()
    {
        var options = Interpreting(formatter: Formatter()) with
        {
            Replacements = WordReplacements.From([new LearnedWord("Maßmann", ["Masman"])]),
        };
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, options);
        var step = Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
            Utterance(0, "Masman."), CommandScope.DictationWindow)));
        Assert.Equal("Maßmann.", step.Segment.Text);
    }

    [Fact]
    public void ReplacementWithItsOwnPeriodDoesNotDoubleTheSentencePeriod()
    {
        var options = Interpreting() with
        {
            Replacements = WordReplacements.From([new LearnedWord("Syn.", ["Sinon"])]),
        };
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, options);
        var step = Assert.IsType<InsertText>(Assert.Single(interpreter.Interpret(
            Utterance(0, "Sinon", "."), CommandScope.DictationWindow)));
        Assert.Equal("Syn.", step.Segment.Text);
    }

    [Fact]
    public void AppendingPreservesTheExistingLinesAndTheirLineEndings()
    {
        const string original = "# Notiz\r\nAlpha: Alfa\r\n\r\n";
        var updated = WordListFile.Add(original, new LearnedWord("Beta", ["Beda"]));
        Assert.StartsWith(original, updated, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingANewWordAtCapacityIsRefusedBeforeSaving()
    {
        var existing = WordListFile.Format(Enumerable.Range(0, WordListFile.MaxWords)
            .Select(i => new LearnedWord("Ziel" + i, ["Variante" + i])));
        Assert.Throws<InvalidOperationException>(() =>
            WordListFile.Add(existing, new LearnedWord("NeuesZiel", ["NeueVariante"])));
    }

    [Fact]
    public async Task DoneWhileStartingDoesNotOpenTheMicrophoneLater()
    {
        var log = new EventLog();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeDictationFactory(log) { Gate = gate, Configure = run => run.Complete() };
        var audio = new FakeAudioCapture(log);
        using var stop = new CancellationTokenSource();
        var task = new WordListener(factory, audio).ListenAsync(null, null, stop.Token, CancellationToken.None);
        Assert.Equal(1, factory.StartCount);
        await stop.CancelAsync();
        gate.TrySetResult();
        Assert.Empty(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(audio.Sessions);
    }

    [Fact]
    public async Task AFailedLearningDisposalDoesNotReserveTheMicrophoneForever()
    {
        var log = new EventLog();
        var factory = new FakeDictationFactory(log) { Configure = run => run.Complete() };
        var device = FakeAudioCapture.MicA;
        var catalog = new Freihand.Core.Tests.Audio.Kit.FakeDeviceCatalog([device], device);
        var opens = 0;
        var audio = new AudioCapture(catalog, _ => new ReleaseBackend(++opens == 1), CaptureOptions.Default, TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() =>
            new WordListener(factory, audio).ListenAsync(null, null, CancellationToken.None, CancellationToken.None));
        Assert.Equal(1, factory.LastRun!.DisposeCount);
        await using var next = audio.Open(device);
    }

    private sealed class ReleaseBackend(bool fail) : ICaptureBackend
    {
        public CaptureFormat Format => new(16000, 1, CaptureSampleFormat.Pcm16);
        public event EventHandler<CaptureData>? DataAvailable { add { } remove { } }
        public event EventHandler<CaptureStopReason?>? Stopped { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose()
        {
            if (fail)
            {
                throw new IOException("Synthetic release failure");
            }
        }
    }
}