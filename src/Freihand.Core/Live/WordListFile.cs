using System.Text;

namespace Freihand.Core.Live;

/// <summary>Gelesene Wortliste: wirksame Wörter und je verworfene Zeile ein Grund (ohne den Text der Zeile).</summary>
public sealed record WordListDocument(IReadOnlyList<LearnedWord> Words, IReadOnlyList<string> Problems);

/// <summary>
/// Die lesbaren Wortlisten (P11 S4). Eine Zeile je Wort: <c>Richtige Schreibweise: gehört, gehört</c>. Zeilen mit <c>#</c>
/// am Anfang und leere Zeilen zählen nicht. Eine Schreibweise ohne gehörte Schreibweisen (<c>Nivella:</c>) ersetzt nichts;
/// in <c>woerter.txt</c> schaltet sie so ein gelerntes Wort ab. Eine fehlerhafte Zeile wird übergangen und gemeldet, die
/// übrigen gelten weiter. Wirksam ist eine Zeile nur, wenn <see cref="Parse"/> sie als Wort liefert; dieselbe Regel gilt beim
/// Anfügen (Codex zu PR #66).
/// </summary>
public static class WordListFile
{
    public const int MaxWords = 500;
    public const int MaxLength = 100;

    /// <summary>Kopf von <c>woerter.txt</c>, der Datei der Nutzerin oder des Nutzers; Freihand legt sie nur an, schreibt sie nie.</summary>
    public const string Header =
        "# Freihand: eigene Wörter. Eine Zeile je Wort, zuerst die richtige Schreibweise, nach dem Doppelpunkt\n" +
        "# die falsch gehörten Schreibweisen, durch Kommas getrennt. Beispiel:\n" +
        "#   Nivella: Niwela, Nivela\n" +
        "# Freihand ersetzt nur ganze Wörter. Änderungen gelten ab dem nächsten Diktat.\n" +
        "# Gelernte Wörter stehen in woerter-gelernt.txt. Eine Zeile hier hat Vorrang vor einer gelernten Zeile mit\n" +
        "# derselben Schreibweise; nur die Schreibweise mit Doppelpunkt (z. B. „Nivella:“) schaltet sie ab.\n";

    /// <summary>Kopf von <c>woerter-gelernt.txt</c>; diese Datei schreibt nur Freihand.</summary>
    public const string LearnedHeader =
        "# Freihand: gelernte Wörter. Diese Datei schreibt Freihand beim Lernen; bitte nicht von Hand ändern.\n" +
        "# Ändern oder abschalten in woerter.txt: Dort hat eine Zeile mit derselben Schreibweise Vorrang.\n";

    public static WordListDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = Scan(text);
        return new WordListDocument(
            [.. lines.Where(line => line.Word is not null).Select(line => line.Word!)],
            [.. lines.Where(line => line.Problem is not null).Select(line => line.Problem!)]);
    }

    /// <summary>Kopf und ein Wort je Zeile; Kommentare und fehlerhafte Zeilen der alten Datei gehen dabei verloren.</summary>
    public static string Format(IEnumerable<LearnedWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var text = new StringBuilder(Header);
        foreach (var word in words)
        {
            text.Append(word.Spelling).Append(": ").Append(string.Join(", ", word.Heard)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Fügt ein gelerntes Wort an. Alle übrigen Zeilen bleiben Zeichen für Zeichen, samt Zeilenenden und Leerzeilen. Gibt es
    /// die Schreibweise als wirksames Wort, kommen nur neue gehörte Schreibweisen zu dessen Zeile; sonst wird eine Zeile
    /// angehängt, mit dem Zeilenende, das die Datei schon verwendet. Eine leere Datei bekommt den Kopf der gelernten Wörter.
    /// </summary>
    /// <exception cref="InvalidOperationException">Die Liste hat schon <see cref="MaxWords"/> wirksame Wörter.</exception>
    public static string Add(string existing, LearnedWord word)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(word);
        if (Invalid(word.Spelling) is not null || word.Heard.Count == 0 || word.Heard.Any(item => Invalid(item) is not null))
        {
            throw new ArgumentException("Ungültiges Wort.", nameof(word));
        }

        var text = existing.Length == 0 ? LearnedHeader : existing;
        var lines = Scan(text);
        if (lines.FirstOrDefault(line => line.Word?.Spelling == word.Spelling) is { } found)
        {
            var heard = found.Word!.Heard.ToList();
            heard.AddRange(word.Heard.Where(item => !heard.Contains(item, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal));
            return text[..found.Start] + word.Spelling + ": " + string.Join(", ", heard) + text[(found.Start + found.Length)..];
        }

        if (lines.Count(line => line.Word is not null) >= MaxWords)
        {
            throw new InvalidOperationException($"Die Wörterliste hat schon {MaxWords} Wörter.");
        }

        var ending = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = text.EndsWith('\n') ? string.Empty : ending;
        return text + separator + word.Spelling + ": " + string.Join(", ", word.Heard.Distinct(StringComparer.Ordinal)) + ending;
    }

    /// <summary>Grund, warum eine Schreibweise nicht in die Datei passt, sonst null.</summary>
    public static string? Invalid(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Trim().Length == 0)
        {
            return "ist leer";
        }

        if (text.Length > MaxLength)
        {
            return $"ist länger als {MaxLength} Zeichen";
        }

        if (text.Any(c => char.IsControl(c) || c is ':' or ',' or '#'))
        {
            return "enthält Doppelpunkt, Komma, # oder ein Steuerzeichen";
        }

        return WordComparison.PhraseKeys(text).All(key => key.Length == 0) ? "besteht nur aus Satzzeichen" : null;
    }

    /// <summary>
    /// Eine Zeile mit Lage ihres Inhalts (ohne Zeilenende) und entweder dem wirksamen Wort oder dem Grund, warum sie nicht
    /// zählt; Kommentare und Leerzeilen haben beides nicht.
    /// </summary>
    private sealed record Line(int Start, int Length, LearnedWord? Word, string? Problem);

    /// <summary>Die eine Regel, welche Zeile wirksam ist; Parse und Add verwenden sie beide.</summary>
    private static List<Line> Scan(string text)
    {
        var lines = new List<Line>();
        var spellings = new HashSet<string>(StringComparer.Ordinal);
        var words = 0;
        var number = 0;
        for (var start = 0; start < text.Length;)
        {
            number++;
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            var content = text[start..end].TrimEnd('\r', '\n');
            lines.Add(Read(start, content, number));
            start = end;
        }

        return lines;

        Line Read(int start, string content, int number)
        {
            var line = content.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                return new Line(start, content.Length, null, null);
            }

            string? problem = null;
            LearnedWord? word = null;
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                problem = "Doppelpunkt fehlt.";
            }
            else
            {
                var spelling = line[..colon].Trim();
                var heard = line[(colon + 1)..].Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
                if (Invalid(spelling) is { } reason)
                {
                    problem = $"Schreibweise {reason}.";
                }
                else if (heard.Select(Invalid).FirstOrDefault(item => item is not null) is { } bad)
                {
                    problem = $"gehörte Schreibweise {bad}.";
                }
                else if (spellings.Contains(spelling))
                {
                    problem = "Schreibweise steht schon weiter oben.";
                }
                else if (words == MaxWords)
                {
                    problem = $"mehr als {MaxWords} Wörter.";
                }
                else
                {
                    spellings.Add(spelling);
                    words++;
                    word = new LearnedWord(spelling, heard);
                }
            }

            return new Line(start, content.Length, word, problem is null ? null : $"Zeile {number}: {problem}");
        }
    }
}
