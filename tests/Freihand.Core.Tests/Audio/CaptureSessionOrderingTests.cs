using System.Collections.Concurrent;
using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using Freihand.Core.Tests.Audio.Kit;
using Freihand.Core.Tests.TestKit;

namespace Freihand.Core.Tests.Audio;

public sealed class CaptureSessionOrderingTests
{
    public static TheoryData<string, string, bool> Endings
    {
        get
        {
            var rows = new TheoryData<string, string, bool>();
            foreach (var first in new[] { "stop", "error", "dispose" })
            foreach (var second in new[] { "stop", "error", "dispose" })
            foreach (var concurrent in new[] { false, true })
                rows.Add(first, second, concurrent);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Endings))]
    public async Task EndingsPreserveOrderForEveryObserver(string first, string second, bool concurrent)
    {
        await using var rig = CaptureRig.Open(CaptureRig.NativeFormat);
        await rig.StartedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = new ConcurrentQueue<CaptureStatus>();
        var after = new ConcurrentQueue<CaptureStatus>();
        rig.Session.StatusChanged += (_, status) => before.Enqueue(status);
        rig.Session.StatusChanged += (_, status) =>
        {
            if (status.Phase != CapturePhase.Recording) return;
            entered.TrySetResult();
            if (concurrent) release.Task.WaitAsync(CaptureRig.Guard).GetAwaiter().GetResult();
            else
            {
                End(first);
                End(second);
            }
        };
        rig.Session.StatusChanged += (_, status) => after.Enqueue(status);

        var delivery = Task.Run(() => rig.Backend.Deliver(TestSignals.Pcm16Pattern(3_200)));
        try
        {
            await entered.Task.WaitAsync(CaptureRig.Guard);
            if (concurrent)
            {
                End(first);
                End(second);
                // Der zweite Empfänger ist noch in Recording; keine verschachtelte Meldung.
                Assert.Empty(after);
            }
        }
        finally { release.TrySetResult(); }
        Assert.True(await delivery.WaitAsync(CaptureRig.Guard));
        Assert.Equal(before.ToArray(), after.ToArray());
        Assert.Equal(CapturePhase.Recording, after.First().Phase);
        Assert.All(after.Skip(1), status =>
        {
            Assert.Equal(CapturePhase.Stopped, status.Phase);
            Assert.Equal(first == "error" ? CaptureStopReason.DeviceRemoved : CaptureStopReason.Requested, status.StopReason);
        });
        Assert.Equal(CapturePhase.Stopped, after.Last().Phase);
        Assert.False(rig.Backend.Deliver(TestSignals.Pcm16Pattern(3_200)));
        await rig.Session.DisposeAsync();
        Assert.Equal(1, rig.Backend.DisposeCalls);
        Assert.Equal(first == "error" ? 0 : 1, rig.Backend.StopCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(200), rig.Session.Status.Unread);

        void End(string ending)
        {
            switch (ending)
            {
                case "stop": rig.Session.Stop(); break;
                case "error": rig.Backend.Fail(CaptureStopReason.DeviceRemoved); break;
                default: rig.Session.DisposeAsync().AsTask().WaitAsync(CaptureRig.Guard).GetAwaiter().GetResult(); break;
            }
        }
    }

    [Fact]
    public async Task DisposeWaitsForClaimedStopWithoutHoldingTheStateLock()
    {
        var backend = new GatedBackend();
        var session = new CaptureSession(CaptureRig.Headset, _ => backend, CaptureOptions.Default, new ManualClock());
        var stop = Task.Run(session.Stop);
        await backend.Stopping.Task.WaitAsync(CaptureRig.Guard);
        var dispose = session.DisposeAsync().AsTask();
        var again = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(dispose.IsCompleted);
            Assert.False(again.IsCompleted);
            Assert.Equal(0, backend.DisposeCalls);
            // Der Backend-Callback kann auf einem anderen Thread laufen, auch während Stop blockiert.
            await Task.Run(() => backend.Fail()).WaitAsync(CaptureRig.Guard);
            Assert.Equal(CaptureStopReason.Requested, session.Status.StopReason);
        }
        finally { backend.Release.TrySetResult(); }
        await Task.WhenAll(stop, dispose, again).WaitAsync(CaptureRig.Guard);
        Assert.Equal(1, backend.DisposeCalls);
        session.Stop();
        Assert.Equal(1, backend.StopCalls);
    }

    [Fact]
    public async Task DisposeCallbacksCanReadStopAndDisposeTheSession()
    {
        var backend = new GatedBackend();
        backend.Release.TrySetResult();
        var session = new CaptureSession(CaptureRig.Headset, _ => backend, CaptureOptions.Default, new ManualClock());
        var notifications = 0;
        backend.OnDispose = () => Task.Run(() =>
        {
            Assert.Equal(CapturePhase.Stopped, session.Status.Phase);
            backend.Fail();
            session.Stop();
        }).WaitAsync(CaptureRig.Guard).GetAwaiter().GetResult();
        session.StatusChanged += (_, status) =>
        {
            Assert.Equal(CapturePhase.Stopped, status.Phase);
            // Die Freigabe ist abgeschlossen, bevor ihre Meldung fremden Code aufruft.
            Assert.True(session.DisposeAsync().IsCompletedSuccessfully);
            session.Stop();
            notifications++;
        };
        await session.DisposeAsync();
        Assert.Equal(1, notifications);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisposeAttemptsEveryCleanupAndKeepsTheFirstFailure(bool stopFails, bool timerFails)
    {
        var stopError = new InvalidOperationException("synthetic stop failure");
        var timerError = new InvalidOperationException("synthetic timer failure");
        var backendError = new InvalidOperationException("synthetic dispose failure");
        var backend = new GatedBackend { StopError = stopFails ? stopError : null, OnDispose = () => throw backendError };
        backend.Release.TrySetResult();
        var clock = new CleanupClock(timerFails ? timerError : null);
        var session = new CaptureSession(CaptureRig.Headset, _ => backend, CaptureOptions.Default, clock);
        var expected = stopFails ? stopError : timerFails ? timerError : backendError;
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask()));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask()));
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(1, clock.Timer.DisposeCalls);
        Assert.Equal(CapturePhase.Stopped, session.Status.Phase);
    }

    private sealed class CleanupClock(Exception? error) : TimeProvider
    {
        public CleanupTimer Timer { get; } = new(error);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => Timer;
    }

    private sealed class CleanupTimer(Exception? error) : ITimer
    {
        public int DisposeCalls;
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose()
        {
            DisposeCalls++;
            if (error is not null) throw error;
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class GatedBackend : ICaptureBackend
    {
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureFormat Format => CaptureRig.NativeFormat;
        public event EventHandler<CaptureData>? DataAvailable { add { } remove { } }
        public event EventHandler<CaptureStopReason?>? Stopped;
        public int StopCalls;
        public int DisposeCalls;
        public Action? OnDispose;
        public Exception? StopError;
        public void Start() { }
        public void Stop()
        {
            Interlocked.Increment(ref StopCalls);
            Stopping.TrySetResult();
            if (StopError is not null) throw StopError;
            Release.Task.WaitAsync(CaptureRig.Guard).GetAwaiter().GetResult();
        }
        public void Fail() => Stopped?.Invoke(this, CaptureStopReason.DeviceRemoved);
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCalls);
            OnDispose?.Invoke();
        }
    }
}
