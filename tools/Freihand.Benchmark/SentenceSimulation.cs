using Whisper.net;

namespace Freihand.Benchmark;

/// <param name="PauseSeconds">Stille, nach der ein Satz als beendet gilt (ER-15).</param>
/// <param name="MaxSegmentSeconds">Höchste Länge eines Satzes; längere werden geteilt und als Längenschnitt markiert.</param>
internal sealed record SentenceOptions(double PauseSeconds, double MaxSegmentSeconds);

/// <summary>Liefert die Sprachbereiche einer ganzen Aufnahme, in Sekunden und aufsteigend.</summary>
internal interface ISpeechSegmenter
{
    string Name { get; }

    IReadOnlyList<(double Start, double End)> Detect(float[] audio);
}

/// <summary>Silero VAD aus Whisper.net (ER-16).</summary>
internal sealed class SileroSegmenter(WhisperVadProcessor vad) : ISpeechSegmenter
{
    public string Name => "silero-vad";

    public IReadOnlyList<(double Start, double End)> Detect(float[] audio) =>
        [.. vad.DetectSpeech(audio).Select(s => (s.Start.TotalSeconds, s.End.TotalSeconds))];
}

/// <summary>
/// Lautstärke in 20-ms-Fenstern, Schwelle 12 dB über dem Grundrauschen; ein Bereich endet nach der Pause und zählt
/// ab 0,2 s Länge.
/// </summary>
internal sealed class EnergySegmenter(double pauseSeconds) : ISpeechSegmenter
{
    public string Name => "lautstaerke";

    public IReadOnlyList<(double Start, double End)> Detect(float[] audio)
    {
        const int frame = WavFile.SampleRate / 50;
        const double frameSeconds = frame / (double)WavFile.SampleRate;
        var levels = new double[audio.Length / frame];
        for (var f = 0; f < levels.Length; f++)
        {
            double sum = 0;
            for (var i = f * frame; i < (f + 1) * frame; i++)
            {
                sum += audio[i] * audio[i];
            }

            levels[f] = Math.Sqrt(sum / frame);
        }

        var regions = new List<(double, double)>();
        if (levels.Length == 0)
        {
            return regions;
        }

        var threshold = Math.Max(levels.Order().ElementAt(levels.Length / 10) * 4, 0.01);
        var pauseFrames = (int)Math.Ceiling(pauseSeconds / frameSeconds);
        int? start = null;
        var lastLoud = 0;
        for (var f = 0; f < levels.Length; f++)
        {
            if (levels[f] > threshold)
            {
                start ??= f;
                lastLoud = f;
            }
            else if (start is { } s && f - lastLoud >= pauseFrames)
            {
                if ((lastLoud - s + 1) * frameSeconds >= 0.2)
                {
                    regions.Add((Math.Max(0, (s - 10) * frameSeconds), (lastLoud + 1 + 10) * frameSeconds));
                }

                start = null;
            }
        }

        if (start is { } open && (lastLoud - open + 1) * frameSeconds >= 0.2)
        {
            regions.Add((Math.Max(0, (open - 10) * frameSeconds), Math.Min(levels.Length, lastLoud + 1 + 10) * frameSeconds));
        }

        return regions;
    }
}

/// <summary>
/// Satzweiser Modus (ER-19): Jeder Satz wird einmal erkannt, sobald nach ihm die Pause abgelaufen ist; es gibt keine
/// Teilergebnisse und keine Neuberechnung. Sprachbereiche mit kürzerem Abstand als die Pause gehören zum selben Satz,
/// solange er nicht länger als <see cref="SentenceOptions.MaxSegmentSeconds"/> wird. Die Zeit wird wie in
/// <see cref="LiveSimulation"/> nachgerechnet: Liegt die Erkennung zurück, wartet der nächste Satz.
/// </summary>
/// <remarks>
/// Die Sprachbereiche werden vorab über die ganze Aufnahme bestimmt; ihre Rechenzeit steht getrennt in
/// <see cref="SimulationResult.SegmentationSeconds"/> und ist nicht in den Decode-Schritten enthalten.
/// </remarks>
internal sealed class SentenceSimulation
{
    // Halbes Sample: Zeiten in Sekunden sind Samplezahlen; ein Vergleich darf nicht an der Rundung hängen
    private const double Tolerance = 0.5 / WavFile.SampleRate;

    private readonly ITranscriber _transcriber;
    private readonly SentenceOptions _options;
    private readonly ISpeechSegmenter _segmenter;

    public SentenceSimulation(ITranscriber transcriber, SentenceOptions options, ISpeechSegmenter segmenter)
    {
        // Kürzer als ein Sample käme der Iterator nicht voran
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PauseSeconds, 1.0 / WavFile.SampleRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSegmentSeconds, 1.0 / WavFile.SampleRate);
        _transcriber = transcriber;
        _options = options;
        _segmenter = segmenter;
    }

    public string Segmenter => _segmenter.Name;

    public async Task<SimulationResult> RunAsync(float[] audio, CancellationToken cancellationToken = default)
    {
        var total = audio.Length / (double)WavFile.SampleRate;
        var steps = new List<StepRecord>();
        var fixedSegments = new List<FixedSegment>();
        double clock = 0;
        double? firstAt = null;

        var segmentation = StepTimer.Start();
        var regions = _segmenter.Detect(audio);
        var (segmentationSeconds, segmentationSuspended) = segmentation.Stop();

        foreach (var sentence in Sentences(regions, total))
        {
            var begin = Math.Max(clock, sentence.Available);
            var from = (int)(sentence.Start * WavFile.SampleRate);
            var to = Math.Min(audio.Length, (int)(sentence.End * WavFile.SampleRate));

            var timer = StepTimer.Start();
            var texts = await _transcriber.TranscribeAsync(new ReadOnlyMemory<float>(audio, from, to - from), cancellationToken);
            var (elapsed, suspended) = timer.Stop();
            clock = begin + elapsed;
            steps.Add(new StepRecord(sentence.Available, begin, clock, elapsed, texts.Count, texts.Count > 0 ? sentence.End : null, suspended));
            if (texts.Count > 0)
            {
                firstAt ??= clock;
                fixedSegments.Add(new FixedSegment(string.Join(" ", texts), sentence.Start, sentence.End, clock, AtEnd: false, sentence.LengthCut));
            }
        }

        return new SimulationResult(total, steps, fixedSegments, firstAt, segmentationSeconds, segmentationSuspended);
    }

    /// <summary>
    /// Fasst Sprachbereiche zu Sätzen zusammen, so wie ein laufendes System entscheiden müsste: jede Entscheidung fällt
    /// zu ihrer Zeit und nur aus dem bis dahin bekannten Audio. Ein Satz endet, sobald nach ihm die Pause bestätigt ist,
    /// verfügbar nach der Pause, am Ende der Aufnahme sofort. Erreicht er vorher die Höchstlänge, endet er in diesem
    /// Moment: am Ende des letzten schon abgeschlossenen Bereichs, sonst starr an der Höchstlänge. Ein Bereich, der
    /// genau an der Höchstlänge endet, gilt dort noch als laufend. Beides ist ein Längenschnitt, verfügbar an der
    /// Höchstlänge.
    /// </summary>
    internal IEnumerable<Sentence> Sentences(IReadOnlyList<(double Start, double End)> regions, double total)
    {
        var pause = _options.PauseSeconds;
        var max = _options.MaxSegmentSeconds;
        double? start = null;    // Beginn des offenen Satzes
        double? lastEnd = null;  // Ende des letzten abgeschlossenen Bereichs im offenen Satz
        foreach (var (regionStart, regionEnd) in regions)
        {
            var from = regionStart;
            if (start is { } s && lastEnd is { } end)
            {
                var limit = s + max;
                if (from - end >= pause - Tolerance && end + pause <= limit + Tolerance)
                {
                    yield return new Sentence(s, end, end + pause, LengthCut: false);
                    start = null;
                }
                else if (limit < from - Tolerance)
                {
                    // Höchstlänge in der Stille erreicht, bevor die Pause bestätigt war
                    yield return new Sentence(s, end, limit, LengthCut: true);
                    start = null;
                }
            }

            if (start is null)
            {
                start = from;
                lastEnd = null;
            }

            while (regionEnd >= start.Value + max - Tolerance)
            {
                var limit = start.Value + max;
                if (lastEnd is { } gapStart && gapStart < from)
                {
                    // Schnitt an der letzten kurzen Pause vor diesem Bereich
                    yield return new Sentence(start.Value, gapStart, limit, LengthCut: true);
                    start = from;
                }
                else
                {
                    yield return new Sentence(start.Value, limit, limit, LengthCut: true);
                    start = limit;
                    from = limit;
                }

                lastEnd = null;
            }

            if (regionEnd - start.Value <= Tolerance)
            {
                // Nichts mehr übrig: der Bereich endete genau am Schnitt
                start = null;
                lastEnd = null;
                continue;
            }

            lastEnd = lastEnd is { } previous ? Math.Max(previous, regionEnd) : regionEnd;
        }

        if (start is { } open && lastEnd is { } last)
        {
            var limit = open + max;
            var pauseAt = Math.Min(total, last + pause);
            yield return limit < pauseAt - Tolerance
                ? new Sentence(open, last, limit, LengthCut: true)
                : new Sentence(open, last, pauseAt, LengthCut: false);
        }
    }

    /// <param name="Available">Simulierte Zeit, ab der der Satz erkannt werden kann.</param>
    internal sealed record Sentence(double Start, double End, double Available, bool LengthCut);
}

/// <summary>Einstellungen von Silero VAD, wie sie im Ergebnis stehen.</summary>
internal sealed record VadOptions(double MinSilenceSeconds, double MaxSpeechSeconds, double MinSpeechSeconds);
