using System.Collections.Concurrent;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Profiles;
using Freihand.Core.Tests.Recognition.Kit;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Eigene Vorprüfung zum Codex-Delta von PR #69: mit der echten Aufsicht genau eine Neustartzeile je tatsächlich
/// gestartetem Prozess, auch nach einem zurückgehaltenen Neustart und bei zwei Klicks.
/// </summary>
public sealed class RecognizerRestartSupervisorLogTests
{
    private const string Restart = "ereignis erkenner_neustart";

    [Fact]
    public async Task ACrashGivesExactlyOneRestartLine()
    {
        await using var supervisor = await SupervisorHarness.StartedAsync();
        await using var rig = new ControllerRig();
        var log = new ConcurrentQueue<string>();
        await using var controller = CreateController(rig, supervisor, log);

        supervisor.Launcher.Current.Crash();
        await supervisor.WaitReadyAsync(2);

        await RecognitionTestKit.Until(() => log.Count(line => line == Restart) == 1, "eine Neustartzeile");
        await RecognitionTestKit.Settle();
        Assert.Single(log, line => line == Restart);
    }

    [Fact]
    public async Task AHeldRestartCountsOnceWhenItLaunches()
    {
        await using var supervisor = await SupervisorHarness.StartedAsync();
        await using var rig = new ControllerRig();
        var log = new ConcurrentQueue<string>();
        await using var controller = CreateController(rig, supervisor, log);

        supervisor.Supervisor.HoldRestarts();
        supervisor.Launcher.Current.Crash();
        await RecognitionTestKit.Until(() => supervisor.Supervisor.Availability == RecognizerAvailability.Restarting, "Restarting");
        await RecognitionTestKit.Settle();
        Assert.DoesNotContain(Restart, log);

        supervisor.Supervisor.ResumeRestarts();
        await supervisor.WaitReadyAsync(2);
        await RecognitionTestKit.Until(() => log.Count(line => line == Restart) == 1, "eine Neustartzeile");
        await RecognitionTestKit.Settle();
        Assert.Single(log, line => line == Restart);
    }

    [Fact]
    public async Task TwoClicksGiveOneRestartLine()
    {
        await using var supervisor = SupervisorHarness.Create(number =>
            number == 1 ? IncompatibleRecognizer() : LaunchPlan.Service());
        await using var rig = new ControllerRig();
        var log = new ConcurrentQueue<string>();
        await using var controller = CreateController(rig, supervisor, log);
        supervisor.Supervisor.Start();
        await RecognitionTestKit.Until(() => supervisor.Supervisor.Availability == RecognizerAvailability.Unavailable, "Unavailable");

        await controller.RestartRecognizerAsync();
        await controller.RestartRecognizerAsync();
        await supervisor.WaitReadyAsync(2);

        await RecognitionTestKit.Settle();
        Assert.Equal(2, supervisor.Launcher.Launches);
        Assert.Single(log, line => line == Restart);
        Assert.Single(log, line => line == "ereignis erkenner_nicht_verfuegbar");
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
}
