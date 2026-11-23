using Microsoft.Win32;

namespace Freihand.Windows.Beta;

/// <summary>Ein Wert unter <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>; in Tests ersetzbar.</summary>
public interface IRunKey
{
    string? Get(string name);

    void Set(string name, string value);

    void Delete(string name);
}

/// <summary>Anmeldung des Benutzers in der Registrierung; ohne Administratorrechte.</summary>
public sealed class CurrentUserRunKey : IRunKey
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Autostart: genau ein eigener Eintrag „Freihand“, der auf das Programm im
/// Programmordner zeigt, nie auf einen Build-Ordner (BT02). Einschalten geht nur aus dem Programmordner heraus.
/// </summary>
public sealed class Autostart(IRunKey runKey, string programFolder, string executable)
{
    public const string Name = "Freihand";

    private string Command => "\"" + executable + "\"";

    /// <summary>Läuft dieses Freihand aus dem Programmordner? Nur dann lässt sich der Autostart einschalten.</summary>
    public bool CanEnable =>
        System.IO.Path.GetFullPath(executable).StartsWith(
            System.IO.Path.GetFullPath(programFolder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Eingeschaltet heisst: der eigene Eintrag zeigt genau auf dieses Programm.</summary>
    public bool IsEnabled => string.Equals(runKey.Get(Name), Command, StringComparison.OrdinalIgnoreCase);

    /// <returns>Falsch, wenn dieses Freihand nicht aus dem Programmordner läuft; dann bleibt alles, wie es ist.</returns>
    public bool Enable()
    {
        if (!CanEnable)
        {
            return false;
        }

        runKey.Set(Name, Command);
        return true;
    }

    /// <summary>Löscht nur den eigenen Eintrag.</summary>
    public void Disable() => runKey.Delete(Name);
}
