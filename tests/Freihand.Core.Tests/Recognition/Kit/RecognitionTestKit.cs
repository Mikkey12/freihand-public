using System.Buffers;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.TestKit;

namespace Freihand.Core.Tests.Recognition.Kit;

/// <summary>Gemeinsame Bausteine der Abnahmetests zu Erkennerprotokoll, Client und Supervisor (P8-Konzept, Kapitel 7.1).</summary>
internal static class RecognitionTestKit
{
    /// <summary>Echte Zeit nur als Schutz gegen Hänger, nie als Prüfung.</summary>
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>Ein Tick: „knapp“ vor bzw. nach einer Grenze.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    public static TimeSpan Sec(double seconds) => TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    public static long Ticks(double seconds) => Sec(seconds).Ticks;

    /// <summary>Erfundene, reproduzierbare Samples in [−1, 1).</summary>
    public static float[] Samples(int count, int seed = 1)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = ((((i + 1) * 7919) + (seed * 104_729)) % 2048 - 1024) / 1024f;
        }

        return samples;
    }

    /// <summary>Optionen nach dem Konzept (Profil „Standard“: de-CH) mit den gegebenen Vokabularhinweisen.</summary>
    public static RecognitionOptions German(params string[] hints) => new("de-CH", hints);

    /// <summary>Startwerte von P3 Vertrag 4 (Fenster und Höchstdauer 10 s).</summary>
    public static LiveOptions LiveDefaults() => LiveTestKit.Options(LiveMode.Sentence);

    /// <summary>Ruft das synchrone <see cref="ISpeechDetector.Detect"/> auf einem eigenen Faden auf.</summary>
    public static Task<IReadOnlyList<SpeechRegion>> DetectAsync(ISpeechDetector detector, ReadOnlyMemory<float> audio, TimeSpan offset) =>
        Task.Run(() => detector.Detect(audio, offset));

    /// <summary>Wartet auf das Ende der Aufgabe (Schutz gegen Hänger) und liefert ihre Ausnahme oder <c>null</c>.</summary>
    public static async Task<Exception?> Outcome(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(Guard)) != task)
        {
            Assert.Fail("Zeitüberschreitung (Schutz gegen Hänger): Aufruf endet nicht.");
        }

        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>Erwartet genau diese Ausnahme; ohne Uhrvorlauf heisst das „sofort“.</summary>
    public static async Task<T> Throws<T>(Task task)
        where T : Exception
    {
        var outcome = await Outcome(task);
        Assert.NotNull(outcome);
        return Assert.IsAssignableFrom<T>(outcome);
    }

    /// <summary>SI-01: Meldung und Darstellung einer Ausnahme enthalten keine der Kennungen erfundener Texte.</summary>
    public static void AssertNoSecrets(Exception exception, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>Wartet, bis die Bedingung gilt (echte Zeit nur als Schutz gegen Hänger).</summary>
    public static Task Until(Func<bool> condition, string what) => LiveTestKit.Until(condition, what, Guard.TotalSeconds);

    /// <summary>Lässt dem Prüfling Zeit für eine Aktion, die nicht kommen darf; nur für Abwesenheitsprüfungen.</summary>
    public static Task Settle() => LiveTestKit.Settle();
}

/// <summary>
/// Supervisor mit <see cref="FakeLauncher"/>, echten Modelldateien und <see cref="ManualClock"/>. Zeichnet jede Änderung
/// von <see cref="RecognizerSupervisor.Availability"/> auf. Der Supervisor startet erst mit <see cref="StartAsync"/>.
/// </summary>
internal sealed class SupervisorHarness : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<RecognizerAvailability> _transitions = [];
    private readonly bool _ownsFiles;

    private SupervisorHarness(FakeLauncher launcher, ModelFiles files, bool ownsFiles, RecognizerConfiguration configuration, ClientTimeouts timeouts)
    {
        Launcher = launcher;
        Files = files;
        _ownsFiles = ownsFiles;
        Configuration = configuration;
        Timeouts = timeouts;
        Supervisor = new RecognizerSupervisor(launcher, configuration, timeouts, Clock);
        Supervisor.AvailabilityChanged += (_, availability) =>
        {
            lock (_gate)
            {
                _transitions.Add(availability);
            }
        };
    }

    public ManualClock Clock { get; } = new();

    public FakeLauncher Launcher { get; }

    public ModelFiles Files { get; }

    public RecognizerConfiguration Configuration { get; }

    public ClientTimeouts Timeouts { get; }

    public RecognizerSupervisor Supervisor { get; }

    public IReadOnlyList<RecognizerAvailability> Transitions
    {
        get
        {
            lock (_gate)
            {
                return [.. _transitions];
            }
        }
    }

    /// <summary>Legt alles an, startet aber nichts.</summary>
    public static SupervisorHarness Create(
        Func<int, LaunchPlan>? plans = null,
        ModelFiles? files = null,
        Func<ModelFiles, RecognizerConfiguration>? configuration = null,
        ClientTimeouts? timeouts = null)
    {
        var ownsFiles = files is null;
        files ??= ModelFiles.Create();
        var launcher = new FakeLauncher();
        if (plans is not null)
        {
            launcher.Plans = plans;
        }

        return new SupervisorHarness(
            launcher, files, ownsFiles, configuration?.Invoke(files) ?? files.Configuration(), timeouts ?? ClientTimeouts.Default);
    }

    /// <summary>Legt an, startet und wartet auf den ersten <c>Ready</c>.</summary>
    public static async Task<SupervisorHarness> StartedAsync(
        Func<int, LaunchPlan>? plans = null,
        ModelFiles? files = null,
        Func<ModelFiles, RecognizerConfiguration>? configuration = null,
        ClientTimeouts? timeouts = null)
    {
        var harness = Create(plans, files, configuration, timeouts);
        await harness.StartAsync();
        await harness.WaitReadyAsync(1);
        return harness;
    }

    public Task StartAsync()
    {
        Supervisor.Start();
        return Task.CompletedTask;
    }

    /// <summary>Wartet, bis genau <paramref name="launches"/> Starts geschehen sind und der Supervisor <c>Ready</c> meldet.</summary>
    public Task WaitReadyAsync(int launches) => RecognitionTestKit.Until(
        () => Launcher.Launches == launches && Supervisor.Availability == RecognizerAvailability.Ready,
        $"Ready nach Start {launches}");

    public Task WaitAvailabilityAsync(RecognizerAvailability availability) => RecognitionTestKit.Until(
        () => Supervisor.Availability == availability, $"Zustand {availability}");

    public Task<RecognizerSession> SessionAsync(RecognitionOptions? options = null, LiveOptions? live = null) =>
        Supervisor.StartSessionAsync(options ?? RecognitionTestKit.German(), live ?? RecognitionTestKit.LiveDefaults())
            .WaitAsync(RecognitionTestKit.Guard);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Supervisor.DisposeAsync().AsTask().WaitAsync(RecognitionTestKit.Guard);
        }
        finally
        {
            Launcher.ExitAll();
            if (_ownsFiles)
            {
                Files.Dispose();
            }
        }
    }
}

/// <summary>
/// Ein <see cref="RecognizerService"/> mit Attrappen, angesprochen über rohe Rahmen der Testseite (<see cref="FrameChannel"/>).
/// <c>configure</c> sendet der Harnisch mit den Bytes, die der Supervisor des Produkts dafür erzeugt
/// (<see cref="ConfigureCapture"/>): Kapitel 4.7 legt die JSON-Form von <c>config</c> nicht fest, die Testseite errät sie
/// darum nicht.
/// </summary>
internal sealed class ServiceHarness : IAsyncDisposable
{
    private readonly string _marker = "P8-DIENST-" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<Task> _runs = [];

    private ServiceHarness(RecognizerConfiguration configuration, Action<FakeLoader>? setup)
    {
        Configuration = configuration;
        Whisper = new FakeWhisper();
        Vad = new FakeVad();
        Loader = new FakeLoader(Whisper, Vad);
        setup?.Invoke(Loader);
        Service = new RecognizerService(Loader.Load);
        DecodePipe = new DuplexPipe(_marker);
        Decode = new FrameChannel(DecodePipe.App);
    }

    public string Marker => _marker;

    public RecognizerConfiguration Configuration { get; }

    public FakeWhisper Whisper { get; }

    public FakeVad Vad { get; }

    public FakeLoader Loader { get; }

    public RecognizerService Service { get; }

    public DuplexPipe DecodePipe { get; }

    public FrameChannel Decode { get; }

    public DuplexPipe? DetectPipe { get; private set; }

    public FrameChannel? Detect { get; private set; }

    public Task? DecodeRun { get; private set; }

    public Task? DetectRun { get; private set; }

    /// <summary>Startet den Kanal <c>decode</c> und liest dessen <c>hello</c>.</summary>
    public static async Task<ServiceHarness> CreateAsync(RecognizerConfiguration configuration, Action<FakeLoader>? setup = null)
    {
        var harness = new ServiceHarness(configuration, setup);
        harness.DecodeRun = harness.Track(harness.Service.RunDecodeChannelAsync(harness.DecodePipe.Recognizer));
        var hello = await harness.Decode.ExpectAsync("hello");
        Assert.Equal(1, hello.Long("protocol"));
        Assert.Equal("decode", hello.String("channel"));
        return harness;
    }

    /// <summary>Wie <see cref="CreateAsync"/>, dann <c>configure</c>, <c>ready</c> und der Kanal <c>detect</c>.</summary>
    public static async Task<ServiceHarness> ReadyAsync(RecognizerConfiguration configuration, Action<FakeLoader>? setup = null)
    {
        var harness = await CreateAsync(configuration, setup);
        await harness.Decode.SendRawAsync(await ConfigureCapture.CaptureAsync(configuration));
        await harness.Decode.ExpectAsync("ready");
        await harness.OpenDetectAsync();
        return harness;
    }

    /// <summary>Sendet <c>configure</c> und liefert die Antwort (<c>ready</c> oder <c>failed</c>).</summary>
    public async Task<Frame> ConfigureAsync()
    {
        await Decode.SendRawAsync(await ConfigureCapture.CaptureAsync(Configuration));
        var answer = await Decode.ReadAsync();
        Assert.NotNull(answer);
        return answer;
    }

    /// <summary>Nach <see cref="RecognizerService.Ready"/>: Kanal <c>detect</c> starten und dessen <c>hello</c> lesen.</summary>
    public async Task OpenDetectAsync()
    {
        await Service.Ready.WaitAsync(RecognitionTestKit.Guard);
        DetectPipe = new DuplexPipe(_marker);
        Detect = new FrameChannel(DetectPipe.App);
        DetectRun = Track(Service.RunDetectChannelAsync(DetectPipe.Recognizer));
        var hello = await Detect.ExpectAsync("hello");
        Assert.Equal(1, hello.Long("protocol"));
        Assert.Equal("detect", hello.String("channel"));
    }

    public FrameChannel Channel(bool decode) => decode ? Decode : Detect ?? throw new InvalidOperationException("Kanal detect fehlt.");

    public async ValueTask DisposeAsync()
    {
        Whisper.Abort();
        Vad.Abort();
        DecodePipe.Sever();
        DetectPipe?.Sever();
        foreach (var run in _runs)
        {
            await Task.WhenAny(run, Task.Delay(RecognitionTestKit.Guard));
        }
    }

    private Task Track(Task run)
    {
        _runs.Add(run);
        _ = run.ContinueWith(finished => _ = finished.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return run;
    }
}

/// <summary>
/// Holt die Bytes eines <c>configure</c>, wie der Supervisor des Produkts sie für eine Konfiguration sendet: eine
/// gescriptete Gegenseite sendet <c>hello</c> und zeichnet den nächsten Rahmen auf. Damit hängen die rohen
/// Protokolltests am Supervisor nur für diese eine Nachricht, deren JSON-Form Kapitel 4.7 nicht festlegt.
/// </summary>
internal static class ConfigureCapture
{
    public static async Task<byte[]> CaptureAsync(RecognizerConfiguration configuration)
    {
        var captured = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeLauncher
        {
            Plans = _ => LaunchPlan.Scripted(async scripted =>
            {
                var decode = scripted.Connect();
                await decode.SendAsync(Messages.Hello(1, "decode"));
                var frame = await decode.ReadAsync();
                captured.TrySetResult(frame?.Raw ?? []);
            }),
        };
        var supervisor = new RecognizerSupervisor(launcher, configuration, ClientTimeouts.Default, new ManualClock());
        try
        {
            supervisor.Start();
            var raw = await captured.Task.WaitAsync(RecognitionTestKit.Guard);
            var frames = FrameCodec.Parse(raw);
            Assert.True(frames.Count == 1 && frames[0].Type == "configure", "Der Supervisor sendet nach hello kein configure.");
            return raw;
        }
        finally
        {
            await supervisor.DisposeAsync().AsTask().WaitAsync(RecognitionTestKit.Guard);
            launcher.ExitAll();
        }
    }
}

/// <summary>
/// Speicher, der jeden Zugriff nach <see cref="Close"/> zählt (R18: der Client liest das Array nach der Rückkehr nicht
/// mehr). Jeder Zugriff auf <see cref="ReadOnlyMemory{T}"/> eines <see cref="MemoryManager{T}"/> geht über
/// <see cref="GetSpan"/> oder <see cref="Pin"/>.
/// </summary>
internal sealed class GuardedMemory(float[] array) : MemoryManager<float>
{
    private int _closed;
    private int _accessesAfterClose;

    public float[] Array { get; } = array;

    public int AccessesAfterClose => Volatile.Read(ref _accessesAfterClose);

    public void Close() => Volatile.Write(ref _closed, 1);

    public override Span<float> GetSpan()
    {
        Count();
        return Array;
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        Count();
        return Array.AsMemory(elementIndex).Pin();
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }

    private void Count()
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            Interlocked.Increment(ref _accessesAfterClose);
        }
    }
}

/// <summary>Teilprüfungen, die nur unter Windows gelten (Schreibsperre über <c>FileShare</c>, R31).</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Nur unter Windows: Unter Linux setzt .NET FileShare nur als beratende Sperre um; ein Öffnen zum "
                + "Schreiben scheitert dort nicht an einem fremden Lese-Handle mit FileShare.Read. Läuft auf dem Windows-Rechner.";
        }
    }
}
