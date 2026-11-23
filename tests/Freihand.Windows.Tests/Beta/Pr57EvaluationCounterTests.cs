using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Freihand.Windows.Tests.Beta;

public sealed class Pr57EvaluationCounterTests
{
    [Fact]
    public async Task TwoDelaysHaveTheirArithmeticMedianNotTheLowerValue()
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-pr57-median-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllLines(Path.Combine(folder, "2026-11-17.txt"),
                ["2026-11-17T08:00:00.000+01:00 ereignis abschnitt_geschrieben verzug_ms=1000",
                 "2026-11-17T08:00:01.000+01:00 ereignis abschnitt_geschrieben verzug_ms=3000"], new UTF8Encoding(false));
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Freihand.slnx"))) root = root.Parent;
            var script = Path.Combine(root!.FullName, "tools", "Beta", "Freihand-Auswertung.ps1");
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $"[Console]::OutputEncoding=[Text.Encoding]::UTF8; & '{script}' -Ordner '{folder}' -Tage 100000 -Json" })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(process.ExitCode == 0, await error);
            var day = JsonDocument.Parse(await output).RootElement[0];
            Assert.Equal(3000, day.GetProperty("VerzugP95Ms").GetInt32());
            Assert.Equal(2000, day.GetProperty("VerzugMedianMs").GetInt32());
        }
        finally
        {
            var absolute = Path.GetFullPath(folder);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(absolute).StartsWith("freihand-pr57-median-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(absolute, recursive: true);
        }
    }
}
