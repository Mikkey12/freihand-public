namespace Freihand.Core.Recovery;

/// <summary>
/// Ablage der Sicherung (ER-06): ein eigenes, flaches Verzeichnis. Namen sind Dateinamen ohne Pfad.
/// Fehler werden als Ausnahmen gemeldet, nie still übergangen.
/// </summary>
public interface IRecoveryFileSystem
{
    /// <summary>Alle Dateien der Ablage; leer, wenn es die Ablage noch nicht gibt.</summary>
    IReadOnlyList<string> ListFiles();

    byte[] ReadAllBytes(string name);

    /// <summary>Schreibt die Datei vollständig und kehrt erst zurück, wenn sie auf dem Datenträger liegt.</summary>
    void WriteAllBytes(string name, byte[] data);

    /// <summary>Ersetzt <paramref name="destinationName"/> in einem Schritt durch <paramref name="sourceName"/>.</summary>
    void Replace(string sourceName, string destinationName);

    /// <summary>Löscht die Datei; tut nichts, wenn es sie nicht gibt.</summary>
    void Delete(string name);

    DateTimeOffset GetLastWriteTime(string name);
}
