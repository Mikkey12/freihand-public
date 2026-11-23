using System.Collections.Concurrent;
using System.Diagnostics;
using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.Recognition.Kit;

/// <summary>Ein Aufruf der Whisper-Attrappe; <see cref="Audio"/> ist eine Kopie der Samples beim Aufruf.</summary>
internal sealed record WhisperCall(
    int Number, float[] Audio, string Language, string? Prompt, bool FullContext, CancellationToken Token);

/// <summary>Ein Aufruf der VAD-Attrappe; <see cref="Audio"/> ist eine Kopie der Samples beim Aufruf.</summary>
internal sealed record VadCall(int Number, float[] Audio);

/// <summary>
/// Gemeinsame Aufzeichnung der Attrappen: Aufrufe mit Nummer (ab 1), höchste Zahl gleichzeitiger Aufrufe, Signal je
/// begonnenem Aufruf und ein Abbruch für den Fall, dass der Prozess der Attrappe beendet wird (dann enden blockierte
/// Aufrufe, statt Fäden festzuhalten).
/// </summary>
internal abstract class FakeBackend<TCall>
    where TCall : class
{
    private readonly object _gate = new();
    private readonly List<TCall> _calls = [];
    private readonly ConcurrentDictionary<int, TaskCompletionSource<TCall>> _started = new();
    private readonly CancellationTokenSource _abort = new();
    private int _active;
    private int _maxActive;

    public IReadOnlyList<TCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Höchste Zahl gleichzeitig laufender Aufrufe (R24: nie überlappend).</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxActive);

    public bool Disposed { get; private set; }

    /// <summary>Endet, wenn der Prozess der Attrappe beendet wird.</summary>
    public CancellationToken Aborted => _abort.Token;

    /// <summary>Wartet, bis Aufruf Nummer <paramref name="number"/> begonnen hat.</summary>
    public Task<TCall> StartedAsync(int number) =>
        Started(number).Task.WaitAsync(RecognitionTestKit.Guard);

    public void Abort()
    {
        try
        {
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Schon freigegeben.
        }
    }

    public void Dispose() => Disposed = true;

    protected int NextNumber()
    {
        lock (_gate)
        {
            return _calls.Count + 1;
        }
    }

    protected void Enter(TCall call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }

        var active = Interlocked.Increment(ref _active);
        int seen;
        while (active > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, active, seen) != seen)
        {
        }
    }

    protected void Announce(int number, TCall call) => Started(number).TrySetResult(call);

    protected void Leave() => Interlocked.Decrement(ref _active);

    private TaskCompletionSource<TCall> Started(int number) =>
        _started.GetOrAdd(number, _ => new TaskCompletionSource<TCall>(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>
/// Whisper-Attrappe (Kapitel 6): zeichnet jeden Aufruf mit einer Kopie der Samples auf; das Verhalten je Aufruf legt
/// <see cref="Behavior"/> fest (liefern, werfen, blockieren bis zur Freigabe, Abbruchtoken beobachten).
/// </summary>
internal sealed class FakeWhisper : FakeBackend<WhisperCall>, IWhisperBackend
{
    /// <summary>Verhalten je Aufruf; Standard: <see cref="DefaultSegments"/>.</summary>
    public Func<WhisperCall, Task<IReadOnlyList<WhisperSegmentData>>> Behavior { get; set; } =
        _ => Task.FromResult(DefaultSegments);

    /// <summary>Erfundenes Segment „ Guten Tag“ mit passenden Tokens.</summary>
    public static IReadOnlyList<WhisperSegmentData> DefaultSegments { get; } = TestSegments.Of(" Guten Tag", (" Guten", 0, 30), (" Tag", 30, 60));

    public async Task<IReadOnlyList<WhisperSegmentData>> TranscribeAsync(
        ReadOnlyMemory<float> audio, string language, string? prompt, bool fullContext, CancellationToken cancellationToken)
    {
        var call = new WhisperCall(NextNumber(), audio.ToArray(), language, prompt, fullContext, cancellationToken);
        Enter(call);
        try
        {
            Announce(call.Number, call);
            return await Behavior(call);
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>Liefert die Segmente.</summary>
    public static Func<WhisperCall, Task<IReadOnlyList<WhisperSegmentData>>> Returns(IReadOnlyList<WhisperSegmentData> segments) =>
        _ => Task.FromResult(segments);

    /// <summary>Wirft eine Ausnahme mit dieser Meldung (R22: Meldung darf nie über die Pipe gehen).</summary>
    public static Func<WhisperCall, Task<IReadOnlyList<WhisperSegmentData>>> Throws(string message) =>
        _ => Task.FromException<IReadOnlyList<WhisperSegmentData>>(new InvalidOperationException(message));

    /// <summary>Blockiert bis zur Freigabe durch den Test und übergeht dabei den Abbruch; endet nur mit dem Prozess.</summary>
    public Func<WhisperCall, Task<IReadOnlyList<WhisperSegmentData>>> BlocksUntil(Task release, IReadOnlyList<WhisperSegmentData>? segments = null) =>
        async _ =>
        {
            await release.WaitAsync(Aborted);
            return segments ?? DefaultSegments;
        };

    /// <summary>Wartet auf den eigenen Abbruchtoken und meldet den Abbruch (R20: Antwort <c>cancelled</c>).</summary>
    public Func<WhisperCall, Task<IReadOnlyList<WhisperSegmentData>>> WaitsForCancellation() =>
        async call =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Token, Aborted);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            throw new UnreachableException();
        };
}

/// <summary>
/// VAD-Attrappe (Kapitel 6): synchron wie <see cref="IVadBackend.DetectFresh"/>; zeichnet jeden Aufruf mit Kopie der
/// Samples auf; <see cref="Behavior"/> liefert, wirft oder blockiert bis zur Freigabe.
/// </summary>
internal sealed class FakeVad : FakeBackend<VadCall>, IVadBackend
{
    /// <summary>Verhalten je Aufruf; Standard: ein Bereich über die erste Hälfte des Audios.</summary>
    public Func<VadCall, IReadOnlyList<(TimeSpan Start, TimeSpan End)>> Behavior { get; set; } = DefaultRegions;

    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> DetectFresh(ReadOnlyMemory<float> audio)
    {
        var call = new VadCall(NextNumber(), audio.ToArray());
        Enter(call);
        try
        {
            Announce(call.Number, call);
            return Behavior(call);
        }
        finally
        {
            Leave();
        }
    }

    public static IReadOnlyList<(TimeSpan Start, TimeSpan End)> DefaultRegions(VadCall call) =>
        call.Audio.Length == 0 ? [] : [(TimeSpan.Zero, TimeSpan.FromTicks(call.Audio.Length * 625L / 2))];

    public static Func<VadCall, IReadOnlyList<(TimeSpan Start, TimeSpan End)>> Returns(params (TimeSpan Start, TimeSpan End)[] regions) =>
        _ => regions;

    public static Func<VadCall, IReadOnlyList<(TimeSpan Start, TimeSpan End)>> Throws(string message) =>
        _ => throw new InvalidOperationException(message);

    /// <summary>Blockiert den Faden bis zur Freigabe; endet nur mit dem Prozess.</summary>
    public Func<VadCall, IReadOnlyList<(TimeSpan Start, TimeSpan End)>> BlocksUntil(Task release) =>
        call =>
        {
            release.Wait(Aborted);
            return DefaultRegions(call);
        };
}

/// <summary>
/// Lade-Attrappe für <see cref="RecognizerService"/>: zeichnet jede Konfiguration auf, kann blockieren (Neustart dauert),
/// mit einem Code werfen und meldet die Laufzeit nach <see cref="AvailableRuntimes"/> wie Kapitel 5.4 (erste verfügbare
/// aus <c>RuntimeOrder</c>, <c>RuntimeFallback</c> bei Abweichung vom ersten Eintrag).
/// </summary>
internal sealed class FakeLoader(FakeWhisper whisper, FakeVad vad)
{
    private readonly ConcurrentQueue<RecognizerConfiguration> _calls = new();

    public FakeWhisper Whisper { get; } = whisper;

    public FakeVad Vad { get; } = vad;

    /// <summary>Laufzeiten, die auf diesem „Gerät“ laden; Standard: alle.</summary>
    public IReadOnlyList<RecognizerRuntime> AvailableRuntimes { get; set; } = Enum.GetValues<RecognizerRuntime>();

    /// <summary>Wirft beim Laden <see cref="RecognizerException"/> mit diesem Code.</summary>
    public RecognizerErrorCode? Throws { get; set; }

    /// <summary>Blockiert das Laden bis zur Freigabe.</summary>
    public Task? Gate { get; set; }

    /// <summary>Läuft während des Ladens (R31: Schreibsperre prüfen).</summary>
    public Action<RecognizerConfiguration>? DuringLoad { get; set; }

    public IReadOnlyList<RecognizerConfiguration> Calls => [.. _calls];

    public (IWhisperBackend Whisper, IVadBackend Vad, RecognizerInfo Info) Load(RecognizerConfiguration configuration)
    {
        _calls.Enqueue(configuration);
        DuringLoad?.Invoke(configuration);
        Gate?.Wait(Whisper.Aborted);
        if (Throws is { } code)
        {
            throw new RecognizerException(code);
        }

        var runtime = configuration.RuntimeOrder.Cast<RecognizerRuntime?>().FirstOrDefault(r => AvailableRuntimes.Contains(r!.Value))
            ?? throw new RecognizerException(RecognizerErrorCode.RuntimeUnavailable);
        var info = new RecognizerInfo(
            Path.GetFileName(configuration.ModelPath),
            configuration.ModelSha256,
            runtime,
            runtime != configuration.RuntimeOrder[0],
            TimeSpan.FromSeconds(1),
            "1.9.1");
        return (Whisper, Vad, info);
    }
}

/// <summary>Erfundene Segmente für die Whisper-Attrappe.</summary>
internal static class TestSegments
{
    /// <summary>Ein Segment mit Tokens (Text, Start, Ende) in 10 ms, Wahrscheinlichkeit 0,9.</summary>
    public static IReadOnlyList<WhisperSegmentData> Of(string text, params (string Text, long Start, long End)[] tokens) =>
        [new WhisperSegmentData(text, [.. tokens.Select(token => new WhisperTokenData(token.Text, token.Start, token.End, 0.9f, false))])];

    /// <summary>Segmenttext und Tokens passen nicht zusammen (W3): ergibt <c>failed</c> mit <c>TokenAlignment</c>.</summary>
    public static IReadOnlyList<WhisperSegmentData> Misaligned { get; } = Of(" Hallo schöne Welt", (" Hallo", 0, 40), (" Welt", 40, 80));
}
