using System.Runtime.CompilerServices;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>
/// Eigene Regressionstests aus der systematischen Suche nach Geschwistern von N01–N04 (Nachprüfung der Umsetzung):
/// Zeiten zwischen zwei Samples, Abbruch- und Stoppfade, Audiolücken.
/// </summary>
public sealed class SweepRegressionTests
{
    // Geschwister von N03 im satzweisen Modus: Nach einem Schnitt zwischen zwei Samples bleibt bis C kein ganzes Sample;
    // der Längenschnitt muss trotzdem vorankommen, nicht erst beim Streamende
    [Fact]
    public async Task S01_SentenceLengthCutAdvancesWithoutAWholeSampleLeft()
    {
        var options = Options(LiveMode.Sentence) with { MaxUtterance = TimeSpan.FromTicks(1000), CutGuard = TimeSpan.FromTicks(500) };
        using var stop = new CancellationTokenSource();
        var run = SourceRun.Live(FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech((0, 5)), options, cancellationToken: stop.Token);
        try
        {
            await run.FeedUntil(0.01);
            await Until(() => run.Source.Backlog <= Sec(0.001), "Längenschnitte ohne Streamende abgearbeitet");
        }
        finally
        {
            await stop.CancelAsync();
            await run.Events.Finish();
        }
    }

    // Kapitel 5.1, Regel 3: Nach einem Längenschnitt zwischen zwei Samples beginnt die nächste Äusserung genau am Schnitt
    [Fact]
    public async Task S02_NextUtteranceBeginsExactlyAtAnUnalignedCut()
    {
        RecognizedWord[] spoken = [Word("eins", 0, 4.00001), Word("zwei", 4.00001, 9.00001), Word("drei", 9.00001, 11)];
        var run = SourceRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((0, 12)), Options(LiveMode.Sentence));

        // Die Äusserung ist beim Schnitt noch offen: kein Streamende, bevor der Längenschnitt verarbeitet ist
        await run.FeedUntil(10.5);
        await run.Events.WaitFor(e => e is UtteranceCompleted { Utterance.Ending: UtteranceEnd.LengthCut }, "Längenschnitt");
        var events = await run.EndAt(12);

        var utterances = Completed(events);
        Assert.Equal((Sec(9.00001), UtteranceEnd.LengthCut), (utterances[0].End, utterances[0].Ending));
        Assert.Equal(utterances[0].End, utterances[1].Start);
        Assert.Equal(["eins", "zwei", "drei"], CompletedWords(events));
    }

    // Abbruch- und Stoppfad Detektorfehler: Eine Audioquelle, deren Freigabe hängt, hält RunAsync nicht auf (N01)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S03_DetectorFailureDoesNotWaitForABlockingAudioRelease(bool engine)
    {
        var detector = FakeSpeechDetector.Speech((0, 5));
        detector.FailAfter = Sec(0.2);
        ITranscriptSource source = engine
            ? new EngineTranscription(new FakeRecognitionEngine(), detector, DefaultRecognition, Sec(0.6))
            : new LiveTranscription(FakeHypothesisDecoder.Silent(), detector, DefaultRecognition, Options(LiveMode.Live));
        var audio = new SlowReleaseAudio();
        var events = new Recorder<TranscriptEvent>(() => source.RunAsync(audio.ReadAsync()));
        try
        {
            var ended = await Task.WhenAny(events.Completion, Task.Delay(TimeSpan.FromSeconds(5))) == events.Completion;
            Assert.True(ended, "RunAsync wartet auf die Freigabe der Audioquelle.");
            Assert.IsType<InvalidOperationException>(await events.Finish());
        }
        finally
        {
            audio.Release.TrySetResult();
        }

        await Until(() => audio.Disposed == 1, "Audioquelle genau einmal freigegeben");
    }

    // Geschwister von N02, geändert nach R01 (zweite Nachprüfung): Erreicht der Rückstand der Engine die Grenze mitten
    // in einem grossen Block, wird der Rest nicht angenommen, sondern mit seiner Zeitspanne gemeldet; P3 stoppt die
    // Aufnahme selbst und schliesst regulär ab, ohne auf einen Stopp der Quelle oder eine Zusage zu warten
    [Fact]
    public async Task S04_EngineAtLimitRefusesTheRestOfABlockAndEndsRegularly()
    {
        var source = new EngineTranscription(new FakeRecognitionEngine(), FakeSpeechDetector.Speech((0, 121)), DefaultRecognition, Sec(0.6));
        var run = new SourceRun(source);
        run.Feed.Enqueue(TestAudio.Chunk(Sec(0), Sec(120.5)));

        var events = await run.Events.FinishOk(60);

        Assert.Equal(Sec(120), source.AcceptedEnd);
        Assert.Contains(Notices(events, LiveNoticeKind.BufferFull), notice => notice.Start == Sec(120) && notice.End == Sec(120.5));
        Assert.Equal(UtteranceEnd.StreamEnd, Assert.Single(Completed(events)).Ending);
    }

    // Lücken: Die Rückstandsmeldung beginnt am ältesten gehaltenen Audio, nicht mitten in einer Lücke
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S05_BacklogNoticeAfterAGapStartsAtTheOldestHeldAudio(bool engine)
    {
        var decoder = FakeHypothesisDecoder.Silent();
        var gate = new TestGate();
        decoder.Hold = _ => gate.Task;
        var detector = FakeSpeechDetector.Speech((0, 5), (100, 120));
        ITranscriptSource source = engine
            ? new EngineTranscription(new FakeRecognitionEngine(), detector, DefaultRecognition, Sec(0.6))
            : new LiveTranscription(decoder, detector, DefaultRecognition, Options(LiveMode.Sentence));
        using var stop = new CancellationTokenSource();
        var run = new SourceRun(source, stop.Token);
        try
        {
            // Die Engine warnt nach gehaltenem Audio (unverändert); LiveTranscription nach Verarbeitungsrückstand (E2):
            // [0, 5) wartet hängend auf seinen Decode, fällig seit seiner Grenze bei 5 s; danach zählt nur Audio, die
            // Lücke nicht, also 10 s ab 100 s
            var warnAt = engine ? 105 : 110;
            await run.FeedUntil(5);
            run.Feed.SkipTo(Sec(100));
            await run.FeedUntil(warnAt + 1);
            var warning = (LiveNotice)await run.Events.WaitFor(
                e => e is LiveNotice { Kind: LiveNoticeKind.BacklogWarning }, "Warnung ab 10 s");
            // Die Meldung beginnt beim ältesten gehaltenen Audio; die Lücke zählt nicht
            Assert.Equal((Sec(0), Sec(warnAt)), (warning.Start, warning.End));
            Assert.Equal(Sec(5 + (warnAt + 1 - 100)), source.Backlog);
        }
        finally
        {
            gate.Open();
            await stop.CancelAsync();
            await run.Events.Finish();
        }
    }

    /// <summary>Audio bis 0,3 s; danach wartet die Quelle, und ihre Freigabe hängt, bis der Test sie löst.</summary>
    private sealed class SlowReleaseAudio
    {
        private int _disposed;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Disposed => Volatile.Read(ref _disposed);

        public async IAsyncEnumerable<AudioChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    yield return TestAudio.Chunk(Sec(i * 0.1), Sec((i + 1) * 0.1));
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Interlocked.Increment(ref _disposed);
                await Release.Task;
            }
        }
    }
}
