using System.Reflection;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Freihand.Windows.Beta;

/// <summary>Was die Nachricht beim Ende der Windows-Sitzung für Freihand heisst.</summary>
public enum SessionMessage
{
    /// <summary>Nicht betroffen: weiter an WPF.</summary>
    Pass,

    /// <summary><c>WM_QUERYENDSESSION</c>: Windows fragt nur. Antwort „darf schliessen“, Freihand bleibt offen.</summary>
    AllowAndStay,

    /// <summary><c>WM_ENDSESSION</c> mit „wahr“: Die Sitzung endet tatsächlich. Jetzt sichern und beenden.</summary>
    EndNow,

    /// <summary><c>WM_ENDSESSION</c> mit „falsch“: Der Neustart wurde abgebrochen. Freihand läuft weiter.</summary>
    Continue,
}

/// <summary>
/// Freihand schliesst erst, wenn Windows die Sitzung wirklich beendet (Befund vom 16.11.2026). WPF beendet ein Programm
/// schon auf die Rückfrage <c>WM_QUERYENDSESSION</c> (<c>Application.WmQueryEndSession</c> ruft <c>Shutdown</c>); bricht ein
/// anderes Programm den Neustart danach ab, fehlt Freihand bis zur nächsten Anmeldung. Die eingebaute Gegenwehr – die Rückfrage
/// ablehnen – würde den Neustart blockieren und ist darum ausgeschlossen.
/// <para>
/// Regel: Auf die Rückfrage antwortet Freihand „darf schliessen“, ohne sie an WPF weiterzugeben. Erst <c>WM_ENDSESSION</c> mit
/// „wahr“ beendet Freihand, nach dem Sichern; mit „falsch“ läuft es weiter. Dafür hängt sich Freihand mit
/// <c>SetWindowSubclass</c> vor die Fensterprozedur des versteckten WPF-Anwendungsfensters.
/// </para>
/// </summary>
public sealed class SessionEndGuard : IDisposable
{
    private const uint QueryEndSession = 0x0011;
    private const uint EndSession = 0x0016;
    private const nuint SubclassId = 0x46524548; // „FREH“

    private readonly HWND _window;
    private readonly Action _sessionEnds;
    private readonly Action? _sessionQueried;
    private readonly Action? _sessionContinues;
    private readonly SUBCLASSPROC _procedure;
    private bool _attached;

    private SessionEndGuard(HWND window, Action sessionEnds, Action? sessionQueried, Action? sessionContinues)
    {
        _sessionQueried = sessionQueried;
        _sessionContinues = sessionContinues;
        _window = window;
        _sessionEnds = sessionEnds;
        _procedure = Procedure;
    }

    /// <summary>Entscheid je Nachricht; ohne Fenster prüfbar.</summary>
    public static SessionMessage Decide(uint message, nuint wParam) => message switch
    {
        QueryEndSession => SessionMessage.AllowAndStay,
        EndSession => wParam != 0 ? SessionMessage.EndNow : SessionMessage.Continue,
        _ => SessionMessage.Pass,
    };

    /// <summary>
    /// Hängt den Schutz vor <paramref name="window"/>, auf dessen Thread. Null, wenn das nicht gelingt; dann bleibt das
    /// bisherige Verhalten von WPF.
    /// </summary>
    /// <param name="sessionEnds">Sichern und beenden; läuft synchron, bevor Windows die Sitzung schliesst.</param>
    public static SessionEndGuard? Attach(nint window, Action sessionEnds) => Attach(window, sessionEnds, null, null);

    /// <param name="sessionQueried">Windows kündigt das Ende an (WM_QUERYENDSESSION); z. B. keine Prozesse mehr starten.</param>
    /// <param name="sessionContinues">Das Ende ist abgebrochen (WM_ENDSESSION mit „falsch“).</param>
    public static SessionEndGuard? Attach(nint window, Action sessionEnds, Action? sessionQueried, Action? sessionContinues)
    {
        ArgumentNullException.ThrowIfNull(sessionEnds);
        if (window == 0)
        {
            return null;
        }

        var guard = new SessionEndGuard(new HWND(window), sessionEnds, sessionQueried, sessionContinues);
        guard._attached = PInvoke.SetWindowSubclass(guard._window, guard._procedure, SubclassId, 0);
        return guard._attached ? guard : null;
    }

    /// <summary>
    /// Das versteckte Anwendungsfenster von WPF (<c>Application._parkingHwnd</c>), das <c>WM_QUERYENDSESSION</c> verarbeitet;
    /// 0, wenn es nicht zu finden ist (andere WPF-Version). Ein Test hält fest, dass das Feld besteht.
    /// </summary>
    public static nint ApplicationWindow(object application)
    {
        ArgumentNullException.ThrowIfNull(application);
        // Die Windows-Schicht kennt WPF nicht: das Feld in der Typkette des Anwendungsobjekts suchen
        FieldInfo? field = null;
        for (var type = application.GetType(); type is not null && field is null; type = type.BaseType)
        {
            field = type.GetField("_parkingHwnd", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        }

        var wrapper = field?.GetValue(application);
        return wrapper?.GetType().GetProperty("Handle", BindingFlags.Public | BindingFlags.Instance)?.GetValue(wrapper) is nint handle
            ? handle
            : 0;
    }

    public void Dispose()
    {
        if (_attached)
        {
            PInvoke.RemoveWindowSubclass(_window, _procedure, SubclassId);
            _attached = false;
        }
    }

    private LRESULT Procedure(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        switch (Decide(message, wParam.Value))
        {
            case SessionMessage.AllowAndStay:
                Notify(_sessionQueried);
                return new LRESULT(1);
            case SessionMessage.EndNow:
                try
                {
                    _sessionEnds();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Die Sitzung endet so oder so; nichts darf das Ende aufhalten
                }

                return new LRESULT(0);
            case SessionMessage.Continue:
                Notify(_sessionContinues);
                return new LRESULT(0);
            default:
                return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
        }
    }

    /// <summary>Rückmeldung an Freihand; ein Fehler darin hält Windows nie auf.</summary>
    private static void Notify(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Die Antwort an Windows bleibt dieselbe
        }
    }
}
