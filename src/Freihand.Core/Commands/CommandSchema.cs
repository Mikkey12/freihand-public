using System.Globalization;
using Freihand.Core.Profiles;

namespace Freihand.Core.Commands;

/// <summary>
/// Das Datenformat für die Befehlsliste, Version 1 (OF-01). Das JSON-Schema dazu liegt unter
/// <c>schemas/commands.v1.schema.json</c>; <see cref="CommandSetSerializer"/> prüft zusätzlich die
/// Regeln über mehrere Felder (Klasse passend zur Wirkung, eindeutige Wendungen).
/// </summary>
public static class CommandSchema
{
    public const string Kind = "freihand.commands";

    public const int CurrentVersion = 1;

    public const string SchemaId = "urn:freihand:schema:commands:1";

    public const int MaxCommands = 128;

    public const int MaxPhrases = 8;

    public const int MaxPhraseLength = 48;

    public const int MaxDescriptionLength = 200;

    /// <summary>
    /// Wörter aus Buchstaben, getrennt durch je ein Leerzeichen, einen Bindestrich oder einen Apostroph. Die
    /// Fassung im JSON-Schema (Syntax von ECMA 262, <c>\p{L}</c> auf Codepunkten); der Code prüft mit
    /// <see cref="IsPhrase"/>, weil eine .NET-Regex ein Zeichen ausserhalb der Basisebene nicht als einen Buchstaben sieht.
    /// </summary>
    public const string PhrasePattern = @"^\p{L}+([ '\-]\p{L}+)*$";

    /// <summary>
    /// Ob der Text dem <see cref="PhrasePattern"/> entspricht: Wörter aus Buchstaben (Kategorien Lu, Ll, Lt, Lm, Lo,
    /// auch ausserhalb der Basisebene), getrennt durch genau ein Leerzeichen, einen Bindestrich oder einen
    /// Apostroph, kein Trennzeichen am Anfang oder Ende.
    /// </summary>
    public static bool IsPhrase(string value)
    {
        var afterLetter = false;
        for (var index = 0; index < value.Length; index += char.IsSurrogatePair(value, index) ? 2 : 1)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(value, index) is
                UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter)
            {
                afterLetter = true;
            }
            else if (afterLetter && value[index] is ' ' or '\'' or '-')
            {
                afterLetter = false;
            }
            else
            {
                return false;
            }
        }

        return afterLetter;
    }

    /// <summary>Satzzeichen, die ein Diktatzeichen setzen darf (BA-20).</summary>
    public static IReadOnlyList<string> Punctuation { get; } = [",", ".", "?", "!", ":", ";"];

    public static WireNameMap<CommandClass> Classes { get; } = new(
    [
        new(CommandClass.DictationMark, "dictationMark"),
        new(CommandClass.Command, "command"),
        new(CommandClass.Action, "action"),
    ]);

    public static WireNameMap<CommandScope> Scopes { get; } = new(
    [
        new(CommandScope.Everywhere, "everywhere"),
        new(CommandScope.DictationWindow, "dictationWindow"),
    ]);

    public static WireNameMap<CommandEffect> Effects { get; } = new(
    [
        new(CommandEffect.InsertPunctuation, "insertPunctuation"),
        new(CommandEffect.LineBreak, "lineBreak"),
        new(CommandEffect.ParagraphBreak, "paragraphBreak"),
        new(CommandEffect.EnterLiteralMode, "enterLiteralMode"),
        new(CommandEffect.DeleteLastWord, "deleteLastWord"),
        new(CommandEffect.DeleteLastSentence, "deleteLastSentence"),
        new(CommandEffect.Undo, "undo"),
        new(CommandEffect.Commit, "commit"),
        new(CommandEffect.Rebind, "rebind"),
        new(CommandEffect.Cancel, "cancel"),
    ]);

    /// <summary>
    /// Die Klasse, die zu einer Wirkung gehört (Grundentscheid 5). Diktatzeichen erzeugen nur Zeichen im
    /// Text, nie Tasten oder Aktionen (BA-20). Eine Wirkung der Klasse Aktion gibt es im Pilot nicht
    /// (Anhang A); ein Eintrag der Klasse Aktion wird darum abgewiesen.
    /// </summary>
    public static CommandClass ClassOf(CommandEffect effect) => effect switch
    {
        CommandEffect.InsertPunctuation or CommandEffect.LineBreak or CommandEffect.ParagraphBreak => CommandClass.DictationMark,
        _ => CommandClass.Command,
    };

    /// <summary>
    /// Der einzige zulässige Geltungsbereich einer Wirkung (Anhang A), oder <c>null</c>, wenn beide zulässig
    /// sind. Korrektur, Rückgängig und Übernehmen gelten nur im Diktatfenster: Löschen in fremden
    /// Programmen ist eine Aktion mit eigener Freigabe und nicht im Pilot (BA-05, AU-02, SI-09). „Wörtlich
    /// schreiben“ und „abbrechen“ gelten überall, damit aus ihnen keine Lücke entsteht (BA-22, BE-06).
    /// </summary>
    public static CommandScope? RequiredScope(CommandEffect effect) => effect switch
    {
        CommandEffect.DeleteLastWord or CommandEffect.DeleteLastSentence or CommandEffect.Undo or CommandEffect.Commit
            => CommandScope.DictationWindow,
        CommandEffect.EnterLiteralMode or CommandEffect.Cancel => CommandScope.Everywhere,
        _ => null,
    };

    /// <summary>Vergleichsform einer Wendung, um Doppelungen zu erkennen: Kleinbuchstaben, Normalform C.</summary>
    public static string NormalizePhrase(string phrase) =>
        phrase.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
}
