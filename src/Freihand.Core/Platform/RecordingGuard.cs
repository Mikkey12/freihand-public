namespace Freihand.Core.Platform;

/// <summary>Wächter während der Aufnahme (P7-Konzept, Kapitel 5.5).</summary>
public sealed class RecordingGuard : IRecordingGuard
{
    private const string DefaultDesktop = "Default";

    private readonly object _gate = new();
    private readonly IGuardSignals _signals;
    private readonly GuardOptions _options;
    private readonly TimeProvider _time;
    private ITimer? _poll;
    private bool _armed;
    private bool _desktopReported;

    public RecordingGuard(IGuardSignals signals, GuardOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _signals = signals;
        _options = options;
        _time = time;
    }

    public event EventHandler<GuardReason>? StopRequired;

    /// <summary>Abonniert die Signale; ein schon fokussiertes oder nicht lesbares Passwortfeld meldet sofort.</summary>
    public void Arm()
    {
        bool password;
        lock (_gate)
        {
            if (_armed)
            {
                return;
            }

            _armed = true;
            _desktopReported = false;
            _signals.SessionEvent += OnSession;
            _signals.FocusChanged += OnFocus;
            var started = false;
            try
            {
                _signals.Start();
                started = true;
                _poll = _time.CreateTimer(_ => PollDesktop(), null, _options.DesktopPoll, _options.DesktopPoll);
                password = _signals.FocusIsPassword() == true;
            }
            catch
            {
                // Nur ein vollständig gelungener Start lässt den Wächter scharf; ein neuer Versuch startet die Signale erneut
                _armed = false;
                _poll?.Dispose();
                _poll = null;
                _signals.SessionEvent -= OnSession;
                _signals.FocusChanged -= OnFocus;
                if (started)
                {
                    _signals.Stop();
                }

                throw;
            }
        }

        if (password)
        {
            Raise(GuardReason.PasswordField);
        }
    }

    /// <summary>Danach weder Ereignisse noch Abfragen.</summary>
    public void Disarm()
    {
        lock (_gate)
        {
            if (!_armed)
            {
                return;
            }

            _armed = false;
            _poll?.Dispose();
            _poll = null;
            _signals.SessionEvent -= OnSession;
            _signals.FocusChanged -= OnFocus;
            _signals.Stop();
        }
    }

    public void Dispose() => Disarm();

    private void OnSession(object? sender, GuardReason reason) => Raise(reason);

    /// <summary>Nur ein Feld, das sich als Passwortfeld meldet, beendet die Aufnahme (Entscheid vom 11.11.2026, SI-04).</summary>
    private void OnFocus(object? sender, bool? isPassword)
    {
        if (isPassword == true)
        {
            Raise(GuardReason.PasswordField);
        }
    }

    /// <summary>Ein anderer oder nicht lesbarer Eingabedesktop meldet einmal, bis wieder „Default“ gilt.</summary>
    private void PollDesktop()
    {
        bool report;
        lock (_gate)
        {
            if (!_armed)
            {
                return;
            }

            var secure = _signals.InputDesktopName() != DefaultDesktop;
            report = secure && !_desktopReported;
            _desktopReported = secure;
        }

        if (report)
        {
            Raise(GuardReason.SecureDesktop);
        }
    }

    private void Raise(GuardReason reason)
    {
        lock (_gate)
        {
            if (!_armed)
            {
                return;
            }
        }

        StopRequired?.Invoke(this, reason);
    }
}
