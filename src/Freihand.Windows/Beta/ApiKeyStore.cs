using System.Security.Cryptography;
using System.Text;
using Freihand.Windows.Recovery;

namespace Freihand.Windows.Beta;

/// <summary>
/// Schlüssel für die Claude-API: mit DPAPI im Benutzerkontext verschlüsselt in einer
/// eigenen Datei, eigener Zusatzwert. Nie im Klartext auf dem Datenträger, nie im Protokoll, nie im Repository.
/// </summary>
public sealed class ApiKeyStore(string path)
{
    public const string Entropy = "Freihand.Schluessel.v1";

    private readonly WindowsDataProtector _protector = new(Entropy);

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public bool HasKey => File.Exists(Path);

    /// <summary>Speichert den Schlüssel; Leerraum am Rand fällt weg (Einfügen aus der Zwischenablage).</summary>
    public void Save(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".neu";
        try
        {
            File.WriteAllBytes(temporary, _protector.Protect(Encoding.UTF8.GetBytes(key.Trim())));
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            // Nach einem Fehler bleibt keine zweite Kopie liegen
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Der Schlüssel, oder null, wenn keiner gespeichert oder er nicht lesbar ist (anderer Benutzer, beschädigt).</summary>
    public string? Load()
    {
        try
        {
            return File.Exists(Path) ? Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(Path))) : null;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
