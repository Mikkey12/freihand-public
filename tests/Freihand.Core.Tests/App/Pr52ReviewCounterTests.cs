using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Recovery;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Gegenproben der Codex-Prüfung von PR #52 (Befunde 1 und 3), unverändert übernommen, dazu gleichartige Fälle derselben
/// Regeln. Regel 1: Ein Entwurf gilt erst als übernommen, wenn jeder seiner festen Abschnitte geschrieben oder von dir
/// erledigt (kopiert, ins Diktatfenster geholt) ist; kann ein Abschnitt in dieser Sitzung nicht mehr erledigt werden,
/// bleibt der ganze Entwurf stehen und wird angeboten. Regel 3: Beenden bricht laufende Arbeit ab und wartet ihr Ende ab.
/// </summary>
public sealed class Pr52ReviewCounterTests
{
    // Befund 3: Beenden lässt Übernahme weiterlaufen
    [Fact]
    public async Task DisposeCancelsAndAwaitsAnActiveHandOver()
    {
        await using var rig = new ControllerRig();
        rig.Output.HoldWritesOf("synthetic");
        var transfer = rig.Controller.HandOverAsync("synthetic", FakeBinding.NewMark());
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "transfer pending");
        try
        {
            await rig.Controller.DisposeAsync();
            Assert.True(transfer.IsCompleted, "Dispose returned with a live transfer");
            Assert.Null(rig.Binding.Bound);
        }
        finally
        {
            rig.Output.ReleaseHold();
            await transfer;
        }
    }

    // Befund 1: Sicherung unkopierter Abschnitte wird gelöscht
    [Fact]
    public async Task CopyingOneCancelledSegmentMustNotExpireAnotherNeverCopiedSegment()
    {
        var clock = new ManualTimeProvider();
        var files = new InMemoryRecoveryFileSystem(clock);
        var protector = new FakeDataProtector();
        using var store = new RecoveryStore(files, protector, clock, new RecoveryOptions { HoldPeriod = TimeSpan.Zero });
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Recovery = store;
            r.Output.HoldWritesOf("first");
        });
        var run = await rig.StartRecordingAsync();
        var first = new TextSegment(Guid.NewGuid(), "first", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var second = new TextSegment(Guid.NewGuid(), "never copied", SegmentState.Fixed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        run.Emit(new TextOutput(new RecognitionUpdate([first, second], [])));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "first pending");
        await rig.Controller.CancelAsync();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        clock.Advance(RecoveryOptions.MaxSaveInterval);
        Assert.True(rig.Controller.CopyHeld(first.Id));
        using var restart = new RecoveryStore(files, protector, clock);
        Assert.Contains(restart.Scan().Drafts, d => d.Text.Contains(second.Text, StringComparison.Ordinal));
    }

    // Gleichartig zu Befund 1: Auch „ins Diktatfenster holen“ übernimmt nicht, solange ein Abschnitt nirgends steht
    [Fact]
    public async Task TakingTheVisibleRestMustNotExpireAnInvisibleSegment()
    {
        var clock = new ManualTimeProvider();
        var files = new InMemoryRecoveryFileSystem(clock);
        var protector = new FakeDataProtector();
        using var store = new RecoveryStore(files, protector, clock, new RecoveryOptions { HoldPeriod = TimeSpan.Zero });
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Recovery = store;
            r.Output.HoldWritesOf("first");
        });
        var run = await rig.StartRecordingAsync();
        var first = new TextSegment(Guid.NewGuid(), "first", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var second = new TextSegment(Guid.NewGuid(), "never shown", SegmentState.Fixed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        run.Emit(new TextOutput(new RecognitionUpdate([first, second], [])));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "first pending");
        await rig.Controller.CancelAsync();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Equal("first", rig.Controller.TakeHeld());

        // Schon jetzt angeboten (nicht erst nach einem Neustart) und nicht übernommen
        var draft = Assert.Single(store.Scan().Drafts);
        Assert.Null(draft.HandedOverAt);
        Assert.Contains("never shown", draft.Text, StringComparison.Ordinal);
    }

    // Gleichartig zu Befund 1: Fällt ein gehaltener Eintrag aus der Liste (höchstens 100), bleibt sein Entwurf ganz stehen
    [Fact]
    public async Task AHeldEntryLeavingTheListKeepsItsDraftAndOffersIt()
    {
        var clock = new ManualTimeProvider();
        var files = new InMemoryRecoveryFileSystem(clock);
        var protector = new FakeDataProtector();
        using var store = new RecoveryStore(files, protector, clock, new RecoveryOptions { HoldPeriod = TimeSpan.Zero });
        var held = new TextSegment(Guid.NewGuid(), "gehalten und verdrängt", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Recovery = store;
            r.Output.SetReport(held.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", held.Text, OutputReason.FocusElsewhere));
        });
        var run = await rig.StartRecordingAsync();
        run.Emit(new TextOutput(new RecognitionUpdate([held], [])));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "gehaltener Abschnitt");
        await rig.EndAsync(run);
        Assert.Empty(store.Scan().Drafts);

        // Ein zweites Diktat füllt die Liste, bis der gehaltene Eintrag hinausfällt
        var second = await rig.StartRecordingAsync();
        var segments = Enumerable.Range(0, 100)
            .Select(index => new TextSegment(Guid.NewGuid(), $"Satz {index}", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)))
            .ToArray();
        second.Emit(new TextOutput(new RecognitionUpdate(segments, [])));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 101, "hundert weitere Abschnitte");
        await rig.EndAsync(second);

        Assert.DoesNotContain(rig.Controller.Current.Entries, entry => entry.SegmentId == held.Id);
        var offered = Assert.Single(store.Scan().Drafts, draft => draft.Text.Contains(held.Text, StringComparison.Ordinal));
        Assert.Null(offered.HandedOverAt);
    }
}
