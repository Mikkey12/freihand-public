using Freihand.Adapters.Audio;
using Freihand.Core.Live;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Audio.Kit;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Audio.Kit.SignalAnalysis;

namespace Freihand.Core.Tests.Audio;

/// <summary>
/// R36–R41 und R49: Aufnahmesitzung mit WASAPI-Attrappe und Testuhr (P8-Konzept, Kapitel 4.1, 4.3, 5.2 und 5.3). Alle
/// Zeitwerte laufen über die Testuhr; die Startwerte aus <see cref="CaptureOptions.Default"/> gelten.
/// </summary>
public sealed class CaptureSessionTests
{
    private static readonly CaptureOptions Defaults = CaptureOptions.Default;

    [Fact]
    public async Task R36_RecordingStartsOnlyWithAudioAndMeasuresTheLatency()
    {
        await using var rig = CaptureRig.Open();
        Assert.Equal(new CaptureStatus(CapturePhase.Opening, null, null, false, TimeSpan.Zero), rig.Session.Status);
        var backend = await rig.StartedAsync();
        var reader = new ChunkReader(rig.Session);

        // Ein Puffer mit 0 Byte nach 50 ms ist noch kein Audio
        rig.Clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(backend.Deliver([]));
        Assert.Equal(CapturePhase.Opening, rig.Session.Status.Phase);

        // Erste Daten nach 120 ms
        rig.Clock.Advance(TimeSpan.FromMilliseconds(70));
        Assert.True(backend.Deliver(rig.Signal.Next(CaptureRig.BufferLength)));
        var recording = await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");
        Assert.Equal(TimeSpan.FromMilliseconds(120), recording.StartLatency);
        Assert.Null(recording.StopReason);
        Assert.Equal(TimeSpan.FromMilliseconds(120), rig.Session.Status.StartLatency);

        // ReadAsync liefert Blöcke, während die Aufnahme läuft
        rig.FeedFor(TimeSpan.FromMilliseconds(400));
        await reader.WaitForChunksAsync(2);
        Assert.Equal(CapturePhase.Recording, rig.Session.Status.Phase);
        AssertBlocks(reader.Chunks, "während der Aufnahme");

        rig.Session.Stop();
        var read = await reader.CompleteAsync();
        await rig.CloseAsync();
        AssertSameAudio(rig.Reference(), read, "R36");
        rig.Statuses.AssertConsistent(CaptureStopReason.Requested, "R36");
        Assert.True(rig.Statuses.SawRecording, "StatusChanged meldete Recording nicht.");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Fact]
    public async Task R37_DeviceRemovedDeliversEverythingAndOpensNoOtherDevice()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();
        var reader = new ChunkReader(rig.Session);
        rig.FeedFor(TimeSpan.FromSeconds(0.5));
        await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");

        backend.Fail(CaptureStopReason.DeviceRemoved);

        var stopped = await rig.StoppedAsync();
        Assert.Equal(CaptureStopReason.DeviceRemoved, stopped.StopReason);
        var read = await reader.CompleteAsync();
        Assert.InRange(TotalSamples(read), 8_000 - CaptureConverterTests.SampleTolerance, 8_000 + CaptureConverterTests.SampleTolerance);
        AssertSameAudio(rig.Reference(), read, "R37");
        AssertBlocks(read, "R37");
        await rig.CloseAsync();

        Assert.Equal([CaptureRig.Headset], rig.Factory.Devices);
        Assert.False(backend.IsRunning);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(CaptureStopReason.DeviceRemoved, rig.Session.Status.StopReason);
        rig.Statuses.AssertConsistent(CaptureStopReason.DeviceRemoved, "R37");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Fact]
    public async Task R38_OneSecondWithoutDataStopsWithNoSignal()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();
        rig.FeedFor(TimeSpan.FromSeconds(0.5));
        await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");

        await rig.Clock.WaitForTimersAsync();
        rig.Clock.Advance(Defaults.NoSignalTimeout);

        var stopped = await rig.StoppedAsync();
        Assert.Equal(CaptureStopReason.NoSignal, stopped.StopReason);
        Assert.False(backend.IsRunning);
        Assert.Equal(1, backend.StopCalls);
        var read = await ChunkReader.ReadAllAsync(rig.Session);
        AssertSameAudio(rig.Reference(), read, "R38");
        await rig.CloseAsync();
        rig.Statuses.AssertConsistent(CaptureStopReason.NoSignal, "R38");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Fact]
    public async Task R38_DataEveryNineHundredMillisecondsKeepsRecording()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();
        Assert.True(rig.Feed());
        await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");

        for (var i = 0; i < 10; i++)
        {
            Assert.True(rig.Feed(rig.Signal.Next(CaptureRig.BufferLength), TimeSpan.FromSeconds(0.9)), $"Puffer {i} abgewiesen.");
        }

        Assert.Equal(CapturePhase.Recording, rig.Session.Status.Phase);
        Assert.True(backend.IsRunning);
        Assert.Equal(0, backend.StopCalls);
        rig.Session.Stop();
        await rig.CloseAsync();
        rig.Statuses.AssertConsistent(CaptureStopReason.Requested, "R38");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Fact]
    public async Task R38_FirstAudioMayComeLaterThanNoSignalTimeoutThenTheShortTimeoutApplies()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();
        await rig.Clock.WaitForTimersAsync();

        // Wie das Bluetooth-Headset in RM10: erste Daten erst nach mehr als NoSignalTimeout
        var late = Defaults.NoSignalTimeout + TimeSpan.FromMilliseconds(500);
        Assert.True(late < Defaults.FirstDataTimeout);
        rig.Clock.Advance(late);
        Assert.Equal(CapturePhase.Opening, rig.Session.Status.Phase);
        Assert.True(backend.Deliver(rig.Signal.Next(CaptureRig.BufferLength)));
        var recording = await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");
        Assert.Equal(late, recording.StartLatency);

        // Nach den ersten Daten gilt wieder NoSignalTimeout
        await rig.Clock.WaitForTimersAsync();
        rig.Clock.Advance(Defaults.NoSignalTimeout);
        var stopped = await rig.StoppedAsync();
        Assert.Equal(CaptureStopReason.NoSignal, stopped.StopReason);
        Assert.Equal(Defaults.NoSignalTimeout, stopped.SinceLastData);
        await rig.CloseAsync();
        rig.Statuses.AssertConsistent(CaptureStopReason.NoSignal, "R38 erste Daten spät");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Fact]
    public async Task R38_OnlyEmptyBuffersAfterOpenStopWithNoSignalAndNeverRecord()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();
        await rig.Clock.WaitForTimersAsync();

        // Bis zu den ersten Daten gilt FirstDataTimeout, nicht NoSignalTimeout (RM10)
        var buffers = (int)(Defaults.FirstDataTimeout / CaptureRig.BufferLength);
        for (var i = 0; i < buffers; i++)
        {
            Assert.NotEqual(CapturePhase.Stopped, rig.Session.Status.Phase);
            rig.Clock.Advance(CaptureRig.BufferLength);
            backend.Deliver([]);
        }

        var stopped = await rig.StoppedAsync();
        Assert.Equal(CaptureStopReason.NoSignal, stopped.StopReason);
        Assert.Null(stopped.StartLatency);
        Assert.False(backend.IsRunning);
        Assert.Equal(1, backend.StopCalls);
        Assert.Empty(await ChunkReader.ReadAllAsync(rig.Session));
        await rig.CloseAsync();
        Assert.False(rig.Statuses.SawRecording, "Recording ohne ein einziges Sample.");
        rig.Statuses.AssertConsistent(CaptureStopReason.NoSignal, "R38");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    /// <summary>
    /// Nullen in Puffern von 100 ms, Uhr und Audio im Gleichschritt. Ob die Sitzung die 5 s ab dem Eintreffen des ersten
    /// Nullpuffers (Uhr) oder ab dessen erstem Sample (Audio) misst, ist offen; beide liegen einen Puffer auseinander.
    /// Darum: nach 4,9 s Nullen (Uhr 4,8 s) noch kein Hinweis, nach 5,1 s Nullen (Uhr 5,0 s) der Hinweis.
    /// </summary>
    [Fact]
    public async Task R38_FiveSecondsOfExactZerosOnlyRaiseTheHint()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();

        // Der Leser holt laufend ab, sonst wäre nach 5 s ungelesenem Audio QueueFull richtig
        var reader = new ChunkReader(rig.Session);
        rig.FeedFor(TimeSpan.FromSeconds(0.3));
        await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");

        for (var i = 0; i < 49; i++)
        {
            Assert.True(rig.FeedZeros(), $"Nullpuffer {i} abgewiesen.");
            await rig.KeepUpAsync(reader);
        }

        Assert.False(rig.Session.Status.SilentSignal, "Hinweis vor 5 s Nullen.");
        Assert.True(rig.FeedZeros());
        Assert.True(rig.FeedZeros());
        var silent = await rig.WaitForAsync(status => status.SilentSignal, "SilentSignal");
        Assert.Equal(CapturePhase.Recording, silent.Phase);
        Assert.True(backend.IsRunning);

        // Ein einziges Sample ungleich 0 nimmt den Hinweis zurück
        var frames = TestSignals.Frames(rig.Format, CaptureRig.BufferLength);
        var one = TestSignals.Encode(rig.Format, frames, (frame, channel) => frame == frames / 2 && channel == 0 ? 0.25 : 0);
        Assert.True(rig.Feed(one, CaptureRig.BufferLength));
        var heard = await rig.WaitForAsync(status => !status.SilentSignal, "SilentSignal zurückgenommen");
        Assert.Equal(CapturePhase.Recording, heard.Phase);
        Assert.Equal(0, backend.StopCalls);

        rig.Session.Stop();
        AssertSameAudio(rig.Reference(), await reader.CompleteAsync(), "R38");
        await rig.CloseAsync();
        rig.Statuses.AssertConsistent(CaptureStopReason.Requested, "R38");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Theory]
    [InlineData(CaptureStopReason.AccessDenied)]
    [InlineData(CaptureStopReason.DeviceInUse)]
    public async Task R39_FailedStartStopsWithItsReasonAndNeverRecords(CaptureStopReason reason)
    {
        await using var rig = CaptureRig.Open(configure: factory => factory.StartFailure = reason);
        var backend = await rig.Factory.CreatedAsync();

        var stopped = await rig.StoppedAsync();

        Assert.Equal(reason, stopped.StopReason);
        Assert.Null(stopped.StartLatency);
        Assert.Empty(await ChunkReader.ReadAllAsync(rig.Session));
        await rig.CloseAsync();
        Assert.Equal(1, backend.StartCalls);
        Assert.False(backend.IsRunning);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal([CaptureRig.Headset], rig.Factory.Devices);
        Assert.False(rig.Statuses.SawRecording, "Recording trotz gescheitertem Start.");
        Assert.Equal(reason, rig.Session.Status.StopReason);
        rig.Statuses.AssertConsistent(reason, "R39");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Fact]
    public async Task R40_StopDeliversEverythingAndEndsRegularly()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();
        rig.FeedFor(TimeSpan.FromSeconds(0.7));

        rig.Session.Stop();

        var stopped = await rig.StoppedAsync();
        Assert.Equal(CaptureStopReason.Requested, stopped.StopReason);
        Assert.False(backend.IsRunning);
        Assert.Equal(1, backend.StopCalls);

        // Ein zweites Stop() bleibt ohne Wirkung
        rig.Session.Stop();
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(CaptureStopReason.Requested, rig.Session.Status.StopReason);

        var read = await ChunkReader.ReadAllAsync(rig.Session);
        Assert.InRange(TotalSamples(read), 11_200 - CaptureConverterTests.SampleTolerance, 11_200 + CaptureConverterTests.SampleTolerance);
        AssertSameAudio(rig.Reference(), read, "R40");
        AssertBlocks(read, "R40");
        await rig.CloseAsync();
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(TimeSpan.Zero, rig.Session.Status.Unread);
        rig.Statuses.AssertConsistent(CaptureStopReason.Requested, "R40");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    /// <summary>
    /// 16 kHz mono PCM16 geht bitgenau und ohne Nachlauf durch; so ist „alle 5 s“ genau 80 000 Samples und
    /// <c>Unread</c> genau 5 s. Puffer zu 100 ms, also zwei je Block.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R41_QueueFullAfterFiveSecondsKeepsTheQueueReadable(bool readAfterwards)
    {
        await using var rig = CaptureRig.Open(CaptureRig.NativeFormat);
        var backend = await rig.StartedAsync();
        var input = TestSignals.Pcm16Pattern(80_000);
        var pieces = TestSignals.Split(input, 2, [1_600]);

        for (var i = 0; i < 49; i++)
        {
            Assert.True(rig.Feed(pieces[i], CaptureRig.BufferLength), $"Puffer {i} abgewiesen.");
        }

        Assert.Equal(CapturePhase.Recording, rig.Session.Status.Phase);
        Assert.True(rig.Feed(pieces[49], CaptureRig.BufferLength));
        var stopped = await rig.StoppedAsync();
        Assert.Equal(CaptureStopReason.QueueFull, stopped.StopReason);
        Assert.False(backend.IsRunning);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(80_000L, backend.FramesDelivered);

        if (readAfterwards)
        {
            var read = await ChunkReader.ReadAllAsync(rig.Session);
            Assert.Equal(input, Bytes(read));
            AssertBlocks(read, "R41");
            await rig.CloseAsync();
            Assert.Equal(TimeSpan.Zero, rig.Session.Status.Unread);
        }
        else
        {
            await rig.CloseAsync();
            Assert.Equal(TimeSpan.FromSeconds(5), rig.Session.Status.Unread);
        }

        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(CaptureStopReason.QueueFull, rig.Session.Status.StopReason);
        rig.Statuses.AssertConsistent(CaptureStopReason.QueueFull, "R41");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    /// <summary>
    /// 48 kHz mono Float32: 0,5 s Daten, dann ein Puffer mit <c>Discontinuity</c> und <c>MissingFrames</c> 4 800 (0,1 s),
    /// der selbst schon zum Audio nach der Lücke gehört, dann der Rest der zweiten 0,5 s. Die Blöcke gehen über die
    /// echte P3-Klasse <see cref="LiveTranscription"/> mit stummem Decoder und Detektor ohne Sprache.
    /// </summary>
    [Fact]
    public async Task R49_KnownGapShiftsTheNextBlockAndP3ReportsExactlyThisSpan()
    {
        var format = new CaptureFormat(48_000, 1, CaptureSampleFormat.Float32);
        await using var rig = CaptureRig.Open(format);
        await rig.StartedAsync();
        rig.FeedFor(TimeSpan.FromSeconds(0.5));
        Assert.True(rig.Feed(rig.Signal.Next(CaptureRig.BufferLength), TimeSpan.FromMilliseconds(200), discontinuity: true, missingFrames: 4_800));
        rig.FeedFor(TimeSpan.FromSeconds(0.4));
        rig.Session.Stop();
        await rig.StoppedAsync();

        var decoder = FakeHypothesisDecoder.Silent();
        decoder.VerifyAudio = false;
        var source = new LiveTranscription(
            decoder, FakeSpeechDetector.Speech(), LiveTestKit.DefaultRecognition, LiveTestKit.Options(LiveMode.Live));
        var read = new List<AudioChunk>();
        var events = new List<TranscriptEvent>();
        var run = Task.Run(async () =>
        {
            await foreach (var item in source.RunAsync(Tee(rig.Session.ReadAsync(), read)))
            {
                events.Add(item);
            }
        });
        await run.WaitAsync(CaptureRig.Guard);

        await rig.CloseAsync();

        // Vor der Lücke ein kürzerer Block samt Nachlauf, danach genau 1 600 Samples später
        AssertSameAudio(rig.Reference(), read, "R49");
        var last = read.FindIndex(chunk => SampleCount(chunk) < BlockSamples);
        Assert.InRange(last, 0, read.Count - 2);
        var gapStart = End(read[last]);
        AssertBlocks(read[..(last + 1)], "vor der Lücke");
        AssertBlocks(read[(last + 1)..], "nach der Lücke", start: gapStart + Duration(1_600));
        Assert.InRange(TotalSamples(read[..(last + 1)]), 8_000 - CaptureConverterTests.SampleTolerance, 8_000 + CaptureConverterTests.SampleTolerance);
        Assert.InRange(TotalSamples(read[(last + 1)..]), 8_000 - CaptureConverterTests.SampleTolerance, 8_000 + CaptureConverterTests.SampleTolerance);

        var gap = Assert.Single(events.OfType<LiveNotice>(), notice => notice.Kind == LiveNoticeKind.AudioGap);
        Assert.Equal((gapStart, gapStart + TimeSpan.FromSeconds(0.1)), (gap.Start, gap.End));
        Assert.Equal(CaptureStopReason.Requested, rig.Session.Status.StopReason);
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Fact]
    public async Task R49_UnknownGapStopsWithDiscontinuityAndNeverPadsOrJoins()
    {
        var format = new CaptureFormat(48_000, 1, CaptureSampleFormat.Float32);
        await using var rig = CaptureRig.Open(format);
        var backend = await rig.StartedAsync();
        var reader = new ChunkReader(rig.Session);
        rig.FeedFor(TimeSpan.FromSeconds(0.5));

        Assert.True(rig.Feed(rig.Signal.Next(CaptureRig.BufferLength), CaptureRig.BufferLength, discontinuity: true, missingFrames: null));

        var stopped = await rig.StoppedAsync();
        Assert.Equal(CaptureStopReason.Discontinuity, stopped.StopReason);
        Assert.False(backend.IsRunning);
        Assert.Equal(1, backend.StopCalls);
        var read = await reader.CompleteAsync();

        // Nur die 0,5 s vor dem Aussetzer samt Nachlauf; der Puffer danach entfällt, nichts ist aufgefüllt
        AssertSameAudio(rig.Reference(), read, "R49");
        AssertBlocks(read, "R49");
        Assert.InRange(TotalSamples(read), 8_000 - CaptureConverterTests.SampleTolerance, 8_000 + CaptureConverterTests.SampleTolerance);
        await rig.CloseAsync();
        Assert.Equal(1, backend.DisposeCalls);
        rig.Statuses.AssertConsistent(CaptureStopReason.Discontinuity, "R49");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Fact]
    public async Task R49_ExactZerosGiveOnlyTheHintAndNoStop()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();
        var reader = new ChunkReader(rig.Session);

        for (var i = 0; i < 60; i++)
        {
            Assert.True(rig.FeedZeros(), $"Nullpuffer {i} abgewiesen.");
            await rig.KeepUpAsync(reader);
        }

        var status = await rig.WaitForAsync(state => state.SilentSignal, "SilentSignal");
        Assert.Equal(CapturePhase.Recording, status.Phase);
        Assert.Null(status.StopReason);
        Assert.True(backend.IsRunning);
        Assert.Equal(0, backend.StopCalls);

        rig.Session.Stop();
        var read = await reader.CompleteAsync();
        AssertSameAudio(rig.Reference(), read, "R49");
        Assert.All(Samples(read), sample => Assert.Equal(0, sample));
        await rig.CloseAsync();
        rig.Statuses.AssertConsistent(CaptureStopReason.Requested, "R49");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    private static async IAsyncEnumerable<AudioChunk> Tee(IAsyncEnumerable<AudioChunk> chunks, List<AudioChunk> seen)
    {
        await foreach (var chunk in chunks)
        {
            seen.Add(chunk with { Pcm16Mono = chunk.Pcm16Mono.ToArray() });
            yield return chunk;
        }
    }
}
