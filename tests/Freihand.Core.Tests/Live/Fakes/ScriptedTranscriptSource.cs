using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Freihand.Core.Live;
using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.Live.Fakes;

/// <summary>
/// Steuerbare Quelle für die Sitzung (A03, Prüfung PR #24): Der Test bestimmt, welche Ereignisse sie liefert. Den
/// Abbruch übergeht sie absichtlich, wie ein Decoder in T35, und liefert auch nach einem Cancel weiter, solange die
/// Sitzung abholt. Das Audio nimmt sie an, damit <see cref="AcceptedEnd"/> und die Befehlstaste wie bei einer echten
/// Quelle wirken.
/// </summary>
internal sealed class ScriptedTranscriptSource : ITranscriptSource
{
    private readonly Channel<TranscriptEvent> _events = Channel.CreateUnbounded<TranscriptEvent>();
    private long _accepted;
    private int _delivered;
    private int _waiting;
    private int _released;

    public TimeSpan AcceptedEnd => TimeSpan.FromTicks(Interlocked.Read(ref _accepted));

    public TimeSpan Backlog => TimeSpan.Zero;

    /// <summary>Anzahl Ereignisse, die die Quelle an ein wartendes Abholen der Sitzung übergeben hat.</summary>
    public int Delivered => Volatile.Read(ref _delivered);

    /// <summary>Die Sitzung wartet auf das nächste Ereignis, und keines liegt bereit.</summary>
    public bool Waiting => Volatile.Read(ref _waiting) == 1;

    /// <summary>Die Sitzung hat den Ereignisstrom der Quelle beendet.</summary>
    public bool Released => Volatile.Read(ref _released) == 1;

    /// <summary>
    /// Späte Lieferung abgeschlossen, in einem der beiden erlaubten Abläufe (L02): Die Sitzung hat alle
    /// <paramref name="count"/> Ereignisse nach <paramref name="before"/> abgeholt, oder sie hat die Quelle freigegeben,
    /// etwa nach dem ersten späten Ereignis. Solange keines gilt, ist der Ablauf noch offen.
    /// </summary>
    public bool LateDeliverySettled(int before, int count) => Released || Delivered >= before + count;

    /// <summary>Stellt ein Ereignis bereit; die Quelle übergibt es, sobald die Sitzung abholt.</summary>
    public void Emit(TranscriptEvent transcriptEvent) => _events.Writer.TryWrite(transcriptEvent);

    /// <summary>Beendet den Ereignisstrom nach den bereitgestellten Ereignissen.</summary>
    public void End() => _events.Writer.TryComplete();

    public void MarkBoundary(TimeSpan at)
    {
    }

    public async IAsyncEnumerable<TranscriptEvent> RunAsync(
        IAsyncEnumerable<AudioChunk> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Der Abbruch der Sitzung wird absichtlich übergangen; nur das Ende des Stroms beendet die Annahme
        using var stop = new CancellationTokenSource();
        var accept = Task.Run(() => AcceptAsync(audio, stop.Token), CancellationToken.None);
        try
        {
            while (true)
            {
                Volatile.Write(ref _waiting, 1);
                var more = await _events.Reader.WaitToReadAsync(CancellationToken.None);
                Volatile.Write(ref _waiting, 0);
                if (!more)
                {
                    break;
                }

                while (_events.Reader.TryRead(out var next))
                {
                    Interlocked.Increment(ref _delivered);
                    yield return next;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _waiting, 0);
            Volatile.Write(ref _released, 1);
            await stop.CancelAsync();
            try
            {
                await accept;
            }
            catch (OperationCanceledException)
            {
                // Annahme mit dem Strom beendet
            }
        }
    }

    private async Task AcceptAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken)
    {
        await foreach (var chunk in audio.WithCancellation(cancellationToken))
        {
            var end = chunk.Offset + TimeSpan.FromTicks(chunk.Pcm16Mono.Length / 2 * TestAudio.TicksPerSample);
            Interlocked.Exchange(ref _accepted, Math.Max(end.Ticks, Interlocked.Read(ref _accepted)));
        }
    }
}
