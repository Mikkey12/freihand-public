using System.Runtime.InteropServices;

namespace Freihand.Benchmark;

/// <summary>Netz- oder Akkubetrieb für die Messung auf dem Referenznotebook (QM-08).</summary>
internal static partial class PowerStatus
{
    public static bool? IsOnBattery() =>
        GetSystemPowerStatus(out var status) && status.ACLineStatus != 255 ? status.ACLineStatus == 0 : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
