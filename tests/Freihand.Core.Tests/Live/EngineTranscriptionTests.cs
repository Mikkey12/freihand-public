using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.Fakes.FakeRecognitionEngine;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>Quellen über <see cref="IRecognitionEngine"/> ohne Wort-Hypothesen (Kapitel 3, Andere Quellen; Vertrag 4).</summary>
public sealed class EngineTranscriptionTests
{
    [Fact]
    public async Task T57_CommandOverTwoFixedDeltasRunsOnceAfterCompletion()
    {
        var engine = new FakeRecognitionEngine();
        engine.Schedule(1.0, Fixed(Segment("letztes", 0, 1)));
        engine.Schedule(2.0, Fixed(Segment("Wort löschen", 1, 2)));
        engine.Schedule(2.0, Completion(2.0));
        var run = Session(engine, (0, 2));

        await run.PressCommandKeyAt(0);
        await run.FeedUntil(2.0);
        await Until(() => engine.Pushed == 3, "Updates geliefert");
        await Settle();

        // Ohne Pause steht die Grenze noch nicht: weder Hinweis noch Text
        Assert.DoesNotContain(run.Outputs.Items, output => HasText(output) || output is HintOutput || output is CommandOutput);

        await run.FeedUntil(2.6);
        await run.Outputs.WaitFor(output => output is CommandOutput, "Befehl nach der Pause");
        var outputs = await run.EndAt(3.0);

        Assert.Equal([CommandEffect.DeleteLastWord], Effects(outputs));
        Assert.Empty(Hints(outputs));
        Assert.Empty(FixedSegments(outputs));
    }

    [Fact]
    public async Task T57_AmountOverTwoFixedDeltasIsFormattedAsOneSegment()
    {
        var engine = new FakeRecognitionEngine();
        engine.Schedule(1.0, Fixed(Segment("1250", 0, 1)));
        engine.Schedule(2.0, Fixed(Segment("Franken", 1, 2)));
        engine.Schedule(2.0, Completion(2.0));
        var formatter = Formatter();
        var run = Session(engine, (0, 2), Interpreting(formatter: formatter));

        var outputs = await run.EndAt(3.0);

        Assert.Equal([formatter.Format("1250 Franken")], FixedTexts(outputs));
    }

    [Fact]
    public async Task T61_EmptyPreviewAndPauseDoNotCloseBeforeCompletion()
    {
        var engine = new FakeRecognitionEngine();
        engine.Schedule(1.0, Fixed(Segment("letztes", 0, 1), completeUntil: 1.0));
        var run = Session(engine, (0, 2));

        await run.PressCommandKeyAt(0);

        // Audio bis 2,6 s: die Pause ist bestätigt, die Quelle aber erst bis 1 s
        await run.FeedUntil(2.6);
        await Until(() => engine.Pushed == 1, "erstes Delta geliefert");
        await Settle();
        Assert.DoesNotContain(run.Outputs.Items, output => HasText(output) || output is HintOutput || output is CommandOutput);

        engine.Push(Fixed(Segment("Wort löschen", 1, 2), completeUntil: 2.0));
        await run.Outputs.WaitFor(output => output is CommandOutput, "Befehl nach der Zusage");
        var delayed = await run.EndAt(3.0);

        Assert.Equal([CommandEffect.DeleteLastWord], Effects(delayed));
        Assert.Empty(Hints(delayed));
        Assert.Empty(FixedSegments(delayed));

        // Gleiches Ergebnis mit sofort antwortender Engine
        var immediateEngine = new FakeRecognitionEngine();
        immediateEngine.Schedule(1.0, Fixed(Segment("letztes", 0, 1), completeUntil: 1.0));
        immediateEngine.Schedule(2.0, Fixed(Segment("Wort löschen", 1, 2), completeUntil: 2.0));
        var immediateRun = Session(immediateEngine, (0, 2));
        await immediateRun.PressCommandKeyAt(0);
        var immediate = await immediateRun.EndAt(3.0);

        Assert.Equal(Effects(delayed), Effects(immediate));
        Assert.Equal(FixedTexts(delayed), FixedTexts(immediate));
        Assert.Equal(Hints(delayed).Count, Hints(immediate).Count);
    }

    [Fact]
    public async Task T65_SegmentOverTheKeyBoundaryIsSplitByWord()
    {
        var engine = new FakeRecognitionEngine();
        var run = Session(engine, (0, 4));

        await run.PressCommandKeyAt(2.0);

        // Erst nach der Pause liefert die Engine einen Abschnitt über beide Äusserungen
        await run.FeedUntil(4.6);
        engine.Push(Fixed(Segment("Alpha abbrechen", 0, 4), completeUntil: 4.0));
        await run.Outputs.WaitFor(output => output is CommandOutput, "Abbruchbefehl");
        var outputs = await run.EndAt(5.0);

        Assert.Equal(["Alpha"], FixedTexts(outputs));
        Assert.Equal([CommandEffect.Cancel], Effects(outputs));
        var cleared = Assert.IsType<TextOutput>(outputs[^2]);
        Assert.Empty(cleared.Update.Fixed);
        Assert.Empty(cleared.Update.Provisional);
        Assert.Equal(CommandEffect.Cancel, Assert.IsType<CommandOutput>(outputs[^1]).Command.Command.Effect);
        Assert.DoesNotContain(
            outputs.OfType<TextOutput>().SelectMany(output => output.Update.Fixed.Concat(output.Update.Provisional)),
            segment => segment.Text.Contains("abbrechen", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task T66_CompletionNeverDecreasesAndInvalidSegmentsFail(int violation)
    {
        var engine = new FakeRecognitionEngine();
        engine.Schedule(2.0, Fixed(Segment("letztes Wort", 0, 2)));
        var run = Session(engine, (0, 2));

        await run.FeedUntil(2.6);
        await Until(() => engine.Pushed == 1, "Delta geliefert");
        await UntilProcessed(run.Source, 1);

        // 1 s, ohne Wert, 0,5 s, 2 s: erst 2 s schliesst die Äusserung. Statt eines Zeitfensters wartet der Test an der
        // Verarbeitungsbarriere (Kapitel 6), bis die Quelle jedes Update vollständig verarbeitet hat (L01)
        double?[] completions = [1.0, null, 0.5, 2.0];
        foreach (var completion in completions)
        {
            run.Phase++;
            engine.Push(Completion(completion));
            await UntilProcessed(run.Source, run.Phase + 1);
        }

        await run.Outputs.WaitFor(output => FixedTexts([output]).Count > 0, "fest nach der Zusage 2 s");
        Assert.All(
            run.Outputs.Stamped.Where(item => FixedTexts([item.Item]).Count > 0),
            item => Assert.Equal(completions.Length, item.Phase));
        Assert.Equal(["letztes Wort"], FixedTexts(run.Outputs.Items));

        // Ein Abschnitt, der vor der grössten früheren Zusage beginnt, leer ist oder End ≤ Start hat
        await run.FeedUntil(4.0);
        var (update, start, end) = violation switch
        {
            0 => (new RecognitionUpdate([Segment("Rest", 1.6, 1.8)], [], Sec(1.5)), 1.6, 1.8),
            1 => (new RecognitionUpdate([Segment("Rest", 2.8, 2.8)], []), 2.8, 2.8),
            _ => (new RecognitionUpdate([Segment("   ", 2.8, 3.2)], []), 2.8, 3.2),
        };
        engine.Push(update);
        var failure = await run.Outputs.Finish();

        Assert.NotNull(failure);
        var notice = Assert.Single(Notices(run.Outputs.Items, LiveNoticeKind.DecoderFailed));
        Assert.Equal((Sec(start), Sec(end)), (notice.Start, notice.End));
        Assert.Equal(["letztes Wort"], FixedTexts(run.Outputs.Items));
    }

    [Fact]
    public async Task T68_DetectorFailureEndsEngineTranscriptionVisiblyWithoutFurtherText()
    {
        var engine = new FakeRecognitionEngine();
        engine.Schedule(2.0, Fixed(Segment("Erster Satz hier", 0.5, 2.0), completeUntil: 2.0));
        engine.Schedule(4.0, Fixed(Segment("noch mehr", 3.0, 4.0), completeUntil: 4.0));

        // Wie P8 bei Ausfall oder Zeitüberschreitung: Detect wirft, sobald das Audio über 4,5 s hinausreicht
        var detector = FakeSpeechDetector.Speech((0.5, 2.0), (3.0, 6.0));
        detector.FailAfter = Sec(4.5);
        var run = new SessionRun(new EngineTranscription(engine, detector, DefaultRecognition, Sec(0.6)));

        // Erste Äusserung fest, die zweite offen mit festem Delta „noch mehr“
        await run.FeedUntil(4.5);
        await run.Outputs.WaitFor(output => FixedTexts([output]).Contains("Erster Satz hier"), "erster fester Text");
        await Until(() => engine.Pushed == 2, "beide Deltas geliefert");
        await UntilProcessed(run.Source, 2);

        // Der Block [4,5, 4,6) lässt Detect scheitern
        run.Feed.ReleaseUntil(Sec(4.6));
        var failure = await run.Outputs.Finish();

        Assert.NotNull(failure);
        Assert.IsNotAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, detector.Failures);
        var hint = Assert.Single(run.Outputs.Items.OfType<HintOutput>());
        var notice = Assert.IsType<LiveNotice>(hint.Notice);
        Assert.Equal(LiveNoticeKind.DetectorFailed, notice.Kind);
        Assert.Equal(Sec(4.6), run.Source.AcceptedEnd);

        // Abgeschlossen mit dem schon gelieferten festen Delta „noch mehr“ [3, 4); Verlust [4, 4,6)
        Assert.Equal((Sec(4.0), run.Source.AcceptedEnd), (notice.Start, notice.End));
        Assert.Equal(["Erster Satz hier", "noch mehr"], FixedTexts(run.Outputs.Items));
    }

    private static SessionRun Session(
        FakeRecognitionEngine engine, (double Start, double End) speech, InterpreterOptions? interpreting = null) =>
        new(
            new EngineTranscription(engine, FakeSpeechDetector.Speech(speech), DefaultRecognition, Sec(0.6)),
            interpreting: interpreting);
}
