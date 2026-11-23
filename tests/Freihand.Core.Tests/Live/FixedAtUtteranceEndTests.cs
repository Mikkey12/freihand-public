using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>
/// Entscheide E1–E3 vom 03.11.2026 (Q01): Live übernimmt mitten in der
/// Äusserung nichts; der Text entsteht aus dem Decode der ganzen Äusserung oder am Längenschnitt. Die Rückstandswarnung
/// zählt nur fällige, noch nicht abgearbeitete Verarbeitung. Tests T71–T76 (P3-Konzept, Kapitel 7).
/// </summary>
public sealed class FixedAtUtteranceEndTests
{
    [Fact]
    public async Task T71_LiveStepsNeverAdoptAndEveryRequestStartsAtTheUtteranceStart()
    {
        // Vier gleichbleibend erkannte Wörter: Nach bisheriger Regel würden die ersten nach zwei Schritten stabil
        var spoken = Words(1, 0.5, "eins", "zwei", "drei", "vier");
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((1, 3)), Options(LiveMode.Live));

        await run.Steps(1.5, 2.0, 2.5, 3.0, 3.5);
        await run.Events.WaitFor(e => e is ProvisionalText { Text: "eins zwei drei vier" }, "ganze Hypothese als Vorschau");
        var events = await run.EndAt(5);

        // Jeder Auftrag der Äusserung beginnt an ihrem Beginn; der Abschluss-Decode deckt die ganze Äusserung ab
        var requests = decoder.Requests;
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal(Sec(1), request.Start));
        Assert.Equal(Sec(3), requests[^1].End);

        // Vorschau ist die jüngste Hypothese ganz (E1 a); fest erst mit dem Abschluss, genau einmal
        var utterance = Assert.Single(Completed(events));
        Assert.Equal(["eins", "zwei", "drei", "vier"], utterance.Words.Select(word => word.Text));
        Assert.Equal(UtteranceEnd.Pause, utterance.Ending);
    }

    [Fact]
    public void T72_LiveRejectsAWindowShorterThanMaxUtterance()
    {
        var shortWindow = Options(LiveMode.Live) with { Window = Sec(5) };
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new LiveTranscription(FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech(), DefaultRecognition, shortWindow));
        Assert.Contains("MaxUtterance", error.Message, StringComparison.Ordinal);

        // Satzweise decodiert ohnehin die ganze Äusserung; dort bleibt ein kürzeres Window zulässig
        _ = new LiveTranscription(
            FakeHypothesisDecoder.Silent(), FakeSpeechDetector.Speech(), DefaultRecognition, Options(LiveMode.Sentence) with { Window = Sec(5) });
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T73_AHealthyTenSecondUtteranceRaisesNoBacklogWarning(LiveMode mode)
    {
        // Ununterbrochene Sprache über den Längenschnitt bei 10 s hinaus; die Erkennung hält Schritt
        var spoken = Words(0, 0.5, [.. Enumerable.Range(0, 24).Select(k => $"w{k}")]);
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 12)), Options(mode));

        if (mode == LiveMode.Live)
        {
            await run.Steps([.. Enumerable.Range(1, 24).Select(k => k * 0.5)]);
        }
        else
        {
            await run.StepTo(10);
            await FeedPaced(run.Feed, run.Source, 12, step: 0.5);
        }

        var events = await run.EndAt(13);

        // Gewolltes Halten bis Grenze oder Längenschnitt ist kein Rückstand (E2); jedes Wort genau einmal
        Assert.Empty(Notices(events, LiveNoticeKind.BacklogWarning));
        Assert.Empty(Notices(events, LiveNoticeKind.BufferFull));
        Assert.Equal(spoken.Select(word => word.Text), CompletedWords(events));
    }

    [Theory]
    [InlineData(LiveMode.Live, 12.5)]
    [InlineData(LiveMode.Sentence, 21)]
    public async Task T74_ABlockedDecoderStillWarnsWhileTheUtteranceIsOpen(LiveMode mode, double feedTo)
    {
        // Der erste Auftrag hängt; live ist das der Schritt bei 0,5 s, satzweise der Längenschnitt bei 10 s
        var spoken = Words(0, 0.5, [.. Enumerable.Range(0, 60).Select(k => $"w{k}")]);
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var gate = new TestGate();
        decoder.Hold = request => request.Index == 0 ? gate.Task : Task.CompletedTask;
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 30)), Options(mode));

        await run.FeedUntil(mode == LiveMode.Live ? 0.5 : 10);
        await Until(() => decoder.Calls.Count == 1, "erster Auftrag begonnen");
        Assert.False(decoder.Calls[0].Completed, "Der erste Auftrag ist nicht blockiert.");

        // Mehr als 10 s fällige Arbeit stehen aus, obwohl die Äusserung offen ist: Warnung wie bisher (ER-05)
        await run.FeedUntil(feedTo);
        await run.Events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BacklogWarning }, "Warnung bei hängendem Decoder");
        Assert.Single(decoder.Calls);

        gate.Open();
        await run.Events.WaitFor(e => e is LiveNotice { Kind: LiveNoticeKind.BacklogCleared }, "Rückstand abgebaut");

        // Bis zur Entwarnung genau eine Warnung, ab dem ältesten gehaltenen Audio; das schnelle Einspeisen am Ende darf
        // als echter Rechenrückstand erneut warnen und wird darum nicht mitgezählt
        var untilCleared = run.Events.Items.TakeWhile(e => e is not LiveNotice { Kind: LiveNoticeKind.BacklogCleared }).ToList();
        var warning = Assert.Single(Notices(untilCleared, LiveNoticeKind.BacklogWarning));
        Assert.Equal(Sec(0), warning.Start);

        // ER-05/E2: Live steht Arbeit seit 0 s aus, satzweise erst seit C=10 s.
        Assert.Equal(mode == LiveMode.Live ? Sec(10) : Sec(20), warning.End);
        var events = await run.EndAt(31);
        Assert.Equal(spoken.Select(word => word.Text), CompletedWords(events));
    }

    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public async Task T76_ALongUtteranceEndingInAPauseRaisesNoBacklogWarning(LiveMode mode)
    {
        // Befund R2 vom 03.11.2026: 9,4 s Sprache ab 0 s; die Äusserungsgrenze liegt bei 9,4 s vor C=10 s, die Pause wird
        // bei angenommenem Audioende 10 s bestätigt. Dann hält P3 die Kopie der Äusserung und die Pause, zusammen 10 s; der
        // Abschluss-Decode ist gerade erst fällig, das ist kein Rückstand (E2)
        var spoken = Words(0, 0.2, [.. Enumerable.Range(0, 47).Select(k => $"w{k}")]);
        var decoder = FakeHypothesisDecoder.ByInterval(spoken);
        var run = SourceRun.Live(decoder, FakeSpeechDetector.Speech((0, 9.4)), Options(mode));

        if (mode == LiveMode.Live)
        {
            await run.Steps([.. Enumerable.Range(1, 19).Select(k => k * 0.5)]);
        }

        await FeedPaced(run.Feed, run.Source, 11, step: 0.1);
        var events = await run.EndAt(11);

        Assert.Empty(Notices(events, LiveNoticeKind.BacklogWarning));
        var utterance = Assert.Single(Completed(events));
        Assert.Equal(UtteranceEnd.Pause, utterance.Ending);
        Assert.Equal(spoken.Select(word => word.Text), utterance.Words.Select(word => word.Text));
    }

    [Fact]
    public async Task T75_TerminalDecoderFailureLosesTheOpenUtteranceVisibly()
    {
        var spoken = Words(0.5, 0.5, "Erster", "Satz", "hier").Concat(Words(3.0, 0.5, "noch", "mehr", "Text", "folgt")).ToList();

        // Jeder Auftrag der zweiten Äusserung bis über 5 s hinaus scheitert, auch die Wiederholung (Kapitel 5.4)
        var decoder = new FakeHypothesisDecoder(request => request.Start >= Sec(3) && request.End >= Sec(5)
            ? throw new InvalidOperationException("Erkenner ausgefallen")
            : FakeHypothesisDecoder.Heard(spoken, request.Start, request.End));
        var run = SessionRun.Live(decoder, FakeSpeechDetector.Speech((0.5, 2.0), (3.0, 6.0)), Options(LiveMode.Live));

        await run.FeedUntil(2.6);
        await run.Outputs.WaitFor(output => FixedTexts([output]).Contains("Erster Satz hier"), "erster fester Text");
        await run.Steps(3.5, 4.0, 4.5);
        await run.Outputs.WaitFor(output => ShowsPreview(output, "noch mehr Text"), "offene Äusserung als Vorschau");

        run.Feed.ReleaseUntil(Sec(5));
        var failure = await run.Outputs.Finish();
        Assert.NotNull(failure);

        // Die schon feste Äusserung bleibt; aus der offenen wird nichts fest, auch nicht die vorher gezeigte Vorschau
        Assert.Equal(["Erster Satz hier"], FixedTexts(run.Outputs.Items));

        // Der Verlust ist sichtbar: vom Beginn der offenen Äusserung bis zum Ende des gescheiterten Auftrags
        var notice = Assert.Single(Notices(run.Outputs.Items, LiveNoticeKind.DecoderFailed));
        Assert.Equal((Sec(3), Sec(5)), (notice.Start, notice.End));
    }
}
