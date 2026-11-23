using Freihand.Core.Commands;
using Freihand.Core.Recognition;

namespace Freihand.Core.Live;

/// <summary>Was ein gehörter Vorschlag beim Lernen ist (P11 S4).</summary>
public enum CandidateKind
{
    /// <summary>Richtig erkannt; keine Ersetzung nötig.</summary>
    Same,

    /// <summary>Kein gewöhnliches Wort: wird ersetzt, vorgewählt.</summary>
    New,

    /// <summary>Gewöhnliches Wort (die Rechtschreibprüfung kennt jedes Wort darin): nur ausdrücklich, nicht vorgewählt.</summary>
    Ordinary,

    /// <summary>Keine Rechtschreibprüfung verfügbar: nur ausdrücklich, nicht vorgewählt.</summary>
    Unchecked,
}

/// <summary>
/// Vorschläge aus den gehörten Lernsätzen; <paramref name="Unmatched"/> Sätze passten nicht zur Vorlage,
/// <paramref name="Unusable"/> gehörte Formen würden beim Diktat nicht ersetzt und sind darum nicht dabei.
/// </summary>
public sealed record LearningResult(IReadOnlyList<WordCandidate> Candidates, int Unmatched, int Unusable);

/// <summary>Ein gehörter Vorschlag mit Anzahl und Vorwahl.</summary>
public sealed record WordCandidate(string Text, int Count, CandidateKind Kind)
{
    /// <summary>Vorgewählt ist nur, was sicher kein gewöhnliches Wort ist.</summary>
    public bool Preselected => Kind == CandidateKind.New;
}

/// <summary>
/// Vorschläge aus dem Gehörten (P11 S4). Regel: Ersetzungen dürfen keine richtigen Wörter treffen. Darum ist ein Vorschlag
/// nur vorgewählt, wenn die Rechtschreibprüfung von Windows mindestens ein Wort darin nicht kennt; sonst ist eine
/// ausdrückliche Auswahl erforderlich. Kommas im Gehörten trennen Vorschläge (mehrmals ohne Pause gesprochen).
/// </summary>
public static class WordCandidates
{
    /// <summary>Platzhalter für das Wort in einem Lernsatz.</summary>
    public const string Gap = "…";

    /// <summary>
    /// Lernsätze (Handtest vom 18.11.2026): Ein Wort allein gibt Whisper keinen Zusammenhang, es rät dann frei (oft
    /// englisch); im Satz entsteht die Fehlschreibung, die auch beim Diktat vorkommt. Die Sätze passen zu Namen, Orten und
    /// Begriffen.
    /// </summary>
    public static IReadOnlyList<string> Sentences { get; } =
    [
        "Heute habe ich … erwähnt.",
        "Ich schreibe … in die Notiz.",
        "Gestern ging es um … beim Mittagessen.",
    ];

    /// <summary>
    /// Vorschläge aus gehörten Lernsätzen (P11 S4). Regel 4 „Lernen = Ersetzen“ (Codex zu PR #66): Gearbeitet wird mit den
    /// Wörtern, wie die Erkennung sie geliefert hat, nie mit fertigem Text (dessen Fügeregel Wortgrenzen verwischt). Ein
    /// Vorschlag gilt nur, wenn der Diktat-Code mit denselben Befehlen genau diese Äusserung mit ihm so ersetzt, als stünde
    /// an der Lücke die Schreibweise; was schon richtig erkannt wurde, wird ohne Ersetzung gezeigt.
    /// </summary>
    public static LearningResult FromUtterances(
        string spelling, IEnumerable<Utterance> heard, CommandSet commands, Func<string, bool?> isKnownWord) =>
        FromUtterances(spelling, heard, commands, null, isKnownWord);

    /// <param name="options">
    /// Auslegung wie beim Diktat (Befehlswort, Varianten); Formatter und Ersetzungen werden hier immer weggelassen. Ohne
    /// Angabe die Standardauslegung der App.
    /// </param>
    public static LearningResult FromUtterances(
        string spelling, IEnumerable<Utterance> heard, CommandSet commands, InterpreterOptions? options,
        Func<string, bool?> isKnownWord)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(isKnownWord);
        var plain = (options ?? new InterpreterOptions(null, TimeSpan.FromSeconds(5), new Dictionary<string, string>(), null))
            with { Formatter = null, Replacements = WordReplacements.Empty };
        var items = new List<(string Text, bool Same)>();
        var unmatched = 0;
        var unusable = 0;
        foreach (var utterance in heard)
        {
            if (Sentences.Select(sentence => Find(sentence, utterance.Words)).FirstOrDefault(found => found is not null) is not { } gap)
            {
                unmatched++;
            }
            else if (WordListFile.Invalid(spelling) is not null)
            {
                unusable++;
            }
            else if (WritesAtGap(commands, plain, spelling, utterance, gap, WordReplacements.Empty))
            {
                // Richtig erkannt heisst: Das Diktat schreibt genau die Schreibweise (eigene Vorprüfung zu PR #66)
                items.Add((gap.Text, true));
            }
            else if (WordListFile.Invalid(gap.Text) is null
                && WritesAtGap(commands, plain, spelling, utterance, gap, WordReplacements.From([new LearnedWord(spelling, [gap.Text])])))
            {
                items.Add((gap.Text, false));
            }
            else
            {
                unusable++;
            }
        }

        return new LearningResult(Build(items, isKnownWord), unmatched, unusable);
    }

    /// <summary>
    /// Was an der Lücke eines Lernsatzes gehört wurde, aus Text (für Anzeige und Tests; das Lernen selbst arbeitet mit den
    /// Wörtern der Erkennung, <see cref="FromUtterances"/>).
    /// </summary>
    public static string? InGap(string sentence, string heard)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        ArgumentNullException.ThrowIfNull(heard);

        // Ein gesprochener Umbruch ginge beim Aufteilen in Wörter als Leerraum verloren (Codex zu PR #66)
        if (heard.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            return null;
        }

        var words = heard.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select((token, k) => new RecognizedWord(token, TimeSpan.FromSeconds(k), TimeSpan.FromSeconds(k + 0.5), 1)).ToList();
        return Find(sentence, words)?.Text;
    }

    /// <summary>Lage der Lücke in den Einheiten einer Äusserung und das Gehörte dort, ohne Randzeichen.</summary>
    private sealed record GapFound(string Text, IReadOnlyList<WordUnit> Units, int Start, int Length);

    /// <summary>
    /// Die Lücke eines Lernsatzes in den Wörtern der Erkennung: Beginnen sie mit den Wörtern vor der Lücke und enden sie mit
    /// denen danach (verglichen wie Befehlswendungen), ist der Rest dazwischen das Wort. Sonst null. Das Gehörte muss ein
    /// einziger Satz sein (nur das letzte Wort darf mit . ! ? … enden), darf keinen Umbruch enthalten und innen keine
    /// Satzzeichen (<see cref="WordReplacements.WithoutInnerMarks"/>).
    /// </summary>
    private static GapFound? Find(string sentence, IReadOnlyList<RecognizedWord> words)
    {
        var gap = sentence.IndexOf(Gap, StringComparison.Ordinal);
        if (gap < 0)
        {
            throw new ArgumentException("Der Lernsatz hat keine Lücke.", nameof(sentence));
        }

        if (words.Any(word => word.Text.AsSpan().IndexOfAny('\r', '\n') >= 0))
        {
            return null;
        }

        var before = WordComparison.PhraseKeys(sentence[..gap]).Where(key => key.Length > 0).ToList();
        var after = WordComparison.PhraseKeys(sentence[(gap + Gap.Length)..]).Where(key => key.Length > 0).ToList();
        var units = WordComparison.Units(words).Where(unit => unit.Key.Length > 0).ToList();
        var texts = units.Select(unit => string.Concat(unit.Words.Select(word => word.Text))).ToList();
        if (texts.SkipLast(1).Select(text => text.TrimEnd('"', '“', '”', '»', '«', '›', '‹', '„'))
            .Any(text => text.Length > 0 && SentenceEnds.Contains(text[^1])))
        {
            return null;
        }

        var length = units.Count - before.Count - after.Count;
        if (length <= 0
            || !units.Take(before.Count).Select(unit => unit.Key).SequenceEqual(before, StringComparer.Ordinal)
            || !units.Skip(units.Count - after.Count).Select(unit => unit.Key).SequenceEqual(after, StringComparer.Ordinal)
            || !WordReplacements.WithoutInnerMarks(units, before.Count, length))
        {
            return null;
        }

        var text = string.Join(' ', texts.Skip(before.Count).Take(length));
        text = text[WordComparison.LeadingMarks(text).Length..];
        text = text[..(text.Length - WordComparison.TrailingMarks(text).Length)];
        return new GapFound(text, units, before.Count, length);
    }

    /// <summary>
    /// Schreibt der Diktat-Code (diese Auslegung ohne Formatter, in beiden Geltungsbereichen) mit diesen Ersetzungen genau
    /// diese Äusserung so, als stünde an der Lücke die Schreibweise als fertiger Text? Das Soll entsteht mit einem
    /// Platzhalter an der Lücke, der danach durch die Schreibweise samt Randzeichen ersetzt wird; die Schreibweise selbst
    /// geht nie durch die Befehlserkennung (Codex zu PR #66: sonst gälte „Komma“ als richtig erkannt, obwohl „,“ dasteht).
    /// </summary>
    private static bool WritesAtGap(
        CommandSet commands, InterpreterOptions plain, string spelling, Utterance utterance, GapFound gap, WordReplacements replacements)
    {
        var first = gap.Units[gap.Start];
        var last = gap.Units[gap.Start + gap.Length - 1];
        var from = IndexOf(utterance.Words, first.Words[0]);
        var to = IndexOf(utterance.Words, last.Words[^1]);
        var lead = WordComparison.LeadingMarks(string.Concat(first.Words.Select(word => word.Text)));
        var trail = WordComparison.TrailingMarks(string.Concat(last.Words.Select(word => word.Text)));
        var placeholder = lead + Placeholder + trail;
        var expectedWords = utterance.Words.Take(from)
            .Append(new RecognizedWord(placeholder, first.Words[0].Start, last.Words[^1].End, 1))
            .Concat(utterance.Words.Skip(to + 1)).ToList();
        var expected = utterance with { Words = expectedWords };
        var written = DictationInterpreter.WithMarks(lead, spelling, trail).Part;
        var learned = plain with { Replacements = replacements };
        foreach (var scope in new[] { CommandScope.Everywhere, CommandScope.DictationWindow })
        {
            if (new DictationInterpreter(commands, learned).Interpret(utterance, scope) is not [InsertText { Segment.Text: var actual }]
                || new DictationInterpreter(commands, plain).Interpret(expected, scope) is not [InsertText { Segment.Text: var wanted }]
                || wanted.Split(placeholder).Length != 2
                || actual != wanted.Replace(placeholder, written, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Platzhalter aus dem privaten Bereich: kein Wort, kein Diktatzeichen, vom Diktat-Code unverändert.</summary>
    private const string Placeholder = "\uE000";

    private static int IndexOf(IReadOnlyList<RecognizedWord> words, RecognizedWord word)
    {
        for (var k = 0; k < words.Count; k++)
        {
            if (ReferenceEquals(words[k], word))
            {
                return k;
            }
        }

        throw new InvalidOperationException("Wort nicht in der Äusserung.");
    }

    /// <summary>
    /// Vorschläge aus gehörten Texten: „richtig erkannt“ nur, wenn der Text genau die Schreibweise ist (Gross- und
    /// Kleinschreibung zählen); gezählt wird je genauem Text.
    /// </summary>
    /// <param name="isKnownWord">Kennt die Rechtschreibprüfung das Wort? null heisst: keine Prüfung verfügbar.</param>
    public static IReadOnlyList<WordCandidate> From(string spelling, IEnumerable<string> heard, Func<string, bool?> isKnownWord)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(isKnownWord);
        var own = WordComparison.Plain(spelling);
        return Build([.. heard.SelectMany(text => text.Split(',')).Select(WordComparison.Plain).Select(text => (text, text == own))],
            isKnownWord);
    }

    private static List<WordCandidate> Build(IEnumerable<(string Text, bool Same)> items, Func<string, bool?> isKnownWord)
    {
        var counts = new Dictionary<string, (bool Same, int Count)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (raw, same) in items)
        {
            var text = WordComparison.Plain(raw);
            if (WordListFile.Invalid(text) is not null)
            {
                continue;
            }

            if (counts.TryGetValue(text, out var entry))
            {
                counts[text] = (entry.Same && same, entry.Count + 1);
            }
            else
            {
                counts[text] = (same, 1);
                order.Add(text);
            }
        }

        return [.. order.Select(text =>
        {
            var (same, count) = counts[text];
            return new WordCandidate(text, count, same ? CandidateKind.Same : Kind(text, isKnownWord));
        })];
    }

    /// <summary>Ohne Leerraum und Satzzeichen am Rand, Leerraum innen auf ein Leerzeichen.</summary>
    public static string Clean(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var words = heard.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(WordComparison.TrimMarks).Where(word => word.Length > 0);
        return string.Join(' ', words);
    }

    private static readonly char[] SentenceEnds = ['.', '!', '?', '…'];

    private static CandidateKind Kind(string text, Func<string, bool?> isKnownWord)
    {
        var known = text.Split(' ').Select(isKnownWord).ToList();
        if (known.Any(item => item is null))
        {
            return CandidateKind.Unchecked;
        }

        return known.All(item => item == true) ? CandidateKind.Ordinary : CandidateKind.New;
    }
}
