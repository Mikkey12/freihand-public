using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Ergänzende Fehlerpfade der Steuerung (eigene Durchsicht): Abbrechen beim Starten, per Taste hinter laufender Tastenarbeit
/// und im Leerlauf, Beenden der App während der Aufnahme, Zwischenablagefehler, Fehler eines Abonnenten der Anzeige.
/// </summary>
public sealed class DictationControllerEdgeTests
{
    [Fact]
    public async Task CancelWhileStartingOpensNoMicrophoneAndReleasesTheGuard()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new ControllerRig(arrange: r => r.Factory.Gate = gate);

        var press = rig.Controller.RecordAsync();
        await rig.WaitUntil(() => rig.Factory.StartCount == 1, "Start der Erkennersitzung");
        await rig.Controller.CancelAsync();
        await press.WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        // Die Erkennersitzung startet nicht fertig, das Mikrofon geht nie auf, der Wächter ist wieder aus
        Assert.Empty(rig.Audio.Sessions);
        Assert.Equal(1, rig.Guard.DisarmCount);
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
        Assert.Equal(ControllerMessage.Cancelled, rig.Controller.Current.Message);
    }

    [Fact]
    public async Task CancelKeyTakesEffectAtOnceWhileAnEarlierKeyIsStillWorking()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new ControllerRig(arrange: r => r.Factory.Gate = gate);
        rig.Controller.RegisterHotkeys();

        // Die Aufnahmetaste hängt im Start der Erkennersitzung; der Abbruch wartet dahinter in der geordneten Tastenarbeit
        rig.Hotkeys.Press(HotkeyAction.Record);
        await rig.WaitUntil(() => rig.Factory.StartCount == 1, "Start der Erkennersitzung");
        rig.Hotkeys.Press(HotkeyAction.Cancel);

        // Die Absicht gilt schon bei der Rückkehr der Taste, nicht erst, wenn die Tastenarbeit den Abbruch erreicht (BE-06)
        Assert.Equal(ControllerMessage.Cancelled, rig.Controller.Current.Message);
        gate.TrySetResult();
        await rig.WaitUntil(() => rig.Controller.Current.Phase == ControllerPhase.Idle, "Leerlauf");
        Assert.Empty(rig.Audio.Sessions);
    }

    [Fact]
    public async Task CancelWhileIdleDoesNothing()
    {
        await using var rig = new ControllerRig();

        await rig.Controller.CancelAsync();

        Assert.Equal(0, rig.Guard.ArmCount);
        Assert.Equal(ControllerMessage.None, rig.Controller.Current.Message);
    }

    [Fact]
    public async Task DisposeDuringRecordingClosesTheMicrophoneAndReleasesEverything()
    {
        var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var session = rig.Audio.Session!;

        await rig.Controller.DisposeAsync().AsTask().WaitAsync(ControllerRig.Timeout);

        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(1, run.DisposeCount);
        Assert.Equal(1, rig.Guard.DisarmCount);
        await rig.DisposeAsync();
    }

    [Fact]
    public async Task AFailingClipboardIsReportedInsteadOfThrown()
    {
        await using var rig = new ControllerRig();
        var segment = new TextSegment(Guid.NewGuid(), "Erfundener Satz.", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        rig.Output.SetReport(segment.Id, new OutputReport(OutputResult.HeldInDictationWindow, string.Empty, segment.Text, OutputReason.FocusElsewhere));
        var run = await rig.StartRecordingAsync();
        run.Emit(new TextOutput(new RecognitionUpdate([segment], [])));
        await rig.WaitUntil(() => rig.Controller.Current.Entries.Count == 1, "gehaltener Eintrag");
        rig.Clipboard.Failure = new System.Runtime.InteropServices.ExternalException("Zwischenablage belegt");

        var copied = rig.Controller.CopyHeld(rig.Controller.Current.Entries[0].SegmentId);

        Assert.False(copied);
        Assert.Equal(ControllerMessage.ClipboardRefused, rig.Controller.Current.Message);
        await rig.EndAsync(run);
    }

    [Fact]
    public async Task AFailingDisplaySubscriberDoesNotEndTheDictation()
    {
        await using var rig = new ControllerRig();
        rig.Controller.Updated += (_, _) => throw new InvalidOperationException("Anzeige kaputt");

        var run = await rig.StartRecordingAsync();

        // Die Aufnahme läuft weiter; erst die Aufnahmetaste beendet sie regulär
        Assert.Equal(ControllerPhase.Recording, rig.Controller.Current.Phase);
        Assert.Equal(0, rig.Audio.Session!.StopCount);
        await rig.EndAsync(run);
        Assert.Equal(1, rig.Audio.Session!.StopCount);
    }
}
