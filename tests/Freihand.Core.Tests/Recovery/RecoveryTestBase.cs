using Freihand.Core.Recovery;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.Recovery;

/// <summary>Gemeinsame Umgebung: Testzeit, Ablage im Arbeitsspeicher, Attrappe statt DPAPI.</summary>
public abstract class RecoveryTestBase : IDisposable
{
    private RecoveryStore? _store;

    protected RecoveryTestBase()
    {
        Time = new ManualTimeProvider();
        Files = new InMemoryRecoveryFileSystem(Time);
        Start = Time.GetUtcNow();
    }

    internal ManualTimeProvider Time { get; }

    internal InMemoryRecoveryFileSystem Files { get; }

    internal FakeDataProtector Protector { get; } = new();

    protected DateTimeOffset Start { get; }

    /// <summary>Probleme, die der Takt über <see cref="RecoveryStore.IssueDetected"/> gemeldet hat.</summary>
    protected List<RecoveryIssue> Reported { get; } = [];

    public void Dispose()
    {
        _store?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static TextSegment Fixed(string text) =>
        new(Guid.NewGuid(), text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    protected static TextSegment Provisional(string text) =>
        new(Guid.NewGuid(), text, SegmentState.Provisional, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    protected RecoveryStore Open(RecoveryOptions? options = null)
    {
        _store = new RecoveryStore(Files, Protector, Time, options);
        _store.IssueDetected += (_, e) => Reported.Add(e.Issue);
        return _store;
    }

    /// <summary>Harter Abbruch ohne letztes Sichern, danach Neustart auf derselben Ablage.</summary>
    protected RecoveryStore CrashAndRestart(RecoveryOptions? options = null)
    {
        Time.DropAllTimers();
        _store = null;
        return Open(options);
    }

    protected string SingleFileName() => Assert.Single(Files.Files.Keys);
}
