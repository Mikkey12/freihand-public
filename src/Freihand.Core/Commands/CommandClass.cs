namespace Freihand.Core.Commands;

/// <summary>Die drei Klassen gesprochener Steuerung (Grundentscheid 5, Anhang A).</summary>
public enum CommandClass
{
    /// <summary>Wirkt ohne Befehlswort und erzeugt nur Zeichen im Text (BA-20).</summary>
    DictationMark,

    /// <summary>Anweisung an die App über Befehlstaste oder Befehlswort (BA-07, BE-19).</summary>
    Command,

    /// <summary>Wirkung ausserhalb des Textes, etwa Senden; braucht eine bewusste Freigabe (SI-09).</summary>
    Action,
}
