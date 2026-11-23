using System.Reflection;
using Freihand.Adapters.Recognition;
using Freihand.Core.Platform;
using Freihand.Core.Tests.Audio.Kit;
using Freihand.Core.Tests.Recognition.Kit;
using Freihand.Core.Tests.TestKit;
using static Freihand.Core.Tests.Recognition.WordBuilderCases;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>Gegenproben zu PR #27; nur synthetische Daten. Keine Änderung bestehender Tests oder Stubs.</summary>
public sealed class P8ReviewCounterTests
{
    /// <summary>P3 V2, P8 W7/W8 und Kapitel 10.4: U ist der bereits durch W8 korrigierte Start des Vorgängers.</summary>
    [Fact]
    public void C01_ReferenceUsesThePreviousStartAfterItsOwnW8()
    {
        var result = WordBuilderReference.Build(
            [Seg(" a b", Tok(" a", 200, 210), Tok(" b", 199, 200))],
            Offset, OffGridSampleCount, Options.NeighborOverlapTolerance);
        // Audioende 31,995; a wird auf [31,985,31,995) gesetzt. b beginnt bei 31,990 > U.
        // W9 kürzt a auf 31,990. Die geschätzte Zeit überlappt danach nichts; W10 muss annehmen.
        Assert.Null(result.Failure);
        Assert.NotNull(result.Hypothesis);
        Assert.Equal(new[] { Word("a", 31_985, 31_990), Word("b", 31_990, 31_995) }, result.Hypothesis.Words);
    }

    /// <summary>ER-13/ER-17, Kapitel 10.4: Auch monotone Zeitmessung eines korrekten Prüflings muss die Testuhr verwenden.</summary>
    [Fact]
    public void C02_ManualClockAdvancesMonotonicAndUtcTimeTogether()
    {
        var clock = new ManualClock();
        var utc = clock.GetUtcNow();
        var stamp = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), clock.GetUtcNow() - utc);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.GetElapsedTime(stamp, clock.GetTimestamp()));
    }

    /// <summary>BE-18, Kapitel 5.7/10.4: RestartWait begrenzt nur den Aufruf, nicht den laufenden Neustart.</summary>
    [Fact]
    public async Task C03_RestartWaitDoesNotKillTheLoadingRecognizerOrCountAsAnEvent()
    {
        // Entscheid vom 30.10.2026 (b93c31c): Die frühere Erwartung von drei Starts ist ersetzt.
        // RestartWait darf weder den ladenden Erkenner beenden noch das Ereignisfenster belasten.
        var method = typeof(ClientFailureMatrixTests).GetMethod("Expect", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var expected = method.Invoke(null,
            [ClientFailureMatrixTests.Situation.RestartingWithoutReady, ClientFailureMatrixTests.Operation.Decode]);
        Assert.NotNull(expected);
        Assert.Equal(2, expected.GetType().GetProperty("Launches")!.GetValue(expected));

        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await SupervisorHarness.StartedAsync(number => LaunchPlan.Service(loader =>
        {
            if (number >= 2) loader.Gate = load.Task;
        }));
        await using var session = await harness.SessionAsync();
        harness.Launcher.Current.Crash();
        var second = await harness.Launcher.LaunchedAsync(2);
        await Until(() => second.Loader.Calls.Count == 1, "zweiter Erkenner lädt");
        try
        {
            // Vier Wartefristen bleiben mit 60 s unter ModelLoad (120 s). Wären sie Ereignisse,
            // würde die Absturzgrenze erreicht; der einzige echte Ausfall ist aber Start 1.
            for (var i = 0; i < 4; i++)
            {
                var waiting = session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German());
                await harness.Clock.WaitForTimersAsync(1, harness.Timeouts.RestartWait);
                harness.Clock.Advance(harness.Timeouts.RestartWait - Tick);
                Assert.False(waiting.IsCompleted);
                harness.Clock.Advance(Tick);
                await Throws<RecognizerUnavailableException>(waiting);
                Assert.Equal(2, harness.Launcher.Launches);
                Assert.Equal(0, second.KillCalls);
                Assert.False(second.Killed.IsCompleted);
                Assert.False(session.Failed);
                Assert.Equal(RecognizerAvailability.Restarting, harness.Supervisor.Availability);
            }

            // Positive Kontrolle: Derselbe Prozess kann sein Laden abschliessen und wieder dienen.
            load.TrySetResult();
            await harness.WaitReadyAsync(2);
            await session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German()).WaitAsync(Guard);
            Assert.Same(second, harness.Launcher.Current);
            Assert.Equal(2, harness.Launcher.Launches);
            Assert.Equal(0, second.KillCalls);
        }
        finally
        {
            load.TrySetResult();
        }
    }
    /// <summary>Empfehlung BE-18: VadError zählt ausdrücklich zum gemeinsamen Fünf-Minuten-Fenster (Kapitel 10.4).</summary>
    [Fact]
    public async Task C04_FourVadFailuresReachUnavailable()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        for (var i = 1; i <= 4; i++)
        {
            await harness.WaitReadyAsync(i);
            var process = harness.Launcher.Current;
            await using var session = await harness.SessionAsync();
            process.Vad.Behavior = FakeVad.Throws("synthetischer VAD-Ausfall");
            await Throws<RecognizerUnavailableException>(DetectAsync(session.Detector, Samples(16_000), Sec(1)));
            Assert.True(session.Failed);
            await process.Killed.WaitAsync(Guard);
            if (i < 4) await harness.WaitReadyAsync(i + 1);
        }
        await harness.WaitAvailabilityAsync(RecognizerAvailability.Unavailable);
        Assert.Equal(4, harness.Launcher.Launches);
        Assert.Null(harness.Supervisor.LastError);
        await harness.Supervisor.RestartAsync().WaitAsync(Guard);
        await harness.WaitReadyAsync(5);
    }

    /// <summary>Empfehlung ER-13: Discontinuity am ersten Datenpuffer bleibt nach Kapitel 10.4 ohne Wirkung.</summary>
    [Fact]
    public async Task C05_FirstBufferDiscontinuityDoesNotDiscardItsAudio()
    {
        await using var rig = CaptureRig.Open(CaptureRig.NativeFormat);
        await rig.StartedAsync();
        var input = TestSignals.Pcm16Pattern(3_200);
        Assert.True(rig.Feed(input, TimeSpan.FromMilliseconds(200), discontinuity: true, missingFrames: null));
        await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "erster Datenpuffer angenommen");
        rig.Session.Stop();
        var chunks = await ChunkReader.ReadAllAsync(rig.Session);
        Assert.Equal(input, SignalAnalysis.Bytes(chunks));
        Assert.Equal(TimeSpan.Zero, Assert.Single(chunks).Offset);
        Assert.Equal(CaptureStopReason.Requested, rig.Session.Status.StopReason);
    }
}
