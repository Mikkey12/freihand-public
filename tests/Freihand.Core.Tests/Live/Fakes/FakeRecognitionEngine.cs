using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.Live.Fakes;

/// <summary>
/// Erkennung über <see cref="IRecognitionEngine"/> ohne Wort-Hypothesen (Kapitel 3, Andere Quellen). Der Test legt
/// Updates fest, die die Attrappe liefert, sobald sie Audio bis zu einer Sitzungszeit erhalten hat, oder schickt sie
/// selbst. Der Erkennungsauftrag endet erst mit <see cref="Finish"/> oder, wenn so eingestellt, mit dem Audio.
/// </summary>
internal sealed class FakeRecognitionEngine(bool partialResults = false) : IRecognitionEngine
{
    private readonly Channel<RecognitionUpdate> _updates = Channel.CreateUnbounded<RecognitionUpdate>();
    private readonly Lock _gate = new();
    private readonly List<(TimeSpan After, RecognitionUpdate Update)> _scheduled = [];
    private long _receivedTicks;
    private int _pushed;
    private int _consumed;

    public RecognitionRoute Route => RecognitionRoute.Device;

    public RecognitionCapabilities Capabilities { get; } = new(
        PartialResults: partialResults,
        FixedSegments: true,
        VocabularyHints: false,
        Cancellation: true,
        MaxSessionDuration: null,
        DataRegion: null,
        ModelVersion: "fake");

    /// <summary>Endet der Erkennungsauftrag mit dem Audio? Sonst erst mit <see cref="Finish"/>.</summary>
    public bool FinishWithAudio { get; init; } = true;

    /// <summary>Ende des Audios, das die Attrappe bisher erhalten hat.</summary>
    public TimeSpan ReceivedEnd => TimeSpan.FromTicks(Interlocked.Read(ref _receivedTicks));

    /// <summary>Anzahl gelieferter Updates.</summary>
    public int Pushed => Volatile.Read(ref _pushed);

    /// <summary>
    /// Anzahl Updates, nach denen der Empfänger schon das nächste verlangt hat; so beobachtet ein Test, dass P3 ein
    /// Update abgeholt und fertig behandelt hat, ohne ein Zeitfenster abzuwarten (T66).
    /// </summary>
    public int Consumed => Volatile.Read(ref _consumed);

    /// <summary>Die Aufgabe, die das Audio abholt; nach dem Ende des Erkennungsauftrags abgeschlossen (A02).</summary>
    public Task? PumpTask { get; private set; }

    public static TextSegment Segment(string text, double start, double end) =>
        new(Guid.NewGuid(), text, SegmentState.Fixed, TestAudio.Sec(start), TestAudio.Sec(end));

    public static RecognitionUpdate Fixed(TextSegment segment, double? completeUntil = null) =>
        new([segment], [], completeUntil is { } until ? TestAudio.Sec(until) : null);

    public static RecognitionUpdate Completion(double? completeUntil) =>
        new([], [], completeUntil is { } until ? TestAudio.Sec(until) : null);

    /// <summary>Liefert <paramref name="update"/>, sobald Audio bis <paramref name="after"/> erhalten ist.</summary>
    public void Schedule(double after, RecognitionUpdate update)
    {
        lock (_gate)
        {
            _scheduled.Add((TestAudio.Sec(after), update));
        }
    }

    public void Push(RecognitionUpdate update)
    {
        Interlocked.Increment(ref _pushed);
        _updates.Writer.TryWrite(update);
    }

    public void Finish() => _updates.Writer.TryComplete();

    public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        RecognitionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Die Pumpe endet mit dem Erkennungsauftrag, auch wenn der Empfänger abbricht oder vorzeitig aufhört (A02)
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = Task.Run(() => PumpAsync(audio, stop.Token), CancellationToken.None);
        PumpTask = pump;
        try
        {
            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
                Interlocked.Increment(ref _consumed);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await pump;
        }
    }

    private async Task PumpAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
            {
                var end = chunk.Offset + TimeSpan.FromTicks(chunk.Pcm16Mono.Length / 2 * TestAudio.TicksPerSample);
                if (end.Ticks > Interlocked.Read(ref _receivedTicks))
                {
                    Interlocked.Exchange(ref _receivedTicks, end.Ticks);
                }

                List<RecognitionUpdate> due;
                lock (_gate)
                {
                    due = [.. _scheduled.Where(item => item.After <= end).Select(item => item.Update)];
                    _scheduled.RemoveAll(item => item.After <= end);
                }

                foreach (var update in due)
                {
                    Push(update);
                }
            }

            if (FinishWithAudio)
            {
                Finish();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Abbruch oder Ende des Erkennungsauftrags: der Empfänger liest nicht mehr
            _updates.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            // Ein Fehler der Audioquelle beendet den Erkennungsauftrag mit diesem Fehler (A02, K01)
            _updates.Writer.TryComplete(exception);
        }
    }
}
