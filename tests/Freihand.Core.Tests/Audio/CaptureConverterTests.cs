using Freihand.Adapters.Audio;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Audio.Kit;
using static Freihand.Core.Tests.Audio.Kit.SignalAnalysis;

namespace Freihand.Core.Tests.Audio;

/// <summary>
/// R34, R35 und der Wandler-Teil von R49: Umwandlung ins Format von P3 Vertrag 1 (P8-Konzept, Kapitel 4.3 und 5.2).
/// <para>Toleranz der Sample-Zahl: ±16 Samples (1 ms), wie R34 sie für 1 s festlegt. Sie deckt Latenz und Nachlauf des
/// WDL-Resamplers ab; beide hängen nur vom Filter ab, nicht von der Dauer, darum gilt dieselbe feste Grenze für jede
/// Dauer.</para>
/// </summary>
public sealed class CaptureConverterTests
{
    /// <summary>Feste Toleranz der Sample-Zahl nach R34.</summary>
    internal const int SampleTolerance = 16;

    internal static readonly TimeSpan ChunkDuration = TimeSpan.FromMilliseconds(200);

    private static readonly CaptureFormat StereoFloat48 = new(48_000, 2, CaptureSampleFormat.Float32);

    /// <summary>Wandelt einen ganzen Puffer und liefert alle Blöcke samt <see cref="CaptureConverter.Flush"/>.</summary>
    internal static IReadOnlyList<AudioChunk> Convert(CaptureFormat format, IEnumerable<byte[]> pieces)
    {
        var converter = new CaptureConverter(format, ChunkDuration);
        var chunks = new List<AudioChunk>();
        foreach (var piece in pieces)
        {
            chunks.AddRange(converter.Push(piece));
        }

        chunks.AddRange(converter.Flush());
        return chunks;
    }

    [Fact]
    public void R34_SineIsMixedResampledAndKeepsFrequencyAndLevel()
    {
        var input = TestSignals.StereoSine(StereoFloat48, 48_000, rightFactor: 1);

        var chunks = Convert(StereoFloat48, [input]);

        var samples = Samples(chunks);
        Assert.InRange(samples.Length, 16_000 - SampleTolerance, 16_000 + SampleTolerance);
        Assert.InRange(DominantFrequency(samples), 435, 445);
        Assert.InRange(Db(Rms(samples), SineRms(0.5)), -1, 1);
        AssertBlocks(chunks, "1 s Sinus");
    }

    [Fact]
    public void R34_OppositeChannelsAreAveragedToSilence()
    {
        // Rechts gleich minus links: das Mittel ist still; ein Wandler, der nur einen Kanal nimmt, fällt auf
        var input = TestSignals.StereoSine(StereoFloat48, 48_000, rightFactor: -1);

        var samples = Samples(Convert(StereoFloat48, [input]));

        Assert.InRange(samples.Length, 16_000 - SampleTolerance, 16_000 + SampleTolerance);
        Assert.All(samples, sample => Assert.InRange(sample, (short)-2, (short)2));
    }

    /// <summary>
    /// Übersteuerter Eingang wird begrenzt, nie überlaufen. Geprüft wird jedes Sample auf das Vorzeichen (ein Überlauf
    /// kippt es) und der eingeschwungene Teil auf den genauen Grenzwert; je 20 ms am Anfang und am Ende bleiben dabei weg,
    /// weil der Resampler dort aus der Stille ein- und in den Nachlauf ausschwingt.
    /// </summary>
    [Theory]
    [InlineData(1.5, 32_767)]
    [InlineData(-1.5, -32_768)]
    public void R34_OverRangeIsClampedToTheLimit(double value, int limit)
    {
        var input = TestSignals.Constant(StereoFloat48, 48_000, value);

        var samples = Samples(Convert(StereoFloat48, [input]));

        Assert.InRange(samples.Length, 16_000 - SampleTolerance, 16_000 + SampleTolerance);
        Assert.All(samples, sample => Assert.True(sample * Math.Sign(value) >= 0, $"Sample {sample} mit falschem Vorzeichen (Überlauf)."));
        const int Settle = 320;
        Assert.All(samples[Settle..^Settle], sample => Assert.Equal(limit, sample));
    }

    [Fact]
    public void R34_Native16kMonoPcm16PassesBitExact()
    {
        var format = new CaptureFormat(16_000, 1, CaptureSampleFormat.Pcm16);
        var input = TestSignals.Pcm16Pattern(16_800);

        var chunks = Convert(format, TestSignals.Split(input, 2, [1_000, 4_321, 7, 2_600]));

        // Gleiche Bytes, gleiche Zahl, ohne Nachlauf: 5 volle Blöcke und der Rest von 800 Samples
        Assert.Equal(input, Bytes(chunks));
        AssertBlocks(chunks, "16 kHz mono PCM16");
        Assert.Equal(6, chunks.Count);
        Assert.Equal(800, SampleCount(chunks[^1]));
    }

    [Fact]
    public void R35_OddPiecesGiveFixedBlocksWithContiguousOffsets()
    {
        // 7, 13, 20 und 3 ms im Wechsel, zusammen 1,05 s bei 48 kHz
        var input = TestSignals.Sine(StereoFloat48, 50_400);
        var pieces = TestSignals.Split(input, TestSignals.FrameBytes(StereoFloat48), [336, 624, 960, 144]);
        var converter = new CaptureConverter(StereoFloat48, ChunkDuration);

        var pushed = new List<AudioChunk>();
        foreach (var piece in pieces)
        {
            pushed.AddRange(converter.Push(piece));
        }

        var flushed = converter.Flush();
        IReadOnlyList<AudioChunk> chunks = [.. pushed, .. flushed];

        // Push liefert die vollen Blöcke; Flush genau den kürzeren Rest samt Nachlauf (16 800 ± 16 Samples)
        Assert.Equal(5, pushed.Count);
        var last = Assert.Single(flushed);
        Assert.InRange(SampleCount(last), 800 - SampleTolerance, 800 + SampleTolerance);
        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.6), TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.0)],
            chunks.Select(chunk => chunk.Offset));
        AssertBlocks(chunks, "krumme Stücke");

        // Summe der Samples gleich dem Ende der Ausgabe: keine Lücke und keine Überlappung
        Assert.Equal(End(chunks[^1]), Duration(TotalSamples(chunks)));
        Assert.All(chunks, chunk => Assert.Equal(16_000, chunk.SampleRate));
    }

    [Fact]
    public void R49_ConverterGapDeliversTheRestAndShiftsTheNextBlock()
    {
        var format = new CaptureFormat(48_000, 1, CaptureSampleFormat.Float32);
        var signal = new SignalSource(format);
        var converter = new CaptureConverter(format, ChunkDuration);

        var before = new List<AudioChunk>(converter.Push(signal.Next(24_000)));
        before.AddRange(converter.Gap(4_800));
        var after = new List<AudioChunk>(converter.Push(signal.Next(24_000)));
        after.AddRange(converter.Flush());

        // Vor der Lücke: volle Blöcke, dann ein kürzerer Block samt Nachlauf; zusammen 0,5 s
        AssertBlocks(before, "vor der Lücke");
        Assert.True(SampleCount(before[^1]) < BlockSamples, "Kein kürzerer Block vor der Lücke.");
        Assert.InRange(TotalSamples(before), 8_000 - SampleTolerance, 8_000 + SampleTolerance);

        // Danach: genau 1 600 Samples (0,1 s) nach dem Ende des letzten Blocks, wieder lückenlos
        AssertBlocks(after, "nach der Lücke", start: End(before[^1]) + Duration(1_600));
        Assert.InRange(TotalSamples(after), 8_000 - SampleTolerance, 8_000 + SampleTolerance);
    }
}
