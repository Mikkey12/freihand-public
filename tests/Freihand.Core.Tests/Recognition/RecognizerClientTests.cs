using Freihand.Adapters.Recognition;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.Recognition.Kit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Client über Supervisor und Dienst, verbunden über Speicherströme (P8-Konzept, Kapitel 4.6, 5.5–5.7; R16–R18,
/// R21–R27). Die Kombinatorik der Fehler und Zeitgrenzen steht in <see cref="ClientFailureMatrixTests"/>.
/// </summary>
public sealed class RecognizerClientTests
{
    [Fact]
    public async Task R16_LanguageAndPromptTravelWithEveryDecode()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        var hinted = German("Musterbegriff", "Freihand");

        await using (var session = await harness.SessionAsync(hinted))
        {
            await session.Decoder.DecodeAsync(Samples(16_000), Sec(1), hinted).WaitAsync(Guard);
            await session.Decoder.DecodeAsync(Samples(16_000), Sec(2), hinted).WaitAsync(Guard);

            // Optionen je Aufruf (Kapitel 5.5, P3 T43): die Sitzung merkt sich nichts davon
            await session.Decoder.DecodeAsync(Samples(16_000), Sec(3), new RecognitionOptions("fr-CH", [])).WaitAsync(Guard);
        }

        await using (var plain = await harness.SessionAsync(German()))
        {
            await plain.Decoder.DecodeAsync(Samples(16_000), Sec(1), German()).WaitAsync(Guard);
        }

        var decodes = (await harness.Launcher.Current.DecodeConnected).SentBy(PipeSide.App).Where(frame => frame.Type == "decode").ToList();
        Assert.Equal(4, decodes.Count);
        Assert.All(decodes.Take(2), frame =>
        {
            Assert.Equal("de", frame.String("language"));
            Assert.Equal("Musterbegriff, Freihand", frame.String("prompt"));
        });
        Assert.Equal("fr", decodes[2].String("language"));
        Assert.True(WithoutPrompt(decodes[2]), "Ohne Hinweise darf kein Prompt gesendet werden.");
        Assert.Equal("de", decodes[3].String("language"));
        Assert.True(WithoutPrompt(decodes[3]), "Eine Sitzung ohne Hinweise sendet keinen Prompt.");

        // id je Verbindung ab 1 aufsteigend (Kapitel 4.7)
        Assert.Equal(1, decodes[0].Id);
        Assert.True(decodes.Zip(decodes.Skip(1)).All(pair => pair.First.Id < pair.Second.Id), "Die ids steigen nicht auf.");

        var calls = harness.Launcher.Current.Whisper.Calls;
        Assert.Equal(["de", "de", "fr", "de"], calls.Select(call => call.Language));
        Assert.Equal(["Musterbegriff, Freihand", "Musterbegriff, Freihand", null, null], calls.Select(call => call.Prompt));
    }

    [Fact]
    public async Task R17_SamplesArriveBitExactAndTheHypothesisMatchesTheWordBuilder()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync();
        var samples = Samples(24_000, seed: 17);
        samples[0] = -1f;
        samples[1] = 0f;
        samples[2] = 0.999969f;
        samples[3] = -0f;
        samples[4] = float.Epsilon;
        IReadOnlyList<WhisperSegmentData> segments =
        [
            new(" Hallo Welt.", [
                new WhisperTokenData(" Hallo", 0, 40, 0.9f, false),
                new WhisperTokenData(" Welt", 40, 80, 0.8f, false),
                new WhisperTokenData(".", 80, 82, 0.95f, false),
            ]),
        ];
        var whisper = harness.Launcher.Current.Whisper;
        whisper.Behavior = FakeWhisper.Returns(segments);

        var hypothesis = await session.Decoder.DecodeAsync(samples, Sec(12), German()).WaitAsync(Guard);

        var call = Assert.Single(whisper.Calls);
        Assert.Equal(samples.Select(BitConverter.SingleToInt32Bits), call.Audio.Select(BitConverter.SingleToInt32Bits));
        var expected = WhisperWordBuilder.Build(segments, Sec(12), 24_000, WordBuilderOptions.Default);
        Assert.Equal(Sec(12), hypothesis.AudioStart);
        Assert.Equal(Sec(13.5), hypothesis.AudioEnd);
        Assert.Equal(expected.AudioStart, hypothesis.AudioStart);
        Assert.Equal(expected.AudioEnd, hypothesis.AudioEnd);
        Assert.Equal(expected.Words, hypothesis.Words);
    }

    /// <summary>
    /// R18: Nach der Rückkehr überschreibt der Aufrufer sein Array; die aufgezeichnete Nutzlast bleibt, und der Client
    /// greift danach nie mehr auf den Speicher zu (<see cref="GuardedMemory"/> zählt jeden Zugriff). Weitere Aufrufe
    /// auf beiden Kanälen geben dem Client Gelegenheit zu einem späten Zugriff. Ebenso für <c>Detect</c> (P3 Vertrag 1).
    /// </summary>
    [Fact]
    public async Task R18_TheCallerMayOverwriteItsArrayAfterTheCallReturns()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync();
        var process = harness.Launcher.Current;
        var original = Samples(16_000, seed: 18);
        var decoded = new GuardedMemory((float[])original.Clone());
        var detected = new GuardedMemory((float[])original.Clone());

        await session.Decoder.DecodeAsync(decoded.Memory, Sec(1), German()).WaitAsync(Guard);
        decoded.Close();
        Array.Fill(decoded.Array, 0.25f);
        await DetectAsync(session.Detector, detected.Memory, Sec(1)).WaitAsync(Guard);
        detected.Close();
        Array.Fill(detected.Array, -0.25f);

        await session.Decoder.DecodeAsync(Samples(16_000, seed: 2), Sec(2), German()).WaitAsync(Guard);
        await DetectAsync(session.Detector, Samples(16_000, seed: 3), Sec(2)).WaitAsync(Guard);

        Assert.Equal(0, decoded.AccessesAfterClose);
        Assert.Equal(0, detected.AccessesAfterClose);
        Assert.Equal(original, process.Whisper.Calls[0].Audio);
        Assert.Equal(original, process.Vad.Calls[0].Audio);
        var firstDecode = (await process.DecodeConnected).SentBy(PipeSide.App).First(frame => frame.Type == "decode");
        Assert.Equal(original, FrameCodec.Floats(firstDecode.Payload));
    }

    [Fact]
    public async Task R21_CancellationSendsCancelAndDiscardsTheLateResult()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync();
        var whisper = harness.Launcher.Current.Whisper;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        whisper.Behavior = async call =>
        {
            if (call.Number > 1)
            {
                return TestSegments.Of(" Neu", (" Neu", 0, 30));
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Token);
            }
            catch (OperationCanceledException)
            {
                sawCancellation.TrySetResult();
            }

            // liefert trotzdem
            await release.Task.WaitAsync(whisper.Aborted);
            return TestSegments.Of(" Verworfen", (" Verworfen", 0, 30));
        };
        using var cancellation = new CancellationTokenSource();

        var first = session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German(), cancellation.Token);
        await whisper.StartedAsync(1);
        cancellation.Cancel();

        await Throws<OperationCanceledException>(first);
        await sawCancellation.Task.WaitAsync(Guard);
        var sent = (await harness.Launcher.Current.DecodeConnected).SentBy(PipeSide.App);
        var request = Assert.Single(sent, frame => frame.Type == "decode");
        var cancel = Assert.Single(sent, frame => frame.Type == "cancel");
        Assert.Equal(request.Id, cancel.Id);

        release.TrySetResult();
        var next = await session.Decoder.DecodeAsync(Samples(16_000), Sec(2), German()).WaitAsync(Guard);
        Assert.Equal(["Neu"], next.Words.Select(word => word.Text));
        Assert.Equal(Sec(2), next.AudioStart);
    }

    [Fact]
    public async Task R22_DecoderErrorsNeverCarryText()
    {
        const string Secret = "GEHEIM-4711";
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync();
        harness.Launcher.Current.Whisper.Behavior = FakeWhisper.Throws(Secret);

        var error = await Throws<RecognizerException>(session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German()));

        Assert.Equal(RecognizerErrorCode.DecoderError, error.Code);
        AssertNoSecrets(error, Secret);
        var pipe = await harness.Launcher.Current.DecodeConnected;
        var failed = Assert.Single(pipe.SentBy(PipeSide.Recognizer), frame => frame.Type == "failed");
        Assert.Equal(RecognizerErrorCode.DecoderError, failed.Code);
        Assert.DoesNotContain(Secret, FrameCodec.Utf8(pipe.WrittenBy(PipeSide.Recognizer)), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, FrameCodec.Utf8(pipe.WrittenBy(PipeSide.App)), StringComparison.Ordinal);
        Assert.False(await UnobservedFailureLog.SeenAsync(Secret), "Unbeobachteter Fehler mit der Kennung der Attrappe.");
    }

    [Fact]
    public async Task R23_DetectIsNotHeldUpByARunningDecode()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync();
        var whisper = harness.Launcher.Current.Whisper;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        whisper.Behavior = whisper.BlocksUntil(release.Task);

        var decode = session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German());
        await whisper.StartedAsync(1);
        var regions = await DetectAsync(session.Detector, Samples(16_000), Sec(1)).WaitAsync(Guard);

        Assert.False(decode.IsCompleted, "Der Decode ist noch nicht freigegeben.");
        Assert.Equal([new SpeechRegion(Sec(1), Sec(1.5))], regions);
        release.TrySetResult();
        await decode.WaitAsync(Guard);
    }

    /// <summary>
    /// R24: Zwei gleichzeitige <c>DecodeAsync</c> erreichen die Attrappe nacheinander, nie überlappend. Zusätzlich aus
    /// der Aufzeichnung der Verbindung: Der Client sendet das zweite <c>decode</c> erst nach der Antwort auf das erste
    /// (Regel 3: der Client reiht ein).
    /// </summary>
    [Fact]
    public async Task R24_ConcurrentDecodesAreSerialized()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync();
        var whisper = harness.Launcher.Current.Whisper;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = whisper.BlocksUntil(release.Task);
        whisper.Behavior = call => call.Number == 1 ? blocking(call) : Task.FromResult(FakeWhisper.DefaultSegments);

        var first = session.Decoder.DecodeAsync(Samples(16_000, seed: 1), Sec(1), German());
        await whisper.StartedAsync(1);
        var second = session.Decoder.DecodeAsync(Samples(16_000, seed: 2), Sec(2), German());
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(Guard);

        Assert.Equal(1, whisper.MaxConcurrent);
        Assert.Equal([Samples(16_000, seed: 1), Samples(16_000, seed: 2)], whisper.Calls.Select(call => call.Audio));
        var pipe = await harness.Launcher.Current.DecodeConnected;
        var requests = pipe.FramesBy(PipeSide.App).Where(entry => entry.Frame.Type == "decode").ToList();
        var answers = pipe.FramesBy(PipeSide.Recognizer).Where(entry => entry.Frame.Type == "hypothesis").ToList();
        Assert.Equal(2, requests.Count);
        Assert.True(requests[1].Sequence > answers.Single(answer => answer.Frame.Id == requests[0].Frame.Id).Sequence,
            "Das zweite decode ging vor der Antwort auf das erste hinaus.");
    }

    [Fact]
    public async Task R25_RegionsAreShiftedClampedAndEmptyOnesDropped()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync();
        var vad = harness.Launcher.Current.Vad;
        vad.Behavior = FakeVad.Returns((Sec(0.5), Sec(1.2)), (Sec(1.1), Sec(1.4)), (Sec(1.9), Sec(2.5)), (Sec(2.2), Sec(2.4)));

        var regions = await DetectAsync(session.Detector, Samples(32_000), Sec(40)).WaitAsync(Guard);

        Assert.Equal(
            [new SpeechRegion(Sec(40.5), Sec(41.2)), new SpeechRegion(Sec(41.1), Sec(41.4)), new SpeechRegion(Sec(41.9), Sec(42))],
            regions);
        Assert.Single(vad.Calls);
        await DetectAsync(session.Detector, Samples(32_000), Sec(42)).WaitAsync(Guard);
        Assert.Equal(2, vad.Calls.Count);
    }

    [Fact]
    public async Task R26_DecodeWithoutAnswerTimesOutAndTheRecognizerIsKilled()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync();
        var process = harness.Launcher.Current;
        process.Whisper.Behavior = process.Whisper.BlocksUntil(new TaskCompletionSource().Task);

        var decode = session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German());
        await process.Whisper.StartedAsync(1);
        await harness.Clock.WaitForTimersAsync(1, Sec(35));

        // Grenze = DecodeBase 30 s + 5 × 1 s Audio
        harness.Clock.Advance(Sec(35) - Tick);
        Assert.False(decode.IsCompleted, "Zeitgrenze vor 35 s abgelaufen.");
        harness.Clock.Advance(Tick);

        await Throws<RecognizerUnavailableException>(decode);
        await process.Killed.WaitAsync(Guard);
        await harness.WaitReadyAsync(2);
    }

    [Fact]
    public async Task R26_DetectWithoutAnswerTimesOutAfterTwoSeconds()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync();
        var process = harness.Launcher.Current;
        process.Vad.Behavior = process.Vad.BlocksUntil(new TaskCompletionSource().Task);

        var detect = DetectAsync(session.Detector, Samples(16_000), Sec(1));
        await process.Vad.StartedAsync(1);
        await harness.Clock.WaitForTimersAsync(1, Sec(2));

        harness.Clock.Advance(Sec(2) - Tick);
        Assert.False(detect.IsCompleted, "Zeitgrenze vor 2 s abgelaufen.");
        harness.Clock.Advance(Tick);

        await Throws<RecognizerUnavailableException>(detect);
        Assert.True(session.Failed);

        // Detektorfehler, Punkt 3: Neustart wie bei jeder Zeitgrenze
        await process.Killed.WaitAsync(Guard);
        await harness.WaitReadyAsync(2);
    }

    [Fact]
    public async Task R27_NextDecodeWaitsForTheRestartedRecognizer()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await SupervisorHarness.StartedAsync(number => LaunchPlan.Service(loader => loader.Gate = number == 2 ? load.Task : null));
        await using var session = await harness.SessionAsync();
        var first = harness.Launcher.Current;
        first.Whisper.Behavior = first.Whisper.BlocksUntil(new TaskCompletionSource().Task);

        var broken = session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German());
        await first.Whisper.StartedAsync(1);
        first.SeverDecode();
        await Throws<RecognizerUnavailableException>(broken);

        var second = await harness.Launcher.LaunchedAsync(2);
        await Until(() => second.Loader.Calls.Count == 1, "Neustart lädt");
        Assert.Equal(RecognizerAvailability.Restarting, harness.Supervisor.Availability);
        var waiting = session.Decoder.DecodeAsync(Samples(16_000), Sec(2), German());
        Assert.False(waiting.IsCompleted, "DecodeAsync wartet auf den Neustart.");

        load.TrySetResult();
        var hypothesis = await waiting.WaitAsync(Guard);
        Assert.Equal(Sec(2), hypothesis.AudioStart);
        Assert.Single(second.Whisper.Calls);
        Assert.False(session.Failed);
    }

    [Fact]
    public async Task R27_WithoutReadyWithinFifteenSecondsTheDecodeFails()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await SupervisorHarness.StartedAsync(number => LaunchPlan.Service(loader => loader.Gate = number == 2 ? never.Task : null));
        await using var session = await harness.SessionAsync();
        var first = harness.Launcher.Current;
        first.Whisper.Behavior = first.Whisper.BlocksUntil(new TaskCompletionSource().Task);

        var broken = session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German());
        await first.Whisper.StartedAsync(1);
        first.SeverDecode();
        await Throws<RecognizerUnavailableException>(broken);
        var second = await harness.Launcher.LaunchedAsync(2);
        await Until(() => second.Loader.Calls.Count == 1, "Neustart lädt");

        var waiting = session.Decoder.DecodeAsync(Samples(16_000), Sec(2), German());
        await harness.Clock.WaitForTimersAsync(1, Sec(15));
        harness.Clock.Advance(Sec(15) - Tick);
        Assert.False(waiting.IsCompleted, "RestartWait vor 15 s abgelaufen.");
        harness.Clock.Advance(Tick);

        await Throws<RecognizerUnavailableException>(waiting);
        Assert.Empty(second.Whisper.Calls);
    }

    private static bool WithoutPrompt(Frame frame) => string.IsNullOrEmpty(frame.String("prompt"));
}
