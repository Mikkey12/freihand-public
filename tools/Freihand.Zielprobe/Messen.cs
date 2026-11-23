using System.Diagnostics;
using System.Text.Json.Nodes;
using Freihand.Windows.Targets;

namespace Freihand.Zielprobe;

/// <summary>
/// Befehl <c>messen</c> für „Feld messen“ in Freihand (21.11.2026): zuerst die Merkmale des fokussierten Felds, dann der
/// Testtext in Stückgrössen von oben eingegrenzt (<see cref="SuchenAsync"/>), je mit Zurücklesen und Leeren; am Schluss drei
/// Töne. Alle Durchgänge schreiben nur in das Feld, das beim Countdown erkannt wurde (RuntimeId als Anker, Codex zu PR #70).
/// Dieselben Befehle wie bei den Messungen WM03/WM04 und Hermes; Freihand wertet die Zusammenfassung daneben aus (kein JSON
/// in Freihand). Messregel (Codex zu PR #70, zweite Prüfung): Eine Messung wirkt nur auf Grund nachgewiesener Tatsachen am selben Element. Was nicht nachgewiesen ist (ein anderes Element, ein Vorinhalt, ein abgebrochener Durchgang, ein unbekanntes Merkmal), führt zum Abbruch ohne Wirkung: nichts schreiben, nichts löschen, nichts speichern, nichts auf andere Felder übertragen.
/// Darum misst „messen“ nur in ein leeres Feld oder, mit „--platzhalter“, in ein Feld mit nachgewiesenem grauem Hinweistext
/// (<see cref="Schreiben.HinweisAsync"/>: er verschwindet beim ersten getippten Zeichen, echter Text bleibt).
/// </summary>
internal static class Messen
{
    /// <summary>
    /// Mögliche Stückgrössen in Zeichen bis zur Obergrenze von Freihand (50); grösser wird nie geschrieben. Gesucht wird von
    /// oben (<see cref="Naechste"/>), damit ein Feld, das 50 verträgt, nur kurz beschrieben wird.
    /// </summary>
    public static IReadOnlyList<int> Groessen { get; } = [1, 2, 3, 5, 7, 10, 15, 20, 30, 50];

    /// <summary>So oft muss eine Stückgrösse insgesamt fehlerfrei durchgehen, damit sie gilt.</summary>
    public const int Bestaetigungen = 3;

    /// <summary>Länge des Testtexts: passt auch in Felder mit 255 Zeichen Grenze (Outlook-Betreff).</summary>
    public const int Laenge = 250;

    /// <param name="modus">„erkennen“ reicht der Befehl weiter (Sicherheitsprüfung, F4): dann liest <c>fakten</c> keinen Inhalt.</param>
    public static IReadOnlyList<string> FaktenArgs(int wartezeit, string ziel, string? modus = null)
    {
        List<string> args = ["fakten", "--wartezeit", wartezeit.ToString(System.Globalization.CultureInfo.InvariantCulture), "--name", "Feld messen", "--out", ziel];
        if (modus == "erkennen")
        {
            args.Add("--modus");
            args.Add("erkennen");
        }

        return args;
    }

    /// <summary>Datei neben der Messdatei, solange das Werkzeug wartet, bis der Nutzer das Testfeld leert; Freihand zeigt dann einen Hinweis.</summary>
    public static string PausePfad(string ziel) => Path.ChangeExtension(ziel, ".pause");

    /// <summary>Durchgänge (Nummer ab 0), nach denen der Nutzer das Testfeld von Hand geleert hat; nur im Prozess.</summary>
    private static readonly HashSet<int> VonHandGeleert = [];

    public static IReadOnlyList<string> SchreibenArgs(string ziel, int teil, string? anker = null)
    {
        List<string> args = ["schreiben", "--wartezeit", "1", "--name", "Feld messen", "--teile",
            teil.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--laenge", Laenge.ToString(System.Globalization.CultureInfo.InvariantCulture), "--pause", "10", "--out", ziel, "--messmodus"];
        if (anker is not null)
        {
            args.Add("--anker");
            args.Add(anker);
        }

        return args;
    }

    public static async Task<int> AusfuehrenAsync(Optionen optionen)
    {
        var ziel = optionen.Pflicht("out");
        var wartezeit = optionen.Zahl("wartezeit", 10);
        var frist = TimeSpan.FromSeconds(optionen.Zahl("leeren-frist", 90, minimum: 1));
        VonHandGeleert.Clear();
        var ergebnis = 1;
        string? fehler = null;
        string? hinweis = null;
        try
        {
            ergebnis = Fakten.Ausfuehren(Optionen.Lesen(FaktenArgs(wartezeit, ziel, optionen.Text("modus"))));
            if (Fakten.Fokuswechsel)
            {
                fehler = "Fokuswechsel";
            }

            // Regel (eigene Vorprüfung): Lässt Windows den Inhalt nicht lesen, ist weder „leer“ noch „richtig angekommen“
            // prüfbar, und leeren ginge auch nicht; dann wird nichts geschrieben
            // „--modus erkennen“: nur die Merkmale, für einen Wert von Hand (Bedienregel vom 21.11.2026)
            if (ergebnis == 0 && optionen.Text("modus") != "erkennen" && Lesbar(await LesenAsync(ziel).ConfigureAwait(false)))
            {
                string? hinweistext = null;
                if (optionen.Platzhalter)
                {
                    (hinweistext, hinweis) = await Schreiben.HinweisAsync(Fakten.LetzteRuntimeId).ConfigureAwait(false);
                }

                ergebnis = hinweis is null or "ja" or "leer"
                    ? await StufenAsync(ziel, Fakten.LetzteRuntimeId, hinweistext, frist).ConfigureAwait(false)
                    : 1;
            }
        }
        catch (Exception ausnahme) when (ausnahme is not OutOfMemoryException)
        {
            // Auch dann eine Zusammenfassung, damit Freihand den Grund nennt statt „kein Feld“ (auch COM- oder JSON-Fehler)
            fehler = ausnahme.GetType().Name;
        }

        // Freihand liest kein JSON (nur an geprüften Stellen): Zusammenfassung „schlüssel = wert“ daneben
        var zeichen = hinweis is null ? (char?)null : Schreiben.Pruefzeichen;
        await File.WriteAllTextAsync(ZusammenfassungPfad(ziel),
            ZusammenfassungAusDatei(() => File.Exists(ziel) ? File.ReadAllText(ziel) : "[]", fehler, optionen.Text("modus"), hinweis, zeichen, VonHandGeleert),
            new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
        Fertig();
        return ergebnis;
    }

    /// <summary>
    /// Regel (Bedienregel, 21.11.2026): von oben eingrenzen statt von unten hochzählen. Zuerst 50; scheitert eine Grösse
    /// und ist noch keine gelungen, als Nächstes die grösste Stufe bis zur Hälfte (50 → 20 → 10 → 5 → 2 → 1). Ist eine
    /// gelungen, die Stufe in der Mitte zwischen der grössten gelungenen und der kleinsten gescheiterten (10 gelungen,
    /// 20 gescheitert → 15). Liegt keine Stufe mehr dazwischen, ist die Grenze gefunden.
    /// </summary>
    /// <param name="gelungen">Index der grössten gelungenen Stufe, -1 ohne.</param>
    /// <param name="gescheitert">Index der kleinsten gescheiterten Stufe, <c>Groessen.Count</c> ohne.</param>
    /// <returns>Index der nächsten Stufe; null, wenn die Grenze gefunden ist.</returns>
    public static int? Naechste(int gelungen, int gescheitert)
    {
        if (gelungen < 0 && gescheitert >= Groessen.Count)
        {
            return Groessen.Count - 1;
        }

        if (gelungen < 0)
        {
            if (gescheitert == 0)
            {
                return null;
            }

            var haelfte = Math.Max(1, Groessen[gescheitert] / 2);
            var index = gescheitert - 1;
            while (index > 0 && Groessen[index] > haelfte)
            {
                index--;
            }

            return index;
        }

        var mitte = (gelungen + gescheitert) / 2;
        return mitte == gelungen ? null : mitte;
    }

    /// <summary>
    /// Sucht die Grenze nach <see cref="Naechste"/> und bestätigt sie: Die gefundene Grösse muss insgesamt
    /// <see cref="Bestaetigungen"/>-mal fehlerfrei durchgehen; scheitert eine Wiederholung, gilt sie als gescheitert und die
    /// Suche geht darunter weiter. <paramref name="versuch"/> liefert null bei Abbruch (z. B. Feld nicht geleert).
    /// </summary>
    /// <returns>Index der bestätigten Stufe, -1 ohne; null bei Abbruch.</returns>
    public static async Task<int?> SuchenAsync(Func<int, Task<bool?>> versuch)
    {
        ArgumentNullException.ThrowIfNull(versuch);
        var gelungen = -1;
        var gescheitert = Groessen.Count;
        while (true)
        {
            while (Naechste(gelungen, gescheitert) is { } index)
            {
                switch (await versuch(Groessen[index]).ConfigureAwait(false))
                {
                    case null:
                        return null;
                    case true:
                        gelungen = index;
                        break;
                    default:
                        gescheitert = index;
                        break;
                }
            }

            if (gelungen < 0)
            {
                return -1;
            }

            var bestaetigt = true;
            for (var wiederholung = 1; wiederholung < Bestaetigungen && bestaetigt; wiederholung++)
            {
                switch (await versuch(Groessen[gelungen]).ConfigureAwait(false))
                {
                    case null:
                        return null;
                    case false:
                        bestaetigt = false;
                        break;
                }
            }

            if (bestaetigt)
            {
                return gelungen;
            }

            // Die Wiederholung scheiterte: diese Grösse gilt als gescheitert, darunter ist noch nichts gelungen
            gescheitert = gelungen;
            gelungen = -1;
        }
    }

    private static async Task<int> StufenAsync(string ziel, string? anker, string? hinweistext, TimeSpan frist)
    {
        var abbruch = 0;
        await SuchenAsync(async teil =>
        {
            var (ergebnis, ok) = await DurchgangAsync(ziel, teil, anker, hinweistext).ConfigureAwait(false);
            if (ergebnis != 0)
            {
                // Regel (Sicherheitsprüfung, F2): Blieb Prüftext stehen, weil er abwich oder sich nicht sicher leeren liess, leert
                // der Nutzer das Testfeld selbst. Erst wenn dasselbe Feld wieder den Fokus hat und zweimal gleich leer gelesen
                // wird, geht es mit der nächsten kleineren Stufe weiter; diese Stufe zählt als gescheitert, nie als Erfolg
                var json = await LesenAsync(ziel).ConfigureAwait(false);
                if (anker is not null && VonHandZuLeeren(json) && await PauseAsync(ziel, anker, frist).ConfigureAwait(false))
                {
                    VonHandGeleert.Add(Durchgaenge(json) - 1);
                    return false;
                }

                abbruch = ergebnis;
                return null;
            }

            return ok;
        }).ConfigureAwait(false);
        return abbruch;
    }

    /// <summary>Ob der letzte Durchgang Prüftext stehen liess (abweichend oder nicht sicher geleert), am Feld vom Countdown.</summary>
    public static bool VonHandZuLeeren(string json) =>
        (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>().LastOrDefault(eintrag => eintrag.ContainsKey("teilgroesse")) is { } letzter
        && letzter["abbruch"] is JsonValue wert && wert.TryGetValue<string>(out var abbruch)
        && (abbruch.StartsWith(Schreiben.Abweichend, StringComparison.Ordinal) || abbruch.StartsWith(Schreiben.NichtGeleert, StringComparison.Ordinal))
        && letzter["runtimeIdGleich"] is JsonValue gleich && gleich.TryGetValue<bool>(out var gleicheId) && gleicheId;

    private static int Durchgaenge(string json) =>
        (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>().Count(eintrag => eintrag.ContainsKey("teilgroesse"));

    /// <summary>
    /// Wartet, bis der Nutzer das Testfeld geleert hat: dasselbe Element (Anker) hat den Fokus und ist zweimal gleich leer
    /// gelesen, und nach drei Sekunden noch immer (Hände weg von der Tastatur). Ein anderes Feld im Fokus zählt nie, auch ein
    /// leeres. Bis zur Frist nicht geleert: Abbruch, nichts gespeichert.
    /// </summary>
    private static async Task<bool> PauseAsync(string ziel, string anker, TimeSpan frist)
    {
        var datei = PausePfad(ziel);
        await File.WriteAllTextAsync(datei, "leeren").ConfigureAwait(false);
        Toene((440, 300), (330, 500));
        try
        {
            var uia = new UiaAccess();
            var uhr = Stopwatch.StartNew();
            while (uhr.Elapsed < frist)
            {
                await Task.Delay(500).ConfigureAwait(false);
                if (LeerAmAnker(uia, anker))
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    if (LeerAmAnker(uia, anker))
                    {
                        Toene((880, 200));
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            try
            {
                File.Delete(datei);
            }
            catch (Exception ausnahme) when (ausnahme is IOException or UnauthorizedAccessException)
            {
                // Freihand wertet die Datei nur während der Messung aus
            }
        }
    }

    private static bool LeerAmAnker(UiaAccess uia, string anker) =>
        UiaFeld.Fokus(uia) is { } feld && UiaFeld.RuntimeId(uia, feld) == anker
        && Schreiben.LesenStabil(feld) is { } inhalt && Logik.OhneVorinhalt(inhalt)
        && UiaFeld.Fokus(uia) is { } noch && UiaFeld.RuntimeId(uia, noch) == anker;

    private static async Task<(int Ergebnis, bool Ok)> DurchgangAsync(string ziel, int teil, string? anker, string? hinweistext)
    {
        var ergebnis = await Schreiben.AusfuehrenAsync(Optionen.Lesen(SchreibenArgs(ziel, teil, anker)), hinweistext).ConfigureAwait(false);
        return (ergebnis, ergebnis == 0 && LetzterGelungen(await LesenAsync(ziel).ConfigureAwait(false)));
    }

    public static string ZusammenfassungPfad(string ziel) => Path.ChangeExtension(ziel, ".txt");

    private static async Task<string> LesenAsync(string ziel) =>
        File.Exists(ziel) ? await File.ReadAllTextAsync(ziel).ConfigureAwait(false) : "[]";

    /// <summary>Ob der letzte Durchgang zeichengetreu ankam, lesbar war und nicht abbrach; dann folgt die nächste Grösse.</summary>
    public static bool LetzterGelungen(string json) =>
        (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>().LastOrDefault(eintrag => eintrag.ContainsKey("teilgroesse")) is { } letzter
        && letzter["zeichengetreu"] is JsonValue gleich && gleich.TryGetValue<bool>(out var ja) && ja
        && letzter["abbruch"] is null;

    /// <summary>Ob <c>fakten</c> den Inhalt des Felds lesen konnte.</summary>
    public static bool Lesbar(string json) =>
        Merkmale(JsonNode.Parse(json) as JsonArray ?? []) is { } fakten && fakten["inhaltLesbar"] is JsonValue wert
        && wert.TryGetValue<bool>(out var lesbar) && lesbar;

    private static JsonObject? Merkmale(JsonArray liste) =>
        liste.OfType<JsonObject>().FirstOrDefault(eintrag => eintrag.ContainsKey("prozess") && !eintrag.ContainsKey("teilgroesse"));

    /// <summary>
    /// Merkmale des Felds, ob es lesbar ist, und je Durchgang Stückgrösse, zeichengetreu, lesbar, Abbruch; nie Inhalt. Meldet
    /// ein Durchgang andere Merkmale als <c>fakten</c>, eine andere RuntimeId nach dem Schreiben oder einen Abbruch wegen
    /// eines anderen Felds (Fokus gewechselt), steht „abweichung = ja“ darin (Codex zu PR #70).
    /// </summary>
    /// <param name="hinweis">Ergebnis der Hinweisprobe (ja, leer, ersetzt, echter_text, nicht_angekommen,
    /// unklar, anderes_feld, nicht_bindbar, vorher_abgebrochen); nur die Art, nie der Hinweistext selbst.</param>
    /// <param name="pruefzeichen">Das Prüfzeichen (aus dem Testtext, nie Feldinhalt), damit Freihand es nennen kann.</param>
    /// <param name="vonHand">Durchgänge (ab 0), nach denen der Nutzer das Feld geleert hat: Sie erscheinen als gescheitert
    /// ohne Abbruch, die Messung ging kleiner weiter (Sicherheitsprüfung, F2).</param>
    public static string Zusammenfassung(string json, string? fehler = null, string? modus = null, string? hinweis = null, char? pruefzeichen = null, IReadOnlySet<int>? vonHand = null)
    {
        var zeilen = new List<string>();
        if (modus == "erkennen")
        {
            zeilen.Add("modus = erkennen");
        }

        if (hinweis is not null)
        {
            zeilen.Add("hinweistext = " + hinweis);
        }

        if (pruefzeichen is { } zeichen)
        {
            zeilen.Add("pruefzeichen = " + zeichen);
        }

        var liste = JsonNode.Parse(json) as JsonArray ?? [];
        var fakten = Merkmale(liste);
        if (fakten is not null)
        {
            zeilen.Add("prozess = " + Wert(fakten, "prozess"));
            zeilen.Add("steuerelement = " + Wert(fakten, "controlType"));
            zeilen.Add("klasse = " + Wert(fakten, "className"));
            zeilen.Add("oberflaeche = " + Wert(fakten, "frameworkId"));
            zeilen.Add("mehrzeilig = " + JaNein(fakten["mehrzeilig"]));
            zeilen.Add("lesbar = " + JaNein(fakten["inhaltLesbar"]));
            zeilen.Add("entscheid = " + Wert(fakten, "entscheid"));
            zeilen.Add("grund = " + Wert(fakten, "grund"));
        }

        var abweichung = false;
        var nummer = -1;
        foreach (var durchgang in liste.OfType<JsonObject>().Where(eintrag => eintrag.ContainsKey("teilgroesse")))
        {
            var abbruch = Wert(durchgang, "abbruch");
            if (vonHand?.Contains(++nummer) == true)
            {
                zeilen.Add($"durchgang = {durchgang["teilgroesse"]};nein;ja;-");
                continue;
            }

            zeilen.Add($"durchgang = {durchgang["teilgroesse"]};{JaNein(durchgang["zeichengetreu"])};{(durchgang["lesbar"]?.GetValue<bool>() == true ? "ja" : "nein")};{(abbruch.Length == 0 ? "-" : abbruch)}");
            abweichung |= (fakten is not null && Wert(durchgang, "prozess").Length > 0 &&
                (Wert(durchgang, "prozess") != Wert(fakten, "prozess") || Wert(durchgang, "controlType") != Wert(fakten, "controlType") ||
                 Wert(durchgang, "className") != Wert(fakten, "className") || Wert(durchgang, "frameworkId") != Wert(fakten, "frameworkId")))
                || (durchgang["runtimeIdGleich"] is JsonValue gleich && gleich.TryGetValue<bool>(out var gleicheId) && !gleicheId)
                || abbruch.StartsWith(Schreiben.AnderesFeld, StringComparison.Ordinal);
        }

        if (abweichung)
        {
            zeilen.Add("abweichung = ja");
        }

        if (vonHand is { Count: > 0 })
        {
            zeilen.Add("von_hand_geleert = " + vonHand.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (fehler is not null)
        {
            zeilen.Add("fehler = " + fehler);
        }

        return string.Join("\n", zeilen) + "\n";
    }

    /// <summary>
    /// Zusammenfassung aus der Messdatei; ist die Datei selbst nicht lesbar (kein JSON, gesperrt), trotzdem eine Zusammenfassung
    /// mit dem Fehler, damit Freihand den Grund nennt (Codex zu PR #70).
    /// </summary>
    public static string ZusammenfassungAusDatei(Func<string> lesen, string? fehler, string? modus, string? hinweis, char? pruefzeichen, IReadOnlySet<int>? vonHand = null)
    {
        ArgumentNullException.ThrowIfNull(lesen);
        try
        {
            return Zusammenfassung(lesen(), fehler, modus, hinweis, pruefzeichen, vonHand);
        }
        catch (Exception ausnahme) when (ausnahme is not OutOfMemoryException)
        {
            return Zusammenfassung("[]", fehler ?? ausnahme.GetType().Name, modus, hinweis, pruefzeichen);
        }
    }

    private static string Wert(JsonObject eintrag, string name) =>
        (eintrag[name] is JsonValue wert && wert.TryGetValue<string>(out var text) ? text : string.Empty).Replace('\r', ' ').Replace('\n', ' ');

    private static string JaNein(JsonNode? wert) =>
        wert is JsonValue v && v.TryGetValue<bool>(out var ja) ? (ja ? "ja" : "nein") : "-";

    private static void Fertig() => Toene((880, 200), (880, 200), (1320, 400));

    private static void Toene(params (int Hoehe, int Dauer)[] toene)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var (hoehe, dauer) in toene)
        {
            try
            {
                Console.Beep(hoehe, dauer);
            }
            catch (Exception ausnahme) when (ausnahme is PlatformNotSupportedException or IOException)
            {
                // ohne Ton weiter
            }
        }
    }
}
