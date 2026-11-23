using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Freihand.Zielprobe;

/// <summary>Ergebnis eines Textvergleichs; nur Zahlen, nie Inhalt.</summary>
/// <param name="ErsteAbweichung">Index der ersten Abweichung (bei ungleicher Länge mindestens die kürzere Länge); <c>null</c> bei Gleichheit.</param>
/// <param name="AbweichendeZeichen">Positionen mit anderem Zeichen plus Längenunterschied.</param>
internal sealed record Vergleich(bool Gleich, int Erwartet, int Gelesen, int? ErsteAbweichung, int AbweichendeZeichen);

/// <summary>
/// Was das erste Prüfzeichen in einem Feld mit Vorinhalt zeigte („Feld messen“ mit Hinweistext, Bedienregel
/// vom 21.11.2026); nur die Art, nie Inhalt.
/// </summary>
internal enum Hinweisprobe
{
    /// <summary>Der Vorinhalt verschwand, im Feld steht nur das Prüfzeichen: ein grauer Hinweistext.</summary>
    Hinweistext,

    /// <summary>Der Vorinhalt blieb, das Prüfzeichen kam genau einmal dazu: echter Text.</summary>
    Eingefuegt,

    /// <summary>Das Feld ist unverändert: das Prüfzeichen kam nicht an.</summary>
    NichtAngekommen,

    /// <summary>Alles andere; nichts wird gelöscht.</summary>
    Unklar,
}

/// <summary>Ergebnis eines Umbruch-Durchgangs (WM05); nur die Art, nie Inhalt.</summary>
internal enum UmbruchErgebnis
{
    Umbruch,
    OhneUmbruch,
    Fehlt,
    Abweichend,
}

/// <summary>Die reinen Teile des Werkzeugs, ohne UI Automation: gut testbar, ohne fremde Fenster.</summary>
internal static class Logik
{
    private static readonly JsonSerializerOptions JsonOptionen = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Zeilenumbrüche (CRLF, CR, LF) werden je durch ein Leerzeichen ersetzt.</summary>
    public static string UmbrücheErsetzen(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>
    /// Feldinhalt ohne abschliessende Absatzzeichen: Word und der Nachrichtentext von Outlook liefern auch im leeren Dokument
    /// ein Absatzzeichen am Ende. Es ist Aufbau, nicht Inhalt; der Testtext selbst endet nie mit einem Umbruch.
    /// </summary>
    private static readonly char[] Absatzzeichen = [(char)13, (char)10];

    public static string OhneAbschlussUmbruch(string inhalt)
    {
        ArgumentNullException.ThrowIfNull(inhalt);
        return inhalt.TrimEnd(Absatzzeichen);
    }

    /// <summary>
    /// Regel (Codex zu PR #70): Ob vor dem Schreiben etwas im Feld steht, entscheidet der Rohinhalt. Ohne Vorinhalt ist nur ein
    /// Feld ohne Inhalt oder mit genau einem Absatzzeichen, wie Word und Outlook es im leeren Dokument melden. Leerzeilen,
    /// Leerzeichen und Zeichen ohne Breite sind Vorinhalt.
    /// </summary>
    public static bool OhneVorinhalt(string? roh) => roh is "" or "\r" or "\n" or "\r\n";

    /// <summary>
    /// Vorinhalt für die Löschfreigabe (Codex zu PR #70): alles, was vor dem Schreiben im Feld stand, als Rohinhalt. Null nur
    /// bei einem leeren Feld. Ein einzelnes Absatzzeichen ist Vorinhalt nur, wenn es nach dem Schreiben vor dem Text steht:
    /// dann hat es jemand eingegeben. Steht es danach am Ende (Word, Outlook) oder ist es verschwunden (Web-Editor, der leer
    /// einen Absatz meldet), ist es Aufbau des Felds. Bewusste Ausnahme: Eine selbst getippte Leerzeile am Ende eines sonst
    /// leeren Felds sieht genauso aus und geht beim Leeren mit; mehr als ein Absatzzeichen ist immer Vorinhalt.
    /// </summary>
    public static string? Vorinhalt(string? rohDavor, string? rohDanach) =>
        rohDavor is null or "" ? null
        : OhneVorinhalt(rohDavor) && rohDanach?.StartsWith(rohDavor, StringComparison.Ordinal) != true ? null
        : rohDavor;

    /// <summary>Leer heisst: nur Leerraum, geschützte Leerzeichen oder Zeichen ohne Breite (z. B. ein leerer Web-Editor).</summary>
    public static bool IstLeer(string inhalt)
    {
        ArgumentNullException.ThrowIfNull(inhalt);
        return inhalt.All(zeichen => char.IsWhiteSpace(zeichen) || (int)zeichen is 0x200B or 0xFEFF or 0x00A0);
    }

    /// <summary>
    /// WM05: Umbrüche, wie Zielprogramme sie zurückmelden (CRLF, CR, LF, vertikaler Tabulator von Word, Absatz- und
    /// Zeilentrenner), je als ein LF; abschliessende Umbrüche zählen nicht.
    /// </summary>
    public static string UmbrücheVereinheitlichen(string inhalt)
    {
        ArgumentNullException.ThrowIfNull(inhalt);
        var absatz = (char)0x2029;
        var zeile = (char)0x2028;
        return inhalt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(absatz, '\n').Replace(zeile, '\n')
            .Replace('\r', '\n').Replace('\v', '\n')
            .TrimEnd('\n');
    }

    /// <summary>
    /// Was von einem Text mit Umbrüchen angekommen ist (WM05): <see cref="UmbruchErgebnis.Umbruch"/> genau mit allen Umbrüchen,
    /// <see cref="UmbruchErgebnis.OhneUmbruch"/> mit allen Zeichen, aber ohne Umbrüche (weggelassen oder als Leerzeichen),
    /// <see cref="UmbruchErgebnis.Fehlt"/> nichts oder ein Anfang (etwa nach Senden oder Formularabschicken),
    /// sonst <see cref="UmbruchErgebnis.Abweichend"/>.
    /// </summary>
    public static UmbruchErgebnis UmbruchPrüfen(string erwartet, string gelesen)
    {
        ArgumentNullException.ThrowIfNull(erwartet);
        ArgumentNullException.ThrowIfNull(gelesen);
        var inhalt = UmbrücheVereinheitlichen(gelesen);
        if (inhalt == erwartet)
        {
            return UmbruchErgebnis.Umbruch;
        }

        // Regel (Codex zu PR #54): Ohne Umbruch heisst, jedes Zeichen ausser den Umbrüchen ist da, auch jedes Wortleerzeichen;
        // die Umbrüche fehlen oder sind Leerzeichen geworden. Fehlt heisst, angekommen ist nur ein Anfang einer dieser Formen
        var zeilen = erwartet.Split('\n');
        string[] formen = [erwartet, string.Concat(zeilen), string.Join(' ', zeilen)];
        if (inhalt == formen[1] || inhalt == formen[2])
        {
            return UmbruchErgebnis.OhneUmbruch;
        }

        return IstLeer(inhalt) || formen.Any(form => inhalt.Length < form.Length && form.StartsWith(inhalt, StringComparison.Ordinal))
            ? UmbruchErgebnis.Fehlt
            : UmbruchErgebnis.Abweichend;
    }

    /// <summary>Einfacher Vergleich je Position plus Längenunterschied.</summary>
    public static Vergleich Vergleichen(string erwartet, string gelesen)
    {
        ArgumentNullException.ThrowIfNull(erwartet);
        ArgumentNullException.ThrowIfNull(gelesen);
        var gemeinsam = Math.Min(erwartet.Length, gelesen.Length);
        int? erste = null;
        var abweichend = 0;
        for (var i = 0; i < gemeinsam; i++)
        {
            if (erwartet[i] != gelesen[i])
            {
                erste ??= i;
                abweichend++;
            }
        }

        if (erwartet.Length != gelesen.Length)
        {
            erste ??= gemeinsam;
            abweichend += Math.Abs(erwartet.Length - gelesen.Length);
        }

        return new Vergleich(erste is null, erwartet.Length, gelesen.Length, erste, abweichend);
    }

    /// <summary>
    /// Mit --platzhalter gilt bei den Handbefehlen ein kurzer Inhalt (höchstens 40 Zeichen) als Platzhalter, der beim Tippen
    /// verschwindet. Im Messmodus gilt nur ein nachgewiesener Hinweistext (<see cref="HinweisPruefen"/>), und nur, solange
    /// er im Feld steht, verglichen als Rohinhalt nach <see cref="GleicherHinweis"/> (Messregel, Codex zu PR #70); jeder andere
    /// Vorinhalt bricht ab, bevor getippt wird.
    /// </summary>
    public static string? Platzhalterinhalt(string? davor, bool platzhalter, bool messmodus, string? nachgewiesen = null) =>
        messmodus ? (GleicherHinweis(davor, nachgewiesen) ? davor : null)
        : platzhalter && davor is { Length: > 0 and <= 40 } ? davor : null;

    /// <summary>
    /// Ausgangszustand eines Felds vor dem Schreiben, bei messen und den Handbefehlen (Codex zu PR #70): leer nach <see cref="OhneVorinhalt"/> oder genau der nachgewiesene
    /// Hinweistext nach <see cref="GleicherHinweis"/>. Dieselbe Regel vor dem Schreiben, für „nichts angekommen“ und für
    /// „geleert“; zwei verbliebene Absätze sind nicht geleert.
    /// </summary>
    public static bool Ausgangszustand(string? roh, string? hinweis) =>
        roh is not null && (OhneVorinhalt(roh) || GleicherHinweis(roh, hinweis));

    /// <summary>
    /// Eine Regel für jeden Vergleich mit dem nachgewiesenen Hinweistext (Codex zu PR #70): Der Rohinhalt ist der sichtbare
    /// Hinweistext, dahinter höchstens ein Absatzzeichen des Felds, dieselbe Toleranz wie bei <see cref="OhneVorinhalt"/>.
    /// Mehr Absätze oder anderer Text sind neuer Inhalt. Gilt in der Probe, vor dem Schreiben und nach dem Leeren.
    /// </summary>
    public static bool GleicherHinweis(string? roh, string? hinweis)
    {
        if (roh is null || hinweis is null)
        {
            return false;
        }

        var sichtbar = OhneAbschlussUmbruch(hinweis);
        return sichtbar.Length > 0 && roh.StartsWith(sichtbar, StringComparison.Ordinal) && OhneVorinhalt(roh[sichtbar.Length..]);
    }

    /// <summary>
    /// Regel für den Hinweistext (Bedienregel, 21.11.2026): Ein grauer Hinweistext verschwindet vollständig, sobald ein Zeichen
    /// getippt wird; echter Text bleibt stehen. Verglichen wird der Rohinhalt vor und nach genau einem getippten Zeichen
    /// (eigene Vorprüfung zu PR #70); ein einzelnes Absatzzeichen hinter dem Prüfzeichen ist Aufbau des Felds (Word).
    /// </summary>
    public static Hinweisprobe HinweisPruefen(string davor, string danach, char zeichen)
    {
        ArgumentNullException.ThrowIfNull(davor);
        ArgumentNullException.ThrowIfNull(danach);
        var sichtbar = OhneAbschlussUmbruch(danach);
        if (sichtbar.Length == 1 && sichtbar[0] == zeichen && OhneVorinhalt(danach[1..]) && OhneAbschlussUmbruch(davor) != sichtbar)
        {
            return Hinweisprobe.Hinweistext;
        }

        if (sichtbar == OhneAbschlussUmbruch(davor))
        {
            return Hinweisprobe.NichtAngekommen;
        }

        if (danach.Length == davor.Length + 1)
        {
            for (var stelle = 0; stelle < danach.Length; stelle++)
            {
                if (danach[stelle] == zeichen && danach.Remove(stelle, 1) == davor)
                {
                    return Hinweisprobe.Eingefuegt;
                }
            }
        }

        return Hinweisprobe.Unklar;
    }

    /// <summary>
    /// Regel im Messmodus (Codex zu PR #70): Geschrieben wird nur in das Feld, das beim Countdown erkannt wurde (Anker =
    /// seine RuntimeId), und nur, wenn sein Inhalt lesbar ist; sonst Abbruch vor jeder Ausgabe. Ohne RuntimeId lässt sich das
    /// Feld nicht festhalten. Liefert den Abbruchgrund oder null, wenn geschrieben werden darf.
    /// </summary>
    public static string? MessmodusAbbruch(string? anker, string? runtimeId, bool lesbar) =>
        string.IsNullOrEmpty(anker) ? "Feld ohne RuntimeId"
        : runtimeId != anker ? Schreiben.AnderesFeld
        : !lesbar ? "Feld nicht lesbar"
        : null;

    /// <summary>
    /// „Feld messen“ in Freihand. Regel (Sicherheitsprüfung, F2): Gelöscht wird nur der vollständige eigene Prüftext, genau so, wie
    /// er geschrieben wurde (höchstens mit einem Absatzzeichen des Felds am Ende, <see cref="OhneAbschlussUmbruch"/>), am selben
    /// Element mit Fokus, das vorher nachweislich leer war. Gleiche Zeichen oder eine Teilfolge reichen nicht: Ein Feld kann
    /// einen Entwurf nachladen oder Zeichen verlieren, dann bliebe sonst fremder Inhalt nicht erhalten. Ein abweichender
    /// Inhalt bleibt stehen; der Nutzer leert das Testfeld selbst (Handtest Referenznotebook: der Editor verliert dort Zeichen).
    /// </summary>
    /// <param name="platzhalter">Vorinhalt des Felds vor dem Durchgang (<see cref="Vorinhalt"/>); null nur bei leerem Feld.</param>
    public static bool DarfLeerenImMessmodus(string gelesen, string testtext, bool nochFokussiert, bool? gleicheId, string? platzhalter)
    {
        ArgumentNullException.ThrowIfNull(gelesen);
        ArgumentNullException.ThrowIfNull(testtext);
        return nochFokussiert && gleicheId == true && platzhalter is null && testtext.Length > 0
            && string.Equals(gelesen, testtext, StringComparison.Ordinal);
    }

    /// <summary>
    /// Leeren nur, wenn das gebundene Element noch fokussiert ist und der gelesene Inhalt der geschriebene Text ist, höchstens
    /// mit verlorenen Zeichen (eine Teilfolge in derselben Reihenfolge). Alles andere, auch Zeichen des Testtexts in anderer
    /// Reihenfolge oder doppelt, gilt als fremd und wird nie gelöscht (Codex zu PR #49). Ein leeres Feld braucht kein Leeren.
    /// </summary>
    public static bool DarfLeeren(string gelesen, string geschrieben, bool nochFokussiert)
    {
        ArgumentNullException.ThrowIfNull(gelesen);
        ArgumentNullException.ThrowIfNull(geschrieben);
        if (!nochFokussiert || gelesen.Length == 0 || gelesen.Length > geschrieben.Length)
        {
            return false;
        }

        var position = 0;
        foreach (var zeichen in geschrieben)
        {
            if (position < gelesen.Length && gelesen[position] == zeichen)
            {
                position++;
            }
        }

        return position == gelesen.Length;
    }

    /// <summary>
    /// Hängt einen Eintrag an die JSON-Liste in der Datei an; legt Datei und Ordner an. Ist die Datei keine JSON-Liste, bleibt sie
    /// unangetastet (<see cref="InvalidDataException"/>).
    /// </summary>
    public static void Anhängen(string pfad, object eintrag)
    {
        ArgumentException.ThrowIfNullOrEmpty(pfad);
        ArgumentNullException.ThrowIfNull(eintrag);
        var liste = new JsonArray();
        var alt = File.Exists(pfad) ? File.ReadAllText(pfad) : string.Empty;
        if (!string.IsNullOrWhiteSpace(alt))
        {
            try
            {
                liste = JsonNode.Parse(alt) as JsonArray ?? throw new InvalidDataException($"{pfad} enthält keine JSON-Liste.");
            }
            catch (JsonException ausnahme)
            {
                throw new InvalidDataException($"{pfad} ist kein gültiges JSON.", ausnahme);
            }
        }

        liste.Add(JsonSerializer.SerializeToNode(eintrag, JsonOptionen));
        if (Path.GetDirectoryName(Path.GetFullPath(pfad)) is { Length: > 0 } ordner)
        {
            Directory.CreateDirectory(ordner);
        }

        var temporaer = pfad + ".neu";
        File.WriteAllText(temporaer, liste.ToJsonString(JsonOptionen));
        File.Move(temporaer, pfad, overwrite: true);
    }
}
