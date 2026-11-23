namespace Freihand.Windows;

/// <summary>
/// Einstieg der Windows-Schicht. Die Umsetzungen der Plattformverträge aus <c>Freihand.Core.Platform</c>
/// folgen in Paket P7 und P8.
/// </summary>
public static class WindowsPlatform
{
    /// <summary>Windows 10 Version 2004 (Build 19041); ab hier gibt es u. a. <c>WDA_EXCLUDEFROMCAPTURE</c> (SI-10).</summary>
    public static Version MinimumVersion { get; } = new(10, 0, 19041);

    public static bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(MinimumVersion.Major, MinimumVersion.Minor, MinimumVersion.Build);
}
