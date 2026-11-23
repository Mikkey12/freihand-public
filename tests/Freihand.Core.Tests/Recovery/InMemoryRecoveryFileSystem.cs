using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

/// <summary>Ablage im Arbeitsspeicher; kann Schreibfehler und Abstürze nachstellen (ER-06).</summary>
internal sealed class InMemoryRecoveryFileSystem(TimeProvider time) : IRecoveryFileSystem
{
    public Dictionary<string, (byte[] Data, DateTimeOffset WrittenAt)> Files { get; } = [];

    /// <summary>Lässt jedes Schreiben mit einem Ein-/Ausgabefehler scheitern, z. B. Datenträger voll.</summary>
    public bool FailWrites { get; set; }

    /// <summary>Lässt jedes Lesen scheitern, z. B. Datei vorübergehend gesperrt.</summary>
    public bool FailReads { get; set; }

    /// <summary>Lässt das Auflisten der Ablage scheitern.</summary>
    public bool FailList { get; set; }

    /// <summary>Lässt das Löschen vorhandener Dateien scheitern, z. B. Datei gesperrt.</summary>
    public bool FailDeletes { get; set; }

    /// <summary>Stellt einen Absturz nach dem Schreiben der temporären Datei und vor dem Umbenennen nach.</summary>
    public bool CrashBeforeReplace { get; set; }

    /// <summary>Stellt einen Stromausfall mitten im Schreiben nach: nur ein Teil landet in der Datei.</summary>
    public bool CrashDuringWrite { get; set; }

    public IReadOnlyList<string> ListFiles() =>
        FailList ? throw new IOException("Das Verzeichnis ist vorübergehend nicht lesbar.") : [.. Files.Keys];

    public byte[] ReadAllBytes(string name)
    {
        if (FailReads)
        {
            throw new IOException("Die Datei wird von einem anderen Prozess verwendet.");
        }

        return Files.TryGetValue(name, out var file) ? [.. file.Data] : throw new FileNotFoundException(name);
    }

    public void WriteAllBytes(string name, byte[] data)
    {
        if (FailWrites)
        {
            throw new IOException("Auf dem Datenträger ist nicht genügend Speicherplatz verfügbar.");
        }

        if (CrashDuringWrite)
        {
            Files[name] = (data[..(data.Length / 2)], time.GetUtcNow());
            throw new SimulatedCrashException();
        }

        Files[name] = ([.. data], time.GetUtcNow());
    }

    public void Replace(string sourceName, string destinationName)
    {
        if (CrashBeforeReplace)
        {
            throw new SimulatedCrashException();
        }

        if (!Files.Remove(sourceName, out var file))
        {
            throw new FileNotFoundException(sourceName);
        }

        Files[destinationName] = file;
    }

    public void Delete(string name)
    {
        // Wie beim echten Dateisystem scheitert nur das Löschen einer vorhandenen Datei
        if (FailDeletes && Files.ContainsKey(name))
        {
            throw new IOException("Die Datei wird von einem anderen Prozess verwendet.");
        }

        Files.Remove(name);
    }

    public DateTimeOffset GetLastWriteTime(string name) =>
        Files.TryGetValue(name, out var file) ? file.WrittenAt : throw new FileNotFoundException(name);
}

/// <summary>Steht für einen harten Abbruch des Prozesses; der Kern fängt sie absichtlich nicht.</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("Nachgestellter Absturz")
    {
    }
}
