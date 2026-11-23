using System.Buffers.Binary;
using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.Audio.Kit;

/// <summary>Die Testhilfe selbst, ohne Produktcode: Signale, Auswertung und Attrappen messen, was sie vorgeben.</summary>
public sealed class KitTests
{
    [Theory]
    [InlineData(CaptureSampleFormat.Float32, 1e-7)]
    [InlineData(CaptureSampleFormat.Pcm16, 1.0 / 32_767)]
    [InlineData(CaptureSampleFormat.Pcm24, 1.0 / 8_388_607)]
    [InlineData(CaptureSampleFormat.Pcm32, 1.0 / int.MaxValue)]
    public void SamplesRoundTripInEveryFormat(CaptureSampleFormat sampleFormat, double step)
    {
        var format = new CaptureFormat(48_000, 6, sampleFormat);
        var buffer = TestSignals.Encode(format, 10, (frame, channel) => ((frame * 6) + channel - 30) / 60.0);
        var bytes = TestSignals.BytesPerSample(sampleFormat);

        Assert.Equal(10 * 6 * bytes, buffer.Length);
        for (var i = 0; i < 60; i++)
        {
            Assert.Equal((i - 30) / 60.0, TestSignals.Read(sampleFormat, buffer.AsSpan(i * bytes, bytes)), step);
        }
    }

    [Fact]
    public void PcmIsClampedAndFloatIsNot()
    {
        Span<byte> pcm16 = stackalloc byte[2];
        TestSignals.Write(CaptureSampleFormat.Pcm16, pcm16, -1.5);
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(pcm16));
        Span<byte> float32 = stackalloc byte[4];
        TestSignals.Write(CaptureSampleFormat.Float32, float32, 1.5);
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(float32));
    }

    [Fact]
    public void AnalysisMeasuresFrequencyAndLevelOfAGeneratedSine()
    {
        var samples = new short[16_000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Round(TestSignals.SineAt(16_000, i, 440, 0.5) * SignalAnalysis.FullScale);
        }

        Assert.Equal(440, SignalAnalysis.DominantFrequency(samples), 0.1);
        Assert.Equal(0, SignalAnalysis.Db(SignalAnalysis.Rms(samples), SignalAnalysis.SineRms(0.5)), 0.01);
    }

    [Fact]
    public void BlockCheckFindsAGapAndAWrongSize()
    {
        var block = new AudioChunk(new byte[SignalAnalysis.BlockSamples * 2], 16_000, TimeSpan.Zero);
        var next = block with { Offset = TimeSpan.FromSeconds(0.2) };
        SignalAnalysis.AssertBlocks([block, next], "lückenlos");

        Assert.ThrowsAny<Exception>(() => SignalAnalysis.AssertBlocks([block, next with { Offset = TimeSpan.FromSeconds(0.3) }], "Lücke"));
        Assert.ThrowsAny<Exception>(() => SignalAnalysis.AssertBlocks([next with { Offset = TimeSpan.Zero, Pcm16Mono = new byte[20] }, next], "zu kurz"));
    }

    [Fact]
    public void SineSourceContinuesThePhase()
    {
        var format = new CaptureFormat(48_000, 1, CaptureSampleFormat.Float32);
        var source = new SignalSource(format);

        var joined = source.Next(100).Concat(source.Next(100)).ToArray();

        Assert.Equal(TestSignals.Sine(format, 200), joined);
    }

    [Fact]
    public async Task BackendDeliversOnlyWhileRunningAndReusesItsBuffer()
    {
        var format = new CaptureFormat(16_000, 1, CaptureSampleFormat.Pcm16);
        var backend = new FakeCaptureBackend(new AudioDevice("x", "X"), format);
        byte[]? seen = null;
        ReadOnlyMemory<byte> lent = default;
        backend.DataAvailable += (_, data) =>
        {
            seen = data.Buffer.ToArray();
            lent = data.Buffer;
        };
        CaptureStopReason? stoppedWith = CaptureStopReason.Failure;
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Stopped += (_, reason) =>
        {
            stoppedWith = reason;
            confirmed.TrySetResult();
        };

        Assert.False(backend.Deliver(new byte[4]));
        backend.Start();
        Assert.True(backend.Deliver([1, 2, 3, 4]));
        Assert.Equal([1, 2, 3, 4], seen);
        Assert.All(lent.ToArray(), value => Assert.Equal(FakeCaptureBackend.Garbage, value));
        Assert.Equal([1, 2, 3, 4], backend.Delivered.Single().Buffer.ToArray());
        Assert.Equal(2, backend.FramesDelivered);

        backend.Stop();
        backend.Stop();
        await confirmed.Task.WaitAsync(CaptureRig.Guard);
        await backend.ConfirmationsDoneAsync();
        Assert.Null(stoppedWith);
        Assert.False(backend.Deliver([5, 6]));
        Assert.Equal((1, 2, 1), (backend.StartCalls, backend.StopCalls, backend.StopTransitions));
    }

    [Fact]
    public async Task FactoryCountsCallsAndFailsOnRequest()
    {
        var factory = new FakeCaptureFactory(CaptureRig.DeviceFormat) { StartFailure = CaptureStopReason.AccessDenied };
        var device = new AudioDevice("a", "A");

        var backend = factory.Open(device);
        Assert.Same(backend, await factory.CreatedAsync());
        Assert.Equal(CaptureStopReason.AccessDenied, Assert.Throws<CaptureBackendException>(backend.Start).Reason);
        factory.OpenFailure = CaptureStopReason.DeviceMissing;
        Assert.Equal(CaptureStopReason.DeviceMissing, Assert.Throws<CaptureBackendException>(() => factory.Open(device)).Reason);
        Assert.Equal([device, device], factory.Devices);
        Assert.Single(factory.Backends);
    }
}
