namespace Freihand.App;

/// <summary>
/// BT01: „Freihand.exe --beenden“ bittet nur das laufende Freihand, sich zu beenden, und
/// startet selbst nichts. Echter Zusammenbau, Betriebssystemanschlüsse ersetzt.
/// </summary>
[Collection(AppAssembly.Name)]
public sealed class BetaAppStartTests
{
    [Fact]
    public void ExitArgumentOnlySignalsAndStartsNothing()
    {
        RecognizerSupervisor.Starts = 0;
        Shell.Offered = Shell.NewOffer();
        var before = InstanceSignal.ExitRequests;

        new ProbeApp().Start("--beenden");

        Assert.Equal(before + 1, InstanceSignal.ExitRequests);
        Assert.Equal(0, RecognizerSupervisor.Starts);
        Assert.False(Shell.Offered.Task.IsCompleted, "Ohne Start keine Sicherung, kein Angebot");
    }

    private sealed class ProbeApp : App
    {
        internal void Start(params string[] args) => OnStartup(new System.Windows.StartupEventArgs { Args = args });
    }
}
