namespace Freihand.Adapters.Recognition;

/// <summary>
/// Freigegebene Modelldateien mit ihrem SHA-256-Sollwert aus <c>docs/freigabe/inventar.md</c> (WE-14). Der Erkenner prüft
/// die Datei gegen diesen Wert; eine unbekannte, vertauschte oder veränderte Datei startet nicht (P8 Kapitel 5.4).
/// </summary>
public static class KnownModels
{
    /// <summary>Dateiname ohne Pfad → SHA-256 in Kleinbuchstaben.</summary>
    public static IReadOnlyDictionary<string, string> Sha256 { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ggml-large-v3-turbo.bin"] = "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
        ["ggml-large-v3-turbo-q5_0.bin"] = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
        ["ggml-small.bin"] = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
        ["ggml-silero-v5.1.2.bin"] = "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf",
    };

    /// <summary>Sollwert für die Datei unter <paramref name="path"/>; null, wenn sie nicht freigegeben ist.</summary>
    public static string? Expected(string path) =>
        Sha256.TryGetValue(Path.GetFileName(path), out var expected) ? expected : null;
}
