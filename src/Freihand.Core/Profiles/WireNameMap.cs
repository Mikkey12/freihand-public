namespace Freihand.Core.Profiles;

/// <summary>
/// Feste Namen eines Enums im Datenformat (OF-01). Die Namen im JSON hängen bewusst nicht an den
/// C#-Bezeichnern, damit eine Umbenennung im Code kein gespeichertes Profil bricht.
/// </summary>
public sealed class WireNameMap<T>
    where T : struct, Enum
{
    private readonly Dictionary<T, string> names;
    private readonly Dictionary<string, T> values;

    public WireNameMap(IEnumerable<KeyValuePair<T, string>> entries)
    {
        names = new Dictionary<T, string>(entries);
        values = names.ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.Ordinal);

        var missing = Enum.GetValues<T>().Where(value => !names.ContainsKey(value)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Für {typeof(T).Name} fehlt ein Name im Datenformat: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>Alle Namen in der Reihenfolge der Einträge, etwa für Meldungen und den Abgleich mit dem Schema.</summary>
    public IReadOnlyList<string> Names => [.. names.Values];

    public string NameOf(T value) => names[value];

    /// <summary>
    /// Name für den Export: ein Wert, der in keinem Eintrag vorkommt (etwa ein Wert ausserhalb des Enums aus
    /// einer Umwandlung), ergibt einen Platzhalter, den die Prüfung beim Wiederlesen als unbekannten Wert am
    /// Feld abweist. So wirft der Export seine zugesagte Ausnahme und keine interne <c>KeyNotFoundException</c>,
    /// und der Wert selbst erscheint nirgends.
    /// </summary>
    internal string NameForExport(T value) => names.TryGetValue(value, out var name) ? name : "ungültig";

    /// <summary>Gross- und Kleinschreibung zählt; das Datenformat ist streng.</summary>
    public bool TryParse(string? name, out T value)
    {
        if (name is not null && values.TryGetValue(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
