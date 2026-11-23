using System.Diagnostics;
using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Anzeige „Mikrofon stumm“ (Bedienregel vom 19.11.2026): Die Steuerung meldet im Stand, ob das Mikrofon in
/// Windows stumm ist; das Diktat läuft weiter. Exakte Nullen zählen nicht (Monitormikrofon schaltet Pausen auf Null).
/// Protokoll ohne Inhalt.
/// </summary>
public sealed class MicrophoneSignalTests
{
    private static async Task<ControllerRig> RecordingAsync()
    {
        var rig = new ControllerRig();
        rig.Controller.RegisterHotkeys();
        rig.Hotkeys.Press(HotkeyAction.Record, Stopwatch.GetTimestamp());
        await rig.WaitUntilOpenAsync(1);
        return rig;
    }

    [Fact]
    public async Task AMutedMicrophoneIsShownWhileTheDictationGoesOn()
    {
        await using var rig = await RecordingAsync();
        var session = rig.Audio.Sessions[0];

        session.SetRecording(muted: true, silent: false);
        await rig.WaitForPhase(ControllerPhase.Recording);
        await rig.WaitUntil(() => rig.Controller.Current.Signal == MicrophoneSignal.Muted, "stumm");
        Assert.EndsWith(" mikrofon=stumm", rig.Controller.Current.LogLine(), StringComparison.Ordinal);

        // Exakte Nullen allein sind kein Hinweis (Monitormikrofon in Pausen)
        session.SetRecording(muted: false, silent: true);
        await rig.WaitUntil(() => rig.Controller.Current.Signal == MicrophoneSignal.Ok, "frei trotz Nullen");
        Assert.DoesNotContain("mikrofon=", rig.Controller.Current.LogLine(), StringComparison.Ordinal);

        session.SetRecording(muted: true, silent: true);
        await rig.WaitUntil(() => rig.Controller.Current.Signal == MicrophoneSignal.Muted, "wieder stumm");
        Assert.Equal(ControllerPhase.Recording, rig.Controller.Current.Phase);
        Assert.Equal(0, session.StopCount);
        Assert.Contains("ereignis mikrofon=stumm", rig.LogLines);
        Assert.Contains("ereignis mikrofon=frei", rig.LogLines);
    }

    [Fact]
    public async Task TheHintBelongsToTheRunningDictationOnly()
    {
        await using var rig = await RecordingAsync();
        rig.Audio.Sessions[0].SetRecording(muted: true, silent: false);
        await rig.WaitUntil(() => rig.Controller.Current.Signal == MicrophoneSignal.Muted, "stumm");

        rig.Hotkeys.Press(HotkeyAction.Record, Stopwatch.GetTimestamp());
        await rig.WaitForPhase(ControllerPhase.Stopping);
        rig.Factory.LastRun!.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        // Im Leerlauf kein Hinweis mehr, und das nächste Diktat beginnt ohne
        Assert.Equal(MicrophoneSignal.Ok, rig.Controller.Current.Signal);
        rig.Hotkeys.Press(HotkeyAction.Record, Stopwatch.GetTimestamp());
        await rig.WaitUntilOpenAsync(2);
        rig.Audio.Sessions[1].SetRecording();
        await rig.WaitForPhase(ControllerPhase.Recording);
        Assert.Equal(MicrophoneSignal.Ok, rig.Controller.Current.Signal);
    }
}
