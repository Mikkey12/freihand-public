using Freihand.Core.Platform;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Gegenprobe der Codex-Nachprüfung zu PR #56 am Stand 8e69ef9, als fester Regressionstest übernommen: Ein Druck auf
/// Abbrechen ergibt genau ein Meldungsereignis, obwohl zwei Schritte die Meldung setzen. Dazu: zwei abgewiesene Starts
/// bleiben zwei Ereignisse, und ausserhalb eines Diktats zählt jeder Aufruf.
/// </summary>
public sealed class Pr56DeltaCounterTests
{
    [Fact]
    public async Task OneCancelKeyPressMustProduceOneCancelledMessageEvent()
    {
        await using var rig = new ControllerRig(arrange: r => r.Factory.Configure = run => run.IgnoreCancellation = true);
        rig.Controller.RegisterHotkeys();
        var run = await rig.StartRecordingAsync();
        rig.Hotkeys.Press(HotkeyAction.Cancel);
        await rig.WaitUntil(() => rig.Audio.Session!.StopCount == 1, "Mikrofon gestoppt");
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Single(rig.LogLines, line => line.StartsWith("ereignis meldung code=Cancelled ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSameMessageInTwoDictationsIsTwoEvents()
    {
        await using var rig = new ControllerRig();
        rig.Controller.SelectDevice("nicht-vorhanden");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
            await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        }

        Assert.Equal(2, rig.LogLines.Count(line => line.StartsWith("ereignis meldung code=NoDevice ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OutsideADictationEachRefusalIsItsOwnEvent()
    {
        await using var rig = new ControllerRig(arrange: r => r.Binding.MarkedFocus.Enqueue(BindRefusal.PasswordField));

        await rig.Controller.HandOverAsync("Erfunden.", FakeBinding.NewMark()).WaitAsync(ControllerRig.Timeout);
        await rig.Controller.HandOverAsync("Erfunden.", FakeBinding.NewMark()).WaitAsync(ControllerRig.Timeout);

        Assert.Equal(2, rig.LogLines.Count(line => line.StartsWith("ereignis meldung code=HandOverRefused ", StringComparison.Ordinal)));
    }
}
