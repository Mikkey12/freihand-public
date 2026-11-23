using System.Collections.ObjectModel;

namespace Freihand.Core.Profiles;

/// <summary>Feste Gerätekennungen (Katalog Kapitel 17); weitere Geräte wählen eine eigene Kennung nach dem Muster für Bezeichner.</summary>
public static class KnownDevices
{
    /// <summary>Desktop mit CUDA-Grafikkarte.</summary>
    public const string ReferenceDesktop = "reference-desktop";

    public const string ReferenceNotebook = "reference-notebook";

    public const string SecondaryNotebook = "secondary-notebook";
}

/// <summary>Feste Laufzeitkennungen, klein geschrieben; sie entsprechen <c>RecognizerRuntime</c> in P8 (Cuda, Vulkan, Cpu).</summary>
public static class KnownRuntimes
{
    public const string Cuda = "cuda";

    public const string Vulkan = "vulkan";

    public const string Cpu = "cpu";
}

/// <summary>
/// Eine bestandene Live-Abnahme nach Katalog Kapitel 16 für ein Gerät, eine Profilsprache, ein Erkennungsmodell
/// und eine Laufzeit. Die Prüfung der Werte macht der Konstruktor von <see cref="LiveApprovals"/>.
/// </summary>
/// <param name="DeviceId">Gerätekennung nach dem Muster für Bezeichner, höchstens 64 Zeichen.</param>
/// <param name="Language">Profilsprache als BCP-47-Kennung, z. B. <c>de-CH</c>.</param>
/// <param name="Model">
/// Dateiname der Modelldatei wie im Inventar (<c>docs/freigabe/inventar.md</c>); nicht leer, höchstens 128
/// Zeichen, nur Kleinbuchstaben, Ziffern, <c>.</c>, <c>-</c> und <c>_</c>, beginnt mit Kleinbuchstabe oder Ziffer
/// und endet auf <c>.bin</c>.
/// </param>
/// <param name="Runtime">Genau <c>cuda</c>, <c>vulkan</c> oder <c>cpu</c> (klein).</param>
public sealed record LiveApproval(string DeviceId, string Language, string Model, string Runtime);

/// <summary>
/// Das Modell und die Laufzeit, mit denen die Erkennung tatsächlich läuft; Werte nach denselben Regeln wie in
/// <see cref="LiveApproval"/>. P9 bildet sie aus der Meldung „ready“ von P8. Ein Wert, der den Regeln nicht
/// entspricht, stimmt mit keinem Eintrag überein (<see cref="RecognitionModeResolver.Resolve"/> wirft nicht).
/// </summary>
public sealed record RecognizerIdentity(string Model, string Runtime);

/// <summary>Einstellung, wo live abgenommen ist (ER-19). Unveränderlich; eine Änderung ist ein neues Objekt.</summary>
public sealed class LiveApprovals
{
    public const int MaxModelLength = 128;

    private static readonly string[] Runtimes = [KnownRuntimes.Cuda, KnownRuntimes.Vulkan, KnownRuntimes.Cpu];

    /// <summary>Die Einträge, kopiert und nicht änderbar; das Feld verhindert, dass die Eingabeliste durchschlägt.</summary>
    private readonly ReadOnlyCollection<LiveApproval> entries;

    /// <summary>
    /// Prüft jeden Eintrag und kopiert die Liste; eine spätere Änderung der Eingabeliste ändert
    /// <see cref="Entries"/> nicht. Die Fehlermeldungen nennen nur das Feld, nie den Wert.
    /// </summary>
    /// <exception cref="ArgumentException">Ein Feld entspricht den Regeln nicht, oder ein Viererpaket kommt doppelt vor.</exception>
    /// <exception cref="ArgumentNullException">Die Liste, ein Eintrag oder ein Feld ist null.</exception>
    public LiveApprovals(IEnumerable<LiveApproval> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var copy = entries.ToArray();
        var seen = new HashSet<LiveApproval>();
        foreach (var entry in copy)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(entries));
            ArgumentNullException.ThrowIfNull(entry.DeviceId, nameof(entries));
            ArgumentNullException.ThrowIfNull(entry.Language, nameof(entries));
            ArgumentNullException.ThrowIfNull(entry.Model, nameof(entries));
            ArgumentNullException.ThrowIfNull(entry.Runtime, nameof(entries));

            if (!DataSafety.IsId(entry.DeviceId))
            {
                throw new ArgumentException(
                    $"Eine Gerätekennung entspricht dem Muster für Bezeichner nicht (höchstens {DataSafety.MaxIdLength} Zeichen).",
                    nameof(entries));
            }

            if (!DataSafety.IsLanguage(entry.Language))
            {
                throw new ArgumentException("Eine Sprache ist keine gültige Sprachkennung, erwartet z. B. „de-CH“.", nameof(entries));
            }

            if (!DataSafety.IsModelFileName(entry.Model, MaxModelLength))
            {
                throw new ArgumentException(
                    $"Ein Modell ist kein gültiger Dateiname einer Modelldatei (höchstens {MaxModelLength} Zeichen, endet auf .bin).",
                    nameof(entries));
            }

            if (Array.IndexOf(Runtimes, entry.Runtime) < 0)
            {
                throw new ArgumentException($"Eine Laufzeit ist keine von: {string.Join(", ", Runtimes)}.", nameof(entries));
            }

            if (!seen.Add(entry))
            {
                throw new ArgumentException("Ein Eintrag kommt doppelt vor.", nameof(entries));
            }
        }

        this.entries = Array.AsReadOnly(copy);
    }

    /// <summary>
    /// Startwert: nur Referenzrechner mit Hochdeutsch auf der Grafikkarte, also genau
    /// (reference-desktop, de-CH, ggml-large-v3-turbo.bin, cuda): die Modelldatei des Referenzrechner im P8-Konzept.
    /// </summary>
    public static LiveApprovals Default { get; } =
        new([new LiveApproval(KnownDevices.ReferenceDesktop, "de-CH", "ggml-large-v3-turbo.bin", KnownRuntimes.Cuda)]);

    /// <summary>Keine Live-Abnahme, überall satzweise.</summary>
    public static LiveApprovals None { get; } = new([]);

    public IReadOnlyList<LiveApproval> Entries => entries;

    /// <summary>
    /// Ordinaler Vergleich aller vier Teile; keine Ableitung von de-CH auf de oder de-DE, von cuda auf cpu oder
    /// von einem Modell auf ein anderes.
    /// </summary>
    /// <exception cref="ArgumentNullException">Ein Argument ist null.</exception>
    public bool Covers(string deviceId, string language, RecognizerIdentity recognizer)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(recognizer);

        return entries.Any(entry =>
            string.Equals(entry.DeviceId, deviceId, StringComparison.Ordinal)
            && string.Equals(entry.Language, language, StringComparison.Ordinal)
            && string.Equals(entry.Model, recognizer.Model, StringComparison.Ordinal)
            && string.Equals(entry.Runtime, recognizer.Runtime, StringComparison.Ordinal));
    }
}
