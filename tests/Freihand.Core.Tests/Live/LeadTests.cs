using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>
/// Vorlauf (P11 S3, Auswertung S2 vom 18.11.2026): Eine Äusserung nimmt <see cref="LiveOptions.Lead"/> Ton vor dem erkannten
/// Sprachbeginn mit, nie vor der letzten Grenze; der Vorlauf zählt für den Filter als Sprache.
/// </summary>
public sealed class LeadTests
{
    private static LiveOptions WithLead(double seconds) => Options(LiveMode.Sentence) with { Lead = Sec(seconds) };

    // „Zu zwei“ wurde „2.“: der leise Anfang liegt vor dem Sprachbeginn, den Silero meldet
    [Fact]
    public async Task AQuietStartBeforeTheDetectedSpeechIsKeptWithLead()
    {
        RecognizedWord[] spoken = [Word("Zu", 1.7, 1.95), Word("zwei", 2.0, 2.4), Word("Budget", 2.5, 3.0)];

        var withLead = SourceRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((2, 3.2)), WithLead(0.5));
        var without = SourceRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((2, 3.2)), Options(LiveMode.Sentence));

        Assert.Equal(["Zu", "zwei", "Budget"], CompletedWords(await withLead.EndAt(5)));
        Assert.Equal(["zwei", "Budget"], CompletedWords(await without.EndAt(5)));
    }

    // Nachmessung S3 (Vorlesesätze, 18.11.2026): Silero meldet denselben Bereich bei einer späteren Erkennung einige
    // Millisekunden später. Der Vorlauf muss trotzdem als Sprache zählen, sonst entfiel gerade das erste Wort („Bitte“)
    [Fact]
    public async Task TheLeadCountsAsSpeechEvenWhenTheDetectorLaterMovesTheSpeechStart()
    {
        RecognizedWord[] spoken = [Word("Bitte", 1.6, 2.1), Word("prüfe", 2.2, 2.6), Word("das", 2.7, 3.0)];
        var detector = new MovingStart(early: Sec(2.0), late: Sec(2.06), end: Sec(3.2), movesAfter: Sec(2.5));

        var run = SourceRun.Live(FakeHypothesisDecoder.ByInterval(spoken), detector, WithLead(0.5));

        Assert.Equal(["Bitte", "prüfe", "das"], CompletedWords(await run.EndAt(5)));
    }

    /// <summary>Ein Sprachbereich, dessen Beginn ab einer bestimmten Audiolänge etwas später gemeldet wird.</summary>
    private sealed class MovingStart(TimeSpan early, TimeSpan late, TimeSpan end, TimeSpan movesAfter) : ISpeechDetector
    {
        public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset)
        {
            var until = offset + TimeSpan.FromTicks(audio.Length * TestAudio.TicksPerSample);
            var start = until > movesAfter ? late : early;
            var region = new SpeechRegion(start > offset ? start : offset, end < until ? end : until);
            return region.Start < region.End ? [region] : [];
        }
    }

    // Nach einer Pause beginnt der Auftrag 0,5 s vor der Sprache, aber nie vor der letzten Grenze
    [Fact]
    public async Task TheLeadNeverReachesBackBeyondThePreviousBoundary()
    {
        RecognizedWord[] spoken = [Word("eins", 1.0, 1.8), Word("zwei", 2.7, 3.5)];
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((1, 2), (2.7, 4)), WithLead(0.5));

        Assert.Equal(["eins", "zwei"], CompletedWords(await run.EndAt(6)));
        var starts = decoder.Requests.Select(request => request.Start).Distinct().ToList();
        Assert.Equal(Sec(0.5), starts[0]);
        Assert.Equal(Sec(2.2), starts[1]);
    }

    // Am Längenschnitt ist der Schnitt selbst die letzte Grenze: kein Vorlauf, also kein doppelt erkanntes Wort
    [Fact]
    public async Task ThereIsNoLeadAtALengthCut()
    {
        var spoken = Words(1, 0.5, [.. Enumerable.Range(0, 30).Select(k => $"w{k}")]);
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((1, 16)), WithLead(0.5));

        var words = CompletedWords(await run.EndAt(18));

        Assert.Equal(spoken.Select(word => word.Text), words);
        // Übernommen bis zum letzten Wort, das mindestens CutGuard vor der Grenze 10,5 s endet (10,0 s); dort beginnt die
        // nächste Äusserung ohne Vorlauf (mit Vorlauf wäre es 9,5 s)
        var starts = decoder.Requests.Select(request => request.Start).Distinct().Order().ToList();
        Assert.Equal([Sec(0.5), Sec(10)], starts.Take(2));
    }
}
