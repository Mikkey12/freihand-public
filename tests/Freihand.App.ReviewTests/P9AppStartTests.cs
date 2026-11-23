using Freihand.Core.Recovery;
using Freihand.Core.Segments;

namespace Freihand.App;

/// <summary>
/// D04: Beim Start mit offenen Entwürfen bietet die App sie
/// an, auch ohne Modell und Erkenner, und fügt nichts ein. Echter Zusammenbau, Betriebssystemanschlüsse ersetzt.
/// </summary>
[Collection(AppAssembly.Name)]
public sealed class P9AppStartTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Freihand-P9-" + Guid.NewGuid().ToString("N"));

    public P9AppStartTests()
    {
        Environment.Folder = _folder;
        RecognizerSupervisor.Starts = 0;
        RecognizerSupervisor.Releases = 0;
        RecognizerSupervisor.ReleaseGate = null;
        MainWindow.StartupMessage = MainWindow.NewMessage();
        WindowsTargets.Fail = false;
        Shell.Offered = Shell.NewOffer();
        lock (Shell.Startups)
        {
            Shell.Startups.Clear();
        }
    }

    [Fact]
    public async Task D04_OpenDraftsAreOfferedWithoutRecognizerAndNothingIsInserted()
    {
        // Ein Entwurf vom letzten Lauf, ohne Übernahme (z. B. nach einem Absturz); kein Modell im Ordner
        var store = Path.Combine(_folder, "Freihand", "Wiederherstellung");
        using (var before = new RecoveryStore(new PhysicalRecoveryFileSystem(store), new WindowsDataProtector(), TimeProvider.System))
        {
            var draft = before.StartDraft();
            draft.Append(new TextSegment(Guid.NewGuid(), "Der Brief an die Gemeinde", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
            draft.Flush();
        }

        var app = new ProbeApp();
        try
        {
            app.Start();
            var scan = await Shell.Offered.Task.WaitAsync(Limit);

            var offered = Assert.Single(scan.Drafts);
            Assert.Equal("Der Brief an die Gemeinde", offered.Text);
            Assert.Null(offered.HandedOverAt);

            // Kein Erkenner, keine Steuerung, nichts geschrieben: nur angeboten
            await WaitFor(() => Startups().Any(text => text.StartsWith("Modell oder VAD fehlt", StringComparison.Ordinal)));
            Assert.Equal(0, RecognizerSupervisor.Starts);
            Assert.Null(typeof(App).GetField("_controller", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(app));
            Assert.Single(Directory.GetFiles(store, "*.draft"));
        }
        finally
        {
            app.Exit();
        }
    }

    [Fact]
    public async Task D04_RecoveryOffIsRespectedAtStart()
    {
        var store = Path.Combine(_folder, "Freihand", "Wiederherstellung");
        using (var before = new RecoveryStore(new PhysicalRecoveryFileSystem(store), new WindowsDataProtector(), TimeProvider.System))
        {
            var draft = before.StartDraft();
            draft.Append(new TextSegment(Guid.NewGuid(), "Alter Text", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
            draft.Flush();
        }

        Directory.CreateDirectory(Path.Combine(_folder, "Freihand"));
        File.WriteAllText(Path.Combine(_folder, "Freihand", "einstellungen.txt"), "sicherung = aus\n");

        var app = new ProbeApp();
        try
        {
            app.Start();
            var scan = await Shell.Offered.Task.WaitAsync(Limit);

            // Abgeschaltet heisst: nichts angeboten, Vorhandenes gelöscht (ER-06)
            Assert.Empty(scan.Drafts);
            Assert.Empty(Directory.GetFiles(store));
        }
        finally
        {
            app.Exit();
        }
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(_folder);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!absolute.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(absolute).StartsWith("Freihand-P9-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }

        // Die Freigabe der Sicherung läuft nach OnExit im Hintergrund: kurz warten, bis die Dateien frei sind
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (Directory.Exists(absolute))
                {
                    Directory.Delete(absolute, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static List<string> Startups()
    {
        lock (Shell.Startups)
        {
            return [.. Shell.Startups];
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Limit)
            {
                throw new Xunit.Sdk.XunitException("Zeitgrenze überschritten");
            }

            await Task.Delay(5);
        }
    }

    private sealed class ProbeApp : App
    {
        internal void Start() => OnStartup(new System.Windows.StartupEventArgs());

        internal void Exit() => OnExit(new System.Windows.ExitEventArgs());
    }
}

/// <summary>Die Prüfungen am App-Zusammenbau teilen statische Attrappen und laufen darum nacheinander.</summary>
[CollectionDefinition(Name)]
public sealed class AppAssembly
{
    public const string Name = "App-Zusammenbau";
}
