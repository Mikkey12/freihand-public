namespace Freihand.Core.Profiles;

/// <summary>
/// Die verfügbaren Profile: zuerst die eingebauten (BA-21), danach importierte (BA-01). Eingebaute Profile
/// lassen sich weder ersetzen noch entfernen; ein importiertes Profil ersetzt nie still ein vorhandenes. Ein
/// vorhandenes Profil ersetzt nur <see cref="Replace"/>, ausdrücklich und mit denselben Prüfungen wie ein Import.
/// Ein Programm (BA-02) gehört höchstens zu einem importierten Profil.
/// </summary>
public sealed class ProfileCatalog
{
    private const string AnotherProfile = "Ist schon einem anderen Profil zugeordnet.";

    private readonly List<Profile> imported = [];

    public IReadOnlyList<Profile> Profiles => [.. BuiltInProfiles.All, .. imported];

    public Profile? Find(string id) => Profiles.FirstOrDefault(profile => profile.Id == id);

    public static bool IsBuiltIn(string id) => BuiltInProfiles.IsBuiltInId(id);

    /// <summary>
    /// Importiert ein Dokument nach OF-01. Ganz oder gar nicht: Bei einem Fehler, auch bei einem
    /// belegten Bezeichner, wird kein Profil übernommen.
    /// </summary>
    public ImportResult<IReadOnlyList<Profile>> Import(string json, EndpointAllowList allowList)
    {
        var result = ProfileSerializer.Import(json, allowList);
        if (!result.Succeeded)
        {
            return result;
        }

        var errors = new ImportErrors();
        for (var index = 0; index < result.Value.Count; index++)
        {
            var id = result.Value[index].Id;
            if (IsBuiltIn(id))
            {
                errors.Add($"$.profiles[{index}].id", $"„{id}“ ist ein eingebautes Profil und lässt sich nicht ersetzen (BA-21).");
            }
            else if (Find(id) is not null)
            {
                errors.Add($"$.profiles[{index}].id", $"Ein Profil „{id}“ ist schon vorhanden.");
            }

            // BA-02: Ein Programm gehört höchstens zu einem Profil, auch über Dokumente hinweg.
            var programs = result.Value[index].Programs;
            for (var program = 0; program < programs.Count; program++)
            {
                if (imported.Any(profile => profile.Programs.Contains(programs[program])))
                {
                    errors.Add($"$.profiles[{index}].programs[{program}]", AnotherProfile);
                }
            }
        }

        if (errors.Any)
        {
            return ImportResult<IReadOnlyList<Profile>>.Failure(errors.All);
        }

        imported.AddRange(result.Value);
        return result;
    }

    /// <summary>Entfernt ein importiertes Profil; eingebaute bleiben (BA-21).</summary>
    public bool Remove(string id) => !IsBuiltIn(id) && imported.RemoveAll(profile => profile.Id == id) > 0;

    /// <summary>
    /// Ersetzt ein importiertes Profil mit gleicher Id an derselben Stelle (BA-01, SI-11). Ganz oder gar nicht,
    /// mit denselben Prüfungen wie ein Import; eingebaute Profile lassen sich nicht ersetzen (BA-21).
    /// </summary>
    public ImportResult<Profile> Replace(Profile profile, EndpointAllowList allowList)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(allowList);

        // Wie ein Export als Dokument der Version 2 mit genau diesem Profil geschrieben und mit denselben
        // Prüfungen wie ein Import wieder gelesen; jeder Fehler trägt den Pfad $.profiles[0]….
        var read = ProfileSerializer.Import(ProfileSerializer.Write([profile]), allowList);
        if (!read.Succeeded)
        {
            return ImportResult<Profile>.Failure(read.Errors);
        }

        var replacement = read.Value[0];
        var errors = new ImportErrors();
        var position = imported.FindIndex(existing => existing.Id == replacement.Id);
        if (IsBuiltIn(replacement.Id))
        {
            errors.Add("$.profiles[0].id", "Ein eingebautes Profil lässt sich nicht ersetzen (BA-21).");
        }
        else if (position < 0)
        {
            errors.Add("$.profiles[0].id", "Kein importiertes Profil mit dieser Kennung; ersetzt wird nur ein vorhandenes.");
        }

        // BA-02: Ein Programm eines anderen importierten Profils bleibt dessen Programm; die eigenen bleiben frei.
        for (var program = 0; program < replacement.Programs.Count; program++)
        {
            if (imported.Any(other => other.Id != replacement.Id && other.Programs.Contains(replacement.Programs[program])))
            {
                errors.Add($"$.profiles[0].programs[{program}]", AnotherProfile);
            }
        }

        if (errors.Any)
        {
            return ImportResult<Profile>.Failure(errors.All);
        }

        imported[position] = replacement;
        return ImportResult<Profile>.Success(replacement);
    }

    /// <summary>
    /// BA-02: das importierte Profil, dem dieses Programm zugeordnet ist, sonst null. Der Name wird vor dem
    /// Vergleich mit <c>ToLowerInvariant</c> in Kleinbuchstaben gewandelt und dann ordinal verglichen.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="executableName"/> ist null.</exception>
    public Profile? FindForProgram(string executableName)
    {
        ArgumentNullException.ThrowIfNull(executableName);

        var name = executableName.ToLowerInvariant();
        return imported.FirstOrDefault(profile => profile.Programs.Contains(name));
    }

    /// <summary>Exportiert die importierten Profile, ohne die eingebauten.</summary>
    public string ExportImported() => ProfileSerializer.Export(imported);
}
