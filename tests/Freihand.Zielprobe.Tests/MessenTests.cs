namespace Freihand.Zielprobe.Tests;

/// <summary>„Feld messen“ (21.11.2026): Der Befehl messen ruft fakten und schreiben mit festen Werten auf.</summary>
public sealed class MessenTests
{
    [Fact]
    public void FactsThenFixedSizesAndOptionalPlaceholder()
    {
        var fakten = Optionen.Lesen(Messen.FaktenArgs(10, "x.json"));
        Assert.Equal("fakten", fakten.Befehl);
        Assert.Equal(10, fakten.Zahl("wartezeit", 0));
        Assert.Equal("x.json", fakten.Pflicht("out"));

        // Handtest vom 21.11.2026: je Grösse ein eigener Durchgang, von oben eingegrenzt (MessenSucheTests)
        // Wunsch vom 21.11.2026: auch zwischen 1 und 20 fein abgestuft, bis zur Obergrenze 50; jede Grösse muss dreimal gelingen
        Assert.Equal([1, 2, 3, 5, 7, 10, 15, 20, 30, 50], Messen.Groessen);
        Assert.Equal(3, Messen.Bestaetigungen);
        Assert.StartsWith("modus = erkennen\n", Messen.Zusammenfassung("[]", modus: "erkennen"), StringComparison.Ordinal);
        var schreiben = Optionen.Lesen(Messen.SchreibenArgs("x.json", teil: 5));
        Assert.Equal("schreiben", schreiben.Befehl);
        Assert.Equal([5], Optionen.Teile(schreiben.Text("teile")));
        Assert.Equal(250, schreiben.Zahl("laenge", 0));
        Assert.Equal(10, schreiben.Zahl("pause", 0));
        // Messregel (Codex zu PR #70): messen schreibt nur in ein wirklich leeres Feld, nie mit vermutetem Platzhalter
        Assert.False(schreiben.Platzhalter);

        var messen = Optionen.Lesen(["messen", "--wartezeit", "10", "--out", "x.json", "--platzhalter"]);
        Assert.Equal("messen", messen.Befehl);
        Assert.True(messen.Platzhalter);
    }
}

/// <summary>Die Zusammenfassung für Freihand entsteht aus denselben Einträgen, die fakten und schreiben anhängen.</summary>
public sealed class MessenZusammenfassungTests : IDisposable
{
    private readonly string _pfad = Path.Combine(Path.GetTempPath(), "freihand-messen-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() => File.Delete(_pfad);

    // Codex zu PR #70: Ein Durchgang, der wegen eines anderen Felds im Fokus abbrach, ist eine Abweichung, auch ohne
    // andere Merkmale
    [Fact]
    public void AnAbortForAnotherFocusedFieldIsADeviation()
    {
        Logik.Anhängen(_pfad, new FaktenEintrag("t", "Feld messen", "notepad", "Document", "Edit", "Win32", false, "", true, false, false, true,
            true, false, true, "Bindable", "unbekanntes Feld (Standardregel)", 1));
        Logik.Anhängen(_pfad, new DurchgangEintrag("t", "Feld messen", false, 50, 10, "notepad", "Document", "Edit", "Win32", null, null,
            0, 0, 0, true, null, 2000, null, null, null, null, null, Schreiben.AnderesFeld));

        Assert.Contains("abweichung = ja", Messen.Zusammenfassung(File.ReadAllText(_pfad)), StringComparison.Ordinal);
    }

    [Fact]
    public void FactsAndPassesBecomeKeyValueLines()
    {
        Logik.Anhängen(_pfad, new FaktenEintrag("t", "Feld messen", "ms-teams", "Edit", "", "Chrome", false, "", true, false, false, true,
            true, false, true, "Bindable", "unbekanntes Feld (Standardregel)", 1));
        Logik.Anhängen(_pfad, new DurchgangEintrag("t", "Feld messen", false, 20, 10, "ms-teams", "Edit", "", "Chrome", "Written", "None",
            250, 0, 900, true, true, 250, 250, null, null, true, true, null));
        Logik.Anhängen(_pfad, new DurchgangEintrag("t", "Feld messen", false, 50, 10, "ms-teams", "Edit", "", "Chrome", "Written", "None",
            250, 0, 400, true, false, 250, 249, 7, 1, true, false, "Feld nicht geleert, bitte von Hand leeren"));

        Assert.Equal(
            "prozess = ms-teams\nsteuerelement = Edit\nklasse = \noberflaeche = Chrome\nmehrzeilig = ja\nlesbar = ja\nentscheid = Bindable\n" +
            "grund = unbekanntes Feld (Standardregel)\ndurchgang = 20;ja;ja;-\ndurchgang = 50;nein;ja;Feld nicht geleert, bitte von Hand leeren\n",
            Messen.Zusammenfassung(File.ReadAllText(_pfad)));
        Assert.Equal(string.Empty + "\n", Messen.Zusammenfassung("[]"));
        Assert.True(Messen.Lesbar(File.ReadAllText(_pfad)));

        // Der letzte Durchgang (50) kam nicht zeichengetreu an und brach ab: keine grössere Stückgrösse mehr
        Assert.False(Messen.LetzterGelungen(File.ReadAllText(_pfad)));
        Logik.Anhängen(_pfad, new DurchgangEintrag("t", "Feld messen", false, 100, 10, "ms-teams", "Edit", "", "Chrome", "Written", "None",
            250, 0, 300, true, true, 250, 250, null, null, true, true, null));
        Assert.True(Messen.LetzterGelungen(File.ReadAllText(_pfad)));
        Assert.False(Messen.LetzterGelungen("[]"));
    }

    // Eigene Vorprüfung: nicht lesbar → nichts schreiben; anderes Feld im Durchgang → Abweichung; Fehler → genannt
    [Fact]
    public void UnreadableFieldsAreNotWrittenAndAFocusChangeIsReported()
    {
        Logik.Anhängen(_pfad, new FaktenEintrag("t", "Feld messen", "ms-teams", "Edit", "", "Chrome", false, "", true, false, false, true,
            true, false, false, "Bindable", "unbekanntes Feld (Standardregel)", 1));
        Assert.False(Messen.Lesbar(File.ReadAllText(_pfad)));
        Assert.Contains("\nlesbar = nein\n", Messen.Zusammenfassung(File.ReadAllText(_pfad)), StringComparison.Ordinal);
        Assert.False(Messen.Lesbar("[]"));

        Logik.Anhängen(_pfad, new DurchgangEintrag("t", "Feld messen", false, 20, 10, "WINWORD", "Document", "_WwG", "Win32", "Written", "None",
            250, 0, 900, true, true, 250, 250, null, null, true, true, null));
        var zusammenfassung = Messen.Zusammenfassung(File.ReadAllText(_pfad), "InvalidDataException");
        Assert.EndsWith("\nabweichung = ja\nfehler = InvalidDataException\n", zusammenfassung, StringComparison.Ordinal);
    }
}

/// <summary>
/// Bedienregel, 21.11.2026: von oben eingrenzen (50, 20, 10, dazwischen die Mitte) statt von unten hochzählen; nur die
/// gefundene Grösse wird wiederholt. Hier mit einem gedachten Feld, das bis zu einer Grenze fehlerfrei schreibt.
/// </summary>
public sealed class MessenSucheTests
{
    private static async Task<(int? Ergebnis, List<int> Versuche)> Suchen(Func<int, int, bool?> feld)
    {
        var versuche = new List<int>();
        var ergebnis = await Messen.SuchenAsync(teil =>
        {
            versuche.Add(teil);
            return Task.FromResult(feld(teil, versuche.Count));
        });
        return (ergebnis is { } index && index >= 0 ? Messen.Groessen[index] : ergebnis, versuche);
    }

    [Fact]
    public async Task AFieldThatTakesFiftyNeedsOnlyThreePasses()
    {
        var (ergebnis, versuche) = await Suchen((teil, _) => true);

        Assert.Equal(50, ergebnis);
        Assert.Equal([50, 50, 50], versuche);
    }

    [Fact]
    public async Task TheLimitIsNarrowedFromAboveAndConfirmed()
    {
        var (ergebnis, versuche) = await Suchen((teil, _) => teil <= 15);

        Assert.Equal(15, ergebnis);
        Assert.Equal([50, 20, 10, 15, 15, 15], versuche);
    }

    [Fact]
    public async Task OnlySingleCharactersAndNothingAtAll()
    {
        var (eins, versuche) = await Suchen((teil, _) => teil == 1);
        Assert.Equal(1, eins);
        Assert.Equal([50, 20, 10, 5, 2, 1, 1, 1], versuche);

        var (nichts, _) = await Suchen((_, _) => false);
        Assert.Equal(-1, nichts);
    }

    // Scheitert eine Wiederholung, gilt die Grösse als gescheitert und die Suche geht darunter weiter
    [Fact]
    public async Task AFailedRepetitionSearchesBelow()
    {
        var (ergebnis, versuche) = await Suchen((teil, nummer) => teil <= 15 && !(teil == 15 && nummer == 5));

        Assert.Equal(10, ergebnis);
        Assert.Equal([50, 20, 10, 15, 15, 7, 10, 10, 10], versuche);
    }

    [Fact]
    public async Task AnAbortStopsAtOnce()
    {
        var (ergebnis, versuche) = await Suchen((teil, _) => teil == 20 ? null : false);

        Assert.Null(ergebnis);
        Assert.Equal([50, 20], versuche);
    }
}

/// <summary>
/// Handtest vom 21.11.2026: Der Editor verdoppelte Zeichen; das Feld blieb gefüllt und die Suche brach ab. Im Messmodus darf
/// ein vorher nachweislich leeres, noch fokussiertes, gleiches Feld geleert werden, solange nur Zeichen des Testtexts darin stehen.
/// </summary>
public sealed class MessmodusLeerenTests
{
    private const string Testtext = "Grüsse – die Offerte über 1250 CHF liegt bei. Bitte prüfen Sie den Entwurf und die Notiz.";

    [Fact]
    public void OnlyTheExactTestTextIsClearedNeverDoubledCharacters()
    {
        // Sicherheitsprüfung, F2: gleiche Zeichen oder eine Teilfolge reichen nicht; nur der vollständige Prüftext
        Assert.True(Logik.DarfLeerenImMessmodus(Testtext, Testtext, true, true, null));
        Assert.False(Logik.DarfLeerenImMessmodus("Grüsse – die Offerte über 1250 CHF liegt bei. Bitte prüfen nnnnnnnote\r\nuuuuuu", Testtext, true, true, null));
        Assert.False(Logik.DarfLeeren("Grüsse – die Offerte über 1250 CHF liegt bei. Bitte prüfen nnnnnnnote\r\nuuuuuu", Testtext, true));
    }

    [Fact]
    public void ForeignCharactersOtherElementsLostFocusOrThePlaceholderAreNeverCleared()
    {
        Assert.False(Logik.DarfLeerenImMessmodus("Grüsse xyz", Testtext, true, true, null));
        Assert.False(Logik.DarfLeerenImMessmodus("Grüsse", Testtext, true, false, null));
        Assert.False(Logik.DarfLeerenImMessmodus("Grüsse", Testtext, true, null, null));
        Assert.False(Logik.DarfLeerenImMessmodus("Grüsse", Testtext, false, true, null));
        Assert.False(Logik.DarfLeerenImMessmodus("Bitte", Testtext, true, true, "Bitte"));
        Assert.False(Logik.DarfLeerenImMessmodus(string.Empty, Testtext, true, true, null));
    }

    [Fact]
    public void OnlyFeldMessenUsesTheMeasureMode()
    {
        Assert.True(Optionen.Lesen(Messen.SchreibenArgs("x.json", teil: 50)).Messmodus);

        // Codex zu PR #70: Alle Durchgänge sind an das Feld vom Countdown gebunden
        Assert.Equal("42.7.1", Optionen.Lesen(Messen.SchreibenArgs("x.json", teil: 50, anker: "42.7.1")).Anker);
        Assert.Null(Optionen.Lesen(Messen.SchreibenArgs("x.json", teil: 50)).Anker);

        // Codex zu PR #70 (Vorinhalt „ab“, Ziel verschluckt den Anfang von „abc“): Im Messmodus gilt ein Vorinhalt nie als
        // Platzhalter; das Feld ist damit nicht leer, und der Durchgang bricht ab, bevor er tippt. Leeren nach einem Durchgang
        // sieht so nur Felder, die vorher nachweislich leer waren
        Assert.Null(Logik.Platzhalterinhalt("ab", platzhalter: true, messmodus: true));
        Assert.Null(Logik.Platzhalterinhalt("Nachricht eingeben", platzhalter: true, messmodus: true));
        Assert.Equal("ab", Logik.Platzhalterinhalt("ab", platzhalter: true, messmodus: false));
        Assert.Null(Logik.Platzhalterinhalt("ab", platzhalter: false, messmodus: false));
        Assert.Null(Logik.Platzhalterinhalt(new string('x', 41), platzhalter: true, messmodus: false));
        Assert.Null(Logik.Platzhalterinhalt(string.Empty, platzhalter: true, messmodus: false));

        // Bedienregel, 21.11.2026: im Messmodus gilt nur ein nachgewiesener Hinweistext, und nur solange genau er dasteht
        Assert.Equal("Nachricht eingeben", Logik.Platzhalterinhalt("Nachricht eingeben", platzhalter: false, messmodus: true, nachgewiesen: "Nachricht eingeben"));
        Assert.Null(Logik.Platzhalterinhalt("Nachricht eingeben x", platzhalter: true, messmodus: true, nachgewiesen: "Nachricht eingeben"));
        Assert.Null(Logik.Platzhalterinhalt("ab", platzhalter: true, messmodus: true, nachgewiesen: string.Empty));
    }

    // Bedienregel, 21.11.2026: Ein grauer Hinweistext verschwindet beim ersten getippten Zeichen, echter Text bleibt
    [Fact]
    public void OneCharacterTellsAHintFromRealText()
    {
        Assert.Equal(Hinweisprobe.Hinweistext, Logik.HinweisPruefen("Nachricht eingeben", "G", 'G'));
        Assert.Equal(Hinweisprobe.Eingefuegt, Logik.HinweisPruefen("Gruss Nilo", "Gruss NiloG", 'G'));
        Assert.Equal(Hinweisprobe.Eingefuegt, Logik.HinweisPruefen("ab", "Gab", 'G'));
        Assert.Equal(Hinweisprobe.Eingefuegt, Logik.HinweisPruefen("ab", "aGb", 'G'));

        // Eigene Vorprüfung zu PR #70: Rohinhalt; ein Absatzzeichen hinter dem Prüfzeichen ist Aufbau (Word), Leerraum Vorinhalt
        Assert.Equal(Hinweisprobe.Hinweistext, Logik.HinweisPruefen("Nachricht eingeben\r", "G\r", 'G'));
        Assert.Equal(Hinweisprobe.Hinweistext, Logik.HinweisPruefen("\u200B", "G", 'G'));
        Assert.Equal(Hinweisprobe.Unklar, Logik.HinweisPruefen("Nachricht eingeben", "G\r\n\r\n", 'G'));
        Assert.Equal(Hinweisprobe.Eingefuegt, Logik.HinweisPruefen("\r\n\r\n", "\r\n\r\nG", 'G'));
        Assert.Equal(Hinweisprobe.Eingefuegt, Logik.HinweisPruefen(" ", " G", 'G'));
        Assert.Equal(Hinweisprobe.NichtAngekommen, Logik.HinweisPruefen("ab", "ab\r", 'G'));

        // Codex zu PR #70: Der nachgewiesene Hinweistext gilt nur bei genau gleichem Rohinhalt; Absätze dahinter sind neu
        Assert.Equal("Nachricht\r", Logik.Platzhalterinhalt("Nachricht\r", false, messmodus: true, nachgewiesen: "Nachricht\r"));
        Assert.Null(Logik.Platzhalterinhalt("G\r\n\r\n", false, messmodus: true, nachgewiesen: "G"));
        Assert.Equal("Nachricht", Logik.Platzhalterinhalt("Nachricht", false, messmodus: true, nachgewiesen: "Nachricht\r"));
        Assert.Equal("Nachricht\r\n", Logik.Platzhalterinhalt("Nachricht\r\n", false, messmodus: true, nachgewiesen: "Nachricht"));
        Assert.Null(Logik.Platzhalterinhalt("Nachricht ", false, messmodus: true, nachgewiesen: "Nachricht"));

        // Ausgangszustand (Codex zu PR #70): dieselbe Regel vor dem Schreiben, für „nichts angekommen“ und für „geleert“
        Assert.True(Logik.Ausgangszustand(string.Empty, null));
        Assert.True(Logik.Ausgangszustand("\r", null));
        Assert.True(Logik.Ausgangszustand("Hinweis\r", "Hinweis"));
        Assert.False(Logik.Ausgangszustand("\r\n\r\n", null));
        Assert.False(Logik.Ausgangszustand(" ", null));
        Assert.False(Logik.Ausgangszustand("\r\n\r\n", "Hinweis"));
        Assert.False(Logik.Ausgangszustand(null, null));

        // Eine Regel für jeden Vergleich mit dem Hinweistext: höchstens ein Absatzzeichen dahinter
        Assert.True(Logik.GleicherHinweis("Hinweis", "Hinweis"));
        Assert.True(Logik.GleicherHinweis("Hinweis\r", "Hinweis"));
        Assert.True(Logik.GleicherHinweis("Hinweis", "Hinweis\r\n"));
        Assert.False(Logik.GleicherHinweis("Hinweis\r\n\r\n", "Hinweis"));
        Assert.False(Logik.GleicherHinweis("HinweisG", "Hinweis"));
        Assert.False(Logik.GleicherHinweis("\r", "\r"));
        Assert.False(Logik.GleicherHinweis(null, "Hinweis"));
        Assert.False(Logik.GleicherHinweis("Hinweis", null));

        // Codex zu PR #70: vorhandenes „ab“, das Ziel verschluckt das Zeichen → unverändert, nichts wird gelöscht
        Assert.Equal(Hinweisprobe.NichtAngekommen, Logik.HinweisPruefen("ab", "ab", 'G'));

        // Alles andere ist unklar und wird nie gelöscht: ersetzt, verändert, doppelt, anderes Zeichen
        Assert.Equal(Hinweisprobe.Unklar, Logik.HinweisPruefen("ab", "aG", 'G'));
        Assert.Equal(Hinweisprobe.Unklar, Logik.HinweisPruefen("ab", "abGG", 'G'));
        Assert.Equal(Hinweisprobe.Unklar, Logik.HinweisPruefen("ab", "abX", 'G'));
        Assert.Equal(Hinweisprobe.Unklar, Logik.HinweisPruefen("Nachricht eingeben", "X", 'G'));
        Assert.Equal(Hinweisprobe.Unklar, Logik.HinweisPruefen("Nachricht eingeben", string.Empty, 'G'));
        Assert.Equal(Hinweisprobe.NichtAngekommen, Logik.HinweisPruefen("G", "G", 'G'));

        // Die Zusammenfassung nennt nur die Art, nie den Hinweistext
        Assert.StartsWith("hinweistext = echter_text\n", Messen.Zusammenfassung("[]", hinweis: "echter_text"), StringComparison.Ordinal);
        Assert.StartsWith("hinweistext = ersetzt\npruefzeichen = G\n", Messen.Zusammenfassung("[]", hinweis: "ersetzt", pruefzeichen: 'G'), StringComparison.Ordinal);

        // Codex zu PR #70: Ist die Messdatei selbst unlesbar, gibt es trotzdem eine Zusammenfassung mit dem Fehler
        var kaputt = Messen.ZusammenfassungAusDatei(() => "{kaputt", null, null, "leer", 'G');
        Assert.StartsWith("hinweistext = leer\npruefzeichen = G\n", kaputt, StringComparison.Ordinal);
        Assert.Contains("\nfehler = ", kaputt, StringComparison.Ordinal);
        Assert.EndsWith("fehler = IOException\n", Messen.ZusammenfassungAusDatei(() => throw new IOException(), null, null, null, null), StringComparison.Ordinal);
        Assert.EndsWith("fehler = Fokuswechsel\n", Messen.ZusammenfassungAusDatei(() => throw new IOException(), "Fokuswechsel", null, null, null), StringComparison.Ordinal);

        // Geschrieben wird nur ins Feld vom Countdown, und nur, wenn es lesbar ist
        Assert.Null(Logik.MessmodusAbbruch("42.7.1", "42.7.1", lesbar: true));
        Assert.Equal(Schreiben.AnderesFeld, Logik.MessmodusAbbruch("42.7.1", "42.7.2", lesbar: true));
        Assert.Equal(Schreiben.AnderesFeld, Logik.MessmodusAbbruch("42.7.1", null, lesbar: true));
        Assert.Equal("Feld ohne RuntimeId", Logik.MessmodusAbbruch(null, "42.7.1", lesbar: true));
        Assert.Equal("Feld ohne RuntimeId", Logik.MessmodusAbbruch(string.Empty, string.Empty, lesbar: true));
        Assert.Equal("Feld nicht lesbar", Logik.MessmodusAbbruch("42.7.1", "42.7.1", lesbar: false));
        Assert.False(Optionen.Lesen(["schreiben", "--name", "x", "--out", "x.json"]).Messmodus);
    }
}
