using System.Text;
using System.Text.Json.Nodes;
using Freihand.Adapters.Recognition;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.Recognition.Kit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Protokoll der Pipe am Dienst, mit rohen Rahmen der Testseite (P8-Konzept, Kapitel 4.7; R13, R19, R20, R28).
/// Die vollständige Kombinatorik der Rahmen- und Inhaltsfehler steht in <see cref="ProtocolErrorMatrixTests"/>.
/// </summary>
public sealed class RecognizerProtocolTests
{
    [Fact]
    public async Task R13_EmptyDecodeSkipsWhisperAndTooLongAudioFails()
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.ReadyAsync(files.Configuration());

        // sampleCount 0 bei 10 s: leere Hypothese [10 s, 10 s), ohne Whisper (Regel 5)
        await service.Decode.SendRawAsync(Messages.Decode(1, Ticks(10), []));
        var empty = await service.Decode.ExpectAsync("hypothesis");
        Assert.Equal(1, empty.Id);
        Assert.Equal(Ticks(10), empty.Long("audioStartTicks"));
        Assert.Equal(Ticks(10), empty.Long("audioEndTicks"));
        Assert.Empty(empty.Words);
        Assert.Empty(service.Whisper.Calls);

        // 480 001 Samples: failed mit AudioTooLong, ohne Whisper
        await service.Decode.SendRawAsync(Messages.Decode(2, 0, Samples(RecognizerService.MaxSamples + 1)));
        var tooLong = await service.Decode.ExpectAsync("failed");
        Assert.Equal(2, tooLong.Id);
        Assert.Equal(RecognizerErrorCode.AudioTooLong, tooLong.Code);
        Assert.Empty(service.Whisper.Calls);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(70_000u)]
    public async Task R19_HeaderLengthOutsideTheLimitsClosesWithoutAnswer(uint headerLength)
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.ReadyAsync(files.Configuration());

        // Ein vollständiger Kopf dieser Länge (bei 70 000 ein gültiges JSON-Objekt, mit Leerraum aufgefüllt), damit die
        // Ablehnung nur an der Kopflänge liegt.
        var header = headerLength == 0 ? [] : PaddedHeader(new JsonObject { ["type"] = "decode", ["id"] = 1 }, (int)headerLength);
        await service.Decode.SendRawAsync(FrameCodec.Raw(headerLength, header));

        Assert.Empty(await service.Decode.ReadToEndAsync());
        await Outcome(service.DecodeRun!);
        Assert.Empty(service.Whisper.Calls);
    }

    [Fact]
    public async Task R19_ContentFaultsAreAnsweredAndTheConnectionStays()
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.ReadyAsync(files.Configuration());

        // payloadBytes 10 bei sampleCount 3
        await service.Decode.SendRawAsync(Messages.Request("decode", 1, 0, 3, 10));
        var mismatch = await service.Decode.ExpectAsync("failed");
        Assert.Equal(1, mismatch.Id);
        Assert.Equal(RecognizerErrorCode.InvalidRequest, mismatch.Code);

        // danach wird eine gültige Anfrage beantwortet
        await service.Decode.SendRawAsync(Messages.Decode(2, Ticks(1), Samples(16_000)));
        var answered = await service.Decode.ExpectAsync("hypothesis");
        Assert.Equal(2, answered.Id);

        // offsetTicks 1 000: kein Vielfaches von 625
        await service.Decode.SendRawAsync(Messages.Request("decode", 3, 1_000, 16_000, 64_000));
        var offset = await service.Decode.ExpectAsync("failed");
        Assert.Equal(3, offset.Id);
        Assert.Equal(RecognizerErrorCode.InvalidRequest, offset.Code);

        Assert.Single(service.Whisper.Calls);
    }

    /// <summary>
    /// R20: 100 Anfragen, <c>decode</c> und <c>detect</c> gemischt und gleichzeitig auf beiden Kanälen. Die Attrappen
    /// liefern, werfen oder werden abgebrochen, je nach Nummer des Aufrufs (die Schlussanfrage je Kanal folgt derselben
    /// Regel). Je Kanal läuft höchstens eine Anfrage
    /// (Regel 3, die Testseite reiht wie der Client ein). Eingestreut: <c>cancel</c> für eine schon beantwortete und
    /// für eine unbekannte <c>id</c>; sie dürfen nichts ändern und keine Nachricht erzeugen. Weil die Testseite nach
    /// jeder Anfrage genau einen Rahmen liest und eine Schlussanfrage je Kanal folgt, fällt jede überzählige oder
    /// fehlende Antwort als falsche <c>id</c> auf.
    /// </summary>
    [Fact]
    public async Task R20_EveryRequestGetsExactlyOneAnswerInRequestOrder()
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.ReadyAsync(files.Configuration());
        var cancellable = service.Whisper.WaitsForCancellation();
        service.Whisper.Behavior = call => (call.Number % 4) switch
        {
            1 => Task.FromResult(FakeWhisper.DefaultSegments),
            2 => Task.FromException<IReadOnlyList<WhisperSegmentData>>(new InvalidOperationException("Attrappe wirft")),
            3 => cancellable(call),
            _ => Task.FromResult<IReadOnlyList<WhisperSegmentData>>([]),
        };
        service.Vad.Behavior = call => call.Number % 2 == 1
            ? FakeVad.DefaultRegions(call)
            : throw new InvalidOperationException("Attrappe wirft");

        var decodes = Enumerable.Range(1, 100).Count(k => k % 3 != 0);
        var detects = 100 - decodes;

        async Task DecodeLane()
        {
            for (var id = 1; id <= decodes + 1; id++)
            {
                var sentinel = id == decodes + 1;
                await service.Decode.SendRawAsync(Messages.Decode(id, Ticks(id), Samples(16_000, id)));
                if (id % 4 == 3)
                {
                    await service.Whisper.StartedAsync(id);
                    await service.Decode.SendRawAsync(Messages.Cancel(id));
                }

                var answer = await service.Decode.ReadAsync();
                Assert.NotNull(answer);
                Assert.Equal(id, answer.Id);
                (string Type, RecognizerErrorCode? Code) expected = (id % 4) switch
                {
                    1 or 0 => ("hypothesis", null),
                    2 => ("failed", RecognizerErrorCode.DecoderError),
                    _ => ("cancelled", null),
                };
                Assert.Equal(expected.Type, answer.Type);
                Assert.Equal(expected.Code, answer.Code);
                if (id % 4 == 0)
                {
                    Assert.Empty(answer.Words);
                }

                if (!sentinel && id % 10 == 0)
                {
                    // schon beantwortet bzw. unbekannt: ohne Wirkung, keine Nachricht
                    await service.Decode.SendRawAsync(Messages.Cancel(id));
                    await service.Decode.SendRawAsync(Messages.Cancel(9_999));
                }
            }
        }

        async Task DetectLane()
        {
            for (var id = 1; id <= detects + 1; id++)
            {
                await service.Detect!.SendRawAsync(Messages.Detect(id, Ticks(id), Samples(16_000, id)));
                var answer = await service.Detect.ReadAsync();
                Assert.NotNull(answer);
                Assert.Equal(id, answer.Id);
                var regions = id % 2 == 1;
                Assert.Equal(regions ? "regions" : "failed", answer.Type);
                Assert.Equal(regions ? null : (RecognizerErrorCode?)RecognizerErrorCode.VadError, answer.Code);
            }
        }

        await Task.WhenAll(DecodeLane(), DetectLane()).WaitAsync(Guard);

        // Gesamtbild aus der Aufzeichnung: je Kanal hello (decode zusätzlich ready), dann genau eine Antwort je id
        var decodeFrames = FrameCodec.Parse(service.DecodePipe.WrittenBy(PipeSide.Recognizer));
        Assert.Equal(["hello", "ready"], decodeFrames.Take(2).Select(frame => frame.Type));
        Assert.Equal(Enumerable.Range(1, decodes + 1).Select(id => (long?)id), decodeFrames.Skip(2).Select(frame => frame.Id));
        var detectFrames = FrameCodec.Parse(service.DetectPipe!.WrittenBy(PipeSide.Recognizer));
        Assert.Equal("hello", detectFrames[0].Type);
        Assert.Equal(Enumerable.Range(1, detects + 1).Select(id => (long?)id), detectFrames.Skip(1).Select(frame => frame.Id));
        Assert.Equal(decodes + 1, service.Whisper.Calls.Count);
        Assert.Equal(detects + 1, service.Vad.Calls.Count);
        Assert.False(await UnobservedFailureLog.SeenAsync(service.Marker), $"Unbeobachteter Fehler mit Kennung {service.Marker}.");
    }

    /// <summary>R28, Teil Dienst: <c>decode</c> vor <c>configure</c> (also vor <c>ready</c>) ist ein Rahmenfehler (Regel 4).</summary>
    [Fact]
    public async Task R28_DecodeBeforeReadyEndsTheConnection()
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.CreateAsync(files.Configuration());

        await service.Decode.SendRawAsync(Messages.Decode(1, 0, Samples(16_000)));

        Assert.Empty(await service.Decode.ReadToEndAsync());
        await Outcome(service.DecodeRun!);
        Assert.Empty(service.Loader.Calls);
        Assert.Empty(service.Whisper.Calls);
    }

    /// <summary>Ein JSON-Objekt, mit Leerraum genau auf <paramref name="length"/> Byte aufgefüllt (gültiges JSON).</summary>
    internal static byte[] PaddedHeader(JsonObject header, int length)
    {
        var json = Encoding.UTF8.GetBytes(header.ToJsonString());
        Assert.True(json.Length <= length);
        var padded = new byte[length];
        json.CopyTo(padded, 0);
        padded.AsSpan(json.Length).Fill((byte)' ');
        return padded;
    }
}
