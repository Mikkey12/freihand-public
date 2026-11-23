using Freihand.Adapters.Recognition;
using Freihand.Core.Tests.Recognition.Kit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Herunterfahren (Startfehler 0xC0000142): Kündigt Windows das Ende der Sitzung an, startet kein neuer
/// Erkennerprozess; bricht das Ende ab, läuft der vorgemerkte Neustart.
/// </summary>
public sealed class RestartHoldTests
{
    [Fact]
    public async Task ACrashWhileHeldStartsNothingUntilResumed()
    {
        await using var harness = await SupervisorHarness.StartedAsync();

        harness.Supervisor.HoldRestarts();
        harness.Launcher.Current.Crash();
        await harness.WaitAvailabilityAsync(RecognizerAvailability.Restarting);
        await Task.Delay(200);
        Assert.Equal(1, harness.Launcher.Launches);

        harness.Supervisor.ResumeRestarts();
        await harness.WaitReadyAsync(2);
    }

    [Fact]
    public async Task ResumingWithoutACrashStartsNothing()
    {
        await using var harness = await SupervisorHarness.StartedAsync();

        harness.Supervisor.HoldRestarts();
        harness.Supervisor.ResumeRestarts();
        await Task.Delay(200);

        Assert.Equal(1, harness.Launcher.Launches);
        Assert.Equal(RecognizerAvailability.Ready, harness.Supervisor.Availability);
    }
}
