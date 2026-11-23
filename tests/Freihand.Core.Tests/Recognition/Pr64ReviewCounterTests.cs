using System.Reflection;
using Freihand.Adapters.Recognition;
using Freihand.Core.Tests.Recognition.Kit;

namespace Freihand.Core.Tests.Recognition;

// All launches use FakeLauncher and in-memory protocol streams. No OS processes or desktop calls.
public sealed class Pr64ReviewCounterTests
{
    [Fact]
    public async Task HoldingBeforeTheInitialStartMustDeferIt()
    {
        await using var harness = SupervisorHarness.Create();
        harness.Supervisor.HoldRestarts();
        harness.Supervisor.Start();
        Assert.Equal(0, harness.Launcher.Launches);
        harness.Supervisor.ResumeRestarts();
        await harness.WaitReadyAsync(1);
    }

    [Fact]
    public async Task AHoldAfterChoosingAManualRestartMustStillPreventTheLaunch()
    {
        await using var harness = SupervisorHarness.Create();
        var held = false;
        harness.Supervisor.AvailabilityChanged += (_, state) =>
        {
            if (state != RecognizerAvailability.Starting) return;
            harness.Supervisor.HoldRestarts();
            held = true;
        };
        await harness.Supervisor.RestartAsync();
        Assert.True(held);
        Assert.Equal(0, harness.Launcher.Launches);
        harness.Supervisor.ResumeRestarts();
        await harness.WaitReadyAsync(1);
    }

    [Fact]
    public async Task AHoldAfterChoosingAnAutomaticRestartMustStillPreventTheLaunch()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        var held = false;
        harness.Supervisor.AvailabilityChanged += (_, state) =>
        {
            if (state != RecognizerAvailability.Restarting) return;
            harness.Supervisor.HoldRestarts();
            held = true;
        };
        // Invoke the real fault handler synchronously to place Hold deterministically between
        // choosing the next generation and starting it. This is the same handler used on crash.
        var supervisor = harness.Supervisor;
        var generation = typeof(RecognizerSupervisor).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(supervisor);
        typeof(RecognizerSupervisor).GetMethod("OnEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, [generation]);
        Assert.True(held);
        Assert.Equal(1, harness.Launcher.Launches);
        supervisor.ResumeRestarts();
        await harness.WaitReadyAsync(2);
    }

    [Fact]
    public async Task AManualRestartAlreadyHeldIsDeferredAndResumesOnce()
    {
        await using var harness = SupervisorHarness.Create();
        harness.Supervisor.HoldRestarts();
        await harness.Supervisor.RestartAsync();
        Assert.Equal(0, harness.Launcher.Launches);
        harness.Supervisor.ResumeRestarts();
        await harness.WaitReadyAsync(1);
        harness.Supervisor.ResumeRestarts();
        Assert.Equal(1, harness.Launcher.Launches);
    }
}
