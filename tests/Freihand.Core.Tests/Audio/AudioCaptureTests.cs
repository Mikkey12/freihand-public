using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using Freihand.Core.Tests.Audio.Kit;
using Freihand.Core.Tests.TestKit;

namespace Freihand.Core.Tests.Audio;

/// <summary>R42: Mikrofon nur bei Aufnahme (P8-Konzept, Kapitel 4.1, 4.8 und 5.3; ER-17).</summary>
public sealed class AudioCaptureTests
{
    private static readonly AudioDevice Headset = CaptureRig.Headset;

    private static readonly AudioDevice Monitor = new("{0.0.1.00000000}.{monitor}", "Monitor-Mikrofon");

    [Fact]
    public async Task R42_DeviceListAndChangesNeverOpenTheMicrophone()
    {
        var catalog = new FakeDeviceCatalog([Headset, Monitor], Monitor);
        var factory = new FakeCaptureFactory(CaptureRig.DeviceFormat);
        var capture = new AudioCapture(catalog, factory.Open, CaptureOptions.Default, new ManualClock());
        var changes = 0;
        capture.DevicesChanged += (_, _) => Interlocked.Increment(ref changes);

        Assert.Equal([Headset, Monitor], capture.Devices);
        Assert.Equal(Monitor, capture.DefaultDevice);
        catalog.Change([Headset], Headset);

        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Equal([Headset], capture.Devices);
        Assert.Equal(Headset, capture.DefaultDevice);
        Assert.Equal(0, factory.Calls);
        await CaptureFailureWatch.AssertNoneAsync();
    }

    [Fact]
    public async Task R42_OnlyOneSessionUntilItIsDisposed()
    {
        var catalog = new FakeDeviceCatalog([Headset, Monitor], Headset);
        var factory = new FakeCaptureFactory(CaptureRig.DeviceFormat);
        var capture = new AudioCapture(catalog, factory.Open, CaptureOptions.Default, new ManualClock());

        var first = capture.Open(Headset);
        var backend = await factory.CreatedAsync();
        await backend.Started.WaitAsync(CaptureRig.Guard);
        Assert.Equal(Headset, first.Device);

        // Zweites Open, auch für ein anderes Gerät und auch nach Stop(): abgewiesen, ohne ein Mikrofon zu öffnen
        Assert.Throws<InvalidOperationException>(() => capture.Open(Headset));
        Assert.Throws<InvalidOperationException>(() => capture.Open(Monitor));
        first.Stop();
        await StatusLog.WaitForAsync(first, status => status.Phase == CapturePhase.Stopped, "Stopped");
        Assert.Throws<InvalidOperationException>(() => capture.Open(Monitor));
        Assert.Equal(1, factory.Calls);

        // Nach DisposeAsync ist der Zugang freigegeben, und ein neues Open geht
        await first.DisposeAsync().AsTask().WaitAsync(CaptureRig.Guard);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.False(backend.IsRunning);

        var second = capture.Open(Monitor);
        var next = await factory.CreatedAsync(1);
        Assert.Equal([Headset, Monitor], factory.Devices);
        await second.DisposeAsync().AsTask().WaitAsync(CaptureRig.Guard);

        // Freigabe einer laufenden Sitzung schliesst das Mikrofon (ER-17)
        Assert.Equal(1, next.DisposeCalls);
        Assert.False(next.IsRunning);
        await backend.ConfirmationsDoneAsync();
        await next.ConfirmationsDoneAsync();
        await CaptureFailureWatch.AssertNoneAsync();
    }
}
