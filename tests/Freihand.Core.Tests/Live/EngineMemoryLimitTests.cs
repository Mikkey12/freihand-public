using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.Fakes.FakeRecognitionEngine;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>
/// T69: Speichergrenze von <see cref="EngineTranscription"/> (Kapitel 3, Speichergrenze; Prüfung der Umsetzung, U04).
/// Ergänzt die Gegenprobe U04 um die Läufe, die der Bericht nach der Korrektur verlangt.
/// </summary>
public sealed class EngineMemoryLimitTests
{
    private const int WindowSamples = 30 * TestAudio.SampleRate;

    [Fact]
    public async Task T69_DetectorWindowStaysWithinThirtySecondsForAnHour()
    {
        // Durchgehende Sprache über 3600 s; die Quelle bestätigt fortlaufend, darum kein Rückstand
        var detector = new RecordingDetector(new SpeechEverywhere());
        var engine = new FakeRecognitionEngine();
        for (var t = 5; t <= 3600; t += 5)
        {
            engine.Schedule(t, Completion(t));
        }

        var source = new EngineTranscription(engine, detector, DefaultRecognition, Sec(0.6));
        var run = new SourceRun(source);

        // Wie eine laufende Aufnahme in Schritten von 10 s, jeweils bis die Quelle bestätigt hat; auf einmal gespeist
        // stiesse eine verzögerte Attrappe zu Recht an die Grenze von 120 s
        for (var t = 10; t <= 3600; t += 10)
        {
            await run.FeedUntil(t);
            await Until(() => source.Backlog <= Sec(10), $"Zusage bis {t} s verarbeitet");
        }

        var events = await run.EndAt(3600, timeoutSeconds: 600);

        Assert.True(detector.Calls > 0);
        Assert.True(detector.MaximumSamples <= WindowSamples, $"Grösster Detektoraufruf: {detector.MaximumSamples} Samples.");
        Assert.Empty(Notices(events, LiveNoticeKind.BufferFull));

        // Kein künstlicher Schnitt: eine einzige Äusserung, abgeschlossen erst am Streamende
        var utterance = Assert.Single(Completed(events));
        Assert.Equal(UtteranceEnd.StreamEnd, utterance.Ending);
    }

    [Fact]
    public async Task T69_LateSegmentOverABoundaryOutsideTheWindowIsAssignedByWord()
    {
        // Pause bei 5 s; bei 45 s liegt diese Grenze längst ausserhalb des Detektorfensters von 30 s
        var detector = new RecordingDetector(FakeSpeechDetector.Speech((0, 5), (6, 50)));
        var engine = new FakeRecognitionEngine();
        var source = new EngineTranscription(engine, detector, DefaultRecognition, Sec(0.6));
        var run = new SourceRun(source);

        await run.FeedUntil(45);
        engine.Push(Fixed(Segment("eins zwei drei vier", 3, 7)));
        await UntilProcessed(source, 1);

        // Vor der Zusage nichts fest
        Assert.Empty(Completed(run.Events.Items));

        engine.Push(Completion(7));
        await run.Events.WaitFor(e => e is UtteranceCompleted, "Abschluss nach der Zusage");
        var events = await run.EndAt(51);

        // Wortweise auf beide Äusserungen verteilt: [3, 4) [4, 5) vor der Grenze, [5, 6) [6, 7) danach
        var utterances = Completed(events);
        Assert.Equal(2, utterances.Count);
        Assert.Equal(["eins", "zwei"], utterances[0].Words.Select(word => word.Text));
        Assert.Equal((Sec(5), UtteranceEnd.Pause), (utterances[0].End, utterances[0].Ending));
        Assert.Equal(["drei", "vier"], utterances[1].Words.Select(word => word.Text));
        Assert.True(detector.MaximumSamples <= WindowSamples, $"Grösster Detektoraufruf: {detector.MaximumSamples} Samples.");
    }

    // Geändert nach R01 (zweite Nachprüfung): Früher erwartete dieser Test, dass die Annahme nach einer späten Zusage
    // weiterläuft. Das ist mit einer Quelle ohne CompleteUntil nicht vereinbar: Sie wird nur mit dem Streamende fertig,
    // und das zeigt eine Audiofolge nur beim Lesen. Nach ER-05 („Puffer 120 s, dann Aufnahmestopp“) stoppt P3 die
    // Aufnahme darum selbst und schliesst regulär ab (Kapitel 3, Speichergrenze).
    [Fact]
    public async Task T69_WithoutProgressAcceptanceStopsAtTheLimitAndEndsRegularly()
    {
        // Die Quelle bestätigt nichts: bei 120 s Rückstand BufferFull, keine Annahme darüber hinaus, Aufnahmestopp
        var engine = new FakeRecognitionEngine();
        var source = new EngineTranscription(engine, FakeSpeechDetector.Speech((0, 200)), DefaultRecognition, Sec(0.6));
        var run = new SourceRun(source);

        run.Feed.ReleaseUntil(Sec(130));
        var events = await run.Events.FinishOk(60);

        Assert.Equal(Sec(120), source.AcceptedEnd);
        Assert.Equal(Sec(120), engine.ReceivedEnd);
        Assert.True(
            events.ToList().IndexOf(Notices(events, LiveNoticeKind.BacklogWarning)[0])
            < events.ToList().IndexOf(Notices(events, LiveNoticeKind.BufferFull)[0]));
        Assert.Equal((Sec(120), UtteranceEnd.StreamEnd), (Assert.Single(Completed(events)).End, Completed(events)[0].Ending));
    }

    /// <summary>Meldet das ganze übergebene Audio als Sprache.</summary>
    private sealed class SpeechEverywhere : ISpeechDetector
    {
        public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset) =>
            [new SpeechRegion(offset, offset + TimeSpan.FromTicks(audio.Length * TestAudio.TicksPerSample))];
    }

    /// <summary>Protokolliert die Grösse jedes Detektoraufrufs.</summary>
    private sealed class RecordingDetector(ISpeechDetector inner) : ISpeechDetector
    {
        private int _maximum;
        private int _calls;

        public int MaximumSamples => Volatile.Read(ref _maximum);

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset)
        {
            Interlocked.Increment(ref _calls);
            if (audio.Length > _maximum)
            {
                Volatile.Write(ref _maximum, audio.Length);
            }

            return inner.Detect(audio, offset);
        }
    }
}
