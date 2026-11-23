using Freihand.Core.Recognition;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Gegenproben der Codex-Prüfung zu PR #56 am Stand 9abd0c0, als feste Regressionstests übernommen: Die Auswertung zählt
/// Ereignisse statt Zustände, und eine Menüwahl ändert die Zeile, die beim Laden gilt, bei jedem Zeilenende. Die zwei
/// Proben mit dem Skript brauchen Windows PowerShell und laufen darum nur unter Windows (CI: Windows-Lauf).
/// </summary>
public sealed class Pr56ReviewCounterTests
{
    [Fact]
    public async Task PreviouslyHeldSegmentsMustNotBeCountedAgainAfterAnotherDictation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var rig = new ControllerRig();
        var first = await rig.StartRecordingAsync();
        var segment = new TextSegment(Guid.NewGuid(), "Erfundene Probe", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        rig.Output.SetReport(segment.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", segment.Text, OutputReason.FocusElsewhere));
        first.Emit(new TextOutput(new RecognitionUpdate([segment], [])));
        await rig.WaitUntil(() => rig.Controller.Current.Entries.Count == 1, "gehaltener Abschnitt");
        await rig.EndAsync(first);
        var second = await rig.StartRecordingAsync();
        await rig.EndAsync(second);
        var result = Evaluate(rig.LogLines);
        Assert.Equal(2, result.GetProperty("Diktate").GetInt32());
        Assert.Equal(1, result.GetProperty("Gehalten").GetInt32());
    }

    [Fact]
    public async Task RejectedStartsMustBeCountedAsAttempts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var rig = new ControllerRig();
        rig.Controller.SelectDevice("nicht-vorhanden");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
            await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        }
        var result = Evaluate(rig.LogLines);
        Assert.Equal(2, result.GetProperty("Diktate").GetInt32());
    }

    [Fact]
    public void SettingAValueMustReplaceTheFirstLineAlsoWithCrLineEndings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-pr56-setting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "einstellungen.txt");
            File.WriteAllText(path, "# Notiz\rgeraet = alt\rton = aus\r", new UTF8Encoding(false));
            PrototypeSettings.SetValue(path, "geraet", "neu");
            Assert.Equal("# Notiz\rgeraet = neu\rton = aus\r", File.ReadAllText(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ASavedMicrophoneChoiceMustRemainTheChoiceAfterReloadWithDuplicateKeys()
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-pr56-duplicate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "einstellungen.txt");
            File.WriteAllText(path, "geraet = mikro-a\r\n# eigene Notiz\r\ngeraet = mikro-b\r\n", new UTF8Encoding(false));
            PrototypeSettings.SetValue(path, "geraet", "mikro-c");
            Assert.Equal("mikro-c", PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder)).Device);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
    private static JsonElement Evaluate(IReadOnlyList<string> lines)
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-pr56-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllLines(Path.Combine(folder, "2026-11-12.txt"), lines.Select(line => "2026-11-12T10:00:00.000+01:00 " + line), new UTF8Encoding(false));
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Freihand.slnx"))) root = root.Parent;
            var script = Path.Combine(root!.FullName, "tools", "Beta", "Freihand-Auswertung.ps1");
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var arg in new[] { "-NoProfile", "-Command", $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '{script}' -Ordner '{folder}' -Tage 100000 -Json" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(60000));
            Assert.True(process.ExitCode == 0, error.Result);
            return JsonDocument.Parse(output.Result).RootElement[0].Clone();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
