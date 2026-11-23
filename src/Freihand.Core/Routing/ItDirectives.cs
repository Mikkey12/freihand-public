using System.Collections.Frozen;

namespace Freihand.Core.Routing;

/// <summary>
/// Zentrale Vorgaben der IT (GE-04): Wege freigeben oder sperren, Endpunkte freigeben, Datenklassen vorgeben.
/// Sie gehen Profilen und Importen vor: Nichts in einer Anfrage oder einem Profil kann sie lockern (WE-03).
/// Wege, die eine Ausgabe nicht kennt, kann auch die IT nicht einschalten (GE-02).
/// </summary>
public sealed record ItDirectives
{
    /// <summary>Wege, die eine Freigabe brauchen und freigegeben sind, z. B. ein Firmen-Erkennungsdienst (WE-15).</summary>
    public IReadOnlySet<RecognitionRoute> ApprovedRecognition { get; init; } = FrozenSet<RecognitionRoute>.Empty;

    /// <summary>Wege, die eine Freigabe brauchen und freigegeben sind, z. B. Copilot oder lokal nach Messung (WE-08).</summary>
    public IReadOnlySet<TransformRoute> ApprovedTransform { get; init; } = FrozenSet<TransformRoute>.Empty;

    /// <summary>Gesperrte Wege; eine Sperre geht jeder Freigabe vor.</summary>
    public IReadOnlySet<RecognitionRoute> BlockedRecognition { get; init; } = FrozenSet<RecognitionRoute>.Empty;

    /// <summary>Gesperrte Wege; eine Sperre geht jeder Freigabe vor.</summary>
    public IReadOnlySet<TransformRoute> BlockedTransform { get; init; } = FrozenSet<TransformRoute>.Empty;

    /// <summary>
    /// Freigegebene Adressen für Endpunkte ausserhalb des Geräts. <see langword="null"/> heisst: keine Vorgabe der
    /// IT; in der Geschäftsausgabe ist dann kein Endpunkt ausserhalb des Geräts freigegeben (GE-01).
    /// </summary>
    public IReadOnlySet<string>? ApprovedEndpoints { get; init; }

    /// <summary>
    /// Datenklassen der Firma samt Rang (WE-03, GE-04). Sie ergänzen die drei Standardklassen oder legen deren Rang
    /// neu fest; nur hier definierte Klassen gelten, der Rang in einer Anfrage zählt nicht.
    /// </summary>
    public IReadOnlyList<DataClass> DataClasses { get; init; } = [];

    /// <summary>Regeln je Datenklasse; ersetzen für diese Klasse die Standardregel (GE-04).</summary>
    public IReadOnlyDictionary<string, DataClassRule>? DataClassRules { get; init; }

    /// <summary>Mindestklasse jeder Verarbeitung; eine tiefere Einstufung im Profil wird angehoben (WE-03).</summary>
    public DataClass? MinimumDataClass { get; init; }
}

/// <summary>Einstellungen der Richtlinie.</summary>
public sealed record RoutePolicyOptions(Edition Edition)
{
    /// <summary>Grundregeln je Datenklasse; ohne Angabe die Standardregeln (WE-03).</summary>
    public IReadOnlyDictionary<string, DataClassRule> DataClassRules { get; init; } = DataClassRule.Defaults;

    /// <summary>
    /// Selbst freigegebene Adressen ausserhalb des Geräts, z. B. die eigenen Anbieter. <see langword="null"/> heisst
    /// privat: keine Einschränkung. Gibt auch die IT Endpunkte vor, müssen beide Listen die Adresse enthalten.
    /// </summary>
    public IReadOnlySet<string>? ApprovedEndpoints { get; init; }

    /// <summary>Vorgaben der IT (GE-04); gehen allem anderen vor.</summary>
    public ItDirectives? It { get; init; }
}
