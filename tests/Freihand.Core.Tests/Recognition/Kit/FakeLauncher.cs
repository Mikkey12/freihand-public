using System.Collections.Concurrent;
using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.Recognition.Kit;

/// <summary>Wo die Attrappe eine Verbindung mit fremder Prozesskennung einschiebt (R30).</summary>
internal enum ForeignConnection
{
    None,

    /// <summary>Als erste Verbindung, vor <c>decode</c>.</summary>
    BeforeDecode,

    /// <summary>Nach <c>ready</c>, vor <c>detect</c>.</summary>
    BeforeDetect,
}

/// <summary>
/// Wie ein Start der Attrappe abläuft: echter <see cref="RecognizerService"/> mit Attrappen (Lade-Attrappe einstellbar)
/// oder eine gescriptete Gegenseite, die rohe Rahmen sendet (z. B. <c>hello</c> mit <c>protocol</c> 2).
/// </summary>
internal sealed record LaunchPlan(
    Action<FakeLoader>? Loader = null,
    Func<ScriptedRecognizer, Task>? Script = null,
    ForeignConnection Foreign = ForeignConnection.None)
{
    public static LaunchPlan Service(Action<FakeLoader>? loader = null, ForeignConnection foreign = ForeignConnection.None) =>
        new(loader, null, foreign);

    public static LaunchPlan Scripted(Func<ScriptedRecognizer, Task> script) => new(null, script);
}

/// <summary>
/// Attrappe für <see cref="IRecognizerLauncher"/> (Kapitel 6): Jeder <see cref="LaunchAsync"/> startet einen
/// „Prozess“ (<see cref="FakeRecognizerProcess"/>) mit eigener Prozesskennung, der über Speicherströme verbunden ist –
/// zuerst <c>decode</c>, die Verbindung <c>detect</c> erst nach <see cref="RecognizerService.Ready"/> (Kapitel 5.4).
/// Zählt Starts und Kill-Aufrufe. Das Verhalten je Start legt <see cref="Plans"/> fest (Startnummer ab 1).
/// </summary>
internal sealed class FakeLauncher : IRecognizerLauncher
{
    /// <summary>Prozesskennung eines fremden Clients (R30); kein gestarteter Erkenner hat sie.</summary>
    public const int ForeignProcessId = 9_999;

    private readonly object _gate = new();
    private readonly List<FakeRecognizerProcess> _processes = [];
    private readonly ConcurrentDictionary<int, TaskCompletionSource<FakeRecognizerProcess>> _launched = new();

    public FakeLauncher(string? marker = null) => Marker = marker ?? "P8-ATTRAPPE-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Kennung in jeder Ausnahme der Attrappe (Verbindungen, Annahme), für <c>UnobservedFailureLog</c>.</summary>
    public string Marker { get; }

    /// <summary>Verhalten je Startnummer (ab 1); Standard: Dienst mit Attrappen.</summary>
    public Func<int, LaunchPlan> Plans { get; set; } = _ => LaunchPlan.Service();

    public int Launches
    {
        get
        {
            lock (_gate)
            {
                return _processes.Count;
            }
        }
    }

    public IReadOnlyList<FakeRecognizerProcess> Processes
    {
        get
        {
            lock (_gate)
            {
                return [.. _processes];
            }
        }
    }

    /// <summary>Der zuletzt gestartete Prozess.</summary>
    public FakeRecognizerProcess Current => Processes[^1];

    /// <summary>Kill-Aufrufe über alle Prozesse.</summary>
    public int KillCalls => Processes.Sum(process => process.KillCalls);

    /// <summary>Wartet, bis Start Nummer <paramref name="number"/> geschehen ist.</summary>
    public Task<FakeRecognizerProcess> LaunchedAsync(int number) => Launched(number).Task.WaitAsync(RecognitionTestKit.Guard);

    public Task<LaunchedRecognizer> LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FakeRecognizerProcess process;
        lock (_gate)
        {
            var number = _processes.Count + 1;
            process = new FakeRecognizerProcess(this, number, Plans(number));
            _processes.Add(process);
        }

        process.Start();
        Launched(process.Number).TrySetResult(process);
        return Task.FromResult(process.Handle);
    }

    /// <summary>Beendet alle Prozesse der Attrappe (Aufräumen am Testende).</summary>
    public void ExitAll()
    {
        foreach (var process in Processes)
        {
            process.Exit();
        }
    }

    private TaskCompletionSource<FakeRecognizerProcess> Launched(int number) =>
        _launched.GetOrAdd(number, _ => new TaskCompletionSource<FakeRecognizerProcess>(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>
/// Ein gestarteter Erkenner der Attrappe. Er endet nur durch <see cref="Crash"/> (Absturz) oder <c>Kill</c>; dann
/// trennt er alle Verbindungen hart und lässt wartende Annahmen scheitern, wie ein beendeter Prozess. Das Ende eines
/// Kanals allein beendet ihn nicht (nur <c>shutdown</c> tut das), damit ein bleibender Fehler (<c>failed</c>) nie mit
/// einem Prozessende um die Wette läuft. Fehler seiner Hintergrundaufgaben werden beobachtet und gesammelt.
/// </summary>
internal sealed class FakeRecognizerProcess
{
    private readonly object _gate = new();
    private readonly FakeLauncher _launcher;
    private readonly Queue<PipeConnection> _pending = new();
    private readonly Queue<TaskCompletionSource<PipeConnection>> _waiting = new();
    private readonly List<DuplexPipe> _pipes = [];
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _killed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DuplexPipe> _decode = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DuplexPipe> _detect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DuplexPipe> _foreign = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private bool _isExited;
    private int _killCalls;

    internal FakeRecognizerProcess(FakeLauncher launcher, int number, LaunchPlan plan)
    {
        _launcher = launcher;
        Number = number;
        Plan = plan;
        ProcessId = 4_000 + number;
        Whisper = new FakeWhisper();
        Vad = new FakeVad();
        Loader = new FakeLoader(Whisper, Vad);
        plan.Loader?.Invoke(Loader);
    }

    public int Number { get; }

    public int ProcessId { get; }

    public LaunchPlan Plan { get; }

    public FakeWhisper Whisper { get; }

    public FakeVad Vad { get; }

    public FakeLoader Loader { get; }

    public RecognizerService? Service { get; private set; }

    public string Marker => _launcher.Marker;

    public Task Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    public int KillCalls => Volatile.Read(ref _killCalls);

    /// <summary>Endet beim ersten Kill.</summary>
    public Task Killed => _killed.Task;

    /// <summary>Endet, sobald die Verbindung <c>decode</c> zur Annahme bereitliegt.</summary>
    public Task<DuplexPipe> DecodeConnected => _decode.Task;

    /// <summary>Endet, sobald die Verbindung <c>detect</c> zur Annahme bereitliegt (erst nach <c>ready</c>).</summary>
    public Task<DuplexPipe> DetectConnected => _detect.Task;

    /// <summary>Die eingeschobene fremde Verbindung (R30).</summary>
    public Task<DuplexPipe> ForeignConnected => _foreign.Task;

    /// <summary>Gesammelte Fehler der Hintergrundaufgaben (Dienst, Skript); beobachtet, nur zur Diagnose.</summary>
    public IReadOnlyList<Exception> BackgroundFailures => [.. _failures];

    public LaunchedRecognizer Handle => new(ProcessId, Exited, AcceptAsync, Kill);

    internal CancellationToken Stopping => _stop.Token;

    /// <summary>Absturz: der Prozess endet ohne Kill.</summary>
    public void Crash() => Exit();

    /// <summary>Trennt die Verbindung <c>decode</c> hart; der Prozess läuft weiter.</summary>
    public void SeverDecode() => Connected(_decode).Sever();

    /// <summary>Trennt die Verbindung <c>detect</c> hart; der Prozess läuft weiter.</summary>
    public void SeverDetect() => Connected(_detect).Sever();

    /// <summary>Beendet den Prozess: Verbindungen hart getrennt, wartende Annahmen scheitern, Attrappen geben frei.</summary>
    public void Exit()
    {
        TaskCompletionSource<PipeConnection>[] waiting;
        DuplexPipe[] pipes;
        lock (_gate)
        {
            if (_isExited)
            {
                return;
            }

            _isExited = true;
            waiting = [.. _waiting];
            _waiting.Clear();
            pipes = [.. _pipes];
        }

        _stop.Cancel();
        Whisper.Abort();
        Vad.Abort();
        foreach (var pipe in pipes)
        {
            pipe.Sever();
        }

        foreach (var waiter in waiting)
        {
            waiter.TrySetException(new IOException($"Erkenner beendet ({Marker})."));
        }

        _exited.TrySetResult();
    }

    internal void Start()
    {
        if (Plan.Script is { } script)
        {
            var scripted = new ScriptedRecognizer(this);
            Observe(Task.Run(() => script(scripted)));
            return;
        }

        Service = new RecognizerService(Loader.Load);
        var decode = Connect(isDecode: true);
        Observe(RunDecodeAsync(Service, decode));
        Observe(ConnectDetectAfterReadyAsync(Service));
    }

    /// <summary>Legt eine Verbindung zur Annahme bereit; davor, falls geplant, eine mit fremder Prozesskennung.</summary>
    internal DuplexPipe Connect(bool isDecode)
    {
        var foreign = Plan.Foreign == (isDecode ? ForeignConnection.BeforeDecode : ForeignConnection.BeforeDetect);
        if (foreign)
        {
            var intruder = new DuplexPipe(Marker);
            intruder.Recognizer.Write(FrameCodec.Encode(Messages.Hello(1, isDecode ? "decode" : "detect")));
            Offer(intruder, FakeLauncher.ForeignProcessId);
            _foreign.TrySetResult(intruder);
        }

        var pipe = new DuplexPipe(Marker);
        Offer(pipe, ProcessId);
        (isDecode ? _decode : _detect).TrySetResult(pipe);
        return pipe;
    }

    private static DuplexPipe Connected(TaskCompletionSource<DuplexPipe> connection)
    {
        Assert.True(connection.Task.IsCompletedSuccessfully, "Die Verbindung ist noch nicht aufgebaut.");
        return connection.Task.Result;
    }

    private void Offer(DuplexPipe pipe, int clientProcessId)
    {
        var connection = new PipeConnection(pipe.App, clientProcessId);
        lock (_gate)
        {
            _pipes.Add(pipe);
            if (_isExited)
            {
                pipe.Sever();
                return;
            }

            while (_waiting.Count > 0)
            {
                if (_waiting.Dequeue().TrySetResult(connection))
                {
                    return;
                }
            }

            _pending.Enqueue(connection);
        }
    }

    private Task<PipeConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<PipeConnection> waiter;
        lock (_gate)
        {
            if (_pending.Count > 0)
            {
                return Task.FromResult(_pending.Dequeue());
            }

            if (_isExited)
            {
                return Task.FromException<PipeConnection>(new IOException($"Erkenner beendet ({Marker})."));
            }

            waiter = new TaskCompletionSource<PipeConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            _ = waiter.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return waiter.Task;
    }

    private void Kill()
    {
        Interlocked.Increment(ref _killCalls);
        _killed.TrySetResult();
        Exit();
    }

    private async Task RunDecodeAsync(RecognizerService service, DuplexPipe pipe)
    {
        await service.RunDecodeChannelAsync(pipe.Recognizer, _stop.Token);

        // Nur „shutdown“ beendet den Prozess regulär (Kapitel 4.7: Prozess endet mit Code 0).
        if (FrameCodec.Parse(pipe.WrittenBy(PipeSide.App)) is { Count: > 0 } sent && sent[^1].Type == "shutdown")
        {
            Exit();
        }
    }

    private async Task ConnectDetectAfterReadyAsync(RecognizerService service)
    {
        var ready = service.Ready;
        await Task.WhenAny(ready, _exited.Task);
        if (!ready.IsCompletedSuccessfully)
        {
            _ = ready.Exception;
            return;
        }

        var pipe = Connect(isDecode: false);
        await service.RunDetectChannelAsync(pipe.Recognizer, _stop.Token);
    }

    private void Observe(Task task) => _ = task.ContinueWith(
        finished =>
        {
            if (finished.Exception is { } failure)
            {
                _failures.Enqueue(failure);
            }
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}

/// <summary>Gescriptete Gegenseite statt des Dienstes: verbindet sich wie der Erkenner und sendet rohe Rahmen.</summary>
internal sealed class ScriptedRecognizer(FakeRecognizerProcess process)
{
    public FakeRecognizerProcess Process { get; } = process;

    /// <summary>Endet, wenn der Prozess beendet wird.</summary>
    public CancellationToken Stopping => Process.Stopping;

    /// <summary>Verbindet sich als nächste Verbindung; die Rolle (decode oder detect) bestimmt erst das <c>hello</c>.</summary>
    public FrameChannel Connect(bool asDecode = true) => new(Process.Connect(asDecode).Recognizer);
}
