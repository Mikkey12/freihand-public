using Freihand.Core.Recovery;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Was die Liste „Wiederherstellbar“ als gerettet zeigt (Handtest P9 vom 11.11.2026). Regel: Gerettet ist nur Text, der
/// nirgends angekommen ist (nicht übernommen). Nur er färbt das Symbol gelb und öffnet beim Start das Diktatfenster.
/// Übernommener Text bleibt während der Haltefrist still abrufbar, als „übernommen“ gekennzeichnet (ER-06).
/// </summary>
public static class DraftOffers
{
    public static bool IsRescued(RecoverableDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.HandedOverAt is null;
    }

    public static int Rescued(RecoveryScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return scan.Drafts.Count(IsRescued);
    }

    /// <summary>Gerettete zuerst, je neueste zuerst; danach die übernommenen.</summary>
    public static IReadOnlyList<RecoverableDraft> Ordered(RecoveryScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return [.. scan.Drafts.OrderBy(draft => IsRescued(draft) ? 0 : 1).ThenByDescending(draft => draft.UpdatedAt)];
    }

    /// <summary>Beim Start öffnen und nachfragen nur, wenn etwas gerettet oder nicht lesbar ist (AU-12).</summary>
    public static bool NeedsAttention(RecoveryScan scan) => Rescued(scan) > 0 || scan.Issues.Count > 0;
}
