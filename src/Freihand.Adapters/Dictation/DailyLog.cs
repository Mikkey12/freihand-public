using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Protokolldatei der Beta: je Tag eine Datei <c>JJJJ-MM-TT.txt</c> im
/// Ordner, jede Zeile mit Ortszeit davor. Hinein kommen nur Zeilen ohne Text (SI-01): die Zustandszeilen der Steuerung,
/// Start und Ende von Freihand, Marken. Dateien älter als <see cref="KeepDays"/> Tage löscht <see cref="DeleteOld"/>.
/// Ein Schreib- oder Löschfehler wirft nie: Das Protokoll darf kein Diktat stören.
/// </summary>
public sealed partial class DailyLog(string folder, TimeProvider time)
{
    /// <summary>Aufbewahrung nach Entscheid B-E4.</summary>
    public const int KeepDays = 14;

    private readonly Lock _gate = new();

    /// <summary>Tag der letzten Bereinigung; beim ersten Schreiben eines neuen Tags wird bereinigt (Sicherheitsprüfung).</summary>
    private DateOnly? _cleaned;

    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary>Schreibt eine Zeile; Zeilenumbrüche darin werden zu Leerzeichen. Fehler bleiben folgenlos.</summary>
    public void Write(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var now = time.GetLocalNow();
        var entry = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture) + " "
            + line.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ') + "\n";
        var path = Path.Combine(Folder, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".txt");

        // Aufbewahrung auch im laufenden Betrieb, nicht nur beim Start: einmal je Tag
        var today = DateOnly.FromDateTime(now.DateTime);
        bool clean;
        lock (_gate)
        {
            clean = _cleaned != today;
            _cleaned = today;
        }

        if (clean)
        {
            DeleteOld();
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(path, entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Kein Protokoll ist besser als ein gestörtes Diktat
        }
    }

    /// <summary>
    /// Löscht Tagesdateien, deren Datum mehr als <see cref="KeepDays"/> Tage vor heute liegt; nur Dateien mit genau dem
    /// eigenen Namensmuster, nie andere. Gibt die Zahl der gelöschten zurück.
    /// </summary>
    public int DeleteOld()
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var deleted = 0;
        try
        {
            if (!Directory.Exists(Folder))
            {
                return 0;
            }

            foreach (var path in Directory.EnumerateFiles(Folder, "*.txt"))
            {
                if (DayFile().IsMatch(Path.GetFileName(path)) &&
                    DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day) &&
                    day.DayNumber < today.DayNumber - KeepDays)
                {
                    try
                    {
                        File.Delete(path);
                        deleted++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Beim nächsten Start erneut
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ordner nicht lesbar: nichts gelöscht
        }

        return deleted;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\.txt$")]
    private static partial Regex DayFile();
}

/// <summary>
/// „Problem merken“ (B5): schreibt eine Marke mit laufender Nummer, Quelle und dem letzten Zustand ins Protokoll, ohne Text.
/// So lässt sich eine Notiz auf dem Beobachtungsblatt später einer Stelle im Protokoll zuordnen.
/// </summary>
public sealed class ProblemMarker(Action<string> log, Func<string?> state)
{
    private int _number;

    /// <param name="source">„taste“ oder „menü“.</param>
    /// <returns>Die Nummer der Marke, ab 1 je Start von Freihand.</returns>
    public int Mark(string source)
    {
        var number = Interlocked.Increment(ref _number);
        string? current;
        try
        {
            current = state();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            current = null;
        }

        log(string.Create(CultureInfo.InvariantCulture, $"marke nr={number} quelle={source} {current ?? "zustand=unbekannt"}"));
        return number;
    }
}
