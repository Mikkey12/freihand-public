using Freihand.Core.Commands;
using Freihand.Core.Formatting;
using Freihand.Core.Segments;

namespace Freihand.Core.Live;

// Verträge aus dem P3-Konzept (Vertrag 5). Ergebnisse eines Sprachmodells laufen nie hier durch (SI-09).

/// <param name="CommandWord">Grundentscheid 5; leer heisst nur Befehlstaste.</param>
/// <param name="ArmTimeout">Befehlstaste verfällt ohne Äusserung, Startwert 5 s.</param>
/// <param name="PhraseVariants">Erkennungsvarianten als Daten, z. B. „Koma“ → „Komma“.</param>
/// <param name="Formatter">P4; leer heisst ohne Formatierung.</param>
public sealed record InterpreterOptions(
    string? CommandWord,
    TimeSpan ArmTimeout,
    IReadOnlyDictionary<string, string> PhraseVariants,
    SwissTextFormatter? Formatter)
{
    /// <summary>Gelernte Wörter (P11 S4): ersetzt im Diktat, nicht im Literalmodus und nicht in Befehlen.</summary>
    public WordReplacements Replacements { get; init; } = WordReplacements.Empty;
}

/// <summary>Ergebnis der Auslegung einer Äusserung.</summary>
public abstract record DictationStep;

/// <summary>Endgültiger Text, fest, mit Kennung und Zeit.</summary>
public sealed record InsertText(TextSegment Segment) : DictationStep;

public sealed record RunCommand(CommandDefinition Command, TimeSpan Start, TimeSpan End) : DictationStep;

/// <summary>Kein Befehl erkannt oder hier nicht gültig: sichtbarer Hinweis, nichts ausgeführt, kein Text.</summary>
public sealed record CommandHint(Utterance Utterance, string Reason) : DictationStep;

/// <summary>
/// Befehle, Befehlswort, Diktatzeichen und Literalmodus je ganzer Äusserung (P3-Konzept, Kapitel 5.6). Die
/// Befehlstaste wird mit ihrer Sitzungszeit vorgemerkt und erst ausgewertet, wenn alle Äusserungen, die vorher
/// beginnen, ausgelegt sind (Befund F02).
/// </summary>
public sealed class DictationInterpreter
{
    private readonly Lock _gate = new();
    private readonly InterpreterOptions _options;
    private readonly PhraseBook _commands;
    private readonly PhraseBook _marks;
    private readonly string[] _commandWord;
    private readonly List<TimeSpan> _keys = [];
    private TimeSpan? _armedAt;
    private bool _literal;

    public DictationInterpreter(CommandSet commands, InterpreterOptions options)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.PhraseVariants);
        if (options.ArmTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ArmTimeout, "ArmTimeout muss positiv sein.");
        }

        _options = options;

        // Mit Taste oder Befehlswort gelten Befehle und Aktionen; Diktatzeichen wirken ohne beides (BA-20)
        _commands = new PhraseBook(
            commands.Commands.Where(command => command.Class != CommandClass.DictationMark), options.PhraseVariants);
        _marks = new PhraseBook(commands.OfClass(CommandClass.DictationMark), options.PhraseVariants);
        _commandWord = string.IsNullOrWhiteSpace(options.CommandWord) ? [] : WordComparison.PhraseKeys(options.CommandWord);
    }

    /// <summary>Stand nach der zuletzt ausgelegten Äusserung.</summary>
    public bool LiteralMode
    {
        get
        {
            lock (_gate)
            {
                return _literal;
            }
        }
    }

    /// <summary>Merkt die Befehlstaste mit Sitzungszeit vor (BE-19, BA-22).</summary>
    public void PressCommandKey(TimeSpan at)
    {
        lock (_gate)
        {
            var index = _keys.FindLastIndex(key => key <= at);
            _keys.Insert(index + 1, at);
        }
    }

    public IReadOnlyList<DictationStep> Interpret(Utterance utterance, CommandScope scope)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        ArgumentNullException.ThrowIfNull(utterance.Words);
        lock (_gate)
        {
            // Jede Äusserung wird mit dem Modus zu ihrem Beginn ausgelegt: erst die Tasten davor (Kapitel 5.6, T54)
            ApplyKeysUntil(utterance.Start);
            var armed = _armedAt is { } armedAt && utterance.Start - armedAt <= _options.ArmTimeout;

            // Die Aktivierung gilt für genau eine Äusserung, auch eine ohne Wörter (Kapitel 5.1, Regel 2; T51)
            _armedAt = null;
            if (utterance.Words.Count == 0)
            {
                return [];
            }

            if (_literal)
            {
                // Im Literalmodus keine Befehle, keine Diktatzeichen, keine Formatierung (BA-22, T14, T40)
                return Text(utterance, TextJoin.Join(utterance.Words.Select(word => word.Text)));
            }

            var units = WordComparison.Units(utterance.Words);
            var keys = units.Select(unit => unit.Key).ToList();
            if (armed)
            {
                return Command(utterance, keys, scope);
            }

            if (_commandWord.Length > 0 && keys.Count > _commandWord.Length
                && keys.Take(_commandWord.Length).SequenceEqual(_commandWord, StringComparer.Ordinal)
                && _commands.Whole(keys.Skip(_commandWord.Length).ToList()) is not null)
            {
                // Befehlswort am Äusserungsbeginn und danach genau eine Wendung: wie scharf geschaltet (T15, T37)
                return Command(utterance, keys.Skip(_commandWord.Length).ToList(), scope);
            }

            return Text(utterance, Dictate(units, keys, scope));
        }
    }

    /// <summary>Tasten bis zu dieser Sitzungszeit: im Literalmodus beendet eine Taste ihn, sonst schaltet sie scharf.</summary>
    private void ApplyKeysUntil(TimeSpan start)
    {
        while (_keys.Count > 0 && _keys[0] <= start)
        {
            var key = _keys[0];
            _keys.RemoveAt(0);
            if (_literal)
            {
                // Die Taste beendet den Literalmodus und schaltet dabei nichts scharf (Entscheid 2, T38)
                _literal = false;
                _armedAt = null;
            }
            else
            {
                _armedAt = key;
            }
        }
    }

    /// <summary>Scharf geschaltete Äusserung: genau ein Befehl, sonst ein sichtbarer Hinweis, nie Text (Entscheid 1).</summary>
    private List<DictationStep> Command(Utterance utterance, IReadOnlyList<string> keys, CommandScope scope)
    {
        if (_commands.Whole(keys) is not { } command)
        {
            return [new CommandHint(utterance, "Keine Befehlswendung erkannt.")];
        }

        if (command.Scope != CommandScope.Everywhere && command.Scope != scope)
        {
            return [new CommandHint(utterance, $"„{command.Phrases[0]}“ gilt hier nicht.")];
        }

        if (command.Effect == CommandEffect.EnterLiteralMode)
        {
            _literal = true;
        }

        return [new RunCommand(command, utterance.Start, utterance.End)];
    }

    /// <summary>
    /// Diktatzeichen wortweise, die längste Wendung zuerst; ein vom Modell schon gesetztes gleiches Zeichen direkt davor
    /// oder danach wird nicht verdoppelt (BA-20, T13). Danach formatiert P4 die ganze Äusserung (T19, T39).
    /// </summary>
    private string Dictate(List<WordUnit> units, List<string> keys, CommandScope scope)
    {
        var parts = new List<string>();
        var learned = new List<(int Start, int Length)>();
        for (var i = 0; i < units.Count;)
        {
            if (_marks.At(keys, i, mark => mark.Scope == CommandScope.Everywhere || mark.Scope == scope) is not { } found)
            {
                var start = i;
                if (keys[i].Length > 0 && _options.Replacements.At(keys, i, length =>
                        !Enumerable.Range(start + 1, length - 1).Any(k =>
                            _marks.At(keys, k, mark => mark.Scope == CommandScope.Everywhere || mark.Scope == scope) is not null)
                        && WordReplacements.WithoutInnerMarks(units, start, length)) is { } replaced)
                {
                    // Gelerntes Wort (P11 S4): die längste zulässige Folge, ohne Diktatzeichen und ohne Satzzeichen innen;
                    // Satzzeichen am Rand der ersetzten Folge bleiben stehen
                    var first = units[i].First.Text;
                    var last = string.Concat(units[i + replaced.Length - 1].Words.Select(word => word.Text));
                    var (lead, part) = WithMarks(WordComparison.LeadingMarks(first), replaced.Spelling, WordComparison.TrailingMarks(last));
                    parts.Add(part);

                    // TextJoin hängt jeden Teil unverändert hinten an: so liegt die Schreibweise im fertigen Text
                    var end = TextJoin.Join(parts).Length;
                    learned.Add((end - part.Length + lead, replaced.Spelling.Length));
                    i += replaced.Length;
                    continue;
                }

                parts.AddRange(units[i].Words.Select(word => word.Text));
                i++;
                continue;
            }

            var mark = MarkText(found.Command);
            var next = i + found.Length < units.Count ? units[i + found.Length].First.Text : string.Empty;
            var doubled = found.Command.Effect == CommandEffect.InsertPunctuation && mark.Length > 0
                && ((parts.Count > 0 && parts[^1].EndsWith(mark, StringComparison.Ordinal)) || next.StartsWith(mark, StringComparison.Ordinal));
            if (!doubled)
            {
                parts.Add(mark);
            }

            i += found.Length;
        }

        // Gelernte Schreibweisen sind für den Formatter geschützte Wörter (Codex zu PR #66)
        var text = TextJoin.Join(parts);
        return _options.Formatter is { } formatter ? formatter.Format(text, learned) : text;
    }

    /// <summary>
    /// Schreibweise mit den Randzeichen der ersetzten Folge; endet die Schreibweise schon mit demselben Zeichen (z. B. „Syn.“
    /// und „.“), steht es nur einmal da, ebenso am Anfang (Codex zu PR #66).
    /// </summary>
    /// <returns>Länge der Randzeichen davor und der ganze Teil.</returns>
    internal static (int Lead, string Part) WithMarks(string leading, string spelling, string trailing)
    {
        if (trailing.Length > 0 && spelling[^1] == trailing[0])
        {
            trailing = trailing[1..];
        }

        if (leading.Length > 0 && spelling[0] == leading[^1])
        {
            leading = leading[..^1];
        }

        return (leading.Length, leading + spelling + trailing);
    }

    private static string MarkText(CommandDefinition mark) => mark.Effect switch
    {
        CommandEffect.LineBreak => "\n",
        CommandEffect.ParagraphBreak => "\n\n",
        _ => mark.Text ?? string.Empty,
    };

    /// <summary>
    /// Ein fester Abschnitt mit der Kennung der Äusserung (T41). Am Rand kein Leerzeichen und kein Tabulator, Umbrüche
    /// bleiben (Kapitel 3).
    /// </summary>
    private static List<DictationStep> Text(Utterance utterance, string text)
    {
        var trimmed = text.Trim(' ', '\t');
        return trimmed.Length == 0
            ? []
            : [new InsertText(new TextSegment(utterance.Id, trimmed, SegmentState.Fixed, utterance.Start, utterance.End))];
    }
}
