using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Freihand.Adapters.Recognition;
using Freihand.Core.Platform;
using Freihand.Core.Tests.Audio.Kit;
using Freihand.Core.Tests.Recognition.Kit;
using Freihand.Core.Tests.TestKit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>PR #27, 67ec167: Gegenproben ohne Geräte, Modelle oder echte Inhalte.</summary>
public sealed class P8ImplementationReviewTests
{
    /// <summary>BE-18, P8 5.7: Abbruch und Zeitgrenze gelten auch beim Schreiben in eine volle Pipe.</summary>
    [Theory]
    [InlineData("cancel")]
    [InlineData("decode-timeout")]
    [InlineData("detect-timeout")]
    public async Task D01_BlockedPipeWriteIsBounded(string mode)
    {
        var clock = new ManualClock();
        using var cancellation = new CancellationTokenSource();
        await using var blocked = new BlockedStream();
        await using var idle = new BlockedStream();
        await using var client = mode == "detect-timeout"
            ? new RecognizerClient(idle, blocked, ClientTimeouts.Default, clock)
            : new RecognizerClient(blocked, idle, ClientTimeouts.Default, clock);
        Task call = mode == "detect-timeout"
            ? DetectAsync(client, Samples(16_000), Sec(1))
            : client.DecodeAsync(Samples(16_000), Sec(1), German(), cancellation.Token);
        try
        {
            await blocked.Writing.Task.WaitAsync(Guard);
            if (mode == "cancel")
            {
                await cancellation.CancelAsync();
                await Throws<OperationCanceledException>(call);
            }
            else
            {
                // Der Aufruf ist nachweislich im Schreiben. Die Frist muss bereits laufen.
                clock.Advance(mode == "detect-timeout" ? Sec(2) : Sec(35));
                await Throws<RecognizerUnavailableException>(call);
            }
        }
        finally
        {
            await client.DisposeAsync();
            await Observe(call);
        }
    }

    /// <summary>BE-18, P8 5.7: Dispose beendet auch Aufrufe, die gerade auf einen Neustart warten.</summary>
    [Fact]
    public async Task D02_DisposeWakesAnExistingRestartWaiter()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await SupervisorHarness.StartedAsync(n => LaunchPlan.Service(loader =>
        {
            if (n > 1) loader.Gate = load.Task;
        }));
        await using var session = await harness.SessionAsync();
        harness.Launcher.Current.Crash();
        var second = await harness.Launcher.LaunchedAsync(2);
        await Until(() => second.Loader.Calls.Count == 1, "Neustart lädt");
        var call = session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German());
        await harness.Clock.WaitForTimerDueInAsync(harness.Timeouts.RestartWait);
        try
        {
            await harness.Supervisor.DisposeAsync();
            // Kein Vorstellen der Uhr: das Freigeben selbst muss den wartenden Aufruf beenden.
            await Throws<RecognizerUnavailableException>(call);
        }
        finally
        {
            load.TrySetResult();
            harness.Clock.Advance(harness.Timeouts.RestartWait);
            await Observe(call);
        }
    }

    /// <summary>P8 5.7: Eine während RestartWait gescheiterte Sitzung darf danach keinen Decode mehr senden.</summary>
    [Fact]
    public async Task D03_FailedSessionDoesNotResumeAWaitingDecode()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await SupervisorHarness.StartedAsync(n => LaunchPlan.Service(loader =>
        {
            if (n > 1) loader.Gate = load.Task;
        }));
        await using var session = await harness.SessionAsync();
        harness.Launcher.Current.Crash();
        var second = await harness.Launcher.LaunchedAsync(2);
        await Until(() => second.Loader.Calls.Count == 1, "Neustart lädt");
        var call = session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German());
        try
        {
            await harness.Clock.WaitForTimerDueInAsync(harness.Timeouts.RestartWait);
            Assert.Throws<RecognizerUnavailableException>(() => session.Detector.Detect(Samples(16_000), Sec(1)));
            Assert.True(session.Failed);
            load.TrySetResult();
            await harness.WaitReadyAsync(2);
            var error = await Outcome(call);
            Assert.True(error is RecognizerUnavailableException && second.Whisper.Calls.Count == 0,
                $"Gescheiterte Sitzung fortgesetzt: Fehlercode={error?.GetType().Name ?? "keiner"}, Decoderaufrufe={second.Whisper.Calls.Count}.");
        }
        finally
        {
            load.TrySetResult();
            await Observe(call);
        }
    }

    /// <summary>ER-13/ER-17, P8 5.2/10.4: Auch Stop aus einem Status-Empfänger darf keinen Rücksprung melden.</summary>
    [Fact]
    public async Task D04_ReentrantStopDoesNotRegressStatusEvents()
    {
        await using var rig = CaptureRig.Open(CaptureRig.NativeFormat);
        await rig.StartedAsync();
        var seen = new ConcurrentQueue<CapturePhase>();
        // P9 darf unmittelbar auf die Bereitschaft reagieren und die Aufnahme stoppen.
        rig.Session.StatusChanged += (_, status) =>
        {
            if (status.Phase == CapturePhase.Recording) rig.Session.Stop();
        };
        rig.Session.StatusChanged += (_, status) => seen.Enqueue(status.Phase);
        Assert.True(rig.Backend.Deliver(TestSignals.Pcm16Pattern(3_200)));
        Assert.Equal(CapturePhase.Stopped, rig.Session.Status.Phase);
        Assert.Contains(CapturePhase.Stopped, seen);
        Assert.Equal(CapturePhase.Stopped, seen.Last());
    }

    /// <summary>P8 4.7 Regel 1 und 5.4: Eine Antwort auf dem falschen Kanal trennt, statt nur den Aufruf abzulehnen.</summary>
    [Fact]
    public async Task D05_WrongLaneResponseBreaksTheClient()
    {
        var decode = new DuplexPipe("D05-decode");
        var detect = new DuplexPipe("D05-detect");
        await using var client = new RecognizerClient(decode.App, detect.App, ClientTimeouts.Default, new ManualClock());
        var peer = new FrameChannel(decode.Recognizer);
        var call = client.DecodeAsync(Samples(16_000), Sec(1), German());
        var request = await peer.ExpectAsync("decode");
        await peer.SendRawAsync(FrameCodec.Encode(new JsonObject
        {
            ["type"] = "regions", ["id"] = request.Long("id"), ["regions"] = new JsonArray(),
        }));
        await Throws<RecognizerUnavailableException>(call);
    }

    /// <summary>SI-01, P8 4.7/5.5: Auch ein Fehler im Abbruch-Callback darf keinen Backendtext nach aussen tragen.</summary>
    [Fact]
    public async Task D06_CancellationCallbackFailureDoesNotEscapeWithBackendText()
    {
        const string marker = "D06-SYNTHETISCHER-BACKENDFEHLER";
        using var files = ModelFiles.Create();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = await ServiceHarness.ReadyAsync(files.Configuration());
        service.Whisper.Behavior = async call =>
        {
            using var registration = call.Token.Register(() => throw new InvalidOperationException(marker));
            registered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Token);
            return [];
        };
        await service.Decode.SendRawAsync(Messages.Decode(1, 0, Samples(16_000)));
        await registered.Task.WaitAsync(Guard);
        await service.Decode.SendRawAsync(Messages.Cancel(1));
        // Der Dienst darf mit einem neutralen Fehler enden oder den Abbruch beantworten, nie Backendtext auswerfen.
        var response = service.Decode.ReadAsync();
        await Task.WhenAny(service.DecodeRun!, response).WaitAsync(Guard);
        try { await service.Decode.SendRawAsync(Messages.Bare("shutdown")); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        var error = await Outcome(service.DecodeRun!);
        await Observe(response);
        if (error is not null) AssertNoSecrets(error, marker);
        Assert.DoesNotContain(marker, System.Text.Encoding.UTF8.GetString(service.DecodePipe.WrittenBy(PipeSide.Recognizer)));
    }

    /// <summary>P8 5.4/5.7: Ein Fehler beim Freigeben eines Kanals darf den anderen Kanal nicht offen lassen.</summary>
    [Fact]
    public async Task D08_ReleaseFailureStillClosesTheOtherChannel()
    {
        var decode = new DisposeProbeStream(throws: true);
        var detect = new DisposeProbeStream(throws: false);
        var client = new RecognizerClient(decode, detect, ClientTimeouts.Default, new ManualClock());
        try
        {
            _ = await Record.ExceptionAsync(async () => await client.DisposeAsync());
            Assert.Equal(1, decode.DisposeCalls);
            Assert.Equal(1, detect.DisposeCalls);
        }
        finally
        {
            if (detect.DisposeCalls == 0) detect.Dispose();
        }
    }

    private static async Task Observe(Task task)
    {
        try { await task.WaitAsync(Guard); }
        catch (Exception) { /* Nach der Prüfaussage: auch fehlgeschlagene Aufgaben beobachten. */ }
    }

    /// <summary>Volle Pipe: Lesen und Schreiben bleiben bis Abbruch/Freigabe offen; keine Zeitannahme.</summary>
    private class BlockedStream : Stream
    {
        private readonly CancellationTokenSource _closed = new();
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            return 0;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            Writing.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _closed.Cancel();
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DisposeProbeStream(bool throws) : BlockedStream
    {
        public int DisposeCalls { get; private set; }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                DisposeCalls++;
                if (throws) throw new IOException("D08-synthetischer-Freigabefehler");
            }
        }
    }
}
