using System.ComponentModel;
using System.Diagnostics;
using Freihand.Adapters.Recognition;

namespace Freihand.Windows.Tests.Recognition;

/// <summary>
/// Tests, die echte Prozesse starten (R44 bis R46), laufen nacheinander, damit sich ihre Prozesse und Pipes nicht
/// gegenseitig stören. Keiner dieser Tests prüft eine Dauer; die Fristen aus R45 und R46 misst RM13.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessTestsCollection
{
    public const string Name = "Prozessstarts";
}

/// <summary>Hängerschutz: schützt nur davor, dass ein Test ewig wartet; er ist keine Abnahmegrenze.</summary>
internal static class Hangs
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static async Task GuardAsync(Task task, string what)
    {
        using var timeout = new CancellationTokenSource();
        var delay = Task.Delay(Limit, timeout.Token);
        if (await Task.WhenAny(task, delay) != task)
        {
            Assert.Fail($"{what}: keine Antwort innerhalb des Hängerschutzes von {Limit.TotalSeconds:0} s.");
        }

        await timeout.CancelAsync();
        await task;
    }

    public static async Task<T> GuardAsync<T>(Task<T> task, string what)
    {
        await GuardAsync((Task)task, what);
        return await task;
    }
}

/// <summary>Ein harmloser Hilfsprozess mit eigener Prozesskennung (nicht die des Testprozesses).</summary>
internal sealed class HelperProcess : IDisposable
{
    private readonly Process _process;

    private HelperProcess(Process process) => _process = process;

    public int Id => _process.Id;

    /// <summary>Startangaben für <c>ping</c> gegen die eigene Schleife: läuft rund eine Minute, ohne Eingabe und ohne Netz.</summary>
    public static ProcessStartInfo StartInfo()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add("60");
        info.ArgumentList.Add("127.0.0.1");
        return info;
    }

    public static HelperProcess Start() =>
        new(Process.Start(StartInfo()) ?? throw new InvalidOperationException("Der Hilfsprozess liess sich nicht starten."));

    /// <summary>Beendet den Prozess und wartet, bis er wirklich weg ist.</summary>
    public async Task KillAndWaitAsync()
    {
        Kill();
        using var timeout = new CancellationTokenSource(Hangs.Limit);
        await _process.WaitForExitAsync(timeout.Token);
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }

    private void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // schon beendet
        }
        catch (Win32Exception)
        {
            // wird gerade beendet
        }
    }
}

/// <summary>Findet <c>Freihand.Recognizer.exe</c> im Ausgabeordner der Tests.</summary>
internal static class RecognizerExecutable
{
    private const string FileName = "Freihand.Recognizer.exe";

    public static string Locate()
    {
        // Das Testprojekt verweist auf Freihand.Recognizer; die ausführbare Datei wird in den Ausgabeordner kopiert.
        var local = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(local))
        {
            return local;
        }

        // Rückfall: der Ausgabeordner des Projekts selbst, aufwärts vom Ausgabeordner der Tests gesucht.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var projectOutput = Path.Combine(directory.FullName, "src", "Freihand.Recognizer", "bin");
            if (!Directory.Exists(projectOutput))
            {
                continue;
            }

            var newest = Directory.EnumerateFiles(projectOutput, FileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null)
            {
                return newest;
            }
        }

        Assert.Fail($"{FileName} liegt weder in {AppContext.BaseDirectory} noch unter src/Freihand.Recognizer/bin.");
        return local;
    }
}

/// <summary>
/// Reicht <see cref="IRecognizerLauncher"/> durch, zählt die Prozessstarts (R45) und beendet am Ende jeden gestarteten
/// Prozess, auch wenn der Test vorher scheitert.
/// </summary>
internal sealed class RecordingLauncher(IRecognizerLauncher inner) : IRecognizerLauncher
{
    private readonly object _gate = new();
    private readonly List<LaunchedRecognizer> _launched = [];
    private int _launchCount;

    /// <summary>Zahl der Aufrufe von <see cref="LaunchAsync"/>, also der Prozessstarts.</summary>
    public int LaunchCount => Volatile.Read(ref _launchCount);

    public async Task<LaunchedRecognizer> LaunchAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _launchCount);
        var launched = await inner.LaunchAsync(cancellationToken);
        lock (_gate)
        {
            _launched.Add(launched);
        }

        return launched;
    }

    public void KillAll()
    {
        LaunchedRecognizer[] all;
        lock (_gate)
        {
            all = [.. _launched];
        }

        foreach (var launched in all)
        {
            try
            {
                launched.Kill();
            }
            catch (InvalidOperationException)
            {
                // schon beendet
            }
            catch (Win32Exception)
            {
                // wird gerade beendet
            }
        }
    }
}

/// <summary>Temporärer Ordner für nicht vorhandene Modelldateien.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freihand-p8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Aufräumen im temporären Ordner ist nicht Teil der Prüfung
        }
    }
}

internal static class TestConfigurations
{
    /// <summary>Konfiguration mit Modell und VAD-Modell, die es im angegebenen Ordner nicht gibt.</summary>
    public static RecognizerConfiguration MissingModel(string directory) => new(
        ModelPath: Path.Combine(directory, "ggml-fehlt.bin"),
        ModelSha256: new string('0', 64),
        RuntimeOrder: [RecognizerRuntime.Cpu],
        Threads: 4,
        AudioContext: 600,
        VadModelPath: Path.Combine(directory, "ggml-silero-fehlt.bin"),
        VadSha256: new string('0', 64),
        Vad: VadSettings.Default);
}
