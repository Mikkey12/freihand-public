using System.Runtime.InteropServices;
using Freihand.Core.Platform;
using Freihand.Core.Routing;
using Freihand.Windows.Input;
using Windows.Win32;
using Windows.Win32.UI.Accessibility;

namespace Freihand.Windows.Targets;

/// <summary>
/// <see cref="IGuardSignals"/>: WTS-Sitzungsmeldungen am eigenen Nachrichtenfenster, Eingabedesktop und UIA-Fokusereignis
/// (P7-Konzept, Kapitel 5.5). Die WTS-Meldungen gelten nur zwischen <see cref="Start"/> und <see cref="Stop"/>. Das
/// UIA-Fokusereignis ist dagegen einmal für die ganze Lebensdauer angemeldet und wird nur zwischen Start und Stop
/// ausgewertet: Jede neue UIA-Anmeldung liefert allen Fokusempfängern des Prozesses ein veraltetes Ereignis nach (gemessen
/// am 03.11.2026, Taskleiste), und das markierte beim Aufnahmestart die eben gebundene Ausgabe als verlassen
///.
/// </summary>
internal sealed class WindowsGuardSignals : IGuardSignals, IDisposable
{
    private const uint ThisSession = 0;
    private const int ConsoleDisconnect = 0x2;
    private const int RemoteDisconnect = 0x4;
    private const int SessionLogoff = 0x6;
    private const int SessionLock = 0x7;

    private readonly object _gate = new();
    private readonly MessageWindow _window;
    private readonly UiaAccess _uia;
    private readonly FocusHandler _handler;
    private bool _started;

    /// <summary>Laufende Lesevorgänge von <c>IsPassword</c>; <see cref="Stop"/> wartet sie ab (U35-09).</summary>
    private int _reading;

    /// <summary>Jeder Stopp zählt weiter; ein Ergebnis aus einer früheren Laufzeit wird verworfen.</summary>
    private long _generation;

    public WindowsGuardSignals(MessageWindow window, UiaAccess uia)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(uia);
        _window = window;
        _uia = uia;
        _handler = new FocusHandler(this);
        UiaAccess.OnMta(() =>
        {
            _uia.Automation.AddFocusChangedEventHandler(null!, _handler);
            return true;
        });
    }

    /// <summary>Wartet, bis das nachgelieferte Ereignis der eigenen Anmeldung eingetroffen und ungelesen verworfen ist.</summary>
    internal bool WaitForRegistration(TimeSpan timeout) => _handler.Registered.Wait(timeout);

    public event EventHandler<GuardReason>? SessionEvent;

    public event EventHandler<bool?>? FocusChanged;

    public bool? FocusIsPassword() => UiaAccess.OnMta(() =>
    {
        try
        {
            // Ohne gelieferten Fokus ist IsPassword nicht gelesen: null, nie ein sicheres false (SI-04)
            return _uia.Automation.GetFocusedElement() is { } element
                ? _uia.ReadValue<bool>(element, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId)
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    });

    public string? InputDesktopName() => InputDesktop.Name();

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            // Nur ein vollständig gelungener Start gilt; sonst sichtbar scheitern und alles zurücknehmen (SI-04)
            if (!_window.Invoke(() => (bool)PInvoke.WTSRegisterSessionNotification(_window.Handle, ThisSession)))
            {
                throw new InvalidOperationException("Die Sitzungsmeldungen liessen sich nicht anmelden.");
            }

            _window.SessionChange += OnSessionChange;
            _started = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _generation++;

            // Nach Stop beginnt und endet kein Lesen mehr: laufende Abfragen abwarten (SI-04, U35-09). Die Zustellung
            // zählt nicht mit, sonst warteten Wächter und Stop gegenseitig aufeinander.
            while (_reading > 0)
            {
                Monitor.Wait(_gate);
            }

            _window.SessionChange -= OnSessionChange;
            _window.Invoke(() => (bool)PInvoke.WTSUnRegisterSessionNotification(_window.Handle));
        }
    }

    public void Dispose()
    {
        Stop();
        UiaAccess.OnMta(() =>
        {
            try
            {
                _uia.Automation.RemoveFocusChangedEventHandler(_handler);
            }
            catch (COMException)
            {
                // UIA ist schon getrennt
            }

            return true;
        });
    }

    private void OnSessionChange(int code)
    {
        GuardReason? reason = code switch
        {
            SessionLock => GuardReason.SessionLocked,
            ConsoleDisconnect or RemoteDisconnect or SessionLogoff => GuardReason.SessionDisconnected,
            _ => null,
        };
        if (reason is { } value)
        {
            SessionEvent?.Invoke(this, value);
        }
    }

    /// <summary>
    /// Liest nur <c>IsPassword</c> des neuen Fokus, und nur zwischen Start und Stop; sonst bleibt das Ereignis ungelesen.
    /// Nicht lesbar ergibt <c>null</c>.
    /// </summary>
    [ComVisible(true)]
    private sealed class FocusHandler(WindowsGuardSignals owner) : IUIAutomationFocusChangedEventHandler
    {
        /// <summary>Gesetzt beim ersten Ereignis, also beim nachgelieferten Ereignis der Anmeldung.</summary>
        public ManualResetEventSlim Registered { get; } = new();

        public void HandleFocusChangedEvent(IUIAutomationElement sender)
        {
            long generation;
            lock (owner._gate)
            {
                if (!owner._started)
                {
                    Registered.Set();
                    return;
                }

                generation = owner._generation;
                owner._reading++;
            }

            bool? isPassword = null;
            var current = false;
            try
            {
                isPassword = sender is null ? null : owner._uia.ReadValue<bool>(sender, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId);
            }
            finally
            {
                lock (owner._gate)
                {
                    owner._reading--;
                    current = owner._started && owner._generation == generation;
                    Monitor.PulseAll(owner._gate);
                }
            }

            if (current)
            {
                owner.FocusChanged?.Invoke(owner, isPassword);
            }

            Registered.Set();
        }
    }
}

/// <summary>
/// <see cref="IForeignContentReader"/> für die Kern-Funktionen (Kapitel 5.9). Inhalte nur als <see cref="ForeignText"/>
/// mit der höchsten eingerichteten Klasse, standardmässig „vertraulich“. <see cref="ReadSelection"/> liest nur auf die
/// Taste fürs Korrigieren oder Glätten und vor dem Ersetzen (Beta B9, SI-12), nie aus einem Passwortfeld.
/// </summary>
internal sealed class UiaForeignContentReader(UiaAccess uia, DataClass? highest = null) : IForeignContentReader
{
    public SelectionRead ReadSelection(ReadPurpose purpose) => UiaAccess.OnMta(() =>
    {
        try
        {
            if (uia.Automation.GetFocusedElement() is not { } element)
            {
                return SelectionRead.Refused(SelectionRefusal.Unreadable);
            }

            // Nicht lesbar gilt hier wie ein Passwortfeld: lieber nichts lesen
            if (uia.ReadValue<bool>(element, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId) != false)
            {
                return SelectionRead.Refused(SelectionRefusal.PasswordField);
            }

            if (element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId) is not IUIAutomationTextPattern pattern)
            {
                return SelectionRead.Refused(SelectionRefusal.Unreadable);
            }

            var ranges = pattern.GetSelection();
            if (ranges is null || ranges.Length == 0)
            {
                return SelectionRead.Refused(SelectionRefusal.NoSelection);
            }

            if (ranges.Length > 1)
            {
                return SelectionRead.Refused(SelectionRefusal.SpecialContent);
            }

            // Ein Zeichen mehr als erlaubt lesen: so ist „zu lang“ erkennbar, ohne alles zu lesen
            var text = Take(ranges.GetElement(0).GetText(SelectionText.MaxLength + 1));
            return SelectionText.From(text, purpose, highest ?? DataClass.Confidential);
        }
        catch (COMException)
        {
            return SelectionRead.Refused(SelectionRefusal.Unreadable);
        }
    });

    /// <summary>Text aus dem von UIA gelieferten BSTR; der Speicher wird sofort freigegeben.</summary>
    private static unsafe string? Take(global::Windows.Win32.Foundation.BSTR text)
    {
        if (text.Value is null)
        {
            return null;
        }

        try
        {
            return new string(text.Value, 0, text.Length);
        }
        finally
        {
            Marshal.FreeBSTR((nint)text.Value);
        }
    }

    public ForeignText? ReadFocused(ReadPurpose purpose) => UiaAccess.OnMta(() =>
    {
        try
        {
            return uia.Automation.GetFocusedElement() is { } element &&
                uia.Read<string>(element, UIA_PROPERTY_ID.UIA_ValueValuePropertyId) is { } content
                ? new ForeignText(content, purpose, highest ?? DataClass.Confidential)
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    });
}
