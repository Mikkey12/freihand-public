using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

/// <summary>Haltefrist, Frist von 24 Stunden und Abschalten (ER-06, Grundentscheid 2).</summary>
public sealed class RetentionTests : RecoveryTestBase
{
    [Theory]
    [InlineData(15)]
    [InlineData(5)]
    [InlineData(120)]
    public void HandedOverTextIsKeptForTheHoldPeriodThenDeleted(int holdMinutes)
    {
        var hold = TimeSpan.FromMinutes(holdMinutes);
        var store = Open(new RecoveryOptions { HoldPeriod = hold });
        var draft = store.StartDraft();
        draft.Append(Fixed("an Outlook übergeben"));

        draft.MarkHandedOver();

        // Schutz gegen einen Absturz des Zielprogramms: noch anzubieten
        var offered = Assert.Single(store.Scan().Drafts);
        Assert.Equal(Start, offered.HandedOverAt);
        Assert.Equal(Start + hold, offered.DeleteAt);

        Time.Advance(hold - TimeSpan.FromSeconds(1));
        Assert.Single(Files.Files);

        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(Files.Files);
        Assert.Empty(store.Scan().Drafts);
    }

    [Fact]
    public void DefaultHoldPeriodIsFifteenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), new RecoveryOptions().HoldPeriod);
        Assert.True(new RecoveryOptions().Enabled);
    }

    [Fact]
    public void HoldPeriodZeroDeletesOnHandover()
    {
        var store = Open(new RecoveryOptions { HoldPeriod = TimeSpan.Zero });
        var draft = store.StartDraft();
        draft.Append(Fixed("sofort weg"));
        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(Files.Files);

        draft.MarkHandedOver();

        Assert.Empty(Files.Files);
    }

    [Fact]
    public void DraftWithoutHandoverIsDeletedAfter24Hours()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("nie übernommen"));
        Time.Advance(TimeSpan.FromSeconds(1));

        var restarted = CrashAndRestart();
        Assert.Equal(Start + TimeSpan.FromHours(24), Assert.Single(restarted.Scan().Drafts).DeleteAt);

        Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(2));
        Assert.Single(Files.Files);

        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void OldDraftsAreDeletedEvenWithoutScan()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("alt"));
        Time.Advance(TimeSpan.FromSeconds(1));
        CrashAndRestart();

        Time.Advance(TimeSpan.FromHours(24));

        Assert.Empty(Files.Files);
    }

    [Fact]
    public void ExpiredDraftIsNotOfferedAfterLongDowntime()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("vorgestern"));
        Time.Advance(TimeSpan.FromSeconds(1));
        Time.DropAllTimers();
        Time.Advance(TimeSpan.FromDays(2));

        var scan = CrashAndRestart().Scan();

        Assert.Empty(scan.Drafts);
        Assert.Empty(scan.Issues);
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void HoldPeriodNeverExtendsBeyond24Hours()
    {
        var store = Open(new RecoveryOptions { HoldPeriod = TimeSpan.FromHours(24) });
        var draft = store.StartDraft();
        draft.Append(Fixed("langes Diktat"));
        Time.Advance(TimeSpan.FromHours(23));

        draft.MarkHandedOver();

        Assert.Equal(Start + TimeSpan.FromHours(24), Assert.Single(store.Scan().Drafts).DeleteAt);
        Time.Advance(TimeSpan.FromHours(1));
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void OpenDraftStopsRecordingAfter24Hours()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("läuft"));

        Time.Advance(TimeSpan.FromHours(24));
        draft.Append(Fixed("noch mehr"));
        Time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(draft.IsRecording);
        Assert.Empty(Files.Files);
    }

    [Theory]
    [InlineData(-1, 1000)]
    [InlineData(24 * 60 * 60 + 1, 1000)]
    [InlineData(60, 0)]
    [InlineData(60, 1001)]
    public void InvalidOptionsAreRejected(int holdSeconds, int saveMilliseconds)
    {
        var options = new RecoveryOptions
        {
            HoldPeriod = TimeSpan.FromSeconds(holdSeconds),
            SaveInterval = TimeSpan.FromMilliseconds(saveMilliseconds),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => Open(options));
    }

    [Fact]
    public void DisabledRecoveryWritesNothing()
    {
        var store = Open(new RecoveryOptions { Enabled = false });
        var draft = store.StartDraft();

        draft.Append(Fixed("nicht sichern"));
        Time.Advance(TimeSpan.FromSeconds(5));
        draft.Flush();
        draft.MarkHandedOver();

        Assert.Empty(Files.Files);
        Assert.Empty(Protector.Plaintexts);
        Assert.Empty(store.Scan().Drafts);
    }

    [Fact]
    public void DisablingRemovesExistingDrafts()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("von vorher"));
        Time.Advance(TimeSpan.FromSeconds(1));

        var scan = CrashAndRestart(new RecoveryOptions { Enabled = false }).Scan();

        Assert.Empty(scan.Drafts);
        Assert.Empty(Files.Files);
    }
}
