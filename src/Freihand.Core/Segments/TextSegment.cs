namespace Freihand.Core.Segments;

/// <summary>Stufe eines Textabschnitts (ER-02, Anhang B).</summary>
public enum SegmentState
{
    /// <summary>Die Erkennung kann den Text noch ändern; er erscheint nie in fremden Programmen.</summary>
    Provisional,

    /// <summary>Die Erkennung ändert den Text nicht mehr; nur fester Text wird ausgegeben und gesichert.</summary>
    Fixed,
}

/// <summary>Ein Abschnitt erkannten Texts. Ein fester Abschnitt wird nie still verändert (ER-02).</summary>
/// <param name="Id">Bleibt über alle Fassungen eines Abschnitts gleich.</param>
/// <param name="Text">Erkannter Text.</param>
/// <param name="State">Vorläufig oder fest.</param>
/// <param name="Start">Beginn im Audio der Sitzung.</param>
/// <param name="End">Ende im Audio der Sitzung.</param>
public sealed record TextSegment(Guid Id, string Text, SegmentState State, TimeSpan Start, TimeSpan End);
