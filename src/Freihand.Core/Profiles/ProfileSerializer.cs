using System.Text.Json;
using System.Text.RegularExpressions;
using Freihand.Core.Routing;

namespace Freihand.Core.Profiles;

/// <summary>
/// Import und Export von Profilen nach OF-01: versioniertes Schema, Prüfung beim Import, keine
/// ausführbaren Inhalte, keine Geheimnisse, nur zugelassene Endpunkte. Ein Import gilt ganz oder gar nicht.
/// Gelesen werden die Versionen 1 und 2; Profile der Version 1 werden beim Lesen auf Version 2 gehoben (Modus
/// live, kein Vokabular, keine Programme, kein Stil), ein Export schreibt immer Version 2.
/// </summary>
public static partial class ProfileSerializer
{
    private const int MaxEndpointLength = 2048;

    /// <summary>Felder eines Profils, die es erst ab Version 2 gibt; in einem Dokument der Version 1 sind sie unbekannt.</summary>
    private static readonly string[] Version2ProfileFields = ["recognitionMode", "vocabulary", "programs"];

    /// <summary>Liest und prüft ein Dokument mit Profilen.</summary>
    /// <param name="json">Das Dokument.</param>
    /// <param name="allowList">Zugelassene Endpunkte; Profile ohne Endpunkt brauchen keinen Eintrag.</param>
    public static ImportResult<IReadOnlyList<Profile>> Import(string json, EndpointAllowList allowList)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(allowList);
        return Read(json, allowList);
    }

    /// <summary>
    /// Schreibt Profile als Dokument. Das Ergebnis wird vor der Rückgabe wieder eingelesen und geprüft,
    /// damit kein Export Geheimnisse oder ungültige Angaben enthält (OF-01).
    /// </summary>
    /// <exception cref="InvalidOperationException">Ein Profil verletzt das Schema.</exception>
    public static string Export(IEnumerable<Profile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var json = Write(profiles);

        // Die zugelassenen Endpunkte prüft erst der Import beim Empfänger.
        var check = Read(json, allowList: null);
        if (!check.Succeeded)
        {
            throw new InvalidOperationException(
                "Export abgebrochen, ein Profil ist ungültig: " + string.Join("; ", check.Errors));
        }

        return json;
    }

    /// <summary>
    /// Schreibt Profile als Dokument der aktuellen Version, ohne es zu prüfen. <see cref="Export"/> prüft das
    /// Ergebnis selbst; <see cref="ProfileCatalog.Replace"/> liest es mit der Prüfung eines Imports wieder ein.
    /// </summary>
    internal static string Write(IEnumerable<Profile> profiles)
    {
        var document = new ProfileDocumentJson
        {
            Kind = ProfileSchema.Kind,
            SchemaVersion = ProfileSchema.CurrentVersion,
            Profiles = [.. profiles.Select(ToJson)],
        };
        return DataSafety.Serialize(document, ProfileJsonContext.Default.ProfileDocumentJson);
    }

    private static ImportResult<IReadOnlyList<Profile>> Read(string json, EndpointAllowList? allowList)
    {
        var errors = new ImportErrors();
        using var document = DataSafety.ParseEnvelope(
            json, ProfileSchema.Kind, ProfileSchema.OldestReadableVersion, ProfileSchema.CurrentVersion, errors, out var version);
        if (document is null)
        {
            return ImportResult<IReadOnlyList<Profile>>.Failure(errors.All);
        }

        // Ein Dokument wird nie halb als Version 1 und halb als Version 2 gelesen: Felder ab Version 2 sind in
        // Version 1 unbekannt (OF-01).
        if (version < 2)
        {
            RejectVersion2Fields(document.RootElement, errors);
        }

        // Optional heisst weglassbar: ein vorhandenes optionales Feld mit null ist weder Text noch Liste noch
        // Stil und wird wie im JSON-Schema abgewiesen, statt still als fehlend zu gelten.
        RejectExplicitNulls(document.RootElement, version, errors);
        if (errors.Any)
        {
            return ImportResult<IReadOnlyList<Profile>>.Failure(errors.All);
        }

        var data = DataSafety.Deserialize(document, ProfileJsonContext.Default.ProfileDocumentJson, errors);
        if (data is null)
        {
            return ImportResult<IReadOnlyList<Profile>>.Failure(errors.All);
        }

        if (data.Profiles is null)
        {
            errors.Add("$.profiles", "Fehlt.");
            return ImportResult<IReadOnlyList<Profile>>.Failure(errors.All);
        }

        if (data.Profiles.Count > ProfileSchema.MaxProfiles)
        {
            errors.Add("$.profiles", $"Höchstens {ProfileSchema.MaxProfiles} Profile je Dokument.");
            return ImportResult<IReadOnlyList<Profile>>.Failure(errors.All);
        }

        var profiles = new List<Profile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var programs = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < data.Profiles.Count; index++)
        {
            var path = $"$.profiles[{index}]";
            var profile = ReadProfile(data.Profiles[index], path, version, programs, allowList, errors);
            if (profile is null)
            {
                continue;
            }

            if (!ids.Add(profile.Id))
            {
                errors.Add(path + ".id", $"„{profile.Id}“ kommt mehrfach vor.");
                continue;
            }

            profiles.Add(profile);
        }

        return errors.Any
            ? ImportResult<IReadOnlyList<Profile>>.Failure(errors.All)
            : ImportResult<IReadOnlyList<Profile>>.Success(profiles);
    }

    /// <summary>
    /// Weist ein vorhandenes optionales Feld mit <c>null</c> ab (OF-01): <c>description</c>, die Endpunkte,
    /// Kennung und Anzeigename der Audioquelle sowie ab Version 2 <c>vocabulary</c>, <c>programs</c> und
    /// <c>transform.style</c>. Weglassen bleibt erlaubt.
    /// </summary>
    private static void RejectExplicitNulls(JsonElement root, int version, ImportErrors errors)
    {
        if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        string[] profileFields = version >= 2 ? ["description", "vocabulary", "programs"] : ["description"];
        string[] transformFields = version >= 2 ? ["endpoint", "style"] : ["endpoint"];
        var index = 0;
        foreach (var profile in profiles.EnumerateArray())
        {
            var path = $"$.profiles[{index++}]";
            if (profile.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            DataSafety.RejectExplicitNulls(profile, path, errors, profileFields);
            if (profile.TryGetProperty("recognition", out var recognition))
            {
                DataSafety.RejectExplicitNulls(recognition, path + ".recognition", errors, "endpoint");
            }

            if (profile.TryGetProperty("transform", out var transform))
            {
                DataSafety.RejectExplicitNulls(transform, path + ".transform", errors, transformFields);
            }

            if (profile.TryGetProperty("audioSource", out var audioSource))
            {
                DataSafety.RejectExplicitNulls(audioSource, path + ".audioSource", errors, "deviceId", "displayName");
            }
        }
    }

    private static void RejectVersion2Fields(JsonElement root, ImportErrors errors)
    {
        if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var profile in profiles.EnumerateArray())
        {
            var path = $"$.profiles[{index++}]";
            if (profile.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var name in Version2ProfileFields.Where(name => profile.TryGetProperty(name, out _)))
            {
                errors.Add(path, $"Das Feld „{name}“ gibt es erst ab Version 2.");
            }

            if (profile.TryGetProperty("transform", out var transform)
                && transform.ValueKind == JsonValueKind.Object
                && transform.TryGetProperty("style", out _))
            {
                errors.Add(path + ".transform", "Das Feld „style“ gibt es erst ab Version 2.");
            }
        }
    }

    private static Profile? ReadProfile(
        ProfileJson? json, string path, int version, HashSet<string> programOwners, EndpointAllowList? allowList, ImportErrors errors)
    {
        if (json is null)
        {
            errors.Add(path, "Ein Profil muss ein Objekt sein.");
            return null;
        }

        var before = errors.All.Count;

        DataSafety.CheckId(errors, path + ".id", json.Id);
        DataSafety.CheckText(errors, path + ".name", json.Name, ProfileSchema.MaxNameLength, required: true);
        DataSafety.CheckText(errors, path + ".description", json.Description, ProfileSchema.MaxDescriptionLength, required: false);

        var recognition = ReadRecognition(json.Recognition, path + ".recognition", allowList, errors);
        var transform = ReadTransform(json.Transform, path + ".transform", allowList, errors);

        var dataClass = ProfileSchema.DataClasses.FirstOrDefault(candidate => candidate.Id == json.DataClass);
        if (dataClass is null)
        {
            errors.Add(path + ".dataClass", DataSafety.Unknown(json.DataClass, ProfileSchema.DataClasses.Select(c => c.Id)));
        }

        if (json.Language is null)
        {
            errors.Add(path + ".language", "Fehlt.");
        }
        else if (!LanguageRegex().IsMatch(json.Language))
        {
            errors.Add(path + ".language", "Keine gültige Sprachkennung, erwartet z. B. „de-CH“.");
        }

        // Version 1 kennt den Modus nicht: live (P3-Konzept, Entscheid 4). Ab Version 2 ist er Pflicht.
        var recognitionMode = version >= 2
            ? DataSafety.ReadEnum(ProfileSchema.RecognitionModes, json.RecognitionMode, path + ".recognitionMode", errors)
            : RecognitionMode.Live;
        var recordingMode = DataSafety.ReadEnum(ProfileSchema.RecordingModes, json.RecordingMode, path + ".recordingMode", errors);
        var audioSource = ReadAudioSource(json.AudioSource, path + ".audioSource", errors);
        var output = DataSafety.ReadEnum(ProfileSchema.OutputModes, json.Output, path + ".output", errors);
        DataSafety.CheckId(errors, path + ".formatRules", json.FormatRules);
        var strictness = DataSafety.ReadEnum(ProfileSchema.Strictness, json.Strictness, path + ".strictness", errors);
        var vocabulary = ReadVocabulary(json.Vocabulary, path + ".vocabulary", errors);
        var programs = ReadPrograms(json.Programs, path + ".programs", programOwners, errors);

        // AU-03: In strengen Profilen ist die Zwischenablage gesperrt.
        if (strictness == ProfileStrictness.Strict && output == OutputMode.Clipboard)
        {
            errors.Add(path + ".output", "Ein strenges Profil darf nicht in die Zwischenablage ausgeben (AU-03).");
        }

        if (errors.All.Count > before)
        {
            return null;
        }

        return new Profile(
            json.Id!,
            json.Name!,
            json.Description,
            recognition!,
            transform!,
            dataClass!,
            json.Language!,
            recordingMode!.Value,
            audioSource!,
            output!.Value,
            json.FormatRules!,
            strictness!.Value)
        {
            RecognitionMode = recognitionMode!.Value,
            Vocabulary = vocabulary!,
            Programs = programs!,
        };
    }

    /// <summary>
    /// Persönliches Vokabular (BA-03): freier Text je Eintrag, 1 bis 500 Einträge, jeder höchstens 100 Zeichen
    /// und nur einmal, verglichen ordinal. Ein leeres Feld gibt es nicht: ohne Vokabular fehlt das Feld.
    /// </summary>
    private static TermList? ReadVocabulary(List<string?>? entries, string path, ImportErrors errors)
    {
        if (entries is null)
        {
            return TermList.Empty;
        }

        if (entries.Count == 0)
        {
            errors.Add(path, "Darf nicht leer sein; ohne Vokabular fehlt das Feld.");
            return null;
        }

        if (entries.Count > ProfileSchema.MaxVocabularyEntries)
        {
            errors.Add(path, $"Höchstens {ProfileSchema.MaxVocabularyEntries} Einträge.");
            return null;
        }

        var before = errors.All.Count;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entryPath = $"{path}[{index}]";
            var entry = entries[index];
            DataSafety.CheckText(errors, entryPath, entry, ProfileSchema.MaxVocabularyEntryLength, required: true);
            if (entry is not null && !seen.Add(entry))
            {
                errors.Add(entryPath, "Kommt mehrfach vor.");
            }
        }

        return errors.All.Count > before ? null : new TermList(entries!);
    }

    /// <summary>
    /// Programme für den Profilwechsel (BA-02): Dateinamen in Kleinbuchstaben, 1 bis 32 Einträge. Ein Programm
    /// gehört höchstens zu einem Profil; <paramref name="owners"/> sammelt die Programme der Profile davor.
    /// </summary>
    private static TermList? ReadPrograms(List<string?>? entries, string path, HashSet<string> owners, ImportErrors errors)
    {
        if (entries is null)
        {
            return TermList.Empty;
        }

        if (entries.Count == 0)
        {
            errors.Add(path, "Darf nicht leer sein; ohne Programme fehlt das Feld.");
            return null;
        }

        if (entries.Count > ProfileSchema.MaxPrograms)
        {
            errors.Add(path, $"Höchstens {ProfileSchema.MaxPrograms} Programme.");
            return null;
        }

        var before = errors.All.Count;
        var own = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entryPath = $"{path}[{index}]";
            var entry = entries[index];
            if (entry is null)
            {
                errors.Add(entryPath, "Fehlt.");
                continue;
            }

            if (!DataSafety.IsProgramName(entry))
            {
                errors.Add(
                    entryPath,
                    $"Kein gültiger Programmname (Dateiname in Kleinbuchstaben mit der Endung .exe, höchstens {ProfileSchema.MaxProgramLength} Zeichen).");
            }

            DataSafety.CheckSecretsAndCode(errors, entryPath, entry);

            if (!own.Add(entry))
            {
                errors.Add(entryPath, "Kommt mehrfach vor.");
            }
            else if (owners.Contains(entry))
            {
                errors.Add(entryPath, "Ist schon einem anderen Profil zugeordnet.");
            }
        }

        owners.UnionWith(own);
        return errors.All.Count > before ? null : new TermList(entries!);
    }

    private static RecognitionStep? ReadRecognition(RouteStepJson? json, string path, EndpointAllowList? allowList, ImportErrors errors)
    {
        if (json is null)
        {
            errors.Add(path, "Fehlt.");
            return null;
        }

        var route = DataSafety.ReadEnum(ProfileSchema.RecognitionRoutes, json.Route, path + ".route", errors);
        if (route is null)
        {
            return null;
        }

        // WE-03: Verlässt das Audio das Gerät, steht der Endpunkt im Profil und muss zugelassen sein.
        var rule = route.Value.LeavesDevice() ? EndpointRule.RequiredRemote : EndpointRule.Forbidden;
        var endpoint = ReadEndpoint(json.Endpoint, path + ".endpoint", rule, allowList, errors);
        return new RecognitionStep(route.Value, endpoint);
    }

    private static TransformStep? ReadTransform(TransformStepJson? json, string path, EndpointAllowList? allowList, ImportErrors errors)
    {
        if (json is null)
        {
            errors.Add(path, "Fehlt.");
            return null;
        }

        var route = DataSafety.ReadEnum(ProfileSchema.TransformRoutes, json.Route, path + ".route", errors);
        if (route is null)
        {
            return null;
        }

        // „Lokal“ heisst „auf diesem Gerät“ (SI-07): ein lokales Sprachmodell nur über die Loopback-Adresse.
        var rule = route.Value switch
        {
            TransformRoute.None => EndpointRule.Forbidden,
            TransformRoute.Local => EndpointRule.OptionalLoopback,
            _ => EndpointRule.RequiredRemote,
        };
        var endpoint = ReadEndpoint(json.Endpoint, path + ".endpoint", rule, allowList, errors);

        // BA-10: ein Umformstil gibt es nur bei einem Umformweg; bei „none“ (Exakt-Profil, BA-12) ist er ein Fehler.
        TransformStyle? style = null;
        if (json.Style is not null)
        {
            if (route == TransformRoute.None)
            {
                errors.Add(path + ".style", "Ein Exakt-Profil ohne Umformung hat keinen Umformstil (BA-10, BA-12).");
            }
            else
            {
                style = DataSafety.ReadEnum(ProfileSchema.TransformStyles, json.Style, path + ".style", errors);
            }
        }

        return new TransformStep(route.Value, endpoint, style);
    }

    private static Uri? ReadEndpoint(string? value, string path, EndpointRule rule, EndpointAllowList? allowList, ImportErrors errors)
    {
        if (value is null)
        {
            if (rule == EndpointRule.RequiredRemote)
            {
                errors.Add(path, "Fehlt; dieser Weg verlässt das Gerät und braucht einen zugelassenen Endpunkt (WE-03).");
            }

            return null;
        }

        if (rule == EndpointRule.Forbidden)
        {
            errors.Add(path, "Dieser Weg bleibt auf dem Gerät und hat keinen Endpunkt.");
            return null;
        }

        // Nur Webadressen; das Schema wird am Text geprüft, weil Uri unter Linux auch „/pfad“ als Datei liest.
        if (!value.StartsWith("https://", StringComparison.Ordinal) && !value.StartsWith("http://", StringComparison.Ordinal))
        {
            errors.Add(path, "Muss mit https:// beginnen, http:// nur für die Loopback-Adresse dieses Geräts (WE-16).");
            return null;
        }

        if (DataSafety.CodePointCount(value) > MaxEndpointLength
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || DataSafety.ContainsForbiddenCharacter(value)
            || value.Any(char.IsWhiteSpace))
        {
            errors.Add(path, "Ist keine gültige absolute Adresse.");
            return null;
        }

        var before = errors.All.Count;

        // WE-16: immer verschlüsselt; unverschlüsselt nur auf diesem Gerät.
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            errors.Add(path, "Nur https ist erlaubt, http einzig für die Loopback-Adresse dieses Geräts (WE-16).");
        }

        // SI-03: Zugangsdaten gehören in den Schlüsselspeicher, nie in die Adresse.
        if (!string.IsNullOrEmpty(uri.UserInfo) || value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            errors.Add(path, "Darf weder Zugangsdaten noch Abfrage noch Anker enthalten (SI-03).");
        }

        if (rule == EndpointRule.OptionalLoopback && !uri.IsLoopback)
        {
            errors.Add(path, "Ein lokales Sprachmodell läuft auf diesem Gerät; erlaubt ist nur die Loopback-Adresse.");
        }

        if (rule == EndpointRule.RequiredRemote && uri.IsLoopback)
        {
            errors.Add(path, "Dieser Weg verlässt das Gerät; eine Loopback-Adresse verschleiert den tatsächlichen Endpunkt (WE-03).");
        }

        DataSafety.CheckAddressSecretsAndCode(errors, path, value);

        if (errors.All.Count > before)
        {
            return null;
        }

        if (allowList is not null && !allowList.Allows(uri))
        {
            errors.Add(path, "Kein zugelassener Endpunkt (OF-01).");
            return null;
        }

        return uri;
    }

    private static AudioSourceSelection? ReadAudioSource(AudioSourceJson? json, string path, ImportErrors errors)
    {
        if (json is null)
        {
            errors.Add(path, "Fehlt.");
            return null;
        }

        var kind = DataSafety.ReadEnum(ProfileSchema.AudioSourceKinds, json.Kind, path + ".kind", errors);
        switch (kind)
        {
            case AudioSourceKind.SystemDefault:
                if (json.DeviceId is not null || json.DisplayName is not null)
                {
                    errors.Add(path, "Das Standardgerät hat weder Kennung noch Anzeigename.");
                    return null;
                }

                return AudioSourceSelection.SystemDefault;

            case AudioSourceKind.Device:
                var before = errors.All.Count;
                DataSafety.CheckText(errors, path + ".deviceId", json.DeviceId, ProfileSchema.MaxDeviceIdLength, required: true);
                DataSafety.CheckText(errors, path + ".displayName", json.DisplayName, ProfileSchema.MaxNameLength, required: true);
                return errors.All.Count > before ? null : AudioSourceSelection.Device(json.DeviceId!, json.DisplayName!);

            default:
                return null;
        }
    }

    private static ProfileJson ToJson(Profile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Description = profile.Description,
        Recognition = new RouteStepJson
        {
            Route = ProfileSchema.RecognitionRoutes.NameForExport(profile.Recognition.Route),
            Endpoint = profile.Recognition.Endpoint?.OriginalString,
        },
        Transform = new TransformStepJson
        {
            Route = ProfileSchema.TransformRoutes.NameForExport(profile.Transform.Route),
            Endpoint = profile.Transform.Endpoint?.OriginalString,
            Style = profile.Transform.Style is { } style ? ProfileSchema.TransformStyles.NameForExport(style) : null,
        },
        DataClass = profile.DataClass.Id,
        Language = profile.Language,
        RecognitionMode = ProfileSchema.RecognitionModes.NameForExport(profile.RecognitionMode),
        RecordingMode = ProfileSchema.RecordingModes.NameForExport(profile.RecordingMode),
        AudioSource = new AudioSourceJson
        {
            Kind = ProfileSchema.AudioSourceKinds.NameForExport(profile.AudioSource.Kind),
            DeviceId = profile.AudioSource.DeviceId,
            DisplayName = profile.AudioSource.DisplayName,
        },
        Output = ProfileSchema.OutputModes.NameForExport(profile.Output),
        FormatRules = profile.FormatRules,
        Strictness = ProfileSchema.Strictness.NameForExport(profile.Strictness),

        // Leere Listen werden weggelassen: für „kein Vokabular“ gibt es genau eine Schreibweise.
        Vocabulary = profile.Vocabulary is { Count: > 0 } ? [.. profile.Vocabulary] : null,
        Programs = profile.Programs is { Count: > 0 } ? [.. profile.Programs] : null,
    };

    [GeneratedRegex(ProfileSchema.LanguagePatternStrict, RegexOptions.CultureInvariant)]
    private static partial Regex LanguageRegex();

    private enum EndpointRule
    {
        /// <summary>Der Weg bleibt auf dem Gerät und hat keinen Endpunkt.</summary>
        Forbidden,

        /// <summary>Lokales Sprachmodell: kein Endpunkt oder die Loopback-Adresse.</summary>
        OptionalLoopback,

        /// <summary>Der Weg verlässt das Gerät: Endpunkt nötig, nicht die Loopback-Adresse.</summary>
        RequiredRemote,
    }
}
