using Freihand.Core.Recognition;

namespace Freihand.Core.Live;

/// <summary>Audioformat und Zeitrechnung nach Vertrag 1: 16-Bit-PCM mono, 16 000 Hz, ein Sample sind 625 Ticks.</summary>
internal static class PcmAudio
{
    public const int SampleRate = 16_000;

    public const long TicksPerSample = TimeSpan.TicksPerSecond / SampleRate;

    /// <summary>Sample, in dem diese Sitzungszeit liegt (abgerundet).</summary>
    public static long ToSample(TimeSpan time) => Math.DivRem(time.Ticks, TicksPerSample, out var rest) - (rest < 0 ? 1 : 0);

    /// <summary>
    /// Erstes Sample, das nicht vor dieser Sitzungszeit beginnt (aufgerundet). So beginnt ein Auftrag nie vor einem
    /// Kürzungspunkt, der zwischen zwei Samples liegt (Kapitel 5.1, Regel 1; U01).
    /// </summary>
    public static long ToSampleAfter(TimeSpan time) => -ToSample(-time);

    public static TimeSpan ToTime(long sample) => TimeSpan.FromTicks(sample * TicksPerSample);

    /// <summary>Prüft Format und Lage eines Blocks; andere Abtastraten weist P3 ab (Vertrag 1, T49).</summary>
    /// <exception cref="ArgumentException">Abtastrate nicht 16 000 Hz, ungerade Länge oder Beginn nicht auf einem Sample.</exception>
    public static void Validate(AudioChunk chunk)
    {
        if (chunk.SampleRate != SampleRate)
        {
            throw new ArgumentException(
                $"Audio muss mit {SampleRate} Hz kommen, nicht mit {chunk.SampleRate} Hz (Vertrag 1).", nameof(chunk));
        }

        if (chunk.Pcm16Mono.Length % 2 != 0)
        {
            throw new ArgumentException("16-Bit-PCM braucht eine gerade Anzahl Bytes (Vertrag 1).", nameof(chunk));
        }

        if (chunk.Offset.Ticks % TicksPerSample != 0 || chunk.Offset < TimeSpan.Zero)
        {
            throw new ArgumentException("Der Beginn eines Blocks liegt nicht auf einem Sample (Vertrag 1).", nameof(chunk));
        }
    }

    /// <summary>Anzahl Samples im Block.</summary>
    public static int SampleCount(AudioChunk chunk) => chunk.Pcm16Mono.Length / 2;

    /// <summary>Sample geteilt durch 32 768, Bereich [−1, 1) (Vertrag 1).</summary>
    public static void Convert(ReadOnlySpan<byte> pcm, Span<float> target)
    {
        for (var i = 0; i < target.Length; i++)
        {
            var sample = (short)(pcm[2 * i] | (pcm[(2 * i) + 1] << 8));
            target[i] = sample / 32768f;
        }
    }
}

/// <summary>
/// Angenommenes, noch nicht freigegebenes Audio als zusammenhängende Samples ab <see cref="StartSample"/>. Ein Ausschnitt
/// bleibt nur bis zur nächsten Änderung gültig; Decoder erhalten darum Kopien (Vertrag 1).
/// </summary>
internal sealed class AudioBuffer
{
    private const int ShrinkAbove = 1 << 20;

    private float[] _samples = new float[PcmAudio.SampleRate];
    private int _head;
    private int _count;

    /// <summary>Sitzungssample des ersten gehaltenen Samples.</summary>
    public long StartSample { get; private set; }

    /// <summary>Sitzungssample nach dem letzten gehaltenen Sample.</summary>
    public long EndSample => StartSample + _count;

    /// <summary>Verwirft alles und beginnt neu bei <paramref name="startSample"/>, etwa nach einer Lücke.</summary>
    public void Reset(long startSample)
    {
        _head = 0;
        _count = 0;
        StartSample = startSample;
        Shrink();
    }

    /// <summary>Hängt 16-Bit-PCM an, umgerechnet in <see cref="float"/>.</summary>
    public void Append(ReadOnlySpan<byte> pcm)
    {
        var added = pcm.Length / 2;
        if (_head + _count + added > _samples.Length)
        {
            if (_count + added <= _samples.Length && _head > 0)
            {
                Array.Copy(_samples, _head, _samples, 0, _count);
            }
            else
            {
                var grown = new float[Math.Max(_samples.Length * 2, _count + added)];
                Array.Copy(_samples, _head, grown, 0, _count);
                _samples = grown;
            }

            _head = 0;
        }

        PcmAudio.Convert(pcm, _samples.AsSpan(_head + _count, added));
        _count += added;
    }

    /// <summary>Gibt alles vor <paramref name="sample"/> frei.</summary>
    public void ReleaseUntil(long sample)
    {
        var released = (int)Math.Clamp(sample - StartSample, 0, _count);
        _head += released;
        _count -= released;
        StartSample += released;
        if (_count == 0)
        {
            _head = 0;
        }

        Shrink();
    }

    /// <summary>Ausschnitt ohne Kopie; gültig bis zur nächsten Änderung des Puffers.</summary>
    public ReadOnlyMemory<float> Slice(long from, long to)
    {
        CheckRange(from, to);
        return _samples.AsMemory(_head + (int)(from - StartSample), (int)(to - from));
    }

    /// <summary>Eigene Kopie eines Ausschnitts, unabhängig von späteren Freigaben.</summary>
    public float[] Copy(long from, long to) => Slice(from, to).ToArray();

    private void CheckRange(long from, long to)
    {
        if (from < StartSample || to > EndSample || from > to)
        {
            throw new InvalidOperationException($"Audio [{from}, {to}) liegt nicht im Puffer [{StartSample}, {EndSample}).");
        }
    }

    private void Shrink()
    {
        if (_samples.Length > ShrinkAbove && _count < _samples.Length / 4)
        {
            var smaller = new float[Math.Max(_count * 2, PcmAudio.SampleRate)];
            Array.Copy(_samples, _head, smaller, 0, _count);
            _samples = smaller;
            _head = 0;
        }
    }
}

/// <summary>Eigene Kopie des Audios einer abgeschlossenen, noch nicht fertig erkannten Äusserung.</summary>
internal sealed class DetachedAudio(long startSample, float[] samples)
{
    public long StartSample { get; } = startSample;

    public long EndSample => StartSample + samples.Length;

    public float[] Copy(long from, long to)
    {
        if (from < StartSample || to > EndSample || from > to)
        {
            throw new InvalidOperationException($"Audio [{from}, {to}) liegt nicht in der Kopie [{StartSample}, {EndSample}).");
        }

        return samples.AsSpan((int)(from - StartSample), (int)(to - from)).ToArray();
    }
}
