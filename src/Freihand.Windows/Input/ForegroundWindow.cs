using Windows.Win32;
using Windows.Win32.Foundation;

namespace Freihand.Windows.Input;

/// <summary>Ob ein Fenster das Vordergrundfenster von Windows ist (GetForegroundWindow, bereits freigegeben).</summary>
public static class ForegroundWindow
{
    public static bool Is(nint window) => window != 0 && PInvoke.GetForegroundWindow() == new HWND(window);
}
