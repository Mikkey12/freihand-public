using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Output;

namespace Freihand.App;

/// <summary>
/// Statusfenster des Durchstichs: Erkenner, Zustand, Ziel, Modus, Meldung, Vorschau und feste Abschnitte. Es nimmt nie
/// den Fokus, sonst verliesse es selbst die Bindung. Es zeigt nur, was die
/// Steuerung meldet, und ruft keine Windows-Teile auf.
/// </summary>
public partial class MainWindow : Window
{
    private const int ExtendedStyle = -20;
    private const int NoActivate = 0x08000000;

    public MainWindow()
    {
        InitializeComponent();
        Left = SystemParameters.WorkArea.Right - Width - 16;
        Top = SystemParameters.WorkArea.Bottom - Height - 16;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, ExtendedStyle, GetWindowLongPtr(handle, ExtendedStyle) | NoActivate);
        };
        Phase.Text = "Freihand startet …";
    }

    /// <summary>Gehaltenen Text kopieren (D4 b); die Steuerung entscheidet, ob das erlaubt ist.</summary>
    public Func<Guid, bool>? CopyHeld { get; set; }

    /// <summary>Gehaltenen Text ins Diktatfenster holen (P9 Diktatfenster, Kapitel 3.4).</summary>
    public Action<Guid>? TakeToWindow { get; set; }

    public void ShowKeys(string text) => Keys.Text = text;

    public void ShowStartup(string text) => Message.Text = text;

    /// <summary>Zeigt einen Stand der Steuerung; auf dem UI-Thread aufzurufen.</summary>
    public void Show(ControllerUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Recognizer.Text = "Erkenner: " + update.Recognizer switch
        {
            RecognizerAvailability.Starting => "lädt",
            RecognizerAvailability.Ready => "bereit",
            RecognizerAvailability.Restarting => "startet neu",
            _ => "nicht verfügbar",
        }
            // Was geladen ist und ob die Laufzeit zurückgefallen ist; bei Ausfall der bleibende Fehler (U35-03)
            + (update.RecognizerInfo is { } info
                ? $" · {System.IO.Path.GetFileName(info.ModelFile)} · {info.Runtime}{(info.RuntimeFallback ? " (Rückfall)" : string.Empty)}"
                : string.Empty)
            + (update.RecognizerError is { } error ? $" · Fehler: {error}" : string.Empty);
        Phase.Text = update.Phase switch
        {
            ControllerPhase.Idle => "Bereit – Aufnahmetaste drücken",
            ControllerPhase.Starting => "Startet …",
            ControllerPhase.Recording => "● Aufnahme läuft",
            _ => "Beendet …",
        };
        Target.Text = update.Target is null ? "Ziel: –" : "Ziel: " + update.Target;
        Mode.Text = update.Mode is null ? string.Empty
            : $"Modus: {(update.Mode.Mode == LiveMode.Live ? "live" : "satzweise")}"
              + (update.Mode.OverridesProfile ? " (live auf diesem Gerät nicht freigegeben)" : string.Empty);
        // Ein Hinweis steht schon mit Klartext und Spanne in der eigenen Zeile
        Message.Text = StatusText.Message(update.Message, update.Reason);
        Notice.Text = update.Notice is { } notice
            ? $"Hinweis: {Describe(notice.Kind)} ({Seconds(notice.Start)}–{Seconds(notice.End)} s)"
            : string.Empty;
        Provisional.Text = update.Provisional;
        Entries.ItemsSource = update.Entries.Reverse().Select(entry => new EntryView(entry)).ToList();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            CopyHeld?.Invoke(id);
        }
    }

    private void OnToWindow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            TakeToWindow?.Invoke(id);
        }
    }

    /// <summary>Hinweise von P3 mit ihrer Spanne im Diktat (U35-02).</summary>
    private static string Describe(LiveNoticeKind kind) => kind switch
    {
        LiveNoticeKind.BacklogWarning => "Erkennung im Rückstand",
        LiveNoticeKind.BacklogCleared => "Rückstand aufgeholt",
        LiveNoticeKind.BufferFull => "Puffer voll, Audio verloren",
        LiveNoticeKind.AudioGap => "Lücke im Audio",
        LiveNoticeKind.SpeechWithoutText => "Sprache ohne Text",
        LiveNoticeKind.LengthCutUncertain => "Längenschnitt, bitte prüfen",
        LiveNoticeKind.DecoderFailed => "Erkennung gescheitert",
        LiveNoticeKind.DetectorFailed => "Spracherkennung gescheitert",
        _ => kind.ToString(),
    };

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    /// <summary>Zeile der Liste der festen Abschnitte.</summary>
    private sealed class EntryView(OutputEntry entry)
    {
        public Guid SegmentId { get; } = entry.SegmentId;

        public string Status { get; } = entry.Status switch
        {
            EntryStatus.Written => "geschrieben",
            EntryStatus.Refused => $"verweigert: {Describe(entry.Reason)}",
            _ => $"gehalten: {Describe(entry.Reason)}",
        }
            + (entry.CheckEnd ? " · Ende prüfen: Längenschnitt" : string.Empty)
            + (entry.Taken ? " · erledigt" : string.Empty);

        public string Text { get; } = entry.Status == EntryStatus.Written ? entry.Written : entry.Held;

        public Visibility CopyVisibility { get; } =
            entry.Status != EntryStatus.Written && entry.Held.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        private static string Describe(OutputReason reason) => reason switch
        {
            OutputReason.FocusElsewhere => "Fokus war nicht im Zielfeld",
            OutputReason.PreviousHeld => "früherer Abschnitt wurde gehalten",
            OutputReason.TargetGone => "Zielfeld nicht mehr da",
            OutputReason.PasswordField => "Passwortfeld",
            OutputReason.ReadOnly => "Zielfeld schreibgeschützt",
            OutputReason.Elevated => "Zielprogramm läuft mit erhöhten Rechten",
            OutputReason.SecureDesktop => "gesicherter Desktop",
            OutputReason.KeysHeld => "Tasten waren gedrückt",
            OutputReason.Cancelled => "abgebrochen",
            _ => reason.ToString(),
        };
    }
}
