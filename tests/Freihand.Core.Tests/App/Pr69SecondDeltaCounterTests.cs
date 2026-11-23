using System.Collections.Concurrent;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Profiles;
using Freihand.Core.Tests.Recognition.Kit;

namespace Freihand.Core.Tests.App;

public sealed class Pr69SecondDeltaCounterTests
{
    private const string Restart = "ereignis erkenner_neustart";
    private const string Outage = "ereignis erkenner_nicht_verfuegbar";

    [Fact]
    public async Task AnEarlyStartupFailureReachesTheLog()
    {
        await using var supervisor = SupervisorHarness.Create(_ => IncompatibleRecognizer());
        await StartUntilUnavailableAsync(supervisor);
        await using var rig = new ControllerRig();
        var log = new ConcurrentQueue<string>();

        // Startup constructs the controller only after the supervisor has started.
        await using var controller = CreateController(rig, supervisor, log);
        Assert.Equal(RecognizerAvailability.Unavailable, supervisor.Supervisor.Availability);
        Assert.Single(log, line => line == Outage);
    }

    [Fact]
    public async Task AManualRestartThatLaunchesAndBecomesReadyIsLogged()
    {
        await using var supervisor = SupervisorHarness.Create(number =>
            number == 1 ? IncompatibleRecognizer() : LaunchPlan.Service());
        await using var rig = new ControllerRig();
        var log = new ConcurrentQueue<string>();
        await using var controller = CreateController(rig, supervisor, log);
        await StartUntilUnavailableAsync(supervisor);
        Assert.Single(log, line => line == Outage);

        await controller.RestartRecognizerAsync();
        await supervisor.WaitReadyAsync(2);

        Assert.Single(log, line => line == Restart);
    }

    [Fact]
    public async Task TwoClicksDuringTheSameManualRestartOnlyLaunchOneProcess()
    {
        await using var supervisor = SupervisorHarness.Create(number =>
            number == 1 ? IncompatibleRecognizer() : LaunchPlan.Scripted(_ => Task.CompletedTask));
        await using var rig = new ControllerRig();
        var log = new ConcurrentQueue<string>();
        await using var controller = CreateController(rig, supervisor, log);
        await StartUntilUnavailableAsync(supervisor);

        await controller.RestartRecognizerAsync();
        await controller.RestartRecognizerAsync();

        Assert.Equal(2, supervisor.Launcher.Launches);
    }

    [Fact]
    public async Task ADeferredRestartDoesNotCountUntilItLaunches()
    {
        await using var supervisor = await SupervisorHarness.StartedAsync();
        await using var rig = new ControllerRig();
        var log = new ConcurrentQueue<string>();
        await using var controller = CreateController(rig, supervisor, log);
        var restartRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Supervisor.AvailabilityChanged += (_, availability) =>
        {
            if (availability == RecognizerAvailability.Restarting)
                restartRequested.TrySetResult();
        };

        supervisor.Supervisor.HoldRestarts();
        supervisor.Launcher.Current.Crash();
        await restartRequested.Task.WaitAsync(RecognitionTestKit.Guard);

        Assert.Equal(1, supervisor.Launcher.Launches);
        Assert.DoesNotContain(Restart, log);
    }

    private static DictationController CreateController(ControllerRig rig, SupervisorHarness supervisor, ConcurrentQueue<string> log) =>
        new(supervisor.Supervisor, rig.Factory, rig.Audio, rig.Binding, rig.Output, rig.Guard, rig.Hotkeys,
            rig.Clipboard, ControllerRig.Options(ProfileStrictness.Standard), log.Enqueue);

    private static LaunchPlan IncompatibleRecognizer() => LaunchPlan.Scripted(async scripted =>
    {
        var decode = scripted.Connect();
        await decode.SendAsync(Messages.Hello(2, "decode"));
        await decode.ReadToEndOrBreakAsync();
    });

    private static async Task StartUntilUnavailableAsync(SupervisorHarness supervisor)
    {
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Supervisor.AvailabilityChanged += (_, availability) =>
        {
            if (availability == RecognizerAvailability.Unavailable)
                notified.TrySetResult();
        };
        supervisor.Supervisor.Start();
        await notified.Task.WaitAsync(RecognitionTestKit.Guard);
    }
}