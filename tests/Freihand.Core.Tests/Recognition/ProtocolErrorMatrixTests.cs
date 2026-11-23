using System.Text.Json.Nodes;
using Freihand.Adapters.Recognition;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.Recognition.Kit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Protokollfehler am Dienst als Matrix mit ausdrücklichem Abdeckungsinventar (P8-Konzept, Kapitel 4.7, Regeln 1, 2,
/// 4 und 5), mit rohen Rahmen der Testseite (<see cref="FrameCodec"/>, unabhängig vom Produkt).
/// <para><b>Rahmenfehler</b> × Kanal × Zustand → Verbindung zu, keine Antwort auf die fehlerhafte Nachricht, die
/// Attrappe sieht sie nicht, der Kanal des Dienstes endet. <b>Inhaltsfehler</b> × Kanal × Zustand → genau
/// <c>failed</c> mit dem richtigen Code und der <c>id</c> der Anfrage, ohne Aufruf der Attrappe, die Verbindung bleibt,
/// und eine folgende gültige Anfrage wird beantwortet.</para>
/// <para><b>Rotnachweis:</b> Fehlt Regel 1 (Rahmenfehler beenden die Verbindung), werden alle Fälle von
/// <see cref="FrameFaultClosesTheConnectionWithoutAnswer"/> rot; fehlt nur die Grenze der Kopflänge, die JSON-Prüfung,
/// die Prüfung von <c>type</c>, die Nutzlastgrenze oder die Kanal- bzw. Reihenfolgeprüfung, werden genau die Zeilen
/// dieses Fehlers rot. Fehlt Regel 2 (Inhaltsfehler → <c>InvalidRequest</c>, Verbindung bleibt), werden die Fälle
/// <c>PayloadMismatch</c>, <c>OffsetNegative</c>, <c>OffsetNotMultipleOf625</c> und <c>SampleCountNegative</c> von
/// <see cref="ContentFaultIsAnsweredAndTheConnectionStays"/> rot; fehlt Regel 5 (über 480 000 Samples →
/// <c>AudioTooLong</c>), die Fälle <c>TooManySamples</c>. Fehlt Regel 4 (<c>decode</c> vor <c>ready</c>), wird
/// <c>WrongOrder</c> im Zustand <c>BeforeConfigure</c> rot. Werden Antworten nicht in der Reihenfolge der Anfragen je
/// Kanal gesendet (R20), werden die Inhaltsfälle in <c>BusySameLane</c> rot; hält eine laufende Anfrage auf dem einen
/// Kanal den anderen auf (Kapitel 5.6), die Fälle in <c>BusyOtherLane</c>.</para>
/// <para><b>Zustände:</b> <c>BeforeConfigure</c> (nach <c>hello</c>, vor <c>configure</c>), <c>ReadyIdle</c> (nach
/// <c>ready</c>, nichts läuft), <c>BusySameLane</c> (auf demselben Kanal läuft Anfrage 1, die Attrappe blockiert),
/// <c>BusyOtherLane</c> (auf dem anderen Kanal läuft eine Anfrage). Die Attrappe wird erst nach der fehlerhaften Nachricht
/// freigegeben; eine Umsetzung, die während einer Anfrage nicht liest, sieht die Nachricht danach.</para>
/// <para><b>Auslegung (Lücke in Regel 3):</b> Regel 3 sagt, dass der Client weitere Anfragen einreiht, nicht, was der
/// Dienst mit einer zweiten Anfrage während einer laufenden tut. Die Fälle in <c>BusySameLane</c> legen fest: Der Dienst
/// nimmt sie an und antwortet in der Reihenfolge der Anfragen (R20), zuerst die laufende, dann der Fehler. Bei einem
/// Rahmenfehler in <c>BusySameLane</c> darf die Antwort der laufenden Anfrage 1 noch kommen; jede andere Antwort nicht.</para>
/// <para><b>Auslegung (Regel 5):</b> Die Grenze von 480 000 Samples gilt je Anfrage, also auch für <c>detect</c>
/// (<see cref="RecognizerService.MaxSamples"/>: „je Anfrage“); Regel 5 nennt sie nur im Satz zu <c>decode</c>.</para>
/// <para><b>Nicht enthalten, und warum:</b> Kanal <c>detect</c> im Zustand <c>BeforeConfigure</c> (die Verbindung
/// <c>detect</c> entsteht erst nach <c>ready</c>, Kapitel 5.4 und Regel 4); <c>ConfigureOnDetect</c>,
/// <c>CancelOnDetect</c>, <c>ShutdownOnDetect</c> auf <c>decode</c> (dort sind es gültige Nachrichten);
/// <c>WrongOrder</c> auf <c>detect</c> (dort ist nur <c>detect</c> gültig, jede andere Nachricht ist schon falscher Kanal
/// oder falsche Richtung); Inhaltsfehler in <c>BeforeConfigure</c> (eine Anfrage vor <c>configure</c> ist bereits ein
/// Reihenfolgefehler, Regel 4); <c>decode</c> während des Ladens (ob der Dienst dann liest, ist offen; nach dem Laden
/// ist dieselbe Nachricht gültig, der Fall wäre nicht eindeutig). Grenzfälle, die gerade noch gültig sind, stehen in
/// <see cref="LimitsThatAreStillValid"/>.</para>
/// </summary>
public sealed class ProtocolErrorMatrixTests
{
    public enum Lane
    {
        Decode,
        Detect,
    }

    public enum State
    {
        BeforeConfigure,
        ReadyIdle,
        BusySameLane,
        BusyOtherLane,
    }

    public enum FrameFault
    {
        /// <summary>Kopflänge 0.</summary>
        HeaderLengthZero,

        /// <summary>Kopflänge 65 537 mit vollständigem, gültigem Kopf dieser Länge.</summary>
        HeaderLengthTooLarge,

        /// <summary>Kein gültiges JSON.</summary>
        InvalidJson,

        /// <summary>Gültiges JSON, aber kein Objekt.</summary>
        JsonNotObject,

        /// <summary>Objekt ohne <c>type</c>.</summary>
        TypeMissing,

        /// <summary>Unbekannter <c>type</c>.</summary>
        TypeUnknown,

        /// <summary><c>payloadBytes</c> über 16 777 216 (nur der Kopf wird gesendet).</summary>
        PayloadTooLarge,

        /// <summary>Anfrage des anderen Kanals (<c>detect</c> auf decode, <c>decode</c> auf detect).</summary>
        WrongLaneRequest,

        /// <summary><c>configure</c> auf detect.</summary>
        ConfigureOnDetect,

        /// <summary><c>cancel</c> auf detect.</summary>
        CancelOnDetect,

        /// <summary><c>shutdown</c> auf detect.</summary>
        ShutdownOnDetect,

        /// <summary>Falsche Reihenfolge: <c>decode</c> vor <c>configure</c>, sonst ein zweites <c>configure</c>.</summary>
        WrongOrder,

        /// <summary>Nachricht der Gegenrichtung (<c>hypothesis</c> bzw. <c>regions</c> von der App).</summary>
        WrongDirection,
    }

    public enum ContentFault
    {
        /// <summary><c>payloadBytes</c> 10 bei <c>sampleCount</c> 3.</summary>
        PayloadMismatch,

        /// <summary><c>offsetTicks</c> −625.</summary>
        OffsetNegative,

        /// <summary><c>offsetTicks</c> 1 000.</summary>
        OffsetNotMultipleOf625,

        /// <summary><c>sampleCount</c> −1.</summary>
        SampleCountNegative,

        /// <summary>480 001 Samples.</summary>
        TooManySamples,
    }

    public static TheoryData<FrameFault, Lane, State> FrameFaultCases()
    {
        var cases = new TheoryData<FrameFault, Lane, State>();
        foreach (var fault in Enum.GetValues<FrameFault>())
        {
            foreach (var lane in Enum.GetValues<Lane>())
            {
                foreach (var state in Enum.GetValues<State>())
                {
                    var detectOnly = fault is FrameFault.ConfigureOnDetect or FrameFault.CancelOnDetect or FrameFault.ShutdownOnDetect;
                    if ((lane == Lane.Detect && state == State.BeforeConfigure)
                        || (detectOnly && lane == Lane.Decode)
                        || (fault == FrameFault.WrongOrder && lane == Lane.Detect))
                    {
                        continue;
                    }

                    cases.Add(fault, lane, state);
                }
            }
        }

        return cases;
    }

    public static TheoryData<ContentFault, Lane, State> ContentFaultCases()
    {
        var cases = new TheoryData<ContentFault, Lane, State>();
        foreach (var fault in Enum.GetValues<ContentFault>())
        {
            foreach (var lane in Enum.GetValues<Lane>())
            {
                foreach (var state in new[] { State.ReadyIdle, State.BusySameLane, State.BusyOtherLane })
                {
                    cases.Add(fault, lane, state);
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(FrameFaultCases))]
    public async Task FrameFaultClosesTheConnectionWithoutAnswer(FrameFault fault, Lane lane, State state)
    {
        using var files = ModelFiles.Create();
        var configuration = files.Configuration();
        await using var service = state == State.BeforeConfigure
            ? await ServiceHarness.CreateAsync(configuration)
            : await ServiceHarness.ReadyAsync(configuration);
        var release = Block(service);
        var channel = service.Channel(lane == Lane.Decode);
        var id = await StartBusy(service, lane, state);

        await channel.SendRawAsync(await FrameFaultBytes(fault, lane, state, id, configuration));
        release.TrySetResult();

        var frames = await channel.ReadToEndAsync();
        Assert.All(frames, frame => Assert.True(
            state == State.BusySameLane && frame.Id == 1 && frame.Type == Answer(lane),
            $"Antwort {frame} auf einen Rahmenfehler ({fault}, {lane}, {state})."));
        await Outcome(lane == Lane.Decode ? service.DecodeRun! : service.DetectRun!);
        Assert.Equal(BusyLane(lane, state) == Lane.Decode ? 1 : 0, service.Whisper.Calls.Count);
        Assert.Equal(BusyLane(lane, state) == Lane.Detect ? 1 : 0, service.Vad.Calls.Count);
        Assert.False(await UnobservedFailureLog.SeenAsync(service.Marker), $"Unbeobachteter Fehler mit Kennung {service.Marker}.");
    }

    [Theory]
    [MemberData(nameof(ContentFaultCases))]
    public async Task ContentFaultIsAnsweredAndTheConnectionStays(ContentFault fault, Lane lane, State state)
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.ReadyAsync(files.Configuration());
        var release = Block(service);
        var channel = service.Channel(lane == Lane.Decode);
        var id = await StartBusy(service, lane, state);

        await channel.SendRawAsync(ContentFaultBytes(fault, lane, id));
        release.TrySetResult();

        if (state == State.BusySameLane)
        {
            var running = await channel.ExpectAsync(Answer(lane));
            Assert.Equal(1, running.Id);
        }

        var failed = await channel.ExpectAsync("failed");
        Assert.Equal(id, failed.Id);
        Assert.Equal(fault == ContentFault.TooManySamples ? RecognizerErrorCode.AudioTooLong : RecognizerErrorCode.InvalidRequest, failed.Code);

        if (state == State.BusyOtherLane)
        {
            var other = await service.Channel(lane != Lane.Decode).ExpectAsync(Answer(lane == Lane.Decode ? Lane.Detect : Lane.Decode));
            Assert.Equal(1, other.Id);
        }

        // Verbindung bleibt: die nächste gültige Anfrage wird beantwortet, ihre Samples kommen bitgenau an
        var follow = Samples(16_000, seed: 7);
        await channel.SendRawAsync(ValidRequest(lane, id + 1, follow));
        var answer = await channel.ExpectAsync(Answer(lane));
        Assert.Equal(id + 1, answer.Id);
        Assert.Equal((state == State.BusySameLane ? 1 : 0) + 1, Calls(service, lane));
        Assert.Equal(state == State.BusyOtherLane ? 1 : 0, Calls(service, lane == Lane.Decode ? Lane.Detect : Lane.Decode));
        Assert.Equal(follow, lane == Lane.Decode ? service.Whisper.Calls[^1].Audio : service.Vad.Calls[^1].Audio);
    }

    /// <summary>
    /// Grenzfälle, die gerade noch gültig sind: Kopflänge genau 65 536, Nutzlast genau 16 777 216 (dann nur
    /// <c>AudioTooLong</c>, kein Rahmenfehler), genau 480 000 Samples, <c>offsetTicks</c> 0 und 625.
    /// </summary>
    [Fact]
    public async Task LimitsThatAreStillValid()
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.ReadyAsync(files.Configuration());

        var header = new JsonObject
        {
            ["type"] = "decode", ["id"] = 1, ["offsetTicks"] = 625, ["sampleCount"] = 0, ["language"] = "de", ["prompt"] = string.Empty,
        };
        await service.Decode.SendRawAsync(FrameCodec.Raw(RecognizerProtocolTests.PaddedHeader(header, FrameCodec.MaxHeaderLength)));
        var padded = await service.Decode.ExpectAsync("hypothesis");
        Assert.Equal(1, padded.Id);
        Assert.Equal(625, padded.Long("audioStartTicks"));

        await service.Decode.SendRawAsync(Messages.Request("decode", 2, 0, FrameCodec.MaxPayloadBytes / 4, FrameCodec.MaxPayloadBytes));
        var largest = await service.Decode.ExpectAsync("failed");
        Assert.Equal(2, largest.Id);
        Assert.Equal(RecognizerErrorCode.AudioTooLong, largest.Code);

        await service.Decode.SendRawAsync(Messages.Decode(3, 0, Samples(RecognizerService.MaxSamples)));
        var full = await service.Decode.ExpectAsync("hypothesis");
        Assert.Equal(3, full.Id);
        Assert.Equal(RecognizerService.MaxSamples, Assert.Single(service.Whisper.Calls).Audio.Length);

        await service.Detect!.SendRawAsync(Messages.Detect(1, 0, Samples(RecognizerService.MaxSamples)));
        Assert.Equal(1, (await service.Detect.ExpectAsync("regions")).Id);
    }

    private static string Answer(Lane lane) => lane == Lane.Decode ? "hypothesis" : "regions";

    private static int Calls(ServiceHarness service, Lane lane) =>
        lane == Lane.Decode ? service.Whisper.Calls.Count : service.Vad.Calls.Count;

    /// <summary>Beide Attrappen blockieren bis zur Freigabe.</summary>
    private static TaskCompletionSource Block(ServiceHarness service)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Whisper.Behavior = service.Whisper.BlocksUntil(release.Task);
        service.Vad.Behavior = service.Vad.BlocksUntil(release.Task);
        return release;
    }

    /// <summary>Der Kanal, auf dem im Zustand eine Anfrage läuft, sonst <c>null</c>.</summary>
    private static Lane? BusyLane(Lane lane, State state) => state switch
    {
        State.BusySameLane => lane,
        State.BusyOtherLane => lane == Lane.Decode ? Lane.Detect : Lane.Decode,
        _ => null,
    };

    /// <summary>Stellt den Zustand her und liefert die <c>id</c> für die nächste Nachricht auf dem geprüften Kanal.</summary>
    private static async Task<long> StartBusy(ServiceHarness service, Lane lane, State state)
    {
        if (BusyLane(lane, state) is not { } busy)
        {
            return 1;
        }

        await service.Channel(busy == Lane.Decode).SendRawAsync(ValidRequest(busy, 1, Samples(16_000)));
        if (busy == Lane.Decode)
        {
            await service.Whisper.StartedAsync(1);
        }
        else
        {
            await service.Vad.StartedAsync(1);
        }

        return state == State.BusySameLane ? 2 : 1;
    }

    private static byte[] ValidRequest(Lane lane, long id, float[] samples) =>
        lane == Lane.Decode ? Messages.Decode(id, Ticks(1), samples) : Messages.Detect(id, Ticks(1), samples);

    private static async Task<byte[]> FrameFaultBytes(FrameFault fault, Lane lane, State state, long id, RecognizerConfiguration configuration)
    {
        var request = lane == Lane.Decode ? "decode" : "detect";
        return fault switch
        {
            FrameFault.HeaderLengthZero => FrameCodec.Raw(0u, []),
            FrameFault.HeaderLengthTooLarge => FrameCodec.Raw(
                RecognizerProtocolTests.PaddedHeader(
                    new JsonObject { ["type"] = request, ["id"] = id, ["offsetTicks"] = 0, ["sampleCount"] = 0 },
                    FrameCodec.MaxHeaderLength + 1)),
            FrameFault.InvalidJson => Messages.Json("{\"type\":\"" + request + "\",\"id\":"),
            FrameFault.JsonNotObject => Messages.Json("[\"" + request + "\"," + id + "]"),
            FrameFault.TypeMissing => Messages.Json("{\"id\":" + id + ",\"offsetTicks\":0,\"sampleCount\":0}"),
            FrameFault.TypeUnknown => Messages.Json("{\"type\":\"plaudern\",\"id\":" + id + "}"),
            FrameFault.PayloadTooLarge => FrameCodec.Encode(state == State.BeforeConfigure
                ? new JsonObject { ["type"] = "configure", ["payloadBytes"] = FrameCodec.MaxPayloadBytes + 1 }
                : new JsonObject
                {
                    ["type"] = request,
                    ["id"] = id,
                    ["offsetTicks"] = 0,
                    ["sampleCount"] = (FrameCodec.MaxPayloadBytes / 4) + 1,
                    ["payloadBytes"] = FrameCodec.MaxPayloadBytes + 4,
                }),
            FrameFault.WrongLaneRequest => ValidRequest(lane == Lane.Decode ? Lane.Detect : Lane.Decode, id, Samples(16_000)),
            FrameFault.ConfigureOnDetect => await ConfigureCapture.CaptureAsync(configuration),
            FrameFault.CancelOnDetect => Messages.Cancel(1),
            FrameFault.ShutdownOnDetect => Messages.Bare("shutdown"),
            FrameFault.WrongOrder => state == State.BeforeConfigure
                ? Messages.Decode(id, 0, Samples(16_000))
                : await ConfigureCapture.CaptureAsync(configuration),
            FrameFault.WrongDirection => FrameCodec.Encode(new JsonObject { ["type"] = lane == Lane.Decode ? "hypothesis" : "regions", ["id"] = id }),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
    }

    private static byte[] ContentFaultBytes(ContentFault fault, Lane lane, long id)
    {
        var type = lane == Lane.Decode ? "decode" : "detect";
        return fault switch
        {
            ContentFault.PayloadMismatch => Messages.Request(type, id, 0, 3, 10),
            ContentFault.OffsetNegative => Messages.Request(type, id, -625, 16_000, 64_000),
            ContentFault.OffsetNotMultipleOf625 => Messages.Request(type, id, 1_000, 16_000, 64_000),
            ContentFault.SampleCountNegative => Messages.Request(type, id, 0, -1, 0),
            ContentFault.TooManySamples => Messages.Request(type, id, 0, RecognizerService.MaxSamples + 1, (RecognizerService.MaxSamples + 1) * 4),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
    }
}
