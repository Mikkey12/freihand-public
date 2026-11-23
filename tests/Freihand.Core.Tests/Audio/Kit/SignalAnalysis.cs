using System.Buffers.Binary;
using Freihand.Adapters.Audio;
using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.Audio.Kit;

/// <summary>Auswertung der Blöcke nach P3 Vertrag 1: 16 kHz mono PCM16, ein Sample sind 625 Ticks.</summary>
internal static class SignalAnalysis
{
    public const int SampleRate = 16_000;

    public const long TicksPerSample = TimeSpan.TicksPerSecond / SampleRate;

    /// <summary>Samples eines Blocks von 3 200 Samples bei 200 ms (Startwert, Kapitel 4.3).</summary>
    public const int BlockSamples = 3_200;

    /// <summary>Volle Skala für den Pegel: Wert × 32 768 (Kapitel 5.2).</summary>
    public const double FullScale = 32_768;

    public static int SampleCount(AudioChunk chunk) => chunk.Pcm16Mono.Length / 2;

    public static long TotalSamples(IEnumerable<AudioChunk> chunks) => chunks.Sum(chunk => (long)SampleCount(chunk));

    public static TimeSpan Duration(long samples) => TimeSpan.FromTicks(samples * TicksPerSample);

    /// <summary>Sitzungszeit direkt nach dem letzten Sample des Blocks.</summary>
    public static TimeSpan End(AudioChunk chunk) => chunk.Offset + Duration(SampleCount(chunk));

    /// <summary>Alle Samples der Blöcke aneinandergereiht.</summary>
    public static short[] Samples(IEnumerable<AudioChunk> chunks)
    {
        var all = new List<short>();
        foreach (var chunk in chunks)
        {
            var bytes = chunk.Pcm16Mono.Span;
            for (var i = 0; i + 1 < bytes.Length; i += 2)
            {
                all.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes[i..]));
            }
        }

        return [.. all];
    }

    /// <summary>Alle Bytes der Blöcke aneinandergereiht.</summary>
    public static byte[] Bytes(IEnumerable<AudioChunk> chunks) => [.. chunks.SelectMany(chunk => chunk.Pcm16Mono.ToArray())];

    public static double Rms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Pegelabstand in dB.</summary>
    public static double Db(double value, double reference) => 20 * Math.Log10(value / reference);

    /// <summary>Effektivwert eines Sinus der Amplitude <paramref name="amplitude"/> in PCM16 (× 32 768).</summary>
    public static double SineRms(double amplitude) => amplitude / Math.Sqrt(2) * FullScale;

    /// <summary>
    /// Hauptfrequenz über aufsteigende Nulldurchgänge, linear zwischen zwei Samples eingepasst. Je 5 % am Anfang und am
    /// Ende bleiben weg, damit Ein- und Ausschwingen des Resamplers nicht zählen. Ohne zwei Durchgänge 0.
    /// </summary>
    public static double DominantFrequency(ReadOnlySpan<short> samples, int sampleRate = SampleRate)
    {
        var skip = samples.Length / 20;
        double first = -1;
        double last = -1;
        var crossings = 0;
        for (var i = skip + 1; i < samples.Length - skip; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                var at = i - 1 + (-samples[i - 1] / (double)(samples[i] - samples[i - 1]));
                if (crossings == 0)
                {
                    first = at;
                }

                last = at;
                crossings++;
            }
        }

        return crossings < 2 ? 0 : (crossings - 1) * (double)sampleRate / (last - first);
    }

    /// <summary>
    /// Blockbildung nach Kapitel 5.2 und P3 Vertrag 1: <see cref="SampleRate"/>, alle Blöcke ausser dem letzten mit
    /// <paramref name="blockSamples"/> Samples, der letzte nicht leer und höchstens so lang, Offsets ab
    /// <paramref name="start"/> lückenlos und aufsteigend, jeder ein Vielfaches von 625 Ticks.
    /// </summary>
    public static void AssertBlocks(
        IReadOnlyList<AudioChunk> chunks, string context, TimeSpan? start = null, int blockSamples = BlockSamples)
    {
        Assert.True(chunks.Count > 0, $"{context}: keine Blöcke.");
        var expected = start ?? TimeSpan.Zero;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var samples = SampleCount(chunk);
            Assert.True(chunk.SampleRate == SampleRate, $"{context}: Block {i} mit {chunk.SampleRate} Hz.");
            Assert.True(chunk.Pcm16Mono.Length % 2 == 0, $"{context}: Block {i} mit halbem Sample.");
            Assert.True(
                chunk.Offset.Ticks % TicksPerSample == 0,
                $"{context}: Offset {chunk.Offset.Ticks} von Block {i} ist kein Vielfaches von 625 Ticks.");
            Assert.True(
                chunk.Offset == expected,
                $"{context}: Block {i} beginnt bei {chunk.Offset.Ticks} statt lückenlos bei {expected.Ticks} Ticks.");
            if (i < chunks.Count - 1)
            {
                Assert.True(samples == blockSamples, $"{context}: Block {i} von {chunks.Count} hat {samples} statt {blockSamples} Samples.");
            }
            else
            {
                Assert.True(
                    samples > 0 && samples <= blockSamples,
                    $"{context}: letzter Block hat {samples} Samples (erlaubt 1 bis {blockSamples}).");
            }

            expected = End(chunk);
        }
    }

    /// <summary>Gleiche Blöcke: Zahl, Offsets, Abtastrate und jedes Byte.</summary>
    public static void AssertSameAudio(IReadOnlyList<AudioChunk> expected, IReadOnlyList<AudioChunk> actual, string context)
    {
        Assert.True(
            TotalSamples(expected) == TotalSamples(actual),
            $"{context}: {TotalSamples(actual)} Samples geliefert, umgewandelt waren {TotalSamples(expected)}.");
        Assert.True(expected.Count == actual.Count, $"{context}: {actual.Count} Blöcke statt {expected.Count}.");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].Offset == actual[i].Offset, $"{context}: Block {i} bei {actual[i].Offset} statt {expected[i].Offset}.");
            Assert.True(expected[i].SampleRate == actual[i].SampleRate, $"{context}: Block {i} mit {actual[i].SampleRate} Hz.");
            Assert.True(
                expected[i].Pcm16Mono.Span.SequenceEqual(actual[i].Pcm16Mono.Span),
                $"{context}: Block {i} ({SampleCount(actual[i])} Samples) weicht vom Umgewandelten ab.");
        }
    }
}

/// <summary>
/// Was eine Aufnahmesitzung aus den Puffern des Geräts liefern muss (Kapitel 5.2), gerechnet mit einem eigenen
/// <see cref="CaptureConverter"/> über genau dieselben Puffer in derselben Stückelung: jeder Puffer ein
/// <see cref="CaptureConverter.Push"/>; ein Aussetzer mit bekannter Zahl fehlender Frames zuerst
/// <see cref="CaptureConverter.Gap"/>; ein Aussetzer unbekannter Dauer beendet die Aufnahme, sein Puffer und alles
/// danach entfallen (nie zusammengeschoben); am Ende <see cref="CaptureConverter.Flush"/> samt Nachlauf.
/// <para>Annahme der Tests: Die Sitzung wandelt jeden Gerätepuffer während seines Ereignisses mit einem Push um; der
/// Puffer gilt nur so lange (wie bei NAudio). Gleiche Eingabe in gleicher Stückelung ergibt mit dem deterministischen
/// Wandler dieselben Blöcke.</para>
/// </summary>
internal static class ReferenceConversion
{
    public static IReadOnlyList<AudioChunk> Of(CaptureFormat format, TimeSpan chunkDuration, IEnumerable<CaptureData> delivered)
    {
        var converter = new CaptureConverter(format, chunkDuration);
        var chunks = new List<AudioChunk>();
        foreach (var data in delivered)
        {
            if (data.Discontinuity)
            {
                if (data.MissingFrames is not { } missing)
                {
                    break;
                }

                chunks.AddRange(converter.Gap(missing));
            }

            chunks.AddRange(converter.Push(data.Buffer.Span));
        }

        chunks.AddRange(converter.Flush());
        return chunks;
    }

    /// <summary>Frames, die nach den Regeln oben in die Umwandlung eingehen.</summary>
    public static long ConvertedFrames(CaptureFormat format, IEnumerable<CaptureData> delivered)
    {
        long frames = 0;
        foreach (var data in delivered)
        {
            if (data.Discontinuity && data.MissingFrames is null)
            {
                break;
            }

            frames += data.Buffer.Length / TestSignals.FrameBytes(format);
        }

        return frames;
    }
}
