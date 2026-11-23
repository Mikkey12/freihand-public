using Freihand.Adapters.Audio;
using Freihand.Core.Tests.Audio.Kit;
using static Freihand.Core.Tests.Audio.Kit.SignalAnalysis;

namespace Freihand.Core.Tests.Audio;

/// <summary>
/// Matrix der Umwandlung (Kapitel 5.2, P3 Vertrag 1): jedes <see cref="CaptureSampleFormat"/> × Kanäle (1, 2, 6) ×
/// Abtastrate (16 000, 44 100, 48 000) × Stückelung der Gerätepuffer (ganze Blöcke, krumme Stücke, einzelne Frames),
/// zusammen 108 Fälle. Eingang ist 1,05 s Sinus 440 Hz mit Amplitude 0,5, auf allen Kanälen gleich; 1,05 s ergeben
/// 16 800 Samples, also fünf volle Blöcke und einen kürzeren letzten.
/// <para>Je Fall: jede <c>SampleRate</c> 16 000; Blockgrösse 3 200 ausser dem letzten; Offsets lückenlos ab 0 als
/// Vielfache von 625 Ticks; Gesamtzahl 16 800 ± 16 Samples (feste Toleranz nach R34, siehe
/// <see cref="CaptureConverterTests"/>: Latenz und Nachlauf des Resamplers hängen nicht von Dauer, Format oder Stückelung
/// ab); Pegel innerhalb ±1 dB von 0,5/√2 × 32 768; Hauptfrequenz 440 ± 5 Hz wie in R34. 16 kHz mono PCM16 muss in jeder
/// Stückelung bitgenau durchgehen.</para>
/// <para>Krumme Stücke sind 7, 13, 20 und 3 ms im Wechsel wie in R35, auf ganze Frames gerundet; Puffer enthalten immer
/// ganze Frames (Kapitel 4.3: „verschachtelte Frames“).</para>
/// </summary>
public sealed class ConversionMatrixTests
{
    public enum Chunking
    {
        /// <summary>Je Gerätepuffer genau 200 ms, also ein Block.</summary>
        WholeBlocks,

        /// <summary>7, 13, 20 und 3 ms im Wechsel.</summary>
        OddPieces,

        /// <summary>Jeder Frame ein eigener Puffer.</summary>
        SingleFrames,
    }

    private static readonly TimeSpan Length = TimeSpan.FromSeconds(1.05);

    public static TheoryData<CaptureSampleFormat, int, int, Chunking> Cases()
    {
        var cases = new TheoryData<CaptureSampleFormat, int, int, Chunking>();
        foreach (var sampleFormat in Enum.GetValues<CaptureSampleFormat>())
        {
            foreach (var channels in new[] { 1, 2, 6 })
            {
                foreach (var rate in new[] { 16_000, 44_100, 48_000 })
                {
                    foreach (var chunking in Enum.GetValues<Chunking>())
                    {
                        cases.Add(sampleFormat, channels, rate, chunking);
                    }
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void R35_EveryFormatAndChunkingGivesFixedBlocksAndKeepsTheSignal(
        CaptureSampleFormat sampleFormat, int channels, int rate, Chunking chunking)
    {
        var format = new CaptureFormat(rate, channels, sampleFormat);
        var context = $"{sampleFormat}, {channels} Kanäle, {rate} Hz, {chunking}";
        var frames = TestSignals.Frames(format, Length);
        var native = format == new CaptureFormat(16_000, 1, CaptureSampleFormat.Pcm16);
        var input = native ? TestSignals.Pcm16Pattern(frames) : TestSignals.Sine(format, frames);
        IReadOnlyList<int> pattern = chunking switch
        {
            Chunking.WholeBlocks => [TestSignals.Frames(format, CaptureConverterTests.ChunkDuration)],
            Chunking.OddPieces => [.. new[] { 7, 13, 20, 3 }.Select(ms => TestSignals.Frames(format, TimeSpan.FromMilliseconds(ms)))],
            _ => [1],
        };

        var chunks = CaptureConverterTests.Convert(format, TestSignals.Split(input, TestSignals.FrameBytes(format), pattern));

        AssertBlocks(chunks, context);
        Assert.True(SampleCount(chunks[^1]) < BlockSamples, $"{context}: letzter Block nicht kürzer.");
        if (native)
        {
            Assert.True(input.AsSpan().SequenceEqual(Bytes(chunks)), $"{context}: nicht bitgenau durchgereicht.");
            return;
        }

        var samples = Samples(chunks);
        var tolerance = CaptureConverterTests.SampleTolerance;
        Assert.True(
            Math.Abs(samples.Length - 16_800) <= tolerance,
            $"{context}: {samples.Length} Samples statt 16 800 ± {tolerance}.");
        var level = Db(Rms(samples), SineRms(0.5));
        Assert.True(Math.Abs(level) <= 1, $"{context}: Pegel {level:F2} dB neben dem Sinus.");
        var frequency = DominantFrequency(samples);
        Assert.True(Math.Abs(frequency - 440) <= 5, $"{context}: Hauptfrequenz {frequency:F1} Hz.");
    }
}
