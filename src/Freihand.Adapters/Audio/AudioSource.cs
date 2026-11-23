using Freihand.Core.Platform;
using Freihand.Core.Profiles;

namespace Freihand.Adapters.Audio;

/// <summary>Ergebnis der Auflösung einer Audioquelle beim Diktatbeginn (P8-Konzept, Kapitel 4.2 und 5.1).</summary>
public abstract record AudioSourceResolution;

/// <summary>Das zu öffnende Gerät.</summary>
/// <param name="Device">Gerät aus der aktuellen Geräteliste.</param>
/// <param name="IsSystemDefault">Gewählt war das Standardgerät von Windows.</param>
public sealed record ResolvedDevice(AudioDevice Device, bool IsSystemDefault) : AudioSourceResolution;

/// <summary>Kein Gerät; nie ein Ausweichen auf ein anderes Mikrofon (ER-13).</summary>
/// <param name="Reason">Grund, im Pilot immer <see cref="CaptureStopReason.DeviceMissing"/>.</param>
/// <param name="DisplayName">Anzeigename aus dem Profil, beim Standardgerät null.</param>
public sealed record SourceUnavailable(CaptureStopReason Reason, string? DisplayName) : AudioSourceResolution;

/// <summary>Auswahl je Profil auf diesem Gerät; geht der Angabe im Profil vor (ER-12, Kapitel 5.1).</summary>
/// <param name="ByProfileId">Auswahl nach Profilkennung.</param>
public sealed record AudioSourceOverrides(IReadOnlyDictionary<string, AudioSourceSelection> ByProfileId)
{
    /// <summary>Die wirksame Auswahl: der Eintrag für die Kennung des Profils, sonst <see cref="Profile.AudioSource"/>.</summary>
    public AudioSourceSelection For(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ByProfileId.TryGetValue(profile.Id, out var selection) ? selection : profile.AudioSource;
    }
}

/// <summary>Löst eine Auswahl gegen die aktuelle Geräteliste auf; gesucht wird nur über die Kennung, nie über den Namen.</summary>
public static class AudioSourceResolver
{
    public static AudioSourceResolution Resolve(
        AudioSourceSelection selection, IReadOnlyList<AudioDevice> devices, AudioDevice? systemDefault)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(devices);

        if (selection.Kind == AudioSourceKind.SystemDefault)
        {
            return systemDefault is null
                ? new SourceUnavailable(CaptureStopReason.DeviceMissing, null)
                : new ResolvedDevice(systemDefault, IsSystemDefault: true);
        }

        // Nur über die Kennung, nie über den Namen; kein Ausweichen auf das Standardgerät (ER-13)
        var device = devices.FirstOrDefault(candidate => string.Equals(candidate.Id, selection.DeviceId, StringComparison.Ordinal));
        return device is null
            ? new SourceUnavailable(CaptureStopReason.DeviceMissing, selection.DisplayName)
            : new ResolvedDevice(device, IsSystemDefault: false);
    }
}
