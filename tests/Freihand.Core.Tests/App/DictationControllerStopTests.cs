using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Ein Stopp des Mikrofons, der auf einem anderen Thread noch läuft, hält das Ende des Diktats auf: Erst danach wird die
/// Aufnahme freigegeben und das Diktat gilt als beendet (CI-Befund zu C08 nach U35-06).
/// </summary>
public sealed class DictationControllerStopTests
{
    [Fact]
    public async Task ADictationEndsOnlyAfterAPendingMicrophoneStopHasReturned()
    {
        var log = new EventLog();
        var inner = new FakeAudioCapture(log);
        using var release = new ManualResetEventSlim();
        var audio = new SlowStopAudio(inner, release);
        var factory = new FakeDictationFactory(log) { Configure = run => run.IgnoreCancellation = true };
        var keys = new FakeHotkeys(log);
        await using var controller = new DictationController(new FakeRecognizerHost(), factory, audio,
            new FakeBinding(log), new FakeTextOutput(log), new FakeGuard(log), keys, new FakeClipboard(log),
            ControllerRig.Options(ProfileStrictness.Standard));
        controller.RegisterHotkeys();
        await controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        inner.Session!.SetRecording();

        // Die Taste stoppt in der Tastenarbeit; der Stopp hängt, während die Ausgabefolge regulär endet
        keys.Press(HotkeyAction.Cancel);
        await audio.StopEntered.Task.WaitAsync(ControllerRig.Timeout);
        factory.LastRun!.Complete();
        bool endedEarly;
        try
        {
            endedEarly = await Task.WhenAny(controller.Completion, Task.Delay(1000)) == controller.Completion;
            Assert.Equal(0, inner.Session.DisposeCount);
        }
        finally
        {
            release.Set();
        }

        Assert.False(endedEarly, "Das Diktat endete, während der Stopp des Mikrofons noch lief.");
        await controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(1, inner.Session.StopCount);
        Assert.Equal(1, inner.Session.DisposeCount);
    }

    private sealed class SlowStopAudio(FakeAudioCapture inner, ManualResetEventSlim release) : IAudioCapture
    {
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AudioDevice> Devices => inner.Devices;

        public AudioDevice? DefaultDevice => inner.DefaultDevice;

        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public ICaptureSession Open(AudioDevice device) => new SlowStop(inner.Open(device), StopEntered, release);
    }

    private sealed class SlowStop(ICaptureSession inner, TaskCompletionSource entered, ManualResetEventSlim release) : ICaptureSession
    {
        public AudioDevice Device => inner.Device;

        public CaptureStatus Status => inner.Status;

        public event EventHandler<CaptureStatus>? StatusChanged
        {
            add => inner.StatusChanged += value;
            remove => inner.StatusChanged -= value;
        }

        public IAsyncEnumerable<AudioChunk> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public void Stop()
        {
            entered.TrySetResult();
            if (!release.Wait(ControllerRig.Timeout))
            {
                throw new TimeoutException();
            }

            inner.Stop();
        }
    }
}
