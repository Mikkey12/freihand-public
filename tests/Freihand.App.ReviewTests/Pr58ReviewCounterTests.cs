using System.Reflection;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;

namespace Freihand.App;

[Collection(AppAssembly.Name)]
public sealed class Pr58ReviewCounterTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task ABlockedReleaseStillAllowsTheFinalSaveAfterTheSessionDeadline()
    {
        var time = new FrozenTime();
        var files = new MemoryFiles(time);
        var protector = new TestProtector();
        using var store = new RecoveryStore(files, protector, time);
        using var draft = store.StartDraft();
        draft.Append(new TextSegment(Guid.NewGuid(), "Erfundene Probe", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        var releaseGate = RecognizerSupervisor.NewSignal();
        RecognizerSupervisor.ReleaseGate = releaseGate;
        var app = new App();
        typeof(App).GetField("_recovery", Private)!.SetValue(app, store);
        typeof(App).GetField("_recognizer", Private)!.SetValue(app,
            new RecognizerSupervisor(new object(), null!, null!, TimeProvider.System));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Die Freigabe kann erst nach Rueckkehr weiterlaufen, wie bei einem wartenden Dispatcher.Invoke.
            await Task.Run(() => typeof(App).GetMethod("EndForSession", Private)!.Invoke(app, null))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.InRange(elapsed.ElapsedMilliseconds, 1800L, 4500L);
            using var reopened = new RecoveryStore(files, protector, time);
            Assert.Contains(reopened.Scan().Drafts, saved => saved.Id == draft.Id);
            Assert.False(draft.IsRecording);
        }
        finally
        {
            releaseGate.TrySetResult();
            RecognizerSupervisor.ReleaseGate = null;
        }
    }

    [Fact]
    public void ConfirmedSessionEndMustPersistThePendingDictationBeforeReturning()
    {
        var time = new FrozenTime();
        var files = new MemoryFiles(time);
        var protector = new TestProtector();
        using var store = new RecoveryStore(files, protector, time);
        using var draft = store.StartDraft();
        draft.Append(new TextSegment(Guid.NewGuid(), "Erfundener Testabschnitt", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        var app = new App();
        typeof(App).GetField("_recovery", Private)!.SetValue(app, store);

        // Shutdown in AppFakes pumpt absichtlich keinen Dispatcher. WM_ENDSESSION darf nach dieser Rueckkehr
        // den Prozess beenden; eine spaetere Dispatcheroperation ist dann keine Sicherungsgarantie.
        typeof(App).GetMethod("EndForSession", Private)!.Invoke(app, null);

        using var reopened = new RecoveryStore(files, protector, time);
        Assert.Contains(reopened.Scan().Drafts, saved => saved.Id == draft.Id);
    }

    [Fact]
    public void ConfirmedSessionEndMustReleaseTheRecognizerBeforeReturning()
    {
        RecognizerSupervisor.Releases = 0;
        RecognizerSupervisor.ReleaseGate = null;
        var app = new App();
        typeof(App).GetField("_recognizer", Private)!.SetValue(app,
            new RecognizerSupervisor(new object(), null!, null!, TimeProvider.System));

        typeof(App).GetMethod("EndForSession", Private)!.Invoke(app, null);

        Assert.Equal(1, RecognizerSupervisor.Releases);
    }

    // Eigene Ergänzung (gleiche Fehlerklasse): Auch ohne Schutz fürs Sitzungsende darf der Prozess nach OnExit enden;
    // die Entwürfe müssen dann gesichert sein, obwohl die Freigabe noch hängt und OnExit nicht auf sie wartet (C07).
    [Fact]
    public async Task ExitWithoutClosePathPersistsThePendingDictationBeforeReturning()
    {
        var time = new FrozenTime();
        var files = new MemoryFiles(time);
        var protector = new TestProtector();
        using var store = new RecoveryStore(files, protector, time);
        using var draft = store.StartDraft();
        draft.Append(new TextSegment(Guid.NewGuid(), "Erfundener Testabschnitt", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        RecognizerSupervisor.Releases = 0;
        RecognizerSupervisor.ReleaseEntered = RecognizerSupervisor.NewSignal();
        RecognizerSupervisor.ReleaseGate = RecognizerSupervisor.NewSignal();
        var app = new ExitProbe();
        typeof(App).GetField("_recovery", Private)!.SetValue(app, store);
        typeof(App).GetField("_recognizer", Private)!.SetValue(app,
            new RecognizerSupervisor(new object(), null!, null!, TimeProvider.System));
        try
        {
            var exit = Task.Run(app.Exit);
            await RecognizerSupervisor.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await Task.WhenAny(exit, Task.Delay(1000)) == exit, "OnExit wartet auf die Freigabe.");

            using var reopened = new RecoveryStore(files, protector, time);
            Assert.Contains(reopened.Scan().Drafts, saved => saved.Id == draft.Id);
        }
        finally
        {
            RecognizerSupervisor.ReleaseGate.TrySetResult();
            RecognizerSupervisor.ReleaseGate = null;
        }
    }

    private sealed class ExitProbe : App
    {
        internal void Exit() => OnExit(new System.Windows.ExitEventArgs());
    }

    private sealed class FrozenTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer();
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => [.. plaintext];
        public byte[] Unprotect(byte[] protectedData) => [.. protectedData];
    }

    private sealed class MemoryFiles(TimeProvider time) : IRecoveryFileSystem
    {
        private readonly Dictionary<string, byte[]> _data = [];
        public IReadOnlyList<string> ListFiles() => [.. _data.Keys];
        public byte[] ReadAllBytes(string name) => [.. _data[name]];
        public void WriteAllBytes(string name, byte[] data) => _data[name] = [.. data];
        public void Replace(string sourceName, string destinationName)
        {
            _data[destinationName] = _data[sourceName];
            _data.Remove(sourceName);
        }
        public void Delete(string name) => _data.Remove(name);
        public DateTimeOffset GetLastWriteTime(string name) => time.GetUtcNow();
    }
}
