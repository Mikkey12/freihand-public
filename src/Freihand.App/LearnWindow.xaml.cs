using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Windows.Recognition;

namespace Freihand.App;

/// <summary>
/// Wort lernen. Der
/// Nutzer tippt die Schreibweise und spricht das Wort; was Freihand hört, wird als Ersetzung vorgeschlagen.
/// Gewöhnliche Wörter (Rechtschreibprüfung von Windows) sind nie vorgewählt. Zuhören endet spätestens nach einer Minute.
/// Weder Wörter noch Gehörtes gehen ins Protokoll.
/// </summary>
public partial class LearnWindow : Window
{
    private static readonly TimeSpan ListenLimit = TimeSpan.FromMinutes(1);
    private readonly WordStore _store;
    private readonly WordListener? _listener;
    private readonly Func<string?> _device;
    private readonly Func<bool> _idle;
    private readonly Action<string>? _log;
    private readonly Core.Commands.CommandSet _commands;
    private readonly InterpreterOptions? _options;
    private string? _candidateSpelling;
    private readonly WindowsSpelling? _spelling;
    private readonly List<Utterance> _heard = [];
    private readonly List<string> _shown = [];
    private readonly CancellationTokenSource _closed = new();
    private CancellationTokenSource? _stop;
    private bool _listening;
    private int _round;

    /// <param name="listener">Ohne Erkenner null: Sprechen meldet dann, dass die Erkennung nicht bereit ist.</param>
    /// <param name="idle">Kein Diktat läuft; Diktat und Lernen teilen das Mikrofon.</param>
    /// <param name="commands">Dieselben Befehle wie beim Diktat; nur was damit ersetzt würde, wird vorgeschlagen.</param>
    /// <param name="options">Dieselbe Auslegung wie beim Diktat (ohne Formatter); ohne Angabe die Standardauslegung.</param>
    internal LearnWindow(
        WordStore store, WordListener? listener, Core.Commands.CommandSet commands, InterpreterOptions? options,
        Func<string?> device, Func<bool> idle, Action<string>? log)
    {
        _commands = commands;
        _options = options;
        _store = store;
        _listener = listener;
        _device = device;
        _idle = idle;
        _log = log;
        _spelling = WindowsSpelling.TryCreate("de-CH", "de-DE");
        InitializeComponent();
        var problems = _store.Problems();
        State.Text = problems.Count == 0
            ? _spelling is null ? "Keine deutsche Rechtschreibprüfung gefunden: nichts wird vorgewählt, bitte selbst ankreuzen." : string.Empty
            : $"In der Wörterliste werden {problems.Count} Zeilen übergangen ({problems[0]}).";
        Closed += (_, _) =>
        {
            _closed.Cancel();
            _spelling?.Dispose();
        };
        ShowSentences();
        ShowLearned();
        SpellingBox.Focus();
    }

    /// <summary>Was Freihand schon gelernt hat und wirkt (woerter-gelernt.txt, nicht von woerter.txt übersteuert).</summary>
    private void ShowLearned()
    {
        var learned = _store.Learned();
        Learned.Text = learned.Count == 0
            ? "Noch nichts gelernt."
            : "Gelernt: " + string.Join(" · ", learned.Select(word => $"{word.Spelling} ← {string.Join(", ", word.Heard)}"));
    }

    /// <summary>
    /// Ändert sich die Schreibweise nach dem Zuhören, werden die Vorschläge für sie neu geprüft (eigene Vorprüfung zu
    /// PR #66: sonst würde für eine andere Schreibweise gespeichert als geprüft).
    /// </summary>
    private void OnSpellingChanged(object sender, TextChangedEventArgs e)
    {
        ShowSentences();
        if (Candidates is null || _listening || _heard.Count == 0)
        {
            return;
        }

        var spelling = SpellingBox.Text.Trim();
        if (WordListFile.Invalid(spelling) is null)
        {
            ShowCandidates(spelling);
        }
        else
        {
            _candidateSpelling = null;
            Candidates.Children.Clear();
            SaveButton.IsEnabled = false;
        }
    }

    /// <summary>Die Lernsätze mit dem getippten Wort an der Lücke.</summary>
    private void ShowSentences()
    {
        if (Sentences is null)
        {
            return;
        }

        var word = SpellingBox.Text.Trim();
        Sentences.Text = string.Join('\n', WordCandidates.Sentences.Select(sentence =>
            "„" + sentence.Replace(WordCandidates.Gap, word.Length > 0 ? word : "___", StringComparison.Ordinal).TrimEnd('.') + "“"));
    }

    private async void OnListen(object sender, RoutedEventArgs e)
    {
        if (_listening)
        {
            _stop?.Cancel();
            return;
        }

        var spelling = SpellingBox.Text.Trim();
        if (WordListFile.Invalid(spelling) is { } reason)
        {
            State.Text = "Die Schreibweise " + reason + ".";
            return;
        }

        if (_listener is null)
        {
            State.Text = "Die Erkennung ist nicht bereit.";
            return;
        }

        if (!_idle())
        {
            State.Text = "Gerade läuft ein Diktat. Bitte zuerst beenden.";
            return;
        }

        _listening = true;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _stop.CancelAfter(ListenLimit);
        ListenButton.Content = "_Fertig";
        SpellingBox.IsEnabled = false;
        SaveButton.IsEnabled = false;
        State.Text = "Freihand hört zu …";
        var count = 0;
        var round = _round;
        try
        {
            // Die Anzeige läuft mit; massgeblich ist die vollständige Liste nach dem Ende
            var heard = await _listener.ListenAsync(
                _device(), text => Dispatcher.BeginInvoke(() =>
                {
                    if (round == _round)
                    {
                        ShowHeard(text);
                    }
                }), _stop.Token, _closed.Token);
            if (round != _round)
            {
                // „Von vorne“ während des Zuhörens: das Gehörte dieser Runde zählt nicht
                return;
            }

            _heard.AddRange(heard);
            count = heard.Count;
            State.Text = heard.Count == 0 ? "Nichts gehört. Bitte nochmals sprechen." : string.Empty;
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && round == _round)
        {
            State.Text = exception is InvalidOperationException { Message: WordListener.NoMicrophone }
                ? "Kein Mikrofon gefunden. Bitte im Menü „Mikrofon“ eines wählen."
                : exception is InvalidOperationException
                ? "Das Mikrofon ist nicht verfügbar (läuft gerade ein Diktat?)."
                : $"Zuhören gescheitert ({exception.GetType().Name}).";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Fehler einer verworfenen Runde: nichts anzeigen
            return;
        }
        finally
        {
            _listening = false;
            _stop.Dispose();
            _stop = null;
            if (round != _round && !_closed.IsCancellationRequested)
            {
                ListenButton.Content = "_Sprechen";
                SpellingBox.IsEnabled = true;
            }
        }

        if (_closed.IsCancellationRequested)
        {
            return;
        }

        _log?.Invoke($"wort lernen gehoert={count}");
        var listened = State.Text;
        ListenButton.Content = "_Sprechen";
        SpellingBox.IsEnabled = true;
        ShowCandidates(spelling, listened);
    }

    private void ShowHeard(string text)
    {
        _shown.Add(WordCandidates.Clean(text));
        Heard.Text = string.Join(" · ", _shown.TakeLast(5));
    }

    /// <param name="listened">
    /// Meldung vom Zuhören (z. B. kein Mikrofon, nichts gehört); bleibt vorne stehen (Codex zu PR #66).
    /// </param>
    private void ShowCandidates(string spelling, string? listened = null)
    {
        Candidates.Children.Clear();
        State.Text = listened ?? string.Empty;
        _candidateSpelling = spelling;

        // Nur das Wort an der Lücke eines Lernsatzes zählt, und nur, was der Diktat-Code wirklich ersetzen würde
        var result = WordCandidates.FromUtterances(spelling, _heard, _commands, _options, word => _spelling?.IsKnown(word));
        var candidates = result.Candidates;
        var unmatched = result.Unmatched;
        foreach (var candidate in candidates)
        {
            var note = candidate.Kind switch
            {
                CandidateKind.Same => "richtig erkannt",
                CandidateKind.Ordinary => "gewöhnliches Wort – nur ankreuzen, wenn du es nie so meinst",
                CandidateKind.Unchecked => "nicht geprüft – selbst entscheiden",
                _ => "wird ersetzt",
            };
            Candidates.Children.Add(new CheckBox
            {
                Content = $"{candidate.Text} ({candidate.Count}×) – {note}",
                Tag = candidate.Text,
                IsChecked = candidate.Preselected,
                IsEnabled = candidate.Kind != CandidateKind.Same,
                Margin = new Thickness(0, 2, 0, 2),
            });
        }

        SaveButton.IsEnabled = candidates.Any(candidate => candidate.Kind != CandidateKind.Same);
        if (candidates.Count > 0 && candidates.All(candidate => candidate.Kind == CandidateKind.Same))
        {
            State.Text = (State.Text.Length > 0 ? State.Text + " " : string.Empty)
                + "Freihand erkennt das Wort schon richtig; nichts zu speichern.";
        }

        if (unmatched > 0)
        {
            State.Text = (State.Text.Length > 0 ? State.Text + " " : string.Empty)
                + $"{unmatched} Satz/Sätze passten nicht zu den Vorgaben (Pause nach jedem Satz?) und zählen nicht.";
        }

        if (result.Unusable > 0)
        {
            State.Text = (State.Text.Length > 0 ? State.Text + " " : string.Empty)
                + $"{result.Unusable}-mal liess sich das Gehörte nicht als Ersetzung verwenden und zählt nicht.";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Gespeichert wird genau für die Schreibweise, für die geprüft wurde
        var spelling = _candidateSpelling ?? string.Empty;
        var chosen = Candidates.Children.OfType<CheckBox>()
            .Where(box => box.IsChecked == true && box.IsEnabled).Select(box => (string)box.Tag).ToList();
        if (WordListFile.Invalid(spelling) is not null || chosen.Count == 0)
        {
            State.Text = "Nichts angekreuzt.";
            return;
        }

        LearnOutcome outcome;
        try
        {
            outcome = _store.Add(new LearnedWord(spelling, chosen));
        }
        catch (InvalidOperationException)
        {
            State.Text = $"Die Liste der gelernten Wörter ist voll ({WordListFile.MaxWords} Wörter); nichts gespeichert.";
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            State.Text = $"Speichern gescheitert ({exception.GetType().Name}); bitte nochmals versuchen.";
            return;
        }

        if (outcome == LearnOutcome.TakenByOther)
        {
            // Gespeichert heisst wirksam: eine gehörte Form gehört schon zu einem anderen Wort
            var taken = chosen.Select(heard => (Heard: heard, Owner: _store.Owner(heard)))
                .Where(item => item.Owner is not null && item.Owner != spelling)
                .Select(item => $"„{item.Heard}“ → „{item.Owner}“");
            State.Text = "Nichts gespeichert: " + string.Join(", ", taken) + " ist schon gelernt. Schalte jenes Wort in "
                + "woerter.txt ab (nur die Schreibweise mit Doppelpunkt, z. B. „Wort:“) und lerne dann neu.";
            return;
        }

        if (outcome == LearnOutcome.InOwnList)
        {
            // Deine Zeile in woerter.txt hat Vorrang; gespeichert würde es nicht wirken
            State.Text = $"„{spelling}“ steht in deiner Wörterliste (woerter.txt), die Vorrang hat. Nichts gespeichert; "
                + $"ergänze dort: {string.Join(", ", chosen)}";
            return;
        }

        State.Text = $"Gespeichert: {chosen.Count} Ersetzung(en) für „{spelling}“. Gilt ab dem nächsten Diktat.";
        ShowLearned();
        _heard.Clear();
        _shown.Clear();
        Heard.Text = string.Empty;
        Candidates.Children.Clear();
        SaveButton.IsEnabled = false;
    }

    private void OnOpenList(object sender, RoutedEventArgs e) => OpenList(_store);

    /// <summary>Schliessen per Klick oder Escape; IsCancel allein schliesst nur modale Fenster (Handtest vom 18.11.2026).</summary>
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Von vorne (Handtest vom 18.11.2026): Zuhören beenden und alles Gehörte dieser Runde verwerfen; die Schreibweise
    /// bleibt stehen und lässt sich ändern. Gespeichertes bleibt gespeichert.
    /// </summary>
    private void OnReset(object sender, RoutedEventArgs e)
    {
        _round++;
        _stop?.Cancel();
        _heard.Clear();
        _shown.Clear();
        Heard.Text = string.Empty;
        Candidates.Children.Clear();
        SaveButton.IsEnabled = false;
        State.Text = string.Empty;
        if (!_listening)
        {
            ListenButton.Content = "_Sprechen";
            SpellingBox.IsEnabled = true;
        }

        SpellingBox.Focus();
    }

    /// <summary>Wörterliste im Standardprogramm für Textdateien; legt sie bei Bedarf mit Kopf an.</summary>
    internal static void OpenList(WordStore store)
    {
        try
        {
            store.EnsureExists();
            Process.Start(new ProcessStartInfo(store.Path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Kein Programm für .txt oder Ordner gesperrt: die Datei bleibt, wo sie ist
        }
    }
}
