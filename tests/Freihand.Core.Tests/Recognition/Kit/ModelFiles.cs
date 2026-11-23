using System.Security.Cryptography;
using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.Recognition.Kit;

/// <summary>
/// Modell- und VAD-Datei als echte temporäre Dateien aus je 1 KiB Zufallsdaten, zur Laufzeit erzeugt (R31). Der Dienst
/// prüft die SHA-256 bei jedem Laden (Kapitel 5.4); jeder Test mit echtem <see cref="RecognizerService"/> braucht darum
/// solche Dateien. Keine Aufnahmen, keine echten Modelle.
/// </summary>
internal sealed class ModelFiles : IDisposable
{
    private readonly byte[] _model;
    private readonly byte[] _vad;

    private ModelFiles(string directory, string modelFile, string vadFile)
    {
        Directory = directory;
        ModelPath = Path.Combine(directory, modelFile);
        VadPath = Path.Combine(directory, vadFile);
        _model = RandomNumberGenerator.GetBytes(1024);
        _vad = RandomNumberGenerator.GetBytes(1024);
        File.WriteAllBytes(ModelPath, _model);
        File.WriteAllBytes(VadPath, _vad);
        ModelSha256 = Sha256(_model);
        VadSha256 = Sha256(_vad);
    }

    public string Directory { get; }

    public string ModelPath { get; }

    public string VadPath { get; }

    /// <summary>SHA-256 des Ausgangsinhalts, klein geschrieben in Hex.</summary>
    public string ModelSha256 { get; }

    public string VadSha256 { get; }

    /// <summary>Legt beide Dateien in einem eigenen, neuen Verzeichnis an.</summary>
    public static ModelFiles Create(string modelFile = "ggml-small.bin", string vadFile = "ggml-silero-v5.1.2.bin")
    {
        var directory = Path.Combine(Path.GetTempPath(), "freihand-p8-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return new ModelFiles(directory, modelFile, vadFile);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Konfiguration mit diesen Dateien; ohne Angabe Laufzeit nur Prozessor, Threads 4, <c>AudioContext</c> 600.</summary>
    public RecognizerConfiguration Configuration(params RecognizerRuntime[] runtimeOrder) => new(
        ModelPath,
        ModelSha256,
        runtimeOrder.Length == 0 ? [RecognizerRuntime.Cpu] : runtimeOrder,
        Threads: 4,
        AudioContext: 600,
        VadPath,
        VadSha256,
        VadSettings.Default);

    public void DeleteModel() => File.Delete(ModelPath);

    /// <summary>Schreibt den Ausgangsinhalt zurück (nach <see cref="DeleteModel"/> oder einer Änderung).</summary>
    public void RestoreModel() => File.WriteAllBytes(ModelPath, _model);

    /// <summary>
    /// Ändert ein Byte und setzt Grösse und Änderungszeit auf die alten Werte zurück (R31, Gegenfall gleiche Metadaten):
    /// nur eine echte Prüfsumme erkennt die Änderung.
    /// </summary>
    public void CorruptModelKeepingMetadata()
    {
        var before = new FileInfo(ModelPath);
        var length = before.Length;
        var written = before.LastWriteTimeUtc;
        var changed = File.ReadAllBytes(ModelPath);
        changed[changed.Length / 2] ^= 0x5A;
        File.WriteAllBytes(ModelPath, changed);
        File.SetLastWriteTimeUtc(ModelPath, written);
        var after = new FileInfo(ModelPath);
        Assert.Equal(length, after.Length);
        Assert.Equal(written, after.LastWriteTimeUtc);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Aufräumen ist bestmöglich; eine noch offene Datei bleibt im Temp-Verzeichnis.
        }
        catch (UnauthorizedAccessException)
        {
            // Ebenso.
        }
    }
}
