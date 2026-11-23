using System.Text;

namespace Freihand.Core.Segments;

/// <summary>
/// Fügeregel für feste Abschnitte und Teile einer Äusserung (P3-Konzept, Kapitel 3): dazwischen genau ein Leerzeichen,
/// ausser der vorherige Teil endet mit einem Umbruch oder der neue beginnt mit einem Umbruch oder mit
/// <c>. , ; : ! ? …</c>. Gilt für die Auslegung (P3), die Ausgabe (P7) und den Entwurf (P5).
/// </summary>
public static class TextJoin
{
    /// <summary>Zeichen, vor die kein Leerzeichen kommt.</summary>
    private const string NoSpaceBefore = ".,;:!?…";

    /// <summary>
    /// Fügt die Teile in dieser Reihenfolge nach der Fügeregel zusammen (T50, T54, T56). Leere Teile tragen nichts bei
    /// und setzen auch kein Leerzeichen. Umbrüche sind im Kern LF; sie bleiben, wie sie sind.
    /// </summary>
    public static string Join(IEnumerable<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            if (text.Length > 0 && text[^1] != '\n' && part[0] != '\n' && !NoSpaceBefore.Contains(part[0], StringComparison.Ordinal))
            {
                text.Append(' ');
            }

            text.Append(part);
        }

        return text.ToString();
    }
}
