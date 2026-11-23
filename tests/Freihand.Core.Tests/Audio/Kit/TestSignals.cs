using System.Buffers.Binary;
using Freihand.Adapters.Audio;

namespace Freihand.Core.Tests.Audio.Kit;

/// <summary>
/// Erzeugte Gerätesignale (keine Aufnahmen): Werte im Bereich [−1, 1] werden je <see cref="CaptureSampleFormat"/> als
/// verschachtelte Frames, Little Endian, geschrieben. PCM wird mit Vollausschlag 2^(n−1) − 1 skaliert, gerundet und
/// begrenzt; Float32 wird unverändert geschrieben, auch ausserhalb von [−1, 1] (R34).
/// </summary>
internal static class TestSignals
{
    public static int BytesPerSample(CaptureSampleFormat format) => format switch
    {
        CaptureSampleFormat.Float32 => 4,
        CaptureSampleFormat.Pcm16 => 2,
        CaptureSampleFormat.Pcm24 => 3,
        CaptureSampleFormat.Pcm32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static int FrameBytes(CaptureFormat format) => format.Channels * BytesPerSample(format.SampleFormat);

    /// <summary>Frames für diese Dauer im Format des Geräts, auf ganze Frames gerundet.</summary>
    public static int Frames(CaptureFormat format, TimeSpan duration) =>
        (int)Math.Round(duration.TotalSeconds * format.SampleRate, MidpointRounding.AwayFromZero);

    /// <summary>Schreibt einen Wert als ein Sample.</summary>
    public static void Write(CaptureSampleFormat format, Span<byte> destination, double value)
    {
        switch (format)
        {
            case CaptureSampleFormat.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(destination, (float)value);
                break;
            case CaptureSampleFormat.Pcm16:
                BinaryPrimitives.WriteInt16LittleEndian(destination, (short)Scale(value, short.MaxValue));
                break;
            case CaptureSampleFormat.Pcm24:
                var pcm24 = (int)Scale(value, 8_388_607);
                destination[0] = (byte)pcm24;
                destination[1] = (byte)(pcm24 >> 8);
                destination[2] = (byte)(pcm24 >> 16);
                break;
            case CaptureSampleFormat.Pcm32:
                BinaryPrimitives.WriteInt32LittleEndian(destination, (int)Scale(value, int.MaxValue));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>Liest ein Sample als Wert (Gegenstück zu <see cref="Write"/>, für die Prüfung der Testhilfe).</summary>
    public static double Read(CaptureSampleFormat format, ReadOnlySpan<byte> source) => format switch
    {
        CaptureSampleFormat.Float32 => BinaryPrimitives.ReadSingleLittleEndian(source),
        CaptureSampleFormat.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(source) / (double)short.MaxValue,
        CaptureSampleFormat.Pcm24 => ((source[0] | (source[1] << 8) | (source[2] << 16)) << 8 >> 8) / 8_388_607.0,
        CaptureSampleFormat.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(source) / (double)int.MaxValue,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Verschachtelte Frames mit dem Wert je Frame und Kanal.</summary>
    public static byte[] Encode(CaptureFormat format, int frames, Func<long, int, double> value, long firstFrame = 0)
    {
        var bytesPerSample = BytesPerSample(format.SampleFormat);
        var buffer = new byte[frames * FrameBytes(format)];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var at = ((frame * format.Channels) + channel) * bytesPerSample;
                Write(format.SampleFormat, buffer.AsSpan(at, bytesPerSample), value(firstFrame + frame, channel));
            }
        }

        return buffer;
    }

    /// <summary>Sinus auf allen Kanälen gleich, ab Frame <paramref name="firstFrame"/> phasenrichtig fortgesetzt.</summary>
    public static byte[] Sine(
        CaptureFormat format, int frames, double frequency = 440, double amplitude = 0.5, long firstFrame = 0) =>
        Encode(format, frames, (frame, _) => SineAt(format.SampleRate, frame, frequency, amplitude), firstFrame);

    /// <summary>Stereo: rechts gleich <paramref name="rightFactor"/> mal links (R34: 1 oder −1).</summary>
    public static byte[] StereoSine(CaptureFormat format, int frames, double rightFactor, double frequency = 440, double amplitude = 0.5)
    {
        if (format.Channels != 2)
        {
            throw new ArgumentException("Nur für Stereo.", nameof(format));
        }

        return Encode(
            format,
            frames,
            (frame, channel) => (channel == 0 ? 1 : rightFactor) * SineAt(format.SampleRate, frame, frequency, amplitude));
    }

    /// <summary>Gleichwert auf allen Kanälen.</summary>
    public static byte[] Constant(CaptureFormat format, int frames, double value) => Encode(format, frames, (_, _) => value);

    /// <summary>Exakte Nullen (in jedem Format alle Bytes 0).</summary>
    public static byte[] Zeros(CaptureFormat format, int frames) => new byte[frames * FrameBytes(format)];

    /// <summary>
    /// 16-Bit-Muster für die bitgenaue Durchleitung: beginnt mit den Extremwerten −32 768, 32 767, 0, −1, 1, danach
    /// ein fester Pseudozufall über den ganzen Wertebereich.
    /// </summary>
    public static byte[] Pcm16Pattern(int samples)
    {
        short[] head = [short.MinValue, short.MaxValue, 0, -1, 1];
        var bytes = new byte[samples * 2];
        uint state = 0x2545F491;
        for (var i = 0; i < samples; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            var value = i < head.Length ? head[i] : unchecked((short)state);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), value);
        }

        return bytes;
    }

    /// <summary>
    /// Teilt einen Puffer in Stücke mit den Frame-Zahlen aus <paramref name="frameCounts"/>, die sich wiederholen, bis der
    /// Puffer aufgebraucht ist; das letzte Stück kann kürzer sein.
    /// </summary>
    public static IReadOnlyList<byte[]> Split(byte[] buffer, int frameBytes, IReadOnlyList<int> frameCounts)
    {
        var pieces = new List<byte[]>();
        var at = 0;
        for (var i = 0; at < buffer.Length; i++)
        {
            var length = Math.Min(frameCounts[i % frameCounts.Count] * frameBytes, buffer.Length - at);
            pieces.Add(buffer[at..(at + length)]);
            at += length;
        }

        return pieces;
    }

    public static double SineAt(int sampleRate, long frame, double frequency, double amplitude) =>
        amplitude * Math.Sin(2 * Math.PI * frequency * frame / sampleRate);

    private static double Scale(double value, long fullScale) =>
        Math.Clamp(Math.Round(value * fullScale, MidpointRounding.AwayFromZero), -fullScale - 1, fullScale);
}

/// <summary>Fortlaufender Sinus im Format des Geräts: jeder Puffer setzt die Phase des vorherigen fort.</summary>
internal sealed class SignalSource(CaptureFormat format, double frequency = 440, double amplitude = 0.5)
{
    private readonly Lock _gate = new();
    private long _frame;

    public CaptureFormat Format { get; } = format;

    public byte[] Next(int frames)
    {
        long first;
        lock (_gate)
        {
            first = _frame;
            _frame += frames;
        }

        return TestSignals.Sine(Format, frames, frequency, amplitude, first);
    }

    public byte[] Next(TimeSpan duration) => Next(TestSignals.Frames(Format, duration));
}
