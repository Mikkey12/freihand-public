using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Befunde aus dem Handtest P9 vom 11.11.2026. Regel A: Nach jedem Diktat,
/// dessen letzter Abschnitt ganz in ein fremdes Feld geschrieben ist, folgt ein Leerzeichen. Regel B: Gerettet ist nur
/// Text, der nirgends angekommen ist; nur er ruft nach dir. Erfundene Texte.
/// </summary>
public sealed class P9HandtestTests
{
    private static ControllerOptions WithSpace(TimeSpan? limit = null) =>
        ControllerRig.Options(ProfileStrictness.Standard) with
        {
            SpaceAfterDictation = true,
            SpaceAfterDictationLimit = limit ?? TimeSpan.FromSeconds(5),
        };

    // ---- Regel A: Leerzeichen nach dem Diktat ---------------------------------------------------------------------

    [Fact]
    public async Task A_AfterAFullyWrittenDictationASpaceFollowsIntoTheSameField()
    {
        await using var rig = new ControllerRig(WithSpace());

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed("Erster Satz."), Fixed("Zweiter Satz.")));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "zwei Abschnitte");
        await rig.EndAsync(run);

        Assert.Equal(["Erster Satz.", "Zweiter Satz.", " "], rig.Output.Texts);
        var calls = rig.Output.Calls;
        Assert.Equal(calls[0].Target, calls[2].Target);
        Assert.Equal(SegmentState.Fixed, calls[2].Segment.State);
        Assert.True(rig.Log.IndexOf("Output.Done: ") < rig.Log.IndexOf("Binding.Release"), "Das Leerzeichen kommt vor dem Lösen der Bindung");
        // Kein diktierter Text: kein Eintrag
        Assert.Equal(2, rig.Controller.Current.Entries.Count);
    }

    [Fact]
    public async Task A_WithoutTheSwitchNothingIsAdded()
    {
        await using var rig = new ControllerRig();

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed("Ein Satz.")));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "ein Abschnitt");
        await rig.EndAsync(run);

        Assert.Equal(["Ein Satz."], rig.Output.Texts);
    }

    [Fact]
    public async Task A_NoSpaceWhenTheLastSegmentWasHeld()
    {
        var last = Fixed("Gehalten.");
        await using var rig = new ControllerRig(WithSpace(), r =>
            r.Output.SetReport(last.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", last.Text, OutputReason.FocusElsewhere)));

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed("Geschrieben."), last));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "zwei Abschnitte");
        await rig.EndAsync(run);

        Assert.Equal(["Geschrieben.", "Gehalten."], rig.Output.Texts);
    }

    [Fact]
    public async Task A_NoSpaceAfterCancel()
    {
        await using var rig = new ControllerRig(WithSpace());

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed("Vor dem Abbruch.")));
        await rig.WaitUntil(() => rig.Controller.Current.Entries.Count == 1, "geschrieben");
        await rig.Controller.CancelAsync();
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Equal(["Vor dem Abbruch."], rig.Output.Texts);
    }

    [Fact]
    public async Task A_NoSpaceInTheDictationWindowAndNoneWithoutText()
    {
        await using (var pad = new ControllerRig(WithSpace(), r => r.Pad = new FakePad(r.Log) { Focused = true }))
        {
            var run = await pad.StartRecordingAsync();
            run.Emit(Output(Fixed("Ins Fenster.")));
            await pad.WaitUntil(() => pad.Controller.Current.Entries.Count == 1, "eingefügt");
            await pad.EndAsync(run);

            Assert.Equal("Ins Fenster.", pad.Pad!.Text);
            Assert.Empty(pad.Output.Calls);
        }

        await using var empty = new ControllerRig(WithSpace());
        var silent = await empty.StartRecordingAsync();
        await empty.EndAsync(silent);
        Assert.Empty(empty.Output.Calls);
    }

    [Fact]
    public async Task A_ASpaceThatCannotBeWrittenNeverHoldsTheEndOfTheDictation()
    {
        await using var rig = new ControllerRig(WithSpace(TimeSpan.FromMilliseconds(100)), r => r.Output.HoldWritesOf(" "));

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed("Ein Satz.")));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "ein Abschnitt");
        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        run.Complete();

        // Die Grenze von 100 ms bricht das hängende Leerzeichen ab; das Diktat endet und gibt frei
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
        Assert.True(rig.Log.Contains("Binding.Release"));
        Assert.Equal(ControllerMessage.None, rig.Controller.Current.Message);
    }

    // ---- Regel B: Gerettet ist nur, was nirgends angekommen ist ------------------------------------------------------

    [Fact]
    public void B_OnlyDraftsNotHandedOverAreRescued()
    {
        var now = DateTimeOffset.UtcNow;
        var handedOver = Draft(now.AddMinutes(-3), now.AddMinutes(-2));
        var older = Draft(now.AddMinutes(-10), null);
        var newer = Draft(now.AddMinutes(-1), null);
        var scan = new RecoveryScan([handedOver, older, newer], []);

        Assert.True(DraftOffers.IsRescued(newer));
        Assert.False(DraftOffers.IsRescued(handedOver));
        Assert.Equal(2, DraftOffers.Rescued(scan));
        Assert.Equal([newer, older, handedOver], DraftOffers.Ordered(scan));
        Assert.True(DraftOffers.NeedsAttention(scan));
    }

    [Fact]
    public void B_OnlyHandedOverTextDoesNotCallForAttentionButIssuesDo()
    {
        var now = DateTimeOffset.UtcNow;
        var quiet = new RecoveryScan([Draft(now.AddMinutes(-3), now.AddMinutes(-2))], []);
        var damaged = new RecoveryScan([], [new RecoveryIssue(RecoveryIssueKind.Damaged, "x.draft", null, "beschädigt")]);

        Assert.Equal(0, DraftOffers.Rescued(quiet));
        Assert.False(DraftOffers.NeedsAttention(quiet));
        Assert.True(DraftOffers.NeedsAttention(damaged));
    }

    private static RecoverableDraft Draft(DateTimeOffset updated, DateTimeOffset? handedOver) =>
        new(Guid.NewGuid(), updated.AddMinutes(-1), updated, handedOver, updated.AddHours(1), [new RecoveredSegment(Guid.NewGuid(), "Text")]);

    private static TextSegment Fixed(string text) =>
        new(Guid.NewGuid(), text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    private static TextOutput Output(params TextSegment[] fixedSegments) => new(new RecognitionUpdate(fixedSegments, []));
}
