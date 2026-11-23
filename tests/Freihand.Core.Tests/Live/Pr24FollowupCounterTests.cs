using System.Runtime.CompilerServices;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

public sealed class Pr24FollowupCounterTests
{
    [Fact]
    public async Task L01_StepToMustWaitForAScheduledDecodeToStartAndFinish()
    {
        // A01: Audioannahme und Decoderarbeit dürfen auf getrennten Aufgaben laufen.
        var decoder = FakeHypothesisDecoder.Silent();
        decoder.VerifyAudio = false;
        var source = new QueuedWitness(decoder);
        var run = new SourceRun(source, decoder: decoder);
        var step = run.StepTo(0.5);
        bool returnedBeforeDecode;
        try
        {
            await Until(() => source.AcceptedEnd == Sec(0.5) && run.Feed.Drained,
                "Audio angenommen, Arbeit eingeplant, Leser wartet");
            // Begrenztes Beobachtungsfenster; die Arbeit selbst bleibt deterministisch gesperrt.
            await Task.WhenAny(step, Task.Delay(200));
            returnedBeforeDecode = step.IsCompleted;
        }
        finally
        {
            source.StartWorker.TrySetResult();
            await step.WaitAsync(TimeSpan.FromSeconds(2));
            await run.EndAt(0.5);
        }
        Assert.False(returnedBeforeDecode, "StepTo kehrte vor dem eingeplanten Decode zurück.");
        Assert.Contains(decoder.Requests, request => request.End == Sec(0.5));
    }

    [Fact]
    public async Task L02_T55MustAcceptStoppingAfterTheFirstLateEvent()
    {
        // A03: Eine Sitzung darf nach dem ersten verspäteten Ereignis aufhören.
        var source = new ScriptedTranscriptSource();
        var feed = new AudioFeed();
        var reader = source.RunAsync(feed.ReadAllAsync()).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await Until(() => source.Waiting, "Empfänger wartet");
        var before = source.Delivered;
        source.Emit(new ProvisionalText(Guid.NewGuid(), "Spät"));
        source.Emit(new LiveNotice(LiveNoticeKind.SpeechWithoutText, Sec(1), Sec(2)));
        source.Emit(new UtteranceCompleted(Utterance(2, "Spät")));
        Assert.True(await next);
        await reader.DisposeAsync();
        Assert.True(source.Released);
        // Modell der aktuellen T55-Wartebedingung in Zeile 458.
        Assert.True(CurrentT55DeliveryCondition(source, before),
            "Der zulässige Abbruch nach einer späten Lieferung erfüllt die T55-Bedingung nicht.");
    }

    // Angepasst nach L02 (nur die Modellfunktion, Assertions unverändert): T55 wartet jetzt auf die gemeinsame Bedingung
    // der steuerbaren Quelle. Sie gilt, wenn die Sitzung alle drei späten Ereignisse abgeholt oder die Quelle freigegeben
    // hat; beides ist nach dem Abbruch erlaubt.
    private static bool CurrentT55DeliveryCondition(ScriptedTranscriptSource source, int before) =>
        source.LateDeliverySettled(before, 3);

    // Nur Scheduling-Zeuge, keine vollständige P3-Implementierung. Kein Step wird verworfen.
    private sealed class QueuedWitness(FakeHypothesisDecoder decoder) : ITranscriptSource
    {
        private long accepted;
        public TaskCompletionSource StartWorker { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan AcceptedEnd => TimeSpan.FromTicks(Interlocked.Read(ref accepted));
        public TimeSpan Backlog => AcceptedEnd;
        public void MarkBoundary(TimeSpan at) { }

        public async IAsyncEnumerable<TranscriptEvent> RunAsync(IAsyncEnumerable<AudioChunk> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Task job = Task.CompletedTask;
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
            {
                var end = chunk.Offset + TimeSpan.FromTicks(chunk.Pcm16Mono.Length / 2 * 625L);
                Interlocked.Exchange(ref accepted, end.Ticks);
                if (end == Sec(0.5)) job = RunQueued(end, cancellationToken);
            }
            await job;
            yield break;
        }

        private async Task RunQueued(TimeSpan end, CancellationToken cancellationToken)
        {
            await StartWorker.Task.WaitAsync(cancellationToken);
            await decoder.DecodeAsync(new float[8_000], end - Sec(0.5), DefaultRecognition, cancellationToken);
        }
    }
}
