using System.Globalization;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;

namespace Freihand.App;

/// <summary>Farbe des Zustandspunkts der Pille und des Symbols im Infobereich (Kapitel 3.5).</summary>
public enum StateColor
{
    Grey,
    Red,
    Yellow,
    Green,
}

/// <summary>Zustand der schwebenden Pille.</summary>
public enum PillState
{
    Hidden,
    Starting,
    Recording,
    Finishing,
    FocusAway,
    Done,
    Held,
    Notice,
}

/// <summary>Fester Punkt der Pille: der linke oder rechte und der obere oder untere Rand, je nach nächstem Bildschirmrand.</summary>
/// <param name="X">Fester linker (<paramref name="FromLeft"/>) oder rechter Rand.</param>
/// <param name="Y">Fester oberer (<paramref name="FromTop"/>) oder unterer Rand.</param>
public readonly record struct PillAnchor(double X, double Y, bool FromLeft, bool FromTop);

/// <summary>Ton beim Start und Ende der Aufnahme (Kapitel 3.5).</summary>
public enum Tone
{
    Start,
    End,
}

/// <param name="Details">Ziel, Mikrofon, Erkennung, Zähler; beim Überfahren der Pille.</param>
/// <param name="HideAfter">Blendet sich nach dieser Zeit selbst aus; null bleibt stehen.</param>
/// <param name="OpensDictationWindow">Ein Klick auf die Pille holt den gehaltenen Text ins Diktatfenster.</param>
/// <param name="MicrophoneMuted">Aufnahme mit stummem Mikrofon; der Countdown im Fenster behält den Hinweis.</param>
public sealed record PillView(
    PillState State, StateColor Color, string Text, IReadOnlyList<string> Details, TimeSpan? HideAfter, bool OpensDictationWindow,
    bool MicrophoneMuted = false);

/// <summary>
/// Was Pille und Infobereich zeigen, aus dem Stand der Steuerung (D09). Grundsatz: Freihand zeigt sich nur, solange es etwas
/// tut, und ruft nur, wenn du etwas tun musst. Kein „Bereit“ im Normalbetrieb. Ohne WPF, damit er prüfbar ist; die
/// Fenster lesen nur, was er liefert.
/// </summary>
public sealed class PillPresenter(bool sound)
{
    public static readonly TimeSpan DoneVisible = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan NoticeVisible = TimeSpan.FromSeconds(6);

    /// <summary>Ausgeblendete Zustände; ein neuer Zustand (neues Diktat, neue Meldung, andere Zahl) erscheint wieder.</summary>
    private readonly HashSet<string> _dismissed = [];

    private ControllerPhase _phase = ControllerPhase.Idle;

    public bool Sound { get; set; } = sound;

    /// <summary>Zeigt die Pille für diesen Stand; im Leerlauf gehalten vor Meldung vor fertig.</summary>
    public PillView Show(ControllerUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var details = Details(update);
        if (update.Phase != ControllerPhase.Idle)
        {
            // Stummes Mikrofon zählt nur während der Aufnahme, dort aber in jeder Hauptzeile
            var muted = update.Phase == ControllerPhase.Recording && update.Signal == MicrophoneSignal.Muted;
            if (update.FocusWait is { } wait)
            {
                return new(PillState.FocusAway, StateColor.Yellow, FocusText(wait, muted), details, null, false, muted);
            }

            return update.Phase switch
            {
                ControllerPhase.Starting => new(PillState.Starting, StateColor.Grey, "Startet …", details, null, false),
                ControllerPhase.Recording when muted => new(
                    PillState.Recording, StateColor.Yellow, MutedText, details, null, false, true),
                ControllerPhase.Recording => new(
                    PillState.Recording, StateColor.Red, update.ToDictationWindow ? "Aufnahme ins Diktatfenster" : "Aufnahme", details, null, false),
                _ => new(PillState.Finishing, StateColor.Red, "Wird fertig geschrieben …", details, null, false),
            };
        }

        if (update.HeldOpen > 0 && !_dismissed.Contains(HeldKey(update)))
        {
            return new(PillState.Held, StateColor.Yellow, $"{Count(update.HeldOpen)} gehalten – ins Diktatfenster holen", details, null, true);
        }

        if (IsFinal(update.Message) && !_dismissed.Contains(NoticeKey(update)))
        {
            var color = update.Message == ControllerMessage.Cancelled ? StateColor.Grey : StateColor.Yellow;
            return new(PillState.Notice, color, StatusText.Message(update.Message, update.Reason), details, NoticeVisible, false);
        }

        if (update.Dictation > 0 && update.Written > 0 && !_dismissed.Contains(DoneKey(update)))
        {
            var where = update.ToDictationWindow ? " ins Diktatfenster" : string.Empty;
            return new(PillState.Done, StateColor.Green, $"{Count(update.Written)}{where} geschrieben", details, DoneVisible, false);
        }

        return new(PillState.Hidden, StateColor.Grey, string.Empty, details, null, false);
    }

    /// <summary>
    /// Ausgeblendet (Zeit um oder angeklickt): derselbe Stand erscheint nicht wieder, ein neuer schon. Nur der genannte
    /// Zustand; zeigt die Pille inzwischen etwas anderes, bleibt das stehen.
    /// </summary>
    public void Dismiss(ControllerUpdate update, PillState state)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (_dismissed.Count > 64)
        {
            _dismissed.Clear();
        }

        if (Show(update).State != state)
        {
            return;
        }

        switch (state)
        {
            case PillState.Held:
                _dismissed.Add(HeldKey(update));
                break;
            case PillState.Notice or PillState.Done:
                // Eine Meldung und das Fertig desselben Diktats zusammen, sonst käme das eine nach dem anderen
                _dismissed.Add(NoticeKey(update));
                _dismissed.Add(DoneKey(update));
                break;
        }
    }

    /// <summary>Ton beim Wechsel in die Aufnahme und zurück in den Leerlauf; nur wenn eingeschaltet (D10).</summary>
    public Tone? ToneFor(ControllerUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var previous = _phase;
        _phase = update.Phase;
        if (!Sound)
        {
            return null;
        }

        if (update.Phase == ControllerPhase.Recording && previous is ControllerPhase.Idle or ControllerPhase.Starting)
        {
            return Tone.Start;
        }

        return update.Phase == ControllerPhase.Idle && previous is ControllerPhase.Recording or ControllerPhase.Stopping
            ? Tone.End
            : null;
    }

    /// <summary>Symbol im Infobereich: grau bereit, rot Aufnahme, gelb Hinweis (Variante A „Wellenrahmen“).</summary>
    public static StateColor TrayColor(ControllerUpdate update, int offers)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Phase != ControllerPhase.Idle)
        {
            // Stummes Mikrofon während der Aufnahme: gelb wie in der Pille
            return update.Phase == ControllerPhase.Recording && update.Signal == MicrophoneSignal.Muted ? StateColor.Yellow : StateColor.Red;
        }

        return update.HeldOpen > 0 || offers > 0 || update.Recognizer == RecognizerAvailability.Unavailable
            ? StateColor.Yellow
            : StateColor.Grey;
    }

    /// <summary>„Erkennung neu starten“ im Menü nur, solange der Erkenner nicht verfügbar ist und kein Diktat läuft.</summary>
    public static bool CanRestartRecognizer(ControllerUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return update.Recognizer == RecognizerAvailability.Unavailable && update.Phase == ControllerPhase.Idle;
    }

    /// <summary>Kurztext beim Überfahren des Symbols; Windows zeigt höchstens 127 Zeichen.</summary>
    public static string TrayText(ControllerUpdate update, int offers)
    {
        ArgumentNullException.ThrowIfNull(update);
        var state = update.Phase == ControllerPhase.Recording && update.Signal == MicrophoneSignal.Muted ? "Aufnahme, Mikrofon stumm"
            : update.Phase != ControllerPhase.Idle ? "Aufnahme"
            : update.HeldOpen > 0 ? $"{Count(update.HeldOpen)} gehalten"
            : offers > 0 ? $"{offers} gerettete Texte"
            : update.Recognizer switch
            {
                RecognizerAvailability.Ready => "bereit",
                RecognizerAvailability.Starting => "Erkenner lädt",
                RecognizerAvailability.Restarting => "Erkenner startet neu",
                _ => "Erkenner nicht verfügbar – im Menü neu starten",
            };
        var text = "Freihand – " + state;
        return text.Length <= 127 ? text : text[..127];
    }

    /// <summary>
    /// Regel für den Platz der Pille (Handtest vom 11.11.2026, Bedienregel): Sie hängt an der Ecke, die dem
    /// nächsten Bildschirmrand zugewandt ist. In der rechten Hälfte bleibt ihr rechter Rand fest, in der linken ihr linker,
    /// unten ihr unterer, oben ihr oberer; so wächst sie immer vom nächsten Rand weg. Massgebend ist die Mitte der Pille beim
    /// Loslassen nach dem Verschieben.
    /// </summary>
    public static PillAnchor AnchorOf(
        double left, double top, double width, double height, (double Left, double Top, double Right, double Bottom) area)
    {
        var fromLeft = left + (width / 2) < (area.Left + area.Right) / 2;
        var fromTop = top + (height / 2) < (area.Top + area.Bottom) / 2;
        return new PillAnchor(fromLeft ? left : left + width, fromTop ? top : top + height, fromLeft, fromTop);
    }

    /// <summary>
    /// Platz der Pille am Anker und immer ganz im Arbeitsbereich ihres Bildschirms; ist sie grösser als er, steht sie an
    /// seinem linken oberen Rand.
    /// </summary>
    public static (double Left, double Top) PlaceAt(
        PillAnchor anchor, double width, double height, (double Left, double Top, double Right, double Bottom) area)
    {
        var left = anchor.FromLeft ? anchor.X : anchor.X - width;
        var top = anchor.FromTop ? anchor.Y : anchor.Y - height;
        return (Math.Max(area.Left, Math.Min(left, area.Right - width)), Math.Max(area.Top, Math.Min(top, area.Bottom - height)));
    }

    /// <summary>Inhalt von <c>anzeige.txt</c>: der Anker der Pille, ohne Texte.</summary>
    public static string FormatAnchor(PillAnchor anchor) => string.Create(CultureInfo.InvariantCulture,
        $"# Platz der Pille: fester Rand nach dem nächsten Bildschirmrand (keine Texte)\npille_anker = {anchor.X:0};{anchor.Y:0};{(anchor.FromLeft ? "links" : "rechts")};{(anchor.FromTop ? "oben" : "unten")}\n");

    /// <summary>Liest den Anker aus <c>anzeige.txt</c>; null, wenn er fehlt oder nicht lesbar ist (dann Standardplatz).</summary>
    public static PillAnchor? ParseAnchor(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var line = lines.FirstOrDefault(text => text.StartsWith("pille_anker", StringComparison.Ordinal));
        var equals = line?.IndexOf('=', StringComparison.Ordinal) ?? -1;
        var parts = equals < 0 ? [] : line![(equals + 1)..].Split(';', StringSplitOptions.TrimEntries);
        return parts is [var x, var y, var horizontal, var vertical] &&
            double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var anchorX) &&
            double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var anchorY) &&
            horizontal is "links" or "rechts" && vertical is "oben" or "unten"
                ? new PillAnchor(anchorX, anchorY, horizontal == "links", vertical == "oben")
                : null;
    }

    /// <summary>Standardplatz: unten rechts über dem Infobereich, 8 Einheiten vom Rand.</summary>
    public static PillAnchor DefaultAnchor((double Left, double Top, double Right, double Bottom) area) =>
        new(area.Right - 8, area.Bottom - 8, FromLeft: false, FromTop: false);

    /// <summary>Nur was ein Diktat beendet oder verhindert oder eine Handlung meldet; Hinweise aus dem Diktat nicht.</summary>
    private static bool IsFinal(ControllerMessage message) => message is not (
        ControllerMessage.None or ControllerMessage.Notice or ControllerMessage.CommandNotSupported or ControllerMessage.CommandHint);

    private static List<string> Details(ControllerUpdate update) =>
    [
        "Ziel: " + (update.Target ?? "–"),
        "Mikrofon: " + (update.Microphone ?? "–"),
        "Erkennung: " + update.Recognizer switch
        {
            RecognizerAvailability.Ready => "bereit",
            RecognizerAvailability.Starting => "lädt",
            RecognizerAvailability.Restarting => "startet neu",
            _ => "nicht verfügbar",
        } + (update.Mode is { } mode ? $" · {(mode.Mode == Core.Live.LiveMode.Live ? "live" : "satzweise")}" : string.Empty),
        $"Geschrieben {update.Written} · gehalten {update.HeldOpen}",
    ];

    private static string Count(int count) => count == 1 ? "1 Abschnitt" : $"{count.ToString(CultureInfo.CurrentCulture)} Abschnitte";

    private const string MutedText = "Mikrofon ist stumm geschaltet";

    /// <summary>Hauptzeile, solange die Ausgabe auf den Fokus wartet; dieselbe für Pille und Countdown im Fenster.</summary>
    public static string FocusText(TimeSpan remaining, bool muted) =>
        $"Zurück ins Feld klicken · {Seconds(remaining)} s" + (muted ? $" · {MutedText}" : string.Empty);

    private static string Seconds(TimeSpan value) =>
        Math.Max(0, Math.Ceiling(value.TotalSeconds)).ToString("0", CultureInfo.CurrentCulture);

    private static string HeldKey(ControllerUpdate update) => $"held:{update.Dictation}:{update.HeldOpen}";

    private static string NoticeKey(ControllerUpdate update) => $"notice:{update.MessageNumber}";

    private static string DoneKey(ControllerUpdate update) => $"done:{update.Dictation}";
}
