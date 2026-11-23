using Freihand.Zielprobe;

namespace Freihand.Zielprobe.Tests;

/// <summary>
/// WM05: Einordnung eines zurückgelesenen Texts mit Umbrüchen; keine UI Automation,
/// kein Desktop. Erfundene Texte.
/// </summary>
public sealed class UmbruchTests
{
    private static readonly string Erwartet = Umbruch.Testtext;

    private static string Zeilen(string trenner) => Erwartet.Replace("\n", trenner, StringComparison.Ordinal);

    [Fact]
    public void Testtext_HatZweiUmbrücheUndEineLeerzeileOhneRandumbruch()
    {
        Assert.Equal(3, Erwartet.Count(zeichen => zeichen == '\n'));
        Assert.Contains("\n\n", Erwartet, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', Erwartet);
        Assert.False(Erwartet.StartsWith('\n') || Erwartet.EndsWith('\n'), "Kein Umbruch am Rand");
    }

    [Fact]
    public void Vereinheitlichen_JedeUmbruchartWirdEinLfUndAbschlussumbrücheFallenWeg()
    {
        var absatz = (char)0x2029;
        var zeile = (char)0x2028;
        var gelesen = "a\r\nb\rc\vd" + absatz + "e" + zeile + "f\r\n\r\n";

        Assert.Equal("a\nb\nc\nd\ne\nf", Logik.UmbrücheVereinheitlichen(gelesen));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\v")]
    public void Prüfen_AlleUmbrücheAnDerRichtigenStelleIstUmbruch(string trenner)
    {
        Assert.Equal(UmbruchErgebnis.Umbruch, Logik.UmbruchPrüfen(Erwartet, Zeilen(trenner)));

        // Word und Outlook melden auch im leeren Dokument ein Absatzzeichen am Ende
        Assert.Equal(UmbruchErgebnis.Umbruch, Logik.UmbruchPrüfen(Erwartet, Zeilen(trenner) + "\r"));
    }

    [Fact]
    public void Prüfen_AbsatztrennerVonWordIstUmbruch()
    {
        var absatz = ((char)0x2029).ToString();

        Assert.Equal(UmbruchErgebnis.Umbruch, Logik.UmbruchPrüfen(Erwartet, Zeilen(absatz)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Prüfen_UmbrücheWeggelassenOderAlsLeerzeichenIstOhneUmbruch(string ersatz)
    {
        Assert.Equal(UmbruchErgebnis.OhneUmbruch, Logik.UmbruchPrüfen(Erwartet, Zeilen(ersatz)));
    }

    [Fact]
    public void Prüfen_NichtsOderNurDerAnfangIstFehlt()
    {
        // Etwa wenn ein Umbruch eine Nachricht gesendet oder ein Formular abgeschickt hat
        Assert.Equal(UmbruchErgebnis.Fehlt, Logik.UmbruchPrüfen(Erwartet, string.Empty));
        Assert.Equal(UmbruchErgebnis.Fehlt, Logik.UmbruchPrüfen(Erwartet, "\r\n"));
        Assert.Equal(UmbruchErgebnis.Fehlt, Logik.UmbruchPrüfen(Erwartet, Erwartet.Split('\n')[0]));
    }

    [Fact]
    public void Prüfen_VerloreneLeerzeileIstAbweichend()
    {
        var ohneLeerzeile = Erwartet.Replace("\n\n", "\n", StringComparison.Ordinal);

        Assert.Equal(UmbruchErgebnis.Abweichend, Logik.UmbruchPrüfen(Erwartet, ohneLeerzeile));
    }

    [Fact]
    public void Prüfen_AndererTextIstAbweichend()
    {
        Assert.Equal(UmbruchErgebnis.Abweichend, Logik.UmbruchPrüfen(Erwartet, "Ganz anderer Inhalt im Feld"));
        Assert.Equal(UmbruchErgebnis.Abweichend, Logik.UmbruchPrüfen(Erwartet, Erwartet + " doppelt " + Erwartet));
    }
}
