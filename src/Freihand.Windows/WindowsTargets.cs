using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Windows.Input;
using Freihand.Windows.Targets;

namespace Freihand.Windows;

/// <summary>
/// Die Windows-Teile von P7 zusammengesetzt (P7-Konzept, Kapitel 3.1): eigene Tastenkombinationen, Zielbindung, Ausgabe
/// ins gebundene Ziel und Wächter. P9 verdrahtet die Abläufe; diese Klasse besitzt Nachrichtenfenster und UIA-Verbindung.
/// </summary>
public sealed class WindowsTargets : IDisposable
{
    private readonly MessageWindow _hotkeyWindow;
    private readonly MessageWindow _guardWindow;
    private readonly UiaTargetInspector _inspector;
    private readonly WindowsGuardSignals _signals;
    private readonly HotkeyService _hotkeys;

    public WindowsTargets(TargetRuleTable table, OutputOptions output, HoldOptions? hold = null, GuardOptions? guard = null)
        : this(table, output, hold, guard, log: null)
    {
    }

    /// <summary>So lange wartet der Start auf die Bestätigung der UIA-Anmeldung (Kapitel 10.3).</summary>
    private static readonly TimeSpan RegistrationLimit = TimeSpan.FromMilliseconds(500);

    internal WindowsTargets(TargetRuleTable table, OutputOptions output, HoldOptions? hold, GuardOptions? guard, IUiaAccessLog? log)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(output);

        // Ein gescheiterter Start hinterlässt nichts: Alles ab dem ersten Fenster liegt in einem Fehlerpfad, der die schon
        // erzeugten Ressourcen in umgekehrter Reihenfolge freigibt (U34-12)
        var created = new List<IDisposable>();
        try
        {
            _hotkeyWindow = new MessageWindow();
            created.Add(_hotkeyWindow);
            _guardWindow = new MessageWindow();
            created.Add(_guardWindow);
            var uia = new UiaAccess(log);
            _inspector = new UiaTargetInspector(uia);
            created.Add(_inspector);

            // Ohne bestätigte Anmeldung könnte ihr nachgeliefertes Ereignis die erste Bindung als verlassen markieren
            if (!_inspector.WaitForRegistration(RegistrationLimit))
            {
                throw new InvalidOperationException("UI Automation hat die Anmeldung des Fokusereignisses nicht bestätigt.");
            }

            _signals = new WindowsGuardSignals(_guardWindow, uia);
            created.Add(_signals);

            // Ebenso beim Wächter: seine Anmeldung liefert allen Fokusempfängern ein veraltetes Ereignis nach
            if (!_signals.WaitForRegistration(RegistrationLimit))
            {
                throw new InvalidOperationException("UI Automation hat die Anmeldung des Wächters nicht bestätigt.");
            }
            _hotkeys = new HotkeyService(
                new HotkeyApi(_hotkeyWindow), new AsyncKeyStateProbe(), new KeyboardLayoutInfo(),
                hold ?? new HoldOptions(TimeSpan.FromMilliseconds(15)), TimeProvider.System);
            created.Add(_hotkeys);
            _hotkeyWindow.Hotkey += _hotkeys.OnHotkeyMessage;
            Binding = new TargetBinding(_inspector, table);
            Output = new BoundTargetOutput(Binding, new SendInputInjector(new SendInputApi()), _hotkeys, output, TimeProvider.System);
            Guard = new RecordingGuard(_signals, guard ?? new GuardOptions(TimeSpan.FromMilliseconds(250)), TimeProvider.System);
            created.Add(Guard);
            ForeignContent = new UiaForeignContentReader(uia);
        }
        catch
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try
                {
                    created[index].Dispose();
                }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                {
                    // Weiter freigeben; gemeldet wird der ursprüngliche Fehler des Starts
                }
            }

            throw;
        }
    }

    public IHotkeyService Hotkeys => _hotkeys;

    public ITargetBinding Binding { get; }

    /// <summary>Feld merken und später genau dieses binden, für „Übernehmen“ (P9 Diktatfenster, Kapitel 3.4).</summary>
    public ITargetMarker Marker => (TargetBinding)Binding;

    public ITextOutput Output { get; }

    public IRecordingGuard Guard { get; }

    /// <summary>Für die Kern-Funktionen; im Pilot nicht aufgerufen (Kapitel 5.9).</summary>
    public IForeignContentReader ForeignContent { get; }

    internal ITargetInspector Inspector => _inspector;

    /// <summary>Gibt jeden Teil frei, auch wenn ein anderer scheitert (wie U35-07 beim App-Start).</summary>
    public void Dispose()
    {
        Release(Guard.Dispose);
        Release(Binding.Release);
        Release(() => _hotkeyWindow.Hotkey -= _hotkeys.OnHotkeyMessage);
        Release(_hotkeys.Dispose);
        Release(_inspector.Dispose);
        Release(_signals.Dispose);
        Release(_hotkeyWindow.Dispose);
        Release(_guardWindow.Dispose);
    }

    private static void Release(Action release)
    {
        try
        {
            release();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Weiter freigeben
        }
    }
}
