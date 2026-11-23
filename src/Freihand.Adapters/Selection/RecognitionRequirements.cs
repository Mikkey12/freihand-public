using Freihand.Core.Recognition;

namespace Freihand.Adapters.Selection;

/// <summary>
/// Was ein Erkennungsadapter mindestens können muss, verglichen mit den gemeldeten Fähigkeiten (WE-02).
/// Nicht verlangte Fähigkeiten spielen keine Rolle.
/// </summary>
/// <param name="MinSessionDuration">Eine Sitzung muss mindestens so lange dauern dürfen; ohne Dauergrenze immer erfüllt.</param>
/// <param name="DataRegion">Verlangte Datenregion, ohne Gross-/Kleinschreibung verglichen.</param>
public sealed record RecognitionRequirements(
    bool PartialResults = false,
    bool FixedSegments = false,
    bool VocabularyHints = false,
    bool Cancellation = false,
    TimeSpan? MinSessionDuration = null,
    string? DataRegion = null)
{
    public static RecognitionRequirements None { get; } = new();

    /// <summary>Gibt die erste nicht erfüllte Anforderung zurück, sonst <c>null</c>.</summary>
    public string? FindGap(RecognitionCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (PartialResults && !capabilities.PartialResults)
        {
            return "keine Teilergebnisse";
        }

        if (FixedSegments && !capabilities.FixedSegments)
        {
            return "keine festen Abschnitte";
        }

        if (VocabularyHints && !capabilities.VocabularyHints)
        {
            return "keine Vokabularhinweise";
        }

        if (Cancellation && !capabilities.Cancellation)
        {
            return "kein Abbruch";
        }

        if (MinSessionDuration is { } min && capabilities.MaxSessionDuration is { } max && max < min)
        {
            return $"Sitzung höchstens {max}";
        }

        if (DataRegion is not null
            && !string.Equals(DataRegion, capabilities.DataRegion, StringComparison.OrdinalIgnoreCase))
        {
            return $"Datenregion {capabilities.DataRegion ?? "unbekannt"}";
        }

        return null;
    }

    public bool IsSatisfiedBy(RecognitionCapabilities capabilities) => FindGap(capabilities) is null;
}
