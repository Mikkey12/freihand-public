using Freihand.Adapters.Dictation;

namespace Freihand.App;

/// <summary>Klartext der Meldungszeile im Statusfenster; ohne WPF, damit er prüfbar ist.</summary>
public static class StatusText
{
    /// <summary>Meldung mit Grund; leer ohne Meldung und für Hinweise, die eine eigene Zeile haben.</summary>
    public static string Message(ControllerMessage message, string? reason) => message switch
    {
        ControllerMessage.None or ControllerMessage.Notice => string.Empty,
        // Bei Funk-Headsets meldet Windows das Entfernen erst nach dem Stopp (RM11, Entscheid vom 09.11.2026)
        ControllerMessage.CaptureStopped when reason == "NoSignal" => "Mikrofon beendet: kein Ton vom Mikrofon – Gerät getrennt?",
        // Beim Übernehmen in Worten, weil du dort handeln musst; sonst bleibt der Code wie bisher
        ControllerMessage.HandOverRefused when reason is not null => $"{Describe(message)}: {HandOverReason(reason)}",
        _ => $"{Describe(message)}{(reason is null ? string.Empty : $": {reason}")}",
    };

    /// <summary>Häufige Gründe beim Übernehmen in Worten; sonst bleibt der Code stehen, damit nichts verloren geht.</summary>
    private static string HandOverReason(string reason) => reason switch
    {
        "NotMarkedField" => "ein anderes Feld hat den Fokus",
        "NoFocus" => "kein Feld hat den Fokus",
        "OwnProcess" => "Freihand selbst hat den Fokus",
        "LineBreakRefused" => "Zeilenumbrüche sind für dieses Feld noch nicht freigegeben",
        "Busy" => "ein Diktat läuft",
        _ => reason,
    };

    private static string Describe(ControllerMessage message) => message switch
    {
        ControllerMessage.RecognizerNotReady => "Erkenner noch nicht bereit",
        ControllerMessage.RecognizerUnavailable => "Erkenner nicht verfügbar",
        ControllerMessage.BindRefused => "Feld nicht bindbar",
        ControllerMessage.GuardStopped => "Aufnahme beendet",
        ControllerMessage.GuardFailed => "Wächter nicht gestartet",
        ControllerMessage.NoDevice => "Kein Mikrofon",
        ControllerMessage.CaptureFailed => "Mikrofon nicht geöffnet",
        ControllerMessage.CaptureStopped => "Mikrofon beendet",
        ControllerMessage.DictationFailed => "Diktat abgebrochen",
        ControllerMessage.Cancelled => "Abgebrochen",
        ControllerMessage.HotkeyConflict => "Taste nicht angemeldet",
        ControllerMessage.ClipboardRefused => "Zwischenablage gesperrt",
        ControllerMessage.CommandNotSupported => "Befehl noch nicht unterstützt",
        ControllerMessage.CommandHint => "Kein gültiger Befehl erkannt",
        ControllerMessage.RecoveryFailed => "Sicherung gescheitert",
        ControllerMessage.HandOverRefused => "Nicht übernommen, Text bleibt im Diktatfenster",
        _ => string.Empty,
    };
}
