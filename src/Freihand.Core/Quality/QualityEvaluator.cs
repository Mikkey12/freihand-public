namespace Freihand.Core.Quality;

/// <summary>Rechnet aus einem Messlauf alle Messwerte nach QM-02 aus. Reine Funktion ohne Seiteneffekte.</summary>
public static class QualityEvaluator
{
    public static QualityReport Evaluate(QualityRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var settings = run.Settings;
        return new QualityReport(
            QualityReport.CurrentSchema,
            run.Measurement,
            run.AudioDuration,
            settings,
            EvaluateRealTimeFactor(run),
            EvaluateLatency(run),
            EvaluateWordErrors(run.Sentences),
            EvaluateTerms(run.Sentences),
            DictationMarkErrors.Sum(run.Sentences.Select(s => DictationMarks.Evaluate(s.Reference, s.Hypothesis))),
            CommandErrors.Evaluate(run.Commands, run.AudioDuration > TimeSpan.Zero ? run.AudioDuration : null));
    }

    private static RealTimeFactorReport EvaluateRealTimeFactor(QualityRun run)
    {
        if (run.Compute.Count == 0 || run.AudioDuration <= TimeSpan.Zero)
        {
            return new RealTimeFactorReport(null, null, null, []);
        }

        var windows = RealTimeFactor.PerWindow(run.Compute, run.AudioDuration, run.Settings.Window, run.Settings.Step);
        return new RealTimeFactorReport(
            RealTimeFactor.Compute(run.Compute.Select(c => c.ComputeTime), run.AudioDuration),
            Percentile.NearestRank(windows.Select(w => w.Value), 95),
            windows.Max(w => w.Value),
            windows);
    }

    private static LatencyReport EvaluateLatency(QualityRun run)
    {
        var settings = run.Settings;
        var utterances = run.Utterances;
        var session = run.AudioDuration > TimeSpan.Zero
            ? run.AudioDuration
            : utterances.Select(u => u.SpeechStart).DefaultIfEmpty().Max();

        var firstPartial = utterances
            .Select(u => new LatencySample(u.SpeechStart, Latency.FirstPartial(u)))
            .ToArray();
        var @fixed = utterances
            .Select(u => new LatencySample(u.SpeechStart, Latency.Fixed(u, settings.PauseThreshold, settings.MaxUtterance)))
            .ToArray();

        var firstPartialWindows = Latency.PerWindow(firstPartial, session, settings.Window, settings.Step);
        var fixedWindows = Latency.PerWindow(@fixed, session, settings.Window, settings.Step);

        return new LatencyReport(
            Latency.Summarize(firstPartial.Select(s => s.Latency)),
            Latency.Summarize(@fixed.Select(s => s.Latency)),
            WorstP95(firstPartialWindows),
            WorstP95(fixedWindows),
            firstPartialWindows,
            fixedWindows);
    }

    private static WordErrorReport EvaluateWordErrors(IReadOnlyList<ReferenceSentence> sentences)
    {
        var perSentence = sentences
            .Select(s => new SentenceWordErrors(s.Id, WordErrorRate.Compute(s.Reference, s.Hypothesis)))
            .ToArray();
        var total = perSentence.Aggregate(WordErrorCounts.Zero, (sum, s) => sum + s.Counts);
        return new WordErrorReport(total, perSentence);
    }

    private static IReadOnlyList<CategoryErrors> EvaluateTerms(IReadOnlyList<ReferenceSentence> sentences)
    {
        var perSentence = sentences.SelectMany(s => TermErrors.Evaluate(s.Hypothesis, s.Terms)).ToArray();
        return
        [
            .. Enum.GetValues<TermCategory>().Select(category => new CategoryErrors(
                category,
                perSentence.Where(e => e.Category == category).Sum(e => e.Expected),
                perSentence.Where(e => e.Category == category).Sum(e => e.Errors))),
        ];
    }

    private static TimeSpan? WorstP95(IReadOnlyList<WindowedLatency> windows) =>
        windows.Select(w => w.Summary.P95).Where(p => p is not null).Max();
}
