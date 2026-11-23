using System.Diagnostics.CodeAnalysis;

namespace Freihand.Core.Profiles;

/// <summary>Ein Fehler beim Import von Profilen oder Befehlen (OF-01).</summary>
/// <param name="Path">Stelle im Dokument, z. B. <c>$.profiles[0].transform.endpoint</c>.</param>
/// <param name="Message">Meldung für die Anzeige.</param>
public sealed record ImportError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>
/// Ergebnis eines Imports. Ein Import gilt ganz oder gar nicht: Bei einem einzigen Fehler wird nichts
/// übernommen (OF-01).
/// </summary>
public sealed class ImportResult<T>
    where T : class
{
    private ImportResult(T? value, IReadOnlyList<ImportError> errors)
    {
        Value = value;
        Errors = errors;
    }

    public T? Value { get; }

    public IReadOnlyList<ImportError> Errors { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    public bool Succeeded => Value is not null;

    public static ImportResult<T> Success(T value) => new(value, []);

    public static ImportResult<T> Failure(IReadOnlyList<ImportError> errors)
    {
        if (errors.Count == 0)
        {
            throw new ArgumentException("Ein Fehlschlag braucht mindestens einen Fehler.", nameof(errors));
        }

        return new(null, errors);
    }
}

/// <summary>Sammelt Fehler während der Prüfung, damit ein Import alle Fehler auf einmal meldet.</summary>
internal sealed class ImportErrors
{
    private readonly List<ImportError> errors = [];

    public bool Any => errors.Count > 0;

    public IReadOnlyList<ImportError> All => errors;

    public void Add(string path, string message) => errors.Add(new ImportError(path, message));
}
