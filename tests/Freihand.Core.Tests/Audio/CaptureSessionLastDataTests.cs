using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using Freihand.Core.Tests.Audio.Kit;

namespace Freihand.Core.Tests.Audio;

/// <summary>RM11: Die Sitzung kennt den Abstand vom letzten angekommenen Gerätepuffer bis zu ihrem Stopp.</summary>
public sealed class CaptureSessionLastDataTests
{
    [Fact]
    public async Task L01_DeviceFailureReportsTheTimeSinceTheLastBuffer()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();
        rig.FeedFor(TimeSpan.FromMilliseconds(300));
        await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");
        Assert.Null(rig.Session.Status.SinceLastData);

        rig.Clock.Advance(TimeSpan.FromMilliseconds(430));
        backend.Fail(CaptureStopReason.DeviceRemoved);

        var stopped = await rig.StoppedAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(430), stopped.SinceLastData);
        await rig.CloseAsync();
    }

    [Fact]
    public async Task L02_NoSignalReportsTheWholeTimeout()
    {
        await using var rig = CaptureRig.Open();
        await rig.StartedAsync();
        rig.FeedFor(TimeSpan.FromMilliseconds(200));
        await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");

        await rig.Clock.WaitForTimersAsync();
        rig.Clock.Advance(CaptureOptions.Default.NoSignalTimeout);

        var stopped = await rig.StoppedAsync();
        Assert.Equal(CaptureOptions.Default.NoSignalTimeout, stopped.SinceLastData);
        await rig.CloseAsync();
    }

    [Fact]
    public async Task L03_NoBufferMeansNoValue()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();

        backend.Fail(CaptureStopReason.AccessDenied);

        var stopped = await rig.StoppedAsync();
        Assert.Null(stopped.SinceLastData);
        await rig.CloseAsync();
    }
}
