using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Transform;
using Freihand.Core.Platform;

namespace Freihand.App;

/// <summary>
/// Korrigieren und Glätten einer Markierung in einem anderen Programm. Regeln:
/// <list type="number">
/// <item>Gelesen wird nur die Markierung beim Tastendruck, nur im Arbeitsspeicher (SI-12).</item>
/// <item>Der Vorschlag erscheint zuerst; ersetzt wird nur mit „Ersetzen“ oder Enter, und nur, wenn im selben Feld noch
/// genau derselbe Text markiert ist (<see cref="DictationController.HandOverAsync(string, TargetMark, string)"/>).</item>
/// <item>Was nicht ersetzt werden konnte, bleibt kopierbar; nichts geht verloren.</item>
/// <item>Eine neue Taste ersetzt, was das Fenster zeigt; nur während Claude antwortet oder ersetzt wird, wirkt sie nicht.</item>
/// </list>
/// </summary>
public partial class SuggestionWindow : Window
{
    private CancellationTokenSource? _request;
    private Pending? _pending;
    private string? _copyText;
    private bool _replacing;
    private bool _closing;

    public SuggestionWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Discard();
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _pending is not null)
            {
                e.Handled = true;
                _ = ReplaceAsync();
            }
        };
    }

    /// <summary>Ersetzen über die Steuerung: Text, gemerktes Feld, die markierte Stelle, die ersetzt werden soll.</summary>
    internal Func<string, TargetMark, string, Task<HandOverReport>>? HandOver { get; set; }

    /// <summary>Prüfungen: statt das Fenster zu zeigen und zu aktivieren; nie im Betrieb gesetzt.</summary>
    internal Action? RevealOverride { get; set; }

    /// <summary>Prüfungen: statt in die Zwischenablage zu kopieren.</summary>
    internal Action<string>? CopyOverride { get; set; }

    /// <summary>Läuft gerade eine Anfrage an Claude oder ein Ersetzen? Dann wirkt eine neue Taste nicht.</summary>
    internal bool Working => _request is not null || _replacing;

    /// <summary>Taste fürs Korrigieren oder Glätten aus einem anderen Programm; <paramref name="key"/> nennt die Taste in Hinweisen.</summary>
    internal async Task RunAsync(CorrectionRequest request, CorrectionService corrector, string route, string key)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(corrector);
        if (Working || (request.Mark.Refusal == BindRefusal.OwnProcess && IsVisible))
        {
            // Aus diesem Fenster heraus oder während der Arbeit: nur nach vorn holen
            Reveal();
            return;
        }

        Reset(request.Kind, route);
        if (request.Mark.Mark is not { } mark)
        {
            ShowProblem(CorrectionTexts.NoField(request.Mark.Refusal, key));
            return;
        }

        if (request.Selection.Text is not { } selection)
        {
            ShowProblem(CorrectionTexts.Selection(request.Selection.Refusal, key));
            return;
        }

        var original = selection.Reveal();
        ShowHint(request.Kind == CorrectionKind.Correct ? "Korrigiere …" : "Glätte …");
        ReplaceButton.IsEnabled = false;
        Reveal();
        using var cancel = new CancellationTokenSource();
        _request = cancel;
        try
        {
            var suggestion = await corrector.SuggestAsync(original, request.Kind, cancel.Token, selection.DataClass);
            if (suggestion.Unchanged)
            {
                ShowProblem("Keine Änderung vorgeschlagen.", activate: false);
                return;
            }

            _pending = new Pending(suggestion, mark, original);
            CorrectionTexts.Render(PreviewText, suggestion);
            PreviewHost.Visibility = Visibility.Visible;
            Warnings.Text = string.Join(" ", suggestion.Warnings);
            Warnings.Visibility = suggestion.Warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ShowHint(string.Empty);
            ReplaceButton.IsEnabled = true;
            FocusIfForeground(ReplaceButton);
        }
        catch (TransformFailedException exception)
        {
            ShowProblem(CorrectionTexts.Describe(exception.Failure), activate: false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Verworfen, während Claude antwortete
        }
        finally
        {
            _request = null;
        }
    }

    /// <summary>Beim Beenden von Freihand: wirklich schliessen.</summary>
    public void CloseForExit()
    {
        _closing = true;
        _request?.Cancel();
        Close();
    }

    internal void OnReplace(object sender, RoutedEventArgs e) => _ = ReplaceAsync();

    internal void OnDiscard(object sender, RoutedEventArgs e) => Discard();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_copyText is not { } text)
        {
            return;
        }

        if (CopyOverride is { } copy)
        {
            copy(text);
        }
        else
        {
            try
            {
                PrivateClipboard.SetText(text);
            }
            catch (ExternalException)
            {
                ShowHint("Die Zwischenablage ist gerade belegt; bitte nochmals „Kopieren“.");
                return;
            }
        }

        ShowHint("Kopiert. Mit Strg+V selbst einfügen.");
    }

    /// <summary>
    /// Blendet sich aus, damit das Feld den Fokus zurückbekommt, und lässt die Steuerung ersetzen. Gelingt das nicht ganz,
    /// kommt das Fenster mit dem Grund zurück und der Text bleibt kopierbar.
    /// </summary>
    internal async Task ReplaceAsync()
    {
        if (_pending is not { } pending || HandOver is not { } handOver || _replacing)
        {
            return;
        }

        _replacing = true;
        _pending = null;
        Conceal();
        try
        {
            HandOverReport report;
            try
            {
                report = await handOver(pending.Suggestion.Text, pending.Mark, pending.Original);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Unbekannt, ob etwas ankam: nichts als ersetzt melden, der Vorschlag bleibt kopierbar
                report = new HandOverReport(HandOverResult.Refused, pending.Suggestion.Text, exception.GetType().Name);
            }

            if (report.Result == HandOverResult.Written)
            {
                return;
            }

            ShowProblem(CorrectionTexts.Replace(new HandOverReportView(report.Result == HandOverResult.Partial, report.Reason)), activate: false);
            _copyText = report.Result == HandOverResult.Partial ? report.Held : pending.Suggestion.Text;
            CopyButton.Visibility = Visibility.Visible;
            Reveal(activate: false);
        }
        finally
        {
            _replacing = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Schliessen heisst verwerfen; das Fenster bleibt für die nächste Taste
        if (!_closing)
        {
            e.Cancel = true;
            Discard();
        }

        base.OnClosing(e);
    }

    private void Discard()
    {
        if (_replacing)
        {
            return;
        }

        _request?.Cancel();
        _pending = null;
        Conceal();
    }

    private void Reset(CorrectionKind kind, string route)
    {
        _pending = null;
        _copyText = null;
        Heading.Text = kind == CorrectionKind.Correct ? "Fehler korrigieren" : "Glätten";
        RouteLine.Text = route;
        PreviewText.Inlines.Clear();
        PreviewHost.Visibility = Visibility.Collapsed;
        Warnings.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Collapsed;
        ReplaceButton.IsEnabled = false;
        ShowHint(string.Empty);
    }

    /// <param name="activate">
    /// Nur unmittelbar auf Taste oder Klick; Rückmeldungen nach einem Warten (Antwort, Fehler, Ersetzen) zeigen das Fenster
    /// ohne Aktivieren: Freihand nimmt nie von sich aus den Fokus (Codex-Prüfung PR #62).
    /// </param>
    private void ShowProblem(string text, bool activate = true)
    {
        _pending = null;
        PreviewHost.Visibility = Visibility.Collapsed;
        Warnings.Visibility = Visibility.Collapsed;
        ReplaceButton.IsEnabled = false;
        ShowHint(text);
        Reveal(activate);
        if (activate)
        {
            DiscardButton.Focus();
        }
        else
        {
            FocusIfForeground(DiscardButton);
        }
    }

    /// <summary>Fokus nur innerhalb dieses Fensters setzen, und nur wenn es noch das Vordergrundfenster ist.</summary>
    private void FocusIfForeground(UIElement element)
    {
        if (IsActive && Windows.Input.ForegroundWindow.Is(new WindowInteropHelper(this).Handle))
        {
            element.Focus();
        }
    }

    private void ShowHint(string text)
    {
        Hint.Text = text;
        Hint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Reveal(bool activate = true)
    {
        if (RevealOverride is { } reveal)
        {
            reveal();
            return;
        }

        if (!activate)
        {
            if (!IsVisible)
            {
                ShowActivated = false;
                Show();
                ShowActivated = true;
            }

            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void Conceal()
    {
        if (RevealOverride is null)
        {
            Hide();
        }
    }

    /// <summary>Ein Vorschlag, das Feld und die markierte Stelle, für die er gilt.</summary>
    private sealed record Pending(Suggestion Suggestion, TargetMark Mark, string Original);
}
