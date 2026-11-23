namespace Freihand.Benchmark.Tests;

/// <summary>
/// Gegenproben C01–C04 und B01 aus der Codex-Nachprüfung (Regressionstest),
/// unverändert übernommen.
/// </summary>
public sealed class Pr20ReviewCounterTests
{
    [Fact]
    public void C01_ExactlySixTenthsOfSilenceEndsSentence()
    {
        var result = Sentences([(1.0, 4.0), (4.6, 6.0)], 25, 7);
        Assert.Equal(2, result.Count);
        Assert.Equal(4.6, result[0].Available, 6);
    }

    [Fact]
    public void C02_MaximumReachedWithoutConfirmedPauseCutsImmediately()
    {
        // Identisches Audio bis t=8; erst danach unterscheiden sich die Fortsetzungen.
        var stops = Sentences([(0.0, 8.0)], 8, 10);
        var continues = Sentences([(0.0, 9.0)], 8, 10);
        Assert.Equal(continues[0], stops[0]);
        Assert.True(stops[0].LengthCut);
        Assert.Equal(8.0, stops[0].Available);
    }

    [Fact]
    public void C03_SamePrefixMakesSameLengthCutAtPreviousShortGap()
    {
        var stops = Sentences([(0.0, 5.0), (5.2, 8.0)], 8, 10);
        var continues = Sentences([(0.0, 5.0), (5.2, 9.0)], 8, 10);
        Assert.Equal(continues[0], stops[0]);
        Assert.Equal(5.0, stops[0].End);
        Assert.Equal(8.0, stops[0].Available);
    }

    [Fact]
    public void C04_AcceptedWindowMustAdvanceTheIterator()
    {
        var args = Arguments.Parse(["run", "--window", "1e-20"]);
        try
        {
            var max = args.Positive("window", 25);
            var simulation = new SentenceSimulation(new FakeTranscriber(), new SentenceOptions(0.6, max), new FakeSegmenter());
            var parts = simulation.Sentences([(1.0, 2.0)], 3).Take(3).ToList();
            Assert.All(parts, part => Assert.True(part.End > part.Start));
        }
        catch (ArgumentException)
        {
            // Ein nicht darstellbares Fenster darf vor der Iteration abgewiesen werden (M05).
        }
    }

    [Fact]
    public void B01_EnergyMinimumStillExplicitlyDiscards180Milliseconds()
    {
        var audio = new float[5 * WavFile.SampleRate];
        Array.Fill(audio, 0.5f, 2 * WavFile.SampleRate, (int)(0.18 * WavFile.SampleRate));
        Assert.Empty(new EnergySegmenter(0.6).Detect(audio));
    }

    private static List<SentenceSimulation.Sentence> Sentences(IReadOnlyList<(double Start, double End)> regions, double max, double total) =>
        [.. new SentenceSimulation(new FakeTranscriber(), new SentenceOptions(0.6, max), new FakeSegmenter()).Sentences(regions, total)];

    private sealed class FakeSegmenter : ISpeechSegmenter
    {
        public string Name => "synthetisch";
        public IReadOnlyList<(double Start, double End)> Detect(float[] audio) => [];
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public Task<IReadOnlyList<string>> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
