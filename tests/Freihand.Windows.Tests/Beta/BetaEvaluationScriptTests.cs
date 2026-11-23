using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Freihand.Windows.Tests.Beta;

/// <summary>
/// BT06: Das Auswertungsskript zählt aus erfundenen Protokollen Diktate, gehaltene
/// Abschnitte, Meldungen und Marken je Tag richtig. Läuft mit Windows PowerShell 5.1, wie auf Windows-Standardinstallationen.
/// </summary>
public sealed class BetaEvaluationScriptTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "freihand-bt06-" + Guid.NewGuid().ToString("N"));

    public BetaEvaluationScriptTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void BT06_TheScriptCountsDictationsHeldMessagesAndMarksPerDay()
    {
        // Nur Ereigniszeilen zählen; Zustandszeilen dazwischen (auch mit gehalten=… und meldung=…) nie
        Write("2026-11-10.txt",
            "08:00:00 freihand start",
            "08:01:00 phase=Idle meldung=None grund=- gehalten=0",
            "08:02:00 ereignis diktat_start",
            "08:02:00 phase=Starting meldung=None grund=- gehalten=0",
            "08:02:03 ereignis abschnitt_geschrieben verzug_ms=1200",
            "08:02:04 ereignis abschnitt_geschrieben verzug_ms=3400",
            "08:02:04 ereignis abschnitt_geschrieben verzug_ms=2100",
            "08:02:05 ereignis abschnitt_gehalten status=Held grund=FocusElsewhere",
            "08:02:05 ereignis meldung code=HandOverRefused grund=FocusElsewhere",
            "08:02:05 phase=Recording meldung=HandOverRefused grund=FocusElsewhere gehalten=1",
            "08:02:09 ereignis abschnitt_gehalten status=Held grund=FocusElsewhere",
            "08:02:09 phase=Idle meldung=HandOverRefused grund=FocusElsewhere gehalten=2",
            "08:03:00 marke nr=1 quelle=taste phase=Idle meldung=None gehalten=2",
            "08:04:00 ereignis diktat_start",
            "08:04:01 ereignis meldung code=NoDevice grund=Gerät",
            "08:04:01 phase=Idle meldung=NoDevice grund=Gerät gehalten=2",
            "08:04:30 ereignis diktat_start",
            "08:04:31 ereignis meldung code=NoDevice grund=Gerät",
            "08:04:31 phase=Idle meldung=NoDevice grund=Gerät gehalten=2",
            "09:00:00 freihand start",
            "09:00:05 ereignis diktat_start");
        Write("2026-11-11.txt",
            "10:00:00 freihand start",
            "10:05:00 marke nr=1 quelle=menü zustand=unbekannt");
        File.WriteAllText(Path.Combine(_folder, "notiz.txt"), "phase=Recording");

        var days = Run();

        Assert.Equal(2, days.GetArrayLength());
        var first = days[0];
        Assert.Equal("2026-11-10", first.GetProperty("Tag").GetString());
        Assert.Equal(4, first.GetProperty("Diktate").GetInt32());
        Assert.Equal(2, first.GetProperty("Gehalten").GetInt32());
        Assert.Equal(3, first.GetProperty("Meldungen").GetInt32());
        Assert.Equal(1, first.GetProperty("Codes").GetProperty("HandOverRefused").GetInt32());
        Assert.Equal(2, first.GetProperty("Codes").GetProperty("NoDevice").GetInt32());
        Assert.Equal(3, first.GetProperty("Abschnitte").GetInt32());
        Assert.Equal(2100, first.GetProperty("VerzugMedianMs").GetInt32());
        Assert.Equal(3400, first.GetProperty("VerzugP95Ms").GetInt32());
        var mark = Assert.Single(Marks(first));
        Assert.Equal(1, mark.GetProperty("Nummer").GetInt32());
        Assert.Equal("taste", mark.GetProperty("Quelle").GetString());

        var second = days[1];
        Assert.Equal(0, second.GetProperty("Diktate").GetInt32());
        Assert.Equal(0, second.GetProperty("Meldungen").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("VerzugMedianMs").ValueKind);
        Assert.Equal("menü", Assert.Single(Marks(second)).GetProperty("Quelle").GetString());
    }

    [Fact]
    public void AnInvalidDelayCountsAsSectionButNeverAsMeasurement()
    {
        // Codex-Prüfung PR #57: ein Wert unter null wird als ungültig protokolliert und darf den Median nicht verschieben
        Write("2026-11-17.txt",
            "08:00:01 ereignis abschnitt_geschrieben verzug_ms=1000",
            "08:00:02 ereignis abschnitt_geschrieben verzug_ms=ungueltig",
            "08:00:03 ereignis abschnitt_geschrieben verzug_ms=1400");

        var day = Run()[0];

        Assert.Equal(3, day.GetProperty("Abschnitte").GetInt32());
        Assert.Equal(1, day.GetProperty("VerzugUngueltig").GetInt32());
        Assert.Equal(1200, day.GetProperty("VerzugMedianMs").GetInt32());
        Assert.Equal(1400, day.GetProperty("VerzugP95Ms").GetInt32());
    }

    // Menü „Modell“ (21.11.2026): Der Verzug erscheint zusätzlich je Modell; das Modell kommt aus der letzten Startzeile
    // und gilt über Mitternacht weiter
    [Fact]
    public void TheDelayIsAlsoShownPerModel()
    {
        Write("2026-11-23.txt",
            "08:00:00 ereignis abschnitt_geschrieben verzug_ms=900",
            "08:01:00 freihand start ausgabe=geschaeft sitzungsende=geschuetzt modell=ggml-small.bin",
            "08:02:00 ereignis abschnitt_geschrieben verzug_ms=1000",
            "08:02:01 ereignis abschnitt_geschrieben verzug_ms=3000",
            "12:00:00 freihand start ausgabe=geschaeft sitzungsende=geschuetzt modell=ggml-large-v3-turbo-q5_0.bin",
            "12:01:00 ereignis abschnitt_geschrieben verzug_ms=2000",
            "12:01:01 ereignis abschnitt_geschrieben verzug_ms=ungueltig");
        Write("2026-11-24.txt",
            "07:00:00 ereignis abschnitt_geschrieben verzug_ms=2600");

        var days = Run();

        var first = Models(days[0]).ToList();
        Assert.Equal(["unbekannt", "ggml-small.bin", "ggml-large-v3-turbo-q5_0.bin"], first.Select(model => model.GetProperty("Modell").GetString()));
        Assert.Equal([1, 2, 2], first.Select(model => model.GetProperty("Abschnitte").GetInt32()));
        Assert.Equal(2000, first[1].GetProperty("VerzugMedianMs").GetInt32());
        Assert.Equal(3000, first[1].GetProperty("VerzugP95Ms").GetInt32());
        Assert.Equal(5, days[0].GetProperty("Abschnitte").GetInt32());
        var second = Assert.Single(Models(days[1]));
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", second.GetProperty("Modell").GetString());
        Assert.Equal(2600, second.GetProperty("VerzugMedianMs").GetInt32());
    }

    // Ein Tag mit einem Modell (JSON mit einem Element in PowerShell 5.1); ungültige Abschnitte zählen wie im Tageswert
    [Fact]
    public void ASingleModelAndInvalidDelaysAreCountedLikeTheDay()
    {
        Write("2026-11-25.txt",
            "08:00:00 freihand start ausgabe=geschaeft sitzungsende=geschuetzt modell=ggml-small.bin",
            "08:01:00 ereignis abschnitt_geschrieben verzug_ms=1500",
            "08:01:01 ereignis abschnitt_geschrieben verzug_ms=ungueltig");

        var day = Run()[0];

        var model = Assert.Single(Models(day));
        Assert.Equal("ggml-small.bin", model.GetProperty("Modell").GetString());
        Assert.Equal(day.GetProperty("Abschnitte").GetInt32(), model.GetProperty("Abschnitte").GetInt32());
        Assert.Equal(1, model.GetProperty("VerzugUngueltig").GetInt32());
        Assert.Equal(1500, model.GetProperty("VerzugMedianMs").GetInt32());
    }

    private static IEnumerable<JsonElement> Models(JsonElement day) =>
        day.GetProperty("Modelle") is { ValueKind: JsonValueKind.Array } models ? models.EnumerateArray() : [day.GetProperty("Modelle")];

    private static IEnumerable<JsonElement> Marks(JsonElement day) =>
        day.GetProperty("Marken") is { ValueKind: JsonValueKind.Array } marks ? marks.EnumerateArray() : [day.GetProperty("Marken")];

    /// <summary>Tagesdatei wie <c>DailyLog</c>: Ortszeit mit Datum des Dateinamens, UTF-8 ohne Kennung.</summary>
    private void Write(string name, params string[] lines)
    {
        var day = Path.GetFileNameWithoutExtension(name);
        File.WriteAllText(Path.Combine(_folder, name),
            string.Concat(lines.Select(line => $"{day}T{line[..8]}.000+01:00{line[8..]}\n")), new UTF8Encoding(false));
    }

    private JsonElement Run(int days = 100000)
    {
        var script = Path.Combine(RepositoryRoot(), "tools", "Beta", "Freihand-Auswertung.ps1");
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                     $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '{script}' -Ordner '{_folder}' -Tage {days} -Json",
                 })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "Das Skript endet innert einer Minute");
        Assert.True(process.ExitCode == 0, $"Skript scheiterte: {error.Result}");
        return JsonDocument.Parse(output.Result).RootElement.Clone();
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Freihand.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("Repository nicht gefunden.");
    }

    [Fact]
    public void AutomaticRecognizerRestartsReachTheBetaReportOnceEach()
    {
        Write("2026-11-26.txt",
            "08:00:00 freihand start ausgabe=geschaeft sitzungsende=geschuetzt modell=ggml-small.bin",
            "08:01:00 phase=Recording erkenner=Ready meldung=None",
            // Seit der Deltaprüfung zu PR #69 zählt die Ereigniszeile je begonnenem Neustart, nicht der Zustand
            "08:01:01 ereignis erkenner_neustart",
            "08:01:01 phase=Recording erkenner=Restarting meldung=None",
            "08:01:02 phase=Recording erkenner=Restarting meldung=None",
            "08:01:03 phase=Recording erkenner=Ready meldung=None");

        var day = Run()[0];
        Assert.Equal(1, day.GetProperty("Meldungen").GetInt32());
    }

    [Fact]
    public void TheSelectedTimeRangeStillUsesTheLastEarlierModel()
    {
        var day = DateTime.Today;
        Write(day.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".txt",
            "23:00:00 freihand start ausgabe=geschaeft sitzungsende=geschuetzt modell=ggml-small.bin");
        Write(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".txt",
            "08:01:00 ereignis abschnitt_geschrieben verzug_ms=1500");

        var result = Assert.Single(Run(days: 1).EnumerateArray());
        Assert.Equal("ggml-small.bin", Assert.Single(Models(result)).GetProperty("Modell").GetString());
    }
    [Fact]
    public void TheFirstIncludedDayDoesNotCountAnAlreadyUnavailableRecognizerAgain()
    {
        var day = DateTime.Today;
        Write(day.AddDays(-2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".txt",
            "23:00:00 freihand start modell=ggml-small.bin",
            "23:00:01 phase=Idle erkenner=Unavailable meldung=None");
        Write(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".txt",
            "08:00:00 phase=Idle erkenner=Unavailable meldung=None");

        var report = Assert.Single(Run(days: 1).EnumerateArray());
        Assert.Equal(0, report.GetProperty("Meldungen").GetInt32());
    }
    [Fact]
    public void AFailedStartupLineDoesNotResetTheModelAttribution()
    {
        var day = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Write(day + ".txt",
            "08:00:00 freihand start modell=ggml-small.bin",
            "08:00:01 freihand start_gescheitert typ=InvalidOperationException ort=App.StartAsync",
            "08:00:02 ereignis abschnitt_geschrieben verzug_ms=500");

        var report = Assert.Single(Run(days: 1).EnumerateArray());
        Assert.Equal("ggml-small.bin", Assert.Single(Models(report)).GetProperty("Modell").GetString());
    }
}
