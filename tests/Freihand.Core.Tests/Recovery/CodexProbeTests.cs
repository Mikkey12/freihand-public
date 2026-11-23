using Freihand.Core.Recognition;
using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

/// <summary>
/// Prüftests von Codex für P5 (Branch <c>codex/pruefproben</c>, Ordner <c>codex-probes/</c>), als feste
/// Regressionstests übernommen; Testkörper und Erwartungen unverändert (ER-06, AU-12).
/// </summary>
public sealed class CodexProbeTests : RecoveryTestBase
{
    // ReviewRegressionTests.cs, Befund R1, R2
    [Fact]
    public void Review_ExpiredDeleteIsRetriedAfterStartup()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("Erfundener Text"));
        draft.Flush();
        Time.DropAllTimers();
        Time.Advance(TimeSpan.FromHours(25));
        Files.FailDeletes = true;
        var restarted = CrashAndRestart();
        Assert.NotEmpty(restarted.Scan().Issues);
        Files.FailDeletes = false;
        Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(Files.Files);
    }

    // ReviewRegressionTests.cs, Befund R1, R2
    [Fact]
    public void Review_HandoverWriteFailureRemainsRetryable()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("Erfundener Text"));
        Files.FailWrites = true;
        Assert.Throws<RecoveryException>(draft.MarkHandedOver);
        Files.FailWrites = false;
        draft.Flush();
        Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Single(CrashAndRestart().Scan().Drafts);
    }

    // FollowupBoundaryTests.cs, Befund N1, R2
    [Fact]
    public void UnreadableAtStartupRechecksRealHandoverDeadlineAfterUnlock()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("nur erfundener Text"));
        draft.MarkHandedOver();
        Files.FailReads = true;
        var restarted = CrashAndRestart();
        Assert.NotEmpty(restarted.Scan().Issues);
        Files.FailReads = false;
        Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Empty(Files.Files);
    }

    // FollowupBoundaryTests.cs, Befund N1, R2
    [Fact]
    public void UnreadableAtStartupRechecksCreationDeadlineAfterUnlock()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("erste Fassung"));
        draft.Flush();
        Time.Advance(TimeSpan.FromHours(2));
        draft.Append(Fixed("letzte Fassung"));
        draft.Flush();
        Files.FailReads = true;
        CrashAndRestart().Scan();
        Files.FailReads = false;
        Time.Advance(TimeSpan.FromHours(23));
        Assert.Empty(Files.Files);
    }

    // FollowupBoundaryTests.cs, Befund N1, R2
    [Fact]
    public void FailedHandoverExpiresWithoutWritingLateOrProvisionalText()
    {
        var draft = Open().StartDraft();
        draft.Apply(new RecognitionUpdate([Fixed("fester Text")], [Provisional("VORLAEUFIGER-MARKER")]));
        Files.FailWrites = true;
        Assert.Throws<RecoveryException>(draft.MarkHandedOver);
        Time.Advance(TimeSpan.FromMinutes(16));
        Files.FailWrites = false;
        Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(Files.Files);
        Assert.False(draft.IsRecording);
        Assert.All(Protector.PlaintextStrings, text => Assert.DoesNotContain("VORLAEUFIGER-MARKER", text));
    }

    // FollowupBoundaryTests.cs, Befund N1, R2
    [Fact]
    public void PendingHandoverSavesOnlyFixedTextAfterWriteFailure()
    {
        var draft = Open().StartDraft();
        draft.Apply(new RecognitionUpdate([Fixed("fester Text")], [Provisional("VORLAEUFIGER-MARKER")]));
        Files.FailWrites = true;
        Assert.Throws<RecoveryException>(draft.MarkHandedOver);
        Time.Advance(TimeSpan.FromSeconds(30));
        Files.FailWrites = false;
        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("fester Text", Assert.Single(CrashAndRestart().Scan().Drafts).Text);
        Assert.All(Protector.PlaintextStrings, text => Assert.DoesNotContain("VORLAEUFIGER-MARKER", text));
    }

    // SecondFollowupTests.cs, Befund N1, F1
    [Fact]
    public void PersistentReadFailureRetriesOncePerTickThenStopsAndReports()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("synthetischer Text"));
        draft.MarkHandedOver();
        Time.DropAllTimers();
        var counted = new CountingFiles(Files);
        using var store = new RecoveryStore(counted, Protector, Time);
        var reported = new List<RecoveryIssue>();
        store.IssueDetected += (_, e) => reported.Add(e.Issue);
        Files.FailReads = true;
        Assert.Single(store.Scan().Issues);
        Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(31, counted.Reads);
        Assert.Single(reported);
        Assert.Equal(RecoveryIssueKind.ReadFailed, reported[0].Kind);
        Files.FailReads = false;
        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(32, counted.Reads);
        Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(32, counted.Reads);
        Time.Advance(Start + TimeSpan.FromMinutes(15) - Time.GetUtcNow());
        Assert.Empty(Files.Files);
    }

    // SecondFollowupTests.cs, Befund N1, F1
    [Fact]
    public void KnownDeadlineIsEnforcedEvenWhenReadingTimestampAlsoFails()
    {
        var counted = new CountingFiles(Files);
        using var store = new RecoveryStore(counted, Protector, Time);
        var draft = store.StartDraft();
        draft.Append(Fixed("bekannte Haltefrist"));
        draft.MarkHandedOver();
        Files.FailReads = true;
        counted.FailTimestamp = true;
        Assert.Single(store.Scan().Issues);
        Time.Advance(TimeSpan.FromMinutes(16));
        Assert.True(counted.Deletes > 0, "Trotz bekannter abgelaufener Frist wurde kein Löschversuch ausgeführt.");
        Assert.Empty(Files.Files);
    }

    /// <summary>Zählt Lese- und Löschzugriffe und kann das Lesen des Schreibzeitpunkts scheitern lassen.</summary>
    private sealed class CountingFiles(IRecoveryFileSystem inner) : IRecoveryFileSystem
    {
        public int Reads { get; private set; }

        public int Deletes { get; private set; }

        public bool FailTimestamp { get; set; }

        public IReadOnlyList<string> ListFiles() => inner.ListFiles();

        public byte[] ReadAllBytes(string name)
        {
            Reads++;
            return inner.ReadAllBytes(name);
        }

        public void WriteAllBytes(string name, byte[] bytes) => inner.WriteAllBytes(name, bytes);

        public void Replace(string source, string destination) => inner.Replace(source, destination);

        public void Delete(string name)
        {
            Deletes++;
            inner.Delete(name);
        }

        public DateTimeOffset GetLastWriteTime(string name) =>
            FailTimestamp ? throw new IOException("Test: Zeitangabe nicht lesbar") : inner.GetLastWriteTime(name);
    }
}
