using System.Buffers.Binary;

namespace Freihand.Benchmark;

/// <summary>Liest WAV-Dateien in 16 kHz, 16 Bit, mono, wie sie <see cref="Recorder"/> schreibt.</summary>
internal static class WavFile
{
    public const int SampleRate = 16_000;

    public static float[] ReadMono16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || !Tag(bytes, 0, "RIFF") || !Tag(bytes, 8, "WAVE"))
        {
            throw new InvalidDataException($"{path} ist keine WAV-Datei.");
        }

        var formatOk = false;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
            var body = offset + 8;
            if (size < 0 || body + size > bytes.Length)
            {
                throw new InvalidDataException($"{path}: beschädigter Abschnitt bei Byte {offset}.");
            }

            if (Tag(bytes, offset, "fmt "))
            {
                var format = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body));
                var channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 2));
                var rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4));
                var bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 14));
                formatOk = format == 1 && channels == 1 && rate == SampleRate && bits == 16;
                if (!formatOk)
                {
                    throw new InvalidDataException(
                        $"{path}: erwartet PCM 16 kHz, 16 Bit, mono; gefunden Format {format}, {rate} Hz, {bits} Bit, {channels} Kanäle.");
                }
            }
            else if (Tag(bytes, offset, "data"))
            {
                if (!formatOk)
                {
                    throw new InvalidDataException($"{path}: Formatangabe fehlt vor den Audiodaten.");
                }

                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + (2 * i))) / 32768f;
                }

                return samples;
            }

            offset = body + size + (size % 2);
        }

        throw new InvalidDataException($"{path}: keine Audiodaten gefunden.");
    }

    private static bool Tag(byte[] bytes, int offset, string tag) =>
        bytes.AsSpan(offset, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(tag));
}
