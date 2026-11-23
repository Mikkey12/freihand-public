using System.Diagnostics;
using System.Runtime.InteropServices;
using Freihand.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Freihand.Windows.Targets;

/// <summary>
/// <see cref="ITargetInspector"/> über UI Automation (P7-Konzept, Kapitel 4.2, 5.4, 5.9). Gelesen werden nur Prozess,
/// Steuerelementart, Klasse, Framework, <c>IsPassword</c>, nur lesbar, aktiviert, mehrzeilig, Integritätsstufe,
/// <c>RuntimeId</c>, <c>AutomationId</c> und Fensterhandle; nie Name, Wert, Text, Hilfetext oder Fenstertitel (SI-12).
/// </summary>
internal sealed class UiaTargetInspector : ITargetInspector, IDisposable
{
    private const int MultiLineStyle = 0x0004;
    private const int MaxParentSteps = 64;


    private static readonly Dictionary<int, string> ControlTypes = new()
    {
        [50000] = "Button", [50003] = "ComboBox", [50004] = "Edit", [50020] = "Text", [50025] = "Custom", [50026] = "Group",
        [50030] = "Document", [50032] = "Window", [50033] = "Pane",
    };

    private readonly object _gate = new();
    private readonly UiaAccess _uia;
    private readonly Dictionary<ElementIdentity, IUIAutomationElement> _known = [];
    private readonly FocusHandler _handler;

    /// <summary>Element des Fokusereignisses, das gerade zugestellt wird; <see cref="Focused"/> beantwortet es dann damit.</summary>
    private readonly AsyncLocal<IUIAutomationElement?> _delivering = new();

    /// <summary>Fokusereignisse gehen der Reihe nach, eins nach dem anderen, an die Abonnenten.</summary>
    private Task _delivery = Task.CompletedTask;
    private EventHandler? _focusChanged;
    private bool _subscribed;

    /// <summary>Wechselt mit jedem An- und Abmelden eines Abonnenten; Ereignisse einer früheren Generation verfallen.</summary>
    private long _generation;

    /// <summary>
    /// Meldet das UIA-Fokusereignis einmal für die Lebensdauer des Inspektors an. UIA liefert bei jeder Anmeldung sofort ein
    /// veraltetes Fokusereignis nach (gemessen am 03.11.2026: Taskleiste, obwohl der Fokus woanders lag). Weil im
    /// Konstruktor noch niemand zuhört, wird es ungelesen verworfen; <see cref="WaitForRegistration"/> bestätigt das erst
    /// danach. Ausgewertet werden Ereignisse nur, solange jemand zuhört, also solange eine Bindung besteht; ohne Abonnenten
    /// liest der Inspektor nichts (Kapitel 4.2, 5.9).
    /// </summary>
    public UiaTargetInspector(UiaAccess uia)
    {
        ArgumentNullException.ThrowIfNull(uia);
        _uia = uia;
        _handler = new FocusHandler(this);
        UiaAccess.OnMta(() =>
        {
            _uia.Automation.AddFocusChangedEventHandler(null!, _handler);
            return true;
        });
        _subscribed = true;
    }

    /// <summary>
    /// Wartet, bis das nachgelieferte Ereignis der Anmeldung eingetroffen und ungelesen verworfen ist. Falsch heisst: UIA hat
    /// die Anmeldung nicht bestätigt; dann darf der Start nicht weitergehen (<see cref="WindowsTargets"/>).
    /// </summary>
    internal bool WaitForRegistration(TimeSpan timeout) => _handler.Registered.Wait(timeout);

    /// <summary>Ausgewertet nur, solange jemand zuhört, also solange eine Bindung besteht (Kapitel 4.2).</summary>
    public event EventHandler? FocusChanged
    {
        add
        {
            lock (_gate)
            {
                _focusChanged += value;
                _generation++;
            }
        }

        remove
        {
            lock (_gate)
            {
                _focusChanged -= value;
                _generation++;
            }
        }
    }

    /// <summary>
    /// Fokus mit Kennung und Merkmalen. Während der Zustellung eines Fokusereignisses ist es das Element dieses Ereignisses,
    /// nicht der inzwischen vielleicht schon andere Fokus: Ein fremder Zwischenfokus geht so nicht verloren (Kapitel 5.4).
    /// </summary>
    public (ElementIdentity Identity, TargetFacts Facts)? Focused() => UiaAccess.OnMta(() =>
    {
        IUIAutomationElement element;
        try
        {
            element = _delivering.Value ?? _uia.Automation.GetFocusedElement();
        }
        catch (COMException)
        {
            return ((ElementIdentity, TargetFacts)?)null;
        }

        if (element is null)
        {
            return null;
        }

        var described = Describe(element);
        lock (_gate)
        {
            if (_known.Count > 16)
            {
                _known.Clear();
            }

            _known[described.Identity] = element;
        }

        return described;
    });

    /// <summary>Das zuletzt gesehene Element mit dieser Kennung, sonst eine Suche nach der <c>RuntimeId</c> im Fenster.</summary>
    public bool Exists(ElementIdentity identity) => UiaAccess.OnMta(() =>
    {
        ArgumentNullException.ThrowIfNull(identity);
        IUIAutomationElement? known;
        lock (_gate)
        {
            _known.TryGetValue(identity, out known);
        }

        if (known is not null)
        {
            return RuntimeId(known) == identity.RuntimeId;
        }

        try
        {
            var runtimeId = identity.RuntimeId.Split('.').Select(int.Parse).ToArray();
            var window = _uia.Automation.ElementFromHandle(new HWND((nint)identity.TopLevelWindow));
            return window.FindFirst(TreeScope.TreeScope_Subtree, _uia.Condition(UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId, runtimeId)) is not null;
        }
        catch (Exception exception) when (exception is COMException or FormatException or OverflowException)
        {
            return false;
        }
    });

    public int CountByAutomationId(long topLevelWindow, string automationId) => UiaAccess.OnMta(() =>
    {
        ArgumentNullException.ThrowIfNull(automationId);
        try
        {
            var window = _uia.Automation.ElementFromHandle(new HWND((nint)topLevelWindow));
            return window.FindAll(TreeScope.TreeScope_Subtree, _uia.Condition(UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, automationId)).Length;
        }
        catch (COMException)
        {
            return 0;
        }
    });

    public bool OnSecureDesktop() => InputDesktop.Name() is not "Default";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                Unsubscribe();
            }

            _focusChanged = null;
            _generation++;
        }
    }

    /// <summary>Kennung und Merkmale; nicht lesbare Merkmale bleiben <c>null</c> (Kapitel 5.5).</summary>
    internal (ElementIdentity Identity, TargetFacts Facts) Describe(IUIAutomationElement element)
    {
        var processId = _uia.ReadValue<int>(element, UIA_PROPERTY_ID.UIA_ProcessIdPropertyId) ?? 0;
        var controlType = _uia.ReadValue<int>(element, UIA_PROPERTY_ID.UIA_ControlTypePropertyId) is { } id
            ? ControlTypes.GetValueOrDefault(id, $"ControlType{id}")
            : string.Empty;
        var className = _uia.Read<string>(element, UIA_PROPERTY_ID.UIA_ClassNamePropertyId) ?? string.Empty;
        var frameworkId = _uia.Read<string>(element, UIA_PROPERTY_ID.UIA_FrameworkIdPropertyId) ?? string.Empty;
        var automationId = _uia.Read<string>(element, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId) ?? string.Empty;
        var handle = NativeHandle(element);
        var topLevel = TopLevelWindow(element, handle);
        var identity = new ElementIdentity(
            processId, ProcessStart(processId), topLevel, RuntimeId(element) ?? string.Empty, controlType, className, automationId);
        var facts = new TargetFacts(
            ProcessName(processId),
            controlType,
            className,
            frameworkId,
            IsPassword: _uia.ReadValue<bool>(element, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId),
            IsReadOnly: _uia.ReadValue<bool>(element, UIA_PROPERTY_ID.UIA_ValueIsReadOnlyPropertyId),
            IsEnabled: _uia.ReadValue<bool>(element, UIA_PROPERTY_ID.UIA_IsEnabledPropertyId),
            IsMultiLine: MultiLine(handle),
            IsElevated: IsElevated(processId),
            IsOwnProcess: processId == Environment.ProcessId);
        return (identity, facts);
    }

    /// <summary>Stil mehrzeilig; <c>null</c> ohne Fensterhandle oder wenn <c>GetWindowLong</c> scheitert (Kapitel 5.5).</summary>
    private static bool? MultiLine(nint handle)
    {
        if (handle == 0)
        {
            return null;
        }

        Marshal.SetLastPInvokeError(0);
        var style = PInvoke.GetWindowLong(new HWND(handle), WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        return style == 0 && Marshal.GetLastPInvokeError() != 0 ? null : (style & MultiLineStyle) != 0;
    }

    private string? RuntimeId(IUIAutomationElement element) =>
        _uia.Read<int[]>(element, UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId) is { Length: > 0 } parts ? string.Join('.', parts) : null;

    private nint NativeHandle(IUIAutomationElement element) =>
        _uia.ReadValue<int>(element, UIA_PROPERTY_ID.UIA_NativeWindowHandlePropertyId) ?? 0;

    /// <summary>Hauptfenster über das nächste Element mit eigenem Fensterhandle.</summary>
    private unsafe long TopLevelWindow(IUIAutomationElement element, nint handle)
    {
        try
        {
            var current = element;
            for (var step = 0; handle == 0 && current is not null && step < MaxParentSteps; step++)
            {
                current = _uia.Automation.RawViewWalker.GetParentElement(current);
                handle = current is null ? 0 : NativeHandle(current);
            }
        }
        catch (COMException)
        {
            return 0;
        }

        return handle == 0 ? 0 : (nint)PInvoke.GetAncestor(new HWND(handle), GET_ANCESTOR_FLAGS.GA_ROOT).Value;
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static unsafe long ProcessStart(int processId)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsNull)
        {
            return 0;
        }

        try
        {
            System.Runtime.InteropServices.ComTypes.FILETIME creation, exit, kernel, user;
            return PInvoke.GetProcessTimes(process, &creation, &exit, &kernel, &user)
                ? ((long)(uint)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime
                : 0;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    /// <summary>Höhere Integritätsstufe als Freihand; <c>null</c>, wenn sie nicht feststellbar ist (AU-01).</summary>
    private static bool? IsElevated(int processId) =>
        IntegrityLevel(processId) is { } target && IntegrityLevel(Environment.ProcessId) is { } own ? target > own : null;

    private static unsafe uint? IntegrityLevel(int processId)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsNull)
        {
            return null;
        }

        HANDLE token = default;
        try
        {
            if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
            {
                return null;
            }

            uint length;
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, null, 0, &length);
            if (length == 0)
            {
                return null;
            }

            var buffer = stackalloc byte[(int)length];
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, length, &length))
            {
                return null;
            }

            var sid = ((TOKEN_MANDATORY_LABEL*)buffer)->Label.Sid;
            var count = *PInvoke.GetSidSubAuthorityCount(sid);
            return count == 0 ? null : *PInvoke.GetSidSubAuthority(sid, (uint)(count - 1));
        }
        finally
        {
            if (!token.IsNull)
            {
                PInvoke.CloseHandle(token);
            }

            PInvoke.CloseHandle(process);
        }
    }

    private void Unsubscribe()
    {
        UiaAccess.OnMta(() =>
        {
            try
            {
                _uia.Automation.RemoveFocusChangedEventHandler(_handler);
            }
            catch (COMException)
            {
                // UIA ist schon getrennt; es kommen keine Ereignisse mehr
            }

            return true;
        });
        _subscribed = false;
    }

    /// <summary>
    /// Stellt ein Fokusereignis zu: geordnet nach den vorherigen, mit seinem eigenen Element als Antwort auf
    /// <see cref="Focused"/>. Ein Fehler eines Abonnenten wird aufgefangen, damit er die folgenden Ereignisse nicht aufhält.
    /// </summary>
    private void Deliver(IUIAutomationElement? element, long generation)
    {
        EventHandler? handler;
        lock (_gate)
        {
            // Aufgestaute Ereignisse einer früheren Bindung erreichen keine neue
            handler = generation == _generation ? _focusChanged : null;
        }

        if (handler is null)
        {
            return;
        }

        _delivering.Value = element;
        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeliveryFailures++;
        }
        finally
        {
            _delivering.Value = null;
        }
    }

    /// <summary>Zahl der Zustellungen, bei denen ein Abonnent scheiterte (beobachtet, nicht verschluckt).</summary>
    internal int DeliveryFailures { get; private set; }

    /// <summary>
    /// Reicht das UIA-Fokusereignis ohne Warten weiter, samt dem Element, das den Fokus erhielt; ohne Abonnenten verwirft
    /// es das Ereignis ungelesen.
    /// </summary>
    [ComVisible(true)]
    private sealed class FocusHandler(UiaTargetInspector owner) : IUIAutomationFocusChangedEventHandler
    {
        /// <summary>Gesetzt beim ersten Ereignis, also beim nachgelieferten Ereignis der Anmeldung.</summary>
        public ManualResetEventSlim Registered { get; } = new();

        public void HandleFocusChangedEvent(IUIAutomationElement sender)
        {
            lock (owner._gate)
            {
                // Ohne Abonnenten ungelesen verworfen, so auch das nachgelieferte Ereignis der Anmeldung
                if (owner._focusChanged is not null)
                {
                    var generation = owner._generation;
                    owner._delivery = owner._delivery.ContinueWith(
                        _ => owner.Deliver(sender, generation), CancellationToken.None, TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
            }

            // Erst nach dem Verwerfen: bestätigt die Anmeldung
            Registered.Set();
        }
    }
}
