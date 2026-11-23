using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.App;

/// <summary>
/// „Erkennung neu starten“ von Hand (Gegencheck vom 10.11.2026, Vorstufe zur Beta): nur wenn der Erkenner nicht verfügbar
/// ist; ein bereiter, ladender oder neu startender Erkenner wird nie angefasst.
/// </summary>
public sealed class P9RestartTests
{
    [Fact]
    public async Task AnUnavailableRecognizerIsRestartedOnRequest()
    {
        await using var rig = new ControllerRig(arrange: r => r.Host.Preset(RecognizerAvailability.Unavailable, RecognizerErrorCode.ModelMissing));

        await rig.Controller.RestartRecognizerAsync().WaitAsync(ControllerRig.Timeout);

        Assert.Equal(1, rig.Host.RestartCount);
    }

    [Theory]
    [InlineData(RecognizerAvailability.Ready)]
    [InlineData(RecognizerAvailability.Starting)]
    [InlineData(RecognizerAvailability.Restarting)]
    public async Task AnyOtherStateIsLeftAlone(RecognizerAvailability availability)
    {
        await using var rig = new ControllerRig(arrange: r => r.Host.Preset(availability));

        await rig.Controller.RestartRecognizerAsync().WaitAsync(ControllerRig.Timeout);

        Assert.Equal(0, rig.Host.RestartCount);
    }

    [Fact]
    public async Task AfterDisposeThereIsNoRestart()
    {
        var rig = new ControllerRig(arrange: r => r.Host.Preset(RecognizerAvailability.Unavailable));
        await rig.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(rig.Controller.RestartRecognizerAsync);
        Assert.Equal(0, rig.Host.RestartCount);
    }
}
