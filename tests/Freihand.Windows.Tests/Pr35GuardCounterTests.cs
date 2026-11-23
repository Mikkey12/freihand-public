using System.Reflection;
using System.Runtime.CompilerServices;
using Freihand.Windows.Input;
using Freihand.Windows.Targets;
using Windows.Win32.UI.Accessibility;

namespace Freihand.Windows.Tests;

/// <summary>SI-04, P9/P7 Fokusereignisse: UIA-Attrappen, kein Fenster, kein Desktopzugriff.</summary>
public sealed class Pr35GuardCounterTests
{
    [Fact]
    public async Task C10_StoppedGuardStartsNoFurtherPropertyRead()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new PausedLog(entered, release);
        var automation = DispatchProxy.Create<IUIAutomation, Proxy>();
        var access = (UiaAccess)RuntimeHelpers.GetUninitializedObject(typeof(UiaAccess));
        Set(access, "<Automation>k__BackingField", automation);
        Set(access, "_log", log);
        var window = (MessageWindow)RuntimeHelpers.GetUninitializedObject(typeof(MessageWindow));
        Set(window, "_thread", Thread.CurrentThread);
        using var signals = new WindowsGuardSignals(window, access);
        // Nur der Startzustand wird gesetzt: keine WTS-Anmeldung und kein HWND werden erzeugt.
        Set(signals, "_started", true);
        var stopped = 0;
        var readsAfterStop = 0;
        var totalReads = 0;
        var element = DispatchProxy.Create<IUIAutomationElement, Proxy>();
        ((Proxy)(object)element).Read = () =>
        {
            Interlocked.Increment(ref totalReads);
            if (Volatile.Read(ref stopped) != 0) Interlocked.Increment(ref readsAfterStop);
            return false;
        };
        var handler = (IUIAutomationFocusChangedEventHandler)typeof(WindowsGuardSignals)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(signals)!;
        var delivery = Task.Run(() => handler.HandleFocusChangedEvent(element));
        Task? stop = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop = Task.Run(() =>
            {
                // Invoke führt nur WTSUnRegisterSessionNotification(HWND 0) direkt aus: keine reale Registrierung.
                Set(window, "_thread", Thread.CurrentThread);
                signals.Stop();
                Volatile.Write(ref stopped, 1);
            });
            // Eine korrekte Synchronisierung darf auf die laufende Abfrage warten.
            await Task.WhenAny(stop, Task.Delay(1000));
        }
        finally
        {
            release.Set();
            await delivery.WaitAsync(TimeSpan.FromSeconds(10));
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.InRange(totalReads, 0, 1); // Verwerfen ist ebenso erlaubt wie Abschluss vor Stop.
        Assert.Equal(0, readsAfterStop);
    }

    private static void Set(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public class Proxy : DispatchProxy
    {
        public Func<object?>? Read { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "GetCurrentPropertyValueEx" ? Read!() : null;
    }

    private sealed class PausedLog(TaskCompletionSource entered, ManualResetEventSlim release) : IUiaAccessLog
    {
        public void Property(UIA_PROPERTY_ID property)
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }
        public void Condition(UIA_PROPERTY_ID property) { }
    }
}
