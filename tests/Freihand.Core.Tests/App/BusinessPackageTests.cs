using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Geschäftspaket G1: Standardwerte fürs Notebook mit Modellen im Datenordner,
/// Verzug je geschriebenem Abschnitt im Protokoll, Prüfsummen im Paketskript gleich wie im Inventar. Erfundene Texte.
/// </summary>
public sealed class BusinessPackageTests
{
    [Fact]
    public void BusinessDefaultsUseTheModelsInTheDataFolderVulkanThenCpuAndContext850()
    {
        var defaults = PrototypeSettings.BusinessDefaults("C:\\Daten\\Freihand", "C:\\Daten\\Freihand");

        Assert.Equal(Path.Combine("C:\\Daten\\Freihand", "Modelle", "ggml-small.bin"), defaults.Model);
        Assert.Equal(Path.Combine("C:\\Daten\\Freihand", "Modelle", "ggml-silero-v5.1.2.bin"), defaults.Vad);
        Assert.Equal(["vulkan", "cpu"], defaults.Runtimes);
        Assert.Equal(850, defaults.AudioContext);
        Assert.NotNull(KnownModels.Expected(defaults.Model));
        Assert.NotNull(KnownModels.Expected(defaults.Vad));
    }

    [Fact]
    public async Task AWrittenSectionLogsItsDelayAsANumberOnly()
    {
        await using var rig = new ControllerRig(arrange: r => r.Audio.OnOpen = session =>
            session.Chunks.Add(new AudioChunk(new byte[32_000], 16_000, TimeSpan.Zero)));
        var run = await rig.StartRecordingAsync();
        await rig.WaitUntil(() => rig.Log.Contains("Capture.Chunk"), "erster Block gelesen");

        run.Emit(new TextOutput(new RecognitionUpdate(
            [new TextSegment(Guid.NewGuid(), "VERZUG-MARKE-4QP", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1))], [])));
        await rig.WaitUntil(() => rig.LogLines.Any(line => line.StartsWith("ereignis abschnitt_geschrieben ", StringComparison.Ordinal)),
            "Verzug protokolliert");
        await rig.EndAsync(run);

        var line = Assert.Single(rig.LogLines, item => item.StartsWith("ereignis abschnitt_geschrieben ", StringComparison.Ordinal));
        var delay = long.Parse(line["ereignis abschnitt_geschrieben verzug_ms=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(delay, 0, 10_000);
        Assert.All(rig.LogLines, item => Assert.DoesNotContain("VERZUG-MARKE", item, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAudioThereIsNoDelayLine()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();

        run.Emit(new TextOutput(new RecognitionUpdate(
            [new TextSegment(Guid.NewGuid(), "Ohne Ton", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1))], [])));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "geschrieben");
        await rig.EndAsync(run);

        Assert.DoesNotContain(rig.LogLines, item => item.StartsWith("ereignis abschnitt_geschrieben", StringComparison.Ordinal));
    }

    // Zwei Pakete (22.11.2026): Modell und VAD immer aus dem Datenordner; aus der Einstellung zählt nur der Dateiname,
    // auch bei einer Einstellung aus einem früheren Paket mit den Modellen im Programmordner
    [Fact]
    public void BusinessModelsAlwaysComeFromTheDataFolder()
    {
        var old = PrototypeSettings.BusinessDefaults("C:\\Daten\\Freihand", "C:\\Programme\\Freihand") with
        {
            Model = Path.Combine("C:\\Programme\\Freihand", "Modelle", "ggml-large-v3-turbo-q5_0.bin"),
        };

        var effective = old.WithModelsIn("C:\\Daten\\Freihand");

        Assert.Equal(Path.Combine("C:\\Daten\\Freihand", "Modelle", "ggml-large-v3-turbo-q5_0.bin"), effective.Model);
        Assert.Equal(Path.Combine("C:\\Daten\\Freihand", "Modelle", "ggml-silero-v5.1.2.bin"), effective.Vad);
        Assert.Equal(old with { Model = effective.Model, Vad = effective.Vad }, effective);
    }

    // Das Paketskript prüft die Modelle mit denselben Prüfsummen wie das Inventar (R31)
    [Fact]
    public void ThePackageScriptChecksTheSameHashesAsTheInventory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Freihand.slnx")))
        {
            root = root.Parent;
        }

        var script = File.ReadAllText(Path.Combine(root!.FullName, "tools", "Geschaeft", "Freihand-Geschaeftspaket.ps1"));

        foreach (var name in new[] { "ggml-small.bin", "ggml-large-v3-turbo-q5_0.bin", "ggml-silero-v5.1.2.bin" })
        {
            Assert.Contains($"\"{name}\" = \"{KnownModels.Sha256[name]}\"", script, StringComparison.OrdinalIgnoreCase);
        }
    }
}
