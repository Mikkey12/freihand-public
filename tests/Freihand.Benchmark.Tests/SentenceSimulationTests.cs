namespace Freihand.Benchmark.Tests;

/// <summary>
/// Satzgrenzen im satzweisen Modus. M03 und M05 sowie die Gegenproben B02 und B03 stammen aus dem Codex-Review
/// (Regressionstest).
/// </summary>
public sealed class SentenceSimulationTests
{
    private const double Pause = 0.6;

    [Fact]
    public void M03_ShortVadGapBelowPauseStaysInOneSentence()
    {
        // Silero trennt schon bei 0,3 s; 6,94 bis 7,11 s ist keine Satzpause
        var sentences = Sentences([(0.5, 6.94), (7.11, 9.0)], max: 25, total: 12);

        var sentence = Assert.Single(sentences);
        Assert.Equal((0.5, 9.0), (sentence.Start, sentence.End));
        Assert.Equal(9.0 + Pause, sentence.Available, 6);
        Assert.False(sentence.LengthCut);
    }

    [Fact]
    public void M03_GapOfAtLeastPauseEndsSentence()
    {
        var sentences = Sentences([(0.5, 3.0), (3.6, 5.0)], max: 25, total: 12);

        Assert.Equal(2, sentences.Count);
        Assert.Equal(3.0 + Pause, sentences[0].Available, 6);
        Assert.All(sentences, s => Assert.False(s.LengthCut));
    }

    [Fact]
    public void M03_LengthLimitCutsAtLastShortGapAndIsMarked()
    {
        var sentences = Sentences([(0.0, 5.0), (5.2, 9.0)], max: 8, total: 12);

        Assert.Equal(2, sentences.Count);
        Assert.Equal((0.0, 5.0), (sentences[0].Start, sentences[0].End));
        Assert.True(sentences[0].LengthCut);
        // Den Schnitt kennt ein laufendes System erst, wenn die Höchstlänge erreicht ist, nicht schon beim Wiedereinsetzen
        Assert.Equal(8.0, sentences[0].Available, 6);
        Assert.False(sentences[1].LengthCut);
    }

    [Fact]
    public void LengthCutReachedDuringShortGapIsDecidedAtTheLimit()
    {
        // Höchstlänge bei 8 s mitten in der kurzen Pause: entschieden wird bei 8 s, ohne auf die Fortsetzung zu warten
        var sentences = Sentences([(0.0, 7.9), (8.2, 9.0)], max: 8, total: 12);

        Assert.Equal((0.0, 7.9), (sentences[0].Start, sentences[0].End));
        Assert.True(sentences[0].LengthCut);
        Assert.Equal(8.0, sentences[0].Available, 6);
    }

    [Fact]
    public void PrefixDecisionsMatchFullRecording()
    {
        // F07: Was bis zu einem Zeitpunkt entschieden ist, darf nicht davon abhängen, wie es danach weitergeht. Für jeden
        // Zeitpunkt t werden die Bereiche bei t abgeschnitten; alle Sätze, die vor t verfügbar sind, müssen gleich sein.
        var regions = new List<(double, double)>
        {
            (0.0, 3.0), (3.2, 7.5), (7.7, 8.0), (8.3, 12.0), (12.9, 14.0), (14.1, 30.0), (30.6, 31.0), (31.4, 39.0),
        };
        var full = Sentences(regions, max: 8, total: 1000);

        for (var t = 0.05; t < 40; t += 0.05)
        {
            var cut = t;
            var prefix = regions.Where(r => r.Item1 < cut).Select(r => (r.Item1, Math.Min(r.Item2, cut))).ToList();
            var decided = Sentences(prefix, max: 8, total: 1000).Where(s => s.Available < cut - 1e-9).ToList();

            Assert.Equal(full.Where(s => s.Available < cut - 1e-9), decided);
        }
    }

    [Fact]
    public void AvailabilityNeverUsesFutureAudio()
    {
        // Kein Satz ist verfügbar, bevor sein Ende im Audio liegt und die Entscheidung möglich ist
        var regions = new List<(double, double)> { (0.0, 3.0), (3.2, 7.5), (7.7, 12.0), (12.9, 14.0), (14.1, 30.0) };

        foreach (var sentence in Sentences(regions, max: 8, total: 31))
        {
            Assert.True(sentence.Available >= sentence.End, $"{sentence}");
            Assert.True(sentence.LengthCut || sentence.Available >= Math.Min(31, sentence.End + Pause) - 1e-9, $"{sentence}");
            Assert.True(!sentence.LengthCut || sentence.Available >= sentence.Start + 8 - 1e-9, $"{sentence}");
        }
    }

    [Fact]
    public void B02_ContinuousSpeechOverMaximumIsSplitAsLengthCut()
    {
        var sentences = Sentences([(2.0, 4.5)], max: 1, total: 6);

        Assert.Equal([(2.0, 3.0), (3.0, 4.0), (4.0, 4.5)], sentences.Select(s => (s.Start, s.End)));
        Assert.All(sentences, s => Assert.True(s.LengthCut));
        Assert.Equal(3.0, sentences[0].Available, 6);
        // Bei 5 s ist die Höchstlänge erreicht, die Pause (bis 5,1 s) aber noch nicht bestätigt
        Assert.Equal(5.0, sentences[2].Available, 6);
    }

    [Fact]
    public void LastSentenceIsAvailableAtEndOfRecording()
    {
        var sentence = Assert.Single(Sentences([(1.0, 4.8)], max: 25, total: 5));

        Assert.Equal(5.0, sentence.Available, 6);
    }

    [Theory]
    [InlineData(0, 0.6)]
    [InlineData(-1, 0.6)]
    [InlineData(10, 0)]
    public void B03_NonPositiveLimitsAreRejected(double max, double pause)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SentenceSimulation(new FakeTranscriber(), new SentenceOptions(pause, max), new FakeSegmenter([])));
    }

    [Fact]
    public async Task RunRecordsSegmentEndLengthCutAndSegmentationTime()
    {
        var segmenter = new FakeSegmenter([(0.0, 5.0), (5.2, 9.0)]);
        var simulation = new SentenceSimulation(new FakeTranscriber(), new SentenceOptions(Pause, 8), segmenter);

        var result = await simulation.RunAsync(new float[12 * WavFile.SampleRate]);

        Assert.Equal(2, result.Fixed.Count);
        Assert.True(result.Fixed[0].LengthCut);
        Assert.Equal(5.0, result.Steps[0].LastSegmentEndSeconds);
        Assert.True(result.SegmentationSeconds >= 0);
        Assert.Equal(0, result.Steps.Sum(s => s.SuspendedSeconds ?? 0));
    }

    private static List<SentenceSimulation.Sentence> Sentences(
        IReadOnlyList<(double Start, double End)> regions, double max, double total) =>
        [.. new SentenceSimulation(new FakeTranscriber(), new SentenceOptions(Pause, max), new FakeSegmenter(regions))
            .Sentences(regions, total)];

    private sealed class FakeSegmenter(IReadOnlyList<(double Start, double End)> regions) : ISpeechSegmenter
    {
        public string Name => "attrappe";

        public IReadOnlyList<(double Start, double End)> Detect(float[] audio) => regions;
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public Task<IReadOnlyList<string>> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["Prüftext"]);
    }
}
