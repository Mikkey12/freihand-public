namespace Freihand.Core.Commands;

/// <summary>
/// Eine geprüfte Befehlsliste (Grundentscheid 5, Anhang A). Entsteht nur über
/// <see cref="CommandSetSerializer"/>, damit Bezeichner und Wendungen eindeutig sind.
/// </summary>
public sealed class CommandSet
{
    internal CommandSet(IReadOnlyList<CommandDefinition> commands) => Commands = commands;

    public IReadOnlyList<CommandDefinition> Commands { get; }

    public CommandDefinition? FindById(string id) => Commands.FirstOrDefault(command => command.Id == id);

    /// <summary>Einträge einer Klasse, etwa für die Einblendung „Was kann ich jetzt?“ (BE-12, BE-16).</summary>
    public IEnumerable<CommandDefinition> OfClass(CommandClass commandClass) =>
        Commands.Where(command => command.Class == commandClass);

    /// <summary>Einträge, die an einem Ort gelten: die überall gültigen und die des Orts (BE-16).</summary>
    public IEnumerable<CommandDefinition> ApplicableIn(CommandScope scope) =>
        Commands.Where(command => command.Scope == CommandScope.Everywhere || command.Scope == scope);
}
