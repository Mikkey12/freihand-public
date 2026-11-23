namespace Freihand.App;

/// <summary>P9 Kapitel 3/6: echter App-Zusammenbau mit vollständig ersetzten Betriebssystemanschlüssen.</summary>
[Collection(AppAssembly.Name)]
public sealed class Pr35AppCounterTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Freihand-Pr35-" + Guid.NewGuid().ToString("N"));

    public Pr35AppCounterTests()
    {
        Environment.Folder = _folder;
        var models = Path.Combine(_folder, "Freihand", "Modelle");
        Directory.CreateDirectory(models);
        // Leere lokale Prüffiles, keine Modelle; der Erkenner ist eine Attrappe.
        File.WriteAllBytes(Path.Combine(models, "ggml-large-v3-turbo.bin"), []);
        File.WriteAllBytes(Path.Combine(models, "ggml-silero-v5.1.2.bin"), []);
        RecognizerSupervisor.Starts = 0;
        RecognizerSupervisor.Releases = 0;
        RecognizerSupervisor.ReleaseEntered = RecognizerSupervisor.NewSignal();
        RecognizerSupervisor.ReleaseGate = null;
        MainWindow.StartupMessage = MainWindow.NewMessage();
        WindowsTargets.Fail = false;
    }

    [Fact]
    public async Task C06_StartupFailureReleasesAlreadyStartedRecognizer()
    {
        WindowsTargets.Fail = true;
        var app = new ProbeApp();
        try
        {
            app.Start();
            await MainWindow.StartupMessage.Task.WaitAsync(Limit);
            Assert.Equal(1, RecognizerSupervisor.Starts);
            Assert.Equal(1, RecognizerSupervisor.Releases);
        }
        finally { app.Exit(); }
    }

    [Fact]
    public async Task C07_ExitDoesNotSynchronouslyWaitOnTheUiThread()
    {
        var app = new ProbeApp();
        typeof(App).GetField("_recognizer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(app, new RecognizerSupervisor(new object(), null!, null!, TimeProvider.System));
        RecognizerSupervisor.ReleaseGate = RecognizerSupervisor.NewSignal();
        var exit = Task.Run(app.Exit);
        bool returned;
        try
        {
            await DiagnosticWait.WaitOrReport(RecognizerSupervisor.ReleaseEntered.Task, Limit, "C07 ReleaseEntered");
            returned = await Task.WhenAny(exit, Task.Delay(1000)) == exit;
        }
        finally
        {
            RecognizerSupervisor.ReleaseGate.TrySetResult();
            await exit.WaitAsync(Limit);
        }
        Assert.True(returned, "OnExit blockiert seinen aufrufenden UI-Thread beim Warten auf die Freigabe.");
    }

    public void Dispose()
    {
        RecognizerSupervisor.ReleaseGate = null;
        var absolute = Path.GetFullPath(_folder);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!absolute.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(absolute).StartsWith("Freihand-Pr35-", StringComparison.Ordinal)) throw new InvalidOperationException();
        Directory.Delete(absolute, recursive: true);
    }

    private sealed class ProbeApp : App
    {
        internal void Start() => OnStartup(new System.Windows.StartupEventArgs());
        internal void Exit() => OnExit(new System.Windows.ExitEventArgs());
    }
}
