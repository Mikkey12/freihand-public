namespace Freihand.Core.Recovery;

/// <summary>
/// Ablage auf dem Datenträger, plattformunabhängig mit <see cref="System.IO"/>. Das Verzeichnis
/// gehört allein der Wiederherstellung (ER-06) und wird beim ersten Schreiben angelegt.
/// </summary>
public sealed class PhysicalRecoveryFileSystem(string directory) : IRecoveryFileSystem
{
    public string Directory { get; } = Path.GetFullPath(directory);

    public IReadOnlyList<string> ListFiles() =>
        System.IO.Directory.Exists(Directory)
            ? [.. System.IO.Directory.EnumerateFiles(Directory).Select(path => Path.GetFileName(path))]
            : [];

    public byte[] ReadAllBytes(string name) => File.ReadAllBytes(PathOf(name));

    public void WriteAllBytes(string name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        System.IO.Directory.CreateDirectory(Directory);

        // Durchschreiben und Puffer leeren, damit ein Stromausfall nach der Rückkehr nichts mehr verliert (ER-06)
        using var stream = new FileStream(
            PathOf(name), FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    // Umbenennen ersetzt das Ziel in einem Schritt; ein Absturz hinterlässt die alte oder die neue Fassung
    public void Replace(string sourceName, string destinationName) =>
        File.Move(PathOf(sourceName), PathOf(destinationName), overwrite: true);

    public void Delete(string name)
    {
        // File.Delete scheitert, wenn es das Verzeichnis noch nicht gibt
        var path = PathOf(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public DateTimeOffset GetLastWriteTime(string name) => File.GetLastWriteTimeUtc(PathOf(name));

    private string PathOf(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Path.GetFileName(name) != name)
        {
            throw new ArgumentException("Nur Dateinamen ohne Pfad sind erlaubt.", nameof(name));
        }

        return Path.Combine(Directory, name);
    }
}
