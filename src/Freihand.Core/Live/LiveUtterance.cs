using Freihand.Core.Recognition;

namespace Freihand.Core.Live;

/// <summary>
/// Zustand einer noch nicht abgeschlossenen Äusserung in <see cref="LiveTranscription"/> (Kapitel 5.1 und 5.2):
/// Beginn, Schnittzeit <c>C</c>, Kürzungspunkt <c>K</c>, übernommene Wörter und, sobald sie feststeht, die Grenze. Live
/// übernimmt ein Schritt nichts (Entscheid E1 vom 03.11.2026); übernommen wird nur am Längenschnitt und am Abschluss.
/// </summary>
internal sealed class LiveUtterance
{
    private readonly List<RecognizedWord> _stable = [];

    public LiveUtterance(TimeSpan start, TimeSpan maxUtterance)
        : this(start, start, maxUtterance)
    {
    }

    /// <param name="speechStart">Erkannter Sprachbeginn; davor liegt der Vorlauf (<see cref="LiveOptions.Lead"/>).</param>
    public LiveUtterance(TimeSpan start, TimeSpan speechStart, TimeSpan maxUtterance)
    {
        SpeechStart = speechStart;
        Start = start;
        Limit = start + maxUtterance;
        CutPoint = start;
        DecodedUntil = start;
    }

    /// <summary>Kennung der Äusserung, zugleich ihres vorläufigen und festen Abschnitts (T41).</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Beginn des ersten Sprachbereichs nach der vorherigen Grenze, frühestens die Grenze selbst.</summary>
    public TimeSpan Start { get; }

    /// <summary>Erkannter Sprachbeginn; zwischen <see cref="Start"/> und hier liegt der Vorlauf.</summary>
    public TimeSpan SpeechStart { get; }

    /// <summary>Schnittzeit <c>C</c>: kein Decode der Äusserung reicht darüber hinaus (Regel 3).</summary>
    public TimeSpan Limit { get; }

    /// <summary>Kürzungspunkt <c>K</c>: Beginn des noch nicht übernommenen Audios; geht nie zurück.</summary>
    public TimeSpan CutPoint { get; private set; }

    /// <summary>Grenze am Ende der Äusserung, sobald sie feststeht.</summary>
    public TimeSpan? Boundary { get; private set; }

    public UtteranceEnd Ending { get; private set; }

    /// <summary>Eigene Kopie des Audios [K, Grenze), sobald die Grenze feststeht.</summary>
    public DetachedAudio? Audio { get; set; }

    /// <summary>Am Längenschnitt übernommene Wörter und die des Abschluss-Decodes (Regel 3, Kapitel 5.2).</summary>
    public IReadOnlyList<RecognizedWord> Stable => _stable;

    /// <summary>Die Wörter der jüngsten Hypothese nach <c>K</c>, für die Vorschau (Kapitel 5.2, E1 a).</summary>
    public IReadOnlyList<RecognizedWord> Tail { get; private set; } = [];

    /// <summary>
    /// Bis hier ist die offene Äusserung live verarbeitet: Ende des jüngsten verarbeiteten Schritts, sonst <c>K</c>. Audio
    /// davor ist gewollt gehalten, kein Verarbeitungsrückstand (E2).
    /// </summary>
    public TimeSpan DecodedUntil { get; private set; }

    /// <summary>Zuletzt gemeldeter vorläufiger Text.</summary>
    public string Preview { get; set; } = string.Empty;

    /// <summary>Audio, das diese Äusserung noch hält, solange sie abgeschlossen ist und auf ihre Erkennung wartet.</summary>
    public TimeSpan Held => Boundary is { } boundary && boundary > CutPoint ? boundary - CutPoint : TimeSpan.Zero;

    public void Close(TimeSpan boundary, UtteranceEnd ending)
    {
        Boundary = boundary;
        Ending = ending;
    }

    /// <summary>
    /// Ein Live-Schritt (Kapitel 5.2, E1 a): Die Hypothese wird ganz zur Vorschau, nichts wird übernommen, <c>K</c> bleibt.
    /// </summary>
    public void ShowHypothesis(IReadOnlyList<RecognizedWord> words, TimeSpan decodeEnd)
    {
        Tail = words;
        if (decodeEnd > DecodedUntil)
        {
            DecodedUntil = decodeEnd;
        }
    }

    /// <summary>
    /// Übernimmt Wörter bei einem erzwungenen Schnitt und setzt <c>K</c>; <paramref name="rest"/> sind die Wörter der
    /// Hypothese nach dem Schnitt, die neu decodiert werden.
    /// </summary>
    public void Cut(IEnumerable<RecognizedWord> adopted, TimeSpan cut, IReadOnlyList<RecognizedWord> rest)
    {
        Adopt(adopted, cut);
        Tail = rest;
    }

    /// <summary>Hängt die Wörter des Abschluss-Decodes an.</summary>
    public void AddRest(IEnumerable<RecognizedWord> words) => _stable.AddRange(words);

    private void Adopt(IEnumerable<RecognizedWord> words, TimeSpan cut)
    {
        _stable.AddRange(words);
        if (cut > CutPoint)
        {
            CutPoint = cut;
        }
    }
}
