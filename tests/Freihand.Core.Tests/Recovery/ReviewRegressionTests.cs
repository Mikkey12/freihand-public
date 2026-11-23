using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

/// <summary>
/// Regressionstests zum Codex-Review (Regressionstest): R1 vorübergehende
/// Lösch- und Lesefehler beim Neustart, R2 Schreibfehler bei der Übernahme, N1 echte Frist nach einem
/// vorübergehenden Lesefehler, F1 bekannte Fristen trotz gescheitertem Erfassen (ER-06).
/// </summary>
public sealed class ReviewRegressionTests : RecoveryTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R1_ExpiredDraftIsDeletedOnceATransientLockIsLifted(bool scanFirst)
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("abgelaufen"));
        Time.Advance(TimeSpan.FromSeconds(1));
        Time.DropAllTimers();
        Time.Advance(TimeSpan.FromHours(25));

        Files.FailDeletes = true;
        var store = CrashAndRestart();
        if (scanFirst)
        {
            var scan = store.Scan();
            Assert.Empty(scan.Drafts);
            Assert.Equal(RecoveryIssueKind.DeleteFailed, Assert.Single(scan.Issues).Kind);
        }
        else
        {
            Time.Advance(TimeSpan.FromSeconds(1));
        }

        Files.FailDeletes = false;
        Time.Advance(TimeSpan.FromSeconds(3));

        Assert.Empty(Files.Files);
    }

    [Fact]
    public void R1_ExpiredDamagedDraftIsDeletedOnceATransientLockIsLifted()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("beschädigt und alt"));
        Time.Advance(TimeSpan.FromSeconds(1));
        var name = SingleFileName();
        Files.Files[name] = (Files.Files[name].Data[..^5], Files.Files[name].WrittenAt);
        Time.DropAllTimers();
        Time.Advance(TimeSpan.FromHours(25));

        Files.FailDeletes = true;
        CrashAndRestart().Scan();
        Files.FailDeletes = false;
        Time.Advance(TimeSpan.FromSeconds(3));

        Assert.Empty(Files.Files);
    }

    [Fact]
    public void R1_DraftUnreadableOnRestartIsStillDeletedAfter24Hours()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("gesperrt beim Start"));
        Time.Advance(TimeSpan.FromSeconds(1));
        Time.DropAllTimers();
        Time.Advance(TimeSpan.FromHours(1));

        Files.FailReads = true;
        var scan = CrashAndRestart().Scan();
        Assert.Equal(RecoveryIssueKind.ReadFailed, Assert.Single(scan.Issues).Kind);
        Files.FailReads = false;

        Time.Advance(TimeSpan.FromHours(23));

        Assert.Empty(Files.Files);
    }

    [Fact]
    public void R1_PersistentDeleteFailureIsReportedOnceNotEveryTick()
    {
        var store = Open(new RecoveryOptions { HoldPeriod = TimeSpan.FromMinutes(1) });
        var draft = store.StartDraft();
        draft.Append(Fixed("gesperrt"));
        draft.MarkHandedOver();

        Files.FailDeletes = true;
        Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(10));

        Assert.Equal(RecoveryIssueKind.DeleteFailed, Assert.Single(Reported).Kind);
        Files.FailDeletes = false;
        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void R2_HandoverIsRetriedAfterAWriteFailure()
    {
        var store = Open();
        var draft = store.StartDraft();
        draft.Append(Fixed("noch nicht gesichert"));
        Files.FailWrites = true;

        Assert.Throws<RecoveryException>(draft.MarkHandedOver);
        Assert.Throws<InvalidOperationException>(() => draft.Append(Fixed("mehr")));
        Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(Files.Files);

        Files.FailWrites = false;
        Time.Advance(TimeSpan.FromSeconds(1));

        var offered = Assert.Single(CrashAndRestart().Scan().Drafts);
        Assert.Equal("noch nicht gesichert", offered.Text);
        Assert.Equal(Start, offered.HandedOverAt);
        Assert.Equal(Start + RecoveryOptions.DefaultHoldPeriod, offered.DeleteAt);
    }

    [Fact]
    public void R2_HandoverOverAnOlderVersionIsRetriedByFlush()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("alt"));
        Time.Advance(TimeSpan.FromSeconds(1));
        draft.Append(Fixed("neu"));
        Files.FailWrites = true;

        Assert.Throws<RecoveryException>(draft.MarkHandedOver);
        Assert.True(draft.IsRecording);
        Files.FailWrites = false;
        draft.Flush();

        Assert.False(draft.IsRecording);
        var offered = Assert.Single(CrashAndRestart().Scan().Drafts);
        Assert.Equal("alt neu", offered.Text);
        Assert.Equal(Start + TimeSpan.FromSeconds(1), offered.HandedOverAt);
    }

    [Fact]
    public void R2_RepeatedHandoverKeepsTheFirstHandoverTime()
    {
        var store = Open();
        var draft = store.StartDraft();
        draft.Append(Fixed("zweimal"));
        Files.FailWrites = true;
        Assert.Throws<RecoveryException>(draft.MarkHandedOver);
        Time.Advance(TimeSpan.FromSeconds(30));
        Files.FailWrites = false;

        draft.MarkHandedOver();

        Assert.Equal(Start, draft.HandedOverAt);
        Assert.Equal(Start, Assert.Single(store.Scan().Drafts).HandedOverAt);
        Assert.Throws<InvalidOperationException>(draft.MarkHandedOver);
    }

    [Fact]
    public void R2_DeletionWithoutHoldPeriodIsRetried()
    {
        var draft = Open(new RecoveryOptions { HoldPeriod = TimeSpan.Zero }).StartDraft();
        draft.Append(Fixed("sofort weg"));
        draft.Flush();
        Files.FailDeletes = true;

        Assert.Throws<RecoveryException>(draft.MarkHandedOver);
        Files.FailDeletes = false;
        Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(Files.Files);
        Assert.False(draft.IsRecording);
    }
    [Fact]
    public void N1_UnreadableAtStartupRechecksRealHandoverDeadlineAfterUnlock()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("übernommen"));
        draft.MarkHandedOver();
        Time.DropAllTimers();
        Time.Advance(TimeSpan.FromMinutes(1));

        Files.FailReads = true;
        Assert.Equal(RecoveryIssueKind.ReadFailed, Assert.Single(CrashAndRestart().Scan().Issues).Kind);
        Files.FailReads = false;

        // Haltefrist 15 Minuten ab der Übernahme, nicht 24 Stunden ab dem letzten Schreiben
        Time.Advance(Start + TimeSpan.FromMinutes(15) - Time.GetUtcNow() - TimeSpan.FromSeconds(2));
        Assert.Single(Files.Files);
        Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void N1_UnreadableAtStartupRechecksCreationDeadlineAfterUnlock()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("angelegt"));
        Time.Advance(TimeSpan.FromSeconds(1));
        Time.Advance(TimeSpan.FromHours(2));
        draft.Append(Fixed("später ergänzt"));
        Time.Advance(TimeSpan.FromSeconds(1));
        Time.DropAllTimers();

        Files.FailReads = true;
        CrashAndRestart().Scan();
        Files.FailReads = false;

        // 24 Stunden ab dem Anlegen, nicht ab dem letzten Schreiben (Stunde 26)
        Time.Advance(Start + TimeSpan.FromHours(24) - Time.GetUtcNow() - TimeSpan.FromSeconds(1));
        Assert.Single(Files.Files);
        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void N1_ReadFailureNeverExtendsAKnownEarlierDeadline()
    {
        var store = Open();
        var draft = store.StartDraft();
        draft.Append(Fixed("Frist bekannt"));
        draft.MarkHandedOver();
        Time.Advance(TimeSpan.FromMinutes(1));

        Files.FailReads = true;
        Assert.Equal(RecoveryIssueKind.ReadFailed, Assert.Single(store.Scan().Issues).Kind);

        // Weiterhin unlesbar, aber die bekannte Haltefrist gilt
        Time.Advance(TimeSpan.FromMinutes(14));
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void N1_FileThatTurnsOutDamagedAfterUnlockIsReported()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("erst gesperrt, dann kaputt"));
        Time.Advance(TimeSpan.FromSeconds(1));
        var name = SingleFileName();
        Files.Files[name] = (Files.Files[name].Data[..^3], Files.Files[name].WrittenAt);

        Files.FailReads = true;
        CrashAndRestart().Scan();
        Files.FailReads = false;
        Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(RecoveryIssueKind.Damaged, Assert.Single(Reported).Kind);
    }

    [Fact]
    public void F1_ListingFailureDoesNotSkipKnownDeadlines()
    {
        var store = Open();
        var draft = store.StartDraft();
        draft.Append(Fixed("Frist bekannt, Ablage nicht auflistbar"));
        draft.MarkHandedOver();

        Files.FailList = true;
        Assert.Equal(RecoveryIssueKind.ReadFailed, Assert.Single(store.Scan().Issues).Kind);
        Time.Advance(RecoveryOptions.DefaultHoldPeriod);

        Assert.Empty(Files.Files);
    }
}
