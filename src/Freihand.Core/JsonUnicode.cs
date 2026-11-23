using System.Globalization;

namespace Freihand.Core;

/// <summary>
/// Gemeinsame Unicode-Prüfung für jeden Eingang mit JSON-Text (Profile, Befehle, Messbericht): System.Text.Json
/// nimmt ein einzelnes Ersatzzeichen als Escape an, überspringt es in unbekannten Feldern und wirft erst beim Lesen
/// des Werts. Darum prüfen alle Eingänge den ganzen Text zuerst, damit der Fehler nicht von der Stelle abhängt
/// (Codex-Nachprüfung 4 zu PR #25, W01).
/// </summary>
internal static class JsonUnicode
{
    /// <summary>
    /// Ob der JSON-Text ein einzelnes Ersatzzeichen enthält: eine hohe Hälfte (U+D800 bis U+DBFF) ohne tiefe Hälfte
    /// danach oder eine tiefe Hälfte (U+DC00 bis U+DFFF) ohne hohe davor. Rohe Zeichen und Escapes in
    /// Zeichenketten zählen gleich; ein gültiges Paar ist erlaubt. Ein gemischtes Paar (eine Hälfte roh, eine als
    /// Escape) lässt diese Prüfung durch; es ist kein gültiger JSON-Text, und der Eingang weist es beim Umwandeln mit
    /// derselben Unicode-Meldung ab (JsonInputUnicodeMatrixTests).
    /// </summary>
    public static bool HasLoneSurrogate(string json)
    {
        var inString = false;
        var pendingHigh = false;
        for (var index = 0; index < json.Length; index++)
        {
            int unit = json[index];
            var quote = false;
            if (inString && unit == '\\' && index + 1 < json.Length)
            {
                if (json[index + 1] == 'u'
                    && index + 5 < json.Length
                    && int.TryParse(json.AsSpan(index + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var escaped))
                {
                    unit = escaped;
                    index += 5;
                }
                else
                {
                    // Ein anderer Escape (n, Anführungszeichen, Backslash ...) ist ein Zeichen ohne Ersatzhälfte.
                    if (pendingHigh)
                    {
                        return true;
                    }

                    index++;
                    continue;
                }
            }
            else
            {
                quote = unit == '"';
            }

            if (pendingHigh)
            {
                if (unit is < 0xDC00 or > 0xDFFF)
                {
                    return true;
                }

                pendingHigh = false;
                continue;
            }

            if (unit is >= 0xD800 and <= 0xDBFF)
            {
                pendingHigh = true;
            }
            else if (unit is >= 0xDC00 and <= 0xDFFF)
            {
                return true;
            }
            else if (quote)
            {
                inString = !inString;
            }
        }

        return pendingHigh;
    }
}
