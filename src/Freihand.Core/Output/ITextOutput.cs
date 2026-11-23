using Freihand.Core.Platform;
using Freihand.Core.Segments;

namespace Freihand.Core.Output;

/// <summary>Was mit einem Abschnitt bei der Ausgabe geschah.</summary>
public enum OutputResult
{
    /// <summary>Ins gebundene Ziel geschrieben.</summary>
    Written,

    /// <summary>Ziel abweichend oder unklar; der Text bleibt im Diktatfenster (AU-10).</summary>
    HeldInDictationWindow,

    /// <summary>Verweigert, z. B. Passwortfeld (SI-04) oder Umbruch in einzeiligem Feld (AU-13).</summary>
    Refused,
}

/// <summary>Grund einer gehaltenen oder verweigerten Ausgabe (P7-Konzept, Kapitel 5.7).</summary>
public enum OutputReason
{
    None,
    NotBound,
    FocusElsewhere,
    PasswordField,
    TargetGone,
    SecureDesktop,
    IdentityUnclear,
    Elevated,
    ReadOnly,
    LineBreakRefused,
    ControlCharacter,
    TextElementTooLong,
    InjectionBlocked,
    KeyReleaseFailed,
    PreviousHeld,
    KeysHeld,
    Cancelled,

    /// <summary>Die Bedingung vor dem ersten Zeichen galt nicht mehr (Beta B9: Markierung geändert); nichts geschrieben.</summary>
    ConditionFailed,
}

/// <param name="Written">
/// Was in den Eingabestrom des Ziels eingefügt wurde (erfolgreiche Injektion), samt einem vorangestellten Leerzeichen
/// der Fügeregel. Dass das Zielprogramm es so übernimmt (Zielkompatibilität), belegt nur WM04 je Ziel, nicht dieser Bericht.
/// </param>
/// <param name="Held">Der Rest des Abschnitts, der im Diktatfenster bleibt; leer, wenn alles eingefügt ist.</param>
public sealed record OutputReport(OutputResult Result, string Written, string Held, OutputReason Reason);

/// <summary>Eine Ausgabe als austauschbarer Baustein (AU-05). Nimmt nur festen Text an (BE-01).</summary>
public interface ITextOutput
{
    Task<OutputReport> WriteAsync(TextSegment segment, TargetInfo target, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ausgabe mit Bedingung (Beta B9, Codex zu PR #55): <paramref name="condition"/> wird nach jedem Warten (eigene Tasten,
/// Rückkehr des Fokus) unmittelbar vor dem ersten gesendeten Zeichen geprüft; gilt sie nicht, wird nichts geschrieben
/// (<see cref="OutputReason.ConditionFailed"/>). Ist das erste Zeichen gesendet, wird nach einer Unterbrechung nicht
/// weitergeschrieben, sondern der Rest gehalten: Ob die Stelle noch stimmt, lässt sich dann nicht mehr prüfen.
/// </summary>
public interface IConditionalTextOutput : ITextOutput
{
    Task<OutputReport> WriteAsync(TextSegment segment, TargetInfo target, Func<bool> condition, CancellationToken cancellationToken = default);
}

/// <summary>Warten auf die Rückkehr des Fokus: Beginn mit der Restzeit, Ende mit <c>null</c> (P9 Diktatfenster, Kapitel 3.5).</summary>
public sealed record FocusWait(Guid BindingId, TimeSpan? Remaining);

/// <summary>Meldet, wann die Ausgabe auf die Rückkehr des Fokus wartet, für die Anzeige „Zurück ins Feld klicken“.</summary>
public interface IFocusWaitReporter
{
    event EventHandler<FocusWait>? FocusWaitChanged;
}

/// <param name="KeyWaitLimit">Startwert 2 s.</param>
/// <param name="KeyWaitPoll">Startwert 15 ms: Abstand der Aufrufe von <see cref="IHotkeyService.OwnKeysReleased"/> beim Warten.</param>
/// <param name="PartDelay">Startwert 10 ms.</param>
/// <param name="ControlKeys">Sicherung für echte Tasten (Kapitel 5.8).</param>
public sealed record OutputOptions(TimeSpan KeyWaitLimit, TimeSpan KeyWaitPoll, TimeSpan PartDelay, ControlKeySafety ControlKeys)
{
    /// <summary>
    /// Verlässt der Fokus das gebundene Feld mitten in einem Abschnitt (z. B. eine Windows-Meldung), wartet die Ausgabe so
    /// lange, bis er ins selbe Feld zurückkehrt, und schreibt dann weiter; danach wird gehalten. 0 hält sofort.
    /// Die App setzt 60 s.
    /// </summary>
    public TimeSpan FocusReturnLimit { get; init; } = TimeSpan.Zero;

    /// <summary>Abstand der Prüfungen beim Warten auf die Rückkehr.</summary>
    public TimeSpan FocusReturnPoll { get; init; } = TimeSpan.FromMilliseconds(100);
}
