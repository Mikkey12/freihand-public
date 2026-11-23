using Freihand.Adapters.Recognition;
using Freihand.Core.Live;

namespace Freihand.Benchmark.Tests;

/// <summary>Befehl <c>chain</c> (P8-Konzept, Kapitel 3.2): Einstellungen und Blockbildung ohne Erkenner.</summary>
public sealed class ChainTests
{
    private static ChainSettings Parse(params string[] extra) =>
        ChainSettings.FromArguments(Arguments.Parse(["chain", "--model", "m.bin", "--vad", "v.bin", "--audio", "R1.wav", .. extra]));

    [Fact]
    public void DefaultsFollowTheConcept()
    {
        var settings = Parse();

        Assert.Equal(LiveMode.Live, settings.Mode);
        Assert.Equal("de-CH", settings.Language);
        Assert.Equal([RecognizerRuntime.Cuda, RecognizerRuntime.Cpu], settings.Runtimes);
        Assert.Equal(4, settings.Threads);
        Assert.Equal(1500, settings.AudioContext);
        Assert.Empty(settings.Hints);

        // Startwerte von P3 Vertrag 4 (ER-01: 15 s seit 03.11.2026); beide unter den 20 s des Erkenners (Kapitel 5.5)
        var live = settings.LiveOptions();
        Assert.Equal(TimeSpan.FromSeconds(15), live.Window);
        Assert.Equal(TimeSpan.FromSeconds(15), live.MaxUtterance);
        Assert.Equal(TimeSpan.FromSeconds(0.6), live.PauseThreshold);
    }

    [Fact]
    public void ModeRuntimeAndHintsAreRead()
    {
        var settings = Parse("--mode", "satz", "--runtime", "vulkan,cpu", "--hints", "Musterbegriff, Freihand", "--audio-context", "600");

        Assert.Equal(LiveMode.Sentence, settings.Mode);
        Assert.Equal([RecognizerRuntime.Vulkan, RecognizerRuntime.Cpu], settings.Runtimes);
        Assert.Equal(["Musterbegriff", "Freihand"], settings.Hints);
        Assert.Equal(600, settings.AudioContext);
    }

    [Theory]
    [InlineData("10", 10, 10)]
    [InlineData("20", 20, 20)]
    [InlineData("5", 5, 10)]
    public void MaxUtteranceIsReadAndTheLiveWindowGrowsWithIt(string value, double maxUtterance, double window)
    {
        var live = Parse("--max-utterance", value).LiveOptions();

        Assert.Equal(TimeSpan.FromSeconds(maxUtterance), live.MaxUtterance);
        Assert.Equal(TimeSpan.FromSeconds(window), live.Window);
    }

    [Theory]
    [InlineData("--max-utterance", "20.5")]
    [InlineData("--max-utterance", "0.5")]
    [InlineData("--mode", "schnell")]
    [InlineData("--runtime", "gpu")]
    [InlineData("--runtime", "cpu,cpu")]
    [InlineData("--language", "rm")]
    public void InvalidSettingsAreRejected(string option, string value)
    {
        Assert.ThrowsAny<Exception>(() => Parse(option, value));
    }

    [Fact]
    public void MissingModelOrVadIsRejected()
    {
        Assert.Throws<ArgumentException>(() => ChainSettings.FromArguments(Arguments.Parse(["chain", "--vad", "v.bin", "--audio", "R1.wav"])));
        Assert.Throws<ArgumentException>(() => ChainSettings.FromArguments(Arguments.Parse(["chain", "--model", "m.bin", "--audio", "R1.wav"])));
    }

    [Fact]
    public void ChunksAreGaplessPcm16BlocksOf200Milliseconds()
    {
        var samples = new float[16_000 + 1_000];
        samples[0] = 0.5f;
        samples[1] = -1.5f;
        samples[2] = 1.5f;

        var chunks = Chain.ToChunks(samples);

        Assert.Equal(6, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal(16_000, chunk.SampleRate));
        Assert.All(chunks, chunk => Assert.Equal(0, chunk.Offset.Ticks % 625));
        Assert.All(chunks.Take(5), chunk => Assert.Equal(3_200 * 2, chunk.Pcm16Mono.Length));
        Assert.Equal(1_000 * 2, chunks[^1].Pcm16Mono.Length);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].Offset + TimeSpan.FromTicks(chunks[i - 1].Pcm16Mono.Length / 2 * 625L), chunks[i].Offset);
        }

        var first = chunks[0].Pcm16Mono.Span;
        Assert.Equal(16_384, BitConverter.ToInt16(first[..2]));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(first[2..4]));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(first[4..6]));
    }
}
