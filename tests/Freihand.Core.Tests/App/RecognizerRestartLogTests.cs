using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Beta-Protokoll (Codex zu PR #69): je tatsächlich gestartetem Erkennerprozess ausser dem ersten eine Zeile
/// „ereignis erkenner_neustart“, je Wechsel in „nicht verfügbar“ eine Zeile „ereignis erkenner_nicht_verfuegbar“; ein
/// gleichbleibender Zustand zählt nicht noch einmal.
/// </summary>
public sealed class RecognizerRestartLogTests
{
    [Fact]
    public async Task EveryLaunchAfterTheFirstAndEveryOutageIsLoggedOnce()
    {
        await using var rig = new ControllerRig();

        rig.Host.Launch(RecognizerLaunch.First);
        rig.Host.Set(RecognizerAvailability.Restarting);
        rig.Host.Launch(RecognizerLaunch.Automatic);
        rig.Host.Set(RecognizerAvailability.Restarting);
        rig.Host.Launch(RecognizerLaunch.Automatic);
        rig.Host.Set(RecognizerAvailability.Unavailable);
        rig.Host.Set(RecognizerAvailability.Unavailable);
        rig.Host.Set(RecognizerAvailability.Starting);
        rig.Host.Launch(RecognizerLaunch.Manual);
        rig.Host.Set(RecognizerAvailability.Ready);

        await rig.WaitUntil(() => rig.LogLines.Count(line => line == "ereignis erkenner_neustart") == 3, "Neustarts");
        Assert.Single(rig.LogLines, line => line == "ereignis erkenner_nicht_verfuegbar");
    }
}
