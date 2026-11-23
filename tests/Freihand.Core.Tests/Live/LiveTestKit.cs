using System.Diagnostics;
using Freihand.Core.Commands;
using Freihand.Core.Formatting;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Live.Fakes;
using Freihand.Core.Tests.Recovery;

namespace Freihand.Core.Tests.Live;

/// <summary>Gemeinsame Bausteine der Abnahmetests T01–T68 (P3-Konzept, Kapitel 6 und 7).</summary>
internal static class LiveTestKit
{
    public static readonly TimeSpan Block = TestAudio.Sec(0.1);

    public static TimeSpan Sec(double seconds) => TestAudio.Sec(seconds);

    /// <summary>
    /// Startwerte aus Vertrag 4. Der Modus ist ein Pflichtparameter ohne Startwert; jede Fixture nennt ihren Modus
    /// (Kapitel 5.7, Prüfung PR #24).
    /// </summary>
    public static LiveOptions Options(LiveMode mode) => new(
        mode,
        Step: Sec(0.5),
        Window: Sec(10),
        PauseThreshold: Sec(0.6),
        MaxUtterance: Sec(10),
        CutGuard: Sec(0.3),
        BacklogWarning: Sec(10),
        BacklogLimit: Sec(120),
        Filter: new HallucinationFilter([], 0.5f));

    public static RecognitionOptions DefaultRecognition { get; } = new("de-CH", []);

    /// <summary>Startwerte aus Vertrag 5, ohne Befehlswort und ohne Formatierung.</summary>
    public static InterpreterOptions Interpreting(string? commandWord = null, SwissTextFormatter? formatter = null) =>
        new(commandWord, Sec(5), new Dictionary<string, string>(), formatter);

    /// <summary>P4 mit festgelegten Optionen (T19, T39, T46, T57).</summary>
    public static SwissTextFormatter Formatter() => new(SwissFormatOptions.Default, RegionalFormat.SwissDefault);

    public static RecognizedWord Word(string text, double start, double end, float probability = 0.9f) =>
        new(text, Sec(start), Sec(end), probability);

    /// <summary>Wörter gleicher Dauer ab <paramref name="start"/>, je <paramref name="duration"/> Sekunden.</summary>
    public static IReadOnlyList<RecognizedWord> Words(double start, double duration, params string[] texts) =>
        [.. texts.Select((text, i) => Word(text, start + (i * duration), start + ((i + 1) * duration)))];

    public static Utterance Utterance(double start, params string[] words) => Utterance(start, UtteranceEnd.Pause, words);

    public static Utterance Utterance(double start, UtteranceEnd ending, params string[] words)
    {
        var recognized = Words(start, 0.5, words);
        return new Utterance(Guid.NewGuid(), recognized, Sec(start), Sec(start + (0.5 * Math.Max(words.Length, 1))), ending);
    }

    /// <summary>
    /// Testmodell der Fügeregel aus Kapitel 3, nur für die Vorschaukandidaten in T20. Die Ausgabe prüfen T50, T54 und
    /// T56 gegen die Kernfunktion <see cref="TextJoin"/> und gegen P5.
    /// </summary>
    public static string Join(IEnumerable<string> parts)
    {
        var text = string.Empty;
        foreach (var part in parts)
        {
            var glue = text.Length == 0
                || text.EndsWith('\n')
                || part.StartsWith('\n')
                || (part.Length > 0 && ".,;:!?…".Contains(part[0]));
            text += glue ? part : " " + part;
        }

        return text;
    }

    public static string Join(IEnumerable<RecognizedWord> words) => Join(words.Select(word => word.Text));

    /// <summary>Wartet, bis die Bedingung gilt; sonst schlägt der Test mit <paramref name="what"/> fehl.</summary>
    public static async Task Until(Func<bool> condition, string what, double timeoutSeconds = 20)
    {
        var started = Stopwatch.GetTimestamp();
        var spins = 0;
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(timeoutSeconds))
            {
                Assert.Fail("Zeitüberschreitung: " + what);
            }

            if (++spins < 200)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(1);
            }
        }
    }

    /// <summary>Lässt P3 Zeit für eine Ausgabe, die nicht kommen darf; nur für Abwesenheitsprüfungen.</summary>
    public static Task Settle() => Task.Delay(200);

    /// <summary>Gibt Audio frei und wartet, bis die Quelle es angenommen hat.</summary>
    public static async Task FeedUntil(AudioFeed feed, ITranscriptSource source, double seconds)
    {
        feed.ReleaseUntil(Sec(seconds));
        await Until(() => source.AcceptedEnd >= Sec(seconds), $"Audio bis {seconds} s angenommen");
    }

    /// <summary>
    /// Schrittweiser Treiber (A01, L01; Kapitel 6): gibt Audio blockweise bis <paramref name="seconds"/> frei, danach
    /// nichts mehr, und quittiert genau den erwarteten Schritt: Ein Decoderaufruf mit Audioende <paramref name="seconds"/>
    /// muss beginnen und zurückkehren, und P3 muss sein Ergebnis verarbeitet haben (Testanschluss
    /// <c>ProcessedResults</c>, wo die Quelle ihn hat). Ein freier Decoder allein ist keine Quittung; Annahme und
    /// Decoderarbeit dürfen auf getrennten Aufgaben laufen, der Aufruf darf später beginnen. Ohne Wanduhr.
    /// </summary>
    public static async Task<DecodeCall> StepTo(
        AudioFeed feed, ITranscriptSource source, FakeHypothesisDecoder decoder, Func<long>? processed, double seconds)
    {
        var end = Sec(seconds);
        Assert.True(feed.Position < end, $"Schritt bis {seconds} s liegt nicht hinter dem freigegebenen Audio.");
        var known = decoder.Calls.Count;
        while (feed.Position < end)
        {
            var next = feed.Position + feed.ChunkLength < end ? feed.Position + feed.ChunkLength : end;
            feed.ReleaseUntil(next);
            await Until(() => source.AcceptedEnd >= next && feed.Drained, $"Block bis {next.TotalSeconds} s angenommen");
        }

        DecodeCall? call = null;
        await Until(
            () => (call = decoder.Calls.Skip(known).FirstOrDefault(candidate => candidate.Request.End == end)) is not null,
            $"Decode mit Audioende {seconds} s begonnen");
        await Until(() => call!.Returned, $"Decode mit Audioende {seconds} s zurückgekehrt");
        if (processed is not null)
        {
            await Until(() => processed() > call!.Request.Index, $"Ergebnis des Decodes bis {seconds} s verarbeitet");
        }

        return call!;
    }

    /// <summary>Verarbeitungsbarriere einer Quelle (Testanschluss aus Kapitel 6), sofern sie einen hat.</summary>
    public static Func<long>? ProcessedResults(ITranscriptSource source) => source switch
    {
        LiveTranscription live => () => live.ProcessedResults,
        EngineTranscription engine => () => engine.ProcessedResults,
        _ => null,
    };

    /// <summary>Wartet, bis die Quelle mindestens <paramref name="count"/> Ergebnisse verarbeitet hat (Kapitel 6).</summary>
    public static Task UntilProcessed(ITranscriptSource source, long count) =>
        Until(
            () => (ProcessedResults(source) ?? throw new InvalidOperationException("Quelle ohne Testanschluss."))() >= count,
            $"{count} Ergebnisse verarbeitet");

    /// <summary>
    /// Bestätigt den ersten Auftrag [0, 0,5), den der Test blockiert, bevor er Rückstand aufbaut (A01): Audio bis 0,5 s,
    /// dann genau dieser eine Aufruf, noch nicht beendet.
    /// </summary>
    public static async Task<DecodeCall> FirstCallBlocked(AudioFeed feed, ITranscriptSource source, FakeHypothesisDecoder decoder)
    {
        await FeedUntil(feed, source, 0.5);
        await Until(() => decoder.Calls.Count > 0, "erster Decode bei 0,5 s");
        var call = Assert.Single(decoder.Calls);
        Assert.Equal((Sec(0), Sec(0.5)), (call.Request.Start, call.Request.End));
        Assert.False(call.Completed, "Der erste Auftrag ist nicht blockiert.");
        return call;
    }

    /// <summary>Gibt Audio sekundenweise frei und wartet jeweils auf die Annahme, wie bei einer laufenden Aufnahme.</summary>
    public static async Task FeedPaced(AudioFeed feed, ITranscriptSource source, double seconds, double step = 1)
    {
        var position = feed.Position.TotalSeconds;
        while (position < seconds)
        {
            position = Math.Min(seconds, position + step);
            await FeedUntil(feed, source, position);
        }
    }

    public static IEnumerable<TextSegment> FixedSegments(IEnumerable<DictationOutput> outputs) =>
        outputs.OfType<TextOutput>().SelectMany(output => output.Update.Fixed);

    public static IReadOnlyList<string> FixedTexts(IEnumerable<DictationOutput> outputs) =>
        [.. FixedSegments(outputs).Select(segment => segment.Text)];

    public static IReadOnlyList<string> FixedWords(IEnumerable<DictationOutput> outputs) =>
        [.. FixedTexts(outputs).SelectMany(text => text.Split(' ', StringSplitOptions.RemoveEmptyEntries))];

    public static IReadOnlyList<Utterance> Completed(IEnumerable<TranscriptEvent> events) =>
        [.. events.OfType<UtteranceCompleted>().Select(completed => completed.Utterance)];

    public static IReadOnlyList<string> CompletedWords(IEnumerable<TranscriptEvent> events) =>
        [.. Completed(events).SelectMany(utterance => utterance.Words).Select(word => word.Text)];

    public static IReadOnlyList<LiveNotice> Notices(IEnumerable<TranscriptEvent> events, LiveNoticeKind kind) =>
        [.. events.OfType<LiveNotice>().Where(notice => notice.Kind == kind)];

    public static IReadOnlyList<LiveNotice> Notices(IEnumerable<DictationOutput> outputs, LiveNoticeKind kind) =>
        [.. outputs.OfType<HintOutput>().Select(hint => hint.Notice).OfType<LiveNotice>().Where(notice => notice.Kind == kind)];

    public static IReadOnlyList<CommandEffect> Effects(IEnumerable<DictationOutput> outputs) =>
        [.. outputs.OfType<CommandOutput>().Select(output => output.Command.Command.Effect)];

    public static IReadOnlyList<CommandHint> Hints(IEnumerable<DictationOutput> outputs) =>
        [.. outputs.OfType<HintOutput>().Select(hint => hint.Hint).OfType<CommandHint>()];

    /// <summary>Alle vorläufigen Texte der Ausgabe, in Reihenfolge.</summary>
    public static IReadOnlyList<string> Previews(IEnumerable<DictationOutput> outputs) =>
        [.. outputs.OfType<TextOutput>().SelectMany(output => output.Update.Provisional).Select(segment => segment.Text)];

    /// <summary>Zeigt diese Ausgabe genau diesen vorläufigen Text?</summary>
    public static bool ShowsPreview(DictationOutput output, string text) =>
        output is TextOutput update && update.Update.Provisional.Any(segment => segment.Text == text);

    /// <summary>Text im Sinne der Tests: ein Update mit festem oder vorläufigem Abschnitt.</summary>
    public static bool HasText(DictationOutput output) =>
        output is TextOutput text && (text.Update.Fixed.Count > 0 || text.Update.Provisional.Count > 0);

    /// <summary>Gibt die Updates wie die App an <c>DraftRecorder.Apply</c> (P5) und liefert den gesicherten Text.</summary>
    public static string DraftText(IEnumerable<DictationOutput> outputs)
    {
        var time = new ManualTimeProvider();
        var files = new InMemoryRecoveryFileSystem(time);
        var protector = new FakeDataProtector();
        using (var store = new RecoveryStore(files, protector, time))
        {
            var draft = store.StartDraft();
            foreach (var output in outputs.OfType<TextOutput>())
            {
                draft.Apply(output.Update);
            }

            draft.Flush();
        }

        // Wie nach einem Neustart: der gesicherte Entwurf, so wie er angeboten wird (AU-12)
        using var restarted = new RecoveryStore(files, protector, time);
        return Assert.Single(restarted.Scan().Drafts).Text;
    }
}

/// <summary>Liest einen Ereignisstrom im Hintergrund mit, wie die Oberfläche es täte.</summary>
internal sealed class Recorder<T>
{
    private readonly Lock _gate = new();
    private readonly List<(T Item, int Phase)> _items = [];
    private readonly Func<int> _phase;

    public Recorder(Func<IAsyncEnumerable<T>> start, Func<int>? phase = null)
    {
        _phase = phase ?? (() => 0);
        Completion = Task.Run(async () =>
        {
            await foreach (var item in start())
            {
                lock (_gate)
                {
                    _items.Add((item, _phase()));
                }
            }
        });
    }

    public Task Completion { get; }

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items.Select(item => item.Item)];
            }
        }
    }

    /// <summary>Jedes Ereignis mit der Phase des Tests, in der es ankam.</summary>
    public IReadOnlyList<(T Item, int Phase)> Stamped
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public async Task<T> WaitFor(Func<T, bool> predicate, string what, double timeoutSeconds = 20)
    {
        T? found = default;
        await LiveTestKit.Until(
            () =>
            {
                if (Completion.IsFaulted)
                {
                    throw Completion.Exception!.GetBaseException();
                }

                var finished = Completion.IsCompleted;
                var match = Items.Where(predicate).ToList();
                if (match.Count == 0)
                {
                    Assert.False(finished, "Der Strom endete ohne: " + what);
                    return false;
                }

                found = match[0];
                return true;
            },
            what,
            timeoutSeconds);
        return found!;
    }

    /// <summary>Wartet auf das Ende; liefert die Ausnahme, mit der der Strom endete, oder nichts.</summary>
    public async Task<Exception?> Finish(double timeoutSeconds = 30)
    {
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
            return null;
        }
        catch (TimeoutException)
        {
            Assert.Fail("Der Strom endet nicht.");
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>Wartet auf ein normales Ende; eine Ausnahme lässt den Test fehlschlagen.</summary>
    public async Task<IReadOnlyList<T>> FinishOk(double timeoutSeconds = 30)
    {
        var exception = await Finish(timeoutSeconds);
        if (exception is not null)
        {
            throw new Xunit.Sdk.XunitException("Der Strom endete mit Fehler: " + exception);
        }

        return Items;
    }
}

/// <summary>Eine Sitzung aus Quelle, Auslegung und Ausgabe mit Attrappen (Vertrag 6).</summary>
internal sealed class SessionRun
{
    public SessionRun(
        ITranscriptSource source,
        CommandSet? commands = null,
        InterpreterOptions? interpreting = null,
        Func<CommandScope>? scope = null,
        CancellationToken cancellationToken = default,
        FakeHypothesisDecoder? decoder = null)
    {
        Source = source;
        Decoder = decoder;
        Interpreter = new DictationInterpreter(commands ?? BuiltInCommands.Pilot, interpreting ?? LiveTestKit.Interpreting());
        Session = new DictationSession(Source, Interpreter, scope ?? (() => CommandScope.DictationWindow));
        Outputs = new Recorder<DictationOutput>(() => Session.RunAsync(Feed.ReadAllAsync(), cancellationToken), () => Phase);
    }

    public AudioFeed Feed { get; } = new();

    public ITranscriptSource Source { get; }

    /// <summary>Die Decoder-Attrappe der Quelle, für den schrittweisen Treiber.</summary>
    public FakeHypothesisDecoder? Decoder { get; }

    public DictationInterpreter Interpreter { get; }

    public DictationSession Session { get; }

    public Recorder<DictationOutput> Outputs { get; }

    /// <summary>Phase des Tests, mit der jede Ausgabe gestempelt wird (T66).</summary>
    public int Phase { get; set; }

    /// <summary>Sitzung über <see cref="LiveTranscription"/>; die Optionen nennen den Modus (Kapitel 5.7).</summary>
    public static SessionRun Live(
        FakeHypothesisDecoder decoder,
        ISpeechDetector detector,
        LiveOptions options,
        InterpreterOptions? interpreting = null,
        Func<CommandScope>? scope = null,
        RecognitionOptions? recognition = null,
        CancellationToken cancellationToken = default) =>
        new(
            new LiveTranscription(decoder, detector, recognition ?? LiveTestKit.DefaultRecognition, options),
            interpreting: interpreting,
            scope: scope,
            cancellationToken: cancellationToken,
            decoder: decoder);

    public Task FeedUntil(double seconds) => LiveTestKit.FeedUntil(Feed, Source, seconds);

    /// <summary>
    /// Ein erwarteter Schritt mit Audioende <paramref name="seconds"/>, quittiert durch Aufruf, Rückkehr und Verarbeitung
    /// (<see cref="LiveTestKit.StepTo"/>).
    /// </summary>
    public Task<DecodeCall> StepTo(double seconds) =>
        LiveTestKit.StepTo(
            Feed,
            Source,
            Decoder ?? throw new InvalidOperationException("Keine Decoder-Attrappe."),
            LiveTestKit.ProcessedResults(Source),
            seconds);

    /// <summary>Mehrere erwartete Schritte nacheinander, jeder einzeln quittiert.</summary>
    public async Task<IReadOnlyList<DecodeCall>> Steps(params double[] ends)
    {
        var calls = new List<DecodeCall>();
        foreach (var end in ends)
        {
            calls.Add(await StepTo(end));
        }

        return calls;
    }

    /// <summary>Bestätigt den blockierten ersten Auftrag [0, 0,5) (<see cref="LiveTestKit.FirstCallBlocked"/>).</summary>
    public Task<DecodeCall> FirstCallBlocked() =>
        LiveTestKit.FirstCallBlocked(Feed, Source, Decoder ?? throw new InvalidOperationException("Keine Decoder-Attrappe."));

    /// <summary>
    /// Gibt Audio bis <paramref name="seconds"/> frei, beendet den Strom und wartet auf das Ende. Schnelle Einspeisung:
    /// Annahme und Erkennung laufen unabhängig, Schritte können dabei entfallen (Kapitel 5.2).
    /// </summary>
    public async Task<IReadOnlyList<DictationOutput>> EndAt(double seconds, double timeoutSeconds = 30)
    {
        Feed.ReleaseUntil(LiveTestKit.Sec(seconds));
        Feed.Complete();
        return await Outputs.FinishOk(timeoutSeconds);
    }

    /// <summary>Befehlstaste, sobald alles Audio bis <paramref name="seconds"/> angenommen ist (Vertrag 6).</summary>
    public async Task PressCommandKeyAt(double seconds)
    {
        await FeedUntil(seconds);
        Assert.Equal(LiveTestKit.Sec(seconds), Source.AcceptedEnd);
        Session.PressCommandKey();
    }
}

/// <summary>Eine Erkennung allein, ohne Auslegung (Vertrag 4).</summary>
internal sealed class SourceRun
{
    public SourceRun(ITranscriptSource source, CancellationToken cancellationToken = default, FakeHypothesisDecoder? decoder = null)
    {
        Source = source;
        Decoder = decoder;
        Events = new Recorder<TranscriptEvent>(() => Source.RunAsync(Feed.ReadAllAsync(), cancellationToken));
    }

    public AudioFeed Feed { get; } = new();

    public ITranscriptSource Source { get; }

    /// <summary>Die Decoder-Attrappe der Quelle, für den schrittweisen Treiber.</summary>
    public FakeHypothesisDecoder? Decoder { get; }

    public Recorder<TranscriptEvent> Events { get; }

    /// <summary>Erkennung über <see cref="LiveTranscription"/>; die Optionen nennen den Modus (Kapitel 5.7).</summary>
    public static SourceRun Live(
        FakeHypothesisDecoder decoder,
        ISpeechDetector detector,
        LiveOptions options,
        RecognitionOptions? recognition = null,
        CancellationToken cancellationToken = default) =>
        new(
            new LiveTranscription(decoder, detector, recognition ?? LiveTestKit.DefaultRecognition, options),
            cancellationToken,
            decoder);

    public Task FeedUntil(double seconds) => LiveTestKit.FeedUntil(Feed, Source, seconds);

    /// <summary>
    /// Ein erwarteter Schritt mit Audioende <paramref name="seconds"/>, quittiert durch Aufruf, Rückkehr und Verarbeitung
    /// (<see cref="LiveTestKit.StepTo"/>).
    /// </summary>
    public Task<DecodeCall> StepTo(double seconds) =>
        LiveTestKit.StepTo(
            Feed,
            Source,
            Decoder ?? throw new InvalidOperationException("Keine Decoder-Attrappe."),
            LiveTestKit.ProcessedResults(Source),
            seconds);

    /// <summary>Mehrere erwartete Schritte nacheinander, jeder einzeln quittiert.</summary>
    public async Task<IReadOnlyList<DecodeCall>> Steps(params double[] ends)
    {
        var calls = new List<DecodeCall>();
        foreach (var end in ends)
        {
            calls.Add(await StepTo(end));
        }

        return calls;
    }

    /// <summary>Bestätigt den blockierten ersten Auftrag [0, 0,5) (<see cref="LiveTestKit.FirstCallBlocked"/>).</summary>
    public Task<DecodeCall> FirstCallBlocked() =>
        LiveTestKit.FirstCallBlocked(Feed, Source, Decoder ?? throw new InvalidOperationException("Keine Decoder-Attrappe."));

    /// <summary>Wie <see cref="SessionRun.EndAt"/>: schnelle Einspeisung bis zum Streamende.</summary>
    public async Task<IReadOnlyList<TranscriptEvent>> EndAt(double seconds, double timeoutSeconds = 30)
    {
        Feed.ReleaseUntil(LiveTestKit.Sec(seconds));
        Feed.Complete();
        return await Events.FinishOk(timeoutSeconds);
    }
}
