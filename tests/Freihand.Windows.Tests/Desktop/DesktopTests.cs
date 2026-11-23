using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Routing;
using Freihand.Core.Segments;
using Freihand.Windows.Input;
using Freihand.Windows.Targets;
using Windows.Win32.UI.Accessibility;

namespace Freihand.Windows.Tests.Desktop;

/// <summary>
/// Stufe C des P7-Konzepts (Kapitel 7.3): automatisch auf einem echten Desktop, nur mit eigenen Testfenstern. Tests, die
/// Tasten einspeisen, prüfen vorher, dass ihr Testfenster im Vordergrund ist, und speisen sonst nichts ein. Die automatische
/// Prüfung schliesst die Kategorie aus. Nur erfundene Testtexte.
/// </summary>
[Trait("Kategorie", "Desktop")]
[Collection("Desktop")]
public sealed class DesktopTests
{
    private const int Y = 0x59;
    private const int Control = 0x11;
    private const int Alt = 0x12;
    private const string Fixture = "Fixtures/au01-testtext.txt";
    private const string FixtureSha256 = "c44cc9232815f5a6ecb5722acb287dc85413c6748ae404a5625a0d1dd22d07e2";

    private static readonly HotkeyGesture CtrlAltY = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Y);

    /// <summary>
    /// Zeile der Zieltabelle für die Testfenster: mehrzeiliges Win32-EDIT, Umbruch als U+000D. Über <c>CUIAutomation8</c>
    /// meldet UIA ein mehrzeiliges EDIT als „Document“ (gemessen am 03.11.2026).
    /// </summary>
    private static readonly TargetRule TestField = new(
        TestTargetProcess.ProcessName, "Document", "Edit", "Win32", null, FieldKinds.MultiLine, "Testfenster – mehrzeilig",
        LineBreakMode.UnicodeReturn, 200, IdentityMode.RuntimeId);

    private static readonly OutputOptions Output = new(
        TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(10), ControlKeySafety.NotApproved);

    [Fact]
    public void W21_ASecondServiceCannotTakeACombinationTheFirstHolds()
    {
        using var firstWindow = new MessageWindow();
        using var secondWindow = new MessageWindow();
        using var first = Service(firstWindow);
        using var second = Service(secondWindow);

        Assert.Equal(HotkeyConflict.None, first.Register(HotkeyAction.Record, CtrlAltY));
        Assert.Equal(HotkeyConflict.TakenBySystemOrOtherApp, second.Register(HotkeyAction.Record, CtrlAltY));

        first.Unregister(HotkeyAction.Record);
        Assert.Equal(HotkeyConflict.None, second.Register(HotkeyAction.Record, CtrlAltY));
    }

    [Fact]
    public void W22_InjectedHoldGivesOnePressAndOneReleaseWithinFiftyMilliseconds()
    {
        using var target = new TestTargetProcess();
        target.Window("a", "multi");
        using var window = new MessageWindow();
        using var service = Service(window);
        var events = new List<(string Kind, long Timestamp)>();
        service.Pressed += (_, e) => Record(events, "ab", e);
        service.Released += (_, e) => Record(events, "auf", e);
        Assert.Equal(HotkeyConflict.None, service.Register(HotkeyAction.Record, CtrlAltY, trackRelease: true));
        RequireForeground(target, "a");

        // Vor jedem Ab prüft der Helfer das eigene Fenster im Vordergrund; bei Verlust Abbruch, eigene Tasten gehen auf
        long lifted;
        using (var keys = GuardedKeys.For(target.Frame("a")))
        {
            keys.Down(Control);
            keys.Down(Alt);
            keys.Down(Y);
            for (var repeat = 0; repeat < 10; repeat++)
            {
                Thread.Sleep(50);
                keys.Down(Y);
            }

            lifted = Stopwatch.GetTimestamp();
            keys.Up(Y);
            keys.Up(Alt);
            keys.Up(Control);
        }

        WaitUntil(() => Count(events, "auf") > 0, TimeSpan.FromSeconds(1));
        Thread.Sleep(200);

        // Genau ein Auslösen und ein Loslassen, schnell erkannt; das Testfeld sieht kein Y (SI-05)
        Assert.Equal(1, Count(events, "ab"));
        Assert.Equal(1, Count(events, "auf"));
        var released = events.Single(e => e.Kind == "auf").Timestamp;
        Assert.True(Stopwatch.GetElapsedTime(lifted, released) <= TimeSpan.FromMilliseconds(50),
            $"Released {Stopwatch.GetElapsedTime(lifted, released).TotalMilliseconds:F0} ms nach dem Loslassen.");
        Assert.Equal(0, target.YDowns("a"));
    }

    [Theory]
    [InlineData("00000807")]
    [InlineData("00000409")]
    public async Task W23_TestTextArrivesExactlyInAWin32Edit(string layout)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, Fixture));
        Assert.Equal(2000, text.Length);
        Assert.Equal(FixtureSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, Fixture)))));
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\t', text);

        using var target = new TestTargetProcess();
        target.Layout(layout);
        target.Window("a", "multi");
        using var windows = new WindowsTargets(new TargetRuleTable([TestField]), Output);
        RequireForeground(target, "a");
        var bound = windows.Binding.BindFocused(ProfileStrictness.Standard);
        Assert.NotNull(bound.Target);

        var report = await windows.Output.WriteAsync(Fixed(text), bound.Target!);

        Assert.Equal((OutputResult.Written, OutputReason.None), (report.Result, report.Reason));
        target.WaitForLength("a", text.Length);
        Assert.Equal(text, target.Text("a"));
    }

    [Fact]
    public async Task W24_FocusChangeAfterTheFirstPartSendsNothingToTheSecondWindow()
    {
        using var target = new TestTargetProcess();
        target.Window("a", "multi");
        target.Window("b", "multi");
        var text = NineSentences();
        var uia = new UiaAccess();
        using var inspector = new UiaTargetInspector(uia);
        var binding = new TargetBinding(inspector, new TargetRuleTable([TestField]));
        var injector = new SwitchAfterFirstPart(new SendInputInjector(new SendInputApi()), () =>
        {
            target.WaitForLength("a", 200);
            RequireForeground(target, "b");
        });
        var output = new BoundTargetOutput(binding, injector, new NoKeysHeld(), Output, TimeProvider.System);
        RequireForeground(target, "a");
        var bound = binding.BindFocused(ProfileStrictness.Standard).Target!;

        var report = await output.WriteAsync(Fixed(text), bound);

        Assert.Equal((OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere), (report.Result, report.Reason));
        Assert.Equal(text[..200], report.Written);
        Assert.Equal(text[200..], report.Held);
        Thread.Sleep(200);
        Assert.Equal(text[..200], target.Text("a"));
        Assert.Equal(string.Empty, target.Text("b"));
    }

    [Fact]
    public void W25_PasswordFieldIsRecognisedRefusedAndStopsTheRecording()
    {
        using var target = new TestTargetProcess();
        target.Window("text", "multi");
        target.Window("password", "password");
        using var windows = new WindowsTargets(new TargetRuleTable([TestField]), Output);
        var stops = new List<GuardReason>();
        windows.Guard.StopRequired += (_, reason) =>
        {
            lock (stops)
            {
                stops.Add(reason);
            }
        };

        RequireForeground(target, "password");
        WaitUntil(() => windows.Inspector.Focused()?.Facts.IsPassword == true, TimeSpan.FromSeconds(2));
        Assert.True(windows.Inspector.Focused()?.Facts.IsPassword);
        Assert.Equal(BindRefusal.PasswordField, windows.Binding.BindFocused(ProfileStrictness.Standard).Refusal);

        RequireForeground(target, "text");
        Thread.Sleep(200);
        windows.Guard.Arm();
        RequireForeground(target, "password");
        WaitUntil(() => Locked(stops, list => list.Contains(GuardReason.PasswordField)), TimeSpan.FromSeconds(2));
        windows.Guard.Disarm();
        Assert.Contains(GuardReason.PasswordField, Locked(stops, list => list.ToList()));
    }

    [Fact]
    public void W26_BindingCheckingAndGuardReadNoForeignContent()
    {
        using var target = new TestTargetProcess();
        target.Window("a", "multi");
        target.Title("a", "GEHEIM-4712");
        target.SetText("a", "GEHEIM-4711");
        var log = new AccessLog();
        using var windows = new WindowsTargets(new TargetRuleTable([TestField]), Output, null, null, log);
        RequireForeground(target, "a");

        var focused = windows.Inspector.Focused()!.Value;
        var bound = windows.Binding.BindFocused(ProfileStrictness.Standard);
        Assert.NotNull(bound.Target);
        Assert.Equal(TargetCheck.Focused, windows.Binding.CheckBound(bound.Target!.BindingId));
        Assert.Equal(TargetCheck.Focused, windows.Binding.CheckAndSend(bound.Target.BindingId, () => { }));
        windows.Guard.Arm();
        target.Window("b", "multi");
        RequireForeground(target, "b");
        Thread.Sleep(300);
        windows.Guard.Disarm();

        // Ergebnisse enthalten keinen der Marker
        foreach (var json in new[]
                 {
                     JsonSerializer.Serialize(focused.Facts), JsonSerializer.Serialize(focused.Identity),
                     JsonSerializer.Serialize(bound), JsonSerializer.Serialize(bound.Target),
                 })
        {
            Assert.DoesNotContain("GEHEIM-4711", json, StringComparison.Ordinal);
            Assert.DoesNotContain("GEHEIM-4712", json, StringComparison.Ordinal);
        }

        // Abgefragt wurden nur erlaubte Kennungen, nie Name, Wert, Hilfetext oder LegacyIAccessible (Kapitel 5.9)
        Assert.NotEmpty(log.Properties);
        Assert.All(log.Properties, property => Assert.Contains(property, AllowedProperties));
        Assert.All(log.Conditions, property => Assert.Contains(property, AllowedConditions));
        Assert.DoesNotContain(typeof(WindowsTargets).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(method => method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(method => method.GetCustomAttribute<DllImportAttribute>()?.EntryPoint ?? method.Name),
            entry => entry.StartsWith("GetWindowText", StringComparison.Ordinal));

        // Zweckgebundenes Lesen nur über ForeignText (SI-12)
        RequireForeground(target, "a");
        var foreign = windows.ForeignContent.ReadFocused(ReadPurpose.TargetCheck);
        Assert.NotNull(foreign);
        Assert.Equal("GEHEIM-4711", foreign.Reveal());
        Assert.Equal(DataClass.Confidential, foreign.DataClass);
    }

    [Fact]
    public void W27_LayoutCheckFindsAltGrCharactersAndKeepsADeadKey()
    {
        using var target = new TestTargetProcess();
        var swiss = target.Layout("00000807");
        target.Window("a", "multi");
        RequireForeground(target, "a");

        var layout = new KeyboardLayoutInfo();
        Assert.Equal("@", layout.AltGrCharacter(0x32, shift: false));
        Assert.Equal("€", layout.AltGrCharacter(0x45, shift: false));

        // Tottaste „^“, dann die Layoutprüfung, dann „e“: im Feld steht „ê“
        var caret = VkKeyScanEx('^', swiss);
        Assert.NotEqual(-1, caret);
        Assert.Equal(0, caret >> 8);
        RequireForeground(target, "a");
        using var keys = GuardedKeys.For(target.Frame("a"));
        keys.Tap(caret & 0xFF);
        Thread.Sleep(50);

        // Bei offener Tottaste liefert ToUnicodeEx das Tottastenzeichen mit; für die Regel zählt nur, dass ein Zeichen entsteht
        Assert.EndsWith("@", layout.AltGrCharacter(0x32, shift: false), StringComparison.Ordinal);
        keys.Tap(0x45);
        target.WaitForLength("a", 1);
        Assert.Equal("ê", target.Text("a"));
    }

    private static readonly UIA_PROPERTY_ID[] AllowedProperties =
    [
        UIA_PROPERTY_ID.UIA_ProcessIdPropertyId, UIA_PROPERTY_ID.UIA_ControlTypePropertyId, UIA_PROPERTY_ID.UIA_ClassNamePropertyId,
        UIA_PROPERTY_ID.UIA_FrameworkIdPropertyId, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, UIA_PROPERTY_ID.UIA_NativeWindowHandlePropertyId,
        UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId, UIA_PROPERTY_ID.UIA_ValueIsReadOnlyPropertyId,
        UIA_PROPERTY_ID.UIA_IsEnabledPropertyId,
    ];

    private static readonly UIA_PROPERTY_ID[] AllowedConditions =
        [UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId];

    private static HotkeyService Service(MessageWindow window)
    {
        var service = new HotkeyService(
            new HotkeyApi(window), new AsyncKeyStateProbe(), new KeyboardLayoutInfo(), new HoldOptions(TimeSpan.FromMilliseconds(15)),
            TimeProvider.System);
        window.Hotkey += service.OnHotkeyMessage;
        return service;
    }

    /// <summary>
    /// Holt das Testfenster zu Beginn nach vorn; ohne Erfolg bricht der Test ab, bevor etwas eingespeist wird. Danach prüft
    /// <see cref="GuardedKeys"/> vor jeder Ab-Eingabe, ohne den Vordergrund zurückzuholen.
    /// </summary>
    private static void RequireForeground(TestTargetProcess target, string id)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (target.Focus(id))
            {
                Thread.Sleep(100);
                return;
            }

            Thread.Sleep(100);
        }

        Assert.Fail($"Testfenster {id} nicht im Vordergrund; nichts eingespeist.");
    }

    private static TextSegment Fixed(string text) => new(Guid.NewGuid(), text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    /// <summary>Text aus W09 (b): neun Sätze zu je 48 Zeichen plus „. “, ohne das letzte Leerzeichen (449 Zeichen).</summary>
    private static string NineSentences()
    {
        var sentence = "Das ist ein erfundener Satz für den Fokustest x";
        var text = string.Concat(Enumerable.Range(0, 9).Select(_ => sentence.PadRight(48, 'x')[..48] + ". "))[..^1];
        Assert.Equal(449, text.Length);
        return text;
    }

    private static void Record(List<(string Kind, long Timestamp)> events, string kind, HotkeyEvent e)
    {
        lock (events)
        {
            events.Add((kind, e.Timestamp));
        }
    }

    private static int Count(List<(string Kind, long Timestamp)> events, string kind)
    {
        lock (events)
        {
            return events.Count(e => e.Kind == kind);
        }
    }

    private static T Locked<T>(List<GuardReason> list, Func<List<GuardReason>, T> read)
    {
        lock (list)
        {
            return read(list);
        }
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < limit)
        {
            Thread.Sleep(20);
        }
    }

    [DllImport("user32.dll", EntryPoint = "VkKeyScanExW", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanEx(char character, nint layout);

    /// <summary>Wechselt nach dem ersten eingefügten Teil den Vordergrund (W24).</summary>
    private sealed class SwitchAfterFirstPart(ITextInjector inner, Action afterFirst) : ITextInjector
    {
        private int _parts;

        public InjectionResult Inject(InjectionPart part)
        {
            var result = inner.Inject(part);
            if (++_parts == 1)
            {
                afterFirst();
            }

            return result;
        }
    }

    private sealed class NoKeysHeld : IHotkeyService
    {
        public event EventHandler<HotkeyEvent>? Pressed
        {
            add { }
            remove { }
        }

        public event EventHandler<HotkeyEvent>? Released
        {
            add { }
            remove { }
        }

        public HotkeyConflict Register(HotkeyAction action, HotkeyGesture gesture, bool trackRelease = false) => HotkeyConflict.None;

        public void Unregister(HotkeyAction action)
        {
        }

        public bool OwnKeysReleased() => true;

        public void Dispose()
        {
        }
    }

    private sealed class AccessLog : IUiaAccessLog
    {
        private readonly List<UIA_PROPERTY_ID> _properties = [];
        private readonly List<UIA_PROPERTY_ID> _conditions = [];

        public IReadOnlyList<UIA_PROPERTY_ID> Properties
        {
            get
            {
                lock (_properties)
                {
                    return [.. _properties];
                }
            }
        }

        public IReadOnlyList<UIA_PROPERTY_ID> Conditions
        {
            get
            {
                lock (_conditions)
                {
                    return [.. _conditions];
                }
            }
        }

        public void Property(UIA_PROPERTY_ID property)
        {
            lock (_properties)
            {
                _properties.Add(property);
            }
        }

        public void Condition(UIA_PROPERTY_ID property)
        {
            lock (_conditions)
            {
                _conditions.Add(property);
            }
        }
    }
}
