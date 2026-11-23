using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.UI.Accessibility;

namespace Freihand.Windows.Targets;

/// <summary>
/// Zugriffsprotokoll der UIA-Hülle: nur Kennungen abgefragter Eigenschaften und Bedingungen, nie Werte (P7-Konzept,
/// Kapitel 5.9). Im Betrieb ohne Wirkung; W26 prüft damit, dass nur erlaubte Kennungen abgefragt werden.
/// </summary>
internal interface IUiaAccessLog
{
    void Property(UIA_PROPERTY_ID property);

    void Condition(UIA_PROPERTY_ID property);
}

/// <summary>
/// Schmale Hülle um UI Automation (UIA3): Jede Eigenschaft wird über <see cref="Read"/> gelesen und protokolliert. Ein
/// Fehler beim Lesen (COM-Fehler, Zeitgrenze, nicht unterstützt) ergibt <c>null</c> (Kapitel 5.5). Die Aufrufe laufen
/// auf einem MTA-Thread, damit UIA nie den UI-Thread der eigenen App blockiert.
/// </summary>
internal sealed class UiaAccess
{
    /// <summary><c>CUIAutomation8</c> bietet <see cref="IUIAutomation2"/> mit den Zeitgrenzen; <c>CUIAutomation</c> nicht.</summary>
    private static readonly Guid AutomationClass = typeof(CUIAutomation8).GUID;
    private static readonly Guid AutomationInterface = typeof(IUIAutomation).GUID;
    private readonly IUiaAccessLog? _log;

    public UiaAccess(IUiaAccessLog? log = null, TimeSpan? timeout = null)
    {
        _log = log;
        Automation = OnMta(Create);
        // Ohne gesetzte Zeitgrenzen könnte ein hängendes Zielprogramm jede Prüfung blockieren: dann kein Betrieb
        var milliseconds = (uint)(timeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds;
        if (!OperatingSystem.IsWindowsVersionAtLeast(8) || Automation is not IUIAutomation2 withTimeouts ||
            !OnMta(() => OperatingSystem.IsWindowsVersionAtLeast(8) && SetTimeouts(withTimeouts, milliseconds)))
        {
            throw new InvalidOperationException("UI Automation bietet keine Zeitgrenzen (IUIAutomation2).");
        }
    }

    /// <summary>Zeitgrenzen für Verbindung und Abfrage, damit ein hängendes Zielprogramm nie blockiert.</summary>
    [global::System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static bool SetTimeouts(IUIAutomation2 automation, uint milliseconds)
    {
        automation.ConnectionTimeout = milliseconds;
        automation.TransactionTimeout = milliseconds;
        return automation.ConnectionTimeout == milliseconds && automation.TransactionTimeout == milliseconds;
    }

    public IUIAutomation Automation { get; }

    /// <summary>Führt UIA-Aufrufe auf einem MTA-Thread aus.</summary>
    public static T OnMta<T>(Func<T> function) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA ? function() : Task.Run(function).GetAwaiter().GetResult();

    /// <summary>Wert der Eigenschaft oder <c>null</c>, wenn sie nicht lesbar oder nicht unterstützt ist.</summary>
    public T? Read<T>(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        _log?.Property(property);
        try
        {
            return element.GetCurrentPropertyValueEx(property, ignoreDefaultValue: true) is T value ? value : default;
        }
        catch (COMException)
        {
            return default;
        }
    }

    /// <summary>Wie <see cref="Read{T}"/> für Werttypen; <c>null</c> heisst nicht lesbar.</summary>
    public T? ReadValue<T>(IUIAutomationElement element, UIA_PROPERTY_ID property)
        where T : struct
    {
        _log?.Property(property);
        try
        {
            return element.GetCurrentPropertyValueEx(property, ignoreDefaultValue: true) is T value ? value : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public IUIAutomationCondition Condition(UIA_PROPERTY_ID property, object value)
    {
        _log?.Condition(property);
        return Automation.CreatePropertyCondition(property, value);
    }

    private static unsafe IUIAutomation Create()
    {
        var clsid = AutomationClass;
        var iid = AutomationInterface;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, out var instance).ThrowOnFailure();
        return (IUIAutomation)instance;
    }
}

/// <summary>Name des Eingabedesktops; <c>null</c>, wenn er nicht lesbar ist (UAC-Abfrage, Sperre).</summary>
internal static class InputDesktop
{
    private const uint ReadObjects = 0x0001;

    public static unsafe string? Name()
    {
        var desktop = PInvoke.OpenInputDesktop(0, false, (DESKTOP_ACCESS_FLAGS)ReadObjects);
        if (desktop.IsNull)
        {
            return null;
        }

        try
        {
            Span<char> name = stackalloc char[256];
            fixed (char* buffer = name)
            {
                uint needed;
                if (!PInvoke.GetUserObjectInformation(
                        new HANDLE(desktop.Value), USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                        buffer, (uint)(name.Length * sizeof(char)), &needed))
                {
                    return null;
                }

                return new string(buffer).TrimEnd('\0');
            }
        }
        finally
        {
            PInvoke.CloseDesktop(desktop);
        }
    }
}
