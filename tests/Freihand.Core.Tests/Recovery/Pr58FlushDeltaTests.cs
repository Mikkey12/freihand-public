using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

public sealed class Pr58FlushDeltaTests : RecoveryTestBase
{
    [Fact]
    public void FlushReportsAnIoErrorAndCanRetryWithoutClosingTheDraft()
    {
        var store = Open();
        var draft = store.StartDraft();
        draft.Append(Fixed("Erfundene Probe"));
        Files.FailWrites = true;
        store.Flush();
        Assert.Equal(draft.Id, Assert.Single(Reported).DraftId);
        Assert.True(draft.IsRecording);
        Files.FailWrites = false;
        store.Flush();
        using var reopened = new RecoveryStore(Files, Protector, Time);
        Assert.Contains(reopened.Scan().Drafts, saved => saved.Id == draft.Id);
        Assert.Single(Reported);
    }
}
