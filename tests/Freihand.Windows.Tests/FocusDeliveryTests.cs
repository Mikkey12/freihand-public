using System.Reflection;
using System.Runtime.CompilerServices;
using Freihand.Windows.Targets;
using Windows.Win32.UI.Accessibility;

namespace Freihand.Windows.Tests;

/// <summary>
/// Zustellung der UIA-Fokusereignisse (Deltaprüfung PR #34 zu U34-07): aufgestaute Ereignisse einer früheren Bindung
/// erreichen keine neue; die Anmeldung bestätigt sich erst, wenn ihr nachgeliefertes Ereignis ungelesen verworfen ist.
/// Nur Attrappen, kein Fenster, kein Fokuswechsel.
/// </summary>
public sealed class FocusDeliveryTests
{
    [Fact]
    public async Task QueuedEventsOfAnEarlierSubscriptionNeverReachANewOne()
    {
        using var inspector = new UiaTargetInspector(Access());
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        EventHandler old = (_, _) =>
        {
            blocked.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        inspector.FocusChanged += old;
        var handler = Handler(inspector);

        var reachedNew = 0;
        EventHandler fresh = (_, _) => Interlocked.Increment(ref reachedNew);
        try
        {
            // Das erste Ereignis hängt in der alten Bindung, das zweite wartet dahinter
            handler.HandleFocusChangedEvent(Element());
            Assert.True(blocked.Wait(TimeSpan.FromSeconds(10)));
            handler.HandleFocusChangedEvent(Element());
            var pending = (Task)typeof(UiaTargetInspector)
                .GetField("_delivery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspector)!;

            // Die alte Bindung endet, eine neue beginnt, bevor das zweite zugestellt wird
            inspector.FocusChanged -= old;
            inspector.FocusChanged += fresh;
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, Volatile.Read(ref reachedNew));
        }
        finally
        {
            release.Set();
            inspector.FocusChanged -= fresh;
        }
    }

    [Fact]
    public void RegistrationIsConfirmedOnlyByItsEventAndWithoutReadingIt()
    {
        var reads = 0;
        using var inspector = new UiaTargetInspector(Access(() => Interlocked.Increment(ref reads)));
        Assert.False(inspector.WaitForRegistration(TimeSpan.FromMilliseconds(50)));

        Handler(inspector).HandleFocusChangedEvent(Element());

        Assert.True(inspector.WaitForRegistration(TimeSpan.Zero));
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref reads));
    }

    private static IUIAutomationFocusChangedEventHandler Handler(UiaTargetInspector inspector) =>
        (IUIAutomationFocusChangedEventHandler)typeof(UiaTargetInspector)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspector)!;

    private static UiaAccess Access(Action? onRead = null)
    {
        var automation = DispatchProxy.Create<IUIAutomation, Proxy>();
        ((Proxy)(object)automation).OnCall = _ => null;
        var access = (UiaAccess)RuntimeHelpers.GetUninitializedObject(typeof(UiaAccess));
        typeof(UiaAccess).GetField("<Automation>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(access, automation);
        Reads = onRead;
        return access;
    }

    private static Action? Reads { get; set; }

    private static IUIAutomationElement Element()
    {
        var element = DispatchProxy.Create<IUIAutomationElement, Proxy>();
        ((Proxy)(object)element).OnCall = _ =>
        {
            Reads?.Invoke();
            return null;
        };
        return element;
    }

    public class Proxy : DispatchProxy
    {
        public Func<string, object?>? OnCall { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => OnCall?.Invoke(targetMethod!.Name);
    }
}
