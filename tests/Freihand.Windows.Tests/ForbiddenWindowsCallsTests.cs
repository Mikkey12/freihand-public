using System.Reflection;
using System.Runtime.InteropServices;

namespace Freihand.Windows.Tests;

/// <summary>Stufe B des P7-Konzepts (Kapitel 5.10, 7.2): kein globaler Hook, kein Mitlesen (SI-05).</summary>
public sealed class ForbiddenWindowsCallsTests
{
    private static readonly string[] Forbidden =
    [
        "SetWindowsHookExA", "SetWindowsHookExW", "SetWinEventHook", "RegisterRawInputDevices", "GetRawInputData",
        "GetRawInputBuffer", "GetKeyboardState", "GetKeyState", "keybd_event", "mouse_event", "BlockInput",
        "AttachThreadInput",
    ];

    [Fact]
    public void W20_WindowsLayerDeclaresNoForbiddenWindowsCall()
    {
        Assert.Empty(ForbiddenCalls(typeof(WindowsPlatform).Assembly));
    }

    [Fact]
    public void W20_ScannerFindsAForbiddenDeclaration()
    {
        // Kontrolle: Die Suche erkennt eine verbotene Deklaration auch unter anderem Methodennamen (EntryPoint);
        // die Deklaration unten wird nie aufgerufen
        Assert.Equal(["GetKeyState"], ForbiddenCalls(typeof(ForbiddenWindowsCallsTests).Assembly));
    }

    /// <summary>Einstiegspunkte aller P/Invoke-Methoden (auch generierter und verschachtelter), die verboten sind.</summary>
    private static List<string> ForbiddenCalls(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly;
        return
        [
            .. assembly.GetTypes()
                .SelectMany(type => type.GetMethods(all))
                .Where(method => method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
                .Select(method => method.GetCustomAttribute<DllImportAttribute>()?.EntryPoint ?? method.Name)
                .Where(entry => Forbidden.Contains(entry, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    [DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short ControlProbe(int virtualKey);
}
