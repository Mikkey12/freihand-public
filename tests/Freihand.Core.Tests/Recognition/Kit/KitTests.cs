using System.Text.Json.Nodes;
using Freihand.Adapters.Recognition;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition.Kit;

/// <summary>
/// Die Testhilfe selbst, ohne Produktcode: Speicherstrom-Paar, Rahmen-Codec und Launcher-Attrappe verhalten sich wie in
/// ihren Doc-Kommentaren beschrieben (Grundlage für R13–R32, R50 und später R51).
/// </summary>
public sealed class KitTests
{
    [Fact]
    public async Task ClosingOneEndLetsThePeerDrainThenReadTheEnd()
    {
        var pipe = new DuplexPipe("KIT-1");
        await pipe.App.WriteAsync(new byte[] { 1, 2, 3 });
        pipe.App.Dispose();

        var buffer = new byte[8];
        Assert.Equal(3, await pipe.Recognizer.ReadAsync(buffer).AsTask().WaitAsync(Guard));
        Assert.Equal(0, await pipe.Recognizer.ReadAsync(buffer).AsTask().WaitAsync(Guard));
        var failure = Assert.Throws<IOException>(() => pipe.Recognizer.Write([4]));
        Assert.Contains("KIT-1", failure.Message, StringComparison.Ordinal);
        Assert.True(pipe.App.IsClosed);
        Assert.Equal([1, 2, 3], pipe.WrittenBy(PipeSide.App));
    }

    [Fact]
    public async Task SeverEndsPendingReadsOnBothSidesAndDropsUnreadData()
    {
        var pipe = new DuplexPipe("KIT-2");
        await pipe.Recognizer.WriteAsync(new byte[] { 9 });
        var pending = pipe.Recognizer.ReadAsync(new byte[4]).AsTask();
        Assert.False(pending.IsCompleted);

        pipe.Sever();

        Assert.Equal(0, await pending.WaitAsync(Guard));
        Assert.Equal(0, await pipe.App.ReadAsync(new byte[4]).AsTask().WaitAsync(Guard));
        Assert.Throws<IOException>(() => pipe.App.Write([1]));
        Assert.True(pipe.Severed);
    }

    [Fact]
    public async Task CodecRoundTripsFramesAndFloatsBitExactly()
    {
        var pipe = new DuplexPipe("KIT-3");
        float[] samples = [-1f, 0f, -0f, 0.999969f, float.Epsilon];
        var bytes = Messages.Decode(7, 625, samples);
        await new FrameChannel(pipe.App).SendRawAsync(bytes);

        var frame = await new FrameChannel(pipe.Recognizer).ExpectAsync("decode");
        Assert.Equal(7, frame.Id);
        Assert.Equal(625, frame.Long("offsetTicks"));
        Assert.Equal(samples.Length * 4, frame.Long("payloadBytes"));
        Assert.Equal(samples.Select(BitConverter.SingleToInt32Bits), FrameCodec.Floats(frame.Payload).Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(bytes, frame.Raw);
        Assert.Equal("decode", Assert.Single(pipe.SentBy(PipeSide.App)).Type);

        var failed = new Frame(new JsonObject { ["code"] = "ModelChecksum" }, [], []);
        Assert.Equal(RecognizerErrorCode.ModelChecksum, failed.Code);
        Assert.Equal(RecognizerErrorCode.VadError, new Frame(new JsonObject { ["code"] = (int)RecognizerErrorCode.VadError }, [], []).Code);
    }

    [Fact]
    public async Task LauncherOffersForeignConnectionFirstAndFailsAcceptAfterExit()
    {
        var launcher = new FakeLauncher("KIT-4")
        {
            Plans = _ => LaunchPlan.Scripted(scripted =>
            {
                scripted.Connect();
                return Task.CompletedTask;
            }) with { Foreign = ForeignConnection.BeforeDecode },
        };

        var launched = await launcher.LaunchAsync().WaitAsync(Guard);
        var foreign = await launched.AcceptAsync(CancellationToken.None).WaitAsync(Guard);
        var own = await launched.AcceptAsync(CancellationToken.None).WaitAsync(Guard);

        Assert.Equal(FakeLauncher.ForeignProcessId, foreign.ClientProcessId);
        Assert.Equal(launched.ProcessId, own.ClientProcessId);
        Assert.NotEqual(FakeLauncher.ForeignProcessId, launched.ProcessId);
        var pending = launched.AcceptAsync(CancellationToken.None);
        launched.Kill();
        launched.Kill();
        await launched.Exited.WaitAsync(Guard);
        var failure = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(Guard));
        Assert.Contains("KIT-4", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, launcher.KillCalls);
        Assert.Equal(0, await own.Stream.ReadAsync(new byte[4]).AsTask().WaitAsync(Guard));
    }

    [Fact]
    public void GuardedMemoryCountsOnlyAccessesAfterClose()
    {
        // Wie beim Prüfling: die Sicht entsteht vor dem Schliessen, jeder spätere Zugriff zählt
        var memory = new GuardedMemory([1f, 2f]);
        ReadOnlyMemory<float> view = memory.Memory;
        Assert.Equal(1f, view.Span[0]);
        memory.Close();
        Assert.Equal(0, memory.AccessesAfterClose);
        _ = view.Span[1];
        using (view.Pin())
        {
        }

        Assert.Equal(2, memory.AccessesAfterClose);
    }
}
