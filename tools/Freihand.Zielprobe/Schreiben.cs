using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Segments;
using Freihand.Windows;
using Freihand.Windows.Targets;
using Windows.Win32.UI.Accessibility;

namespace Freihand.Zielprobe;

/// <summary>Ergebnis eines Durchgangs; nur Zahlen, ja/nein und Namen von Gründen, nie Feldinhalt.</summary>
internal sealed record DurchgangEintrag(
    string Zeitstempel,
    string Name,
    bool Einzeilig,
    int Teilgroesse,
    int PauseMs,
    string Prozess,
    string ControlType,
    string ClassName,
    string FrameworkId,
    string? AusgabeErgebnis,
    string? AusgabeGrund,
    int GeschriebenLaenge,
    int GehaltenLaenge,
    long DauerMs,
    bool Lesbar,
    bool? Zeichengetreu,
    int ErwartetLaenge,
    int? GelesenLaenge,
    int? ErsteAbweichung,
    int? AbweichendeZeichen,
    bool? RuntimeIdGleich,
    bool? Geleert,
    string? Abbruch,
    string? Leseweg = null,
    int? LaengeVorher = null);

/// <summary>Befehl <c>schreiben</c>: Testtext in Teilen ins fokussierte Feld schreiben und das Ergebnis prüfen.</summary>
internal static class Schreiben
{
    private const string Fixture = "Fixtures/au01-testtext.txt";
    private const string FixtureSha256 = "c44cc9232815f5a6ecb5722acb287dc85413c6748ae404a5625a0d1dd22d07e2";
    private static readonly TimeSpan NachWartezeit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ZwischenDurchgaengen = TimeSpan.FromSeconds(2);

    /// <summary>Abbruch im Messmodus, wenn nicht mehr das Feld vom Countdown den Fokus hat; nichts geschrieben.</summary>
    public const string AnderesFeld = "Anderes Feld im Fokus";

    /// <summary>
    /// Abbruch im Messmodus (Sicherheitsprüfung, F2): Der Prüftext kam nicht genau so an. Das Werkzeug löscht dann nie selbst, auch
    /// nicht bei gleichen Zeichen oder einer Teilfolge; der Nutzer leert das Testfeld, dann geht die Messung kleiner weiter.
    /// </summary>
    public const string Abweichend = "Prüftext weicht ab, nicht geleert";

    /// <summary>Abbruch, wenn das Leeren nicht nachweislich gelang; beginnt immer so (Auswertung in Freihand).</summary>
    public const string NichtGeleert = "Feld nicht geleert";

    /// <param name="hinweistext">Im Messmodus der nachgewiesene Hinweistext des Felds (nur im Prozess), sonst null.</param>
    public static async Task<int> AusfuehrenAsync(Optionen optionen, string? hinweistext = null)
    {
        var name = optionen.Pflicht("name");
        var ziel = optionen.Pflicht("out");
        var wartezeit = optionen.Zahl("wartezeit", 5);
        var pause = optionen.Zahl("pause", 10);
        var teile = Optionen.Teile(optionen.Text("teile"));
        var text = Testtext(optionen.Zahl("laenge", 2000));
        Countdown.Warten(wartezeit, "Testtext schreiben");

        var uia = new UiaAccess();
        using var inspector = new UiaTargetInspector(uia);
        for (var nummer = 0; nummer < teile.Count; nummer++)
        {
            if (nummer > 0)
            {
                Thread.Sleep(ZwischenDurchgaengen);
            }

            var eintrag = await DurchgangAsync(uia, inspector, name, optionen.Einzeilig, optionen.Platzhalter, optionen.Messmodus, optionen.Anker, hinweistext, teile[nummer], pause, text).ConfigureAwait(false);
            Logik.Anhängen(ziel, eintrag);
            Console.WriteLine(Zeile(eintrag));
            if (eintrag.Abbruch is { } grund)
            {
                Console.Error.WriteLine($"Abbruch: {grund}");
                return 1;
            }

            if (!eintrag.Lesbar)
            {
                Console.WriteLine("Feld nicht lesbar, bitte von Auge prüfen. Es wurde nicht geleert; weitere Durchgänge unterbleiben.");

                // Messregel: ohne Zurücklesen ist nichts nachgewiesen; die Messung hört auf, statt ins gefüllte Feld weiterzumachen
                return optionen.Messmodus ? 1 : 0;
            }
        }

        Console.WriteLine($"Angehängt an {ziel}");
        return 0;
    }

    /// <summary>Testtext aus der Fixture, geprüft; Umbrüche durch Leerzeichen ersetzt, weil der Modus Refuse sie nicht schreibt.</summary>
    private static string Testtext(int laenge)
    {
        var pfad = Path.Combine(AppContext.BaseDirectory, Fixture);
        var bytes = File.ReadAllBytes(pfad);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != FixtureSha256)
        {
            throw new InvalidDataException("Der Testtext hat nicht die erwartete Prüfsumme.");
        }

        var text = Logik.UmbrücheErsetzen(System.Text.Encoding.UTF8.GetString(bytes));
        if (text.Length != 2000)
        {
            throw new InvalidDataException("Der Testtext hat nicht 2000 Zeichen.");
        }

        // Kürzer für Felder mit Längengrenze (Outlook-Betreff: 255 Zeichen); ohne Leerzeichen am Ende
        return laenge is >= 1 and < 2000 ? text[..laenge].TrimEnd() : text;
    }

    private static async Task<DurchgangEintrag> DurchgangAsync(
        UiaAccess uia, UiaTargetInspector inspector, string name, bool einzeilig, bool platzhalter, bool messmodus, string? anker,
        string? hinweistext, int teil, int pause, string text)
    {
        var zeit = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        TargetFacts? facts = null;

        DurchgangEintrag Abbruch(string grund) => Eintrag(zeit, name, einzeilig, teil, pause, facts, grund);

        if (UiaFeld.Fokus(uia) is not { } vorher || inspector.Focused() is not { } fokus)
        {
            return Abbruch("Kein fokussiertes Feld.");
        }

        facts = fokus.Facts;
        var idVorher = UiaFeld.RuntimeId(uia, vorher);

        var rohDavor = UiaFeld.Inhalt(vorher);
        var davor = rohDavor is null ? null : Logik.OhneAbschlussUmbruch(rohDavor);

        // Messregel: Merkmale (Inspektor), Inhalt und Anker müssen vom selben Element stammen
        var gleichesElement = fokus.Identity.RuntimeId == idVorher ? idVorher : null;
        if (messmodus && Logik.MessmodusAbbruch(anker, gleichesElement, davor is not null) is { } nichtHier)
        {
            return Abbruch(nichtHier);
        }

        // Messregel: Im Messmodus wird nur in ein Feld ohne Vorinhalt (Rohinhalt, auch Leerzeilen zählen) oder mit genau dem
        // nachgewiesenen Hinweistext geschrieben (Codex zu PR #70); die Handbefehle schreiben auch neben Leerraum
        // Codex zu PR #70: Der nachgewiesene Hinweistext gilt nur, wenn der Rohinhalt ihm entspricht (Logik.GleicherHinweis);
        // Absätze dahinter sind neuer Inhalt
        var grundinhalt = Logik.Platzhalterinhalt(messmodus ? rohDavor : davor, platzhalter, messmodus, hinweistext);
        if (rohDavor is { } vorhanden && grundinhalt is null && (messmodus ? !Logik.OhneVorinhalt(vorhanden) : !Logik.IstLeer(davor!)))
        {
            return Abbruch("Feld nicht leer") with { Leseweg = UiaFeld.LetzterLeseweg, LaengeVorher = vorhanden.Length };
        }

        var lesbar = davor is not null;
        var regel = new TargetRule(
            facts.ProcessName, facts.ControlType, Leer(facts.ClassName), Leer(facts.FrameworkId), null,
            einzeilig ? FieldKinds.SingleLine : FieldKinds.MultiLine, "Zielprobe", LineBreakMode.Refuse, teil, IdentityMode.RuntimeId);
        var optionen = new OutputOptions(
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(pause), ControlKeySafety.NotApproved);
        using var windows = new WindowsTargets(new TargetRuleTable([regel]), optionen);
        BindResult gebunden;
        if (messmodus)
        {
            // Im Messmodus wird genau das Element gebunden, dessen RuntimeId der Anker ist: erst merken und prüfen, dann nur
            // dieses gemerkte Feld binden (AU-10). Wechselt der Fokus danach, schreibt die Ausgabe nicht (Codex zu PR #70)
            var merken = windows.Marker.MarkFocused(ProfileStrictness.Standard);
            if (merken.Mark is not { } gemerkt)
            {
                return Abbruch($"Nicht bindbar: {merken.Refusal}");
            }

            if (Logik.MessmodusAbbruch(anker, gemerkt.Identity.RuntimeId, lesbar: true) is { } anderes)
            {
                return Abbruch(anderes);
            }

            gebunden = windows.Marker.BindMarked(gemerkt, ProfileStrictness.Standard);
        }
        else
        {
            gebunden = windows.Binding.BindFocused(ProfileStrictness.Standard);

            // Dieselbe Regel bei den Handbefehlen (Vorprüfung zu PR #73): gebunden, gelesen und geleert wird nur das Feld vom
            // Beginn des Durchgangs
            if (gebunden.Target is not null && (UiaFeld.Fokus(uia) is not { } gebundenesFeld || UiaFeld.RuntimeId(uia, gebundenesFeld) != idVorher))
            {
                return Abbruch(AnderesFeld);
            }
        }

        if (gebunden.Target is not { } ziel)
        {
            return Abbruch($"Nicht bindbar: {gebunden.Refusal}");
        }

        var stoppuhr = Stopwatch.StartNew();
        var bericht = await windows.Output.WriteAsync(
            new TextSegment(Guid.NewGuid(), text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)), ziel).ConfigureAwait(false);
        stoppuhr.Stop();
        Thread.Sleep(NachWartezeit);

        // Regel (Codex zu PR #73): Gelesen und vor dem Leeren geprüft wird immer das gebundene Feld, das Element vom Beginn
        // des Durchgangs, nie das Feld, das gerade den Fokus hat; der Fokus wird danach getrennt verglichen. Entschieden
        // wird erst, wenn das Feld zweimal hintereinander lesbar und gleich war (Handtest Referenznotebook); ohne ein solches Paar
        // wird weder bewertet noch geleert
        var feld = vorher;
        var rohDanach = lesbar ? LesenStabil(feld) : null;
        var spaeter = UiaFeld.Fokus(uia);
        var idNachher = spaeter is null ? null : UiaFeld.RuntimeId(uia, spaeter);
        var gleicheId = idVorher is null || idNachher is null ? (bool?)null : idVorher == idNachher && (anker is null || idNachher == anker);
        var danach = rohDanach is null ? null : Logik.OhneAbschlussUmbruch(rohDanach);
        var vergleich = danach is null ? null : Logik.Vergleichen(text, danach);

        bool? geleert = null;
        string? abbruch = null;

        // Messregel: Vorinhalt ist alles, was vorher im Feld stand, als Rohinhalt, auch Leerraum, Leerzeilen oder ein Platzhalter.
        // Mit Vorinhalt leert das Werkzeug nie selbst (Codex zu PR #70: ein verschluckter Anfang liesse Vorinhalt wie
        // geschriebenen Text aussehen; abschliessende Leerzeilen dürfen nicht wie Aufbau des Felds behandelt werden)
        // Ein nachgewiesener Hinweistext ist kein Vorinhalt: er verschwand beim ersten Zeichen und ist keine Eingabe
        var vorinhalt = messmodus && grundinhalt is not null ? null : grundinhalt ?? Logik.Vorinhalt(rohDavor, rohDanach);

        // Ausgangszustand für „nichts angekommen“ und „geleert“: im Messmodus immer mit dem nachgewiesenen Hinweistext, auch
        // wenn das Feld vor diesem Durchgang leer war (eigene Vorprüfung); bei den Handbefehlen der vermutete Platzhalter
        var ausgang = messmodus ? hinweistext : grundinhalt;
        if (messmodus && gleicheId != true)
        {
            // Nach dem Schreiben nicht nachweislich dasselbe Feld: nichts leeren, als Abweichung melden
            geleert = false;
            abbruch = AnderesFeld;
        }
        else if (lesbar && rohDanach is null)
        {
            // Kein gültiges Paar gleicher Lesungen: nichts bewerten, nichts leeren
            geleert = false;
            abbruch = NichtGeleert + ", bitte von Hand leeren (Inhalt nicht stabil lesbar)";
        }
        else if (danach is not null && !Logik.Ausgangszustand(rohDanach, ausgang) && rohDanach != rohDavor)
        {
            // Regel für jedes Löschen (Codex zu PR #70): Gelöscht wird nur, wenn ein Lesen unmittelbar davor noch genau den
            // Inhalt zeigt, auf den sich der Entscheid stützt (FeldLeeren liest vor Strg+A und vor Entf); geleert ist das Feld
            // nur, wenn es wieder im Ausgangszustand ist, nach derselben Regel wie vor dem Schreiben
            // Im Messmodus (Sicherheitsprüfung, F2) nur der vollständige eigene Prüftext, zweimal gleich gelesen (LesenStabil); ein
            // abweichender Inhalt bleibt stehen, Zeichenmenge oder Teilfolge reichen nicht. Geleert heisst: danach wieder
            // zweimal lesbar und gleich im Ausgangszustand
            var fokussiert = windows.Binding.CheckBound(ziel.BindingId) == TargetCheck.Focused;
            var darf = vorinhalt is null && (messmodus
                ? Logik.DarfLeerenImMessmodus(danach, text, fokussiert, gleicheId, vorinhalt)
                : Logik.DarfLeeren(danach, bericht.Written, fokussiert));
            if (darf)
            {
                UiaFeld.FeldLeeren(windows.Binding, ziel.BindingId, () => UiaFeld.Inhalt(feld) == rohDanach);
                Thread.Sleep(500);
                geleert = Logik.Ausgangszustand(LesenStabil(feld), ausgang);
                abbruch = geleert == true ? null : NichtGeleert + ", bitte von Hand leeren";
            }
            else
            {
                geleert = false;
                abbruch = messmodus && vorinhalt is null && vergleich?.Gleich != true ? Abweichend : NichtGeleert + ", bitte von Hand leeren";
            }
        }
        else if (danach is not null)
        {
            // Nichts angekommen (z. B. gehalten): das Feld ist im Ausgangszustand oder genau wie vorher, nichts zu leeren
            geleert = true;
        }

        return new DurchgangEintrag(
            zeit, name, einzeilig, teil, pause, facts.ProcessName, facts.ControlType, facts.ClassName, facts.FrameworkId,
            bericht.Result.ToString(), bericht.Reason.ToString(), bericht.Written.Length, bericht.Held.Length,
            stoppuhr.ElapsedMilliseconds, lesbar && danach is not null, vergleich?.Gleich, text.Length, vergleich?.Gelesen,
            vergleich?.ErsteAbweichung, vergleich?.AbweichendeZeichen, gleicheId, geleert, abbruch, UiaFeld.LetzterLeseweg);
    }

    /// <summary>
    /// „Feld messen“ mit Hinweistext. Regel (Sicherheitsprüfung, F5): Meldet das Feld einen leeren Wert und zeigt den Hinweis nur
    /// optisch, wird normal gemessen („leer“). Meldet es einen nichtleeren Wert, lässt sich ohne Tippen nicht nachweisen, dass
    /// er nur ein Hinweis ist, und eine Tabellenzelle (z. B. Dynamics) kann schon beim ersten Zeichen ihren Wert ersetzen: dann
    /// wird nichts getippt („belegt“); Freihand bietet „Nur Feld erkennen“ oder ein eindeutig leeres Testfeld an.
    /// </summary>
    /// <returns>Immer ohne Hinweistext (null) und das Ergebnis für die Zusammenfassung; getippt wird nie.</returns>
    public static Task<(string? Hinweistext, string Ergebnis)> HinweisAsync(string? anker)
    {
        var uia = new UiaAccess();
        if (UiaFeld.Fokus(uia) is not { } vorher || UiaFeld.RuntimeId(uia, vorher) is not { } id || id != anker)
        {
            return Task.FromResult<(string?, string)>((null, "vorher_abgebrochen"));
        }

        return Task.FromResult<(string?, string)>((null, Logik.OhneVorinhalt(UiaFeld.Inhalt(vorher)) ? "leer" : "belegt"));
    }

    /// <summary>Erstes Zeichen des Testtexts; damit prüft „Feld messen“ einen Hinweistext.</summary>
    public static char Pruefzeichen => Testtext(250)[0];

    /// <summary>
    /// Liest alle 250 ms, bis das Feld zweimal hintereinander lesbar und gleich ist, höchstens 5 s; liefert diesen
    /// Rohinhalt. Eine nicht lesbare Lesung beginnt das Paar neu (Codex zu PR #73). Ohne gültiges Paar bis zur Frist: null,
    /// dann wird weder bewertet noch geleert.
    /// </summary>
    internal static string? LesenStabil(IUIAutomationElement element)
    {
        var uhr = Stopwatch.StartNew();
        string? vorige = null;
        while (uhr.Elapsed <= TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(250);
            var jetzt = UiaFeld.Inhalt(element);
            if (jetzt is not null && jetzt == vorige)
            {
                return jetzt;
            }

            vorige = jetzt;
        }

        return null;
    }

    private static DurchgangEintrag Eintrag(string zeit, string name, bool einzeilig, int teil, int pause, TargetFacts? facts, string abbruch) =>
        new(zeit, name, einzeilig, teil, pause, facts?.ProcessName ?? string.Empty, facts?.ControlType ?? string.Empty,
            facts?.ClassName ?? string.Empty, facts?.FrameworkId ?? string.Empty, null, null, 0, 0, 0, true, null, 2000, null, null,
            null, null, null, abbruch);

    private static string? Leer(string wert) => wert.Length == 0 ? null : wert;

    private static string Zeile(DurchgangEintrag e)
    {
        var treue = !e.Lesbar ? "nicht lesbar, bitte von Auge prüfen" : e.Zeichengetreu is { } gleich ? $"zeichengetreu {(gleich ? "ja" : "nein")}" : "zeichengetreu nicht geprüft";
        var ausgabe = e.AusgabeErgebnis is null ? "nicht geschrieben" : $"{e.AusgabeErgebnis}/{e.AusgabeGrund}";
        var gelesen = e.GelesenLaenge is { } laenge ? $", gelesen {laenge} von {e.ErwartetLaenge}" : string.Empty;
        var abweichung = e.Zeichengetreu == false ? $", erste Abweichung bei {e.ErsteAbweichung}, {e.AbweichendeZeichen} Zeichen anders" : string.Empty;
        return $"Teilgrösse {e.Teilgroesse}: {e.DauerMs} ms, {ausgabe}, {treue}{gelesen}{abweichung}, RuntimeId gleich: {Fakten.JaNein(e.RuntimeIdGleich)}";
    }
}
