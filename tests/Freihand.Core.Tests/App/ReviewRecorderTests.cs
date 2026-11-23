using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Recovery;

namespace Freihand.Core.Tests.App;

/// <summary>Prüfaufnahmen: nur auf Wunsch, verschlüsselt, befristet.</summary>
public sealed class ReviewRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Freihand-P11-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly FakeDataProtector _protector = new();

    private string Folder => Path.Combine(_root, "Pruefaufnahmen");

    private ReviewRecorder Recorder() => new(Folder, _protector, _time);

    [Fact]
    public void OffUntilSwitchedOnAndNothingIsStored()
    {
        var recorder = Recorder();

        Assert.Null(recorder.ActiveUntil);
        Assert.Null(recorder.Begin(1));
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void AnEnabledRecordingStoresAudioAndSectionsEncryptedAndReadsBack()
    {
        var recorder = Recorder();
        Assert.True(recorder.Enable());
        Assert.Equal(_time.GetUtcNow() + ReviewRecorder.Duration, recorder.ActiveUntil);

        var recording = Assert.IsAssignableFrom<IReviewRecording>(recorder.Begin(3));
        var first = Pcm(3200, 1);
        var second = Pcm(3200, 2);
        recording.Audio(new AudioChunk(first, 16_000, TimeSpan.Zero));
        recording.Audio(new AudioChunk(second, 16_000, TimeSpan.FromMilliseconds(100)));
        var segment = new TextSegment(Guid.NewGuid(), "Ich bin nicht sicher.", SegmentState.Fixed, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(190));
        recording.Section(segment, new OutputReport(OutputResult.HeldInDictationWindow, "Ich bin nicht", " sicher.", OutputReason.FocusElsewhere));

        var name = recording.Complete();

        Assert.NotNull(name);
        Assert.EndsWith("-3" + ReviewRecorder.Extension, name, StringComparison.Ordinal);
        var stored = File.ReadAllBytes(Path.Combine(Folder, name));
        Assert.StartsWith("FAKE", System.Text.Encoding.ASCII.GetString(stored, 0, 4), StringComparison.Ordinal);
        var data = ReviewFile.Read(stored, _protector);
        Assert.Equal(3, data.Header.Number);
        Assert.Equal(16_000, data.Header.SampleRate);
        Assert.Equal([0L, 100L], data.Header.Chunks.Select(chunk => chunk.OffsetMs));
        Assert.Equal([.. first, .. second], data.Pcm.ToArray());
        var section = Assert.Single(data.Header.Sections);
        Assert.Equal(("Ich bin nicht sicher.", "Ich bin nicht", " sicher.", "HeldInDictationWindow", "FocusElsewhere", 20L, 190L),
            (section.Text, section.Written, section.Held, section.Result, section.Reason, section.StartMs, section.EndMs));

        // Ein zweites Complete schreibt nichts
        Assert.Null(recording.Complete());
    }

    [Fact]
    public void AfterSevenDaysTheStartDeletesEverythingAndNothingNewIsRecorded()
    {
        var recorder = Recorder();
        recorder.Enable();
        var recording = recorder.Begin(1)!;
        recording.Audio(new AudioChunk(Pcm(320, 1), 16_000, TimeSpan.Zero));
        Assert.NotNull(recording.Complete());

        _time.Advance(ReviewRecorder.Duration - TimeSpan.FromSeconds(1));
        recorder.Cleanup();
        Assert.Single(Directory.GetFiles(Folder, "*" + ReviewRecorder.Extension));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(recorder.ActiveUntil);
        recorder.Cleanup();
        Assert.False(Directory.Exists(Folder));
        Assert.Null(recorder.Begin(2));
    }

    [Fact]
    public void SwitchingOffDeletesAllAndARunningRecordingIsNotWritten()
    {
        var recorder = Recorder();
        recorder.Enable();
        var done = recorder.Begin(1)!;
        done.Audio(new AudioChunk(Pcm(320, 1), 16_000, TimeSpan.Zero));
        done.Complete();
        var running = recorder.Begin(2)!;
        running.Audio(new AudioChunk(Pcm(320, 2), 16_000, TimeSpan.Zero));

        recorder.Disable();

        Assert.Null(running.Complete());
        Assert.False(Directory.Exists(Folder));
        Assert.Null(recorder.ActiveUntil);
    }

    [Fact]
    public void ARecordingWithoutAudioOrTextWritesNothing()
    {
        var recorder = Recorder();
        recorder.Enable();

        Assert.Null(recorder.Begin(1)!.Complete());
        Assert.Empty(Directory.GetFiles(Folder, "*" + ReviewRecorder.Extension));
    }

    [Fact]
    public void AudioBeyondTheLimitIsCountedNotKept()
    {
        var recorder = Recorder();
        recorder.Enable();
        var recording = recorder.Begin(1)!;
        recording.Audio(new AudioChunk(new byte[ReviewRecorder.MaxAudioBytes], 16_000, TimeSpan.Zero));
        recording.Audio(new AudioChunk(new byte[640], 16_000, TimeSpan.FromSeconds(625)));

        var data = ReviewFile.Read(File.ReadAllBytes(Path.Combine(Folder, recording.Complete()!)), _protector);

        Assert.Equal(ReviewRecorder.MaxAudioBytes, data.Pcm.Length);
        Assert.Equal(640, data.Header.DroppedBytes);
    }

    [Fact]
    public async Task ADictationRecordsItsAudioAndWrittenTextAndLogsOnlyTheFileNameAndDecodeTimes()
    {
        var recorder = Recorder();
        recorder.Enable();
        var pcm = Pcm(6400, 7);
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Review = recorder;
            r.Audio.OnOpen = session => session.Chunks.Add(new AudioChunk(pcm, 16_000, TimeSpan.Zero));
        });
        var run = await rig.StartRecordingAsync();
        run.Decodes = new DecodeStatistics(2, TimeSpan.FromMilliseconds(380), TimeSpan.FromMilliseconds(250));
        await rig.WaitUntil(() => rig.Log.Contains("Capture.Chunk"), "erster Block gelesen");
        run.Emit(new TextOutput(new RecognitionUpdate(
            [new TextSegment(Guid.NewGuid(), "Erfundene Probe", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromMilliseconds(200))], [])));
        await rig.WaitUntil(() => rig.LogLines.Any(line => line.StartsWith("ereignis abschnitt_geschrieben ", StringComparison.Ordinal)), "geschrieben");
        await rig.EndAsync(run);

        var line = Assert.Single(rig.LogLines, entry => entry.StartsWith("ereignis pruefaufnahme datei=", StringComparison.Ordinal));
        var name = line["ereignis pruefaufnahme datei=".Length..];
        Assert.Matches(@"^\d{8}-\d{6}-\d{3}-1\.fpa$", name);
        Assert.Contains("ereignis erkennung decodes=2 rechenzeit_ms=380 laengste_ms=250", rig.LogLines);
        Assert.DoesNotContain(rig.LogLines, entry => entry.Contains("Erfundene", StringComparison.Ordinal));

        var data = ReviewFile.Read(File.ReadAllBytes(Path.Combine(Folder, name)), _protector);
        Assert.Equal(pcm, data.Pcm.ToArray());
        var section = Assert.Single(data.Header.Sections);
        Assert.Equal(("Erfundene Probe", "Written"), (section.Text, section.Result));
    }

    [Fact]
    public async Task WithoutReviewOrDecodeTimesNoExtraLinesAppear()
    {
        await using var rig = new ControllerRig(arrange: r =>
            r.Audio.OnOpen = session => session.Chunks.Add(new AudioChunk(Pcm(640, 1), 16_000, TimeSpan.Zero)));
        var run = await rig.StartRecordingAsync();
        await rig.WaitUntil(() => rig.Log.Contains("Capture.Chunk"), "erster Block gelesen");
        await rig.EndAsync(run);

        Assert.DoesNotContain(rig.LogLines, entry => entry.StartsWith("ereignis pruefaufnahme", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.LogLines, entry => entry.StartsWith("ereignis erkennung", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(_root);
        if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(absolute).StartsWith("Freihand-P11-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }

        if (Directory.Exists(absolute))
        {
            Directory.Delete(absolute, recursive: true);
        }
    }

    private static byte[] Pcm(int bytes, int seed)
    {
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        return data;
    }
}

/// <summary>Liest eine Prüfaufnahme wie die Auswertung (S2); Freihand selbst liest sie nie.</summary>
internal static class ReviewFile
{
    public static (ReviewHeader Header, ReadOnlyMemory<byte> Pcm) Read(byte[] protectedData, Freihand.Core.Recovery.IDataProtector protector)
    {
        var plain = protector.Unprotect(protectedData);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(plain);
        var header = System.Text.Json.JsonSerializer.Deserialize<ReviewHeader>(plain.AsSpan(4, length))
            ?? throw new InvalidDataException("Kopf der Prüfaufnahme fehlt.");
        return (header, plain.AsMemory(4 + length));
    }
}
