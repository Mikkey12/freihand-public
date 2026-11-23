using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using System.Runtime.CompilerServices;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

public sealed class Pr24ReviewCounterTests
{
    [Fact]
    public async Task K01_EngineMustPropagateAnAudioSourceFailure()
    {
        var engine = new FakeRecognitionEngine();
        using var cancel = new CancellationTokenSource();
        var read = ReadAll(engine, cancel.Token);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await cancel.CancelAsync();
            try { await read; } catch (Exception) { }
        }
    }

    private static async Task ReadAll(FakeRecognitionEngine engine, CancellationToken token)
    {
        await foreach (var update in engine.RecognizeAsync(BrokenAudio(), DefaultRecognition, token)) { }
    }

    private static async IAsyncEnumerable<AudioChunk> BrokenAudio()
    {
        await Task.Yield();
        yield return TestAudio.Chunk(Sec(0), Sec(.1));
        throw new InvalidOperationException("Künstlicher Eingabefehler");
    }

    [Fact]
    public async Task K02_TheT23FixtureMustActuallyDriveThreeSpecifiedSteps()
    {
        // Isolierter Zeuge der Eingabesteuerung, KEINE P3-Referenzimplementierung.
        // Audioannahme bleibt unabhängig von einem noch nicht fertig verarbeiteten Auftrag.
        // Angepasst nach A01 (nur Zeuge und Treiberaufruf, Assertions unverändert): Der Zeuge decodiert jetzt über eine
        // Decoder-Attrappe und nur, wenn kein Auftrag läuft; ein Schritt bei laufendem Auftrag entfällt wie in
        // Kapitel 5.2. Statt EndAt(6) treibt der schrittweise Treiber wie in T23 bis 3,5 s, danach erst das Streamende.
        // Dieser Zeuge beginnt den Decode direkt in der Annahmeschleife; getrennte Annahme mit verzögertem Arbeitsstart
        // deckt er nicht ab, das prüft L01 (Pr24FollowupCounterTests).
        var decoder = FakeHypothesisDecoder.Silent();
        decoder.VerifyAudio = false;
        var source = new SchedulingWitness(decoder);
        var run = new SourceRun(source, decoder: decoder);
        await run.StepTo(3.5);
        await run.EndAt(6);
        // Kapitel 7 T23 nennt genau diese drei Audioenden; die aktuelle Fixture erzwingt sie nicht.
        Assert.Contains(Sec(2.5), source.DecodeEnds);
        Assert.Contains(Sec(3), source.DecodeEnds);
        Assert.Contains(Sec(3.5), source.DecodeEnds);
    }

    private sealed class SchedulingWitness(FakeHypothesisDecoder decoder) : ITranscriptSource
    {
        private long _accepted;
        public List<TimeSpan> DecodeEnds { get; } = [];
        public TimeSpan AcceptedEnd => TimeSpan.FromTicks(Interlocked.Read(ref _accepted));
        public TimeSpan Backlog => AcceptedEnd;
        public void MarkBoundary(TimeSpan at) { }

        public async IAsyncEnumerable<TranscriptEvent> RunAsync(IAsyncEnumerable<AudioChunk> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var running = Task.CompletedTask;
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
            {
                var end = chunk.Offset + TimeSpan.FromTicks(chunk.Pcm16Mono.Length / 2 * TestAudio.TicksPerSample);
                Interlocked.Exchange(ref _accepted, end.Ticks);
                // Schritt alle 0,5 s; er entfällt, solange der vorherige Auftrag noch läuft.
                if (end.Ticks % Sec(.5).Ticks == 0 && running.IsCompleted)
                {
                    DecodeEnds.Add(end);
                    running = decoder.DecodeAsync(new float[TestAudio.SampleIndex(end)], TimeSpan.Zero, DefaultRecognition, cancellationToken);
                }
            }
            await running;
            yield break;
        }
    }
}
