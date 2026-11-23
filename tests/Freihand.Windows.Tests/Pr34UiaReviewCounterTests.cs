using System.Reflection;
using System.Runtime.CompilerServices;
using Freihand.Windows.Input;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Windows.Targets;
using Windows.Win32.UI.Accessibility;

namespace Freihand.Windows.Tests;

/// <summary>PR #34: UIA-Attrappen und reines Nachrichtenfenster, ohne Desktopfokus oder SendInput (AU-10, SI-04).</summary>
public sealed class Pr34UiaReviewCounterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C07_QueuedFocusEventToAForeignElementAndBackResumesTheSameField(bool returnBeforeDelivery)
    {
        var first = Element(1);
        var second = Element(2);
        IUIAutomationElement current = first;
        var uia = Access(() => current);
        using var inspector = new UiaTargetInspector(uia);
        using var delivered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // AU-10: Eine erlaubte Verzögerung der Weiterleitung darf den fremden Zwischenfokus nicht auslöschen.
        inspector.FocusChanged += (_, _) => { delivered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var adapter = new InspectorAdapter(inspector);
        var binding = new TargetBinding(adapter, new TargetRuleTable([]));
        var target = binding.BindFocused(ProfileStrictness.Standard).Target;
        Assert.NotNull(target);
        inspector.FocusChanged += (_, _) => completed.TrySetResult();
        var handler = (IUIAutomationFocusChangedEventHandler)typeof(UiaTargetInspector)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspector)!;
        try
        {
            current = second;
            handler.HandleFocusChangedEvent(second);
            Assert.True(delivered.Wait(TimeSpan.FromSeconds(10)));
            if (returnBeforeDelivery) current = first;
        }
        finally { resume.Set(); }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Entscheid vom 11.11.2026: Zurück im selben Feld gilt die Bindung wieder; solange der Fokus fremd ist, meldet die
        // Prüfung vor jedem Teil FocusElsewhere, darum gelangt nie ein Teil in das fremde Element
        current = second;
        Assert.Equal(TargetCheck.FocusElsewhere, binding.CheckBound(target.BindingId));
        current = first;
        Assert.Equal(TargetCheck.Focused, binding.CheckBound(target.BindingId));
        binding.Release();
    }

    [Fact]
    public void C08_NoFocusedElementDoesNotProveThatTheFocusIsNotAPasswordField()
    {
        // SI-04: Fehlender Fokus liefert kein lesbares IsPassword=false.
        var uia = Access(() => null!);
        var signals = (WindowsGuardSignals)RuntimeHelpers.GetUninitializedObject(typeof(WindowsGuardSignals));
        typeof(WindowsGuardSignals).GetField("_uia", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(signals, uia);
        Assert.Null(signals.FocusIsPassword());
    }

    [Theory]
    [InlineData(2000u)]
    [InlineData(731u)]
    public void C09_TheDocumentedUiaTimeoutIsActuallyConfigured(uint milliseconds)
    {
        // P7-Umsetzungsbericht: Verbindungs- und Transaktionszeitgrenze 2 s.
        // Nur COM-Objekt anlegen; keine Fokusabfrage, kein Fenster und keine Eingabe.
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) return;
        var uia = new UiaAccess(timeout: TimeSpan.FromMilliseconds(milliseconds));
        // COM-Objekt: die Schnittstelle zeigt nur die Abfrage (QueryInterface), nicht der statische Typ __ComObject
        var timed = uia.Automation as IUIAutomation2;
        Assert.NotNull(timed);
        Assert.Equal(milliseconds, timed.ConnectionTimeout);
        Assert.Equal(milliseconds, timed.TransactionTimeout);
    }

    [Fact]
    public void C10_UnreadableWindowStyleMustNotMeanSingleLine()
    {
        // Kapitel 5.5: Das Attrappenhandle 1 ist kein Testfenster. Ein Lesefehler ist kein sicherer Wert.
        using var inspector = new UiaTargetInspector(Access(() => Element(1)));
        Assert.Equal(0, global::Windows.Win32.PInvoke.GetWindowLong(new global::Windows.Win32.Foundation.HWND(1),
            global::Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_STYLE));
        Assert.NotEqual(0, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        var described = inspector.Describe(Element(1));
        Assert.Null(described.Facts.IsMultiLine);
    }
    [Fact]
    public void C11_FailedSessionNotificationRegistrationMustNotReportSuccessfulStart()
    {
        // SI-04: Kein Fenster und keine Registrierung werden erzeugt. HWND 0 ist absichtlich ungültig.
        // Invoke führt den Aufruf direkt aus, weil sein Besitzer hier der Testthread ist.
        var window = (MessageWindow)RuntimeHelpers.GetUninitializedObject(typeof(MessageWindow));
        typeof(MessageWindow).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, Thread.CurrentThread);
        using var signals = new WindowsGuardSignals(window, Access(() => Element(1)));
        Assert.False((bool)global::Windows.Win32.PInvoke.WTSRegisterSessionNotification(default, 0));
        var error = Record.Exception(signals.Start);
        Assert.NotNull(error);
    }
    [Fact]
    public async Task C13_RealMessageWindowDoesNotDeadlockWithHotkeyRegistration()
    {
        // BF-02: Nur HWND_MESSAGE, keine Hotkey-Anmeldung und keine Eingabe. Echte SendMessage-Weiterleitung.
        using var window = new MessageWindow();
        var api = new MessageApi(window);
        using var service = new HotkeyService(api, new Keys(), new Layout(), new HoldOptions(TimeSpan.FromMilliseconds(15)), TimeProvider.System);
        Assert.Equal(HotkeyConflict.None, service.Register(HotkeyAction.Record, new(HotkeyModifiers.Control, 0x59)));
        api.Dispatch = () => service.OnHotkeyMessage(1, 123);
        var result = service.Register(HotkeyAction.Command, new(HotkeyModifiers.Control, 0x58));
        await Task.WhenAll(api.Pending).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(api.TimedOut, "Der echte Nachrichten-Thread und die Dienstsperre bilden einen Wartezyklus.");
        Assert.Equal(HotkeyConflict.None, result);
    }

    private sealed class Keys : IKeyStateProbe { public bool IsDown(int code) => false; }
    private sealed class Layout : IKeyboardLayoutInfo { public string? AltGrCharacter(int code, bool shift) => null; }
    private sealed class MessageApi(MessageWindow window) : IHotkeyApi
    {
        public Action? Dispatch { get; set; }
        public bool TimedOut { get; private set; }
        public List<Task> Pending { get; } = [];
        public bool Register(int id, uint modifiers, int virtualKey)
        {
            if (Dispatch is null) return window.Invoke(() => true);
            using var entered = new ManualResetEventSlim();
            var callback = Task.Factory.StartNew(() => window.Invoke(() =>
            {
                entered.Set();
                Dispatch();
                return true;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Pending.Add(callback);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var invoke = Task.Factory.StartNew(() => window.Invoke(() => true), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Pending.Add(invoke);
            TimedOut = !invoke.Wait(TimeSpan.FromSeconds(2));
            return !TimedOut;
        }
        public void Unregister(int id) { }
    }
    private static UiaAccess Access(Func<IUIAutomationElement> focused)
    {
        var automation = DispatchProxy.Create<IUIAutomation, UiaProxy>();
        ((UiaProxy)(object)automation).Call = method => method == "GetFocusedElement" ? focused() : null;
        var access = (UiaAccess)RuntimeHelpers.GetUninitializedObject(typeof(UiaAccess));
        typeof(UiaAccess).GetField("<Automation>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(access, automation);
        return access;
    }

    private static IUIAutomationElement Element(int runtimeId)
    {
        var element = DispatchProxy.Create<IUIAutomationElement, UiaProxy>();
        ((UiaProxy)(object)element).Property = property => property switch
        {
            UIA_PROPERTY_ID.UIA_ProcessIdPropertyId => Environment.ProcessId,
            UIA_PROPERTY_ID.UIA_ControlTypePropertyId => 50004,
            UIA_PROPERTY_ID.UIA_ClassNamePropertyId => "ReviewFixture",
            UIA_PROPERTY_ID.UIA_FrameworkIdPropertyId => "ReviewFixture",
            UIA_PROPERTY_ID.UIA_AutomationIdPropertyId => "fixture",
            UIA_PROPERTY_ID.UIA_NativeWindowHandlePropertyId => 1,
            UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId => new[] { 42, runtimeId },
            UIA_PROPERTY_ID.UIA_IsPasswordPropertyId => false,
            UIA_PROPERTY_ID.UIA_ValueIsReadOnlyPropertyId => false,
            UIA_PROPERTY_ID.UIA_IsEnabledPropertyId => true,
            _ => null,
        };
        return element;
    }

    public class UiaProxy : DispatchProxy
    {
        public Func<string, object?>? Call { get; set; }
        internal Func<UIA_PROPERTY_ID, object?>? Property { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "GetCurrentPropertyValueEx"
                ? Property!((UIA_PROPERTY_ID)args![0]!) : Call?.Invoke(targetMethod.Name);
    }

    private sealed class InspectorAdapter(UiaTargetInspector inner) : ITargetInspector
    {
        // Die UIA-Attrappe nutzt die lesbare Identität des Testhosts, stellt aber ein fremdes Feld dar.
        public (ElementIdentity Identity, TargetFacts Facts)? Focused() => inner.Focused() is { } value
            ? (value.Identity, value.Facts with { IsOwnProcess = false }) : null;
        public bool Exists(ElementIdentity identity) => inner.Exists(identity);
        public int CountByAutomationId(long window, string id) => inner.CountByAutomationId(window, id);
        public bool OnSecureDesktop() => false;
        public event EventHandler? FocusChanged { add => inner.FocusChanged += value; remove => inner.FocusChanged -= value; }
    }
}
