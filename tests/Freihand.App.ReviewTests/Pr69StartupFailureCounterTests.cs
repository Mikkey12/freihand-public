namespace Freihand.App;

[Collection(AppAssembly.Name)]
public sealed class Pr69StartupFailureCounterTests
{
    [Fact]
    public async Task LockedSettingsStillLeaveAStartFailureInTheLog()
    {
        var profile = Directory.CreateTempSubdirectory("freihand-start-failure-");
        var folder = profile.CreateSubdirectory("Freihand");
        var settings = Path.Combine(folder.FullName, "einstellungen.txt");
        var protocol = Path.Combine(folder.FullName, "Protokoll");
        var previousFolder = Environment.Folder;
        var previousMessage = MainWindow.StartupMessage;
        var app = new ProbeApp();
        try
        {
            Environment.Folder = profile.FullName;
            MainWindow.StartupMessage = MainWindow.NewMessage();
            File.WriteAllText(settings, "ton = aus\n");
            using var locked = File.Open(settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            app.Start();
            Assert.Equal("Start gescheitert: IOException",
                await MainWindow.StartupMessage.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.True(Directory.Exists(protocol), "Der Fehler beim Lesen der Einstellungen muss auch im Tagesprotokoll stehen.");
            Assert.Single(Directory.EnumerateFiles(protocol, "*.txt").SelectMany(File.ReadLines),
                line => line.Contains("freihand start_gescheitert typ=IOException", StringComparison.Ordinal));
        }
        finally
        {
            app.Exit();
            Environment.Folder = previousFolder;
            MainWindow.StartupMessage = previousMessage;
            if (Directory.Exists(protocol))
            {
                foreach (var log in Directory.EnumerateFiles(protocol, "*.txt"))
                    File.Delete(log);
                Directory.Delete(protocol);
            }
            File.Delete(settings);
            folder.Delete();
            profile.Delete();
        }
    }

    private sealed class ProbeApp : App
    {
        internal void Start() => OnStartup(new System.Windows.StartupEventArgs());
        internal void Exit() => OnExit(new System.Windows.ExitEventArgs());
    }
}