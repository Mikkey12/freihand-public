using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Transform;
using Freihand.Core.Platform;
using Freihand.Core.Recovery;

namespace Freihand.App;

/// <summary>
/// Diktatfenster: Schreibfläche mit Rückgängig, Kopieren,
/// Übernehmen ins gemerkte Feld und Leeren, dazu die Liste „Wiederherstellbar“. Als Ziel (E2) schreibt die Steuerung über
/// <see cref="IDictationPad"/> an die Schreibmarke. Der Text der Schreibfläche wird über <see cref="PadDraft"/> gesichert.
/// </summary>
public partial class DictationWindow : Window, IDictationPad, IDictationPadText
{
    private const int FirstWords = 6;

    private PadDraft _draft = new(null);
    private RecoveryStore? _store;
    private bool _copyAllowed = true;
    private bool _closing;

    public DictationWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Hide();
            }
        };
        ShowTarget();
    }

    /// <summary>Das Feld, das beim Öffnen per Taste den Fokus hatte; Ziel von „Übernehmen“ (AU-10).</summary>
    public TargetMark? Mark { get; private set; }

    /// <summary>Übernehmen; die Anzeige ruft die Steuerung und meldet das Ergebnis zurück.</summary>
    public Func<string, TargetMark, Task<HandOverReport>>? HandOver { get; set; }

    /// <summary>Zahl der angebotenen Entwürfe nach der letzten Durchsicht (Symbol im Infobereich gelb).</summary>
    public int Offers { get; private set; }

    public event EventHandler? OffersChanged;

    // ---- Korrigieren und Glätten (Beta B8) ---------------------------------------------------------------------------

    private CorrectionService? _corrector;
    private PendingSuggestion? _pending;
    private bool _correcting;

    /// <summary>Schaltet die Werkzeugleiste ein; <paramref name="route"/> zeigt vor dem Senden, wohin der Text geht.</summary>
    public void AttachCorrector(CorrectionService corrector, string route)
    {
        _corrector = corrector;
        RouteLine.Text = route;
        ToolbarHost.Visibility = Visibility.Visible;
    }

    private void OnCorrect(object sender, RoutedEventArgs e) => _ = SuggestAsync(CorrectionKind.Correct);

    private void OnSmooth(object sender, RoutedEventArgs e) => _ = SuggestAsync(CorrectionKind.Smooth);

    /// <summary>
    /// Holt einen Vorschlag für die Auswahl, sonst den ganzen Text. Nichts wird ersetzt, bis „Ersetzen“ gedrückt ist; ein
    /// Fehler lässt den Text, wie er war, mit Hinweis.
    /// </summary>
    internal async Task SuggestAsync(CorrectionKind kind)
    {
        if (_corrector is not { } corrector || _correcting)
        {
            return;
        }

        var whole = Pad.SelectionLength == 0;
        var start = whole ? 0 : Pad.SelectionStart;
        var length = whole ? Pad.Text.Length : Pad.SelectionLength;
        var source = Pad.Text;
        if (source.Substring(start, length).Trim().Length == 0)
        {
            ShowHint("Kein Text zum Überarbeiten.");
            return;
        }

        _correcting = true;
        CorrectButton.IsEnabled = SmoothButton.IsEnabled = false;
        ShowHint(kind == CorrectionKind.Correct ? "Korrigiere …" : "Glätte …");
        try
        {
            var suggestion = await corrector.SuggestAsync(source.Substring(start, length), kind);
            if (suggestion.Unchanged)
            {
                ShowHint("Keine Änderung vorgeschlagen.");
                return;
            }

            ShowSuggestion(new PendingSuggestion(source, start, length, suggestion));
            ShowHint(string.Empty);
        }
        catch (TransformFailedException exception)
        {
            ShowHint(Describe(exception.Failure));
        }
        finally
        {
            _correcting = false;
            CorrectButton.IsEnabled = SmoothButton.IsEnabled = true;
        }
    }

    private void ShowSuggestion(PendingSuggestion pending)
    {
        _pending = pending;
        CorrectionTexts.Render(PreviewText, pending.Suggestion);

        PreviewWarnings.Text = string.Join(" ", pending.Suggestion.Warnings);
        PreviewWarnings.Visibility = pending.Suggestion.Warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Controls.Grid.SetColumnSpan(Pad, 1);
        PreviewHost.Visibility = Visibility.Visible;
    }

    private void HideSuggestion()
    {
        _pending = null;
        PreviewHost.Visibility = Visibility.Collapsed;
        System.Windows.Controls.Grid.SetColumnSpan(Pad, 2);
    }

    /// <summary>
    /// Ersetzt als ein Schritt, den Strg+Z zurücknimmt. Nur wenn der Text seit der Anfrage unverändert ist (Regel aus PR #52):
    /// sonst gingen inzwischen gemachte Eingaben verloren.
    /// </summary>
    internal void OnReplace(object sender, RoutedEventArgs e)
    {
        if (_pending is not { } pending)
        {
            return;
        }

        if (Pad.Text != pending.Source)
        {
            HideSuggestion();
            ShowHint("Der Text hat sich inzwischen geändert; der Vorschlag ist verworfen. Bitte neu anfordern.");
            return;
        }

        Pad.Select(pending.Start, pending.Length);
        Pad.SelectedText = pending.Suggestion.Text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        Pad.Select(pending.Start + Pad.SelectedText.Length, 0);
        HideSuggestion();
        Pad.Focus();
    }

    internal void OnDiscard(object sender, RoutedEventArgs e)
    {
        HideSuggestion();
        Pad.Focus();
    }

    private static string Describe(TransformFailure failure) => CorrectionTexts.Describe(failure);

    /// <summary>Ein Vorschlag mit dem Text und der Stelle, für die er gilt.</summary>
    private sealed record PendingSuggestion(string Source, int Start, int Length, Suggestion Suggestion);

    // ---- IDictationPad: aus jedem Thread ----------------------------------------------------------------------------

    public bool HasFocus => Dispatcher.Invoke(() => TakesDictation(
        IsVisible, IsActive, Pad.IsKeyboardFocusWithin, Windows.Input.ForegroundWindow.Is(new WindowInteropHelper(this).Handle)));

    /// <summary>
    /// Regel (Handtest P11 vom 17.11.2026): Diktat geht nur dann ins Diktatfenster, wenn es sichtbar und aktiv ist, die
    /// Schreibfläche den Tastaturfokus hat und Windows es selbst als Vordergrundfenster meldet. So schreibt Freihand nie in
    /// sein eigenes Fenster, während das Feld eines anderen Programms den Fokus hat.
    /// </summary>
    internal static bool TakesDictation(bool visible, bool active, bool keyboardFocus, bool foreground) =>
        visible && active && keyboardFocus && foreground;

    public string Insert(string text) => Dispatcher.Invoke(() =>
    {
        var insertion = PadText.Insertion(Pad.Text, Pad.SelectionStart, text);
        if (insertion.Length > 0)
        {
            // An der Schreibmarke; eine Auswahl bleibt stehen, eingefügt wird an ihrem Anfang
            var at = Pad.SelectionStart;
            Pad.Select(at, 0);
            Pad.SelectedText = insertion.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
            Pad.Select(at + Pad.SelectedText.Length, 0);
        }

        return insertion;
    });

    // ---- IDictationPadText: Platz für „Umformen“ (Kapitel 7) --------------------------------------------------------

    public string Text => Pad.Text;

    /// <summary>Ersetzt den ganzen Text als ein Schritt, den Strg+Z zurücknimmt.</summary>
    public void ReplaceAll(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Pad.SelectAll();
        Pad.SelectedText = text;
        Pad.Select(Pad.Text.Length, 0);
    }

    // ---- Anzeige -----------------------------------------------------------------------------------------------------

    /// <summary>Sicherung der Schreibfläche und Angebot geretteter Texte (Kapitel 3.3).</summary>
    public void AttachRecovery(RecoveryStore store, bool copyAllowed)
    {
        _store = store;
        _copyAllowed = copyAllowed;
        CopyButton.IsEnabled = copyAllowed;
        _draft = new PadDraft(store);
        _draft.Failed += (_, kind) => ShowHint($"Sicherung gescheitert: {kind}");
        _draft.Changed(Pad.Text);
    }

    /// <summary>
    /// Zeigt das Fenster; <paramref name="mark"/> ersetzt das gemerkte Feld. Mit <paramref name="activate"/> (Aufruf durch den
    /// Benutzer) wird es aktiviert und die Schreibmarke gesetzt; ohne (Angebot beim Start) erscheint es, ohne den Fokus zu
    /// nehmen: Freihand nimmt nie von sich aus den Fokus (Handtest P11 vom 17.11.2026).
    /// </summary>
    public void Open(MarkResult? mark, bool activate = true)
    {
        if (mark is { Mark: { } target })
        {
            Mark = target;
        }
        else if (mark is { Refusal: { } refusal } && refusal != BindRefusal.OwnProcess)
        {
            // Aus einem nicht bindbaren Ort geöffnet: kein Ziel für Übernehmen, mit Grund
            Mark = null;
            ShowHint($"Kein Zielfeld gemerkt ({refusal}). Ins Zielfeld klicken, dann {_keyText} drücken.");
        }

        ShowTarget();
        Refresh();
        if (!activate)
        {
            ShowActivated = false;
            Show();
            ShowActivated = true;
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Pad.Focus();
        Keyboard.Focus(Pad);
    }

    private string _keyText = "Strg+Alt+D";

    /// <summary>Taste fürs Diktatfenster, für Hinweise.</summary>
    public void SetKeyText(string text) => _keyText = text;

    /// <summary>Hängt gehaltenen Text ans Ende der Schreibfläche, auf eigener Zeile (Kapitel 3.4).</summary>
    public void Append(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        Pad.Select(Pad.Text.Length, 0);
        var separator = Pad.Text.Length == 0 || Pad.Text.EndsWith('\n') ? string.Empty : Environment.NewLine;
        Pad.SelectedText = separator + PadText.ToCore(text).Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        Pad.Select(Pad.Text.Length, 0);
    }

    /// <summary>Liest die Ablage neu (Scan) und zeigt die Liste; laufende Entwürfe dieses Laufs zeigt sie nie.</summary>
    public void Refresh()
    {
        if (_store is null)
        {
            return;
        }

        RecoveryScan scan;
        try
        {
            scan = _store.Scan();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Offer(scan);
    }

    /// <summary>Angebot nach dem Start (AU-12): Zeitpunkt, Länge, die ersten Wörter; nie eingefügt.</summary>
    public void Offer(RecoveryScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        // Gerettet ist nur, was nirgends angekommen ist; Übernommenes bleibt still abrufbar (Handtest vom 11.11.2026)
        Drafts.ItemsSource = DraftOffers.Ordered(scan).Select(draft => new DraftView(draft, _copyAllowed)).ToList();
        _issues = scan.Issues.Count;
        RecoveryIssues.Text = _issues == 0 ? string.Empty
            : $"{_issues} Sicherung{(_issues == 1 ? " ist" : "en sind")} nicht lesbar oder unvollständig; „Alle löschen“ entfernt sie.";
        RecoveryIssues.Visibility = _issues == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecoveryPanel.Visibility = scan.Drafts.Count + _issues == 0 ? Visibility.Collapsed : Visibility.Visible;
        Offers = DraftOffers.Rescued(scan);
        OffersChanged?.Invoke(this, EventArgs.Empty);
    }

    private int _issues;

    /// <summary>Rückfrage vor „Alle löschen“; die Fenstertests ersetzen sie, damit kein Dialog erscheint.</summary>
    internal Func<int, bool>? ConfirmDeleteAllOverride { get; set; }

    private bool ConfirmDeleteAll(int count) => ConfirmDeleteAllOverride?.Invoke(count) ?? MessageBox.Show(
        this,
        $"Alle {count} gesicherten Texte löschen? Das lässt sich nicht rückgängig machen. Der Text in der Schreibfläche bleibt.",
        "Freihand – Alle löschen",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowHint(string text)
    {
        Hint.Text = text;
        Hint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Beim Beenden: sichern und stehen lassen, damit der Text beim nächsten Start angeboten wird.</summary>
    public void CloseForExit()
    {
        _draft.Dispose();
        _closing = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Schliessen blendet nur aus; der Text bleibt
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void ShowTarget() =>
        TargetLine.Text = Mark is { } mark
            ? $"Übernehmen nach: {mark.Display}"
            : $"Kein Zielfeld gemerkt – fürs Übernehmen ins Zielfeld klicken und {_keyText} drücken.";

    private void OnPadChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _draft.Changed(Pad.Text);

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Pad.Text.Length > 0)
        {
            Copy(Pad.Text);
        }
    }

    private async void OnHandOver(object sender, RoutedEventArgs e)
    {
        if (Mark is not { } mark)
        {
            ShowHint($"Kein Zielfeld gemerkt: ins Zielfeld klicken, dann {_keyText} drücken.");
            return;
        }

        if (HandOver is not { } handOver || Pad.Text.Trim().Length == 0)
        {
            return;
        }

        // Ausblenden, damit das gemerkte Feld den Fokus zurückbekommt; geprüft wird in der Steuerung (AU-10)
        ShowHint(string.Empty);
        HandOverButton.IsEnabled = false;
        Hide();
        var sent = Pad.Text;
        HandOverReport report;
        try
        {
            report = await handOver(sent, mark);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report = new HandOverReport(HandOverResult.Refused, sent, exception.GetType().Name);
        }
        finally
        {
            HandOverButton.IsEnabled = true;
        }

        // Regel (Codex zu PR #52): Die Schreibfläche wird nur verändert, wenn sie seit dem Beginn unverändert ist; was
        // du inzwischen geschrieben hast, geht nie verloren und wird weiter gesichert, nicht als übernommen markiert
        if (Pad.Text != sent)
        {
            // Geändert werden konnte nur im wieder geöffneten Fenster: es ist schon sichtbar, nur der Hinweis kommt dazu
            ShowHint(report.Result switch
            {
                HandOverResult.Written => "Übernommen. Was du inzwischen hier geändert hast, bleibt stehen.",
                HandOverResult.Partial => StatusText.Message(ControllerMessage.HandOverRefused, report.Reason)
                    + " Ein Teil ist übernommen; der Text hier ist unverändert geblieben, bitte prüfen.",
                _ => StatusText.Message(ControllerMessage.HandOverRefused, report.Reason),
            });
            return;
        }

        switch (report.Result)
        {
            case HandOverResult.Written:
                // Übernommen: Haltefrist beginnt (ER-06); Strg+Z holt den Text zurück
                _draft.HandedOver();
                ReplaceAll(string.Empty);
                break;
            case HandOverResult.Partial:
                // Der geschriebene Teil steht im Feld, der Rest bleibt hier
                ReplaceAll(report.Held.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
                Reveal();
                ShowHint(StatusText.Message(ControllerMessage.HandOverRefused, report.Reason) + " Der Rest steht hier.");
                break;
            default:
                Reveal();
                ShowHint(StatusText.Message(ControllerMessage.HandOverRefused, report.Reason));
                break;
        }
    }

    /// <summary>
    /// Ersetzt das erneute Zeigen nach einem nicht ganz gelungenen Übernehmen; die Fenstertests setzen es, damit sie nie
    /// ein Fenster zeigen oder den Fokus bewegen (Codex-Deltaprüfung PR #52).
    /// </summary>
    internal Action? RevealOverride { get; set; }

    private void Reveal()
    {
        if (RevealOverride is { } reveal)
        {
            reveal();
        }
        else
        {
            // Rückmeldung nach dem Übernehmen: zeigen, nie den Fokus nehmen (Codex-Prüfung PR #62)
            Open(null, activate: false);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        // Leeren heisst loswerden (Kapitel 3.2): Sicherung gelöscht; Strg+Z holt den Text zurück und sichert ihn neu
        _draft.Cleared();
        ReplaceAll(string.Empty);
        ShowHint(string.Empty);
        Pad.Focus();
    }

    private void OnOpenDraft(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is not { } draft || _store is null)
        {
            return;
        }

        Append(draft.Text);

        // Erst wenn der Text hier gesichert ist, wird der alte Entwurf gelöscht; sonst bliebe er doppelt oder ginge verloren
        if (_draft.Flush())
        {
            Delete(draft.Id);
        }

        Refresh();
    }

    private void OnCopyDraft(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is { } draft)
        {
            Copy(draft.Text);
        }
    }

    private void OnDeleteDraft(object sender, RoutedEventArgs e)
    {
        if (Find(sender) is { } draft)
        {
            Delete(draft.Id);
            Refresh();
        }
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        var listed = Drafts.ItemsSource is IEnumerable<DraftView> views ? views.Select(view => view.Id).ToList() : [];
        if (_store is null || listed.Count + _issues == 0 || !ConfirmDeleteAll(listed.Count + _issues))
        {
            return;
        }

        if (_issues == 0)
        {
            // Nur die angezeigten; laufende Sicherungen (Schreibfläche, gehaltener Text) bleiben unberührt
            foreach (var id in listed)
            {
                Delete(id);
            }

            Refresh();
            return;
        }

        // Unlesbare Dateien lassen sich nur mit der ganzen Ablage entfernen; das löscht auch laufende Sicherungen
        try
        {
            _store.DeleteAll();
        }
        catch (RecoveryException exception)
        {
            ShowHint($"Löschen gescheitert: {exception.Issue.Kind}");
        }

        // Den Text hier sofort neu sichern, nicht erst im nächsten Takt (Codex zu PR #53)
        _draft.Restart(Pad.Text);
        Refresh();
    }

    private void Delete(Guid id)
    {
        try
        {
            _store?.Delete(id);
        }
        catch (RecoveryException exception)
        {
            ShowHint($"Löschen gescheitert: {exception.Issue.Kind}");
        }
    }

    private void Copy(string text)
    {
        if (!_copyAllowed)
        {
            ShowHint("Zwischenablage im strengen Profil gesperrt.");
            return;
        }

        try
        {
            PrivateClipboard.SetText(text);
            ShowHint("Kopiert.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowHint($"Zwischenablage gesperrt: {exception.GetType().Name}");
        }
    }

    private RecoverableDraft? Find(object sender) =>
        sender is FrameworkElement { Tag: Guid id } && Drafts.ItemsSource is IEnumerable<DraftView> views
            ? views.FirstOrDefault(view => view.Id == id)?.Draft
            : null;

    /// <summary>Zeile der Liste „Wiederherstellbar“: Zeitpunkt, Länge, die ersten Wörter.</summary>
    private sealed class DraftView(RecoverableDraft draft, bool canCopy)
    {
        public RecoverableDraft Draft { get; } = draft;

        public Guid Id => Draft.Id;

        public bool CanCopy { get; } = canCopy;

        public string Summary
        {
            get
            {
                var text = Draft.Text;
                var words = text.Split((char[]?)null, FirstWords + 1, StringSplitOptions.RemoveEmptyEntries);
                var start = string.Join(' ', words.Take(FirstWords));
                var more = words.Length > FirstWords ? " …" : string.Empty;
                var state = DraftOffers.IsRescued(Draft) ? string.Empty : "übernommen · ";
                return string.Create(CultureInfo.CurrentCulture,
                    $"{state}{Draft.UpdatedAt.LocalDateTime:dd.MM. HH:mm} · {text.Length} Zeichen · „{start}{more}“");
            }
        }
    }
}
