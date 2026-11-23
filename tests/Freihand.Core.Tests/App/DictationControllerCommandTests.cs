using Freihand.Adapters.Dictation;
using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Gegencheck vom 10.11.2026: Befehle und Befehlshinweise verschwinden nicht still. Der Pilot führt Befehle noch nicht aus,
/// meldet sie aber sichtbar; ins Protokoll gehen nur Codes, nie Text (SI-01).
/// </summary>
public sealed class DictationControllerCommandTests
{
    [Fact]
    public async Task RecognizedCommandIsReportedAsNotSupportedAndWritesNothing()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var command = BuiltInCommands.Pilot.Commands[0];

        run.Emit(new CommandOutput(new RunCommand(command, TimeSpan.Zero, TimeSpan.FromSeconds(1))));
        await rig.WaitUntil(
            () => rig.Updates.Any(update => update.Message == ControllerMessage.CommandNotSupported && update.Reason == command.Id),
            "Meldung zum Befehl");
        await rig.EndAsync(run);

        Assert.Empty(rig.Output.Calls);
        Assert.Contains(rig.LogLines, line => line.Contains($"meldung=CommandNotSupported grund={command.Id} ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommandHintIsReportedWithoutItsText()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var utterance = new Utterance(Guid.NewGuid(), [], TimeSpan.Zero, TimeSpan.FromSeconds(1), UtteranceEnd.CommandKey);

        run.Emit(new HintOutput(new CommandHint(utterance, "Keine Befehlswendung erkannt."), null));
        await rig.WaitUntil(() => rig.Updates.Any(update => update.Message == ControllerMessage.CommandHint), "Befehlshinweis");
        await rig.EndAsync(run);

        Assert.Empty(rig.Output.Calls);
        var hint = rig.Updates.First(update => update.Message == ControllerMessage.CommandHint);
        Assert.Null(hint.Reason);
        Assert.DoesNotContain(rig.LogLines, line => line.Contains("Befehlswendung", StringComparison.Ordinal));
    }

    // Codex zu PR #47: Meldungen aus dem Diktat überschreiben nie den Grund, mit dem es endet (BufferFull, Fehler, Abbruch)
    [Fact]
    public async Task MessagesFromTheDictationNeverReplaceTheReasonItEndsWith()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var utterance = new Utterance(Guid.NewGuid(), [], TimeSpan.Zero, TimeSpan.FromSeconds(1), UtteranceEnd.CommandKey);

        run.Emit(new HintOutput(null, new LiveNotice(LiveNoticeKind.BufferFull, TimeSpan.Zero, TimeSpan.FromSeconds(1))));
        await rig.WaitForPhase(ControllerPhase.Stopping);
        run.Emit(new CommandOutput(new RunCommand(BuiltInCommands.Pilot.Commands[0], TimeSpan.Zero, TimeSpan.FromSeconds(1))));
        run.Emit(new HintOutput(new CommandHint(utterance, "Keine Befehlswendung erkannt."), null));
        run.Emit(new HintOutput(null, new LiveNotice(LiveNoticeKind.BacklogCleared, TimeSpan.Zero, TimeSpan.FromSeconds(2))));
        await rig.EndAsync(run);

        Assert.Equal(ControllerMessage.Notice, rig.Controller.Current.Message);
        Assert.Equal(nameof(LiveNoticeKind.BufferFull), rig.Controller.Current.Reason);
        Assert.DoesNotContain(rig.Updates, update => update.Message is ControllerMessage.CommandNotSupported or ControllerMessage.CommandHint);
    }

    // Codex zu PR #50: Ein langsamer oder wartender Schreibauftrag hält eine beendende Meldung nicht auf; das Mikrofon geht
    // sofort zu, der Abschnitt wird danach noch fertig geschrieben
    [Fact]
    public async Task EndingNoticeStopsTheMicrophoneWhileAWriteIsStillRunning()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var session = rig.Audio.Session!;
        const string text = "Ein Abschnitt, der lange zum Schreiben braucht";
        rig.Output.HoldWritesOf(text);

        run.Emit(new TextOutput(new RecognitionUpdate(
            [new Freihand.Core.Segments.TextSegment(Guid.NewGuid(), text, Freihand.Core.Segments.SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1))], [])));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreibauftrag läuft");
        run.Emit(new HintOutput(null, new LiveNotice(LiveNoticeKind.BufferFull, TimeSpan.Zero, TimeSpan.FromSeconds(1))));

        await rig.WaitForPhase(ControllerPhase.Stopping);
        Assert.Equal(1, session.StopCount);
        Assert.Equal(nameof(LiveNoticeKind.BufferFull), rig.Controller.Current.Reason);

        rig.Output.ReleaseHold();
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal([text], rig.Output.Texts);
    }

    // Codex zu PR #50: Ein Schreibfehler beendet das ganze Diktat; der betroffene und jeder weitere Abschnitt bleibt gehalten
    [Fact]
    public async Task WriteFailureEndsTheDictationAndHoldsEveryRemainingSegment()
    {
        await using var rig = new ControllerRig(arrange: r => r.Output.FailOn = "Erster Abschnitt");
        var run = await rig.StartRecordingAsync();
        var session = rig.Audio.Session!;

        run.Emit(new TextOutput(new RecognitionUpdate([Fixed("Erster Abschnitt"), Fixed("Zweiter Abschnitt")], [])));
        run.Emit(new TextOutput(new RecognitionUpdate([Fixed("Dritter Abschnitt")], [])));
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Equal(1, session.StopCount);
        Assert.Equal(ControllerMessage.DictationFailed, rig.Controller.Current.Message);
        var entries = rig.Controller.Current.Entries;
        Assert.Equal(
            [("Erster Abschnitt", OutputReason.InjectionBlocked), ("Zweiter Abschnitt", OutputReason.PreviousHeld), ("Dritter Abschnitt", OutputReason.PreviousHeld)],
            entries.Select(entry => (entry.Held, entry.Reason)));
        Assert.All(entries, entry => Assert.Equal(EntryStatus.Held, entry.Status));
        Assert.Equal(["Erster Abschnitt"], rig.Output.Texts);
    }

    private static Freihand.Core.Segments.TextSegment Fixed(string text) =>
        new(Guid.NewGuid(), text, Freihand.Core.Segments.SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task HintWithNoticeStillHandlesTheNotice()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var utterance = new Utterance(Guid.NewGuid(), [], TimeSpan.Zero, TimeSpan.FromSeconds(1), UtteranceEnd.CommandKey);
        var notice = new LiveNotice(LiveNoticeKind.AudioGap, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        run.Emit(new HintOutput(new CommandHint(utterance, "Keine Befehlswendung erkannt."), notice));
        await rig.WaitUntil(() => rig.Updates.Any(update => update.Notice == notice), "Hinweis mit Spanne");
        await rig.EndAsync(run);

        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.CommandHint);
    }
}
