using Freihand.Adapters.Audio;
using Freihand.Adapters.Recognition;
using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Audio.Kit;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.Recognition.Kit;
using Freihand.Core.Tests.TestKit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Integration;

/// <summary>
/// R51, Integrationsabnahme Detektorfehler (P8-Konzept, Kapitel 5.7, Absatz Detektorfehler, und Tabelle 7.1; P3-Konzept,
/// Kapitel 5.4, Absatz Detektorfehler). Kette: <see cref="CaptureSession"/> mit WASAPI-Attrappe → P3
/// <see cref="DictationSession"/> über <see cref="LiveTranscription"/> → <see cref="RecognizerSession"/> aus dem
/// <see cref="RecognizerSupervisor"/>, verbunden über Speicherströme mit einem <see cref="RecognizerService"/>, dessen
/// Whisper und VAD Attrappen sind. Die Verdrahtung macht <see cref="DictationWiring"/> so, wie P9 sie übernimmt.
/// <para><b>Ablauf.</b> Satzweiser Modus (Startwerte P3 Vertrag 4; Kapitel 5.7, Punkt 2 nennt ihn ausdrücklich), 16 kHz
/// mono PCM16 (bitgenau durchgereicht, Blöcke zu 0,2 s). Die VAD-Attrappe meldet Sprache genau dort, wo das Signal nicht
/// null ist; „Sprache“ ist ein konstanter Wert, Stille sind Nullen. Erste Äusserung [0,4, 1,4), Pause bis 2,4 s, zweite
/// Äusserung ab 2,4 s. Nach Block 12 (2,4 s) ist die erste Äusserung fest („Guten Tag“ [0,4, 1,4)). Blöcke 13 und 14
/// öffnen die zweite Äusserung. Bei Block 15 [2,8, 3,0) hält die VAD-Attrappe an; währenddessen nimmt das Mikrofon
/// weitere 0,4 s auf, die P3 nicht mehr abholt. Dann scheitert <c>Detect</c>: nach <see cref="ClientTimeouts.Detect"/>
/// auf der Testuhr (Variante <see cref="DetectorFault.Timeout"/>) bzw. mit <c>failed</c> (<c>VadError</c>, Variante
/// <see cref="DetectorFault.VadError"/>).</para>
/// <para><b>Erwartete Spanne.</b> Die zweite Äusserung hat im satzweisen Modus keine stabilen Wörter; die Verlustspanne
/// beginnt darum an ihrem Beginn, dem Sprachbeginn 2,4 s (P3 Kapitel 5.1 und 5.4), und endet bei <c>AcceptedEnd</c>
/// einschliesslich des Blocks, bei dem <c>Detect</c> scheiterte: 3,0 s. Die 0,4 s danach liegen ungelesen in der
/// Warteschlange und stehen in <see cref="CaptureStatus.Unread"/> (Kapitel 5.7, Punkt 1).</para>
/// <para><b>Uhren.</b> Aufnahme und Erkenner haben je eine <see cref="ManualClock"/>. Die Uhr der Aufnahme läuft nur mit
/// dem eingespeisten Audio (<see cref="CaptureRig.Feed()"/>), damit <c>NoSignalTimeout</c> nicht greift, während P3 in
/// <c>Detect</c> wartet. Die Uhr des Erkenners läuft im Gleichschritt mit dem eingespeisten Audio mit; nach jedem Block
/// wartet der Test, bis P3 dessen <c>Detect</c> zurückerhalten hat. So hat der Zeitgeber jedes <c>Detect</c> eine eigene
/// Fälligkeit, und der Test erkennt den Zeitgeber des hängenden Aufrufs genau
/// (<see cref="ManualClock.WaitForTimerDueInAsync"/>), auch wenn eine Umsetzung Zeitgeber beantworteter Aufrufe stehen
/// lässt. Echte Zeit dient nur als Schutz gegen Hänger und für Abwesenheitsnachweise (<see cref="RecognitionTestKit.Settle"/>).</para>
/// <para><b>Rotnachweis</b> (welche Regel welche Prüfung rot macht, wenn sie fehlt):</para>
/// <list type="bullet">
/// <item>P8 Tabelle 5.7, Zeile 6 (Detect wirft <see cref="RecognizerUnavailableException"/> bei <c>VadError</c> bzw.
/// nach <see cref="ClientTimeouts.Detect"/>): ohne Zeitgrenze endet der Lauf nie (Hängerschutz schlägt an); läuft sie vor
/// 2 s ab, schlägt die Prüfung „noch nichts nach Detect − 1 Tick“ an; eine leere Liste oder ein Bereich über alles statt
/// der Ausnahme lässt Text bzw. kein <c>DetectorFailed</c> entstehen.</item>
/// <item>P3 Kapitel 5.4, Detektorfehler: fehlt der Abschluss mit Meldung, fehlt das einzige <c>DetectorFailed</c> oder
/// seine Spanne [2,4, 3,0) stimmt nicht; entsteht danach Text, schlägt „kein Text nach der Meldung“ an; wiederholt P3
/// <c>Detect</c>, stimmt die Zahl der <c>detect</c>-Aufrufe (15) nicht; endet <c>RunAsync</c> regulär, fehlt der
/// Fehler; geht fester Text verloren, stimmen fester Abschnitt und Entwurf (P5) nicht.</item>
/// <item>Kapitel 5.7, Punkt 1 (Aufnahme beenden, ER-17): stoppt <see cref="CaptureSession.Stop"/> die Attrappe nicht oder
/// gibt <see cref="CaptureSession.DisposeAsync"/> sie nicht frei, läuft das Mikrofon weiter (<c>IsRunning</c>,
/// <c>DisposeCalls</c>); fehlt der Ausweis des ungelesenen Rests, stimmt <c>Unread</c> (0,4 s) nicht. Endet
/// <c>RunAsync</c> nicht mit Fehler, ruft die Verdrahtung <c>Stop</c> gar nicht erst auf.</item>
/// <item>Kapitel 5.7, Punkt 2 (kein stilles Weiterlaufen ohne VAD): fehlt <see cref="RecognizerSession.Failed"/>, schlägt
/// diese Prüfung an; wird die Sitzung nach dem Neustart wieder verbunden, beantwortet der neue Erkenner ihre Aufrufe
/// (Zahl der Aufrufe der Attrappen des zweiten Prozesses) statt sofort zu werfen.</item>
/// <item>Kapitel 5.7, Punkt 3 (Neustart nur für ein neues Diktat, BE-18): ohne Neustart kein Kill des ersten Prozesses
/// und kein zweiter <c>Ready</c>; mehr als ein Neustart ändert die Zahl der Starts (2). Beginnt das neue Diktat nicht mit
/// neuer Sitzung und Zeiten ab 0, stimmen erster <c>detect</c>-Offset (0), <c>decode</c>-Offset (0,4 s) und Zeiten des
/// festen Abschnitts nicht.</item>
/// <item>Kapitel 5.7, Punkt 4 (sichtbar): dieselben Prüfungen wie zu P3 oben, Meldung mit Spanne und fester Entwurf.</item>
/// <item>SI-01 und P3 Kapitel 5.4 (Fehler beobachtet): enthält eine Ausnahme Text der Attrappe oder bleibt ein Fehler
/// unbeobachtet, schlagen die Prüfungen mit der Kennung an.</item>
/// </list>
/// </summary>
public sealed class DetectorFailureIntegrationTests
{
    /// <summary>Wie <c>Detect</c> scheitert (Tabelle 7.1, R51).</summary>
    public enum DetectorFault
    {
        /// <summary>Die VAD-Attrappe antwortet nicht mehr; Zeitgrenze <see cref="ClientTimeouts.Detect"/> über die Testuhr.</summary>
        Timeout,

        /// <summary>Die VAD-Attrappe wirft; der Dienst antwortet mit <c>failed</c> (<c>VadError</c>).</summary>
        VadError,
    }

    /// <summary>Wert eines „gesprochenen“ Samples; Stille sind exakte Nullen.</summary>
    private const double Voice = 0.25;

    /// <summary>Der Block [2,8, 3,0), bei dem <c>Detect</c> scheitert: der 15. Aufruf (je Block genau einer, Kapitel 5.6).</summary>
    private const int FailingDetect = 15;

    private static readonly TimeSpan Chunk = CaptureOptions.Default.ChunkDuration;

    private static readonly TimeSpan FirstSpeechStart = Sec(0.4);

    private static readonly TimeSpan FirstSpeechEnd = Sec(1.4);

    private static readonly TimeSpan SecondSpeechStart = Sec(2.4);

    /// <summary>Ende des Blocks, bei dem <c>Detect</c> scheitert, und damit <c>AcceptedEnd</c>.</summary>
    private static readonly TimeSpan FailedChunkEnd = Sec(3.0);

    /// <summary>Was das Mikrofon aufnimmt, während <c>Detect</c> hängt; P3 holt es nicht mehr ab.</summary>
    private static readonly TimeSpan HeldBack = Sec(0.4);

    [Theory]
    [InlineData(DetectorFault.Timeout)]
    [InlineData(DetectorFault.VadError)]
    public async Task R51_DetectorFailureEndsTheDictationVisiblyAndOnlyANewDictationContinues(DetectorFault fault)
    {
        var secret = "GEHEIM-" + Guid.NewGuid().ToString("N")[..8];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = German();
        var live = LiveDefaults();
        Assert.Equal(LiveMode.Sentence, live.Mode);
        await using var harness = await SupervisorHarness.StartedAsync(
            number => LaunchPlan.Service(loader => loader.Vad.Behavior = VadBehavior(loader.Vad, number == 1, release.Task, secret)));
        var first = harness.Launcher.Current;
        var detectLimit = harness.Timeouts.Detect;

        // ---- Erstes Diktat ------------------------------------------------------------------------------------------
        await using var rig = CaptureRig.Open(CaptureRig.NativeFormat);
        var backend = await rig.StartedAsync();
        await using var session = await harness.SessionAsync(options, live);
        var dictation = DictationWiring.Start(rig.Session, session, options, live);

        // Erste Äusserung [0,4, 1,4), Pause bis 2,4 s: fest, bevor die zweite beginnt
        for (var chunk = 1; chunk <= 12; chunk++)
        {
            await FeedChunkAsync(rig, harness.Clock, dictation, chunk, FirstDictationVoice);
        }

        await dictation.Outputs.WaitFor(output => LiveTestKit.FixedTexts([output]).Count > 0, "erste Äusserung fest");

        // Zweite Äusserung ab 2,4 s offen
        for (var chunk = 13; chunk < FailingDetect; chunk++)
        {
            await FeedChunkAsync(rig, harness.Clock, dictation, chunk, FirstDictationVoice);
        }

        // Block 15 [2,8, 3,0): die VAD-Attrappe hält an
        FeedChunk(rig, harness.Clock, FirstDictationVoice);
        await first.Vad.StartedAsync(FailingDetect);
        Assert.Equal(FailedChunkEnd, dictation.Source.AcceptedEnd);
        if (fault == DetectorFault.Timeout)
        {
            // Zeitgeber genau dieses Aufrufs gestellt, bevor die Uhr weiterläuft
            await harness.Clock.WaitForTimerDueInAsync(detectLimit);
        }

        // Das Mikrofon nimmt weiter auf, während P3 in Detect wartet; P3 holt nichts mehr ab
        for (var fed = TimeSpan.Zero; fed < HeldBack; fed += Chunk)
        {
            FeedChunk(rig, harness.Clock, FirstDictationVoice);
        }

        if (fault == DetectorFault.Timeout)
        {
            harness.Clock.Advance(detectLimit - HeldBack - Tick);
            await Settle();
            Assert.False(dictation.Outputs.Completion.IsCompleted, "Detect scheiterte vor ClientTimeouts.Detect.");
            Assert.Empty(dictation.Outputs.Items.OfType<HintOutput>());
            harness.Clock.Advance(Tick);
        }
        else
        {
            release.TrySetResult();
        }

        var failure = await dictation.Outputs.Finish();

        // RunAsync endet mit Fehler wie beim Decoderfehler, ohne Text der Attrappe (SI-01)
        Assert.NotNull(failure);
        Assert.IsNotAssignableFrom<OperationCanceledException>(failure);
        AssertNoSecrets(failure, secret);

        // Der feste Text der ersten Äusserung bleibt, auch im Entwurf (P5); kein weiterer fester Abschnitt
        var outputs = dictation.Outputs.Items;
        var kept = Assert.Single(LiveTestKit.FixedSegments(outputs));
        Assert.Equal(("Guten Tag", FirstSpeechStart, FirstSpeechEnd), (kept.Text, kept.Start, kept.End));
        Assert.Equal("Guten Tag", LiveTestKit.DraftText(outputs));

        // Genau eine Meldung DetectorFailed mit der Spanne des nicht fest abgeschlossenen Bereichs, danach kein Text
        var hint = Assert.Single(outputs.OfType<HintOutput>());
        Assert.Null(hint.Hint);
        var notice = Assert.IsType<LiveNotice>(hint.Notice);
        Assert.Equal(LiveNoticeKind.DetectorFailed, notice.Kind);
        Assert.Equal(FailedChunkEnd, dictation.Source.AcceptedEnd);
        Assert.Equal((SecondSpeechStart, FailedChunkEnd), (notice.Start, notice.End));
        var noticeAt = IndexOf(outputs, hint);
        Assert.True(IndexOf(outputs, outputs.First(output => LiveTestKit.FixedSegments([output]).Any())) < noticeAt);
        Assert.DoesNotContain(outputs.Skip(noticeAt + 1), LiveTestKit.HasText);

        // P3 wiederholt nicht: je Block genau ein Detect, der 15. scheitert; kein Decode nach dem der ersten Äusserung
        Assert.Equal(FailingDetect, dictation.Detector.Calls);
        Assert.Equal(1, dictation.Detector.Failures);
        Assert.Equal(1, dictation.Decoder.Calls);
        Assert.Equal(FailingDetect, first.Vad.Calls.Count);
        Assert.Single(first.Whisper.Calls);

        // Punkt 1: Aufnahme gestoppt und freigegeben, Mikrofon zu; der ungelesene Rest steht in Unread, lückenlos nach
        // der gemeldeten Spanne
        Assert.True(dictation.StoppedAfterFailure, "Die Verdrahtung hat die Aufnahme nicht nach dem Fehler beendet.");
        Assert.Equal(1, rig.Factory.Calls);
        Assert.False(backend.IsRunning, "Das Mikrofon ist noch offen.");
        Assert.Equal(1, backend.StopTransitions);
        Assert.Equal(1, backend.DisposeCalls);
        var status = rig.Session.Status;
        Assert.Equal((CapturePhase.Stopped, (CaptureStopReason?)CaptureStopReason.Requested), (status.Phase, status.StopReason));
        Assert.Equal(HeldBack, status.Unread);
        Assert.Equal(rig.Clock.Elapsed, notice.End + status.Unread);

        // Punkt 2: Sitzung gescheitert
        Assert.True(session.Failed, "Die Erkennersitzung gilt nach dem Detektorfehler nicht als gescheitert.");

        // Punkt 3: der Supervisor startet den Erkenner neu
        await first.Killed.WaitAsync(Guard);
        await harness.WaitReadyAsync(2);
        var second = harness.Launcher.Current;
        Assert.NotSame(first, second);

        // Punkt 2: auch nach dem Neustart nicht wieder verbunden; jeder Aufruf wirft sofort, ohne eine Attrappe zu erreichen
        await Throws<RecognizerUnavailableException>(session.Decoder.DecodeAsync(Samples(16_000), Sec(5), options));
        await Throws<RecognizerUnavailableException>(DetectAsync(session.Detector, Samples(16_000), Sec(5)));
        Assert.Empty(second.Whisper.Calls);
        Assert.Empty(second.Vad.Calls);
        Assert.Equal(FailingDetect, first.Vad.Calls.Count);
        Assert.Single(first.Whisper.Calls);

        // ---- Neues, bewusst gestartetes Diktat: neue Aufnahme, neue Sitzung, neue P3-Sitzung mit Zeiten ab 0 -----------
        await using var nextRig = CaptureRig.Open(CaptureRig.NativeFormat);
        var nextBackend = await nextRig.StartedAsync();
        await using var nextSession = await harness.SessionAsync(options, live);
        Assert.NotSame(session, nextSession);
        Assert.False(nextSession.Failed);
        var next = DictationWiring.Start(nextRig.Session, nextSession, options, live);
        Assert.NotSame(dictation.Source, next.Source);
        for (var chunk = 1; chunk <= 12; chunk++)
        {
            await FeedChunkAsync(nextRig, harness.Clock, next, chunk, NextDictationVoice);
        }

        nextRig.Session.Stop();
        var nextOutputs = await next.Outputs.FinishOk();

        var fresh = Assert.Single(LiveTestKit.FixedSegments(nextOutputs));
        Assert.Equal(("Guten Tag", FirstSpeechStart, FirstSpeechEnd), (fresh.Text, fresh.Start, fresh.End));
        Assert.Empty(nextOutputs.OfType<HintOutput>());
        Assert.Equal(TimeSpan.Zero, next.Detector.Offsets[0]);
        Assert.Equal([FirstSpeechStart], next.Decoder.Offsets);
        var decodes = (await second.DecodeConnected).SentBy(PipeSide.App).Where(frame => frame.Type == "decode").ToList();
        Assert.Equal(FirstSpeechStart.Ticks, Assert.Single(decodes).Long("offsetTicks"));
        Assert.Equal(12, second.Vad.Calls.Count);
        Assert.Single(second.Whisper.Calls);
        Assert.False(nextBackend.IsRunning);
        Assert.Equal(1, nextBackend.DisposeCalls);
        Assert.Equal(TimeSpan.Zero, nextRig.Session.Status.Unread);

        // Die gescheiterte Sitzung blieb unberührt, und es gab genau einen Neustart
        Assert.Equal(FailingDetect, first.Vad.Calls.Count);
        await Settle();
        Assert.Equal(2, harness.Launcher.Launches);
        Assert.Equal(RecognizerAvailability.Ready, harness.Supervisor.Availability);

        // Kein unbeobachteter Fehler
        Assert.False(await UnobservedFailureLog.SeenAsync(secret), $"Unbeobachteter Fehler mit Kennung {secret}.");
        Assert.False(
            await UnobservedFailureLog.SeenAsync(harness.Launcher.Marker),
            $"Unbeobachteter Fehler mit Kennung {harness.Launcher.Marker}.");
        await CaptureFailureWatch.AssertNoneAsync();
    }

    /// <summary>Erstes Diktat: Sprache in [0,4, 1,4) und ab 2,4 s.</summary>
    private static bool FirstDictationVoice(TimeSpan at) =>
        (at >= FirstSpeechStart && at < FirstSpeechEnd) || at >= SecondSpeechStart;

    /// <summary>Neues Diktat: dieselbe erste Äusserung [0,4, 1,4), ab seiner eigenen Zeit 0.</summary>
    private static bool NextDictationVoice(TimeSpan at) => at >= FirstSpeechStart && at < FirstSpeechEnd;

    /// <summary>
    /// VAD-Attrappe nach dem Signal: Sprache, wo das Sample nicht null ist (ohne Zustand, Kapitel 5.6). Im ersten Prozess
    /// hält der Aufruf <see cref="FailingDetect"/> an, bis der Test ihn freigibt (dann wirft er, der Dienst antwortet mit
    /// <c>failed</c>/<c>VadError</c>) oder der Prozess endet (Zeitgrenze, Kill).
    /// </summary>
    private static Func<VadCall, IReadOnlyList<(TimeSpan Start, TimeSpan End)>> VadBehavior(
        FakeVad vad, bool failing, Task release, string secret) =>
        call =>
        {
            if (failing && call.Number >= FailingDetect)
            {
                release.Wait(vad.Aborted);
                throw new InvalidOperationException(secret);
            }

            return SpeechRuns(call.Audio);
        };

    /// <summary>Zusammenhängende Folgen von Samples ungleich null, relativ zum Anfang des Audios.</summary>
    private static IReadOnlyList<(TimeSpan Start, TimeSpan End)> SpeechRuns(float[] audio)
    {
        var runs = new List<(TimeSpan Start, TimeSpan End)>();
        int? start = null;
        for (var i = 0; i <= audio.Length; i++)
        {
            var voiced = i < audio.Length && audio[i] != 0f;
            if (voiced && start is null)
            {
                start = i;
            }
            else if (!voiced && start is { } begin)
            {
                runs.Add((SampleTime(begin), SampleTime(i)));
                start = null;
            }
        }

        return runs;
    }

    private static TimeSpan SampleTime(long sample) => TimeSpan.FromTicks(sample * (TimeSpan.TicksPerSecond / 16_000));

    /// <summary>
    /// Ein Block (zwei Gerätepuffer) Sprache oder Stille nach <paramref name="voice"/>; die Uhr des Erkenners läuft im
    /// Gleichschritt mit, jeweils vor dem Puffer. Jeder Puffer muss ankommen (Aufnahme läuft).
    /// </summary>
    private static void FeedChunk(CaptureRig rig, ManualClock recognizerClock, Func<TimeSpan, bool> voice)
    {
        for (var fed = TimeSpan.Zero; fed < Chunk; fed += CaptureRig.BufferLength)
        {
            var at = rig.Clock.Elapsed;
            var frames = TestSignals.Frames(rig.Format, CaptureRig.BufferLength);
            var buffer = voice(at) ? TestSignals.Constant(rig.Format, frames, Voice) : TestSignals.Zeros(rig.Format, frames);
            // Die Uhr des Erkenners zuerst: P3 ruft Detect für diesen Block erst mit dem Puffer auf
            recognizerClock.Advance(CaptureRig.BufferLength);
            Assert.True(rig.Feed(buffer, CaptureRig.BufferLength), $"Das Gerät nahm bei {at} keinen Puffer mehr an.");
        }
    }

    /// <summary>Wie <see cref="FeedChunk"/>, dann warten, bis P3 das <c>Detect</c> dieses Blocks zurückerhalten hat.</summary>
    private static async Task FeedChunkAsync(
        CaptureRig rig, ManualClock recognizerClock, DictationWiring dictation, int chunk, Func<TimeSpan, bool> voice)
    {
        FeedChunk(rig, recognizerClock, voice);
        await Until(() => dictation.Detector.Returned >= chunk, $"Detect für Block {chunk} beantwortet");
        Assert.Equal(chunk, dictation.Detector.Returned);
    }

    private static int IndexOf(IReadOnlyList<DictationOutput> outputs, DictationOutput output)
    {
        for (var i = 0; i < outputs.Count; i++)
        {
            if (ReferenceEquals(outputs[i], output))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Verdrahtung eines Diktats nach Kapitel 5.7 (Testhilfe, von P9 übernommen): Aufnahmesitzung → P3
/// <see cref="DictationSession"/> über <see cref="LiveTranscription"/> → Decoder und Detektor der
/// <see cref="RecognizerSession"/>, je hinter einem Zähler. Endet <c>RunAsync</c> mit Fehler, ruft die Hilfe sofort
/// <see cref="ICaptureSession.Stop"/> und danach <see cref="IAsyncDisposable.DisposeAsync"/> (Punkt 1) und gibt den
/// Fehler weiter; endet es regulär (nach <c>Stop</c> durch den Test), gibt sie die Aufnahme frei. Die Erkennersitzung
/// gibt sie nicht frei; P3 tut es auch nicht.
/// </summary>
internal sealed class DictationWiring
{
    private int _stoppedAfterFailure;

    private DictationWiring(ICaptureSession capture, RecognizerSession recognizer, RecognitionOptions options, LiveOptions live)
    {
        Capture = capture;
        Recognizer = recognizer;
        Detector = new CountingDetector(recognizer.Detector);
        Decoder = new CountingDecoder(recognizer.Decoder);
        Source = new LiveTranscription(Decoder, Detector, options, live);
        Session = new DictationSession(
            Source, new DictationInterpreter(BuiltInCommands.Pilot, LiveTestKit.Interpreting()), () => CommandScope.DictationWindow);
        Outputs = new Recorder<DictationOutput>(RunAsync);
    }

    public ICaptureSession Capture { get; }

    public RecognizerSession Recognizer { get; }

    public CountingDetector Detector { get; }

    public CountingDecoder Decoder { get; }

    public ITranscriptSource Source { get; }

    public DictationSession Session { get; }

    /// <summary>Ausgabe des Diktats, im Hintergrund mitgelesen wie von der Oberfläche.</summary>
    public Recorder<DictationOutput> Outputs { get; }

    /// <summary>Nach einem Fehler von <c>RunAsync</c> wurden <c>Stop</c> und <c>DisposeAsync</c> aufgerufen.</summary>
    public bool StoppedAfterFailure => Volatile.Read(ref _stoppedAfterFailure) == 1;

    /// <summary>Verdrahtet und startet das Diktat sofort.</summary>
    public static DictationWiring Start(ICaptureSession capture, RecognizerSession recognizer, RecognitionOptions options, LiveOptions live) =>
        new(capture, recognizer, options, live);

    private async IAsyncEnumerable<DictationOutput> RunAsync()
    {
        var outputs = Session.RunAsync(Capture.ReadAsync()).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                bool next;
                try
                {
                    next = await outputs.MoveNextAsync();
                }
                catch (Exception)
                {
                    // Kapitel 5.7, Punkt 1: sofort Stop, danach DisposeAsync; das Mikrofon ist zu (ER-17)
                    Capture.Stop();
                    await Capture.DisposeAsync();
                    Volatile.Write(ref _stoppedAfterFailure, 1);
                    throw;
                }

                if (!next)
                {
                    break;
                }

                yield return outputs.Current;
            }
        }
        finally
        {
            await outputs.DisposeAsync();
        }

        // Reguläres Ende nach Stop: Aufnahme freigeben
        await Capture.DisposeAsync();
    }
}

/// <summary>Detektor hinter einem Zähler: Aufrufe von P3, Rückgaben, Fehler und die Offsets der Aufrufe.</summary>
internal sealed class CountingDetector(ISpeechDetector inner) : ISpeechDetector
{
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _offsets = [];
    private int _returned;
    private int _failures;

    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _offsets.Count;
            }
        }
    }

    public int Returned => Volatile.Read(ref _returned);

    public int Failures => Volatile.Read(ref _failures);

    public IReadOnlyList<TimeSpan> Offsets
    {
        get
        {
            lock (_gate)
            {
                return [.. _offsets];
            }
        }
    }

    public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset)
    {
        lock (_gate)
        {
            _offsets.Add(offset);
        }

        try
        {
            var regions = inner.Detect(audio, offset);
            Interlocked.Increment(ref _returned);
            return regions;
        }
        catch
        {
            Interlocked.Increment(ref _failures);
            throw;
        }
    }
}

/// <summary>Decoder hinter einem Zähler: Aufrufe von P3 und ihre Offsets.</summary>
internal sealed class CountingDecoder(IHypothesisDecoder inner) : IHypothesisDecoder
{
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _offsets = [];

    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _offsets.Count;
            }
        }
    }

    public IReadOnlyList<TimeSpan> Offsets
    {
        get
        {
            lock (_gate)
            {
                return [.. _offsets];
            }
        }
    }

    public Task<RecognitionHypothesis> DecodeAsync(
        ReadOnlyMemory<float> audio, TimeSpan offset, RecognitionOptions options, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _offsets.Add(offset);
        }

        return inner.DecodeAsync(audio, offset, options, cancellationToken);
    }
}
