using System.Globalization;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Messprotokoll für die Abnahmen RM10 und RM11 (P8 Kapitel 10): Hängt jede Protokollzeile der Steuerung mit UTC-Zeitstempel
/// an die Datei aus <see cref="VariableName"/>. Ohne diese Umgebungsvariable entsteht keine Datei. Die Zeilen tragen nie
/// Text (<see cref="ControllerUpdate.LogLine"/>, SI-01).
/// </summary>
public static class MeasurementLog
{
    /// <summary>Umgebungsvariable mit dem Pfad der Protokolldatei.</summary>
    public const string VariableName = "FREIHAND_MESSPROTOKOLL";

    /// <summary>Gibt jede Zeile an <paramref name="inner"/> und, wenn die Variable einen Pfad enthält, zusätzlich an die Datei.</summary>
    /// <param name="environment">Liest eine Umgebungsvariable; null oder leer heisst nicht gesetzt.</param>
    /// <param name="inner">Bisheriges Ziel (z. B. Debugausgabe); darf fehlen.</param>
    public static Action<string> Create(Func<string, string?> environment, Action<string>? inner)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var path = environment(VariableName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return line => inner?.Invoke(line);
        }

        var gate = new object();
        return line =>
        {
            inner?.Invoke(line);
            var entry = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
            try
            {
                lock (gate)
                {
                    File.AppendAllText(path, entry);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Das Messprotokoll darf kein Diktat stören
            }
        };
    }
}
