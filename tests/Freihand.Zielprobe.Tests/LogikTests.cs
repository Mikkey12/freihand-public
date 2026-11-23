using System.Text.Json;
using Freihand.Zielprobe;

namespace Freihand.Zielprobe.Tests;

/// <summary>Die reinen Teile des Zielprobe-Werkzeugs; keine UI Automation, kein Desktop.</summary>
public sealed class LogikTests
{
    [Fact]
    public void Vergleich_GleicherText_IstGleichOhneAbweichung()
    {
        var v = Logik.Vergleichen("Grüsse aus Zürich", "Grüsse aus Zürich");
        Assert.True(v.Gleich);
        Assert.Null(v.ErsteAbweichung);
        Assert.Equal(0, v.AbweichendeZeichen);
        Assert.Equal((17, 17), (v.Erwartet, v.Gelesen));
    }

    [Fact]
    public void Vergleich_AnderesZeichen_ZaehltPositionUndIndex()
    {
        var v = Logik.Vergleichen("abcdef", "abXdYf");
        Assert.False(v.Gleich);
        Assert.Equal(2, v.ErsteAbweichung);
        Assert.Equal(2, v.AbweichendeZeichen);
    }

    [Fact]
    public void Vergleich_ZuKurz_ErsteAbweichungIstDieKuerzereLaenge()
    {
        var v = Logik.Vergleichen("abcdef", "abcd");
        Assert.False(v.Gleich);
        Assert.Equal(4, v.ErsteAbweichung);
        Assert.Equal(2, v.AbweichendeZeichen);
        Assert.Equal((6, 4), (v.Erwartet, v.Gelesen));
    }

    [Fact]
    public void Vergleich_ZuLangMitFruehAbweichung_ZaehltBeides()
    {
        var v = Logik.Vergleichen("abc", "aXcde");
        Assert.Equal(1, v.ErsteAbweichung);
        Assert.Equal(3, v.AbweichendeZeichen);
    }

    [Fact]
    public void Vergleich_LeerGegenText_IstAbweichungAnIndexNull()
    {
        var v = Logik.Vergleichen("abc", string.Empty);
        Assert.Equal(0, v.ErsteAbweichung);
        Assert.Equal(3, v.AbweichendeZeichen);
    }

    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\rb", "a b")]
    [InlineData("a\n\nb\n", "a  b ")]
    [InlineData("ohne", "ohne")]
    public void Umbrueche_WerdenJeDurchLeerzeichenErsetzt(string eingabe, string erwartet) =>
        Assert.Equal(erwartet, Logik.UmbrücheErsetzen(eingabe));

    [Fact]
    public void Umbrueche_TesttextBehaeltSeineLaenge()
    {
        var pfad = Path.Combine(AppContext.BaseDirectory, "Fixtures", "au01-testtext.txt");
        var text = File.ReadAllText(pfad);
        Assert.Equal(2000, text.Length);
        var ersetzt = Logik.UmbrücheErsetzen(text);
        Assert.Equal(2000, ersetzt.Length);
        Assert.DoesNotContain('\n', ersetzt);
    }

    [Fact]
    public void Leeren_GenauDerGeschriebeneText_IstErlaubt() =>
        Assert.True(Logik.DarfLeeren("abc def", "abc def", nochFokussiert: true));

    [Fact]
    public void Leeren_VerloreneZeichen_IstErlaubt() =>
        Assert.True(Logik.DarfLeeren("ac df", "abc def", nochFokussiert: true));

    // Codex zu PR #49: Zeichen des Testtexts in anderer Reihenfolge oder doppelt sind fremder Inhalt
    [Theory]
    [InlineData("cab cab", "abc")]
    [InlineData("cba", "abc")]
    [InlineData("aabc", "abc")]
    [InlineData("abcabc", "abc def")]
    public void Leeren_AndereReihenfolgeOderDoppelt_IstVerboten(string gelesen, string geschrieben) =>
        Assert.False(Logik.DarfLeeren(gelesen, geschrieben, nochFokussiert: true));

    [Fact]
    public void Leeren_FremdeZeichen_IstVerboten() =>
        Assert.False(Logik.DarfLeeren("abc Q", "abc def", nochFokussiert: true));

    [Fact]
    public void Leeren_FokusWeg_IstVerbotenAuchBeiGleichemText() =>
        Assert.False(Logik.DarfLeeren("abc", "abc", nochFokussiert: false));

    [Fact]
    public void Leeren_LeeresFeld_BrauchtKeinLeeren() =>
        Assert.False(Logik.DarfLeeren(string.Empty, "abc", nochFokussiert: true));

    [Fact]
    public void Anhaengen_LegtListeAnUndHaengtAn()
    {
        var ordner = Path.Combine(Path.GetTempPath(), "zielprobe-" + Guid.NewGuid().ToString("N"));
        var pfad = Path.Combine(ordner, "unter", "ergebnis.json");
        try
        {
            Logik.Anhängen(pfad, new { Name = "Eins", Zahl = 1 });
            Logik.Anhängen(pfad, new { Name = "Zwei", Zahl = 2 });

            using var dokument = JsonDocument.Parse(File.ReadAllText(pfad));
            var liste = dokument.RootElement;
            Assert.Equal(JsonValueKind.Array, liste.ValueKind);
            Assert.Equal(2, liste.GetArrayLength());
            Assert.Equal("Eins", liste[0].GetProperty("name").GetString());
            Assert.Equal(2, liste[1].GetProperty("zahl").GetInt32());
            Assert.False(File.Exists(pfad + ".neu"));
        }
        finally
        {
            if (Directory.Exists(ordner))
            {
                Directory.Delete(ordner, recursive: true);
            }
        }
    }

    [Fact]
    public void Anhaengen_KeineListe_LaesstDieDateiUnveraendert()
    {
        var pfad = Path.Combine(Path.GetTempPath(), "zielprobe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(pfad, "{\"a\":1}");
            Assert.Throws<InvalidDataException>(() => Logik.Anhängen(pfad, new { X = 1 }));
            Assert.Equal("{\"a\":1}", File.ReadAllText(pfad));
            File.WriteAllText(pfad, "kein json");
            Assert.Throws<InvalidDataException>(() => Logik.Anhängen(pfad, new { X = 1 }));
            Assert.Equal("kein json", File.ReadAllText(pfad));
        }
        finally
        {
            File.Delete(pfad);
        }
    }

    [Fact]
    public void Argumente_BefehlOptionenUndSchalter()
    {
        var o = Optionen.Lesen(["schreiben", "--wartezeit", "3", "--name", "Outlook Betreff", "--teile", "1,4,10", "--einzeilig", "--platzhalter", "--out", "x.json"]);
        Assert.Equal("schreiben", o.Befehl);
        Assert.Equal(3, o.Zahl("wartezeit", 5));
        Assert.Equal(10, o.Zahl("pause", 10));
        Assert.Equal("Outlook Betreff", o.Pflicht("name"));
        Assert.True(o.Einzeilig);
        Assert.True(o.Platzhalter);
        Assert.False(Optionen.Lesen(["schreiben"]).Platzhalter);
        Assert.Equal([1, 4, 10], Optionen.Teile(o.Text("teile")));
    }

    [Fact]
    public void Argumente_Standardteile_UndFehler()
    {
        Assert.Equal([1, 4, 10, 20], Optionen.Teile(null));
        Assert.Throws<ArgumentException>(() => Optionen.Teile("1,0"));
        Assert.Throws<ArgumentException>(() => Optionen.Teile("a"));
        Assert.Throws<ArgumentException>(() => Optionen.Lesen(["fakten", "--name"]));
        Assert.Throws<ArgumentException>(() => Optionen.Lesen(["fakten", "frei"]));
        Assert.Throws<ArgumentException>(() => Optionen.Lesen(["fakten"]).Pflicht("out"));
        Assert.Throws<ArgumentException>(() => Optionen.Lesen(["fakten", "--wartezeit", "-1"]).Zahl("wartezeit", 5));
        Assert.True(Optionen.Lesen(["--help"]).Hilfe);
    }

    [Theory]
    [InlineData("\r", "")]
    [InlineData("Testtext\r", "Testtext")]
    [InlineData("Testtext\r\n\r", "Testtext")]
    [InlineData("Test\rtext", "Test\rtext")]
    [InlineData("Testtext ", "Testtext ")]
    public void OnlyTrailingParagraphMarksAreIgnored(string inhalt, string erwartet) =>
        Assert.Equal(erwartet, Logik.OhneAbschlussUmbruch(inhalt));

    [Theory]
    [InlineData("", true)]
    [InlineData("\r", true)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    [InlineData("\r\n\r\n", false)]
    [InlineData("\r\r", false)]
    [InlineData(" ", false)]
    [InlineData("\u200B", false)]
    [InlineData("a", false)]
    [InlineData(null, false)]
    public void OnlyAnEmptyFieldOrASingleParagraphMarkHasNoPriorContent(string? roh, bool ohne) =>
        Assert.Equal(ohne, Logik.OhneVorinhalt(roh));

    [Fact]
    public void PriorContentForTheDeleteGateIsTheRawContent()
    {
        // Codex zu PR #70: Leerzeilen des Nutzers sind Vorinhalt, auch wenn sie nur aus Absatzzeichen bestehen
        Assert.Equal("\r\n\r\n", Logik.Vorinhalt("\r\n\r\n", "\r\n\r\nTesttext"));
        Assert.Equal(" ", Logik.Vorinhalt(" ", " Testtext"));
        Assert.Equal("ab", Logik.Vorinhalt("ab", "abTesttext"));

        // Ein einzelnes Absatzzeichen ist Vorinhalt nur, wenn es nach dem Schreiben vor dem Text steht; am Ende (Word, Outlook)
        // oder verschwunden (Web-Editor, der leer einen Absatz meldet) ist es Aufbau des Felds
        Assert.Null(Logik.Vorinhalt("\r", "Testtext\r"));
        Assert.Null(Logik.Vorinhalt("\r\n", "Testtext\r\n"));
        Assert.Null(Logik.Vorinhalt("\n", "Testtext"));
        Assert.Equal("\r\n", Logik.Vorinhalt("\r\n", "\r\nTesttext"));
        Assert.Equal("\n", Logik.Vorinhalt("\n", "\n"));

        // Leer oder nicht lesbar: kein Vorinhalt
        Assert.Null(Logik.Vorinhalt(string.Empty, "Testtext"));
        Assert.Null(Logik.Vorinhalt(null, null));
    }

    [Fact]
    public void EmptyMeansOnlyWhitespaceNoBreakSpaceOrZeroWidth()
    {
        Assert.True(Logik.IstLeer(string.Empty));
        Assert.True(Logik.IstLeer(" " + (char)0x00A0 + (char)0x200B + (char)0xFEFF));
        Assert.False(Logik.IstLeer("a"));
        Assert.False(Logik.IstLeer(" " + (char)0x200B + "x"));
    }
}
