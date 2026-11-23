using Freihand.Zielprobe;

namespace Freihand.Zielprobe.Tests;

/// <summary>Gegenproben der Codex-Prüfung von PR #54 (Einordnung im Befehl umbruch), unverändert übernommen.</summary>
public sealed class Pr54ReviewCounterTests
{
    [Fact]
    public void LostWordSpacesAreDeviatingEvenWhenLineBreaksAreAlsoLost()
    {
        const string expected = "Alpha beta\nGamma delta";
        const string received = "AlphabetaGammadelta";
        Assert.Equal(UmbruchErgebnis.Abweichend, Logik.UmbruchPrüfen(expected, received));
    }

    [Fact]
    public void AShortPrefixWithSuppressedLineBreaksIsMissing()
    {
        const string expected = "Alpha\nBeta\nGamma";
        const string received = "AlphaBeta";
        Assert.Equal(UmbruchErgebnis.Fehlt, Logik.UmbruchPrüfen(expected, received));
    }
}
