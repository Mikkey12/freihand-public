using Freihand.Adapters.Recognition;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.Recognition.Kit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Fehler und Zeitgrenzen des Clients nach der Tabelle in Kapitel 5.7 als Matrix mit ausdrücklichem Abdeckungsinventar:
/// jede Zeile × Operation (<c>DecodeAsync</c>, <c>Detect</c>) × Zeitpunkt (vor bzw. während der Anfrage), über
/// Supervisor, Dienst und Attrappen mit manueller Uhr.
/// <para>Jeder Fall prüft: die genaue Ausnahme samt Code; <see cref="RecognizerSession.Failed"/> nach der Tabelle; Kill
/// und Neustart beim Supervisor (Zahl der Starts; kein Kill, wo kein Neustart vorgesehen ist); die Folgeaufrufe beider
/// Operationen auf derselben Sitzung (beantwortet nach <c>Ready</c> bzw. sofort <see cref="RecognizerUnavailableException"/>
/// ohne Aufruf der Attrappen); dass keine Ausnahme Text der Attrappen enthält (SI-01; Kennung in Segmenttext und
/// Fehlermeldungen der Attrappen) und kein Fehler unbeobachtet bleibt.</para>
/// <para><b>Rotnachweis:</b> Fehlt die Abbildung von <c>failed</c> auf <see cref="RecognizerException"/> mit Code (Zeile 1),
/// werden <c>DecoderFailed</c> und <c>TokenAlignmentFailed</c> rot; fehlt <c>cancel</c> samt
/// <see cref="OperationCanceledException"/> (Zeile 2), <c>Cancelled</c>; fehlt die Decode-Zeitgrenze mit Kill und Neustart
/// (Zeile 3, Kapitel 5.4), <c>DecodeTimeout</c>; fehlt die Erkennung getrennter Verbindungen (Zeile 4),
/// <c>ConnectionLost</c>; fehlt das Warten auf <c>Ready</c> bis <c>RestartWait</c> (Zeile 5), <c>Restarting</c> mit
/// <c>DecodeAsync</c> und <c>RestartingWithoutReady</c>; fehlt der Detektorfehler mit <c>Failed</c> (Zeile 6 und Absatz
/// Detektorfehler, Punkt 3), <c>VadFailed</c>, <c>DetectTimeout</c>, <c>ConnectionLost</c> mit <c>Detect</c> und
/// <c>Restarting</c> mit <c>Detect</c>; fehlt der Identitätsvergleich nach einem Neustart (Zeile 7, Kapitel 5.9),
/// <c>IdentityChanged</c>; fehlt das sofortige Werfen gescheiterter Sitzungen (Zeile 8), <c>SessionFailed</c> und die
/// Folgeaufrufe aller Fälle mit <c>Failed</c>; fehlt das sofortige Werfen bei <c>Unavailable</c> (Zeile 9),
/// <c>Unavailable</c>.</para>
/// <para><b>Nicht enthalten, und warum:</b> Zeilen 1, 2, 3 und 5 mit <c>Detect</c> (<c>failed</c> mit Decoder-Code, Abbruch
/// per Token, Decode-Zeitgrenze und Warten auf den Neustart gibt es nur für <c>DecodeAsync</c>; <c>Detect</c> hat keinen
/// Token). <c>failed</c> und Zeitgrenzen „vor“ der Anfrage (eine Antwort bzw. Zeitgrenze setzt eine laufende Anfrage voraus).
/// Getrennte Verbindung „vor“ der Anfrage: Hat der Supervisor die Trennung bemerkt, ist das Zeile 5 bzw. 6
/// (<c>Restarting</c>); vorher wäre der Zeitpunkt unbestimmt. <c>Restarting</c>, Identitätswechsel, gescheiterte Sitzung
/// und <c>Unavailable</c> „während“ der Anfrage: Ein Neustart während einer Anfrage ist eine getrennte Verbindung
/// (Zeile 4). <c>RestartingWithoutReady</c> mit <c>Detect</c>: <c>Detect</c> wartet nie auf einen Neustart (Zeile 6).
/// Bei <c>Unavailable</c> legt die Tabelle <c>Failed</c> nicht fest; der Fall prüft es nicht.</para>
/// </summary>
public sealed class ClientFailureMatrixTests
{
    public enum Situation
    {
        /// <summary>Zeile 1: <c>failed</c> mit <c>DecoderError</c>.</summary>
        DecoderFailed,

        /// <summary>Zeile 1: <c>failed</c> mit <c>TokenAlignment</c>.</summary>
        TokenAlignmentFailed,

        /// <summary>Zeile 2: Abbruch durch den Token von P3.</summary>
        Cancelled,

        /// <summary>Zeile 3: Decode länger als <c>DecodeBase</c> + 5 × Audiodauer.</summary>
        DecodeTimeout,

        /// <summary>Zeilen 4 und 6: Verbindung des Kanals während der Anfrage hart getrennt.</summary>
        ConnectionLost,

        /// <summary>Zeilen 5 und 6: Aufruf während <c>Restarting</c>; <c>Ready</c> kommt innerhalb von <c>RestartWait</c>.</summary>
        Restarting,

        /// <summary>Zeile 5: Aufruf während <c>Restarting</c>; kein <c>Ready</c> innerhalb von <c>RestartWait</c>.</summary>
        RestartingWithoutReady,

        /// <summary>Zeile 6: <c>failed</c> auf <c>detect</c> (<c>VadError</c>).</summary>
        VadFailed,

        /// <summary>Zeile 6: <c>Detect</c> länger als <c>ClientTimeouts.Detect</c>.</summary>
        DetectTimeout,

        /// <summary>Zeile 7: Neustart, danach andere Laufzeit als in <c>RecognizerSession.Info</c>.</summary>
        IdentityChanged,

        /// <summary>Zeile 8: Sitzung schon gescheitert (nach <c>VadError</c>).</summary>
        SessionFailed,

        /// <summary>Zeile 9: Supervisor <c>Unavailable</c> (Neustart scheitert an der Prüfsumme).</summary>
        Unavailable,
    }

    public enum Operation
    {
        Decode,
        Detect,
    }

    public enum Moment
    {
        Before,
        During,
    }

    public static TheoryData<Situation, Operation, Moment> Cases()
    {
        var cases = new TheoryData<Situation, Operation, Moment>();
        foreach (var situation in Enum.GetValues<Situation>())
        {
            foreach (var operation in Enum.GetValues<Operation>())
            {
                foreach (var moment in Enum.GetValues<Moment>())
                {
                    if (Applies(situation, operation, moment))
                    {
                        cases.Add(situation, operation, moment);
                    }
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ClientFollowsTheFailureTable(Situation situation, Operation operation, Moment moment)
    {
        var secret = "GEHEIM-" + Guid.NewGuid().ToString("N")[..8];
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spoken = TestSegments.Of($" {secret}", ($" {secret}", 0, 30));
        await using var harness = await SupervisorHarness.StartedAsync(
            number => LaunchPlan.Service(loader =>
            {
                loader.Whisper.Behavior = FakeWhisper.Returns(spoken);
                if (number >= 2 && situation is Situation.Restarting or Situation.RestartingWithoutReady)
                {
                    loader.Gate = load.Task;
                }

                if (number >= 2 && situation == Situation.IdentityChanged)
                {
                    loader.AvailableRuntimes = [RecognizerRuntime.Cpu];
                }
            }),
            configuration: files => files.Configuration(RecognizerRuntime.Cuda, RecognizerRuntime.Cpu));
        await using var session = await harness.SessionAsync();
        var first = harness.Launcher.Current;
        using var cancellation = new CancellationTokenSource();

        Task Call() => operation == Operation.Decode
            ? session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German(), cancellation.Token)
            : DetectAsync(session.Detector, Samples(16_000), Sec(1));

        Exception? outcome;
        switch (situation)
        {
            case Situation.DecoderFailed:
                first.Whisper.Behavior = FakeWhisper.Throws(secret);
                outcome = await Outcome(Call());
                break;

            case Situation.TokenAlignmentFailed:
                first.Whisper.Behavior = FakeWhisper.Returns(TestSegments.Of($" Hallo {secret} Welt", (" Hallo", 0, 40), (" Welt", 40, 80)));
                outcome = await Outcome(Call());
                break;

            case Situation.Cancelled when moment == Moment.Before:
                await cancellation.CancelAsync();
                outcome = await Outcome(Call());
                break;

            case Situation.Cancelled:
                first.Whisper.Behavior = first.Whisper.WaitsForCancellation();
                var cancelled = Call();
                await first.Whisper.StartedAsync(1);
                await cancellation.CancelAsync();
                outcome = await Outcome(cancelled);
                break;

            case Situation.DecodeTimeout:
                first.Whisper.Behavior = first.Whisper.BlocksUntil(never.Task);
                var slow = Call();
                await first.Whisper.StartedAsync(1);
                await harness.Clock.WaitForTimersAsync(1, Sec(35));
                harness.Clock.Advance(Sec(35));
                outcome = await Outcome(slow);
                break;

            case Situation.ConnectionLost:
                first.Whisper.Behavior = first.Whisper.BlocksUntil(never.Task);
                first.Vad.Behavior = first.Vad.BlocksUntil(never.Task);
                var lost = Call();
                if (operation == Operation.Decode)
                {
                    await first.Whisper.StartedAsync(1);
                    first.SeverDecode();
                }
                else
                {
                    await first.Vad.StartedAsync(1);
                    first.SeverDetect();
                }

                outcome = await Outcome(lost);
                break;

            case Situation.Restarting:
                await CrashIntoRestarting(harness);
                var waiting = Call();
                if (operation == Operation.Decode)
                {
                    Assert.False(waiting.IsCompleted, "DecodeAsync wartet auf den Neustart.");
                    load.TrySetResult();
                    outcome = await Outcome(waiting);
                }
                else
                {
                    // Detect wartet nie auf einen Neustart: das Ergebnis steht vor der Freigabe des Ladens fest
                    outcome = await Outcome(waiting);
                    load.TrySetResult();
                }

                break;

            case Situation.RestartingWithoutReady:
                await CrashIntoRestarting(harness);
                var starving = Call();
                await harness.Clock.WaitForTimersAsync(1, Sec(15));
                harness.Clock.Advance(Sec(15) - Tick);
                Assert.False(starving.IsCompleted, "RestartWait vor 15 s abgelaufen.");
                harness.Clock.Advance(Tick);
                outcome = await Outcome(starving);
                load.TrySetResult();
                break;

            case Situation.VadFailed:
                first.Vad.Behavior = FakeVad.Throws(secret);
                outcome = await Outcome(Call());
                break;

            case Situation.DetectTimeout:
                first.Vad.Behavior = first.Vad.BlocksUntil(never.Task);
                var stuck = Call();
                await first.Vad.StartedAsync(1);
                await harness.Clock.WaitForTimersAsync(1, Sec(2));
                harness.Clock.Advance(Sec(2));
                outcome = await Outcome(stuck);
                break;

            case Situation.IdentityChanged:
                first.Crash();
                await harness.WaitReadyAsync(2);
                await Until(() => session.Failed, "Sitzung gescheitert nach Identitätswechsel");
                outcome = await Outcome(Call());
                break;

            case Situation.SessionFailed:
                first.Vad.Behavior = FakeVad.Throws(secret);
                Assert.IsType<RecognizerUnavailableException>(await Outcome(DetectAsync(session.Detector, Samples(16_000), Sec(1))));
                Assert.True(session.Failed);
                await harness.WaitReadyAsync(2);
                outcome = await Outcome(Call());
                break;

            case Situation.Unavailable:
                harness.Files.CorruptModelKeepingMetadata();
                first.Crash();
                await harness.WaitAvailabilityAsync(RecognizerAvailability.Unavailable);
                outcome = await Outcome(Call());
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(situation));
        }

        // Ausnahme samt Code, nie Text der Attrappen
        var expected = Expect(situation, operation);
        if (expected.Exception is null)
        {
            Assert.Null(outcome);
        }
        else
        {
            Assert.NotNull(outcome);
            Assert.IsAssignableFrom(expected.Exception, outcome);
            Assert.Equal(expected.Code, (outcome as RecognizerException)?.Code);
            AssertNoSecrets(outcome, secret);
        }

        // Failed nach der Tabelle
        if (expected.Failed is { } failed)
        {
            Assert.Equal(failed, session.Failed);
        }

        // Kill und Neustart beim Supervisor
        if (expected.Kill)
        {
            await first.Killed.WaitAsync(Guard);
        }

        if (situation == Situation.Unavailable)
        {
            Assert.Equal(RecognizerAvailability.Unavailable, harness.Supervisor.Availability);
        }
        else
        {
            await harness.WaitReadyAsync(expected.Launches);
        }

        Assert.Equal(expected.Launches, harness.Launcher.Launches);

        // Folgeaufrufe beider Operationen auf derselben Sitzung
        var current = harness.Launcher.Current;
        current.Whisper.Behavior = FakeWhisper.Returns(spoken);
        current.Vad.Behavior = FakeVad.DefaultRegions;
        var decodesBefore = current.Whisper.Calls.Count;
        var detectsBefore = current.Vad.Calls.Count;
        var decode = await Outcome(session.Decoder.DecodeAsync(Samples(16_000), Sec(5), German()));
        var detect = await Outcome(DetectAsync(session.Detector, Samples(16_000), Sec(5)));
        if (expected.FollowUpWorks)
        {
            Assert.Null(decode);
            Assert.Null(detect);
            Assert.Equal(decodesBefore + 1, current.Whisper.Calls.Count);
            Assert.Equal(detectsBefore + 1, current.Vad.Calls.Count);
        }
        else
        {
            AssertNoSecrets(Assert.IsType<RecognizerUnavailableException>(decode), secret);
            AssertNoSecrets(Assert.IsType<RecognizerUnavailableException>(detect), secret);
            Assert.Equal(decodesBefore, current.Whisper.Calls.Count);
            Assert.Equal(detectsBefore, current.Vad.Calls.Count);
        }

        if (!expected.Kill && expected.Launches == 1)
        {
            Assert.Equal(0, harness.Launcher.KillCalls);
        }

        Assert.False(await UnobservedFailureLog.SeenAsync(secret), $"Unbeobachteter Fehler mit Kennung {secret}.");
        Assert.False(await UnobservedFailureLog.SeenAsync(harness.Launcher.Marker), $"Unbeobachteter Fehler mit Kennung {harness.Launcher.Marker}.");
    }

    private static bool Applies(Situation situation, Operation operation, Moment moment) => situation switch
    {
        Situation.DecoderFailed or Situation.TokenAlignmentFailed or Situation.DecodeTimeout =>
            operation == Operation.Decode && moment == Moment.During,
        Situation.Cancelled => operation == Operation.Decode,
        Situation.ConnectionLost => moment == Moment.During,
        Situation.Restarting => moment == Moment.Before,
        Situation.RestartingWithoutReady => operation == Operation.Decode && moment == Moment.Before,
        Situation.VadFailed or Situation.DetectTimeout => operation == Operation.Detect && moment == Moment.During,
        Situation.IdentityChanged or Situation.SessionFailed or Situation.Unavailable => moment == Moment.Before,
        _ => false,
    };

    /// <summary>Erwartung nach der Tabelle in Kapitel 5.7.</summary>
    /// <param name="Exception">Erwartete Ausnahme, <c>null</c> = beantwortet.</param>
    /// <param name="Code">Code bei <see cref="RecognizerException"/>.</param>
    /// <param name="Failed">Erwartetes <see cref="RecognizerSession.Failed"/>, <c>null</c> = von der Tabelle nicht festgelegt.</param>
    /// <param name="Kill">Der Supervisor beendet den ersten Erkenner (Kapitel 5.4 bzw. Detektorfehler, Punkt 3).</param>
    /// <param name="Launches">Zahl der Starts am Ende.</param>
    /// <param name="FollowUpWorks">Folgeaufrufe werden beantwortet; sonst werfen sie sofort.</param>
    private sealed record Expectation(Type? Exception, RecognizerErrorCode? Code, bool? Failed, bool Kill, int Launches, bool FollowUpWorks);

    private static Expectation Expect(Situation situation, Operation operation) => situation switch
    {
        Situation.DecoderFailed => new(typeof(RecognizerException), RecognizerErrorCode.DecoderError, false, false, 1, true),
        Situation.TokenAlignmentFailed => new(typeof(RecognizerException), RecognizerErrorCode.TokenAlignment, false, false, 1, true),
        Situation.Cancelled => new(typeof(OperationCanceledException), null, false, false, 1, true),
        Situation.DecodeTimeout => new(typeof(RecognizerUnavailableException), null, false, true, 2, true),
        Situation.ConnectionLost => operation == Operation.Decode
            ? new(typeof(RecognizerUnavailableException), null, false, true, 2, true)
            : new(typeof(RecognizerUnavailableException), null, true, true, 2, false),
        Situation.Restarting => operation == Operation.Decode
            ? new(null, null, false, false, 2, true)
            : new(typeof(RecognizerUnavailableException), null, true, false, 2, false),
        Situation.RestartingWithoutReady => new(typeof(RecognizerUnavailableException), null, false, false, 2, true),
        Situation.VadFailed or Situation.DetectTimeout => new(typeof(RecognizerUnavailableException), null, true, true, 2, false),
        Situation.IdentityChanged => new(typeof(RecognizerUnavailableException), null, true, false, 2, false),
        Situation.SessionFailed => new(typeof(RecognizerUnavailableException), null, true, true, 2, false),
        Situation.Unavailable => new(typeof(RecognizerUnavailableException), null, null, false, 2, false),
        _ => throw new ArgumentOutOfRangeException(nameof(situation)),
    };

    /// <summary>Absturz des ersten Erkenners; der zweite lädt und bleibt blockiert, der Supervisor ist in <c>Restarting</c>.</summary>
    private static async Task CrashIntoRestarting(SupervisorHarness harness)
    {
        harness.Launcher.Current.Crash();
        var second = await harness.Launcher.LaunchedAsync(2);
        await Until(() => second.Loader.Calls.Count == 1, "Neustart lädt");
        Assert.Equal(RecognizerAvailability.Restarting, harness.Supervisor.Availability);
    }
}
