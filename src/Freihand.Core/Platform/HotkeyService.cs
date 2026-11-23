namespace Freihand.Core.Platform;

/// <summary>Dienst für eigene Tastenkombinationen samt Halten (P7-Konzept, Kapitel 5.1, 5.2 und 5.7).</summary>
public sealed class HotkeyService : IHotkeyService
{
    private const uint NoRepeat = 0x4000;

    /// <summary>Strg, Alt, Umschalt, Windows links und rechts (Kapitel 5.7, Schritt 7).</summary>
    private static readonly (HotkeyModifiers Modifier, int[] Codes)[] ModifierKeys =
    [
        (HotkeyModifiers.Control, [0x11]),
        (HotkeyModifiers.Alt, [0x12]),
        (HotkeyModifiers.Shift, [0x10]),
        (HotkeyModifiers.Windows, [0x5B, 0x5C]),
    ];

    private readonly object _gate = new();
    private readonly IHotkeyApi _api;
    private readonly IKeyStateProbe _keys;
    private readonly IKeyboardLayoutInfo _layout;
    private readonly HoldOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<HotkeyAction, Registration> _registrations = [];
    private int _nextId;
    private Hold? _hold;
    private HotkeyGesture? _unreleased;
    private bool _disposed;

    public HotkeyService(IHotkeyApi api, IKeyStateProbe keys, IKeyboardLayoutInfo layout, HoldOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _api = api;
        _keys = keys;
        _layout = layout;
        _options = options;
        _time = time;
    }

    public event EventHandler<HotkeyEvent>? Pressed;

    public event EventHandler<HotkeyEvent>? Released;

    /// <summary>Regeln 1–5 ohne Windows, dann Regel 6; eine ersetzte Kombination wird erst nach der neuen abgemeldet.</summary>
    public HotkeyConflict Register(HotkeyAction action, HotkeyGesture gesture, bool trackRelease = false)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var registered = _registrations.ToDictionary(entry => entry.Key, entry => entry.Value.Gesture);
            var conflict = HotkeyRules.Check(action, gesture, registered, _layout);
            if (conflict != HotkeyConflict.None)
            {
                return conflict;
            }

            var id = ++_nextId;
            if (!_api.Register(id, (uint)gesture.Modifiers | NoRepeat, gesture.VirtualKey))
            {
                return HotkeyConflict.TakenBySystemOrOtherApp;
            }

            HotkeyEvent? released = null;
            if (_registrations.Remove(action, out var old))
            {
                released = EndHold(action);
                _api.Unregister(old.Id);
            }

            _registrations[action] = new Registration(id, gesture, trackRelease);
            Raise(Released, released);
            return HotkeyConflict.None;
        }
    }

    /// <summary>Während des Haltens kommt <see cref="Released"/> genau einmal, danach keine Abfrage mehr.</summary>
    public void Unregister(HotkeyAction action)
    {
        HotkeyEvent? released = null;
        lock (_gate)
        {
            if (_registrations.Remove(action, out var registration))
            {
                released = EndHold(action);
                _api.Unregister(registration.Id);
            }
        }

        Raise(Released, released);
    }

    /// <summary>
    /// Fragt nur Haupttaste und Modifikatoren der zuletzt ausgelösten Kombination ab, und nur bis sie einmal alle oben
    /// gemeldet sind; danach und vor jeder Auslösung wahr ohne Abfrage (SI-05).
    /// </summary>
    public bool OwnKeysReleased()
    {
        lock (_gate)
        {
            if (_unreleased is not { } gesture)
            {
                return true;
            }

            if (_keys.IsDown(gesture.VirtualKey))
            {
                return false;
            }

            foreach (var (modifier, codes) in ModifierKeys)
            {
                if (gesture.Modifiers.HasFlag(modifier) && codes.Any(_keys.IsDown))
                {
                    return false;
                }
            }

            _unreleased = null;
            return true;
        }
    }

    /// <summary>Vom Nachrichtenfenster bei <c>WM_HOTKEY</c>; <paramref name="timestamp"/> ist der Zeitstempel beim Empfang.</summary>
    public void OnHotkeyMessage(int id, long timestamp)
    {
        HotkeyEvent pressed;
        lock (_gate)
        {
            if (_disposed || _registrations.FirstOrDefault(entry => entry.Value.Id == id) is not { Value: { } registration } match)
            {
                return;
            }

            pressed = new HotkeyEvent(match.Key, timestamp);
            _unreleased = registration.Gesture;
            if (registration.TrackRelease && _hold?.Action != match.Key)
            {
                _hold?.Timer.Dispose();
                _hold = new Hold(match.Key, registration.Gesture.VirtualKey, _time.CreateTimer(
                    _ => Poll(), null, _options.PollInterval, _options.PollInterval));
            }
        }

        Raise(Pressed, pressed);
    }

    public void Dispose()
    {
        HotkeyEvent? released;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            released = _hold is { } hold ? EndHold(hold.Action) : null;
            foreach (var registration in _registrations.Values)
            {
                _api.Unregister(registration.Id);
            }

            _registrations.Clear();
        }

        Raise(Released, released);
    }

    /// <summary>Nur die Haupttaste; <see cref="Released"/> bei der zweiten Abfrage in Folge, die oben meldet (Kapitel 5.2).</summary>
    private void Poll()
    {
        HotkeyEvent? released = null;
        lock (_gate)
        {
            if (_hold is not { } hold)
            {
                return;
            }

            if (_keys.IsDown(hold.VirtualKey))
            {
                hold.UpPolls = 0;
            }
            else if (++hold.UpPolls >= 2)
            {
                released = EndHold(hold.Action);
            }
        }

        Raise(Released, released);
    }

    private HotkeyEvent? EndHold(HotkeyAction action)
    {
        if (_hold is not { } hold || hold.Action != action)
        {
            return null;
        }

        hold.Timer.Dispose();
        _hold = null;
        return new HotkeyEvent(action, _time.GetTimestamp());
    }

    private void Raise(EventHandler<HotkeyEvent>? handler, HotkeyEvent? hotkeyEvent)
    {
        if (hotkeyEvent is not null)
        {
            handler?.Invoke(this, hotkeyEvent);
        }
    }

    private sealed record Registration(int Id, HotkeyGesture Gesture, bool TrackRelease);

    private sealed class Hold(HotkeyAction action, int virtualKey, ITimer timer)
    {
        public HotkeyAction Action { get; } = action;

        public int VirtualKey { get; } = virtualKey;

        public ITimer Timer { get; } = timer;

        public int UpPolls { get; set; }
    }
}
