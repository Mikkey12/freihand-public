using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>Sitzung aus Erkennung, Auslegung und Formatierung; einzige Stelle, die festen Text veröffentlicht (Vertrag 6).</summary>
public sealed class DictationSessionTests
{
    [Fact]
    public async Task T01_IdIsFixedAtMostOnceAndNeverChangedLater()
    {
        var spoken = Words(0.5, 0.5, "Erstens", "kommt", "Alpha")
            .Concat(Words(4.0, 0.5, "zweitens", "kommt", "Beta"))
            .Concat(Words(8.0, 0.5, "drittens", "Gamma"))
            .ToList();
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken, partialAtEnd: word => "x" + word.Text),
            FakeSpeechDetector.Speech((0.5, 2.0), (4.0, 5.5), (8.0, 9.0)),
            Options(LiveMode.Live));

        var outputs = await run.EndAt(11);

        var fixedIds = new HashSet<Guid>();
        foreach (var update in outputs.OfType<TextOutput>().Select(output => output.Update))
        {
            foreach (var segment in update.Fixed)
            {
                Assert.Equal(SegmentState.Fixed, segment.State);
                Assert.True(fixedIds.Add(segment.Id), $"Kennung {segment.Id} zweimal fest gemeldet.");
            }

            // Kein Update nach dem Festwerden zeigt dieselbe Kennung wieder, auch nicht vorläufig
            Assert.DoesNotContain(update.Provisional, segment => fixedIds.Contains(segment.Id));
        }

        Assert.Equal(["Erstens kommt Alpha", "zweitens kommt Beta", "drittens Gamma"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T17_CancelCommandEndsInOrderAndDropsPendingDecodes()
    {
        var spoken = Words(0.5, 0.5, "Wir", "schreiben", "einen", "Brief")
            .Append(Word("abbrechen", 3.5, 4.1))
            .Concat(Words(5.0, 0.5, "noch", "mehr", "Text"))
            .ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);

        // Jeder Auftrag, der „abbrechen“ ganz enthält, wartet auf das Tor „command“; spätere Aufträge auf „pending“
        var command = new TestGate();
        var pending = new TestGate();
        decoder.Hold = request =>
            request.Contains(Sec(3.5), Sec(4.1)) ? command.Task
            : request.Overlaps(Sec(5.0), Sec(6.5)) ? pending.Task
            : Task.CompletedTask;
        var run = SessionRun.Live(
            decoder, FakeSpeechDetector.Speech((0.5, 2.5), (3.5, 4.1), (5.0, 6.5)), Options(LiveMode.Live));

        await run.FeedUntil(3.1);
        await run.Outputs.WaitFor(output => FixedTexts([output]).Count > 0, "erster fester Text");
        await run.PressCommandKeyAt(3.2);

        // Ausstehende Folgearbeit ausdrücklich herstellen (A03): „abbrechen“ ist noch nicht erkannt, die folgende
        // Äusserung ist schon angenommen, aber von keinem Decode erkannt
        await run.FeedUntil(6.0);
        await Until(() => decoder.Calls.Any(call => call.Request.Contains(Sec(3.5), Sec(4.1))), "Decode von „abbrechen“ läuft");
        Assert.Equal(Sec(6.0), run.Source.AcceptedEnd);
        Assert.DoesNotContain(decoder.Calls, call => call.Completed && call.Request.Overlaps(Sec(5.0), Sec(6.5)));
        Assert.DoesNotContain(run.Outputs.Items, output => output is CommandOutput);

        command.Open();
        await run.Outputs.WaitFor(output => output is CommandOutput, "Abbruchbefehl");
        pending.Open();
        var outputs = await run.EndAt(8);

        // Erst ein leeres Update ohne neue feste Abschnitte, dann der Befehl, danach nichts mehr
        Assert.Equal([CommandEffect.Cancel], Effects(outputs));
        var cleared = Assert.IsType<TextOutput>(outputs[^2]);
        Assert.Empty(cleared.Update.Fixed);
        Assert.Empty(cleared.Update.Provisional);
        Assert.Equal(CommandEffect.Cancel, Assert.IsType<CommandOutput>(outputs[^1]).Command.Command.Effect);
        Assert.Equal(["Wir schreiben einen Brief"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T18_DraftRecorderGetsOnlyFinalFixedSegmentsOnce()
    {
        var spoken = Words(0.5, 0.7, "Heute", "bleibt", "alles", "gleich").Concat(Words(5.0, 0.7, "Morgen", "kommt", "Neues")).ToList();
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken, partialAtEnd: word => "x" + word.Text),
            FakeSpeechDetector.Speech((0.5, 3.3), (5.0, 7.1)),
            Options(LiveMode.Live));

        // Vorbedingung (A01): Der Schritt bei 1 s schneidet „Heute“ an; diese Vorschau wird später verworfen
        await run.StepTo(1.0);
        await run.Outputs.WaitFor(output => ShowsPreview(output, "xHeute"), "angeschnittenes Wort als Vorschau");
        var outputs = await run.EndAt(9);

        Assert.Contains("xHeute", Previews(outputs));
        var segments = FixedSegments(outputs).ToList();
        Assert.All(segments, segment => Assert.Equal(SegmentState.Fixed, segment.State));
        Assert.Equal(segments.Count, segments.Select(segment => segment.Id).Distinct().Count());
        Assert.Equal(["Heute bleibt alles gleich", "Morgen kommt Neues"], FixedTexts(outputs));
        Assert.Equal("Heute bleibt alles gleich Morgen kommt Neues", DraftText(outputs));
    }

    [Fact]
    public async Task T19_AmountOverADecodeBoundaryIsFormattedAsAWhole()
    {
        var spoken = new[]
        {
            Word("Bitte", 0.5, 1.0), Word("1250", 1.0, 1.8), Word("Franken", 2.2, 2.8), Word("überweisen", 2.8, 3.6),
        };
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var formatter = Formatter();
        var run = SessionRun.Live(
            decoder,
            FakeSpeechDetector.Speech((0.5, 1.8), (2.2, 3.6)),
            Options(LiveMode.Live),
            interpreting: Interpreting(formatter: formatter));

        // Erwartete Schritte, jeder quittiert (A01, L01); die Lücke [1,8, 2,2) ist kürzer als die Pausenschwelle
        await run.Steps(1.0, 1.5, 2.0, 2.5, 3.0);
        await run.Outputs.WaitFor(output => ShowsPreview(output, "Bitte 1250 Franken"), "Schritt bei 3 s verarbeitet");
        var outputs = await run.EndAt(6);

        // Seit E1 (03.11.2026) entsteht die ganze Äusserung aus Aufträgen ab ihrem Beginn; formatiert wird je Äusserung,
        // über die Lücke zwischen „1250“ und „Franken“ hinweg
        Assert.All(decoder.Requests, request => Assert.Equal(Sec(0.5), request.Start));
        Assert.Equal([formatter.Format("Bitte 1250 Franken überweisen")], FixedTexts(outputs));
    }

    [Fact]
    public async Task T36_CommandHeardOverThreeStepsRunsOnceAndNextIsText()
    {
        RecognizedWord[] command = [Word("letztes", 1.0, 1.5), Word("Wort", 2.0, 2.5), Word("löschen", 3.0, 3.5)];
        RecognizedWord[] again = [Word("letztes", 7.0, 7.5), Word("Wort", 8.0, 8.5), Word("löschen", 9.0, 9.5)];
        var decoder = FakeHypothesisDecoder.ByInterval([.. command, .. again]);
        var run = SessionRun.Live(decoder, FakeSpeechDetector.Speech((1.0, 4.6), (7.0, 10.6)), Options(LiveMode.Live));

        await run.PressCommandKeyAt(0.5);

        // Erwartete Schritte, jeder quittiert (A01, L01): die drei Wörter kommen in drei Schritten hinzu; die Pause nach
        // 4,6 s ist bei 5,0 s noch nicht bestätigt
        await run.Steps(1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0);
        await run.Outputs.WaitFor(output => ShowsPreview(output, "letztes Wort löschen"), "Vorschau der scharf geschalteten Äusserung");
        var outputs = await run.EndAt(12);

        // Seit E1 (03.11.2026) beginnt jeder Auftrag der ersten Äusserung an ihrem Beginn; der Befehl wirkt erst mit dem
        // Abschluss, genau einmal
        Assert.All(decoder.Requests.Where(request => request.End <= Sec(4.6)), request => Assert.Equal(Sec(1.0), request.Start));

        Assert.Equal([CommandEffect.DeleteLastWord], Effects(outputs));
        var text = Assert.Single(FixedSegments(outputs));
        Assert.Equal("letztes Wort löschen", text.Text);
        Assert.True(text.Start >= Sec(7.0));
    }

    [Fact]
    public async Task T39_FormattedVersionReachesOutputAndDraft()
    {
        var decoder = FakeHypothesisDecoder.ByInterval([Word("1250", 0.5, 1.3), Word("Franken", 1.7, 2.3)]);
        var formatter = Formatter();
        var run = SessionRun.Live(
            decoder,
            FakeSpeechDetector.Speech((0.5, 1.3), (1.7, 2.3)),
            Options(LiveMode.Live),
            interpreting: Interpreting(formatter: formatter));

        // Erwartete Schritte, jeder quittiert (A01, L01)
        await run.Steps(1.0, 1.5, 2.0, 2.5);
        await run.Outputs.WaitFor(output => ShowsPreview(output, "1250 Franken"), "Schritt bei 2,5 s verarbeitet");
        var outputs = await run.EndAt(4);

        // Seit E1 (03.11.2026) entsteht die Äusserung aus Aufträgen ab ihrem Beginn
        Assert.All(decoder.Requests, request => Assert.Equal(Sec(0.5), request.Start));
        var expected = formatter.Format("1250 Franken");
        Assert.NotEqual("1250 Franken", expected);
        Assert.Equal([expected], FixedTexts(outputs));
        Assert.Equal(expected, DraftText(outputs));
    }

    [Fact]
    public async Task T40_LiteralModeOutputIsUnchanged()
    {
        var spoken = Words(0.5, 0.5, "wörtlich", "schreiben").Concat(Words(3.0, 0.5, "fünf", "Franken", "Komma")).ToList();
        var formatter = Formatter();
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken),
            FakeSpeechDetector.Speech((0.5, 1.5), (3.0, 4.5)),
            Options(LiveMode.Live),
            interpreting: Interpreting(formatter: formatter));

        await run.PressCommandKeyAt(0);
        var outputs = await run.EndAt(6);

        // Ohne Literalmodus würde P4 daraus etwas anderes machen
        Assert.NotEqual("fünf Franken Komma", formatter.Format("fünf Franken Komma"));
        Assert.Equal(["fünf Franken Komma"], FixedTexts(outputs));
        Assert.Equal("fünf Franken Komma", DraftText(outputs));
    }

    [Fact]
    public async Task T41_UpdatesCarryOnlyNewSegmentsAndOverlapDuplicatesNothing()
    {
        var spoken = Words(0.5, 0.5, "Guten", "Morgen", "allerseits")
            .Append(Word("rückgängig", 3.5, 4.2))
            .Concat(Words(5.5, 0.4, "Kaffee", "bitte"))
            .Concat(Words(7.0, 0.5, "Wir", "machen", "weiter"))
            .ToList();
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken),
            FakeSpeechDetector.Speech((0.5, 2.0), (3.5, 4.2), (5.5, 6.3), (7.0, 8.5)),
            Options(LiveMode.Live));

        // Erwartete Schritte, jeder quittiert (A01, L01), damit jede Äusserung vor ihrem Abschluss sicher eine Vorschau hat;
        // zwischen den Äusserungen wartet der Test, bis die vorige abgeschlossen ist, also keine Arbeit mehr aussteht
        await run.StepTo(1.0);
        await run.FeedUntil(2.8);
        await run.Outputs.WaitFor(output => FixedTexts([output]).Count > 0, "erste Äusserung fest");
        await run.PressCommandKeyAt(2.8);

        // Wiederanlauf mitten im Befehl: Audio ab 3,0 s kommt ein zweites Mal; der Schritt bei 4,5 s zeigt „rückgängig“
        await run.StepTo(4.0);
        run.Feed.Rewind(Sec(3.0));
        await run.StepTo(4.5);
        await run.Outputs.WaitFor(output => ShowsPreview(output, "rückgängig"), "Vorschau des Befehls");
        await run.FeedUntil(5.0);
        await run.Outputs.WaitFor(output => output is CommandOutput, "Befehl abgeschlossen");
        await run.PressCommandKeyAt(5.0);

        // Wiederanlauf mitten im Text, nachdem der Hinweis abgeschlossen ist
        await run.FeedUntil(7.0);
        await run.Outputs.WaitFor(output => output is HintOutput { Hint: not null }, "Hinweis abgeschlossen");
        await run.StepTo(7.5);
        await run.FeedUntil(8.0);
        run.Feed.Rewind(Sec(7.2));
        var outputs = await run.EndAt(10.5);

        var updates = outputs.OfType<TextOutput>().Select(output => output.Update).ToList();
        var fixedSegments = FixedSegments(outputs).ToList();
        Assert.Equal(["Guten Morgen allerseits", "Wir machen weiter"], fixedSegments.Select(segment => segment.Text));
        Assert.Equal(2, fixedSegments.Select(segment => segment.Id).Distinct().Count());
        Assert.Equal([CommandEffect.Undo], Effects(outputs));
        var hint = Assert.Single(Hints(outputs));
        Assert.Equal(["Kaffee", "bitte"], hint.Utterance.Words.Select(word => word.Text));

        // Vorläufig und fest dieselbe Kennung; Befehl und Hinweis ohne festen Abschnitt
        foreach (var segment in fixedSegments)
        {
            var fixedAt = updates.FindIndex(update => update.Fixed.Contains(segment));
            Assert.Contains(updates.Take(fixedAt), update => update.Provisional.Any(preview => preview.Id == segment.Id));
        }

        Assert.DoesNotContain(fixedSegments, segment => segment.Id == hint.Utterance.Id);

        // Auch die scharf geschaltete Äusserung zeigt ihren Rohtext als Vorschau (Kapitel 5.6), nie als festen Text
        var commandPreviews = updates
            .SelectMany(update => update.Provisional)
            .Where(preview => preview.Text.Contains("rückgängig", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(commandPreviews);
        Assert.DoesNotContain(fixedSegments, segment => commandPreviews.Any(preview => preview.Id == segment.Id));
    }

    [Fact]
    public async Task T46_DialectTranscriptionKeepsNegationAmountAndName()
    {
        var spoken = new[]
        {
            Word("Ich", 0, 0.4), Word("habe", 0.4, 0.8), Word("keine", 0.8, 1.2), Word("1250", 1.2, 1.8),
            Word("Franken", 1.8, 2.4), Word("an", 2.4, 2.6), Word("Frau", 2.6, 3.0), Word("Gerber", 3.0, 3.6),
            Word("überwiesen.", 3.6, 4.0),
        }.Concat(Words(5.0, 0.5, "letztes", "Wort", "löschen")).ToList();
        var formatter = Formatter();
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken),
            FakeSpeechDetector.Speech((0, 4.0), (5.0, 6.5)),
            Options(LiveMode.Live),
            interpreting: Interpreting(formatter: formatter));

        var outputs = await run.EndAt(8);

        Assert.Equal(
            [formatter.Format("Ich habe keine 1250 Franken an Frau Gerber überwiesen."), formatter.Format("letztes Wort löschen")],
            FixedTexts(outputs));
        Assert.Empty(Effects(outputs));
        Assert.Empty(Hints(outputs));
    }

    [Fact]
    public async Task T47_CommandKeyDuringAnOpenUtteranceSplitsAtAcceptedEnd()
    {
        var spoken = new[]
        {
            Word("Das", 0.5, 0.9), Word("war", 0.9, 1.3), Word("falsch", 1.3, 1.9),
            Word("letztes", 2.1, 2.6), Word("Wort", 2.6, 3.0), Word("löschen", 3.0, 3.6),
        };
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech((0.5, 3.6)), Options(LiveMode.Live));

        await run.PressCommandKeyAt(2.0);
        var outputs = await run.EndAt(5);

        Assert.Equal(["Das war falsch"], FixedTexts(outputs));
        Assert.Equal([CommandEffect.DeleteLastWord], Effects(outputs));
        Assert.Empty(Hints(outputs));
    }

    [Fact]
    public async Task T48_ArmingDependsOnUtteranceStartNotOnInterpretationTime()
    {
        // Taste bei 1 s; die Äusserung beginnt bei 5,5 s, also innerhalb von ArmTimeout, wird aber erst nach 9 s ausgelegt
        var decoder = FakeHypothesisDecoder.ByInterval(Words(5.5, 0.5, "letztes", "Wort", "löschen"));
        var late = new TestGate();
        decoder.Hold = _ => late.Task;
        var run = SessionRun.Live(decoder, FakeSpeechDetector.Speech((5.5, 7.0)), Options(LiveMode.Sentence));

        await run.PressCommandKeyAt(1);
        await run.FeedUntil(9);
        late.Open();
        var outputs = await run.EndAt(10);

        Assert.Equal([CommandEffect.DeleteLastWord], Effects(outputs));
        Assert.Empty(FixedSegments(outputs));
    }

    [Fact]
    public async Task T50_DictationMarkAsOwnUtteranceJoinsWithoutSpace()
    {
        var spoken = Words(0.5, 0.5, "Das", "ist", "gut").Append(Word("Komma", 3.0, 3.5)).Append(Word("danke", 4.5, 5.0)).ToList();
        var run = SessionRun.Live(
            FakeHypothesisDecoder.ByInterval(spoken),
            FakeSpeechDetector.Speech((0.5, 2.0), (3.0, 3.5), (4.5, 5.0)),
            Options(LiveMode.Live));

        var outputs = await run.EndAt(6.5);

        // Ausgabe über die Kernfunktion der Fügeregel (Kapitel 3), Entwurf über P5
        Assert.Equal(["Das ist gut", ",", "danke"], FixedTexts(outputs));
        Assert.Equal("Das ist gut, danke", TextJoin.Join(FixedTexts(outputs)));
        Assert.Equal("Das ist gut, danke", DraftText(outputs));
    }

    [Fact]
    public async Task T51_ArmedUtteranceWithoutWordsUsesTheKeyAndGivesNoHint()
    {
        // Taste bei 0,5 s; die nächste Äusserung [1, 2) ist ein Husten ohne Wörter, dann „letztes Wort löschen“ ab 3,5 s,
        // noch innerhalb von ArmTimeout nach der Taste
        var decoder = FakeHypothesisDecoder.ByInterval(Words(3.5, 0.5, "letztes", "Wort", "löschen"));
        var run = SessionRun.Live(decoder, FakeSpeechDetector.Speech((1, 2), (3.5, 5)), Options(LiveMode.Sentence));

        await run.PressCommandKeyAt(0.5);
        var outputs = await run.EndAt(7);

        // Die Äusserung ohne Wörter schliesst die Aktivierung ab, ohne Text und ohne Hinweis (Kapitel 5.1, Regel 2);
        // die nächste ist darum Text
        Assert.DoesNotContain(outputs, output => output is HintOutput);
        Assert.Empty(Effects(outputs));
        Assert.Equal(["letztes Wort löschen"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T54_KeysAreEvaluatedInAudioOrderNotDecodeOrder()
    {
        var blocked = await LiteralThenKeyAsync(holdFirstComma: true);
        var immediate = await LiteralThenKeyAsync(holdFirstComma: false);

        Assert.Equal(["Komma", ","], FixedTexts(blocked));
        Assert.Equal("Komma,", TextJoin.Join(FixedTexts(blocked)));
        Assert.Equal("Komma,", DraftText(blocked));
        Assert.Empty(Hints(blocked));
        Assert.All(Effects(blocked), effect => Assert.Equal(CommandEffect.EnterLiteralMode, effect));

        // Derselbe Ablauf mit sofortigem Decoder ergibt dieselbe Ausgabe
        Assert.Equal(FixedTexts(blocked), FixedTexts(immediate));
        Assert.Equal(Effects(blocked), Effects(immediate));
    }

    [Fact]
    public async Task T54_LateLiteralCommandDoesNotReenableAfterALaterKey()
    {
        var spoken = Words(1.0, 0.5, "wörtlich", "schreiben").Append(Word("Komma", 3.5, 4.0)).ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var late = new TestGate();
        decoder.Hold = request => request.Overlaps(Sec(1.0), Sec(2.0)) ? late.Task : Task.CompletedTask;
        var run = SessionRun.Live(decoder, FakeSpeechDetector.Speech((1.0, 2.0), (3.5, 4.0)), Options(LiveMode.Sentence));

        // Scharf geschaltetes „wörtlich schreiben“; sein Decode wird erst nach der späteren Taste fertig
        await run.PressCommandKeyAt(0.5);
        await run.PressCommandKeyAt(3.0);
        await run.FeedUntil(4.6);
        late.Open();
        var outputs = await run.EndAt(5);

        Assert.Equal([","], FixedTexts(outputs));
        Assert.False(run.Interpreter.LiteralMode);
    }

    [Fact]
    public async Task T55_CancelClearsPreviewThenCommandThenNothing()
    {
        var (run, pending, _) = await ArmedCancelWithPreviewAsync(CancellationToken.None);

        run.Feed.ReleaseUntil(Sec(6.0));
        await run.Outputs.WaitFor(output => output is CommandOutput, "Abbruchbefehl");
        pending.Open();
        var outputs = await run.EndAt(7);

        Assert.Equal([CommandEffect.Cancel], Effects(outputs));
        var cleared = Assert.IsType<TextOutput>(outputs[^2]);
        Assert.Empty(cleared.Update.Fixed);
        Assert.Empty(cleared.Update.Provisional);
        Assert.Equal(CommandEffect.Cancel, Assert.IsType<CommandOutput>(outputs[^1]).Command.Command.Effect);
        Assert.Equal(["Erster Teil fertig"], FixedTexts(outputs));
    }

    [Fact]
    public async Task T55_CancellationTokenStopsAllOutput()
    {
        using var cancellation = new CancellationTokenSource();
        var (run, pending, _) = await ArmedCancelWithPreviewAsync(cancellation.Token);

        var before = run.Outputs.Count;
        await cancellation.CancelAsync();
        await run.Outputs.Finish();
        pending.Open();
        run.Feed.ReleaseUntil(Sec(7));
        run.Feed.Complete();

        Assert.Equal(before, run.Outputs.Count);
        Assert.Equal(["Erster Teil fertig"], FixedTexts(run.Outputs.Items));
    }

    [Fact]
    public async Task T55_LateDeliveryAfterTheCancelCommandIsSuppressed()
    {
        // Steuerbare Quelle (A03): Sie liefert nach dem Abbruchbefehl absichtlich weiter
        var source = new ScriptedTranscriptSource();
        var run = new SessionRun(source);
        var first = Utterance(0.5, "Erster", "Teil", "fertig");
        source.Emit(new ProvisionalText(first.Id, "Erster Teil fertig"));
        source.Emit(new UtteranceCompleted(first));
        await run.Outputs.WaitFor(output => FixedTexts([output]).Count > 0, "erster fester Text");

        // Taste, Vorschau der scharf geschalteten Äusserung, dann „abbrechen“
        await run.PressCommandKeyAt(2.8);
        var cancel = Utterance(3.0, "abbrechen");
        source.Emit(new ProvisionalText(cancel.Id, "abbrechen"));
        await run.Outputs.WaitFor(output => ShowsPreview(output, "abbrechen"), "Vorschau vorhanden");
        source.Emit(new UtteranceCompleted(cancel));
        await run.Outputs.WaitFor(output => output is CommandOutput, "Abbruchbefehl");

        // Späte Lieferung nach dem Befehl; beide Abläufe sind erlaubt und werden laufend beobachtet (L02): Die Sitzung holt
        // alles ab und unterdrückt es, oder sie gibt die Quelle frei, auch erst nach dem ersten späten Ereignis
        var delivered = source.Delivered;
        var late = Utterance(4.4, "Nachtrag", "kommt", "noch");
        source.Emit(new ProvisionalText(late.Id, "Nachtrag kommt noch"));
        source.Emit(new LiveNotice(LiveNoticeKind.SpeechWithoutText, Sec(5.6), Sec(6.0)));
        source.Emit(new UtteranceCompleted(late));
        await Until(() => source.LateDeliverySettled(delivered, 3), "späte Ereignisse abgeholt oder Quelle freigegeben");
        var lateDelivered = source.Delivered - delivered;
        Assert.True(source.Released || lateDelivered == 3, $"{lateDelivered} späte Ereignisse zugestellt, Quelle nicht frei");

        source.End();
        var outputs = await run.EndAt(7);

        // Unterdrückt, gleich in welchem Ablauf: nach dem Befehl nichts mehr, keine Spur der späten Ereignisse
        Assert.Equal([CommandEffect.Cancel], Effects(outputs));
        var cleared = Assert.IsType<TextOutput>(outputs[^2]);
        Assert.Empty(cleared.Update.Fixed);
        Assert.Empty(cleared.Update.Provisional);
        Assert.Equal(CommandEffect.Cancel, Assert.IsType<CommandOutput>(outputs[^1]).Command.Command.Effect);
        Assert.Equal(["Erster Teil fertig"], FixedTexts(outputs));
        Assert.DoesNotContain(Previews(outputs), preview => preview.Contains("Nachtrag", StringComparison.Ordinal));
        Assert.Empty(Notices(outputs, LiveNoticeKind.SpeechWithoutText));
    }

    [Fact]
    public async Task T55_LateDeliveryAfterCancellationIsSuppressed()
    {
        // Steuerbare Quelle (A03): Sie übergeht den Abbruch und liefert danach noch
        using var cancellation = new CancellationTokenSource();
        var source = new ScriptedTranscriptSource();
        var run = new SessionRun(source, cancellationToken: cancellation.Token);
        var first = Utterance(0.5, "Erster", "Teil", "fertig");
        source.Emit(new UtteranceCompleted(first));
        await run.Outputs.WaitFor(output => FixedTexts([output]).Count > 0, "erster fester Text");
        var second = Utterance(3.0, "Zweiter", "Teil");
        source.Emit(new ProvisionalText(second.Id, "Zweiter Teil"));
        await run.Outputs.WaitFor(output => ShowsPreview(output, "Zweiter Teil"), "Vorschau vorhanden");

        // Die Sitzung wartet auf das nächste Ereignis, dann der Abbruch
        await Until(() => source.Waiting, "Sitzung wartet auf die Quelle");
        var before = run.Outputs.Count;
        var delivered = source.Delivered;
        await cancellation.CancelAsync();
        source.Emit(new UtteranceCompleted(second));

        // Die Sitzung wartete auf die Quelle; das späte Ereignis geht an dieses Abholen, ausser sie hat die Quelle schon
        // freigegeben (beide Abläufe erlaubt, L02)
        await Until(() => source.LateDeliverySettled(delivered, 1), "spätes Ereignis geliefert oder Quelle freigegeben");
        await run.Outputs.Finish();
        source.End();

        Assert.Equal(before, run.Outputs.Count);
        Assert.Equal(["Erster Teil fertig"], FixedTexts(run.Outputs.Items));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task T56_LineBreaksJoinExactly(int variant)
    {
        var (spoken, regions, segments, joined) = variant switch
        {
            0 => (
                Words(0.5, 0.5, "Alpha").Concat(Words(2.0, 0.5, "neue", "Zeile")).Concat(Words(4.0, 0.5, "Beta")),
                new[] { (0.5, 1.0), (2.0, 3.0), (4.0, 4.5) },
                new[] { "Alpha", "\n", "Beta" },
                "Alpha\nBeta"),
            1 => (
                Words(0.5, 0.5, "Alpha").Concat(Words(2.0, 0.5, "neuer", "Absatz")).Concat(Words(4.0, 0.5, "Beta")),
                new[] { (0.5, 1.0), (2.0, 3.0), (4.0, 4.5) },
                new[] { "Alpha", "\n\n", "Beta" },
                "Alpha\n\nBeta"),
            _ => (
                Words(0.5, 0.5, "Alpha", "neue", "Zeile").Concat(Words(3.0, 0.5, "Beta")),
                new[] { (0.5, 2.0), (3.0, 3.5) },
                new[] { "Alpha\n", "Beta" },
                "Alpha\nBeta"),
        };
        var run = SessionRun.Live(FakeHypothesisDecoder.ByInterval(spoken), FakeSpeechDetector.Speech(regions), Options(LiveMode.Live));

        var outputs = await run.EndAt(6);

        // Ausgabe über die Kernfunktion der Fügeregel (Kapitel 3), Entwurf über P5
        Assert.Equal(segments, FixedTexts(outputs));
        Assert.Equal(joined, TextJoin.Join(FixedTexts(outputs)));
        Assert.Equal(joined, DraftText(outputs));
        Assert.Empty(Effects(outputs));
    }

    /// <summary>
    /// T54: Literalmodus über eine Taste bei 0 s, dann „Komma“ in [1, 2), Taste bei <c>AcceptedEnd</c> 2 s, „Komma“ in
    /// [2,5, 3). Der Abschluss-Decode des ersten „Komma“ liefert auf Wunsch erst, nachdem das zweite gesprochen ist.
    /// </summary>
    private static async Task<IReadOnlyList<DictationOutput>> LiteralThenKeyAsync(bool holdFirstComma)
    {
        var spoken = new[] { Word("wörtlich", 0.1, 0.2), Word("schreiben", 0.2, 0.3), Word("Komma", 1.0, 2.0), Word("Komma", 2.5, 3.0) };
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var late = new TestGate();
        decoder.Hold = request => holdFirstComma && request.Overlaps(Sec(1.0), Sec(2.0)) ? late.Task : Task.CompletedTask;
        var run = SessionRun.Live(
            decoder, FakeSpeechDetector.Speech((0.1, 0.3), (1.0, 2.0), (2.5, 3.0)), Options(LiveMode.Sentence));

        await run.PressCommandKeyAt(0);
        await run.PressCommandKeyAt(2.0);
        await run.FeedUntil(3.6);
        late.Open();
        return await run.EndAt(4.5);
    }

    /// <summary>
    /// T55: fester Text, Taste, scharf geschaltetes „abbrechen“ mit Vorschau; der Decode der folgenden Äusserung
    /// liefert erst, wenn der Test das Tor öffnet.
    /// </summary>
    private static async Task<(SessionRun Run, TestGate Pending, FakeHypothesisDecoder Decoder)> ArmedCancelWithPreviewAsync(
        CancellationToken cancellationToken)
    {
        var spoken = Words(0.5, 0.5, "Erster", "Teil", "fertig")
            .Append(Word("abbrechen", 3.0, 3.4))
            .Concat(Words(4.4, 0.4, "Nachtrag", "kommt", "noch"))
            .ToList();
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var pending = new TestGate();
        decoder.Hold = request => request.Overlaps(Sec(4.4), Sec(5.6)) ? pending.Task : Task.CompletedTask;
        var run = SessionRun.Live(
            decoder,
            FakeSpeechDetector.Speech((0.5, 2.0), (3.0, 3.4), (4.4, 5.6)),
            Options(LiveMode.Live),
            cancellationToken: cancellationToken);

        await run.FeedUntil(2.6);
        await run.Outputs.WaitFor(output => FixedTexts([output]).Count > 0, "erster fester Text");
        await run.PressCommandKeyAt(2.8);
        run.Phase = 1;

        // Schrittweise (A01): der Schritt bei 3,5 s erkennt „abbrechen“; sein Rohtext erscheint als Vorschau (Kapitel 5.6)
        await run.StepTo(3.5);
        await Until(
            () => run.Outputs.Stamped.Any(item =>
                item.Phase == 1 && item.Item is TextOutput text && text.Update.Provisional.Any(segment => segment.Text.Length > 0)),
            "Vorschau vorhanden");
        return (run, pending, decoder);
    }
}
