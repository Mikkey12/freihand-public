using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Gegenprobe der Codex-Nachprüfung zu PR #56 am Stand 637109a, als fester Regressionstest übernommen, dazu die Regel in
/// jeder Phase: Ein Abbruch ist je Diktat genau ein Meldungsereignis, gleich ob beim Erkennerstart, während der Aufnahme,
/// beim Auslaufen nach dem Stopp oder bei zweimal gedrückter Taste. Das Diktat geht jeder Meldung ausdrücklich mit.
/// </summary>
public sealed class Pr56StartCancelCounterTests
{
    [Fact]
    public async Task OneCancelKeyDuringStartMustProduceOneCancelledEvent()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new ControllerRig(arrange: r => r.Factory.Gate = gate);
        rig.Controller.RegisterHotkeys();
        var start = rig.Controller.RecordAsync();
        await rig.WaitUntil(() => rig.Factory.StartCount == 1, "Erkennerstart wartet");
        rig.Hotkeys.Press(HotkeyAction.Cancel);
        await start.WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Single(rig.LogLines, line => line.StartsWith("ereignis meldung code=Cancelled ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoCancelKeysInOneDictationAreOneEvent()
    {
        await using var rig = new ControllerRig(arrange: r => r.Factory.Configure = run => run.IgnoreCancellation = true);
        rig.Controller.RegisterHotkeys();
        var run = await rig.StartRecordingAsync();

        rig.Hotkeys.Press(HotkeyAction.Cancel);
        rig.Hotkeys.Press(HotkeyAction.Cancel);
        await rig.WaitUntil(() => rig.Audio.Session!.StopCount >= 1, "Mikrofon gestoppt");
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Single(rig.LogLines, line => line.StartsWith("ereignis meldung code=Cancelled ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingWhileTheStoppedDictationRunsOutIsOneEvent()
    {
        await using var rig = new ControllerRig(arrange: r => r.Factory.Configure = run => run.IgnoreCancellation = true);
        rig.Controller.RegisterHotkeys();
        var run = await rig.StartRecordingAsync();
        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.WaitForPhase(ControllerPhase.Stopping);

        rig.Hotkeys.Press(HotkeyAction.Cancel);
        await rig.WaitUntil(() => rig.Controller.Current.Message == ControllerMessage.Cancelled, "abgebrochen");
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Single(rig.LogLines, line => line.StartsWith("ereignis meldung code=Cancelled ", StringComparison.Ordinal));
    }

    // Die Gegenrichtung: ein Abbruch in jedem von zwei Diktaten sind zwei Ereignisse. Beide Starts laufen wie bei der
    // Bedienung über die Tastenreihe: So kommt der zweite Start sicher nach dem eingereihten Abbruch des ersten
    // (Codex zu PR #56: ein direkter RecordAsync konnte vom noch eingereihten CancelAsync getroffen werden)
    [Fact]
    public async Task ACancelInEachOfTwoDictationsIsTwoEvents()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new ControllerRig(arrange: r => r.Factory.Gate = gate);
        rig.Controller.RegisterHotkeys();
        for (var dictation = 1; dictation <= 2; dictation++)
        {
            var number = dictation;
            rig.Hotkeys.Press(HotkeyAction.Record);
            await rig.WaitUntil(() => rig.Factory.StartCount == number, "Erkennerstart wartet");
            rig.Hotkeys.Press(HotkeyAction.Cancel);
            await rig.WaitUntil(() => Cancelled(rig) == number, "Abbruch protokolliert");
        }

        await rig.WaitUntil(() => rig.Controller.Current.Phase == ControllerPhase.Idle, "beide Diktate beendet");
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(2, Cancelled(rig));
        Assert.Equal(2, rig.Factory.StartCount);
    }

    private static int Cancelled(ControllerRig rig) =>
        rig.LogLines.Count(line => line.StartsWith("ereignis meldung code=Cancelled ", StringComparison.Ordinal));
}
