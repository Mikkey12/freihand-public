using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>Erkennung mit Wort-Hypothesen: Äusserungen, Schnitte, Stabilität, Puffer (Kapitel 5.1–5.4).</summary>
public sealed class LiveTranscriptionTests
{
    [Fact]
    public async Task T02_SixtyMinutesKeepEveryWordOnceAndInOrder()
    {
        // 600 Äusserungen zu je acht Wörtern à 0,4 s, dazwischen je 2,8 s Stille, zusammen 60 Minuten
        var spoken = new List<RecognizedWord>();
        var regions = new List<(double, double)>();
        for (var n = 0; n < 600; n++)
        {
            var start = n * 6.0;
            spoken.AddRange(Words(start, 0.4, [.. Enumerable.Range(0, 8).Select(k => $"w{n}_{k}")]));
            regions.Add((start, start + 3.2));
        }

        // Nach Audiointervall; ein angeschnittenes Wort erscheint vorläufig mit anderem Wortlaut und wird widerrufen
        var decoder = FakeHypothesisDecoder.ByInterval(spoken, partialAtEnd: word => "x" + word.Text);
        decoder.VerifyAudio = false;
        var run = SessionRun.Live(decoder, FakeSpeechDetector.Speech([.. regions]), Options(LiveMode.Live));

        // Vorbedingung (A01): Der Schritt bei 0,5 s schneidet „w0_1“ [0,4, 0,8) an; seine Vorschau wird später widerrufen
        await run.StepTo(0.5);
        await run.Outputs.WaitFor(output => ShowsPreview(output, "w0_0 xw0_1"), "angeschnittenes Wort als Vorschau");

        // Normaler Betrieb (Kapitel 7, T02): Die Erkennung hält mit dem Audio Schritt. Der Test speist schneller als in
        // Echtzeit ein; darum wartet er nach jeder Sekunde, bis der Rückstand abgebaut ist, sonst würde ein langsamer
        // Rechner den Puffer füllen und BufferFull wäre richtig. Ein Puffer, der nicht freigegeben wird, lässt das Warten
        // scheitern.
        for (var second = 1; second <= 3600; second++)
        {
            await run.FeedUntil(second);
            await Until(() => run.Source.Backlog < Sec(20), $"Erkennung holt bei {second} s auf");
            Assert.True(run.Source.Backlog < Sec(120), $"Puffer an der Grenze bei {second} s");
        }

        var outputs = await run.EndAt(3600, timeoutSeconds: 600);

        Assert.Contains("w0_0 xw0_1", Previews(outputs));
        Assert.Equal(spoken.Select(word => word.Text), FixedWords(outputs));
        Assert.DoesNotContain(FixedWords(outputs), word => word.StartsWith('x'));
        Assert.Empty(Notices(outputs, LiveNoticeKind.BufferFull));
    }

    [Fact]
    public async Task T03_WordAtShiftedBoundaryIsNotLost()
    {
        // Die Erkennung setzt die Grenze zwischen „Brücke“ und „hält“ abwechselnd bei 1,5 s und 1,4 s (Messbefund 1)
        IReadOnlyList<RecognizedWord> first =
        [
            Word("Die", 0, 0.4), Word("alte", 0.4, 0.9), Word("Brücke", 0.9, 1.5), Word("hält", 1.5, 2.0), Word("noch", 2.0, 2.5),
        ];
        IReadOnlyList<RecognizedWord> second =
        [
            Word("Die", 0, 0.4), Word("alte", 0.4, 0.9), Word("Brücke", 0.9, 1.4), Word("hält", 1.4, 2.0), Word("noch", 2.0, 2.5),
        ];
        var decoder = new FakeHypothesisDecoder(request =>
            FakeHypothesisDecoder.Heard(request.Index % 2 == 0 ? first : second, request.Start, request.End));
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 2.5)), Options(LiveMode.Live));

        var events = await run.EndAt(4);

        Assert.Equal(["Die", "alte", "Brücke", "hält", "noch"], CompletedWords(events));
    }

    [Fact]
    public async Task T04_CutsOnlyAtSafePoints()
    {
        var spoken = Words(0.5, 0.5, "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht")
            .Concat(Words(6.0, 0.5, "neun", "zehn", "elf", "zwölf"))
            .ToList();
        var detector = FakeSpeechDetector.Speech((0.5, 4.5), (6.0, 8.0));
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, detector, Options(LiveMode.Live));

        // Erwartete Schritte 1,0, 1,5 und 2,0 s, jeder quittiert (A01, L01); bei 0,5 s ist noch keine Äusserung offen
        await run.Steps(1.0, 1.5, 2.0);
        await run.Events.WaitFor(e => e is ProvisionalText { Text: "eins zwei drei" }, "Schritt bei 2 s verarbeitet");
        await run.EndAt(10);

        var calls = decoder.Calls;
        Assert.True(calls.Count > 2);
        for (var i = 1; i < calls.Count; i++)
        {
            var cut = calls[i].Request.Start;
            var latest = calls.Take(i).Last(call => call.Completed);

            // Nie innerhalb eines Worts der jüngsten Hypothese
            Assert.DoesNotContain(latest.Words, word => word.Start < cut && cut < word.End);

            // Nur am Ende oder Beginn eines Worts oder in Nicht-Sprache (in diesem Ablauf gibt es keinen erzwungenen Schnitt)
            var wordEdges = calls.Take(i).SelectMany(call => call.Words).SelectMany(word => new[] { word.Start, word.End });
            var inSpeech = detector.Regions.Any(region => region.Start < cut && cut < region.End);
            Assert.True(!inSpeech || wordEdges.Contains(cut), $"Kürzung bei {cut} weder an einem Wortrand noch in Nicht-Sprache.");
        }
    }

    [Fact]
    public async Task T08_RepeatedSequenceIsFilteredNotRejected()
    {
        // Fixture T62: „a“ … „e“ in [0, 5) und dieselben Wörter überlappend in [4,5, 9,5)
        var decoder = FakeHypothesisDecoder.ByInterval(RepeatFixture(4.5));
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 9.5)), Options(LiveMode.Sentence));

        var events = await run.EndAt(9.5);

        // Kein Wiederholungsversuch: die Hypothese wurde nicht als ungültig abgewiesen
        Assert.Single(decoder.Calls);
        Assert.Empty(Notices(events, LiveNoticeKind.DecoderFailed));
        Assert.Equal(Words(0, 1, "a", "b", "c", "d", "e"), Completed(events).SelectMany(utterance => utterance.Words));
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T09_FixedWithinPauseOrAtMaxUtterance(LiveMode mode)
    {
        var spoken = Words(0.5, 0.5, "Guten", "Morgen", "allerseits")
            .Concat(Words(3.0, 0.5, [.. Enumerable.Range(0, 26).Select(k => $"n{k}")]))
            .ToList();
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((0.5, 2.0), (3.0, 16.0)), Options(mode));

        // Äusserungsende 2,0 s plus Pausenschwelle 0,6 s; der Decoder antwortet sofort. Mehr Audio gibt es noch nicht.
        await run.FeedUntil(2.6);
        await run.Outputs.WaitFor(output => FixedTexts([output]).Contains("Guten Morgen allerseits"), "fest nach der Pause");

        // Ohne Pause: Schnitt spätestens bei MaxUtterance ab Äusserungsbeginn 3,0 s, also bei 13,0 s
        await run.FeedUntil(13.0);
        await run.Outputs.WaitFor(output => FixedWords([output]).Contains("n0"), "fest beim Längenschnitt");

        await run.EndAt(17);
    }

    [Fact]
    public async Task T10_SlowDecoderSkipsStepsAndReportsBacklogOnce()
    {
        var spoken = Words(0, 0.5, [.. Enumerable.Range(0, 16).Select(k => $"a{k}")])
            .Concat(Words(9, 0.5, [.. Enumerable.Range(0, 10).Select(k => $"b{k}")]))
            .ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var gate = new TestGate();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 8), (9, 14)), Options(LiveMode.Live));

        // Erst den tatsächlich blockierten Auftrag [0, 0,5) bestätigen, dann Rückstand aufbauen (A01)
        await run.FirstCallBlocked();
        await run.FeedUntil(12.5);
        await run.Events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BacklogWarning }, "Warnung bei Rückstand");
        Assert.Single(decoder.Calls);
        Assert.True(run.Source.Backlog >= Sec(10));

        gate.Open();
        await run.Events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BacklogCleared }, "Rückstand abgebaut");
        var events = await run.EndAt(16);

        Assert.Single(Notices(events, LiveNoticeKind.BacklogWarning));
        Assert.Single(Notices(events, LiveNoticeKind.BacklogCleared));
        Assert.True(
            events.ToList().IndexOf(Notices(events, LiveNoticeKind.BacklogWarning)[0])
            < events.ToList().IndexOf(Notices(events, LiveNoticeKind.BacklogCleared)[0]));
        Assert.True(run.Source.Backlog < Sec(10));
        Assert.Equal(1, decoder.MaxConcurrent);
        Assert.Equal(spoken.Select(word => word.Text), CompletedWords(events));
    }

    [Fact]
    public async Task T11_NoDecodeGetsMoreThanWindow()
    {
        var spoken = Words(0, 1, [.. Enumerable.Range(0, 30).Select(k => $"v{k}")]);
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var gate = new TestGate();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 30)), Options(LiveMode.Live));

        // Erst den blockierten Auftrag [0, 0,5) bestätigen, dann Rückstand von 25 s, damit Aufträge an die Grenze stossen
        await run.FirstCallBlocked();
        await run.FeedUntil(25);
        gate.Open();
        await run.EndAt(32);

        Assert.All(decoder.Requests, request => Assert.True(request.SampleCount <= 10 * TestAudio.SampleRate, $"{request}"));
    }

    [Fact]
    public async Task T20_ProvisionalTextReplacesWhole()
    {
        // Wörter à 0,7 s: Decodes alle 0,5 s schneiden Wörter an; die Attrappe hört dann ein anderes Wort
        var spoken = Words(0.5, 0.7, "eins", "zwei", "drei", "vier", "fünf");
        var decoder = FakeHypothesisDecoder.ByInterval(spoken, partialAtEnd: word => "x" + word.Text);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0.5, 4.0)), Options(LiveMode.Live));

        // Vorbedingung (A01): Der erwartete Schritt bei 1 s schneidet „eins“ an, die Vorschau zeigt den anderen Wortlaut
        await run.StepTo(1.0);
        await run.Events.WaitFor(e => e is ProvisionalText { Text: "xeins" }, "angeschnittener Zwischenschritt");
        var events = await run.EndAt(6);

        // Jede Vorschau besteht aus dem schon festen Text vor dem Auftrag und genau einer Hypothese, ohne ältere Reste
        var candidates = decoder.Calls
            .Where(call => call.Completed)
            .Select(call => Join(spoken.Where(word => word.End <= call.Request.Start).Concat(call.Words)))
            .ToHashSet();
        var previews = events.OfType<ProvisionalText>().ToList();
        Assert.NotEmpty(previews);
        Assert.All(previews, preview => Assert.Contains(preview.Text, candidates));
        Assert.Contains(previews, preview => preview.Text.Contains('x'));
    }

    [Fact]
    public async Task T21_LiveAndSentenceGiveTheSameWords()
    {
        var spoken = Words(0.5, 0.5, "Am", "Morgen", "kommt", "der", "Bericht")
            .Concat(Words(4.0, 0.5, [.. Enumerable.Range(0, 28).Select(k => $"l{k}")]))
            .Concat(Words(19.5, 0.5, "Ende", "gut"))
            .ToList();

        async Task<IReadOnlyList<string>> RunAsync(LiveMode mode)
        {
            var run = SourceRun.Live(
                FakeHypothesisDecoder.ByInterval(spoken),
                FakeSpeechDetector.Speech((0.5, 3.0), (4.0, 18.0), (19.5, 20.5)),
                Options(mode));
            return CompletedWords(await run.EndAt(22));
        }

        var live = await RunAsync(LiveMode.Live);
        var sentence = await RunAsync(LiveMode.Sentence);

        Assert.NotEmpty(live);
        Assert.Equal(live, sentence);
    }

    [Fact]
    public async Task T22_ModeComesFromOptionsAndHoldsForTheWholeDictation()
    {
        var spoken = Words(0.5, 0.5, "Hallo", "Welt", "heute").Concat(Words(3.5, 0.5, "und", "morgen")).ToList();
        var detector = FakeSpeechDetector.Speech((0.5, 2.0), (3.5, 4.5));
        var sentenceOptions = Options(LiveMode.Sentence);

        // Live ist der Schritt bei 1 s erwartet und quittiert, dort entsteht sicher die Vorschau „Hallo“ (A01, L01);
        // satzweise gibt es vor der Pause keinen Decode, dort genügt die Annahme bis 1 s
        async Task<IReadOnlyList<TranscriptEvent>> RunAsync(LiveOptions options)
        {
            var run = SourceRun.Live(FakeHypothesisDecoder.ByInterval(spoken), detector, options);
            if (options.Mode == LiveMode.Live)
            {
                await run.StepTo(1.0);
                await run.Events.WaitFor(e => e is ProvisionalText { Text: "Hallo" }, "Live-Zwischenvorschau");
            }
            else
            {
                await run.FeedUntil(1.0);
            }

            return await run.EndAt(6);
        }

        var sentenceEvents = await RunAsync(sentenceOptions);

        // Satzweise über das ganze Diktat: nie vorläufiger Text
        Assert.Empty(sentenceEvents.OfType<ProvisionalText>());
        Assert.Equal(spoken.Select(word => word.Text), CompletedWords(sentenceEvents));

        // Erst eine neue Instanz nimmt den anderen Modus
        var liveEvents = await RunAsync(sentenceOptions with { Mode = LiveMode.Live });
        Assert.NotEmpty(liveEvents.OfType<ProvisionalText>());
    }

    /// <summary>
    /// Vergleich aus Kapitel 5.2 (Befund P05), seit E1 (03.11.2026) ohne Übereinstimmung von Hypothesen; er gilt weiter für
    /// die Wiederholungsprüfung und die Floskelliste (Kapitel 5.5). Vorher prüfte T23 ihn über die Stabilisierung.
    /// </summary>
    [Theory]
    [InlineData("-5", "5", false)]
    [InlineData("5%", "5", false)]
    [InlineData("Ende.", "ende", true)]
    [InlineData("„Ende“", "Ende", true)]
    [InlineData("’s", "'s", true)]
    [InlineData("(5)", "5", false)]
    public void T23_ComparisonKeepsSignsUnitsAndBracketsApart(string first, string second, bool same)
    {
        var a = Assert.Single(WordComparison.Units([Word(first, 0, 0.5)])).Key;
        var b = Assert.Single(WordComparison.Units([Word(second, 0, 0.5)])).Key;
        Assert.Equal(same, a == b);
    }

    [Fact]
    public async Task T24_FixedTextIsTheFinalDecodeNotAnEarlierPreview()
    {
        // E1 (03.11.2026): Live wird nichts mitten in der Äusserung übernommen; die Vorschau zeigt jede Lesart, fest wird
        // die Lesart des Abschluss-Decodes über die ganze Äusserung (vormals: Schreibweise der jüngsten Übereinstimmung)
        var readings = 0;
        var decoder = new FakeHypothesisDecoder(request =>
        {
            if (!request.Contains(Sec(1), Sec(1.5)) || request.End < Sec(2.5))
            {
                return [];
            }

            // „Ende“, dann „Ende.“, danach „Ende!“ für jeden weiteren Aufruf, auch den Abschluss
            var reading = Interlocked.Increment(ref readings);
            return [Word(reading == 1 ? "Ende" : reading == 2 ? "Ende." : "Ende!", 1, 1.5)];
        });
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((1, 5)), Options(LiveMode.Live));

        // Zwei lesende Schritte, die Schritte davor ebenfalls quittiert (A01, L01)
        await run.Steps(1.5, 2.0, 2.5);
        await run.Events.WaitFor(e => e is ProvisionalText { Text: "Ende" }, "erste Lesart verarbeitet");
        await run.StepTo(3.0);
        await run.Events.WaitFor(e => e is ProvisionalText { Text: "Ende." }, "zweite Lesart verarbeitet");
        var events = await run.EndAt(6);

        // Jeder Auftrag beginnt am Äusserungsbeginn; der letzte ist der Abschluss bis zur Grenze bei 5 s
        var requests = decoder.Requests;
        Assert.All(requests, request => Assert.Equal(Sec(1), request.Start));
        Assert.Equal(Sec(5), requests[^1].End);
        Assert.True(readings >= 3, $"nur {readings} Lesungen");
        Assert.Equal(["Ende!"], CompletedWords(events));
    }

    [Fact]
    public async Task T25_RealRepetitionInNewAudioStays()
    {
        string[] phrase = ["Wir", "prüfen", "diese", "Angaben", "nochmals"];
        var spoken = Words(1, 0.5, [.. phrase, .. phrase]);
        var run = SourceRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1, 6)), Options(LiveMode.Live));

        var events = await run.EndAt(8);

        // Beide Folgen bleiben; schon verarbeitetes Audio wird nicht erneut erkannt, also keine dritte
        Assert.Equal([.. phrase, .. phrase], CompletedWords(events));
    }

    [Fact]
    public void T27_WordWithSixtyPercentInSpeechStays()
    {
        Assert.True(HalfInRegions((1, 1.3), (1.7, 2.0)));
    }

    [Fact]
    public void T27_WordWithFortyNinePercentInSpeechIsDropped()
    {
        Assert.False(HalfInRegions((1, 1.3), (1.81, 2.0)));
    }

    [Fact]
    public void T27_DuplicateRegionsCountOnce()
    {
        // Doppelt und überlappend gemeldet: zusammen weiter 49 %, das Wort entfällt
        Assert.False(HalfInRegions((1, 1.3), (1, 1.3), (1.1, 1.3), (1.81, 2.0)));
    }

    [Fact]
    public async Task T77_AShortGapKeepsTheWordThatT27WouldDrop()
    {
        // Dieselben Bereiche wie T27 mit 49 %: Die Lücke von 0,51 s liegt unter der Pausenschwelle und gilt als Sprache
        Assert.Equal(["Beispiel"], await WordsWithRegions((1, 1.3), (1.81, 2.0)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task T28_InvalidHypothesisIsRetriedOnceThenDecoderFailed(int violation)
    {
        var decoder = FakeHypothesisDecoder.Silent();
        decoder.RawResponse = request => new RecognitionHypothesis(request.Start, request.End, violation switch
        {
            // End ≤ Start
            0 => [Word("Gültig", 1, 2), new RecognizedWord("kaputt", Sec(2.5), Sec(2.5), 0.9f)],

            // Beginn vor AudioStart
            1 => [new RecognizedWord("vorher", request.Start - Sec(0.2), request.Start + Sec(0.1), 0.9f), Word("Gültig", 1, 2)],

            // Ende nach AudioEnd
            _ => [Word("Gültig", 1, 2), new RecognizedWord("nachher", request.End - Sec(0.2), request.End + Sec(0.2), 0.9f)],
        });
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((1, 3)), Options(LiveMode.Sentence));

        run.Feed.ReleaseUntil(Sec(5));
        run.Feed.Complete();
        var failure = await run.Events.Finish();

        // Ein Wiederholungsversuch desselben Decodes, dann DecoderFailed mit Zeitspanne und Ende mit Fehler
        Assert.NotNull(failure);
        var calls = decoder.Requests;
        Assert.Equal(2, calls.Count);
        Assert.Equal((calls[0].Start, calls[0].End), (calls[1].Start, calls[1].End));
        var notice = Assert.Single(Notices(run.Events.Items, LiveNoticeKind.DecoderFailed));
        Assert.True(notice.Start >= calls[0].Start && notice.End <= calls[0].End, $"{notice}");

        // Der Puffer wird nicht still gekürzt: die Meldung deckt die ganze Sprache ab, und nichts wird fest
        Assert.True(notice.Start <= Sec(1) && notice.End >= Sec(3), $"{notice}");
        Assert.Empty(CompletedWords(run.Events.Items));
    }

    [Fact]
    public async Task T29_TenMinutesOfNonSpeechAreReleased()
    {
        var run = SessionRun.Live(FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech(), Options(LiveMode.Live));

        for (var block = 1; block <= 6000; block++)
        {
            await run.FeedUntil(block / 10.0);
            Assert.True(run.Source.Backlog < Sec(0.6) + (2 * Block), $"Puffer {run.Source.Backlog} bei {block / 10.0} s");
        }

        var outputs = await run.EndAt(600);

        Assert.Empty(FixedSegments(outputs));
        Assert.DoesNotContain(
            outputs.OfType<TextOutput>().SelectMany(output => output.Update.Provisional),
            segment => segment.Text.Length > 0);
    }

    [Fact]
    public async Task T67_NonSpeechIsReleasedWhileASpeechDecodeIsBlocked()
    {
        // Ein kurzer Laut [0, 0,2); sein Decode [0, 0,5) blockiert zehn Minuten lang und übergeht den Abbruch
        var decoder = FakeHypothesisDecoder.Silent();
        var gate = new TestGate();
        decoder.Hold = _ => gate.Task;
        decoder.HonorCancellation = false;
        var run = SessionRun.Live(decoder, FakeSpeechDetector.Speech((0, 0.2)), Options(LiveMode.Live));

        // Eigene Schranke (Kapitel 7, T67), nicht die von T29: Gehalten werden darf nur, was noch nicht abschliessend
        // verarbeitet ist, also die Äusserung [0, 0,2) des laufenden Auftrags und wie in T29 das noch nicht als
        // Nicht-Sprache bestätigte Ende (unter PauseThreshold plus zwei Blöcken). Die Kopie, die der laufende Auftrag vom
        // Audio [0, 0,5) hält, zählt nicht zum Puffer: bestätigte Nicht-Sprache wird auch während des Decodes freigegeben
        // (Kapitel 5.1, Regel 5; Vertrag 1).
        var bound = Sec(0.2) + Sec(0.6) + (2 * Block);

        // Erst den tatsächlich begonnenen, blockierten Auftrag [0, 0,5) bestätigen, dann weiteres Audio (A01, L01)
        var blocked = await run.FirstCallBlocked();
        Assert.True(run.Source.Backlog < bound, $"Puffer {run.Source.Backlog} bei 0,5 s");
        for (var block = 6; block <= 6000; block++)
        {
            await run.FeedUntil(block / 10.0);
            Assert.True(run.Source.Backlog < bound, $"Puffer {run.Source.Backlog} bei {block / 10.0} s");
        }

        Assert.Same(blocked, Assert.Single(decoder.Calls));
        Assert.False(blocked.Completed);
        gate.Open();
        var outputs = await run.EndAt(600);

        // Der Speicher des laufenden Decodes blieb während der Freigabe unverändert (Vertrag 1)
        Assert.True(blocked.Completed);
        Assert.True(blocked.AudioIntact);
        Assert.Empty(FixedSegments(outputs));
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T30_LengthCutsKeepEveryWordOnce(LiveMode mode)
    {
        var spoken = Words(0, 1, [.. Enumerable.Range(0, 25).Select(k => $"w{k}")]);
        var run = SourceRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((0, 25)), Options(mode));

        var events = await run.EndAt(25);

        var utterances = Completed(events).Where(utterance => utterance.Words.Count > 0).ToList();
        Assert.Equal(3, utterances.Count);
        Assert.Equal((Sec(9), UtteranceEnd.LengthCut), (utterances[0].End, utterances[0].Ending));
        Assert.Equal((Sec(9), Sec(18), UtteranceEnd.LengthCut), (utterances[1].Start, utterances[1].End, utterances[1].Ending));
        Assert.Equal((Sec(25), UtteranceEnd.StreamEnd), (utterances[2].End, utterances[2].Ending));
        Assert.Equal(spoken.Take(9), utterances[0].Words);
        Assert.Equal(spoken.Skip(9).Take(9), utterances[1].Words);
        Assert.Equal(spoken.Skip(18), utterances[2].Words);
    }

    [Fact]
    public async Task T31_BlockedDecoderFillsBufferThenStopsAccepting()
    {
        var spoken = Words(0, 1, [.. Enumerable.Range(0, 130).Select(k => $"w{k}")]);
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        decoder.VerifyAudio = false;
        var gate = new TestGate();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 130)), Options(LiveMode.Live));

        // Erst den blockierten Auftrag [0, 0,5) bestätigen (A01); Warnung ab 10 s Rückstand, noch bevor weiteres Audio kommt
        await run.FirstCallBlocked();
        await run.FeedUntil(10);
        await run.Events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BacklogWarning }, "Warnung ab 10 s");

        // Die Aufnahme läuft weiter; bei 120 s ist der Puffer voll, danach wird nichts mehr angenommen
        run.Feed.ReleaseUntil(Sec(130));
        await run.Events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BufferFull }, "BufferFull", 60);
        Assert.Equal(Sec(120), run.Source.AcceptedEnd);
        await Settle();
        Assert.Equal(Sec(120), run.Source.AcceptedEnd);

        // P8 stoppt die Aufnahme; nach der Freigabe wird jeder angenommene Block genau einmal verarbeitet
        run.Feed.Stop();
        gate.Open();
        var events = await run.Events.FinishOk(120);

        Assert.Equal(spoken.Take(120).Select(word => word.Text), CompletedWords(events));
        Assert.True(
            events.ToList().IndexOf(Notices(events, LiveNoticeKind.BacklogWarning)[0])
            < events.ToList().IndexOf(Notices(events, LiveNoticeKind.BufferFull)[0]));
    }

    [Fact]
    public async Task T32_ShortGapKeepsOneUtterance()
    {
        var spoken = Words(1.0, 0.5, "Erst", "noch").Concat(Words(2.3, 0.35, "dann", "weiter")).ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((1.0, 2.0), (2.3, 3.0)), Options(LiveMode.Sentence));

        // 0,3 s Abstand bei PauseThreshold 0,6 s: nach dem ersten Bereich kein Decode
        await run.FeedUntil(2.3);
        await Settle();
        Assert.Empty(decoder.Calls);

        // Nach dem zweiten Bereich und 0,6 s Stille genau einer über beide
        await run.FeedUntil(3.6);
        await Until(() => decoder.Calls.Count > 0, "Decode nach der Pause");
        var events = await run.EndAt(5);

        var request = Assert.Single(decoder.Requests);
        Assert.True(request.Start <= Sec(1.0) && request.End >= Sec(3.0), $"{request}");
        Assert.Equal(["Erst", "noch", "dann", "weiter"], Assert.Single(Completed(events)).Words.Select(word => word.Text));
    }

    [Fact]
    public async Task T33_ShortSpeechRegionIsNotDropped()
    {
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval([Word("ja", 1.0, 1.18)]), FakeSpeechDetector.Speech((1.0, 1.18)), Options(LiveMode.Live));

        var outputs = await run.EndAt(3);

        Assert.Equal(["ja"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T34_StreamEndDecodesTheOpenRestOnce()
    {
        var spoken = Words(0.5, 0.5, "Hier", "beginnt", "es").Append(Word("fertig", 3.0, 3.3)).ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0.5, 2.0), (3.0, 3.3)), Options(LiveMode.Sentence));

        // Streamende direkt nach dem Rest von 300 ms, ohne Nachlaufpause
        var events = await run.EndAt(3.3);

        Assert.Single(decoder.Requests, request => request.Overlaps(Sec(3.0), Sec(3.3)));
        var last = Completed(events)[^1];
        Assert.Equal(UtteranceEnd.StreamEnd, last.Ending);
        Assert.Equal(["fertig"], last.Words.Select(word => word.Text));
    }

    [Fact]
    public async Task T34_CancellationAtTheSamePointDecodesNothingMore()
    {
        var spoken = Words(0.5, 0.5, "Hier", "beginnt", "es").Append(Word("fertig", 3.0, 3.3)).ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        using var cancellation = new CancellationTokenSource();
        var run = SourceRun.Live(
            decoder, FakeSpeechDetector.Speech((0.5, 2.0), (3.0, 3.3)), Options(LiveMode.Sentence), cancellationToken: cancellation.Token);

        await run.FeedUntil(3.3);
        await run.Events.WaitFor(e => e is UtteranceCompleted, "erste Äusserung");
        await Settle();
        var calls = decoder.Calls.Count;
        var events = run.Events.Count;

        await cancellation.CancelAsync();
        await run.Events.Finish();
        run.Feed.Complete();
        await Settle();

        Assert.Equal(calls, decoder.Calls.Count);
        Assert.Equal(events, run.Events.Count);
        Assert.DoesNotContain(decoder.Requests, request => request.Overlaps(Sec(3.0), Sec(3.3)));
    }

    [Fact]
    public async Task T42_DecoderTimesAreSessionTimes()
    {
        // 30 s Stille, dann Sprache; das Fenster wird auf 32 s gekürzt
        var spoken = Words(30, 1, "Alpha", "Beta", "Gamma", "Delta");
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((30, 34)), Options(LiveMode.Live));

        // Bis 30 s nur Nicht-Sprache, also kein Decode (Kapitel 5.2); danach jeder Schritt quittiert (A01, L01). Seit E1
        // (03.11.2026) beginnt jeder Schritt am Äusserungsbeginn bei 30 s
        await run.FeedUntil(30);
        await run.Steps(30.5, 31.0, 31.5, 32.0, 32.5, 33.0, 33.5);
        await run.Events.WaitFor(e => e is ProvisionalText { Text: "Alpha Beta Gamma" }, "Schritt bei 33,5 s verarbeitet");
        var events = await run.EndAt(36);

        Assert.All(decoder.Requests, request => Assert.Equal(Sec(30), request.Start));
        Assert.All(decoder.Calls, call => Assert.True(call.AudioIntact, $"Audio von {call.Request} falsch übergeben"));

        // Wortzeiten unverändert, ohne zweiten Offset; „Gamma“ über dem Schnitt genau einmal vollständig
        Assert.Equal(spoken, Completed(events).SelectMany(utterance => utterance.Words));
    }

    [Fact]
    public async Task T43_DecoderGetsTheSessionRecognitionOptions()
    {
        var spoken = Words(0.5, 0.5, "Frau", "Gerber", "kommt");
        var first = FakeHypothesisDecoder.ByInterval(spoken);
        var hints = new RecognitionOptions("de-CH", ["Gerber", "Kantonsspital"]);
        await SourceRun.Live(first, FakeSpeechDetector.Speech((0.5, 2.0)), Options(LiveMode.Live), recognition: hints).EndAt(4);

        Assert.NotEmpty(first.Requests);
        Assert.All(first.Requests, request =>
        {
            Assert.Equal("de-CH", request.Options.Language);
            Assert.Equal(["Gerber", "Kantonsspital"], request.Options.VocabularyHints);
        });

        // Eine zweite Sitzung ohne Hinweise erhält keine alten
        var second = FakeHypothesisDecoder.ByInterval(spoken);
        await SourceRun.Live(second, FakeSpeechDetector.Speech((0.5, 2.0)), Options(LiveMode.Live), recognition: new RecognitionOptions("de-CH", []))
            .EndAt(4);

        Assert.NotEmpty(second.Requests);
        Assert.All(second.Requests, request => Assert.Empty(request.Options.VocabularyHints));
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T45_AudioBeforeTheCutIsNeverRequestedAgain(LiveMode mode)
    {
        var spoken = Words(0, 1, [.. Enumerable.Range(0, 14).Select(k => $"p{k}")]);

        // Die Attrappe würde Audio vor einem früheren Auftragsbeginn später anders erkennen
        var furthest = TimeSpan.Zero;
        var decoder = new FakeHypothesisDecoder(request =>
        {
            var cut = furthest;
            furthest = request.Start > furthest ? request.Start : furthest;
            return
            [
                .. FakeHypothesisDecoder.Heard(spoken, request.Start, request.End)
                    .Select(word => word.Start < cut ? word with { Text = "anders" } : word),
            ];
        });
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 14)), Options(mode));

        var events = await run.EndAt(15);

        var starts = decoder.Requests.Select(request => request.Start).ToList();
        Assert.Equal(starts.Order(), starts);
        Assert.Equal(spoken.Select(word => word.Text), CompletedWords(events));
    }

    [Fact]
    public async Task T49_OtherSampleRatesAreRejected()
    {
        var run = SourceRun.Live(FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech(), Options(LiveMode.Live));

        run.Feed.Enqueue(TestAudio.Chunk(Sec(0), Sec(0.1), sampleRate: 48_000));
        run.Feed.Complete();

        Assert.IsAssignableFrom<ArgumentException>(await run.Events.Finish());
    }

    [Fact]
    public async Task T49_GapIsReportedAndEndsTheUtterance()
    {
        var spoken = Words(1, 0.5, "vorher", "dort").Concat(Words(3, 0.5, "danach", "hier")).ToList();
        var run = SourceRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1, 2), (3, 4)), Options(LiveMode.Live));

        await run.FeedUntil(2);
        run.Feed.SkipTo(Sec(3));
        var events = await run.EndAt(5);

        var gap = Assert.Single(Notices(events, LiveNoticeKind.AudioGap));
        Assert.Equal((Sec(2), Sec(3)), (gap.Start, gap.End));
        var utterances = Completed(events).Where(utterance => utterance.Words.Count > 0).ToList();
        Assert.Equal(2, utterances.Count);
        Assert.Equal(UtteranceEnd.AudioGap, utterances[0].Ending);
        Assert.Equal(["vorher", "dort"], utterances[0].Words.Select(word => word.Text));
        Assert.Equal(["danach", "hier"], utterances[1].Words.Select(word => word.Text));
    }

    [Fact]
    public async Task T49_OverlappingBlockIsTakenOnlyWithItsNewPart()
    {
        var spoken = Words(1, 0.5, "eins", "zwei", "drei", "vier");
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((1, 3)), Options(LiveMode.Live));

        await run.FeedUntil(2);
        run.Feed.Enqueue(TestAudio.Chunk(Sec(1.5), Sec(2.5)));
        await Until(() => run.Source.AcceptedEnd == Sec(2.5), "überlappender Block angenommen");
        var events = await run.EndAt(5);

        Assert.Equal(Sec(5), run.Source.AcceptedEnd);
        Assert.Empty(Notices(events, LiveNoticeKind.AudioGap));
        Assert.All(decoder.Calls, call => Assert.True(call.AudioIntact, $"Audio von {call.Request} falsch übergeben"));
        Assert.Equal(["eins", "zwei", "drei", "vier"], CompletedWords(events));
    }

    [Fact]
    public async Task T51_EmptyDecodeOfCompleteUtteranceGivesNeitherTextNorHint()
    {
        var run = SessionRun.Live(FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech((1, 2)), Options(LiveMode.Live));

        var outputs = await run.EndAt(4);

        Assert.Empty(FixedSegments(outputs));
        Assert.DoesNotContain(outputs, output => output is HintOutput);
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T51_EmptyUtteranceIsStillCompletedInternally(LiveMode mode)
    {
        var run = SourceRun.Live(FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech((1, 2)), Options(mode));

        var events = await run.EndAt(4);

        // Intern ein Abschluss ohne Wörter, damit Grenze, Kennung und Aktivierung enden (Kapitel 5.1, Regel 2); kein Verlust
        var utterance = Assert.Single(Completed(events));
        Assert.Empty(utterance.Words);
        Assert.Equal(UtteranceEnd.Pause, utterance.Ending);
        Assert.Empty(events.OfType<LiveNotice>());
    }

    [Fact]
    public async Task T51_LengthCutWithoutAdoptableWordReportsSpeechWithoutText()
    {
        var run = SourceRun.Live(FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech((0, 12)), Options(LiveMode.Sentence));

        await run.FeedUntil(10);
        var notice = (LiveNotice)await run.Events.WaitFor(
            e => e is LiveNotice { Kind: LiveNoticeKind.SpeechWithoutText }, "SpeechWithoutText beim Längenschnitt");

        Assert.Equal((Sec(0), Sec(9.7)), (notice.Start, notice.End));

        // Der Puffer ist bis zum Schnitt freigegeben; übrig ist nur [9,7, 10)
        await Until(() => run.Source.Backlog <= Sec(0.3), "Puffer bis zum Schnitt freigegeben");
        await run.EndAt(12);
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T78_EveryLengthCutWithTextIsReportedOnceAndLeavesTheTextAlone(LiveMode mode)
    {
        var spoken = Words(0, 1, [.. Enumerable.Range(0, 25).Select(k => $"w{k}")]);
        var run = SourceRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((0, 25)), Options(mode));

        var events = await run.EndAt(25);

        // Spanne: vom Kürzungspunkt bis zur Grenze des Auftrags, je Schnitt genau ein Hinweis
        Assert.Equal(
            [(Sec(9), Sec(10)), (Sec(18), Sec(19))],
            Notices(events, LiveNoticeKind.LengthCutUncertain).Select(notice => (notice.Start, notice.End)));
        Assert.Empty(Notices(events, LiveNoticeKind.SpeechWithoutText));

        // Der Hinweis ändert nichts am Text (wie T30)
        Assert.Equal(spoken, Completed(events).SelectMany(utterance => utterance.Words));
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T78_NoLengthCutMeansNoHint(LiveMode mode)
    {
        var spoken = Words(0, 1, ["ein", "kurzer", "Satz"]);
        var run = SourceRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((0, 3)), Options(mode));

        var events = await run.EndAt(6);

        Assert.Empty(events.OfType<LiveNotice>());
    }

    [Fact]
    public async Task T78_LostSpeechKeepsItsOwnNoticeWithoutASecondOne()
    {
        var run = SourceRun.Live(FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech((0, 12)), Options(LiveMode.Sentence));

        var events = await run.EndAt(12);

        Assert.Single(Notices(events, LiveNoticeKind.SpeechWithoutText));
        Assert.Empty(Notices(events, LiveNoticeKind.LengthCutUncertain));
    }

    [Fact]
    public async Task T52_BacklogIsWorkedOffInWindowSizedRequests()
    {
        var decoder = FakeHypothesisDecoder.Silent();
        var gate = new TestGate();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        // Seit E1 verlangt live Window >= MaxUtterance; der Schnitt nach 2 s ist damit ein Längenschnitt (Regel 3)
        var options = Options(LiveMode.Live) with { Window = Sec(2), MaxUtterance = Sec(2) };
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 100)), options);

        // Der erste Decode bei 0,5 s blockiert tatsächlich (A01); Blöcke bis 6 s werden angenommen, dann liefert er leer
        await run.FirstCallBlocked();
        await run.FeedUntil(6);
        gate.Open();
        var events = await run.EndAt(6);

        var requests = decoder.Requests;
        Assert.All(requests, request => Assert.True(request.SampleCount <= 32_000, $"{request}"));
        Assert.Equal(
            [(Sec(0), Sec(2)), (Sec(1.7), Sec(3.7)), (Sec(3.4), Sec(5.4))],
            requests.Skip(1).Take(3).Select(request => (request.Start, request.End)));

        // Für jeden vollen leeren Auftrag eine Verlustmeldung [K, K + 1,7); der Rest wird weiter decodiert
        var full = requests.Skip(1).Where(request => request.End - request.Start == Sec(2)).ToList();
        Assert.Equal(
            full.Select(request => (request.Start, request.Start + Sec(1.7))),
            Notices(events, LiveNoticeKind.SpeechWithoutText).Select(notice => (notice.Start, notice.End)));
        Assert.Contains(requests, request => request.Start == Sec(5.1));
        Assert.Empty(Notices(events, LiveNoticeKind.BufferFull));
    }

    [Fact]
    public async Task T53_EmptyDecoderInSentenceModeCutsAtTheGuardAndReportsLoss()
    {
        var decoder = FakeHypothesisDecoder.Silent();
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 25)), Options(LiveMode.Sentence));

        var events = await run.EndAt(25);

        Assert.Equal(
            [(Sec(0), Sec(10)), (Sec(9.7), Sec(19.7)), (Sec(19.4), Sec(25))],
            decoder.Requests.Select(request => (request.Start, request.End)));
        Assert.Equal(
            [(Sec(0), Sec(9.7)), (Sec(9.7), Sec(19.4))],
            Notices(events, LiveNoticeKind.SpeechWithoutText).Select(notice => (notice.Start, notice.End)));
        Assert.Empty(Notices(events, LiveNoticeKind.DecoderFailed));
        Assert.Empty(CompletedWords(events));
    }

    [Fact]
    public async Task T58_OverlappingWordsAreInvalidAndNothingIsAdopted()
    {
        var decoder = FakeHypothesisDecoder.Silent();
        decoder.RawResponse = request =>
            new RecognitionHypothesis(request.Start, request.End, [Word("A", 0, 2), Word("B", 1, 1.5)]);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 3)), Options(LiveMode.Sentence));

        run.Feed.ReleaseUntil(Sec(3));
        run.Feed.Complete();
        var failure = await run.Events.Finish();

        Assert.NotNull(failure);
        Assert.Equal(
            [(Sec(0), Sec(3)), (Sec(0), Sec(3))],
            decoder.Requests.Select(request => (request.Start, request.End)));
        Assert.Single(Notices(run.Events.Items, LiveNoticeKind.DecoderFailed));
        Assert.Empty(CompletedWords(run.Events.Items));
        Assert.DoesNotContain(decoder.Requests, request => request.Start == Sec(1.5));
    }

    [Fact]
    public async Task T59_WordNearTheLimitIsCutBeforeAndDecodedAgainWhole()
    {
        RecognizedWord word = Word("Wort", 9.5, 9.9);
        RecognizedWord after = Word("danach", 10.2, 11);
        var decoder = new FakeHypothesisDecoder(request => request.Start < Sec(9.5)
            ? FakeHypothesisDecoder.Heard([word], request.Start, request.End)
            : FakeHypothesisDecoder.Heard([word, after], request.Start, request.End));
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 12)), Options(LiveMode.Sentence));

        var events = await run.EndAt(12);

        // Schnittwahl Fall b: Schnitt am Beginn des Worts, davor Verlustmeldung
        var notice = Assert.Single(Notices(events, LiveNoticeKind.SpeechWithoutText));
        Assert.Equal((Sec(0), Sec(9.5)), (notice.Start, notice.End));
        Assert.DoesNotContain(decoder.Requests, request => request.Start > Sec(9.5) && request.Start < Sec(9.9));

        // „Wort“ genau einmal vollständig, in der nächsten Äusserung
        var holder = Assert.Single(Completed(events), utterance => utterance.Words.Contains(word));
        Assert.True(holder.Start >= Sec(9.5));
        Assert.Equal([word, after], Completed(events).SelectMany(utterance => utterance.Words));
    }

    [Fact]
    public async Task T60_LengthCutCutsBeforeAnUnadoptedWord()
    {
        var decoder = FakeHypothesisDecoder.ByInterval([Word("Wort", 1.5, 1.9)]);
        var gate = new TestGate();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        // Seit E1 verlangt live Window >= MaxUtterance; der volle Auftrag ist damit ein Längenschnitt (Regel 3)
        var run = SourceRun.Live(
            decoder, FakeSpeechDetector.Speech((0, 2.4)), Options(LiveMode.Live) with { Window = Sec(2), MaxUtterance = Sec(2) });

        // Erst den blockierten Auftrag [0, 0,5) bestätigen (A01), dann Rückstand: der volle Auftrag [0, 2) liefert nur
        // „Wort“ [1,5, 1,9)
        await run.FirstCallBlocked();
        await run.FeedUntil(4);
        gate.Open();
        var events = await run.EndAt(5);

        var requests = decoder.Requests;
        var full = requests.Single(request => request.Start == Sec(0) && request.End == Sec(2));
        Assert.Equal(Sec(1.5), requests.First(request => request.Index > full.Index).Start);
        var notice = Assert.Single(Notices(events, LiveNoticeKind.SpeechWithoutText));
        Assert.Equal((Sec(0), Sec(1.5)), (notice.Start, notice.End));
        Assert.Equal(["Wort"], CompletedWords(events));
    }

    [Fact]
    public async Task T63_SingleLongWordIsAdoptedDespiteTheGuard()
    {
        var decoder = FakeHypothesisDecoder.ByInterval([Word("Langwort", 0, 9.8)]);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 12)), Options(LiveMode.Sentence));

        var events = await run.EndAt(12);

        var first = Completed(events)[0];
        Assert.Equal(["Langwort"], first.Words.Select(word => word.Text));
        Assert.Equal((Sec(9.8), UtteranceEnd.LengthCut), (first.End, first.Ending));
        Assert.Contains(decoder.Requests, request => request.Start == Sec(9.8));
        Assert.Empty(Notices(events, LiveNoticeKind.SpeechWithoutText));
    }

    [Fact]
    public async Task T68_DetectorFailureEndsVisiblyWithoutFurtherText()
    {
        var spoken = Words(0.5, 0.5, "Erster", "Satz", "hier").Concat(Words(3.0, 0.5, "noch", "mehr", "Text", "folgt")).ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);

        // Wie P8 bei Ausfall oder Zeitüberschreitung: Detect wirft, sobald das Audio über 4,5 s hinausreicht
        var detector = FakeSpeechDetector.Speech((0.5, 2.0), (3.0, 6.0));
        detector.FailAfter = Sec(4.5);
        var run = SessionRun.Live(decoder, detector, Options(LiveMode.Live));

        // Erste Äusserung fest; die zweite offen und als Vorschau sichtbar
        await run.FeedUntil(2.6);
        await run.Outputs.WaitFor(output => FixedTexts([output]).Contains("Erster Satz hier"), "erster fester Text");
        await run.Steps(3.5, 4.0, 4.5);
        await run.Outputs.WaitFor(output => ShowsPreview(output, "noch mehr Text"), "offene Äusserung verarbeitet");
        var calls = decoder.Calls.Count;

        // Der Block [4,5, 4,6) lässt Detect scheitern
        run.Feed.ReleaseUntil(Sec(4.6));
        var failure = await run.Outputs.Finish();

        // Der Lauf endet mit Fehler wie beim Decoderfehler, ohne Wiederholversuch
        Assert.NotNull(failure);
        Assert.IsNotAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, detector.Failures);

        // Seit E1 (03.11.2026) hat die offene Äusserung keine übernommenen Wörter: aus ihr wird nichts fest, auch nicht
        // die vorher gezeigte Vorschau; die schon feste Äusserung bleibt
        Assert.Equal(["Erster Satz hier"], FixedTexts(run.Outputs.Items));

        // Genau eine sichtbare Meldung: Verlust vom Beginn der offenen Äusserung bis AcceptedEnd samt Fehlerblock
        var hint = Assert.Single(run.Outputs.Items.OfType<HintOutput>());
        var notice = Assert.IsType<LiveNotice>(hint.Notice);
        Assert.Equal(LiveNoticeKind.DetectorFailed, notice.Kind);
        Assert.Equal(Sec(4.6), run.Source.AcceptedEnd);
        Assert.Equal((Sec(3), run.Source.AcceptedEnd), (notice.Start, notice.End));

        // Sonst kein weiterer Text und kein stilles Weiterlaufen ohne Sprachbereiche: kein weiterer Decode
        Assert.Equal(calls, decoder.Calls.Count);
    }

    /// <summary>Fixture T62: „a“ … „e“ in [0, 5) und dieselbe Folge ab <paramref name="repeatStart"/>.</summary>
    internal static IReadOnlyList<RecognizedWord> RepeatFixture(double repeatStart) =>
        [.. Words(0, 1, "a", "b", "c", "d", "e"), .. Words(repeatStart, 1, "a", "b", "c", "d", "e")];

    /// <summary>Die Prüfung selbst, ohne Überbrückung kurzer Lücken (T27): Wort [1, 2) gegen die Bereiche.</summary>
    private static bool HalfInRegions(params (double Start, double End)[] regions) => SpeechRegions.HalfInSpeech(
        SpeechRegions.Merge(regions.Select(region => new SpeechRegion(Sec(region.Start), Sec(region.End))), TimeSpan.Zero, Sec(4)),
        Word("Beispiel", 1, 2));

    private static async Task<IReadOnlyList<string>> WordsWithRegions(params (double Start, double End)[] regions)
    {
        var run = SourceRun.Live(
            FakeHypothesisDecoder.ByInterval([Word("Beispiel", 1, 2)]), FakeSpeechDetector.Speech(regions), Options(LiveMode.Sentence));
        return CompletedWords(await run.EndAt(4));
    }
}
