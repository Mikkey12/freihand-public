using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Freihand.Windows.TestTarget;

/// <summary>
/// Eigene Testfenster für die Desktoptests (P7-Konzept, Kapitel 7.3), in einem eigenen Prozess, damit Freihand sie nicht als
/// eigenes Programm abweist. Befehle zeilenweise über die Standardeingabe, Antwort je eine Zeile; Texte in Base64.
/// Nur erfundene Testtexte.
/// </summary>
internal static unsafe class Program
{
    private const uint InvokeMessage = PInvoke.WM_APP + 1;
    private const int MultiLine = 0x0004;
    private const int Password = 0x0020;
    private const int AutoVScroll = 0x0040;
    private const int AutoHScroll = 0x0080;
    private const int WantReturn = 0x1000;
    private const int KeyY = 0x59;
    private const int WindowProcedureIndex = -4;

    private static readonly List<SafeHandle> Layouts = [];

    private static readonly Dictionary<string, TestWindow> Windows = [];
    private static readonly WNDPROC FrameProcedure = Frame;
    private static readonly WNDPROC EditProcedure = Edit;
    private static readonly Dictionary<nint, nint> OriginalProcedures = [];
    private static HWND _control;
    private static uint _uiThread;

    public static int Main()
    {
        var ready = new ManualResetEventSlim();
        var ui = new Thread(() => RunUi(ready)) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ready.Wait();
        Console.Out.WriteLine("bereit");
        Console.Out.Flush();
        while (Console.In.ReadLine() is { } line)
        {
            var parts = line.Split(' ');
            if (parts[0] == "quit")
            {
                break;
            }

            string answer;
            try
            {
                answer = OnUi(() => Execute(parts));
            }
            catch (Exception exception)
            {
                answer = "fehler " + exception.GetType().Name;
            }

            Console.Out.WriteLine(answer);
            Console.Out.Flush();
        }

        return 0;
    }

    private static string Execute(string[] parts) => parts[0] switch
    {
        "window" => ((nint)CreateTestWindow(parts[1], parts[2]).Edit.Value).ToString(),
        "title" => Set(PInvoke.SetWindowText(Windows[parts[1]].Frame, Decode(parts[2]))),
        "settext" => SetText(Windows[parts[1]].Edit, Decode(parts[2])),
        "gettext" => Encode(GetText(Windows[parts[1]].Edit)),
        "focus" => Focus(Windows[parts[1]]) ? "1" : "0",
        "frame" => ((nint)Windows[parts[1]].Frame.Value).ToString(),
        "keys" => Windows[parts[1]].YDowns.ToString(),
        "layout" => Layout(parts[1]),
        "close" => Close(parts[1]),
        "pid" => Environment.ProcessId.ToString(),
        _ => "unbekannt",
    };

    private static void RunUi(ManualResetEventSlim ready)
    {
        _uiThread = PInvoke.GetCurrentThreadId();
        RegisterFrameClass();
        _control = PInvoke.CreateWindowEx(0, "FreihandTestFrame", "Steuerung", 0, 0, 0, 0, 0, new HWND(-3), default, default, null);
        ready.Set();
        while (PInvoke.GetMessage(out var message, default, 0, 0) > 0)
        {
            PInvoke.TranslateMessage(message);
            PInvoke.DispatchMessage(message);
        }
    }

    private static void RegisterFrameClass()
    {
        fixed (char* name = "FreihandTestFrame")
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = FrameProcedure,
                hInstance = PInvoke.GetModuleHandle((PCWSTR)null),
                lpszClassName = name,
            };
            PInvoke.RegisterClassEx(windowClass);
        }
    }

    private static TestWindow CreateTestWindow(string id, string kind)
    {
        var frame = PInvoke.CreateWindowEx(
            0, "FreihandTestFrame", "Freihand Testfenster " + id, WINDOW_STYLE.WS_OVERLAPPEDWINDOW | WINDOW_STYLE.WS_VISIBLE,
            100 + (Windows.Count * 40), 100 + (Windows.Count * 40), 600, 400, default, default, default, null);
        var style = (int)(WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_BORDER) | AutoHScroll | kind switch
        {
            "multi" => MultiLine | AutoVScroll | WantReturn,
            "password" => Password,
            _ => 0,
        };
        var edit = PInvoke.CreateWindowEx(0, "EDIT", string.Empty, (WINDOW_STYLE)style, 0, 0, 580, 360, frame, default, default, null);
        var window = new TestWindow(frame, edit);
        Windows[id] = window;
        var original = SetWindowLongPtr(edit, WindowProcedureIndex, Marshal.GetFunctionPointerForDelegate(EditProcedure));
        OriginalProcedures[(nint)edit.Value] = original;
        return window;
    }

    /// <summary>Holt das Testfenster in den Vordergrund; meldet, ob es dort ist.</summary>
    private static bool Focus(TestWindow window)
    {
        var foreground = PInvoke.GetForegroundWindow();
        var foregroundThread = foreground.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(foreground);
        var attached = foregroundThread != 0 && foregroundThread != _uiThread && PInvoke.AttachThreadInput(foregroundThread, _uiThread, true);
        try
        {
            PInvoke.ShowWindow(window.Frame, SHOW_WINDOW_CMD.SW_SHOW);
            PInvoke.BringWindowToTop(window.Frame);
            PInvoke.SetForegroundWindow(window.Frame);
            PInvoke.SetFocus(window.Edit);
        }
        finally
        {
            if (attached)
            {
                PInvoke.AttachThreadInput(foregroundThread, _uiThread, false);
            }
        }

        return PInvoke.GetForegroundWindow() == window.Frame;
    }

    private static string SetText(HWND edit, string text)
    {
        fixed (char* chars = text)
        {
            PInvoke.SendMessage(edit, PInvoke.WM_SETTEXT, default, new LPARAM((nint)chars));
        }

        return "1";
    }

    private static string GetText(HWND edit)
    {
        var length = (int)PInvoke.SendMessage(edit, PInvoke.WM_GETTEXTLENGTH, default, default).Value;
        var buffer = new char[length + 1];
        fixed (char* chars = buffer)
        {
            var copied = (int)PInvoke.SendMessage(edit, PInvoke.WM_GETTEXT, new WPARAM((nuint)buffer.Length), new LPARAM((nint)chars)).Value;
            return new string(buffer, 0, copied);
        }
    }

    private static string Close(string id)
    {
        if (Windows.Remove(id, out var window))
        {
            PInvoke.DestroyWindow(window.Frame);
        }

        return "1";
    }

    private static string Set(BOOL result) => result ? "1" : "0";

    /// <summary>Lädt und aktiviert ein Tastaturlayout für den UI-Thread; der Griff bleibt bis zum Ende erhalten.</summary>
    private static string Layout(string id)
    {
        var layout = PInvoke.LoadKeyboardLayout(id, ACTIVATE_KEYBOARD_LAYOUT_FLAGS.KLF_ACTIVATE);
        Layouts.Add(layout);
        return layout.DangerousGetHandle().ToString("X");
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(HWND window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern LRESULT CallWindowProc(nint procedure, HWND window, uint message, WPARAM wParam, LPARAM lParam);

    private static string Decode(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string OnUi(Func<string> function)
    {
        string? result = null;
        Exception? failure = null;
        Action call = () =>
        {
            try
            {
                result = function();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        };
        var handle = GCHandle.Alloc(call);
        try
        {
            PInvoke.SendMessage(_control, InvokeMessage, new WPARAM((nuint)GCHandle.ToIntPtr(handle)), default);
        }
        finally
        {
            handle.Free();
        }

        return failure is null ? result! : throw failure;
    }

    private static LRESULT Frame(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == InvokeMessage)
        {
            ((Action)GCHandle.FromIntPtr((nint)wParam.Value).Target!)();
            return default;
        }

        if (message == PInvoke.WM_SIZE && Windows.Values.FirstOrDefault(item => item.Frame == window) is { } test)
        {
            PInvoke.MoveWindow(test.Edit, 0, 0, (int)(lParam.Value & 0xFFFF), (int)((lParam.Value >> 16) & 0xFFFF), true);
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>Zählt <c>WM_KEYDOWN</c> mit Y im Testfeld (W22), sonst wie das Original.</summary>
    private static LRESULT Edit(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_KEYDOWN && (int)wParam.Value == KeyY && Windows.Values.FirstOrDefault(item => item.Edit == window) is { } test)
        {
            test.YDowns++;
        }

        var original = OriginalProcedures[(nint)window.Value];
        return CallWindowProc(original, window, message, wParam, lParam);
    }

    private sealed class TestWindow(HWND frame, HWND edit)
    {
        public HWND Frame { get; } = frame;

        public HWND Edit { get; } = edit;

        public int YDowns { get; set; }
    }
}
