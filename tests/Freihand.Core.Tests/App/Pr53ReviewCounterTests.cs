using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Gegenproben der Codex-Prüfung von PR #53 (Befund 1), übernommen, dazu die gleichartigen abschliessenden Gründe. Regel:
/// Das Leerzeichen nach dem Diktat kommt nur nach einem regulären Ende, nie nach einem abschliessenden Grund (Fehler,
/// beendender Hinweis, Wächter, Mikrofonausfall, Abbrechen).
/// </summary>
public sealed class Pr53ReviewCounterTests
{
    [Theory]
    [InlineData(LiveNoticeKind.DecoderFailed)]
    [InlineData(LiveNoticeKind.DetectorFailed)]
    [InlineData(LiveNoticeKind.BufferFull)]
    public async Task NoTrailingSpaceAfterAnEndingError(LiveNoticeKind kind)
    {
        await using var rig = new ControllerRig(ControllerRig.Options(Freihand.Core.Profiles.ProfileStrictness.Standard) with { SpaceAfterDictation = true });
        var run = await rig.StartRecordingAsync();
        run.Emit(new TextOutput(new RecognitionUpdate([new TextSegment(Guid.NewGuid(), "synthetic", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1))], [])));
        await rig.WaitUntil(() => rig.Controller.Current.Entries.Count == 1, "written");
        run.Emit(new HintOutput(null, new LiveNotice(kind, TimeSpan.Zero, TimeSpan.FromSeconds(1))));
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(new[] { "synthetic" }, rig.Output.Texts);
    }

    // Gleichartig: Wächter (z. B. Sperre) und Mikrofonausfall beenden ebenfalls mit Grund
    [Fact]
    public async Task NoTrailingSpaceAfterTheGuardStops()
    {
        await using var rig = new ControllerRig(ControllerRig.Options(Freihand.Core.Profiles.ProfileStrictness.Standard) with { SpaceAfterDictation = true });
        var run = await rig.StartRecordingAsync();
        run.Emit(new TextOutput(new RecognitionUpdate([new TextSegment(Guid.NewGuid(), "synthetic", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1))], [])));
        await rig.WaitUntil(() => rig.Controller.Current.Entries.Count == 1, "written");
        rig.Guard.Raise(Freihand.Core.Platform.GuardReason.SessionLocked);
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(new[] { "synthetic" }, rig.Output.Texts);
    }

    [Fact]
    public async Task NoTrailingSpaceAfterTheMicrophoneFails()
    {
        await using var rig = new ControllerRig(ControllerRig.Options(Freihand.Core.Profiles.ProfileStrictness.Standard) with { SpaceAfterDictation = true });
        var run = await rig.StartRecordingAsync();
        run.Emit(new TextOutput(new RecognitionUpdate([new TextSegment(Guid.NewGuid(), "synthetic", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1))], [])));
        await rig.WaitUntil(() => rig.Controller.Current.Entries.Count == 1, "written");
        rig.Audio.Session!.Fail(Freihand.Core.Platform.CaptureStopReason.DeviceRemoved);
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(new[] { "synthetic" }, rig.Output.Texts);
    }
}
