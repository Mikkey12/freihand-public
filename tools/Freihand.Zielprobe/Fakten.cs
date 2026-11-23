using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Windows.Targets;

namespace Freihand.Zielprobe;

/// <summary>Merkmale eines Felds, wie Freihand sie liest; kein Inhalt, nie Name, Wert oder Fenstertitel.</summary>
internal sealed record FaktenEintrag(
    string Zeitstempel,
    string Name,
    string Prozess,
    string ControlType,
    string ClassName,
    string FrameworkId,
    bool AutomationIdGesetzt,
    string AutomationId,
    bool RuntimeIdVorhanden,
    bool? IsPassword,
    bool? IsReadOnly,
    bool? IsEnabled,
    bool? Mehrzeilig,
    bool? IsElevated,
    bool InhaltLesbar,
    string Entscheid,
    string Grund,
    int? TeilgroesseBeiUnbekannt);

/// <summary>Befehl <c>fakten</c>: Merkmale des fokussierten Felds lesen und den Entscheid der Zieltabelle festhalten.</summary>
internal static class Fakten
{
    /// <summary>
    /// RuntimeId des Felds aus dem letzten Aufruf (Punktfolge), null ohne Feld oder ohne lesbare RuntimeId. Nur im Prozess,
    /// nie in die Ergebnisdatei: „messen“ bindet damit alle Durchgänge an genau dieses Feld (Codex zu PR #70).
    /// </summary>
    public static string? LetzteRuntimeId { get; private set; }

    /// <summary>Der Fokus wechselte beim letzten Aufruf zwischen den Lesevorgängen; dann wurde nichts festgehalten.</summary>
    public static bool Fokuswechsel { get; private set; }

    public static int Ausfuehren(Optionen optionen)
    {
        LetzteRuntimeId = null;
        Fokuswechsel = false;
        var wartezeit = optionen.Zahl("wartezeit", 5);
        var name = optionen.Pflicht("name");
        var ziel = optionen.Pflicht("out");
        Countdown.Warten(wartezeit, "Merkmale lesen");

        var uia = new UiaAccess();
        using var inspector = new UiaTargetInspector(uia);
        if (inspector.Focused() is not { } fokus)
        {
            Console.Error.WriteLine("Kein fokussiertes Feld gefunden; nichts festgehalten.");
            return 1;
        }

        var (identity, facts) = fokus;
        var element = UiaFeld.Fokus(uia);

        // Messregel, Stufe Erfassen (Codex zu PR #70): Merkmale, Lesbarkeit und Anker stammen vom selben Element. Hat der Fokus
        // zwischen den Lesevorgängen gewechselt, wird nichts festgehalten und nichts gemessen
        var elementId = element is null ? null : UiaFeld.RuntimeId(uia, element);
        if (identity.RuntimeId.Length > 0 && elementId != identity.RuntimeId)
        {
            Console.Error.WriteLine("Der Fokus wechselte während des Lesens; nichts festgehalten.");
            Fokuswechsel = true;
            return 1;
        }

        // „Nur Feld erkennen“ (Sicherheitsprüfung, F4): nur Merkmale, kein Zugriff auf den Inhalt, auch nicht zur Prüfung der Lesbarkeit
        var erkennen = optionen.Text("modus") == "erkennen";
        var lesbar = !erkennen && element is not null && UiaFeld.Inhalt(element) is not null;
        LetzteRuntimeId = identity.RuntimeId.Length > 0 ? identity.RuntimeId : null;
        var entscheid = new TargetRuleTable([]).Decide(facts, ProfileStrictness.Standard);
        var (text, grund, teil) = entscheid switch
        {
            Bindable bindbar => ("Bindable", bindbar.Known ? "bekannte Zeile" : "unbekanntes Feld (Standardregel)", (int?)bindbar.Rule.MaxPartLength),
            NotBindable nicht => ("NotBindable", nicht.Reason.ToString(), null),
            _ => ("unbekannt", string.Empty, null),
        };
        var eintrag = new FaktenEintrag(
            DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture), name, facts.ProcessName,
            facts.ControlType, facts.ClassName, facts.FrameworkId, identity.AutomationId.Length > 0, identity.AutomationId,
            identity.RuntimeId.Length > 0, facts.IsPassword, facts.IsReadOnly, facts.IsEnabled, facts.IsMultiLine, facts.IsElevated,
            lesbar, text, grund, teil);
        Logik.Anhängen(ziel, eintrag);

        Console.WriteLine($"Prozess {eintrag.Prozess}, {eintrag.ControlType}, Klasse {Zeige(eintrag.ClassName)}, Framework {Zeige(eintrag.FrameworkId)}");
        Console.WriteLine($"AutomationId gesetzt: {JaNein(eintrag.AutomationIdGesetzt)}, RuntimeId vorhanden: {JaNein(eintrag.RuntimeIdVorhanden)}");
        Console.WriteLine($"Passwort: {JaNein(eintrag.IsPassword)}, nur lesbar: {JaNein(eintrag.IsReadOnly)}, aktiviert: {JaNein(eintrag.IsEnabled)}, mehrzeilig: {JaNein(eintrag.Mehrzeilig)}, erhöht: {JaNein(eintrag.IsElevated)}");
        Console.WriteLine(erkennen ? "Inhalt nicht gelesen (nur erkennen)" : $"Inhalt per UIA lesbar: {JaNein(lesbar)}");
        Console.WriteLine($"Zieltabelle (Standard): {text} – {grund}");
        Console.WriteLine($"Angehängt an {ziel}");
        return 0;
    }

    private static string Zeige(string wert) => wert.Length == 0 ? "(leer)" : wert;

    internal static string JaNein(bool? wert) => wert switch { true => "ja", false => "nein", null => "nicht lesbar" };
}

/// <summary>Countdown mit Konsolen-Piepton je Sekunde.</summary>
internal static class Countdown
{
    public static void Warten(int sekunden, string zweck)
    {
        Console.WriteLine($"{zweck}: In {sekunden} s das Zielfeld anklicken und die Hände von der Tastatur nehmen.");
        for (var rest = sekunden; rest > 0; rest--)
        {
            Console.WriteLine($"  {rest} …");
            try
            {
                Console.Beep(880, 120);
            }
            catch (Exception ausnahme) when (ausnahme is PlatformNotSupportedException or IOException)
            {
                // ohne Piepton weiter
            }

            Thread.Sleep(880);
        }
    }
}
