using System.Globalization;
using System.Windows;
using Freihand.Adapters.Dictation;

namespace Freihand.App;

/// <summary>
/// „Feld messen“: zuerst die Hinweise (Bedienregel vom 21.11.2026), dann blendet sich das Fenster aus, damit
/// der Fokus ins Zielfeld kann, und zeigt am Schluss das Ergebnis. Ist das Feld sicher erkannt, lässt sich ein Wert von
/// Hand festlegen (für Programme, die Windows nicht zurücklesen lässt).
/// </summary>
public partial class FieldMeasureWindow : Window
{
    private readonly Func<string?> _blocked;
    private readonly Func<bool, Task<MeasurementResult>> _measure;
    private readonly Func<MeasuredField, string> _saveManual;
    private readonly Action _openList;
    private MeasuredField? _candidate;

    /// <param name="blocked">Grund, warum jetzt nicht gemessen werden kann (z. B. laufendes Diktat); null, wenn es geht.</param>
    /// <param name="measure">Misst (Wert: nur erkennen).</param>
    /// <param name="saveManual">Speichert einen Wert von Hand und sagt, was gilt.</param>
    /// <param name="openList">Öffnet die Liste der gemessenen Felder.</param>
    public FieldMeasureWindow(
        Func<string?> blocked, Func<bool, Task<MeasurementResult>> measure, Func<MeasuredField, string> saveManual, Action openList)
    {
        _blocked = blocked;
        _measure = measure;
        _saveManual = saveManual;
        _openList = openList;
        InitializeComponent();
        foreach (var step in FieldMeasurement.Steps)
        {
            ManualBox.Items.Add(step.ToString(CultureInfo.CurrentCulture));
        }
    }

    private void OnStart(object sender, RoutedEventArgs e) => _ = RunAsync(identifyOnly: false);

    private void OnIdentify(object sender, RoutedEventArgs e) => _ = RunAsync(identifyOnly: true);

    private async Task RunAsync(bool identifyOnly)
    {
        if (_blocked() is { } reason)
        {
            ShowResult(new MeasurementResult(null, reason, false));
            return;
        }

        StartButton.IsEnabled = false;
        IdentifyButton.IsEnabled = false;
        Hide();
        MeasurementResult result;
        try
        {
            result = await _measure(identifyOnly);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = new MeasurementResult(null, $"Messung gescheitert ({exception.GetType().Name}). Bitte das Feld prüfen und den Prüftext von Hand löschen.", true);
        }

        // Nicht aktivieren (Codex zu PR #70): wer inzwischen in einem anderen Fenster arbeitet, behält den Fokus. Das Fenster
        // kommt nur nach vorne; Topmost ein und aus setzt es zuoberst, ohne es zu aktivieren
        ShowResult(result);
        ShowActivated = false;
        Topmost = true;
        Show();
        Topmost = false;
    }

    private void ShowResult(MeasurementResult result)
    {
        Before.Visibility = Visibility.Collapsed;
        After.Visibility = Visibility.Visible;
        StartButton.Visibility = Visibility.Collapsed;
        IdentifyButton.Visibility = Visibility.Collapsed;
        ResultText.Text = result.Text;
        var shown = result.Field ?? result.Candidate;
        IdentityText.Text = shown is null
            ? string.Empty
            : $"So meldet sich das Feld: {shown.Identity}." + (result.Field is null ? string.Empty : $" Gespeichert in „{MeasuredFieldStore.FileName}“.");
        _candidate = result.Candidate;
        ManualPanel.Visibility = _candidate is null ? Visibility.Collapsed : Visibility.Visible;
        var preset = result.Field?.Characters ?? 1;
        ManualBox.SelectedIndex = Math.Max(0, FieldMeasurement.Steps.ToList().IndexOf(preset));
    }

    private void OnManual(object sender, RoutedEventArgs e)
    {
        if (_candidate is not { } candidate || ManualBox.SelectedIndex < 0)
        {
            return;
        }

        var characters = FieldMeasurement.Steps[ManualBox.SelectedIndex];
        ManualText.Text = _saveManual(candidate with
        {
            Characters = characters,
            Manual = true,
            Date = DateOnly.FromDateTime(DateTime.Now),
        });
    }

    private void OnOpenList(object sender, RoutedEventArgs e) => _openList();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
