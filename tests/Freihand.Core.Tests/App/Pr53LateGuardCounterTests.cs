using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Gegenprobe der Codex-Deltaprüfung von PR #53, übernommen. Regel: Ein abschliessender Grund beendet auch ein schon
/// wartendes Leerzeichen nach dem Diktat.
/// </summary>
public sealed class Pr53LateGuardCounterTests
{
    [Fact]
    public async Task AGuardStopWhileTheTrailingSpaceWaitsMustCancelThatSpace()
    {
        await using var rig = new ControllerRig(ControllerRig.Options(ProfileStrictness.Standard) with { SpaceAfterDictation = true }, r => r.Output.HoldWritesOf(" "));
        var run = await rig.StartRecordingAsync();
        run.Emit(new TextOutput(new RecognitionUpdate([new TextSegment(Guid.NewGuid(), "synthetic", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1))], [])));
        await rig.WaitUntil(() => rig.Controller.Current.Entries.Count == 1, "text written");
        await rig.Controller.RecordAsync();
        run.Complete();
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "space waiting");
        try
        {
            rig.Guard.Raise(GuardReason.PasswordField);
            Assert.Equal(ControllerMessage.GuardStopped, rig.Controller.Current.Message);
            Assert.True(rig.Output.Calls[1].Token.IsCancellationRequested, "The optional space can still be written after GuardStopped");
        }
        finally
        {
            rig.Output.ReleaseHold();
            await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        }
    }
}
