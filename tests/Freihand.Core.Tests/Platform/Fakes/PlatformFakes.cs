using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;

namespace Freihand.Core.Tests.Platform.Fakes;

/// <summary>
/// Zeit für P7-Tests, die nur mit <see cref="Advance"/> weiterläuft. Anders als die Testuhr der Wiederherstellung liefert
/// sie auch <see cref="GetTimestamp"/> aus der manuellen Zeit (Ticks zu <see cref="TimeSpan.TicksPerSecond"/>), damit
/// Zeitstempel von Ereignissen prüfbar sind. Fällige Zeitgeber laufen im Aufrufer.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _elapsed;
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero) + Elapsed;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Elapsed.Ticks;

    /// <summary>Zahl der aktiven Zeitgeber, z. B. um zu prüfen, ob eine Abfrage läuft.</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => timer.DueAt is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Rückt die Zeit vor und lässt fällige Zeitgeber der Reihe nach laufen.</summary>
    public void Advance(TimeSpan delta)
    {
        TimeSpan target;
        lock (_gate)
        {
            target = _elapsed + delta;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.DueAt is { } due && due <= target).MinBy(timer => timer.DueAt);
                if (next is null)
                {
                    _elapsed = target;
                    return;
                }

                _elapsed = next.DueAt!.Value;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public TimeSpan? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._elapsed + dueTime;
                _period = period;
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : DueAt + _period;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary><see cref="IHotkeyApi"/> mit Protokoll; <see cref="Accept"/> entscheidet je Anmeldung.</summary>
internal sealed class FakeHotkeyApi : IHotkeyApi
{
    public List<(int Id, uint Modifiers, int VirtualKey)> Registered { get; } = [];

    public List<int> Unregistered { get; } = [];

    /// <summary>Alle Aufrufe in Reihenfolge, z. B. „R 1 0x4003 0x59“ und „U 1“.</summary>
    public List<string> Calls { get; } = [];

    public Func<int, uint, int, bool> Accept { get; set; } = (_, _, _) => true;

    public bool Register(int id, uint modifiers, int virtualKey)
    {
        Calls.Add($"R {id} 0x{modifiers:X} 0x{virtualKey:X}");
        var accepted = Accept(id, modifiers, virtualKey);
        if (accepted)
        {
            Registered.Add((id, modifiers, virtualKey));
        }

        return accepted;
    }

    public void Unregister(int id)
    {
        Calls.Add($"U {id}");
        Unregistered.Add(id);
        Registered.RemoveAll(entry => entry.Id == id);
    }

    /// <summary>Kennung, unter der die Kombination mit dieser Taste zuletzt angemeldet wurde.</summary>
    public int IdOf(int virtualKey) => Registered.Last(entry => entry.VirtualKey == virtualKey).Id;
}

/// <summary><see cref="IKeyStateProbe"/>: gedrückte Tasten setzt der Test; jede Abfrage wird protokolliert.</summary>
internal sealed class FakeKeyStateProbe : IKeyStateProbe
{
    private readonly object _gate = new();
    private readonly HashSet<int> _down = [];
    private readonly List<int> _queried = [];

    public IReadOnlyList<int> Queried
    {
        get
        {
            lock (_gate)
            {
                return [.. _queried];
            }
        }
    }

    public void Press(params int[] keys)
    {
        lock (_gate)
        {
            _down.UnionWith(keys);
        }
    }

    public void Lift(params int[] keys)
    {
        lock (_gate)
        {
            _down.ExceptWith(keys);
        }
    }

    public bool IsDown(int virtualKey)
    {
        lock (_gate)
        {
            _queried.Add(virtualKey);
            return _down.Contains(virtualKey);
        }
    }
}

/// <summary><see cref="IKeyboardLayoutInfo"/> wie de-CH im Ausschnitt der Tests: Strg+Alt+2 → „@“, +E → „€“, +7 → „|“.</summary>
internal sealed class FakeLayout : IKeyboardLayoutInfo
{
    public Dictionary<(int VirtualKey, bool Shift), string> Characters { get; } = new()
    {
        [(0x32, false)] = "@",
        [(0x45, false)] = "€",
        [(0x37, false)] = "|",
    };

    public string? AltGrCharacter(int virtualKey, bool shift) =>
        Characters.TryGetValue((virtualKey, shift), out var character) ? character : null;
}

/// <summary>
/// <see cref="ITargetInspector"/>: Fokus, vorhandene Elemente und Kennungszählung setzt der Test;
/// <see cref="RaiseFocusChanged"/> löst das Fokusereignis aus.
/// </summary>
internal sealed class FakeTargetInspector : ITargetInspector
{
    private EventHandler? _focusChanged;

    public (ElementIdentity Identity, TargetFacts Facts)? Focus { get; set; }

    public HashSet<ElementIdentity> Existing { get; } = [];

    public Dictionary<(long Window, string AutomationId), int> AutomationIdCounts { get; } = [];

    public bool SecureDesktop { get; set; }

    public int FocusSubscribers { get; private set; }

    public event EventHandler? FocusChanged
    {
        add
        {
            _focusChanged += value;
            FocusSubscribers++;
        }

        remove
        {
            _focusChanged -= value;
            FocusSubscribers--;
        }
    }

    /// <summary>Wird beim nächsten Aufruf von <see cref="Focused"/> ausgeführt, z. B. um ein Warten zu erzwingen.</summary>
    public Action? OnFocused { get; set; }

    public (ElementIdentity Identity, TargetFacts Facts)? Focused()
    {
        OnFocused?.Invoke();
        return Focus;
    }

    public bool Exists(ElementIdentity identity) => Existing.Contains(identity);

    public int CountByAutomationId(long topLevelWindow, string automationId) =>
        AutomationIdCounts.TryGetValue((topLevelWindow, automationId), out var count) ? count : 0;

    public bool OnSecureDesktop() => SecureDesktop;

    /// <summary>Setzt den Fokus und meldet das Fokusereignis.</summary>
    public void MoveFocus((ElementIdentity Identity, TargetFacts Facts)? focus)
    {
        Focus = focus;
        RaiseFocusChanged();
    }

    public void RaiseFocusChanged() => _focusChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary><see cref="ISendInputApi"/>: Rückgabewert je Aufruf steuerbar (Standard: alle eingefügt), jeder Aufruf protokolliert.</summary>
internal sealed class FakeSendInputApi : ISendInputApi
{
    public List<IReadOnlyList<InputEvent>> Calls { get; } = [];

    /// <summary>Rückgabewerte der nächsten Aufrufe; leer heisst: alle Ereignisse eingefügt.</summary>
    public Queue<int> Results { get; } = new();

    public int Send(IReadOnlyList<InputEvent> events)
    {
        Calls.Add([.. events]);
        return Results.TryDequeue(out var inserted) ? Math.Min(inserted, events.Count) : events.Count;
    }
}

/// <summary>
/// <see cref="ITextInjector"/>: protokolliert die Teile; <see cref="Respond"/> legt das Ergebnis je Teil fest (Standard:
/// alle Einheiten); <see cref="During"/> läuft innerhalb von <see cref="Inject"/>, z. B. für einen Abbruch.
/// </summary>
internal sealed class FakeTextInjector : ITextInjector
{
    private readonly object _gate = new();
    private readonly List<InjectionPart> _parts = [];

    public IReadOnlyList<InjectionPart> Parts
    {
        get
        {
            lock (_gate)
            {
                return [.. _parts];
            }
        }
    }

    public Func<InjectionPart, InjectionResult>? Respond { get; set; }

    public Action<InjectionPart>? During { get; set; }

    public InjectionResult Inject(InjectionPart part)
    {
        lock (_gate)
        {
            _parts.Add(part);
        }

        During?.Invoke(part);
        return Respond?.Invoke(part) ?? new InjectionResult(part.Text.Length, []);
    }
}

/// <summary>
/// <see cref="ITargetBinding"/> für die Ausgabe: bindet auf Anforderung mit neuer Kennung; das Ergebnis der Prüfung je
/// Aufruf von <see cref="CheckAndSend"/> legt <see cref="NextChecks"/> fest (Standard <see cref="TargetCheck.Focused"/>).
/// Hält die Kennungsregel aus Kapitel 5.4 ein: eine andere Kennung als die aktuelle ergibt <see cref="TargetCheck.NotBound"/>.
/// </summary>
internal sealed class FakeTargetBinding(TargetRule rule) : ITargetBinding
{
    private readonly object _gate = new();

    public TargetInfo? Bound { get; private set; }

    public TargetRule? BoundRule { get; private set; }

    public Queue<TargetCheck> NextChecks { get; } = new();

    public int Checks { get; private set; }

    public BindResult BindFocused(ProfileStrictness strictness)
    {
        lock (_gate)
        {
            Bound = new TargetInfo(rule.ProcessName, rule.FieldKind, rule.Display, IsPassword: false) { BindingId = Guid.NewGuid() };
            BoundRule = rule;
            return new BindResult(Bound, null);
        }
    }

    public TargetCheck CheckBound(Guid bindingId)
    {
        lock (_gate)
        {
            return Check(bindingId);
        }
    }

    public TargetCheck CheckAndSend(Guid bindingId, Action send)
    {
        ArgumentNullException.ThrowIfNull(send);
        lock (_gate)
        {
            var check = Check(bindingId);
            if (check == TargetCheck.Focused)
            {
                send();
            }

            return check;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            Bound = null;
            BoundRule = null;
        }
    }

    private TargetCheck Check(Guid bindingId)
    {
        Checks++;
        if (Bound is null || Bound.BindingId != bindingId)
        {
            return TargetCheck.NotBound;
        }

        return NextChecks.TryDequeue(out var check) ? check : TargetCheck.Focused;
    }
}

/// <summary><see cref="IHotkeyService"/> für die Ausgabe: nur <see cref="OwnKeysReleased"/> ist steuerbar und zählt Aufrufe.</summary>
internal sealed class FakeHotkeyService : IHotkeyService
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

    public Func<bool> KeysReleased { get; set; } = () => true;

    public int ReleaseQueries { get; private set; }

    public HotkeyConflict Register(HotkeyAction action, HotkeyGesture gesture, bool trackRelease = false) => HotkeyConflict.None;

    public void Unregister(HotkeyAction action)
    {
    }

    public bool OwnKeysReleased()
    {
        ReleaseQueries++;
        return KeysReleased();
    }

    public void Dispose()
    {
    }
}

/// <summary><see cref="IGuardSignals"/>: Ereignisse löst der Test aus; Abfragen und Start/Stop werden gezählt.</summary>
internal sealed class FakeGuardSignals : IGuardSignals
{
    public event EventHandler<GuardReason>? SessionEvent;

    public event EventHandler<bool?>? FocusChanged;

    public bool? Password { get; set; } = false;

    public string? Desktop { get; set; } = "Default";

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public int PasswordQueries { get; private set; }

    public int DesktopQueries { get; private set; }

    public bool? FocusIsPassword()
    {
        PasswordQueries++;
        return Password;
    }

    public string? InputDesktopName()
    {
        DesktopQueries++;
        return Desktop;
    }

    public void Start() => Starts++;

    public void Stop() => Stops++;

    public void RaiseSession(GuardReason reason) => SessionEvent?.Invoke(this, reason);

    public void RaiseFocus(bool? isPassword) => FocusChanged?.Invoke(this, isPassword);
}

/// <summary>Testtabelle aus Kapitel 7 (Zeilen N, T, S) und passende Fakten bzw. Kennungen.</summary>
internal static class TargetTable
{
    public static TargetRule N { get; } = new(
        "notepad", "Document", "RichEditD2DPT", "Win32", null, FieldKinds.DocumentBody, "Editor – Dokumenttext",
        LineBreakMode.UnicodeReturn, 200, IdentityMode.RuntimeId);

    public static TargetRule T { get; } = new(
        "ms-teams", "Edit", null, "Chrome", null, FieldKinds.ChatInput, "Teams – Chat-Eingabe",
        LineBreakMode.ShiftEnter, 100, IdentityMode.AutomationId);

    public static TargetRule S { get; } = new(
        "outlook", "Edit", "RichEdit20WPT", "Win32", false, FieldKinds.Subject, "Outlook – Betreff",
        LineBreakMode.Refuse, 200, IdentityMode.RuntimeId);

    public static TargetRuleTable Table() => new([N, T, S]);

    /// <summary>Sichere Fakten, die auf die Zeile passen; Abweichungen per <c>with</c>.</summary>
    public static TargetFacts FactsFor(TargetRule rule) => new(
        rule.ProcessName, rule.ControlType ?? "Edit", rule.ClassName ?? "Chrome_RenderWidgetHostHWND", rule.FrameworkId ?? "Win32",
        IsPassword: false, IsReadOnly: false, IsEnabled: true, IsMultiLine: rule.IsMultiLine ?? true, IsElevated: false,
        IsOwnProcess: false);

    /// <summary>Kennung eines Elements; Abweichungen per <c>with</c>.</summary>
    public static ElementIdentity IdentityFor(TargetRule rule, string runtimeId = "42.1", string automationId = "") => new(
        ProcessId: 4711, ProcessStartTicks: 638_000_000_000_000_000, TopLevelWindow: 0x1234, RuntimeId: runtimeId,
        ControlType: rule.ControlType ?? "Edit", ClassName: rule.ClassName ?? "Chrome_RenderWidgetHostHWND", AutomationId: automationId);

    public static OutputOptions Options(ControlKeySafety safety = ControlKeySafety.NotApproved) => new(
        KeyWaitLimit: TimeSpan.FromSeconds(2), KeyWaitPoll: TimeSpan.FromMilliseconds(15), PartDelay: TimeSpan.FromMilliseconds(10),
        ControlKeys: safety);
}
