namespace Freihand.Core.Profiles;

/// <summary>
/// Zugelassene Endpunkte für den Import (OF-01, WE-03). Ein Eintrag gibt Schema, Rechner, Port und
/// optional einen Pfad vor; ein Endpunkt passt, wenn sein Pfad gleich ist oder darunter liegt. Ohne
/// Eintrag ist kein Endpunkt zugelassen.
/// </summary>
public sealed class EndpointAllowList
{
    private readonly IReadOnlyList<Uri> entries;

    public EndpointAllowList(IEnumerable<Uri> entries)
    {
        this.entries = [.. entries];

        foreach (var entry in this.entries)
        {
            if (!entry.IsAbsoluteUri)
            {
                throw new ArgumentException($"„{entry}“ ist kein absoluter Endpunkt.", nameof(entries));
            }

            if (!string.IsNullOrEmpty(entry.UserInfo) || !string.IsNullOrEmpty(entry.Query) || !string.IsNullOrEmpty(entry.Fragment))
            {
                throw new ArgumentException($"„{entry}“ darf weder Zugangsdaten noch Abfrage noch Anker enthalten.", nameof(entries));
            }
        }
    }

    /// <summary>Lässt keinen Endpunkt zu; Profile ohne Endpunkt sind davon nicht betroffen.</summary>
    public static EndpointAllowList None { get; } = new([]);

    public IReadOnlyList<Uri> Entries => entries;

    public bool Allows(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            return false;
        }

        try
        {
            return entries.Any(entry => Matches(entry, endpoint));
        }
        catch (UriFormatException)
        {
            // Ein Rechnername, den die internationale Namensform nicht abbilden kann, ist nie zugelassen.
            return false;
        }
    }

    private static bool Matches(Uri entry, Uri endpoint)
    {
        if (!string.Equals(entry.Scheme, endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.IdnHost, endpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
            || entry.Port != endpoint.Port)
        {
            return false;
        }

        var prefix = entry.AbsolutePath.TrimEnd('/');
        var path = endpoint.AbsolutePath;
        return prefix.Length == 0
            || string.Equals(path.TrimEnd('/'), prefix, StringComparison.Ordinal)
            || path.StartsWith(prefix + "/", StringComparison.Ordinal);
    }
}
