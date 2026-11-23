using System.Text;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Schreiben von Textdateien, die auch von Hand bearbeitet werden (Einstellungen, gemessene Felder): Lesen wie beim Laden
/// und Ersetzen nur auf den gelesenen Stand (Regel aus der Codex-Prüfung zu PR #69).
/// </summary>
internal static class SafeTextFile
{
    /// <summary>Text und Kodierung der Datei, wie beim Laden (UTF-8 ohne Kennung, sonst nach BOM).</summary>
    public static string Read(string path, out Encoding encoding)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        encoding = reader.CurrentEncoding;
        return text;
    }

    /// <summary>
    /// Regel für jedes Schreiben der Datei (Codex zu PR #69): Freihand schreibt nur auf den Stand, den es gelesen hat.
    /// Zuerst die vollständige Fassung im selben Ordner schreiben, dann prüfen, ob die Datei noch genau dem gelesenen Stand
    /// entspricht (<paramref name="expected"/>; null: sie darf nicht bestehen), und erst dann in einem Schritt ersetzen.
    /// Hat sie sich geändert, etwa weil ein Editor gespeichert hat, bleibt sie, wie sie ist. Die Hilfsdatei wird immer
    /// entfernt. Zwischen Prüfung und Ersetzen bleibt ein Spalt von Mikrosekunden; Windows bietet kein Ersetzen unter Vorbehalt.
    /// </summary>
    /// <returns>Falsch, wenn die Datei nicht mehr dem gelesenen Stand entspricht; dann wurde nichts geschrieben.</returns>
    public static bool ReplaceIfUnchanged(string path, string? expected, string text, Encoding encoding)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.neu");
        try
        {
            File.WriteAllText(temporary, text, encoding);
            if (expected is null)
            {
                try
                {
                    File.Move(temporary, path, overwrite: false);
                    return true;
                }
                catch (IOException) when (File.Exists(path))
                {
                    return false;
                }
            }

            if (!File.Exists(path) || Read(path, out _) != expected)
            {
                return false;
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Bleibt liegen; stört nicht, weil Freihand nur die Datei selbst liest
            }
        }
    }
}
