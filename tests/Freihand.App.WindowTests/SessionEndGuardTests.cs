using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Freihand.Windows.Beta;

namespace Freihand.App;

/// <summary>
/// Sitzungsende (Befund vom 16.11.2026): Freihand schliesst erst, wenn Windows die Sitzung wirklich beendet. Ein echtes,
/// nie gezeigtes Fenster mit einem Haken an Stelle von WPFs Anwendungsfenster bekommt die Nachrichten von Windows
/// zugeschickt; kein Neustart, keine Eingaben, nur der eigene Prozess.
/// </summary>
public sealed class SessionEndGuardTests
{
    private const int QueryEndSession = 0x0011;
    private const int EndSession = 0x0016;
    private const int User = 0x0400;

    [Theory]
    [InlineData(QueryEndSession, 0, SessionMessage.AllowAndStay)]
    [InlineData(QueryEndSession, 1, SessionMessage.AllowAndStay)]
    [InlineData(EndSession, 1, SessionMessage.EndNow)]
    [InlineData(EndSession, 0, SessionMessage.Continue)]
    [InlineData(User, 1, SessionMessage.Pass)]
    public void EachMessageHasOneMeaning(int message, int wParam, SessionMessage expected) =>
        Assert.Equal(expected, SessionEndGuard.Decide((uint)message, (nuint)wParam));

    [Fact]
    public Task TheQuestionIsAnsweredYesAndNeverReachesWpf() => OnWindow((window, wpfSaw, ended) =>
    {
        var answer = SendMessage(window, QueryEndSession, 0, 0);

        Assert.Equal(1, answer);
        Assert.Empty(wpfSaw);
        Assert.Equal(0, ended());
    });

    [Fact]
    public Task ACancelledRestartKeepsFreihandRunning() => OnWindow((window, wpfSaw, ended) =>
    {
        SendMessage(window, QueryEndSession, 0, 0);
        SendMessage(window, EndSession, 0, 0);

        Assert.Equal(0, ended());
        Assert.Empty(wpfSaw);
    });

    [Fact]
    public Task ARealSessionEndEndsFreihandOnce() => OnWindow((window, wpfSaw, ended) =>
    {
        SendMessage(window, QueryEndSession, 0, 0);
        SendMessage(window, EndSession, 1, 0);

        Assert.Equal(1, ended());
    });

    [Fact]
    public Task OtherMessagesStillReachWpf() => OnWindow((window, wpfSaw, ended) =>
    {
        SendMessage(window, User, 0, 0);

        Assert.Equal([User], wpfSaw);
    });

    // Der Schutz findet WPFs Anwendungsfenster über ein privates Feld; fehlt es in einer neuen WPF-Version, fällt dieser Test
    // 18.11.2026: Ankündigung und Abbruch des Sitzungsendes werden gemeldet (keine Prozesse mehr starten, wieder freigeben)
    [Fact]
    public Task QueryAndCancelAreReported() => OnWindow((window, wpfSaw, ended) =>
    {
        Queried = 0;
        Continued = 0;
        SendMessage(window, QueryEndSession, 0, 0);
        Assert.Equal((1, 0), (Queried, Continued));
        SendMessage(window, EndSession, 0, 0);
        Assert.Equal((1, 1, 0), (Queried, Continued, ended()));
    });

    [ThreadStatic]
    private static int Queried;

    [ThreadStatic]
    private static int Continued;

    [Fact]
    public void WpfStillHasItsApplicationWindowField()
    {
        var field = typeof(Application).GetField("_parkingHwnd", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.NotNull(field!.FieldType.GetProperty("Handle", BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(0, SessionEndGuard.ApplicationWindow(new object()));
        Assert.Null(SessionEndGuard.Attach(0, () => { }));
    }

    /// <summary>Unsichtbares Fenster auf eigenem STA-Thread; der Haken spielt WPFs Anwendungsfenster und zählt, was ihn erreicht.</summary>
    private static async Task OnWindow(Action<nint, List<int>, Func<int>> body)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            HwndSource? source = null;
            SessionEndGuard? guard = null;
            try
            {
                var wpfSaw = new List<int>();
                var ended = 0;
                source = new HwndSource(new HwndSourceParameters("Freihand-Sitzungsende-Probe") { Width = 0, Height = 0, WindowStyle = 0 });
                source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg is QueryEndSession or EndSession or User)
                    {
                        wpfSaw.Add(msg);
                    }

                    return IntPtr.Zero;
                });
                guard = SessionEndGuard.Attach(source.Handle, () => ended++, () => Queried++, () => Continued++);
                Assert.NotNull(guard);
                body(source.Handle, wpfSaw, () => ended);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                guard?.Dispose();
                source?.Dispose();
                finished.TrySetResult(failure);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        if (result is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result).Throw();
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
