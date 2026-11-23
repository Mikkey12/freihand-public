using Freihand.Core.Platform;

namespace Freihand.Adapters.Dictation;

/// <summary>Ein Eintrag im Untermenü „Mikrofon“ (B3); <see cref="Id"/> null heisst Standardgerät von Windows.</summary>
public sealed record MicrophoneEntry(string? Id, string Name, bool Selected, bool Present);

/// <summary>
/// Untermenü „Mikrofon“: zuerst „Windows-Standard“, dann jedes vorhandene Gerät. Ist das
/// gewählte Gerät nicht verbunden, steht es angehakt und nicht wählbar am Ende: Freihand wechselt nie still auf ein
/// anderes Gerät (ER-13), der nächste Start meldet „Gerät fehlt“.
/// </summary>
public static class MicrophoneMenu
{
    public const string WindowsDefault = "Windows-Standard";

    public const string Missing = "Gewähltes Gerät – nicht verbunden";

    public static IReadOnlyList<MicrophoneEntry> Entries(IReadOnlyList<AudioDevice> devices, string? selected)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var entries = new List<MicrophoneEntry> { new(null, WindowsDefault, selected is null, Present: true) };
        entries.AddRange(devices.Select(device => new MicrophoneEntry(device.Id, device.Name, device.Id == selected, Present: true)));
        if (selected is not null && devices.All(device => device.Id != selected))
        {
            entries.Add(new MicrophoneEntry(selected, Missing, Selected: true, Present: false));
        }

        return entries;
    }
}
