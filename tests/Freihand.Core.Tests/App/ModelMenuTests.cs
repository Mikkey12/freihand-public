using System.Text;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Menü „Modell“: Zu jedem Modell gehört ein festes Rechenfenster; das
/// Menü bietet nur freigegebene Modelle neben dem laufenden an, und die Wahl schreibt Modell und Fenster in einem Schritt.
/// </summary>
public sealed class ModelMenuTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "freihand-modell-" + Guid.NewGuid().ToString("N"));

    public ModelMenuTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void EveryChoiceIsReleasedAndCarriesItsWindow()
    {
        foreach (var (file, _, audioContext) in ModelMenu.Choices)
        {
            Assert.NotNull(KnownModels.Expected(file));
            Assert.Equal(file == "ggml-small.bin" ? 850 : 1500, audioContext);
        }
    }

    // Die Standardwerte beider Ausgaben passen zur Tabelle; sonst liefe ein Modell mit fremdem Fenster
    [Fact]
    public void TheDefaultsMatchTheTable()
    {
        foreach (var settings in new[] { PrototypeSettings.Defaults(_folder), PrototypeSettings.BusinessDefaults(_folder, _folder) })
        {
            var choice = Assert.Single(ModelMenu.Choices, item => item.File == Path.GetFileName(settings.Model));
            Assert.Equal(choice.AudioContext, settings.AudioContext);
        }
    }

    [Fact]
    public void OnlyModelsBesideTheRunningOneAreOffered()
    {
        var folder = Path.Combine(_folder, "Modelle");
        var small = Path.Combine(folder, "ggml-small.bin");
        var turbo = Path.Combine(folder, "ggml-large-v3-turbo-q5_0.bin");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { small, turbo, Path.Combine(folder, "ggml-base.bin") };

        var entries = ModelMenu.Entries(small, small, present.Contains);

        Assert.Equal([small, turbo], entries.Select(entry => entry.Path));
        Assert.Equal([true, false], entries.Select(entry => entry.Selected));
        Assert.Equal([true, false], entries.Select(entry => entry.Running));
        Assert.Equal(["Klein – schnell", "Gross – genauer"], entries.Select(ModelMenu.Label));
    }

    // Fehlt das eingestellte Modell, bietet das Menü die anderen an; so lässt sich der Start retten
    [Fact]
    public void WithoutTheRunningModelTheOthersAreStillOffered()
    {
        var folder = Path.Combine(_folder, "Modelle");
        var small = Path.Combine(folder, "ggml-small.bin");
        var turbo = Path.Combine(folder, "ggml-large-v3-turbo-q5_0.bin");

        var entry = Assert.Single(ModelMenu.Entries(small, small, path => path == turbo));

        Assert.Equal((turbo, false, false), (entry.Path, entry.Selected, entry.Running));
        Assert.Empty(ModelMenu.Entries(string.Empty, string.Empty, _ => true));
        Assert.Empty(ModelMenu.Entries("ggml-small.bin", "ggml-small.bin", _ => true));
    }

    [Fact]
    public void AChoiceThatIsNotRunningYetSaysSo()
    {
        var folder = Path.Combine(_folder, "Modelle");
        var small = Path.Combine(folder, "ggml-small.bin");
        var turbo = Path.Combine(folder, "ggml-large-v3-turbo-q5_0.bin");

        var entries = ModelMenu.Entries(small, turbo, _ => true);
        var chosen = Assert.Single(entries, entry => entry.Selected);

        Assert.Equal(turbo, chosen.Path);
        Assert.Equal("Gross – genauer (ab Neustart)", ModelMenu.Label(chosen));
        Assert.Equal("Modell: Gross – genauer – gilt nach Neustart von Freihand (Beenden und wieder starten)", ModelMenu.Notice(chosen, saved: true));
        Assert.Equal("Modell: Klein – schnell", ModelMenu.Notice(entries[0] with { Selected = true }, saved: true));
        Assert.StartsWith("Modell „Gross – genauer“ nicht gespeichert", ModelMenu.Notice(chosen, saved: false), StringComparison.Ordinal);
    }

    // Modell und Rechenfenster in einem Schreibvorgang; Notizen von Hand bleiben, es gilt die letzte Zeile eines Schlüssels
    [Fact]
    public void ModelAndWindowAreWrittenTogetherAndNothingElseChanges()
    {
        var path = Path.Combine(_folder, "einstellungen.txt");
        File.WriteAllText(path, "# Notiz\r\nmodell = alt.bin\r\naudiokontext = 900\r\naudiokontext = 850\r\nton = ein\r\n", new UTF8Encoding(false));

        PrototypeSettings.SetValues(path, ("modell", @"C:\Modelle\ggml-large-v3-turbo-q5_0.bin"), ("audiokontext", "1500"));

        Assert.Equal(
            "# Notiz\r\nmodell = C:\\Modelle\\ggml-large-v3-turbo-q5_0.bin\r\naudiokontext = 900\r\naudiokontext = 1500\r\nton = ein\r\n",
            File.ReadAllText(path));
        var loaded = PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(_folder));
        Assert.Equal(1500, loaded.AudioContext);
        Assert.Equal(@"C:\Modelle\ggml-large-v3-turbo-q5_0.bin", loaded.Model);
    }

    // Codex zu PR #69: Für Modelle der Tabelle gehört das Fenster zum Modell; nur andere Modelle lesen „audiokontext“
    [Fact]
    public void AHandEditedWindowCountsOnlyForModelsOutsideTheTable()
    {
        var path = Path.Combine(_folder, "einstellungen.txt");
        var defaults = PrototypeSettings.Defaults(_folder);
        // Pfade über Path.Combine, damit der Test auch unter Linux den Dateinamen findet
        File.WriteAllText(path, $"modell = {Path.Combine(_folder, "ggml-large-v3-turbo.bin")}\naudiokontext = 850\n", new UTF8Encoding(false));
        Assert.Equal(1500, PrototypeSettings.LoadOrCreate(path, defaults).AudioContext);

        File.WriteAllText(path, $"modell = {Path.Combine(_folder, "ggml-base.bin")}\naudiokontext = 700\n", new UTF8Encoding(false));
        Assert.Equal(700, PrototypeSettings.LoadOrCreate(path, defaults).AudioContext);
        Assert.Null(ModelMenu.WindowFor("ggml-base.bin"));
        Assert.Equal(850, ModelMenu.WindowFor(Path.Combine(_folder, "GGML-SMALL.BIN")));
    }

    // Fehlt ein Schlüssel, wird er angehängt, im Zeilenende der Datei
    [Fact]
    public void AMissingKeyIsAppended()
    {
        var path = Path.Combine(_folder, "einstellungen.txt");
        File.WriteAllText(path, "modell = alt.bin", new UTF8Encoding(false));

        PrototypeSettings.SetValues(path, ("modell", "neu.bin"), ("audiokontext", "1500"));

        Assert.Equal("modell = neu.bin\naudiokontext = 1500\n", File.ReadAllText(path));
    }

    // Unter Linux sperrt FileShare nicht; der Test gilt dort als leer (xUnit 2 kennt kein Überspringen zur Laufzeit)
    [Fact]
    public void AFileThatCannotBeWrittenChangesNeitherValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_folder, "einstellungen.txt");
        const string before = "modell = alt.bin\naudiokontext = 850\n";
        File.WriteAllText(path, before, new UTF8Encoding(false));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Das Ersetzen einer gesperrten Datei meldet Windows als verweigerten Zugriff; beides fangen die Aufrufer
            var exception = Record.Exception(() => PrototypeSettings.SetValues(path, ("modell", "neu.bin"), ("audiokontext", "1500")));
            Assert.True(exception is IOException or UnauthorizedAccessException, exception?.GetType().Name ?? "kein Fehler");
        }

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(["einstellungen.txt"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }
}
