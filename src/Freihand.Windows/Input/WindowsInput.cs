using Freihand.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Freihand.Windows.Input;

/// <summary><see cref="IHotkeyApi"/> über <c>RegisterHotKey</c> auf dem eigenen Nachrichtenfenster (Kapitel 5.1).</summary>
internal sealed class HotkeyApi(MessageWindow window) : IHotkeyApi
{
    public bool Register(int id, uint modifiers, int virtualKey) =>
        window.Invoke(() => (bool)PInvoke.RegisterHotKey(window.Handle, id, (HOT_KEY_MODIFIERS)modifiers, (uint)virtualKey));

    public void Unregister(int id) => window.Invoke(() => (bool)PInvoke.UnregisterHotKey(window.Handle, id));
}

/// <summary><see cref="IKeyStateProbe"/> über <c>GetAsyncKeyState</c>; nur Tasten eigener Kombinationen (SI-05).</summary>
internal sealed class AsyncKeyStateProbe : IKeyStateProbe
{
    public bool IsDown(int virtualKey) => (PInvoke.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}

/// <summary>
/// Layoutprüfung mit <c>ToUnicodeEx</c> und eigenem Tastenzustand (Strg, Alt, allenfalls Umschalt), Flag 0x4, damit ein
/// angefangenes Tottastenzeichen nicht verloren geht; liest keinen echten Tastenzustand (Kapitel 5.1).
/// </summary>
internal sealed class KeyboardLayoutInfo(Func<HKL>? layout = null) : IKeyboardLayoutInfo
{
    private const uint KeepKeyboardState = 0x4;
    private const int Shift = 0x10;
    private const int Control = 0x11;
    private const int Alt = 0x12;

    public string? AltGrCharacter(int virtualKey, bool shift)
    {
        var hkl = (layout ?? ActiveLayout)();
        Span<byte> state = stackalloc byte[256];
        state[Control] = 0x80;
        state[Alt] = 0x80;
        if (shift)
        {
            state[Shift] = 0x80;
        }

        Span<char> buffer = stackalloc char[8];
        unsafe
        {
            var scanCode = PInvoke.MapVirtualKeyEx((uint)virtualKey, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC, hkl);
            fixed (byte* keyState = state)
            fixed (char* chars = buffer)
            {
                var count = PInvoke.ToUnicodeEx((uint)virtualKey, scanCode, keyState, chars, buffer.Length, KeepKeyboardState, hkl);
                return count > 0 ? new string(buffer[..count]) : null;
            }
        }
    }

    /// <summary>Layout des Vordergrundfensters, sonst des eigenen Threads.</summary>
    private static HKL ActiveLayout()
    {
        var foreground = PInvoke.GetForegroundWindow();
        var thread = foreground.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(foreground);
        return PInvoke.GetKeyboardLayout(thread);
    }
}

/// <summary><see cref="ISendInputApi"/> als dünne Hülle um <c>SendInput</c> (Kapitel 5.7).</summary>
internal sealed class SendInputApi : ISendInputApi
{
    /// <summary>Rechte Strg- und Alt-Taste, Windows-Tasten: erweiterte Tasten.</summary>
    private static readonly HashSet<int> Extended = [0xA3, 0xA5, 0x5B, 0x5C];

    public unsafe int Send(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return 0;
        }

        var inputs = new INPUT[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            inputs[i].type = INPUT_TYPE.INPUT_KEYBOARD;
            ref var key = ref inputs[i].Anonymous.ki;
            var up = item.Down ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
            if (item.Kind == InputEventKind.Unicode)
            {
                key.wScan = (ushort)item.Code;
                key.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | up;
            }
            else
            {
                key.wVk = (VIRTUAL_KEY)item.Code;
                key.dwFlags = up | (Extended.Contains(item.Code) ? KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY : 0);
            }
        }

        return (int)PInvoke.SendInput(inputs, sizeof(INPUT));
    }
}
