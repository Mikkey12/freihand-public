namespace Freihand.Core.Commands;

/// <summary>
/// Die Pilot-Befehlsliste aus Anhang A, als Dokument im Schema nach OF-01. Befehle laufen über die
/// Befehlstaste (BE-19) oder das noch offene Befehlswort; Diktatzeichen wirken ohne beides (BA-20).
/// Aktionen gibt es im Pilot keine.
/// </summary>
public static class BuiltInCommands
{
    public const string Json = """
        {
          "kind": "freihand.commands",
          "schemaVersion": 1,
          "commands": [
            {
              "id": "comma",
              "class": "dictationMark",
              "phrases": [
                "Komma"
              ],
              "scope": "everywhere",
              "effect": "insertPunctuation",
              "text": ",",
              "description": "setzt ein Komma"
            },
            {
              "id": "period",
              "class": "dictationMark",
              "phrases": [
                "Punkt"
              ],
              "scope": "everywhere",
              "effect": "insertPunctuation",
              "text": ".",
              "description": "setzt einen Punkt"
            },
            {
              "id": "question-mark",
              "class": "dictationMark",
              "phrases": [
                "Fragezeichen"
              ],
              "scope": "everywhere",
              "effect": "insertPunctuation",
              "text": "?",
              "description": "setzt ein Fragezeichen"
            },
            {
              "id": "exclamation-mark",
              "class": "dictationMark",
              "phrases": [
                "Ausrufezeichen"
              ],
              "scope": "everywhere",
              "effect": "insertPunctuation",
              "text": "!",
              "description": "setzt ein Ausrufezeichen"
            },
            {
              "id": "colon",
              "class": "dictationMark",
              "phrases": [
                "Doppelpunkt"
              ],
              "scope": "everywhere",
              "effect": "insertPunctuation",
              "text": ":",
              "description": "setzt einen Doppelpunkt"
            },
            {
              "id": "new-line",
              "class": "dictationMark",
              "phrases": [
                "neue Zeile"
              ],
              "scope": "everywhere",
              "effect": "lineBreak",
              "description": "Zeilenumbruch, nie Senden; in fremden Programmen nach AU-13"
            },
            {
              "id": "new-paragraph",
              "class": "dictationMark",
              "phrases": [
                "neuer Absatz"
              ],
              "scope": "everywhere",
              "effect": "paragraphBreak",
              "description": "Absatz, nie Senden; in fremden Programmen nach AU-13"
            },
            {
              "id": "literal-mode",
              "class": "command",
              "phrases": [
                "wörtlich schreiben"
              ],
              "scope": "everywhere",
              "effect": "enterLiteralMode",
              "description": "schaltet Befehle, Diktatzeichen und Umformung ab; aus nur über die Befehlstaste"
            },
            {
              "id": "delete-last-word",
              "class": "command",
              "phrases": [
                "letztes Wort löschen"
              ],
              "scope": "dictationWindow",
              "effect": "deleteLastWord",
              "description": "löscht das letzte Wort"
            },
            {
              "id": "delete-last-sentence",
              "class": "command",
              "phrases": [
                "letzten Satz löschen"
              ],
              "scope": "dictationWindow",
              "effect": "deleteLastSentence",
              "description": "löscht den letzten Satz"
            },
            {
              "id": "undo",
              "class": "command",
              "phrases": [
                "rückgängig"
              ],
              "scope": "dictationWindow",
              "effect": "undo",
              "description": "nimmt den letzten Abschnitt zurück"
            },
            {
              "id": "commit",
              "class": "command",
              "phrases": [
                "übernehmen"
              ],
              "scope": "dictationWindow",
              "effect": "commit",
              "description": "fügt den festen Text nach Zielprüfung ins gebundene Ziel ein"
            },
            {
              "id": "rebind",
              "class": "command",
              "phrases": [
                "neu binden"
              ],
              "scope": "everywhere",
              "effect": "rebind",
              "description": "bindet das aktuelle Eingabefeld als Ziel"
            },
            {
              "id": "cancel",
              "class": "command",
              "phrases": [
                "abbrechen"
              ],
              "scope": "everywhere",
              "effect": "cancel",
              "description": "beendet die Aufnahme und verwirft Ausstehendes"
            }
          ]
        }
        """;

    private static readonly Lazy<CommandSet> Loaded = new(Load);

    /// <summary>Die geprüfte Pilot-Befehlsliste.</summary>
    public static CommandSet Pilot => Loaded.Value;

    private static CommandSet Load()
    {
        var result = CommandSetSerializer.Import(Json);
        return result.Succeeded
            ? result.Value
            : throw new InvalidOperationException(
                "Die eingebaute Befehlsliste verletzt das Schema: " + string.Join("; ", result.Errors));
    }
}
