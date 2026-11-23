namespace Freihand.Adapters.Dictation;

/// <summary>Ein Eintrag im Menü „Modell“.</summary>
/// <param name="Path">Vollständiger Pfad der Modelldatei.</param>
/// <param name="Name">Name im Menü, in einfachen Worten.</param>
/// <param name="AudioContext">Rechenfenster, das zu diesem Modell gehört.</param>
/// <param name="Selected">In der Einstellungsdatei gewählt (gilt ab dem nächsten Start).</param>
/// <param name="Running">Läuft gerade.</param>
public sealed record ModelEntry(string Path, string Name, int AudioContext, bool Selected, bool Running);

/// <summary>
/// Menü „Modell“. Zu jedem Modell gehört ein festes Rechenfenster, damit
/// eine Wahl im Menü nie ein Modell mit unpassendem Fenster ergibt: <c>small</c> 850 (Entscheid vom 05.11.2026 für
/// Notebooks), die grossen Modelle das volle Fenster 1500 (mit verkleinertem Fenster wiederholte large-v3-turbo-q5_0 ganze
/// Sätze, Machbarkeitstest B). Angeboten werden nur freigegebene Modelle, deren Datei neben dem laufenden Modell liegt. Die
/// Wahl gilt ab dem nächsten Start von Freihand; ein laufendes Diktat bleibt unberührt.
/// </summary>
public static class ModelMenu
{
    /// <summary>Dateiname, Name im Menü, Rechenfenster; in dieser Reihenfolge im Menü.</summary>
    public static IReadOnlyList<(string File, string Name, int AudioContext)> Choices { get; } =
    [
        ("ggml-small.bin", "Klein – schnell", 850),
        ("ggml-large-v3-turbo-q5_0.bin", "Gross – genauer", 1500),
        ("ggml-large-v3-turbo.bin", "Gross – genauer, volle Grösse", 1500),
    ];

    /// <summary>
    /// Rechenfenster, das zum Modell gehört; null für ein Modell ausserhalb der Tabelle (dann gilt „audiokontext“ aus der
    /// Datei). Gilt beim Laden der Einstellungen, damit auch eine Änderung von Hand nur am Modell nie ein fremdes Fenster erbt.
    /// </summary>
    public static int? WindowFor(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var file = System.IO.Path.GetFileName(model);
        foreach (var choice in Choices)
        {
            if (string.Equals(choice.File, file, StringComparison.OrdinalIgnoreCase))
            {
                return choice.AudioContext;
            }
        }

        return null;
    }

    /// <param name="running">Modell, mit dem Freihand gestartet ist.</param>
    /// <param name="saved">Modell in der Einstellungsdatei (nach einer Wahl im Menü).</param>
    /// <param name="exists">Prüft, ob die Datei vorhanden ist.</param>
    public static IReadOnlyList<ModelEntry> Entries(string running, string saved, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(exists);
        var folder = System.IO.Path.GetDirectoryName(running);
        if (string.IsNullOrEmpty(folder))
        {
            return [];
        }

        return
        [
            .. Choices
                .Select(choice => (choice, path: System.IO.Path.Combine(folder, choice.File)))
                .Where(item => exists(item.path))
                .Select(item => new ModelEntry(
                    item.path, item.choice.Name, item.choice.AudioContext, Same(item.path, saved), Same(item.path, running))),
        ];
    }

    /// <summary>Text im Menü: der gewählte Eintrag, der noch nicht läuft, sagt es.</summary>
    public static string Label(ModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Selected && !entry.Running ? entry.Name + " (ab Neustart)" : entry.Name;
    }

    /// <summary>Hinweis nach der Wahl.</summary>
    public static string Notice(ModelEntry entry, bool saved)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!saved)
        {
            return $"Modell „{entry.Name}“ nicht gespeichert (Einstellungsdatei gesperrt?)";
        }

        return entry.Running
            ? $"Modell: {entry.Name}"
            : $"Modell: {entry.Name} – gilt nach Neustart von Freihand (Beenden und wieder starten)";
    }

    /// <summary>Gleiche Datei; ein unlesbarer Pfad aus der Einstellungsdatei gilt als verschieden, nie als Absturz.</summary>
    private static bool Same(string path, string other)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
