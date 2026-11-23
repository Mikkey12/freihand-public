using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.Live.Fakes;

/// <summary>
/// Künstliches Audio nach Vertrag 1: 16 kHz mono, ein Sample sind 625 Ticks. Der Wert jedes Samples hängt nur von
/// seiner Sitzungszeit ab; so prüfen die Attrappen, ob P3 richtig ausschneidet, umrechnet und nichts verändert.
/// </summary>
internal static class TestAudio
{
    public const int SampleRate = 16_000;

    public const long TicksPerSample = TimeSpan.TicksPerSecond / SampleRate;

    public static TimeSpan Sec(double seconds) => TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    public static long SampleIndex(TimeSpan time) => time.Ticks / TicksPerSample;

    /// <summary>16-Bit-Wert des Samples an dieser Stelle der Sitzung.</summary>
    public static short SampleAt(long index) => (short)((((index * 7919) + 13) % 65536) - 32768);

    /// <summary>Was P3 dem Decoder und dem Detektor übergeben muss: Sample geteilt durch 32 768.</summary>
    public static float FloatAt(long index) => SampleAt(index) / 32768f;

    /// <summary>Block von <paramref name="start"/> bis <paramref name="end"/>; andere Abtastraten nur für T49.</summary>
    public static AudioChunk Chunk(TimeSpan start, TimeSpan end, int sampleRate = SampleRate)
    {
        var samples = (int)((end - start).Ticks * sampleRate / TimeSpan.TicksPerSecond);
        var bytes = new byte[samples * 2];
        var first = start.Ticks * sampleRate / TimeSpan.TicksPerSecond;
        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), SampleAt(first + i));
        }

        return new AudioChunk(bytes, sampleRate, start);
    }

    /// <summary>Stimmt der übergebene Speicher mit dem Audio ab <paramref name="offset"/> überein?</summary>
    public static bool Matches(ReadOnlySpan<float> audio, TimeSpan offset)
    {
        if (offset.Ticks % TicksPerSample != 0)
        {
            return false;
        }

        var first = SampleIndex(offset);
        for (var i = 0; i < audio.Length; i++)
        {
            if (audio[i] != FloatAt(first + i))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Audioquelle, die P3 selbst abholt (Annahme). Der Test gibt Audio bis zu einer Sitzungszeit frei; mehr liefert
/// die Quelle nicht, bis er weiteres freigibt. Holt P3 nichts ab, entsteht auch kein angenommenes Audio (T31).
/// </summary>
internal sealed class AudioFeed
{
    private readonly Lock _gate = new();
    private readonly Queue<object> _items = new();
    private TaskCompletionSource _changed = NewSignal();
    private TimeSpan _position;
    private bool _completed;
    private bool _readerWaiting;

    public AudioFeed(TimeSpan? chunkLength = null) => ChunkLength = chunkLength ?? TestAudio.Sec(0.1);

    public TimeSpan ChunkLength { get; }

    /// <summary>Ende des bisher freigegebenen Audios.</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return _position;
            }
        }
    }

    /// <summary>
    /// Alles freigegebene Audio ist abgeholt, und der Leser wartet auf weiteres: Die Quelle hat den letzten Block
    /// angenommen und verlangt den nächsten (schrittweiser Treiber, A01).
    /// </summary>
    public bool Drained
    {
        get
        {
            lock (_gate)
            {
                return _readerWaiting && _items.Count == 0;
            }
        }
    }

    /// <summary>Gibt Audio bis <paramref name="end"/> in Blöcken von <see cref="ChunkLength"/> frei.</summary>
    public void ReleaseUntil(TimeSpan end)
    {
        lock (_gate)
        {
            if (end > _position)
            {
                _items.Enqueue(new Range(_position, end));
                _position = end;
            }

            Signal();
        }
    }

    /// <summary>Gibt einen bestimmten Block frei, etwa mit anderer Abtastrate oder überlappend (T49).</summary>
    public void Enqueue(AudioChunk chunk)
    {
        lock (_gate)
        {
            _items.Enqueue(chunk);
            var end = chunk.Offset + TimeSpan.FromTicks(chunk.Pcm16Mono.Length / 2 * TimeSpan.TicksPerSecond / chunk.SampleRate);
            if (end > _position)
            {
                _position = end;
            }

            Signal();
        }
    }

    /// <summary>Lücke: Das nächste freigegebene Audio beginnt erst bei <paramref name="time"/> (T49).</summary>
    public void SkipTo(TimeSpan time)
    {
        lock (_gate)
        {
            _position = time;
        }
    }

    /// <summary>Wiederanlauf: Das nächste freigegebene Audio beginnt wieder bei <paramref name="time"/> (T41).</summary>
    public void Rewind(TimeSpan time)
    {
        lock (_gate)
        {
            _position = time;
        }
    }

    /// <summary>Normales Streamende, nachdem alles Freigegebene geliefert ist.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Signal();
        }
    }

    /// <summary>Aufnahmestopp (P8 nach <c>BufferFull</c>): nicht abgeholtes Audio entfällt, dann Streamende.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _items.Clear();
            _completed = true;
            Signal();
        }
    }

    public async IAsyncEnumerable<AudioChunk> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            AudioChunk? next = null;
            Task wait;
            lock (_gate)
            {
                _readerWaiting = false;
                if (_items.Count > 0)
                {
                    next = TakeNext();
                }
                else if (_completed)
                {
                    yield break;
                }
                else
                {
                    _readerWaiting = true;
                }

                wait = _changed.Task;
            }

            if (next is { } chunk)
            {
                yield return chunk;
                continue;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AudioChunk TakeNext()
    {
        var item = _items.Peek();
        if (item is AudioChunk concrete)
        {
            _items.Dequeue();
            return concrete;
        }

        var range = (Range)item;
        var start = range.From;
        var end = start + ChunkLength < range.To ? start + ChunkLength : range.To;
        if (end >= range.To)
        {
            _items.Dequeue();
        }
        else
        {
            range.From = end;
        }

        return TestAudio.Chunk(start, end);
    }

    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private sealed class Range(TimeSpan from, TimeSpan to)
    {
        public TimeSpan From { get; set; } = from;

        public TimeSpan To { get; } = to;
    }
}
