using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Freihand.Windows.Input;

/// <summary>
/// Reines Nachrichtenfenster (<c>HWND_MESSAGE</c>) auf einem eigenen Thread mit Nachrichtenschleife (P7-Konzept,
/// Kapitel 5.1). <c>RegisterHotKey</c> und <c>WTSRegisterSessionNotification</c> müssen auf dem Thread des Fensters
/// laufen; <see cref="Invoke{T}"/> führt Aufrufe dort aus. Meldungen gehen ohne Warten an die Abonnenten.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const uint InvokeMessage = PInvoke.WM_APP + 1;
    private static readonly HWND MessageParent = new(-3);

    private readonly Thread _thread;
    private readonly WNDPROC _procedure;
    private readonly string _className = "Freihand.MessageWindow." + Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource<HWND> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public MessageWindow()
    {
        unsafe
        {
            _procedure = Procedure;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "Freihand Nachrichtenfenster" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Handle = _created.Task.GetAwaiter().GetResult();
    }

    public HWND Handle { get; }

    /// <summary><c>WM_HOTKEY</c> mit Kennung und Stopwatch-Zeitstempel beim Empfang (ER-17).</summary>
    public event Action<int, long>? Hotkey;

    /// <summary><c>WM_WTSSESSION_CHANGE</c> mit dem Ereigniscode.</summary>
    public event Action<int>? SessionChange;

    /// <summary>Führt <paramref name="function"/> auf dem Thread des Fensters aus und wartet auf das Ergebnis.</summary>
    public T Invoke<T>(Func<T> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            return function();
        }

        var call = new Call(() => function());
        var handle = GCHandle.Alloc(call);
        try
        {
            PInvoke.SendMessage(Handle, InvokeMessage, new WPARAM((nuint)GCHandle.ToIntPtr(handle)), default);
        }
        finally
        {
            handle.Free();
        }

        call.Failure?.Throw();
        return (T)call.Result!;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PInvoke.PostMessage(Handle, PInvoke.WM_CLOSE, default, default);
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private unsafe void Run()
    {
        try
        {
            fixed (char* name = _className)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = _procedure,
                    hInstance = PInvoke.GetModuleHandle((PCWSTR)null),
                    lpszClassName = name,
                };
                if (PInvoke.RegisterClassEx(windowClass) == 0)
                {
                    throw new InvalidOperationException($"RegisterClassEx scheiterte ({Marshal.GetLastPInvokeError()}).");
                }

                var handle = PInvoke.CreateWindowEx(0, name, null, 0, 0, 0, 0, 0, MessageParent, default, windowClass.hInstance, null);
                if (handle.IsNull)
                {
                    throw new InvalidOperationException($"CreateWindowEx scheiterte ({Marshal.GetLastPInvokeError()}).");
                }

                _created.SetResult(handle);
                while (PInvoke.GetMessage(out var message, default, 0, 0) > 0)
                {
                    PInvoke.TranslateMessage(message);
                    PInvoke.DispatchMessage(message);
                }

                PInvoke.UnregisterClass(name, windowClass.hInstance);
            }
        }
        catch (Exception exception)
        {
            _created.TrySetException(exception);
        }
    }

    private LRESULT Procedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_HOTKEY:
                var timestamp = Stopwatch.GetTimestamp();
                Hotkey?.Invoke((int)wParam.Value, timestamp);
                return default;

            case PInvoke.WM_WTSSESSION_CHANGE:
                SessionChange?.Invoke((int)wParam.Value);
                return default;

            case InvokeMessage:
                ((Call)GCHandle.FromIntPtr((nint)wParam.Value).Target!).Run();
                return default;

            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(window);
                return default;

            case PInvoke.WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return default;

            default:
                return PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private sealed class Call(Func<object?> function)
    {
        public object? Result { get; private set; }

        public System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure { get; private set; }

        public void Run()
        {
            try
            {
                Result = function();
            }
            catch (Exception exception)
            {
                Failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
            }
        }
    }
}
