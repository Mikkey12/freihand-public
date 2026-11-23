using System.Diagnostics;
using System.Globalization;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Segments;
using Freihand.Windows;
using Freihand.Windows.Targets;

namespace Freihand.Zielprobe;

/// <summary>Ergebnis eines Umbruch-Durchgangs (WM05); nur Zahlen, ja/nein und Namen, nie Feldinhalt.</summary>
internal sealed record UmbruchEintrag(
    string Zeitstempel,
    string Name,
    string Modus,
    int Teilgroesse,
    string Prozess,
    string ControlType,
    string ClassName,
    string FrameworkId,
    bool? Mehrzeilig,
    string? AusgabeErgebnis,
    string? AusgabeGrund,
    long DauerMs,
    bool Lesbar,
    string? Ergebnis,
    int? GelesenLaenge,
    bool? RuntimeIdGleich,
    bool? Geleert,
    string? Abbruch);

/// <summary>
/// Befehl <c>umbruch</c> (WM05): ein kurzer Testtext mit Umbrüchen und einer Leerzeile über
/// den echten Ausgabeweg ins fokussierte Feld, zurücklesen und einordnen. Modus <c>unicode</c> sendet U+000D ohne echte Taste,
/// <c>umschalt-enter</c> echte Umschalt+Enter mit <c>Neutralize</c>.
/// </summary>
internal static class Umbruch
{
    /// <summary>Erfundener Testtext; LF ist der Umbruch des Kerns, zwei LF ergeben eine Leerzeile.</summary>
    public const string Testtext = "Erste Zeile mit Umlauten äöü\nZweite Zeile, Grüsse\n\nDritter Absatz.";

    private static readonly TimeSpan NachWartezeit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ZwischenDurchgaengen = TimeSpan.FromSeconds(2);

    public static async Task<int> AusfuehrenAsync(Optionen optionen)
    {
        var name = optionen.Pflicht("name");
        var ziel = optionen.Pflicht("out");
        var wartezeit = optionen.Zahl("wartezeit", 5);
        var durchgaenge = optionen.Zahl("durchgaenge", 2, minimum: 1);
        var teil = optionen.Zahl("teil", 1, minimum: 1);
        var (modus, umbruch, sicherung) = (optionen.Text("modus") ?? "unicode").ToLowerInvariant() switch
        {
            "unicode" => ("unicode", LineBreakMode.UnicodeReturn, ControlKeySafety.NotApproved),
            "umschalt-enter" => ("umschalt-enter", LineBreakMode.ShiftEnter, ControlKeySafety.Neutralize),
            var anders => throw new ArgumentException($"--modus „{anders}“: erlaubt sind unicode und umschalt-enter."),
        };

        Countdown.Warten(wartezeit, "Text mit Umbrüchen schreiben");
        var uia = new UiaAccess();
        using var inspector = new UiaTargetInspector(uia);
        for (var nummer = 0; nummer < durchgaenge; nummer++)
        {
            if (nummer > 0)
            {
                Thread.Sleep(ZwischenDurchgaengen);
            }

            var eintrag = await DurchgangAsync(uia, inspector, name, modus, umbruch, sicherung, teil).ConfigureAwait(false);
            Logik.Anhängen(ziel, eintrag);
            Console.WriteLine(Zeile(eintrag));
            if (eintrag.Abbruch is { } grund)
            {
                Console.Error.WriteLine($"Abbruch: {grund}");
                return 1;
            }
        }

        Console.WriteLine($"Angehängt an {ziel}");
        return 0;
    }

    private static async Task<UmbruchEintrag> DurchgangAsync(
        UiaAccess uia, UiaTargetInspector inspector, string name, string modus, LineBreakMode umbruch, ControlKeySafety sicherung, int teil)
    {
        var zeit = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        TargetFacts? facts = null;
        UmbruchEintrag Abbruch(string grund) => new(
            zeit, name, modus, teil, facts?.ProcessName ?? string.Empty, facts?.ControlType ?? string.Empty,
            facts?.ClassName ?? string.Empty, facts?.FrameworkId ?? string.Empty, facts?.IsMultiLine, null, null, 0, true, null,
            null, null, null, grund);

        if (UiaFeld.Fokus(uia) is not { } vorher || inspector.Focused() is not { } fokus)
        {
            return Abbruch("Kein fokussiertes Feld.");
        }

        facts = fokus.Facts;
        var idVorher = UiaFeld.RuntimeId(uia, vorher);
        // Dieselbe Regel wie bei „schreiben“ (Codex zu PR #70): leer entscheidet der Rohinhalt, Leerzeilen sind Vorinhalt
        var davor = UiaFeld.Inhalt(vorher);
        if (davor is { } vorhanden && !Logik.OhneVorinhalt(vorhanden))
        {
            return Abbruch("Feld nicht leer");
        }

        var regel = new TargetRule(
            facts.ProcessName, facts.ControlType, Leer(facts.ClassName), Leer(facts.FrameworkId), null, FieldKinds.MultiLine,
            "Zielprobe", umbruch, teil, IdentityMode.RuntimeId);
        var optionen = new OutputOptions(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(10), sicherung);
        using var windows = new WindowsTargets(new TargetRuleTable([regel]), optionen);
        var gebunden = windows.Binding.BindFocused(ProfileStrictness.Standard);
        if (gebunden.Target is not { } gebundenesZiel)
        {
            return Abbruch($"Nicht bindbar: {gebunden.Refusal}");
        }

        // Dieselbe Regel wie bei „schreiben“: gebunden, gelesen und geleert wird nur das Feld vom Beginn des Durchgangs
        if (UiaFeld.Fokus(uia) is not { } gebundenesFeld || UiaFeld.RuntimeId(uia, gebundenesFeld) != idVorher)
        {
            return Abbruch("Anderes Feld im Fokus");
        }

        var stoppuhr = Stopwatch.StartNew();
        var bericht = await windows.Output.WriteAsync(
            new TextSegment(Guid.NewGuid(), Testtext, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)), gebundenesZiel).ConfigureAwait(false);
        stoppuhr.Stop();
        Thread.Sleep(NachWartezeit);

        // Dieselbe Regel wie bei „schreiben“ (Codex zu PR #73): immer das gebundene Feld lesen, erst bei einem gültigen Paar
        // gleicher Lesungen entscheiden, den Fokus danach getrennt vergleichen
        var gelesen = davor is not null ? Schreiben.LesenStabil(vorher) : null;
        var spaeter = UiaFeld.Fokus(uia);
        var idNachher = spaeter is null ? null : UiaFeld.RuntimeId(uia, spaeter);
        var gleicheId = idVorher is null || idNachher is null ? (bool?)null : idVorher == idNachher;
        var ergebnis = gelesen is null ? (UmbruchErgebnis?)null : Logik.UmbruchPrüfen(Testtext, gelesen);

        bool? geleert = null;
        string? abbruch = ergebnis switch
        {
            UmbruchErgebnis.Fehlt => "Text fehlt ganz oder teilweise: bitte von Auge prüfen, ob etwas gesendet oder abgeschickt wurde",
            null => "Feld nicht lesbar oder nicht stabil: bitte von Auge prüfen und von Hand leeren",
            _ => null,
        };
        if (gelesen is { } inhalt && ergebnis is UmbruchErgebnis.Umbruch or UmbruchErgebnis.OhneUmbruch)
        {
            var fokussiert = windows.Binding.CheckBound(gebundenesZiel.BindingId) == TargetCheck.Focused;
            var vereinheitlicht = Logik.UmbrücheVereinheitlichen(inhalt);
            // Dieselbe Regel wie bei „schreiben“ (Codex zu PR #70): unmittelbar vor dem Leeren noch einmal lesen; geleert heisst
            // wieder leer nach Logik.OhneVorinhalt
            if (Logik.Vorinhalt(davor, inhalt) is null
                && (Logik.DarfLeeren(vereinheitlicht, Testtext, fokussiert) || Logik.DarfLeeren(vereinheitlicht.Replace("\n", " ", StringComparison.Ordinal), Testtext.Replace("\n", " ", StringComparison.Ordinal), fokussiert)))
            {
                UiaFeld.FeldLeeren(windows.Binding, gebundenesZiel.BindingId, () => UiaFeld.Inhalt(vorher) == inhalt);
                Thread.Sleep(500);
                geleert = Logik.OhneVorinhalt(UiaFeld.Inhalt(vorher));
            }
            else
            {
                geleert = false;
            }

            abbruch ??= geleert == true ? null : "Feld nicht geleert, bitte von Hand leeren";
        }
        else if (ergebnis is UmbruchErgebnis.Abweichend)
        {
            abbruch = "Text weicht ab: bitte von Auge prüfen und von Hand leeren";
        }

        return new UmbruchEintrag(
            zeit, name, modus, teil, facts.ProcessName, facts.ControlType, facts.ClassName, facts.FrameworkId, facts.IsMultiLine,
            bericht.Result.ToString(), bericht.Reason.ToString(), stoppuhr.ElapsedMilliseconds, gelesen is not null,
            ergebnis?.ToString(), gelesen?.Length, gleicheId, geleert, abbruch);
    }

    private static string? Leer(string wert) => wert.Length == 0 ? null : wert;

    private static string Zeile(UmbruchEintrag e) =>
        $"{e.Modus}: {e.DauerMs} ms, Ausgabe {e.AusgabeErgebnis ?? "–"}/{e.AusgabeGrund ?? "–"}, Ergebnis {e.Ergebnis ?? "nicht lesbar"}, "
        + $"mehrzeilig gemeldet: {Fakten.JaNein(e.Mehrzeilig)}, RuntimeId gleich: {Fakten.JaNein(e.RuntimeIdGleich)}";
}
