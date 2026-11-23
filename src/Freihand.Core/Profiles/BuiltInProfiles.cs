namespace Freihand.Core.Profiles;

/// <summary>
/// Die zwei fest eingebauten Pilot-Profile „Standard“ und „Streng“ (BA-21). Sie liegen als Dokument im
/// selben Schema wie importierte Profile (OF-01) und durchlaufen dieselbe Prüfung. Bearbeiten,
/// Ersetzen oder Entfernen verhindert <see cref="ProfileCatalog"/>.
/// </summary>
public static class BuiltInProfiles
{
    public const string StandardId = "standard";

    public const string StrictId = "strict";

    /// <summary>
    /// Im Pilot: Erkennung auf dem Gerät (WE-07), keine Umformung (Grundentscheid 3), Ein/Aus (BF-06),
    /// Schweizer Schreibweise (BA-17). „Streng“ ist ein Exakt-Profil (BA-12), weist unbekannte Felder ab
    /// (SI-04), gilt als vertraulich und gibt zuerst ins Diktatfenster aus (AU-04). Beide wünschen den Modus
    /// live (ER-20), der wirksam nur wird, wo live abgenommen ist (ER-19); sie haben weder Vokabular noch
    /// Programme, weil eingebaute Profile nicht bearbeitbar sind (BA-21). Dokument der Version 2.
    /// </summary>
    public const string Json = """
        {
          "kind": "freihand.profiles",
          "schemaVersion": 2,
          "profiles": [
            {
              "id": "standard",
              "name": "Standard",
              "description": "Diktat auf diesem Gerät direkt ins gebundene Ziel, ohne Umformung.",
              "recognition": {
                "route": "device"
              },
              "transform": {
                "route": "none"
              },
              "dataClass": "intern",
              "language": "de-CH",
              "recognitionMode": "live",
              "recordingMode": "toggle",
              "audioSource": {
                "kind": "systemDefault"
              },
              "output": "boundTarget",
              "formatRules": "ch-standard",
              "strictness": "standard"
            },
            {
              "id": "strict",
              "name": "Streng",
              "description": "Exaktes Diktat auf diesem Gerät ins Diktatfenster; unbekannte Felder werden abgewiesen.",
              "recognition": {
                "route": "device"
              },
              "transform": {
                "route": "none"
              },
              "dataClass": "vertraulich",
              "language": "de-CH",
              "recognitionMode": "live",
              "recordingMode": "toggle",
              "audioSource": {
                "kind": "systemDefault"
              },
              "output": "dictationWindow",
              "formatRules": "ch-standard",
              "strictness": "strict"
            }
          ]
        }
        """;

    private static readonly Lazy<IReadOnlyList<Profile>> Loaded = new(Load);

    /// <summary>Beide eingebauten Profile, „Standard“ zuerst.</summary>
    public static IReadOnlyList<Profile> All => Loaded.Value;

    public static Profile Standard => All.Single(profile => profile.Id == StandardId);

    public static Profile Strict => All.Single(profile => profile.Id == StrictId);

    public static bool IsBuiltInId(string id) => All.Any(profile => profile.Id == id);

    private static IReadOnlyList<Profile> Load()
    {
        // Eingebaute Profile haben keinen Endpunkt und brauchen darum keine zugelassenen Endpunkte.
        var result = ProfileSerializer.Import(Json, EndpointAllowList.None);
        return result.Succeeded
            ? result.Value
            : throw new InvalidOperationException(
                "Die eingebauten Profile verletzen das Schema: " + string.Join("; ", result.Errors));
    }
}
