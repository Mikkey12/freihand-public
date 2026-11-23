using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>Schutz vor erfundenem Text und Wiederholungen (ER-16, Kapitel 5.5).</summary>
public sealed class HallucinationTests
{
    [Fact]
    public async Task T05_WordsMostlyOutsideSpeechNeverAppear()
    {
        var spoken = new[]
        {
            Word("Rauschen", 0.0, 0.6), // nichts in Sprache
            Word("fast", 0.6, 1.2), // ein Drittel in Sprache
            Word("Guten", 1.2, 1.8),
            Word("Tag", 1.8, 2.4),
            Word("allerseits", 2.4, 3.0),
            Word("Nachhall", 3.0, 3.4), // ein Viertel in Sprache
        };
        var run = SessionRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1.0, 3.1)), Options(LiveMode.Live));

        var outputs = await run.EndAt(5);

        Assert.DoesNotContain(PreviewWords(outputs), word => word is "Rauschen" or "fast" or "Nachhall");
        Assert.Equal(["Guten Tag allerseits"], FixedTexts(outputs));
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T77_WordInAPauseShorterThanTheThresholdStays(LiveMode mode)
    {
        // Whisper legt „Danach“ ganz in die Lücke von 0,52 s zwischen zwei Sprachbereichen derselben Äusserung
        var spoken = new[]
        {
            Word("Guten", 1.0, 1.5),
            Word("Tag.", 1.5, 2.0),
            Word("Danach", 2.0, 2.5),
            Word("gehen", 2.6, 3.0),
            Word("wir", 3.0, 3.4),
        };
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1.0, 2.0), (2.52, 3.4)), Options(mode));

        var outputs = await run.EndAt(6);

        Assert.Equal(["Guten Tag. Danach gehen wir"], FixedTexts(outputs));
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T77_APauseOfTheFullThresholdIsStillSilence(LiveMode mode)
    {
        var spoken = new[]
        {
            Word("Guten", 1.0, 1.5),
            Word("Tag", 1.5, 2.0),
            Word("erfunden", 2.05, 2.55),
            Word("Weiter", 2.7, 3.2),
        };
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1.0, 2.0), (2.6, 3.2)), Options(mode));

        var outputs = await run.EndAt(6);

        Assert.DoesNotContain("erfunden", string.Join(" ", FixedTexts(outputs)));
        Assert.DoesNotContain(PreviewWords(outputs), word => word == "erfunden");
    }

    [Fact]
    public async Task T77_ANoiseMarkInAShortPauseStaysOut()
    {
        var spoken = new[]
        {
            Word("Guten", 1.0, 1.5),
            Word("Tag", 1.5, 2.0),
            Word("[Musik]", 2.0, 2.5),
            Word("allerseits", 2.6, 3.2),
        };
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1.0, 2.0), (2.5, 3.2)), Options(LiveMode.Sentence));

        var outputs = await run.EndAt(6);

        Assert.Equal(["Guten Tag allerseits"], FixedTexts(outputs));
    }

    [Theory]
    [InlineData(2.59, 1)]
    [InlineData(2.6, 2)]
    [InlineData(2.61, 2)]
    public void T77_OnlyGapsBelowTheThresholdAreBridged(double nextStart, int regions)
    {
        var merged = new List<SpeechRegion> { new(Sec(1), Sec(2)), new(Sec(nextStart), Sec(3)) };

        Assert.Equal(regions, SpeechRegions.BridgeShortGaps(merged, Sec(0.6)).Count);
    }

    [Fact]
    public void T77_ChainedShortGapsBecomeOneRegion()
    {
        var merged = new List<SpeechRegion> { new(Sec(1), Sec(2)), new(Sec(2.3), Sec(3)), new(Sec(3.2), Sec(4)), new(Sec(5), Sec(6)) };

        Assert.Equal([new(Sec(1), Sec(4)), new SpeechRegion(Sec(5), Sec(6))], SpeechRegions.BridgeShortGaps(merged, Sec(0.6)));
    }

    [Fact]
    public async Task T77_NoRegionFollowsSoTheEndOfSpeechStaysOutside()
    {
        // Nur Lücken zwischen zwei Bereichen gelten als Sprache, nicht der Nachhall hinter dem letzten
        var spoken = new[] { Word("Guten", 1.0, 1.5), Word("Tag", 1.5, 2.0), Word("Nachhall", 2.1, 2.5) };
        var run = SessionRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1.0, 2.0)), Options(LiveMode.Sentence));

        var outputs = await run.EndAt(6);

        Assert.Equal(["Guten Tag"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T06_TextBeforeTheFirstSpeechRegionNeverAppears()
    {
        var spoken = new[]
        {
            Word("Untertitel", 0.2, 0.8), Word("im", 0.8, 1.0), Word("Auftrag", 1.0, 1.4), Word("Vorspann", 1.45, 1.95),
            Word("Guten", 2.0, 2.5), Word("Morgen", 2.5, 3.0), Word("zusammen", 3.0, 3.5),
        };
        var run = SessionRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((2.0, 3.5)), Options(LiveMode.Live));

        var outputs = await run.EndAt(5.5);

        Assert.DoesNotContain(PreviewWords(outputs), word => word is "Untertitel" or "im" or "Auftrag" or "Vorspann");
        Assert.Equal(["Guten Morgen zusammen"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T07_KnownPhraseDropsOnlyAsWholeUnsureUtterance()
    {
        var spoken = new[]
        {
            // Floskel als ganze Äusserung, mittlere Wahrscheinlichkeit 0,3
            Word("Danke", 1.0, 1.5, 0.2f), Word("fürs", 1.5, 2.0, 0.4f), Word("Zuhören", 2.0, 2.5, 0.3f),

            // Dieselbe Floskel mitten in einer Äusserung
            Word("Wir", 4.0, 4.5), Word("sagen", 4.5, 5.0),
            Word("Danke", 5.0, 5.5, 0.2f), Word("fürs", 5.5, 6.0, 0.4f), Word("Zuhören", 6.0, 6.5, 0.3f),
            Word("und", 6.5, 7.0), Word("gehen", 7.0, 7.5),

            // Geräuschmarke, sicher erkannt
            Word("Weiter", 9.0, 9.5), Word("[Musik]", 9.5, 10.0), Word("geht", 10.0, 10.5), Word("es", 10.5, 11.0),
        };
        var options = Options(LiveMode.Live) with { Filter = new HallucinationFilter(["Danke fürs Zuhören"], 0.5f) };
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1.0, 2.5), (4.0, 7.5), (9.0, 11.0)), options);

        var outputs = await run.EndAt(13);

        Assert.Equal(["Wir sagen Danke fürs Zuhören und gehen", "Weiter geht es"], FixedTexts(outputs));
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T87_NoiseMarkOverSeveralWordsAlwaysDrops(LiveMode mode)
    {
        // Messung vom 09.11.2026: Whisper liefert eine Geräuschmarke über zwei Wörter, je Wort geprüft blieb sie stehen
        var spoken = new[]
        {
            Word("Weiter", 1.0, 1.5), Word("*Applaus", 1.5, 1.7), Word("im", 1.7, 1.8), Word("Saal*", 1.8, 2.0), Word("geht", 2.0, 2.5), Word("es", 2.5, 3.0),
            Word("Dann", 5.0, 5.5), Word("[Musik", 5.5, 5.8), Word("spielt]", 5.8, 6.0), Word(".", 6.0, 6.01), Word("Schluss", 6.1, 6.5),

            // Ohne Abschluss keine Marke
            Word("Das", 9.0, 9.5), Word("*wichtige", 9.5, 10.0), Word("Wort", 10.0, 10.5),
        };
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1.0, 3.0), (5.0, 6.5), (9.0, 10.5)), Options(mode));

        var outputs = await run.EndAt(12);

        Assert.Equal(["Weiter geht es", "Dann Schluss", "Das *wichtige Wort"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T12_SentenceModeDecodesEachUtteranceOnceWithoutPreview()
    {
        var spoken = Words(1.0, 0.5, "Erstens", "kommt", "das").Concat(Words(7.0, 0.5, "Drittens", "kommt", "dies")).ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);

        // Der mittlere Sprachbereich enthält keine Wörter, etwa ein Husten
        var run = SessionRun.Live(
            decoder, FakeSpeechDetector.Speech((1.0, 2.5), (4.0, 5.0), (7.0, 8.5)), Options(LiveMode.Sentence));

        var outputs = await run.EndAt(10);

        Assert.Single(decoder.Requests, request => request.Overlaps(Sec(1.0), Sec(2.5)));
        Assert.Single(decoder.Requests, request => request.Overlaps(Sec(7.0), Sec(8.5)));
        var updates = outputs.OfType<TextOutput>().Select(output => output.Update).ToList();
        Assert.All(updates, update => Assert.Empty(update.Provisional));
        Assert.Equal(2, updates.Count(update => update.Fixed.Count > 0));
        Assert.Equal(["Erstens kommt das", "Drittens kommt dies"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T26_UnsureLookingPhraseInSpeechStaysWhenSure()
    {
        // Kurze Dankesformel ganz in Sprache, Wahrscheinlichkeit 0,9; der Sprachbereich läuft über mehrere Schritte weiter
        var decoder = FakeHypothesisDecoder.ByInterval(Words(1.0, 0.4, "Vielen", "Dank"));
        var options = Options(LiveMode.Live) with { Filter = new HallucinationFilter(["Vielen Dank"], 0.5f) };
        var run = SessionRun.Live(decoder, FakeSpeechDetector.Speech((1.0, 3.0)), options);

        // Erwartete Schritte, jeder quittiert (A01, L01): 2,0 und 2,5 s liefern beide „Vielen Dank“, danach 3,0 s
        await run.Steps(1.5, 2.0);
        await run.Outputs.WaitFor(output => ShowsPreview(output, "Vielen Dank"), "erste vollständige Hypothese verarbeitet");
        await run.Steps(2.5, 3.0);
        var outputs = await run.EndAt(5);

        // Seit E1 (03.11.2026) entsteht der feste Text aus dem Abschluss-Decode ab Äusserungsbeginn; die sichere Formel
        // bleibt
        Assert.All(decoder.Requests, request => Assert.Equal(Sec(1.0), request.Start));
        Assert.Equal(["Vielen Dank"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T26_SamePhraseOutsideSpeechNeverAppears()
    {
        var spoken = Words(1.0, 0.5, "Guten", "Abend").Concat(Words(2.2, 0.4, "Vielen", "Dank")).ToList();
        var options = Options(LiveMode.Live) with { Filter = new HallucinationFilter(["Vielen Dank"], 0.5f) };
        var run = SessionRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((1.0, 2.0)), options);

        var outputs = await run.EndAt(5);

        Assert.DoesNotContain(PreviewWords(outputs), word => word is "Vielen" or "Dank");
        Assert.Equal(["Guten Abend"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T35_DecoderIgnoringCancellationIsDiscarded()
    {
        var spoken = Words(0.5, 0.5, "Erster", "Satz", "hier").Concat(Words(3.0, 0.4, "letztes", "Wort", "löschen")).ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var late = new TestGate();
        decoder.Hold = request => request.Overlaps(Sec(3.0), Sec(4.2)) ? late.Task : Task.CompletedTask;
        decoder.HonorCancellation = false;
        using var cancellation = new CancellationTokenSource();
        var run = SessionRun.Live(
            decoder, FakeSpeechDetector.Speech((0.5, 2.0), (3.0, 4.2)), Options(LiveMode.Live), cancellationToken: cancellation.Token);

        await run.FeedUntil(2.6);
        await run.Outputs.WaitFor(output => FixedTexts([output]).Count > 0, "erster fester Text");
        await run.PressCommandKeyAt(2.8);
        await run.FeedUntil(4.8);
        await Until(() => decoder.Calls.Any(call => call.Request.Overlaps(Sec(3.0), Sec(4.2))), "Decode der zweiten Äusserung läuft");
        await Settle();
        var before = run.Outputs.Count;

        // Abbruch; der Decoder liefert trotzdem
        await cancellation.CancelAsync();
        late.Open();
        await run.Outputs.Finish();
        run.Feed.Complete();
        await Settle();

        Assert.Equal(before, run.Outputs.Count);
        Assert.Equal(["Erster Satz hier"], FixedTexts(run.Outputs.Items));
        Assert.Empty(Effects(run.Outputs.Items));
    }

    [Fact]
    public async Task T62_OverlappingRepeatIsRemovedWithoutDecoderFailure()
    {
        var decoder = FakeHypothesisDecoder.ByInterval(LiveTranscriptionTests.RepeatFixture(4.5));
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 9.5)), Options(LiveMode.Sentence));

        var events = await run.EndAt(9.5);

        Assert.Empty(Notices(events, LiveNoticeKind.DecoderFailed));
        Assert.Equal(Words(0, 1, "a", "b", "c", "d", "e"), Completed(events).SelectMany(utterance => utterance.Words));
    }

    [Fact]
    public async Task T62_RepeatInNewAudioStays()
    {
        var fixture = LiveTranscriptionTests.RepeatFixture(5);
        var run = SourceRun.Live(
            FakeHypothesisDecoder.ByInterval(fixture), FakeSpeechDetector.Speech((0, 10)), Options(LiveMode.Sentence));

        var events = await run.EndAt(10);

        Assert.Empty(Notices(events, LiveNoticeKind.DecoderFailed));
        Assert.Equal(fixture, Completed(events).SelectMany(utterance => utterance.Words));
    }

    private static IEnumerable<string> PreviewWords(IEnumerable<DictationOutput> outputs) =>
        outputs.OfType<TextOutput>()
            .SelectMany(output => output.Update.Provisional.Concat(output.Update.Fixed))
            .SelectMany(segment => segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
