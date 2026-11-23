using System.Collections;

namespace Freihand.Core.Profiles;

/// <summary>
/// Unveränderliche Liste von Texten mit Gleichheit nach Inhalt und Reihenfolge (ordinal); kopiert beim
/// Erstellen. Damit bleibt <see cref="Profile"/> ein Wert: zwei Profile mit gleichen Angaben sind gleich,
/// auch mit verschiedenen Listenobjekten.
/// </summary>
public sealed class TermList : IReadOnlyList<string>, IEquatable<TermList>
{
    private readonly string[] items;

    public TermList(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        this.items = [.. items];
        if (this.items.Any(item => item is null))
        {
            throw new ArgumentNullException(nameof(items), "Die Liste enthält einen Eintrag ohne Wert.");
        }
    }

    private TermList()
    {
        items = [];
    }

    public static TermList Empty { get; } = new();

    public int Count => items.Length;

    public string this[int index] => items[index];

    /// <summary>Ob der Eintrag vorkommt, ordinal verglichen.</summary>
    public bool Contains(string item) => Array.IndexOf(items, item) >= 0;

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(TermList? other) =>
        other is not null && (ReferenceEquals(this, other) || items.AsSpan().SequenceEqual(other.items));

    public override bool Equals(object? obj) => Equals(obj as TermList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in items)
        {
            hash.Add(item, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(TermList? left, TermList? right) => Equals(left, right);

    public static bool operator !=(TermList? left, TermList? right) => !Equals(left, right);
}
