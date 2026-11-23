using Whisper.net;

namespace Freihand.Benchmark;

/// <summary>Einstellungen der Live-Simulation.</summary>
/// <param name="StepSeconds">Audio, das zwischen zwei Durchläufen mindestens hinzukommt.</param>
/// <param name="WindowSeconds">Höchste Länge des noch nicht festen Audios, danach wird fest geschrieben.</param>
/// <param name="GuardSeconds">Abstand zum Audioende, innerhalb dessen nichts fest wird.</param>
internal sealed record SimulationOptions(double StepSeconds, double WindowSeconds, double GuardSeconds);

/// <param name="DecodeSeconds">Verstrichene Zeit der Erkennung, samt einer allfälligen Schlafzeit.</param>
/// <param name="LastSegmentEndSeconds">Spätestes Ende der gelieferten Abschnitte im Audio; leer ohne Ergebnis.</param>
/// <param name="SuspendedSeconds">Davon Standby oder Ruhezustand, leer wenn nicht messbar; ein Lauf mit Schlafzeit wird wiederholt.</param>
internal sealed record StepRecord(
    double AudioSeconds,
    double StartSeconds,
    double FinishSeconds,
    double DecodeSeconds,
    int Segments,
    double? LastSegmentEndSeconds,
    double? SuspendedSeconds);

/// <param name="FixedAtSeconds">Simulierte Zeit, zu der der Abschnitt fest wurde.</param>
/// <param name="AtEnd">Erst beim Diktatende fest geschrieben; zählt nicht zur Live-Latenz.</param>
/// <param name="LengthCut">Wegen der Höchstlänge geschnitten statt an einer Pause (nur satzweise).</param>
internal sealed record FixedSegment(string Text, double StartSeconds, double EndSeconds, double FixedAtSeconds, bool AtEnd, bool LengthCut = false);

/// <param name="SegmentationSeconds">Rechenzeit der vorab bestimmten Sprachbereiche (nur satzweise).</param>
/// <param name="SegmentationSuspendedSeconds">Davon Standby oder Ruhezustand, leer wenn nicht messbar.</param>
internal sealed record SimulationResult(
    double AudioSeconds,
    IReadOnlyList<StepRecord> Steps,
    IReadOnlyList<FixedSegment> Fixed,
    double? FirstPartialAtSeconds,
    double SegmentationSeconds = 0,
    double? SegmentationSuspendedSeconds = 0);

/// <summary>
/// Rechnet nach, wie whisper.cpp live arbeiten würde: Alle <see cref="SimulationOptions.StepSeconds"/> wird das
/// noch nicht feste Audio neu erkannt; liegt die Erkennung zurück, beginnt der nächste Durchlauf erst nach dem
/// vorherigen und nimmt alles bis dahin angefallene Audio mit. Fest wird ein Abschnitt, wenn zwei
/// aufeinanderfolgende Durchläufe ihn gleich liefern (vereinfachtes LocalAgreement; die Umsetzung folgt in P3).
/// </summary>
internal sealed class LiveSimulation(WhisperProcessor processor, SimulationOptions options)
{
    private sealed record Hypothesis(string Text, double Start, double End);

    public async Task<SimulationResult> RunAsync(float[] audio, CancellationToken cancellationToken = default)
    {
        var total = audio.Length / (double)WavFile.SampleRate;
        var steps = new List<StepRecord>();
        var fixedSegments = new List<FixedSegment>();
        List<Hypothesis>? previous = null;
        List<Hypothesis> pending = [];
        double? firstPartialAt = null;
        double trim = 0, audioPos = 0, clock = 0;

        while (audioPos < total)
        {
            audioPos = Math.Min(total, Math.Max(audioPos + options.StepSeconds, clock));
            var start = Math.Max(clock, audioPos);
            var from = (int)(trim * WavFile.SampleRate);
            var to = (int)(audioPos * WavFile.SampleRate);

            var timer = StepTimer.Start();
            var hypothesis = new List<Hypothesis>();
            await foreach (var segment in processor.ProcessAsync(new ReadOnlyMemory<float>(audio, from, to - from), cancellationToken))
            {
                var text = segment.Text.Trim();
                if (text.Length > 0)
                {
                    hypothesis.Add(new Hypothesis(text, trim + segment.Start.TotalSeconds, trim + segment.End.TotalSeconds));
                }
            }

            var (elapsed, suspended) = timer.Stop();
            clock = start + elapsed;
            steps.Add(new StepRecord(audioPos, start, clock, elapsed, hypothesis.Count, hypothesis.Count > 0 ? hypothesis.Max(h => h.End) : null, suspended));
            if (firstPartialAt is null && hypothesis.Count > 0)
            {
                firstPartialAt = clock;
            }

            var agreed = 0;
            if (previous is not null)
            {
                while (agreed < hypothesis.Count
                    && agreed < previous.Count
                    && string.Equals(hypothesis[agreed].Text, previous[agreed].Text, StringComparison.OrdinalIgnoreCase)
                    && hypothesis[agreed].End <= audioPos - options.GuardSeconds)
                {
                    agreed++;
                }
            }

            var windowFull = audioPos - trim > options.WindowSeconds;
            if (windowFull && agreed < hypothesis.Count - 1)
            {
                agreed = hypothesis.Count - 1;
            }

            if (agreed > 0)
            {
                for (var i = 0; i < agreed; i++)
                {
                    var h = hypothesis[i];
                    fixedSegments.Add(new FixedSegment(h.Text, h.Start, h.End, clock, AtEnd: false));
                }

                trim = Math.Max(trim, hypothesis[agreed - 1].End);
                previous = null;
            }
            else if (windowFull && hypothesis.Count == 0)
            {
                // Lange Stille: das leere Audio nicht immer wieder neu erkennen
                trim = Math.Max(trim, audioPos - options.GuardSeconds);
                previous = null;
            }
            else
            {
                previous = hypothesis;
            }

            pending = hypothesis[agreed..];
        }

        foreach (var h in pending)
        {
            fixedSegments.Add(new FixedSegment(h.Text, h.Start, h.End, clock, AtEnd: true));
        }

        return new SimulationResult(total, steps, fixedSegments, firstPartialAt);
    }
}
