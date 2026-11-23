using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

/// <summary>Absturz, Stromausfall, Schreibfehler und beschädigte Dateien (ER-06, Abnahme ER-06, AU-12).</summary>
public sealed class FailureTests : RecoveryTestBase
{
    [Fact]
    public void CrashBetweenWriteAndRenameKeepsThePreviousVersion()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("erster Satz."));
        Time.Advance(TimeSpan.FromSeconds(1));
        draft.Append(Fixed("zweiter Satz."));

        Files.CrashBeforeReplace = true;
        Assert.Throws<SimulatedCrashException>(() => Time.Advance(TimeSpan.FromSeconds(1)));
        Files.CrashBeforeReplace = false;
        Assert.Contains(Files.Files.Keys, name => name.EndsWith(".tmp", StringComparison.Ordinal));

        var scan = CrashAndRestart().Scan();

        // Verloren ist höchstens die letzte Sekunde
        Assert.Equal("erster Satz.", Assert.Single(scan.Drafts).Text);
        var issue = Assert.Single(scan.Issues);
        Assert.Equal(RecoveryIssueKind.IncompleteWrite, issue.Kind);
        Assert.Equal(draft.Id, issue.DraftId);
        Assert.DoesNotContain(Files.Files.Keys, name => name.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void PowerLossDuringTheFirstWriteIsReported()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("halb geschrieben"));

        Files.CrashDuringWrite = true;
        Assert.Throws<SimulatedCrashException>(() => Time.Advance(TimeSpan.FromSeconds(1)));
        Files.CrashDuringWrite = false;

        var scan = CrashAndRestart().Scan();

        Assert.Empty(scan.Drafts);
        Assert.Equal(RecoveryIssueKind.IncompleteWrite, Assert.Single(scan.Issues).Kind);
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void WriteFailureIsReportedAndRetried()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("Datenträger voll"));

        Files.FailWrites = true;
        Time.Advance(TimeSpan.FromSeconds(1));

        var issue = Assert.Single(Reported);
        Assert.Equal(RecoveryIssueKind.WriteFailed, issue.Kind);
        Assert.Equal(draft.Id, issue.DraftId);
        Assert.DoesNotContain("Datenträger voll", issue.Message, StringComparison.Ordinal);
        Assert.Equal(issue, draft.LastIssue);
        Assert.Equal(RecoveryIssueKind.WriteFailed, Assert.Throws<RecoveryException>(draft.Flush).Issue.Kind);
        Assert.Empty(Files.Files);

        Files.FailWrites = false;
        Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(draft.LastIssue);
        Assert.EndsWith(".draft", SingleFileName(), StringComparison.Ordinal);
    }

    [Fact]
    public void HandoverReportsWriteFailure()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("übernommen"));
        Files.FailWrites = true;

        Assert.Equal(RecoveryIssueKind.WriteFailed, Assert.Throws<RecoveryException>(draft.MarkHandedOver).Issue.Kind);
    }

    public static TheoryData<string> Damages => ["truncated", "extended", "flipped", "magic", "version", "empty", "key"];

    [Theory]
    [MemberData(nameof(Damages))]
    public void DamagedFileIsReportedNotOfferedAndDeletable(string damage)
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("beschädigt"));
        Time.Advance(TimeSpan.FromSeconds(1));
        var name = SingleFileName();
        Damage(name, damage);

        var store = CrashAndRestart();
        var scan = store.Scan();

        Assert.Empty(scan.Drafts);
        var issue = Assert.Single(scan.Issues);
        Assert.Equal(RecoveryIssueKind.Damaged, issue.Kind);
        Assert.Equal(draft.Id, issue.DraftId);
        Assert.Equal(name, issue.FileName);
        Assert.Single(Files.Files);

        store.Delete(draft.Id);
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void DamagedFileIsDeletedAfter24Hours()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("beschädigt"));
        Time.Advance(TimeSpan.FromSeconds(1));
        Damage(SingleFileName(), "truncated");

        CrashAndRestart().Scan();
        Time.Advance(TimeSpan.FromHours(24));

        Assert.Empty(Files.Files);
    }

    [Fact]
    public void UnknownFileIsReportedAndRemovedByDeleteAll()
    {
        var store = Open();
        Files.WriteAllBytes("fremd.txt", [1, 2, 3]);

        var issue = Assert.Single(store.Scan().Issues);
        Assert.Equal(RecoveryIssueKind.Damaged, issue.Kind);
        Assert.Null(issue.DraftId);

        store.DeleteAll();
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void IssuesFoundOnTheFirstTickAreReported()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("beschädigt"));
        Time.Advance(TimeSpan.FromSeconds(1));
        Damage(SingleFileName(), "flipped");

        CrashAndRestart();
        Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(RecoveryIssueKind.Damaged, Assert.Single(Reported).Kind);
    }

    [Fact]
    public void DeleteFailureIsReported()
    {
        var store = Open();
        var draft = store.StartDraft();
        draft.Append(Fixed("gesperrt"));
        draft.Flush();

        Files.FailDeletes = true;

        Assert.Equal(RecoveryIssueKind.DeleteFailed, Assert.Throws<RecoveryException>(() => store.Delete(draft.Id)).Issue.Kind);
        Assert.Equal(RecoveryIssueKind.DeleteFailed, Assert.Throws<RecoveryException>(store.DeleteAll).Issue.Kind);
        Assert.Single(Files.Files);
    }

    private void Damage(string name, string damage)
    {
        var (data, writtenAt) = Files.Files[name];
        byte[] damaged = damage switch
        {
            "truncated" => data[..^10],
            "extended" => [.. data, 0],
            "flipped" => Flip(data, 20),
            "magic" => Flip(data, 0),
            "version" => Flip(data, 4),
            "empty" => [],
            "key" => data,
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        Files.Files[name] = (damaged, writtenAt);
        Protector.FailUnprotect = damage == "key";
    }

    private static byte[] Flip(byte[] data, int index)
    {
        var copy = data.ToArray();
        copy[index] ^= 0xFF;
        return copy;
    }
}
