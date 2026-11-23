using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Recovery;

namespace Freihand.Core.Tests.App;

public sealed class Pr60ReviewCounterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Freihand-Pr60-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private string Folder => Path.Combine(_root, "Pruefaufnahmen");
    private ReviewRecorder NewRecorder(IDataProtector? protector = null) => new(Folder, protector ?? new FakeDataProtector(), _time);
    private static void AddAudio(IReviewRecording recording) => recording.Audio(new AudioChunk(new byte[640], 16000, TimeSpan.Zero));

    [Fact]
    public void DisableMustRevokeConsentEvenWhenTheStateFileCannotBeDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var recorder = NewRecorder();
        Assert.True(recorder.Enable());
        using var locked = new FileStream(Path.Combine(Folder, ReviewRecorder.StateFile), FileMode.Open, FileAccess.Read, FileShare.Read);
        recorder.Disable();
        var afterDisable = recorder.Begin(1);
        // Auch eine neue Instanz darf nach dem abgeschlossenen Ausschalten die alte Freigabe nicht wieder lesen.
        var restarted = NewRecorder();
        restarted.Cleanup();
        var afterRestart = restarted.Begin(2);
        foreach (var recording in new[] { afterDisable, afterRestart })
        {
            if (recording is null) continue;
            AddAudio(recording);
            recording.Complete();
        }
        Assert.Empty(Directory.GetFiles(Folder, "*" + ReviewRecorder.Extension));
        Assert.Null(afterDisable);
        Assert.Null(afterRestart);
    }

    [Fact]
    public void ReenablingMustNotReviveARecordingRevokedByDisable()
    {
        var recorder = NewRecorder();
        Assert.True(recorder.Enable());
        var oldRecording = recorder.Begin(1)!;
        AddAudio(oldRecording);
        recorder.Disable();
        Assert.True(recorder.Enable());
        Assert.Null(oldRecording.Complete());
        Assert.Empty(Directory.GetFiles(Folder, "*" + ReviewRecorder.Extension));
    }

    [Fact]
    public void ExpiryDuringProtectionMustPreventPublishingTheRecording()
    {
        var recorder = NewRecorder(new AdvancingProtector(_time));
        Assert.True(recorder.Enable());
        _time.Advance(ReviewRecorder.Duration - TimeSpan.FromMilliseconds(1));
        var recording = recorder.Begin(1)!;
        AddAudio(recording);
        Assert.Null(recording.Complete());
        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, "*" + ReviewRecorder.Extension).Any());
    }

    [Fact]
    public void ExpiryMustPurgeTheFolderWithoutAnotherDictationOrRestart()
    {
        var recorder = NewRecorder();
        Assert.True(recorder.Enable());
        var recording = recorder.Begin(1)!;
        AddAudio(recording);
        Assert.NotNull(recording.Complete());
        _time.Advance(ReviewRecorder.Duration);
        Assert.Null(recorder.ActiveUntil);
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public async Task AReviewSectionFailureMustNotTurnAnAlreadyWrittenSectionIntoHeld()
    {
        await using var rig = new ControllerRig(arrange: r => r.Review = new SingleRecorder(new ThrowingSection()));
        var run = await rig.StartRecordingAsync();
        run.Emit(new TextOutput(new RecognitionUpdate([Segment()], [])));
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(EntryStatus.Written, Assert.Single(rig.Controller.Current.Entries).Status);
        Assert.DoesNotContain(rig.Updates, update => update.Message == ControllerMessage.DictationFailed);
    }

    [Fact]
    public async Task SectionsHeldAfterAnOutputFailureMustAlsoAppearInTheReview()
    {
        var review = new CollectedSections();
        await using var rig = new ControllerRig(arrange: r => r.Review = new SingleRecorder(review));
        var run = await rig.StartRecordingAsync();
        var first = Segment();
        rig.Output.FailOn = first.Text;
        run.Emit(new TextOutput(new RecognitionUpdate([first, Segment()], [])));
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(2, rig.Controller.Current.Entries.Count);
        Assert.Equal(2, review.Count);
    }

    [Fact]
    public async Task AReviewAudioFailureMustNotAbortTheDictation()
    {
        var events = new EventLog();
        var audio = new FakeAudioCapture(events)
        {
            OnOpen = session => session.Chunks.Add(new AudioChunk(new byte[640], 16000, TimeSpan.Zero)),
        };
        await using var controller = new DictationController(
            new FakeRecognizerHost(), new AudioFactory(), audio, new FakeBinding(events), new FakeTextOutput(events),
            new FakeGuard(events), new FakeHotkeys(events), new FakeClipboard(events),
            ControllerRig.Options(Freihand.Core.Profiles.ProfileStrictness.Standard), review: new SingleRecorder(new ThrowingAudio()));
        await controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.NotEqual(ControllerMessage.DictationFailed, controller.Current.Message);
        Assert.Equal(EntryStatus.Written, Assert.Single(controller.Current.Entries).Status);
    }

    private sealed class ThrowingAudio : IReviewRecording
    {
        public void Audio(AudioChunk chunk) => throw new IOException("Erfundener Beobachterfehler");
        public void Section(TextSegment segment, OutputReport report) { }
        public string? Complete() => null;
    }
    private sealed class AudioFactory : IDictationFactory
    {
        public Task<IDictationRun> StartAsync(CancellationToken cancellationToken) => Task.FromResult<IDictationRun>(new AudioRun());
    }
    private sealed class AudioRun : IDictationRun
    {
        public Freihand.Core.Profiles.ModeDecision Mode => new(LiveMode.Sentence, Freihand.Core.Profiles.RecognitionMode.Live, Freihand.Core.Profiles.ModeReason.NotApproved);
        public void PressCommandKey() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async IAsyncEnumerable<DictationOutput> RunAsync(IAsyncEnumerable<AudioChunk> audio,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
            {
                _ = chunk;
                break;
            }
            yield return new TextOutput(new RecognitionUpdate([Segment()], []));
        }
    }
    private static TextSegment Segment() => new(Guid.NewGuid(), "Erfundene Probe", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    private sealed class SingleRecorder(IReviewRecording recording) : IReviewRecorder
    {
        public IReviewRecording Begin(int number) => recording;
    }
    private sealed class ThrowingSection : IReviewRecording
    {
        public void Audio(AudioChunk chunk) { }
        public void Section(TextSegment segment, OutputReport report) => throw new IOException("Erfundener Beobachterfehler");
        public string? Complete() => null;
    }
    private sealed class CollectedSections : IReviewRecording
    {
        public int Count { get; private set; }
        public void Audio(AudioChunk chunk) { }
        public void Section(TextSegment segment, OutputReport report) => Count++;
        public string? Complete() => null;
    }
    private sealed class AdvancingProtector(ManualTimeProvider time) : IDataProtector
    {
        private readonly FakeDataProtector _inner = new();
        public byte[] Protect(byte[] plaintext)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            return _inner.Protect(plaintext);
        }
        public byte[] Unprotect(byte[] protectedData) => _inner.Unprotect(protectedData);
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(_root);
        if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(absolute).StartsWith("Freihand-Pr60-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
