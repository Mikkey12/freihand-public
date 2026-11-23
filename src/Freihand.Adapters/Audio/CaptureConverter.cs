using System.Buffers.Binary;
using Freihand.Core.Recognition;
using NAudio.Dsp;

namespace Freihand.Adapters.Audio;

/// <summary>Sampleformat des Geräts.</summary>
public enum CaptureSampleFormat
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <summary>Format, in dem das Gerät liefert; Puffer sind verschachtelte Frames, Little Endian.</summary>
public sealed record CaptureFormat(int SampleRate, int Channels, CaptureSampleFormat SampleFormat);

/// <summary>
/// Gerätformat → 16 kHz, mono, PCM16 in Blöcken fester Länge mit lückenlosen Offsets (P3 Vertrag 1, P8-Konzept
/// Kapitel 5.2); nur eine bekannte Lücke (<see cref="Gap"/>) lässt den Offset springen. Kanäle werden gemittelt, mit dem
/// WDL-Resampler auf 16 kHz gebracht und als Wert × 32 768 gerundet und begrenzt; 16 kHz mono PCM16 geht bitgenau durch.
/// Nicht threadsicher; die Aufnahmesitzung ruft ihn unter ihrer Sperre auf.
/// </summary>
public sealed class CaptureConverter
{
    private const int TargetRate = 16_000;

    /// <summary>Ticks je Sample bei 16 kHz.</summary>
    private const long TicksPerSample = TimeSpan.TicksPerSecond / TargetRate;

    /// <summary>Nullen je Schritt beim Ausspülen des Resamplers.</summary>
    private const int DrainStep = 64;

    private readonly CaptureFormat _input;
    private readonly int _chunkSamples;
    private readonly int _frameBytes;
    private readonly bool _passThrough;
    private readonly List<short> _pending = [];
    private readonly byte[] _partial;
    private int _partialLength;
    private WdlResampler? _resampler;

    /// <summary>Samples, die schon als Block geliefert sind, plus Samples bekannter Lücken.</summary>
    private long _offsetSamples;

    /// <summary>Eingangsframes seit dem letzten Ausspülen, und was der Resampler daraus geliefert hat.</summary>
    private long _segmentInputFrames;
    private long _segmentOutputSamples;

    /// <param name="input">Format des Geräts.</param>
    /// <param name="chunkDuration">Länge eines Blocks, Startwert 200 ms (3 200 Samples).</param>
    public CaptureConverter(CaptureFormat input, TimeSpan chunkDuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.SampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Channels, 1);
        _chunkSamples = checked((int)(chunkDuration.Ticks / TicksPerSample));
        ArgumentOutOfRangeException.ThrowIfLessThan(_chunkSamples, 1, nameof(chunkDuration));

        _input = input;
        _frameBytes = BytesPerSample(input.SampleFormat) * input.Channels;
        _partial = new byte[_frameBytes];
        _passThrough = input is { SampleRate: TargetRate, Channels: 1, SampleFormat: CaptureSampleFormat.Pcm16 };
    }

    /// <summary>Nimmt einen Gerätepuffer an und liefert die damit vollen Blöcke.</summary>
    public IReadOnlyList<AudioChunk> Push(ReadOnlySpan<byte> deviceBuffer)
    {
        var mono = Decode(deviceBuffer);
        if (mono.Length > 0)
        {
            _segmentInputFrames += mono.Length;
            Append(_input.SampleRate == TargetRate ? mono : Resample(mono));
        }

        return TakeChunks(final: false);
    }

    /// <summary>Liefert den Rest samt Nachlauf des Resamplers; danach beginnt der nächste Block um die Lücke später.</summary>
    /// <param name="missingDeviceFrames">Fehlende Frames im Format des Geräts.</param>
    public IReadOnlyList<AudioChunk> Gap(long missingDeviceFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(missingDeviceFrames);
        var chunks = Flush();
        _offsetSamples += (long)Math.Round(missingDeviceFrames * (double)TargetRate / _input.SampleRate, MidpointRounding.AwayFromZero);
        return chunks;
    }

    /// <summary>Liefert den Rest samt Nachlauf des Resamplers als letzten, kürzeren Block.</summary>
    public IReadOnlyList<AudioChunk> Flush()
    {
        _partialLength = 0;
        if (_resampler is not null)
        {
            Drain();
        }

        _segmentInputFrames = 0;
        _segmentOutputSamples = 0;
        return TakeChunks(final: true);
    }

    private static int BytesPerSample(CaptureSampleFormat format) => format switch
    {
        CaptureSampleFormat.Float32 => 4,
        CaptureSampleFormat.Pcm16 => 2,
        CaptureSampleFormat.Pcm24 => 3,
        CaptureSampleFormat.Pcm32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static short ToPcm16(float value) =>
        (short)Math.Clamp(Math.Round(value * 32_768.0), short.MinValue, short.MaxValue);

    /// <summary>Ganze Frames als gemittelte Monowerte; ein angefangener Frame wartet auf den nächsten Puffer.</summary>
    private float[] Decode(ReadOnlySpan<byte> buffer)
    {
        var total = _partialLength + buffer.Length;
        var frames = total / _frameBytes;
        var bytes = new byte[frames * _frameBytes];
        var fromPartial = Math.Min(_partialLength, bytes.Length);
        _partial.AsSpan(0, fromPartial).CopyTo(bytes);
        var fromBuffer = bytes.Length - fromPartial;
        buffer[..fromBuffer].CopyTo(bytes.AsSpan(fromPartial));

        var rest = buffer[fromBuffer..];
        rest.CopyTo(_partial.AsSpan(_partialLength - fromPartial));
        _partialLength = _partialLength - fromPartial + rest.Length;

        if (_passThrough)
        {
            var samples = new float[frames];
            for (var i = 0; i < frames; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)) / 32_768f;
            }

            return samples;
        }

        var size = BytesPerSample(_input.SampleFormat);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < _input.Channels; c++)
            {
                sum += Read(bytes.AsSpan((f * _frameBytes) + (c * size), size));
            }

            mono[f] = (float)(sum / _input.Channels);
        }

        return mono;
    }

    private double Read(ReadOnlySpan<byte> sample) => _input.SampleFormat switch
    {
        CaptureSampleFormat.Float32 => BinaryPrimitives.ReadSingleLittleEndian(sample),
        CaptureSampleFormat.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32_768.0,
        CaptureSampleFormat.Pcm24 => (((sample[2] << 24) | (sample[1] << 16) | (sample[0] << 8)) >> 8) / 8_388_608.0,
        CaptureSampleFormat.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2_147_483_648.0,
        _ => throw new InvalidOperationException(),
    };

    private float[] Resample(float[] mono)
    {
        if (_resampler is null)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false, 64, 32);
            _resampler.SetFilterParms(0.693f, 0.707f);
            _resampler.SetFeedMode(true);
            _resampler.SetRates(_input.SampleRate, TargetRate);
        }

        var produced = Feed(mono);
        _segmentOutputSamples += produced.Length;
        return produced;
    }

    private float[] Feed(ReadOnlySpan<float> mono)
    {
        var resampler = _resampler!;
        var capacity = (int)((long)mono.Length * TargetRate / _input.SampleRate) + DrainStep;
        var output = new float[capacity];
        var wanted = resampler.ResamplePrepare(mono.Length, 1, out var inbuffer);
        mono[..Math.Min(wanted, mono.Length)].CopyTo(inbuffer);
        var count = resampler.ResampleOut(output, wanted, capacity, 1);
        return output.AsSpan(0, count).ToArray();
    }

    /// <summary>Spült den Resampler mit Nullen, bis die Ausgabe der Dauer des Eingangs entspricht.</summary>
    private void Drain()
    {
        var expected = (long)Math.Round(_segmentInputFrames * (double)TargetRate / _input.SampleRate, MidpointRounding.AwayFromZero);
        var zeros = new float[DrainStep];
        var guard = (int)(_input.SampleRate / DrainStep) + 16;
        while (_segmentOutputSamples < expected && guard-- > 0)
        {
            var produced = Feed(zeros);
            var keep = (int)Math.Min(produced.Length, expected - _segmentOutputSamples);
            _segmentOutputSamples += keep;
            Append(produced.AsSpan(0, keep));
        }

        _resampler!.Reset(0);
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _pending.Add(ToPcm16(sample));
        }
    }

    private IReadOnlyList<AudioChunk> TakeChunks(bool final)
    {
        var chunks = new List<AudioChunk>();
        var taken = 0;
        while (_pending.Count - taken >= _chunkSamples || (final && _pending.Count > taken))
        {
            var length = Math.Min(_chunkSamples, _pending.Count - taken);
            var pcm = new byte[length * 2];
            for (var i = 0; i < length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), _pending[taken + i]);
            }

            chunks.Add(new AudioChunk(pcm, TargetRate, TimeSpan.FromTicks(_offsetSamples * TicksPerSample)));
            _offsetSamples += length;
            taken += length;
        }

        _pending.RemoveRange(0, taken);
        return chunks;
    }
}
