using System.Runtime.InteropServices;
using Freihand.Core.Platform;
using Freihand.Windows.Input;

namespace Freihand.Windows.Tests.Desktop;

/// <summary>
/// Eingabehelfer der Desktoptests (Prüfung PR #34, U34-11): Vor jeder Ab-Eingabe muss das eigene Testfenster im
/// Vordergrund sein; sonst bricht der Test ohne weitere Ab-Eingabe ab. Der Vordergrund wird nie still zurückgeholt.
/// Selbst gedrückte Tasten gibt <see cref="Dispose"/> in umgekehrter Reihenfolge frei. Jede Eingabe muss
/// <c>SendInput</c> einfügen; eine nicht eingefügte Freigabe bleibt vermerkt und wird gemeldet.
/// </summary>
internal sealed class GuardedKeys(Func<bool> ownWindowInForeground, ISendInputApi sender) : IDisposable
{
    private readonly List<int> _pressed = [];

    public IReadOnlyList<int> Pressed => _pressed;

    /// <summary>Für ein konkretes Testfenster: dessen Rahmen muss das Vordergrundfenster sein.</summary>
    public static GuardedKeys For(nint frame) => new(() => GetForegroundWindow() == frame, new SendInputApi());

    public void Down(int virtualKey)
    {
        if (!ownWindowInForeground())
        {
            throw new ForegroundLostException(virtualKey);
        }

        if (!Send(virtualKey, down: true))
        {
            throw new InputNotInsertedException($"Taste 0x{virtualKey:X2} ab nicht eingefügt.");
        }

        // Wiederholtes Ab derselben Taste (automatische Wiederholung) ist eine gedrückte Taste
        if (!_pressed.Contains(virtualKey))
        {
            _pressed.Add(virtualKey);
        }
    }

    public void Up(int virtualKey)
    {
        if (!Send(virtualKey, down: false))
        {
            throw new InputNotInsertedException($"Taste 0x{virtualKey:X2} auf nicht eingefügt.");
        }

        _pressed.Remove(virtualKey);
    }

    /// <summary>Antippen: ab und auf, mit Prüfung vor dem Ab.</summary>
    public void Tap(int virtualKey)
    {
        Down(virtualKey);
        Up(virtualKey);
    }

    /// <summary>Versucht jede verbleibende Freigabe und meldet danach die, die nicht eingefügt wurden.</summary>
    public void Dispose()
    {
        foreach (var key in Enumerable.Reverse(_pressed).ToList())
        {
            if (Send(key, down: false))
            {
                _pressed.Remove(key);
            }
        }

        if (_pressed.Count > 0)
        {
            throw new InputNotInsertedException(
                "Freigabe nicht eingefügt: " + string.Join(", ", _pressed.Select(key => $"0x{key:X2}")) + ".");
        }
    }

    private bool Send(int virtualKey, bool down) =>
        sender.Send([new InputEvent(InputEventKind.VirtualKey, virtualKey, down, Unit: 0)]) == 1;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}

/// <summary>Das eigene Testfenster ist nicht mehr im Vordergrund; nichts weiter eingespeist.</summary>
internal sealed class ForegroundLostException(int virtualKey)
    : Exception($"Testfenster nicht mehr im Vordergrund; Taste 0x{virtualKey:X2} nicht eingespeist.");

/// <summary><c>SendInput</c> hat eine Eingabe des Tests nicht eingefügt.</summary>
internal sealed class InputNotInsertedException(string message) : Exception(message);
