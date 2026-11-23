using Freihand.Core.Commands;

namespace Freihand.Core.Live;

/// <summary>
/// Gesprochene Wendungen der Befehlsliste in Vergleichsform (Kapitel 5.2 und 5.6). Erkennungsvarianten aus
/// <see cref="InterpreterOptions.PhraseVariants"/> gelten als die gepflegte Wendung, auf die sie zeigen.
/// </summary>
internal sealed class PhraseBook
{
    private readonly List<(string[] Keys, CommandDefinition Command)> _entries;

    public PhraseBook(IEnumerable<CommandDefinition> commands, IReadOnlyDictionary<string, string> variants)
    {
        var maintained = new List<(string[] Keys, CommandDefinition Command)>();
        foreach (var command in commands)
        {
            foreach (var phrase in command.Phrases)
            {
                var keys = WordComparison.PhraseKeys(phrase);
                if (keys.Length > 0)
                {
                    maintained.Add((keys, command));
                }
            }
        }

        var entries = new List<(string[] Keys, CommandDefinition Command)>(maintained);
        foreach (var (variant, target) in variants)
        {
            var keys = WordComparison.PhraseKeys(variant ?? string.Empty);
            var targetKeys = WordComparison.PhraseKeys(target ?? string.Empty);
            if (keys.Length == 0)
            {
                continue;
            }

            foreach (var (phraseKeys, command) in maintained)
            {
                if (phraseKeys.SequenceEqual(targetKeys, StringComparer.Ordinal))
                {
                    entries.Add((keys, command));
                }
            }
        }

        // Die längste Wendung zuerst (Kapitel 5.6), sonst in der Reihenfolge der Liste
        _entries = [.. entries.OrderByDescending(entry => entry.Keys.Length)];
    }

    /// <summary>Der Eintrag, dessen Wendung genau diese Einheiten bildet.</summary>
    public CommandDefinition? Whole(IReadOnlyList<string> keys) =>
        _entries.FirstOrDefault(entry => entry.Keys.SequenceEqual(keys, StringComparer.Ordinal)).Command;

    /// <summary>Die längste Wendung, die bei <paramref name="index"/> beginnt, und wie viele Einheiten sie umfasst.</summary>
    public (CommandDefinition Command, int Length)? At(IReadOnlyList<string> keys, int index, Func<CommandDefinition, bool> applies)
    {
        foreach (var (phrase, command) in _entries)
        {
            if (index + phrase.Length <= keys.Count && applies(command) && Matches(keys, index, phrase))
            {
                return (command, phrase.Length);
            }
        }

        return null;
    }

    private static bool Matches(IReadOnlyList<string> keys, int index, string[] phrase)
    {
        for (var i = 0; i < phrase.Length; i++)
        {
            if (!string.Equals(keys[index + i], phrase[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
