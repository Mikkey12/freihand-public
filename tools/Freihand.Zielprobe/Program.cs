using Freihand.Zielprobe;

// Messwerkzeug für WM03/WM04 und WM05. Tippt in das fokussierte Feld eines fremden Fensters.
Console.OutputEncoding = System.Text.Encoding.UTF8;
try
{
    var optionen = Optionen.Lesen(args);
    if (optionen.Hilfe || optionen.Befehl is null or "help")
    {
        Hilfe();
        return optionen.Hilfe || optionen.Befehl == "help" ? 0 : 1;
    }

    switch (optionen.Befehl)
    {
        case "fakten":
            return Fakten.Ausfuehren(optionen);
        case "schreiben":
            return await Schreiben.AusfuehrenAsync(optionen).ConfigureAwait(false);
        case "messen":
            return await Messen.AusfuehrenAsync(optionen).ConfigureAwait(false);
        case "umbruch":
            return await Umbruch.AusfuehrenAsync(optionen).ConfigureAwait(false);
        default:
            Console.Error.WriteLine($"Unbekannter Befehl „{optionen.Befehl}“.");
            Hilfe();
            return 1;
    }
}
catch (Exception ausnahme) when (ausnahme is ArgumentException or InvalidDataException or IOException or InvalidOperationException)
{
    Console.Error.WriteLine($"Fehler: {ausnahme.Message}");
    return 1;
}

static void Hilfe() => Console.WriteLine("""
    Freihand.Zielprobe: Messwerkzeug für die Zielprogramm-Messungen WM03, WM04 und WM05.

    ACHTUNG: Der Befehl "schreiben" tippt den Testtext in das fokussierte Feld eines fremden Fensters und leert es danach
    mit Strg+A und Entf. Nur in ein leeres Testfeld verwenden, das nichts Wichtiges enthält.

    Befehle:
      fakten    Merkmale des fokussierten Felds lesen (wie Freihand sie liest) und den Entscheid der Zieltabelle festhalten.
        --wartezeit S   Sekunden bis zum Lesen, mit Piepton je Sekunde (Standard 5)
        --name TEXT     frei gewählte Bezeichnung des Felds, z. B. "Outlook Betreff" (Pflicht)
        --out PFAD      JSON-Datei; jeder Aufruf hängt einen Eintrag an die Liste an (Pflicht)

      schreiben Testtext (AU-01, 2000 Zeichen, Umbrüche als Leerzeichen) in Teilen schreiben, zurücklesen und vergleichen.
        --wartezeit S   Sekunden bis zum Start (Standard 5)
        --name TEXT     Bezeichnung des Felds (Pflicht)
        --teile LISTE   Teilgrössen in Zeichen, je ein Durchgang, z. B. 1,4,10,20 (Standard)
        --laenge N      nur die ersten N Zeichen des Testtexts, z. B. 250 für Felder mit Längengrenze (Standard 2000)
        --platzhalter   ein kurzer Inhalt vor dem Schreiben (höchstens 40 Zeichen) ist der Platzhalter des Felds, z. B. in Teams
        --pause MS      Pause zwischen den Teilen in Millisekunden (Standard 10)
        --out PFAD      JSON-Datei; jeder Durchgang hängt einen Eintrag an die Liste an (Pflicht)
        --einzeilig     nur ein Vermerk im Ergebnis (Umbrüche werden immer durch Leerzeichen ersetzt)

      messen    Für „Feld messen“ in Freihand: fakten, dann schreiben (250 Zeichen) von oben eingegrenzt: 50, bei Fehler 20, 10,
                5, 2, 1, nach einem Erfolg die Mitte; die gefundene Grösse zweimal wiederholt; nur ins Feld vom Countdown;
                am Ende drei Töne.
        --wartezeit S   Sekunden bis zum Start (Standard 10)
        --platzhalter   steht etwas im Feld, zuerst mit einem Zeichen prüfen, ob es ein grauer Hinweistext ist; in echtem
                        Text löscht die Prüfung nie, das Zeichen bleibt stehen und wird genannt
        --out PFAD      JSON-Datei (Pflicht)

      umbruch   WM05: kurzen erfundenen Text mit zwei Umbrüchen und einer Leerzeile schreiben, zurücklesen, einordnen
                (Umbruch, OhneUmbruch, Fehlt, Abweichend). ACHTUNG: In Chat- und Formularfeldern kann ein Umbruch senden oder
                abschicken; nur in Testfeldern ohne Empfänger verwenden.
        --wartezeit S   Sekunden bis zum Start (Standard 5)
        --name TEXT     Bezeichnung des Felds (Pflicht)
        --modus M       unicode (U+000D, keine echte Taste; Standard) oder umschalt-enter (echte Tasten mit Neutralize)
        --teil N        Teilgrösse in Zeichen (Standard 1)
        --durchgaenge N Zahl der Durchgänge (Standard 2)
        --out PFAD      JSON-Datei; jeder Durchgang hängt einen Eintrag an die Liste an (Pflicht)

    Das Feld muss vor jedem Durchgang leer sein; bei messen zählen auch Leerzeilen und Leerzeichen als Inhalt (nur ein
    einzelnes Absatzzeichen wie in Word gilt als leer). Nach jedem Durchgang wird es geleert, aber nur, wenn vorher nichts
    darin stand (oder nur ein nachgewiesener Hinweistext) und sein Inhalt genau der geschriebene Text ist, höchstens mit
    verlorenen Zeichen; bei messen auch, wenn nur Zeichen des Testtexts darin stehen. Unmittelbar vor dem Löschen wird
    nochmals gelesen; hat sich der Inhalt geändert, wird nichts gelöscht. Geleert ist ein Feld nur, wenn es danach wieder
    leer ist (oder den nachgewiesenen Hinweistext zeigt). messen schreibt nicht in Felder, die
    sich nicht lesen lassen. Lässt sich ein Feld bei den übrigen Befehlen nicht per UI Automation lesen, wird
    trotzdem einmal geschrieben; bitte dann von Auge prüfen und von Hand leeren.

    Es wird nie Feldinhalt gespeichert oder ausgegeben, nur Längen, Index der ersten Abweichung, Zahl abweichender Zeichen.
    Die Zwischenablage wird nicht benutzt.
    """);
