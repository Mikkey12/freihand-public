using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;

namespace Freihand.App;

/// <summary>
/// Ausgabe des Programms: privat mit Korrigieren und Glätten online, oder Geschäft
/// (<c>-p:FreihandAusgabe=Geschaeft</c>) ganz ohne Online-Bausteine (GE-02), mit den Modellen aus dem Modellpaket im
/// Datenordner.
/// </summary>
internal static class Edition
{
#if GESCHAEFT
    public const bool Business = true;
#else
    public const bool Business = false;
#endif

    public static string Name => Business ? "Geschäft" : "Privat";

    /// <summary>Standardwerte der Einstellungen für diese Ausgabe; <paramref name="folder"/> ist der Datenordner.</summary>
    public static PrototypeSettings Defaults(string folder) =>
        Business ? PrototypeSettings.BusinessDefaults(folder, folder) : PrototypeSettings.Defaults(folder);

    /// <summary>
    /// Geltende Einstellungen: In der Geschäftsausgabe liegen Modell und VAD immer im Datenordner unter „Modelle“
    /// (<see cref="PrototypeSettings.WithModelsIn"/>); privat gilt die Einstellung, wie sie ist.
    /// </summary>
    public static PrototypeSettings Effective(PrototypeSettings settings, string folder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Business ? settings.WithModelsIn(folder) : settings;
    }

    /// <summary>Hinweis, wenn Modell oder VAD fehlen, mit den fehlenden Dateien; in der Geschäftsausgabe mit dem Modellpaket.</summary>
    public static string ModelMissing(PrototypeSettings settings, string settingsPath, string folder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var missing = string.Join(", ", new[] { settings.Model, settings.Vad }.Where(path => !System.IO.File.Exists(path)).Select(System.IO.Path.GetFileName));
        return Business
            ? $"Sprachmodell fehlt ({missing}) in {System.IO.Path.Combine(folder, "Modelle")}. Das Modellpaket nach %LOCALAPPDATA% kopieren (LIESMICH)."
            : $"Modell oder VAD fehlt ({missing}); Pfade in {settingsPath} prüfen.";
    }

    /// <summary>
    /// Ohne Online gibt es kein Korrigieren und Glätten, also auch keine Tasten dafür (GE-02). Die beiden Aktionen nehmen
    /// schon an der Prüfung auf doppelte Belegung nicht teil, damit sie keine verfügbare Taste verdrängen.
    /// </summary>
    public static IReadOnlyDictionary<HotkeyAction, HotkeyGesture> Gestures(
        PrototypeSettings settings, out IReadOnlyList<HotkeyAction> invalid, out IReadOnlyList<HotkeyDuplicate> duplicates)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Gestures(Business ? [HotkeyAction.Correct, HotkeyAction.Smooth] : [], out invalid, out duplicates);
    }
}
