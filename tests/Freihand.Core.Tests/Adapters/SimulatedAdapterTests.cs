using Freihand.Adapters;
using Freihand.Adapters.Fakes;
using Freihand.Adapters.Selection;
using Freihand.Core.Quality;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;
using Freihand.Core.Segments;
using Freihand.Core.Transform;

namespace Freihand.Core.Tests.Adapters;

public class SimulatedRecognitionEngineTests
{
    private static readonly RecognitionOptions Options = new("de-CH", []);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task YieldsScriptedUpdatesWithResponseDelay()
    {
        var clock = new VirtualClock();
        var first = Update("Guten");
        var second = Update("Guten Tag");
        var engine = new SimulatedRecognitionEngine(clock, [first, second], new SimulatedRecognitionOptions
        {
            ResponseDelay = TimeSpan.FromMilliseconds(300),
        });

        var updates = await engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 3, Second), Options).ToListAsync();

        // Drei Stücke, aber nur zwei Zwischenstände im Skript; je 300 ms Verzögerung nach dem Stück
        Assert.Equal([first, second], updates);
        Assert.Equal(TimeSpan.FromSeconds(3.3), clock.Now);
        Assert.Equal(3, engine.Trace.Count);
    }

    [Fact]
    public async Task SlowProcessingBuildsBacklog()
    {
        // ER-05: Faktor 1,5 → jedes 1-s-Stück braucht 1,5 s; Rückstand nach Stück k = 1 s + 0,5 s · (k + 1)
        var clock = new VirtualClock();
        var engine = new SimulatedRecognitionEngine(clock, [], new SimulatedRecognitionOptions { ProcessingFactor = 1.5 });

        await engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 20, Second), Options).ToListAsync();

        var backlog = engine.Trace.Select(r => r.Backlog.TotalSeconds).ToArray();
        Assert.Equal(1.5, backlog[0], 10);
        Assert.Equal(6.0, backlog[9], 10);
        Assert.Equal(11.0, backlog[19], 10);
        // Warnschwelle 10 s (ER-05): 1 + 0,5 · 18 = 10 ab Stück 17
        Assert.Equal(17, engine.Trace.First(r => r.Backlog >= TimeSpan.FromSeconds(10)).ChunkIndex);
        Assert.Equal(1.5, RealTimeFactor.Compute(engine.Trace.Select(r => r.ComputeTime), TimeSpan.FromSeconds(20)), 10);
    }

    [Fact]
    public async Task FastProcessingKeepsUp()
    {
        var clock = new VirtualClock();
        var engine = new SimulatedRecognitionEngine(clock, [], new SimulatedRecognitionOptions { ProcessingFactor = 0.5 });

        await engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 10, Second), Options).ToListAsync();

        Assert.All(engine.Trace, r => Assert.Equal(TimeSpan.FromSeconds(0.5), r.Backlog));
    }

    [Fact]
    public async Task RecomputationCountsTowardsRealTimeFactor()
    {
        // Jedes Stück berechnet bis 2 s altes Audio neu: 1 + 2 + 8 · 3 = 27 s Audio, Faktor 0,1 → 2,7 s
        var clock = new VirtualClock();
        var engine = new SimulatedRecognitionEngine(clock, [], new SimulatedRecognitionOptions
        {
            ProcessingFactor = 0.1,
            Recompute = TimeSpan.FromSeconds(2),
        });

        await engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 10, Second), Options).ToListAsync();

        var samples = engine.Trace.Select(r => r.ToComputeSample()).ToArray();
        Assert.Equal(TimeSpan.FromSeconds(27), engine.Trace.Aggregate(TimeSpan.Zero, (s, r) => s + r.ComputedAudio));
        Assert.Equal(0.27, RealTimeFactor.Compute(samples.Select(s => s.ComputeTime), TimeSpan.FromSeconds(10)), 10);
        Assert.Equal(TimeSpan.FromSeconds(9), samples[^1].AudioStart);
    }

    [Fact]
    public async Task OutageFailsSessionAndRecoveryAllowsNewOne()
    {
        var clock = new VirtualClock();
        var engine = new SimulatedRecognitionEngine(clock, [], new SimulatedRecognitionOptions
        {
            Route = RecognitionRoute.Online,
            ProcessingFactor = 0.2,
            Outages = [new OutageWindow(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8))],
        });

        var failure = await Assert.ThrowsAsync<AdapterFailureException>(
            async () => await engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 10, Second), Options).ToListAsync());

        Assert.Equal(AdapterFailureKind.Unavailable, failure.Kind);
        Assert.Equal(4, engine.Trace.Count);
        Assert.False(engine.IsAvailable);

        await clock.WaitUntilAsync(TimeSpan.FromSeconds(8));
        Assert.True(engine.IsAvailable);

        await engine.RecognizeAsync(SimulatedAudio.LiveAsync(new VirtualClock(), 2, Second), Options).ToListAsync();
        Assert.Equal(2, engine.Sessions);
    }

    [Fact]
    public async Task RejectsSessionDuringOutage()
    {
        var clock = new VirtualClock();
        var engine = new SimulatedRecognitionEngine(clock, [], new SimulatedRecognitionOptions
        {
            Outages = [new OutageWindow(TimeSpan.Zero, TimeSpan.FromSeconds(1))],
        });

        await Assert.ThrowsAsync<AdapterFailureException>(
            async () => await engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 1, Second), Options).ToListAsync());
        Assert.Empty(engine.Trace);
    }

    [Fact]
    public async Task AbortsAfterConfiguredChunks()
    {
        var clock = new VirtualClock();
        var engine = new SimulatedRecognitionEngine(clock, [Update("a"), Update("b"), Update("c")], new SimulatedRecognitionOptions
        {
            AbortAfterChunks = 2,
        });
        var received = new List<RecognitionUpdate>();

        var failure = await Assert.ThrowsAsync<AdapterFailureException>(async () =>
        {
            await foreach (var update in engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 5, Second), Options))
            {
                received.Add(update);
            }
        });

        Assert.Equal(AdapterFailureKind.Aborted, failure.Kind);
        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task CancellationStopsDuringProcessing()
    {
        var clock = new VirtualClock();
        var engine = new SimulatedRecognitionEngine(clock, [Update("a"), Update("b"), Update("c")], new SimulatedRecognitionOptions
        {
            ProcessingFactor = 0.5,
        });
        using var cts = new CancellationTokenSource();
        var received = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 5, Second), Options, cts.Token))
            {
                received++;
                await cts.CancelAsync();
            }
        });

        Assert.Equal(1, received);
        Assert.Single(engine.Trace);
    }

    [Fact]
    public async Task SameSetupGivesSameTrace()
    {
        static async Task<IReadOnlyList<ProcessingRecord>> RunAsync()
        {
            var clock = new VirtualClock();
            var engine = new SimulatedRecognitionEngine(clock, [], new SimulatedRecognitionOptions
            {
                ProcessingFactor = 1.3,
                Recompute = TimeSpan.FromMilliseconds(700),
                ResponseDelay = TimeSpan.FromMilliseconds(120),
            });
            await engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 30, TimeSpan.FromMilliseconds(500)), Options).ToListAsync();
            return engine.Trace;
        }

        Assert.Equal(await RunAsync(), await RunAsync());
    }

    [Fact]
    public void SilenceHasExpectedLength()
    {
        var chunk = SimulatedAudio.Silence(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));

        Assert.Equal(3200, chunk.Pcm16Mono.Length);
        Assert.Equal(TimeSpan.FromMilliseconds(100), SimulatedAudio.DurationOf(chunk));
        Assert.True(chunk.Pcm16Mono.Span.IndexOfAnyExcept((byte)0) < 0);
    }

    private static RecognitionUpdate Update(string provisional) =>
        new([], [new TextSegment(Guid.Empty, provisional, SegmentState.Provisional, TimeSpan.Zero, TimeSpan.Zero)]);
}

public class SimulatedTransformerTests
{
    private static readonly TransformRequest Request = new("Höflich formulieren", "mach das", DataClass.Internal);

    [Fact]
    public async Task EchoesTextAfterDelay()
    {
        var clock = new VirtualClock();
        var transformer = new SimulatedTransformer(clock, new SimulatedTransformOptions { Delay = TimeSpan.FromSeconds(2) });

        var result = await transformer.TransformAsync(Request);

        Assert.Equal("mach das", result.Text);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Now);
        Assert.Equal(TransformRoute.Local, transformer.Route);
    }

    [Fact]
    public async Task AppliesConfiguredTransformation()
    {
        var transformer = new SimulatedTransformer(new VirtualClock(), new SimulatedTransformOptions
        {
            Transform = r => r.Text.ToUpperInvariant(),
        });

        Assert.Equal("MACH DAS", (await transformer.TransformAsync(Request)).Text);
    }

    [Fact]
    public async Task FailsDuringOutageAndRecovers()
    {
        var clock = new VirtualClock();
        var transformer = new SimulatedTransformer(clock, new SimulatedTransformOptions
        {
            Route = TransformRoute.Home,
            Delay = TimeSpan.FromSeconds(1),
            Outages = [new OutageWindow(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))],
        });

        // Ausfall beginnt während des Auftrags: kein Ergebnis
        var failure = await Assert.ThrowsAsync<AdapterFailureException>(() => transformer.TransformAsync(Request));
        Assert.Equal(AdapterFailureKind.Unavailable, failure.Kind);

        await clock.WaitUntilAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("mach das", (await transformer.TransformAsync(Request)).Text);
    }

    [Fact]
    public async Task AbortsAfterConfiguredCalls()
    {
        var transformer = new SimulatedTransformer(new VirtualClock(), new SimulatedTransformOptions { AbortAfterCalls = 1 });

        await transformer.TransformAsync(Request);
        var failure = await Assert.ThrowsAsync<AdapterFailureException>(() => transformer.TransformAsync(Request));

        Assert.Equal(AdapterFailureKind.Aborted, failure.Kind);
        Assert.Equal(2, transformer.Calls);
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SimulatedTransformer(new VirtualClock()).TransformAsync(Request, cts.Token));
    }
}

/// <summary>Ausfall eines Wegs mitten im Diktat und Wechsel auf einen erlaubten Ausweichweg (WE-10).</summary>
public class FallbackScenarioTests
{
    private static readonly RecognitionOptions Options = new("de-CH", []);

    [Fact]
    public async Task SwitchesToAllowedFallbackAndBackAfterRecovery()
    {
        var clock = new VirtualClock();
        var home = new SimulatedRecognitionEngine(clock, [], new SimulatedRecognitionOptions
        {
            Route = RecognitionRoute.Home,
            ProcessingFactor = 0.1,
            Outages = [new OutageWindow(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10))],
        });
        var device = new SimulatedRecognitionEngine(clock, [], new SimulatedRecognitionOptions { ProcessingFactor = 0.4 });
        // Richtlinie aus P1: „intern“ erlaubt Heim und Gerät (WE-03)
        var registry = new AdapterRegistry(Edition.Private, new RoutePolicy(new RoutePolicyOptions(Edition.Private)));
        registry.Register(new RecognitionAdapter("heim", new RouteEndpoint(EndpointKind.Home, "sprichblitz.example"), home));
        registry.Register(new RecognitionAdapter("geraet", RouteEndpoint.ThisDevice, device));

        SelectionRequest Request(params string[] unavailable) => new(
            DataClass.Internal,
            RecognitionRoute.Home,
            TransformRoute.None,
            RecognitionFallbacks: [RecognitionRoute.Device],
            Unavailable: unavailable.ToHashSet());

        var first = registry.Select(Request());
        Assert.Equal("heim", first.Recognition!.Id);
        await Assert.ThrowsAsync<AdapterFailureException>(async () =>
            await first.Recognition!.Engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 10, TimeSpan.FromSeconds(1)), Options).ToListAsync());

        var fallback = registry.Select(Request("heim"));
        Assert.True(fallback.IsFallback);
        Assert.Equal(RecognitionRoute.Device, fallback.ActiveRecognition);
        await fallback.Recognition!.Engine.RecognizeAsync(SimulatedAudio.LiveAsync(clock, 2, TimeSpan.FromSeconds(1)), Options).ToListAsync();

        await clock.WaitUntilAsync(TimeSpan.FromSeconds(10));
        Assert.True(home.IsAvailable);
        var recovered = registry.Select(Request());
        Assert.False(recovered.IsFallback);
        Assert.Equal("heim", recovered.Recognition!.Id);
    }
}
