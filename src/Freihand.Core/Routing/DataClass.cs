namespace Freihand.Core.Routing;

/// <summary>
/// Datenklasse eines Profils (WE-03). Privat gelten die drei Standardklassen; in der Geschäftsausgabe
/// gibt die IT Klassen und Bezeichnungen vor (GE-04). Eine höhere Stufe ist schutzbedürftiger.
/// </summary>
public sealed record DataClass(string Id, int Rank) : IComparable<DataClass>
{
    public static DataClass Public { get; } = new("oeffentlich", 0);

    public static DataClass Internal { get; } = new("intern", 1);

    public static DataClass Confidential { get; } = new("vertraulich", 2);

    public int CompareTo(DataClass? other) => other is null ? 1 : Rank.CompareTo(other.Rank);

    /// <summary>Die höchste Einstufung bleibt bestehen und lässt sich nicht herabsetzen (WE-03).</summary>
    public static DataClass Highest(DataClass first, DataClass second) =>
        first.CompareTo(second) >= 0 ? first : second;
}
