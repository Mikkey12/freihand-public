using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live.Fakes;

/// <summary>Prüft die Attrappen und Hilfen der Abnahmetests selbst, damit ein roter Abnahmetest an P3 liegt.</summary>
public sealed class TestKitTests
{
    [Fact]
    public async Task FeedDeliversContiguousBlocksOnlyAsReleased()
    {
        var feed = new AudioFeed();
        feed.ReleaseUntil(Sec(0.25));
        var chunks = new List<AudioChunk>();
        await using var reader = feed.ReadAllAsync().GetAsyncEnumerator();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(await reader.MoveNextAsync());
            chunks.Add(reader.Current);
        }

        // Nicht freigegebenes Audio kommt nicht
        var next = reader.MoveNextAsync().AsTask();
        await Task.Delay(50);
        Assert.False(next.IsCompleted);

        feed.ReleaseUntil(Sec(0.3));
        Assert.True(await next);
        chunks.Add(reader.Current);
        feed.Complete();
        Assert.False(await reader.MoveNextAsync());

        Assert.Equal([Sec(0), Sec(0.1), Sec(0.2), Sec(0.25)], chunks.Select(chunk => chunk.Offset));
        Assert.Equal([3200, 3200, 1600, 1600], chunks.Select(chunk => chunk.Pcm16Mono.Length));
        Assert.All(chunks, chunk => Assert.Equal(TestAudio.SampleRate, chunk.SampleRate));
        var samples = chunks[1].Pcm16Mono.ToArray();
        Assert.Equal(TestAudio.SampleAt(1600), BitConverter.ToInt16(samples, 0));
    }

    [Fact]
    public async Task FeedStopDropsWhatWasNotPulled()
    {
        var feed = new AudioFeed();
        feed.ReleaseUntil(Sec(1));
        await using var reader = feed.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());

        feed.Stop();

        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task FeedRewindAndSkipProduceOverlapAndGap()
    {
        var feed = new AudioFeed();
        feed.ReleaseUntil(Sec(0.2));
        feed.Rewind(Sec(0.1));
        feed.ReleaseUntil(Sec(0.2));
        feed.SkipTo(Sec(0.5));
        feed.ReleaseUntil(Sec(0.6));
        feed.Complete();

        var offsets = new List<TimeSpan>();
        await foreach (var chunk in feed.ReadAllAsync())
        {
            offsets.Add(chunk.Offset);
        }

        Assert.Equal([Sec(0), Sec(0.1), Sec(0.1), Sec(0.5)], offsets);
    }

    [Fact]
    public void HeardFollowsTheIntervalRules()
    {
        RecognizedWord[] spoken =
        [
            Word("davor", 0.0, 1.0), Word("halb", 1.0, 2.0), Word("drin", 2.0, 3.0), Word("angeschnitten", 3.0, 4.0),
        ];

        // Ab 1,5 s: „davor“ fehlt, „halb“ ragt zur Hälfte hinein und wird gekürzt, das angeschnittene Wort fehlt
        var heard = FakeHypothesisDecoder.Heard(spoken, Sec(1.5), Sec(3.5));
        Assert.Equal([Word("halb", 1.5, 2.0), Word("drin", 2.0, 3.0)], heard);

        // Weniger als die Hälfte am Beginn: nicht gehört
        Assert.Equal([Word("drin", 2.0, 3.0)], FakeHypothesisDecoder.Heard(spoken, Sec(1.6), Sec(3.5)));

        // Angeschnitten am Ende nur mit eigenem Wortlaut
        Assert.Equal(
            [Word("drin", 2.0, 3.0), Word("x", 3.0, 3.5)],
            FakeHypothesisDecoder.Heard(spoken, Sec(2.0), Sec(3.5), _ => "x"));
    }

    [Fact]
    public async Task DecoderLogsRequestsChecksAudioAndHolds()
    {
        var decoder = FakeHypothesisDecoder.ByInterval([Word("eins", 0.0, 0.5)]);
        var gate = new TestGate();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        var audio = Floats(Sec(0), Sec(1));

        var first = decoder.DecodeAsync(audio, Sec(0), DefaultRecognition);
        await Task.Delay(50);
        Assert.False(first.IsCompleted);
        gate.Open();
        var hypothesis = await first;

        Assert.Equal((Sec(0), Sec(1)), (hypothesis.AudioStart, hypothesis.AudioEnd));
        Assert.Equal([Word("eins", 0.0, 0.5)], hypothesis.Words);
        var call = Assert.Single(decoder.Calls);
        Assert.True(call.AudioIntact);
        Assert.Equal(16_000, call.Request.SampleCount);

        // Falsch ausgeschnitten: der Speicher passt nicht zur angegebenen Sitzungszeit
        await decoder.DecodeAsync(audio, Sec(0.5), DefaultRecognition);
        Assert.False(decoder.Calls[1].AudioIntact);
    }

    [Fact]
    public async Task DecoderCanIgnoreCancellation()
    {
        var decoder = FakeHypothesisDecoder.Silent();
        var gate = new TestGate();
        decoder.Hold = _ => gate.Task;
        decoder.HonorCancellation = false;
        using var cancellation = new CancellationTokenSource();

        var call = decoder.DecodeAsync(Floats(Sec(0), Sec(0.5)), Sec(0), DefaultRecognition, cancellation.Token);
        await cancellation.CancelAsync();
        gate.Open();

        Assert.Empty((await call).Words);
        Assert.True(decoder.Calls[0].Completed);
    }

    [Fact]
    public void DetectorClipsAndKeepsDuplicates()
    {
        var detector = FakeSpeechDetector.Speech((1, 3), (1, 3), (5, 6));

        var regions = detector.Detect(Floats(Sec(2), Sec(5.5)), Sec(2));

        Assert.Equal(
            [new SpeechRegion(Sec(2), Sec(3)), new SpeechRegion(Sec(2), Sec(3)), new SpeechRegion(Sec(5), Sec(5.5))],
            regions);
    }

    [Fact]
    public async Task EngineDeliversScheduledUpdatesWhenAudioArrives()
    {
        var engine = new FakeRecognitionEngine();
        var update = FakeRecognitionEngine.Fixed(FakeRecognitionEngine.Segment("Hallo", 0, 1), completeUntil: 1);
        engine.Schedule(1.0, update);
        var feed = new AudioFeed();
        var received = new List<RecognitionUpdate>();

        var run = Task.Run(async () =>
        {
            await foreach (var item in engine.RecognizeAsync(feed.ReadAllAsync(), DefaultRecognition))
            {
                received.Add(item);
            }
        });
        feed.ReleaseUntil(Sec(0.9));
        await Until(() => engine.ReceivedEnd == Sec(0.9), "Audio bis 0,9 s erhalten");
        Assert.Equal(0, engine.Pushed);

        feed.ReleaseUntil(Sec(1.0));
        feed.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([update], received);
        Assert.Equal(Sec(1), update.CompleteUntil);
        Assert.Equal(SegmentState.Fixed, update.Fixed[0].State);
    }

    [Fact]
    public async Task EnginePassesOnAnAudioSourceFailure()
    {
        // A02: Ein Fehler der Audioquelle beendet den Erkennungsauftrag mit genau diesem Fehler, ohne Zeitüberschreitung
        var engine = new FakeRecognitionEngine();
        var received = new List<RecognitionUpdate>();

        async Task ReadAsync()
        {
            await foreach (var update in engine.RecognizeAsync(FailingAudio(), DefaultRecognition))
            {
                received.Add(update);
            }
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("Künstlicher Eingabefehler", failure.Message);
        Assert.Empty(received);
        Assert.True(engine.PumpTask!.IsCompleted);
    }

    [Fact]
    public async Task EngineEndsPumpAndEnumeratorOnCancellation()
    {
        var engine = new FakeRecognitionEngine { FinishWithAudio = false };
        var feed = new AudioFeed();
        using var cancellation = new CancellationTokenSource();
        var read = Task.Run(async () =>
        {
            await foreach (var update in engine.RecognizeAsync(feed.ReadAllAsync(), DefaultRecognition, cancellation.Token))
            {
            }
        });
        feed.ReleaseUntil(Sec(0.3));
        await Until(() => engine.ReceivedEnd == Sec(0.3), "Audio bis 0,3 s erhalten");

        await cancellation.CancelAsync();

        // Der Enumerator endet mit dem Abbruch und hat die Pumpe beendet, obwohl die Audioquelle offen bleibt
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(engine.PumpTask!.IsCompleted);
    }

    [Fact]
    public async Task EngineCountsUpdatesTheReceiverIsDoneWith()
    {
        var engine = new FakeRecognitionEngine { FinishWithAudio = false };
        engine.Push(FakeRecognitionEngine.Completion(1.0));
        engine.Push(FakeRecognitionEngine.Completion(2.0));
        var feed = new AudioFeed();
        await using var reader = engine.RecognizeAsync(feed.ReadAllAsync(), DefaultRecognition).GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(0, engine.Consumed);

        // Erst wenn der Empfänger das nächste verlangt, ist das vorherige fertig behandelt
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, engine.Consumed);
        engine.Finish();
        Assert.False(await reader.MoveNextAsync());
        Assert.Equal(2, engine.Consumed);
    }

    [Fact]
    public async Task DecoderCountsOpenCallsUntilTheirTaskEnds()
    {
        var decoder = FakeHypothesisDecoder.Silent();
        var gate = new TestGate();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;

        var held = decoder.DecodeAsync(Floats(Sec(0), Sec(0.5)), Sec(0), DefaultRecognition);
        Assert.Equal(1, decoder.Open);
        await decoder.DecodeAsync(Floats(Sec(0), Sec(0.5)), Sec(0), DefaultRecognition);
        Assert.Equal(1, decoder.Open);

        gate.Open();
        await held;
        await Until(() => decoder.Open == 0, "Aufruf abgeschlossen");
        Assert.True(held.IsCompleted);
    }

    [Fact]
    public async Task FeedReportsWhenTheReaderWaitsForMore()
    {
        var feed = new AudioFeed();
        feed.ReleaseUntil(Sec(0.2));
        await using var reader = feed.ReadAllAsync().GetAsyncEnumerator();
        Assert.False(feed.Drained);

        Assert.True(await reader.MoveNextAsync());
        Assert.True(await reader.MoveNextAsync());
        Assert.False(feed.Drained);

        // Der Leser verlangt den nächsten Block, und keiner liegt bereit
        var next = reader.MoveNextAsync().AsTask();
        Assert.True(feed.Drained);
        feed.ReleaseUntil(Sec(0.3));
        Assert.True(await next);
        Assert.False(feed.Drained);
    }

    [Fact]
    public async Task StepToWaitsForTheExpectedCallItsReturnAndItsProcessing()
    {
        // L01: Annahme und Decoderarbeit auf getrennten Aufgaben; der Aufruf beginnt erst später, das Ergebnis wird erst
        // nach einem zweiten Tor verarbeitet. Jede Stufe ist durch ein Tor gesperrt, die Prüfungen brauchen keine Uhr.
        var decoder = FakeHypothesisDecoder.Silent();
        var returned = new TestGate();
        decoder.Hold = _ => returned.Task;
        var source = new DeferredWorker(decoder);
        var feed = new AudioFeed();
        var events = new Recorder<TranscriptEvent>(() => source.RunAsync(feed.ReadAllAsync()));

        var step = LiveTestKit.StepTo(feed, source, decoder, () => source.Processed, 0.5);
        await Until(() => source.AcceptedEnd == Sec(0.5) && feed.Drained, "Audio angenommen, Leser wartet");
        Assert.Empty(decoder.Calls);
        Assert.False(step.IsCompleted, "Ohne Aufruf darf der Schritt nicht quittiert sein.");

        source.Start.Open();
        await Until(() => decoder.Calls.Count == 1, "Aufruf begonnen");
        Assert.False(step.IsCompleted, "Ein laufender Aufruf ist keine Quittung.");

        returned.Open();
        await Until(() => decoder.Calls[0].Returned, "Aufruf zurückgekehrt");
        Assert.False(step.IsCompleted, "Ein unverarbeitetes Ergebnis ist keine Quittung.");

        source.Process.Open();
        var call = await step;
        Assert.Equal((Sec(0), Sec(0.5)), (call.Request.Start, call.Request.End));
        Assert.Equal(1, source.Processed);
        feed.Complete();
        await events.FinishOk();
    }

    [Fact]
    public async Task LateDeliverySettlesWhenTheReaderTakesEverything()
    {
        // L02, erster erlaubter Ablauf: Die Sitzung liest weiter und erhält alle späten Ereignisse
        var source = new ScriptedTranscriptSource();
        var feed = new AudioFeed();
        await using var reader = source.RunAsync(feed.ReadAllAsync()).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await Until(() => source.Waiting, "Abholen wartet");
        var before = source.Delivered;
        source.Emit(new ProvisionalText(Guid.NewGuid(), "Spät"));
        source.Emit(new LiveNotice(LiveNoticeKind.SpeechWithoutText, Sec(1), Sec(2)));
        source.Emit(new UtteranceCompleted(Utterance(2, "Spät")));

        Assert.True(await next);
        Assert.False(source.LateDeliverySettled(before, 3));
        Assert.True(await reader.MoveNextAsync());
        Assert.True(await reader.MoveNextAsync());
        Assert.True(source.LateDeliverySettled(before, 3));
        Assert.False(source.Released);
        Assert.Equal(before + 3, source.Delivered);
    }

    [Fact]
    public async Task LateDeliverySettlesWhenTheReaderStopsAfterTheFirstLateEvent()
    {
        // L02, zweiter erlaubter Ablauf: Die Sitzung nimmt das erste späte Ereignis und gibt die Quelle dann frei
        var source = new ScriptedTranscriptSource();
        var feed = new AudioFeed();
        var reader = source.RunAsync(feed.ReadAllAsync()).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await Until(() => source.Waiting, "Abholen wartet");
        var before = source.Delivered;
        source.Emit(new ProvisionalText(Guid.NewGuid(), "Spät"));
        source.Emit(new LiveNotice(LiveNoticeKind.SpeechWithoutText, Sec(1), Sec(2)));
        source.Emit(new UtteranceCompleted(Utterance(2, "Spät")));

        Assert.True(await next);
        Assert.False(source.LateDeliverySettled(before, 3));
        await reader.DisposeAsync();
        Assert.True(source.Released);
        Assert.Equal(before + 1, source.Delivered);
        Assert.True(source.LateDeliverySettled(before, 3));
    }

    [Fact]
    public void DetectorFailsBeyondTheSetTime()
    {
        var detector = FakeSpeechDetector.Speech((0, 5));
        detector.FailAfter = Sec(1);

        Assert.Single(detector.Detect(Floats(Sec(0.9), Sec(1)), Sec(0.9)));
        Assert.Throws<TimeoutException>(() => detector.Detect(Floats(Sec(1), Sec(1.1)), Sec(1)));
        Assert.Equal(1, detector.Failures);
        Assert.Equal(2, detector.CallCount);
    }

    [Fact]
    public async Task ScriptedSourceDeliversWhatIsEmittedAndReportsRelease()
    {
        var source = new ScriptedTranscriptSource();
        var feed = new AudioFeed();
        using var cancellation = new CancellationTokenSource();
        var notice = new LiveNotice(LiveNoticeKind.AudioGap, Sec(0), Sec(1));
        await using (var reader = source.RunAsync(feed.ReadAllAsync(), cancellation.Token).GetAsyncEnumerator())
        {
            // Das erste Abholen startet die Quelle; sie nimmt Audio an und wartet auf ein Ereignis
            var next = reader.MoveNextAsync().AsTask();
            feed.ReleaseUntil(Sec(0.2));
            await Until(() => source.AcceptedEnd == Sec(0.2), "Audio angenommen");
            await Until(() => source.Waiting, "Abholen wartet");

            // Den Abbruch übergeht sie: Das wartende Abholen erhält das spätere Ereignis trotzdem
            await cancellation.CancelAsync();
            source.Emit(notice);
            Assert.True(await next);
            Assert.Equal(notice, reader.Current);
            Assert.Equal(1, source.Delivered);
            Assert.False(source.Released);
        }

        Assert.True(source.Released);
    }

    [Fact]
    public void JoinFollowsTheRuleOfChapter3()
    {
        Assert.Equal("Das ist gut, danke", Join(["Das ist gut", ",", "danke"]));
        Assert.Equal("Alpha\nBeta", Join(["Alpha", "\n", "Beta"]));
        Assert.Equal("Alpha\n\nBeta", Join(["Alpha", "\n\n", "Beta"]));
        Assert.Equal("Alpha\nBeta", Join(["Alpha\n", "Beta"]));
        Assert.Equal("Komma,", Join(["Komma", ","]));
    }

    [Fact]
    public void DraftTextGoesThroughTheRecorder()
    {
        TextSegment Fixed(string text) => new(Guid.NewGuid(), text, SegmentState.Fixed, Sec(0), Sec(1));
        DictationOutput[] outputs =
        [
            new TextOutput(new RecognitionUpdate([Fixed("Guten")], [])),
            new TextOutput(new RecognitionUpdate([Fixed("Morgen")], [Fixed("vorläufig") with { State = SegmentState.Provisional }])),
        ];

        Assert.Equal("Guten Morgen", DraftText(outputs));
    }

    [Fact]
    public async Task SourceRunFeedsWaitsAndRecords()
    {
        var run = new SourceRun(new EchoSource());

        await run.FeedUntil(0.3);
        var notice = await run.Events.WaitFor(e => e is LiveNotice notice && notice.End == Sec(0.3), "dritter Block");
        var events = await run.EndAt(0.5);

        Assert.Equal(Sec(0.5), run.Source.AcceptedEnd);
        Assert.Equal(5, events.Count);
        Assert.Contains(notice, events);
    }

    /// <summary>
    /// Zeuge mit getrennter Annahme und Decoderarbeit (L01), keine P3-Umsetzung: plant bei 0,5 s einen Decode ein, beginnt
    /// ihn erst nach <see cref="Start"/> und verarbeitet das Ergebnis erst nach <see cref="Process"/>.
    /// </summary>
    private sealed class DeferredWorker(FakeHypothesisDecoder decoder) : ITranscriptSource
    {
        private long _accepted;
        private long _processed;

        public TestGate Start { get; } = new();

        public TestGate Process { get; } = new();

        public long Processed => Interlocked.Read(ref _processed);

        public TimeSpan AcceptedEnd => TimeSpan.FromTicks(Interlocked.Read(ref _accepted));

        public TimeSpan Backlog => TimeSpan.Zero;

        public void MarkBoundary(TimeSpan at)
        {
        }

        public async IAsyncEnumerable<TranscriptEvent> RunAsync(
            IAsyncEnumerable<AudioChunk> audio,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var work = Task.CompletedTask;
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
            {
                var end = chunk.Offset + TimeSpan.FromTicks(chunk.Pcm16Mono.Length / 2 * TestAudio.TicksPerSample);
                Interlocked.Exchange(ref _accepted, end.Ticks);
                if (end == Sec(0.5))
                {
                    work = Task.Run(
                        async () =>
                        {
                            await Start.Task;
                            await decoder.DecodeAsync(Floats(Sec(0), end), Sec(0), DefaultRecognition, CancellationToken.None);
                            await Process.Task;
                            Interlocked.Increment(ref _processed);
                        },
                        CancellationToken.None);
                }
            }

            await work;
            yield break;
        }
    }

    /// <summary>Minimale Quelle: meldet jeden angenommenen Block als Hinweis.</summary>
    private sealed class EchoSource : ITranscriptSource
    {
        private long _accepted;

        public TimeSpan AcceptedEnd => TimeSpan.FromTicks(Interlocked.Read(ref _accepted));

        public TimeSpan Backlog => TimeSpan.Zero;

        public void MarkBoundary(TimeSpan at)
        {
        }

        public async IAsyncEnumerable<TranscriptEvent> RunAsync(
            IAsyncEnumerable<AudioChunk> audio,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
            {
                var end = chunk.Offset + TimeSpan.FromTicks(chunk.Pcm16Mono.Length / 2 * TestAudio.TicksPerSample);
                Interlocked.Exchange(ref _accepted, end.Ticks);
                yield return new LiveNotice(LiveNoticeKind.AudioGap, chunk.Offset, end);
            }
        }
    }

    private static async IAsyncEnumerable<AudioChunk> FailingAudio()
    {
        await Task.Yield();
        yield return TestAudio.Chunk(Sec(0), Sec(0.1));
        throw new InvalidOperationException("Künstlicher Eingabefehler");
    }

    private static float[] Floats(TimeSpan start, TimeSpan end)
    {
        var first = TestAudio.SampleIndex(start);
        return [.. Enumerable.Range(0, (int)(TestAudio.SampleIndex(end) - first)).Select(i => TestAudio.FloatAt(first + i))];
    }
}
