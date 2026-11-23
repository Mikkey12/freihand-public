using System.Runtime.CompilerServices;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.App;

/// <summary>Gegenproben PR 35: nur Attrappen, keine Geräte, Fenster oder erkannten Texte.</summary>
public sealed class Pr35ReviewCounterTests
{
    // ER-05/R51, P9 Kapitel 2: Stop vor jeder Freigabe, auch der Ausgabefolge.
    [Fact]
    public async Task C01_RunFailureStopsCaptureBeforeOutputEnumeratorRelease()
    {
        var log = new EventLog();
        var audio = new FakeAudioCapture(log);
        var run = new FailingRun(log);
        await using var controller = Create(log, audio, new SingleFactory(run));
        await controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        run.Fail.TrySetResult();
        await controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(1, audio.Session!.StopCount);
        Assert.True(log.IndexOf("Capture.Stop") < log.IndexOf("OutputEnumerator.Dispose"),
            "Das Mikrofon muss bereits vor der Freigabe der fehlerhaften Ausgabefolge gestoppt sein.");
    }

    // ER-05, X10: Die Anzeige muss unterschiedliche Verlustspannen unterscheiden können.
    [Fact]
    public async Task C02_LossIntervalsRemainDistinguishableInControllerUpdates()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        run.Emit(new HintOutput(null, new LiveNotice(LiveNoticeKind.AudioGap, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))));
        await rig.WaitUntil(() => rig.Updates.Count(u => u.Message == ControllerMessage.Notice) == 1, "erste Verlustspanne");
        var first = rig.Updates.Last();
        run.Emit(new HintOutput(null, new LiveNotice(LiveNoticeKind.AudioGap, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5))));
        await rig.WaitUntil(() => rig.Updates.Count(u => u.Message == ControllerMessage.Notice) == 2, "zweite Verlustspanne");
        var second = rig.Updates.Last();
        Assert.Equal(new LiveNotice(LiveNoticeKind.AudioGap, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), ReadNotice(first));
        Assert.Equal(new LiveNotice(LiveNoticeKind.AudioGap, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)), ReadNotice(second));
    }

    // ER-13, P9 Kapitel 2: LastError wird ohne zusätzlichen Aufnahmetastendruck sichtbar.
    [Fact]
    public async Task C03_UnavailableReasonsRemainDistinguishableWithoutStartingADictation()
    {
        await using var rig = new ControllerRig();
        var errors = Enum.GetValues<RecognizerErrorCode>();
        Assert.True(errors.Length >= 2);
        rig.Host.Set(RecognizerAvailability.Unavailable, errors[0]);
        var first = rig.Updates.Last();
        rig.Host.Set(RecognizerAvailability.Unavailable, errors[1]);
        Assert.Equal(errors[1], rig.Host.LastError); // Kontrolle: der Eingang hat sich geändert.
        Assert.Equal(errors[0], ReadRecognizerError(first));
        Assert.Equal(errors[1], ReadRecognizerError(rig.Updates.Last()));
    }

    // P9 Kapitel 2/3: Ein älterer Stand darf keinen bereits gemeldeten neueren überschreiben.
    [Fact]
    public async Task C04_ConcurrentPublishDoesNotDeliverRecordingAfterStopping()
    {
        var log = new EventLog();
        var audio = new FakeAudioCapture(log);
        var factory = new FakeDictationFactory(log);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var once = 0;
        var updates = new System.Collections.Concurrent.ConcurrentQueue<ControllerUpdate>();
        await using var controller = Create(log, audio, factory, line =>
        {
            if (line.StartsWith("phase=Recording", StringComparison.Ordinal) && Interlocked.Exchange(ref once, 1) == 0)
            {
                entered.TrySetResult();
                if (!release.Wait(ControllerRig.Timeout)) throw new TimeoutException();
            }
        });
        controller.Updated += (_, update) => updates.Enqueue(update);
        await controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        var recording = Task.Run(() => audio.Session!.SetRecording());
        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(ControllerRig.Timeout);
            stopping = Task.Run(() => controller.RecordAsync());
            // Eine serialisierende Korrektur darf hier warten: Frist gibt nur das Test-Gate frei, keine Latenzassertion.
            await Task.WhenAny(stopping, Task.Delay(1000));
        }
        finally
        {
            release.Set();
            await recording.WaitAsync(ControllerRig.Timeout);
            if (stopping is not null) await stopping.WaitAsync(ControllerRig.Timeout);
            factory.LastRun!.Complete();
        }
        // Der Abschluss darf nicht die beobachtete Reihenfolge verdecken.
        var phases = updates.Select(u => u.Phase).ToList();
        var firstStopping = phases.IndexOf(ControllerPhase.Stopping);
        Assert.True(firstStopping >= 0);
        Assert.DoesNotContain(ControllerPhase.Recording, phases.Skip(firstStopping + 1));
    }

    // SI-04/ER-17: Ein Wächterstopp während der Geräteauflösung darf kein Mikrofon mehr öffnen.
    [Fact]
    public async Task C05_GuardStopDuringDeviceResolutionPreventsOpening()
    {
        var log = new EventLog();
        var guard = new FakeGuard(log);
        var inner = new FakeAudioCapture(log);
        var audio = new OnDeviceAudio(inner, () => guard.Raise(GuardReason.PasswordField));
        var factory = new FakeDictationFactory(log);
        await using var controller = Create(log, audio, factory, guard: guard);
        await controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        factory.LastRun?.Complete();
        await controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(1, audio.Resolutions); // Kontrolle: Stopp lag vor Open, nicht beim Scharfschalten.
        Assert.Empty(inner.Sessions);
        Assert.Equal(1, guard.DisarmCount);
    }

    // BE-03/BE-06, P7 Kapitel 5.1: Der Nachrichtenfenster-Callback darf nicht auf den Treiber warten.
    [Theory]
    [InlineData(HotkeyAction.Record)]
    [InlineData(HotkeyAction.Cancel)]
    public async Task C08_HotkeyCallbackReturnsWhileCaptureStopIsPending(HotkeyAction action)
    {
        var log = new EventLog();
        var inner = new FakeAudioCapture(log);
        using var release = new ManualResetEventSlim();
        var audio = new BlockingStopAudio(inner, release);
        var factory = new FakeDictationFactory(log) { Configure = run => run.IgnoreCancellation = true };
        var keys = new FakeHotkeys(log);
        await using var controller = new DictationController(new FakeRecognizerHost(), factory, audio,
            new FakeBinding(log), new FakeTextOutput(log), new FakeGuard(log), keys, new FakeClipboard(log),
            ControllerRig.Options(ProfileStrictness.Standard));
        controller.RegisterHotkeys();
        await controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        inner.Session!.SetRecording();
        var press = Task.Run(() => keys.Press(action));
        bool returned;
        try
        {
            await audio.StopEntered.Task.WaitAsync(ControllerRig.Timeout);
            returned = await Task.WhenAny(press, Task.Delay(1000)) == press;
        }
        finally
        {
            release.Set();
            await press.WaitAsync(ControllerRig.Timeout);
            factory.LastRun!.Complete();
            await controller.Completion.WaitAsync(ControllerRig.Timeout);
        }
        Assert.True(returned, "Der Tastenhandler wartet synchron auf Capture.Stop statt sofort zurückzukehren.");
        Assert.Equal(1, inner.Session.StopCount);
    }

    // ER-05/R51: P3 liefert den Fehlerhinweis vor seinem Aufräumen; bereits hier muss die Aufnahme stoppen.
    [Fact]
    public async Task C09_FatalNoticeStopsBeforeProducerCleanup()
    {
        var log = new EventLog();
        var audio = new FakeAudioCapture(log);
        var run = new NoticeRun(() => audio.Session!.StopCount);
        await using var controller = Create(log, audio, new SingleFactory(run));
        await controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Equal(1, run.StopsWhenResumed);
    }

    private sealed class NoticeRun(Func<int> stops) : IDictationRun
    {
        public int StopsWhenResumed { get; private set; }
        public ModeDecision Mode => new(LiveMode.Sentence, RecognitionMode.Live, ModeReason.NotApproved);
        public void PressCommandKey() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async IAsyncEnumerable<DictationOutput> RunAsync(IAsyncEnumerable<AudioChunk> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            try
            {
                yield return new HintOutput(null, new LiveNotice(LiveNoticeKind.DecoderFailed, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
                throw new InvalidOperationException("Künstlicher Decoderfehler");
            }
            finally { StopsWhenResumed = stops(); }
        }
    }

    private sealed class BlockingStopAudio(FakeAudioCapture inner, ManualResetEventSlim release) : IAudioCapture
    {
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AudioDevice> Devices => inner.Devices;
        public AudioDevice? DefaultDevice => inner.DefaultDevice;
        public event EventHandler? DevicesChanged { add { } remove { } }
        public ICaptureSession Open(AudioDevice device) => new BlockingCapture(inner.Open(device), StopEntered, release);
    }

    private sealed class BlockingCapture(ICaptureSession inner, TaskCompletionSource entered, ManualResetEventSlim release) : ICaptureSession
    {
        public AudioDevice Device => inner.Device;
        public CaptureStatus Status => inner.Status;
        public event EventHandler<CaptureStatus>? StatusChanged
        {
            add { inner.StatusChanged += value; }
            remove { inner.StatusChanged -= value; }
        }
        public IAsyncEnumerable<AudioChunk> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public void Stop()
        {
            entered.TrySetResult();
            if (!release.Wait(ControllerRig.Timeout)) throw new TimeoutException();
            inner.Stop();
        }
    }
    // Anschluss an die im Bericht vorgeschlagene additive DTO-Erweiterung. Bei anders benannter gleichwertiger
    // Darstellung darf nur dieser Zugriff angepasst werden; Art, Anfang/Ende und Fehlercode bleiben exakte Assertions.
    private static LiveNotice? ReadNotice(ControllerUpdate update)
    {
        var property = typeof(ControllerUpdate).GetProperty("Notice");
        Assert.NotNull(property);
        return Assert.IsType<LiveNotice>(property.GetValue(update));
    }

    private static RecognizerErrorCode? ReadRecognizerError(ControllerUpdate update)
    {
        var property = typeof(ControllerUpdate).GetProperty("RecognizerError");
        Assert.NotNull(property);
        return Assert.IsType<RecognizerErrorCode>(property.GetValue(update));
    }
    private static DictationController Create(EventLog log, IAudioCapture audio, IDictationFactory factory,
        Action<string>? logger = null, FakeGuard? guard = null) => new(
        new FakeRecognizerHost(), factory, audio, new FakeBinding(log), new FakeTextOutput(log),
        guard ?? new FakeGuard(log), new FakeHotkeys(log), new FakeClipboard(log),
        ControllerRig.Options(ProfileStrictness.Standard), logger);

    private sealed class SingleFactory(IDictationRun run) : IDictationFactory
    {
        public Task<IDictationRun> StartAsync(CancellationToken cancellationToken) => Task.FromResult(run);
    }

    private sealed class FailingRun(EventLog log) : IDictationRun
    {
        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModeDecision Mode => new(LiveMode.Sentence, RecognitionMode.Live, ModeReason.NotApproved);
        public void PressCommandKey() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public IAsyncEnumerable<DictationOutput> RunAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken)
            => new FailureEnumerable(log, Fail.Task);
    }

    private sealed class FailureEnumerable(EventLog log, Task fail) : IAsyncEnumerable<DictationOutput>, IAsyncEnumerator<DictationOutput>
    {
        public DictationOutput Current => throw new InvalidOperationException();
        public IAsyncEnumerator<DictationOutput> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public async ValueTask<bool> MoveNextAsync()
        {
            await fail;
            throw new InvalidOperationException("Künstlicher Fehler ohne Inhalt");
        }
        public ValueTask DisposeAsync()
        {
            log.Add("OutputEnumerator.Dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OnDeviceAudio(FakeAudioCapture inner, Action resolve) : IAudioCapture
    {
        public int Resolutions { get; private set; }
        public IReadOnlyList<AudioDevice> Devices => inner.Devices;
        public AudioDevice? DefaultDevice
        {
            get { Resolutions++; resolve(); return inner.DefaultDevice; }
        }
        public event EventHandler? DevicesChanged { add { } remove { } }
        public ICaptureSession Open(AudioDevice device) => inner.Open(device);
    }
}
