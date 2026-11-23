using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Freihand.Adapters.Transform;
using Freihand.Windows.Beta;

namespace Freihand.App;

/// <summary>
/// Schlüssel für Korrigieren eintragen: Einfügen aus der Zwischenablage ohne Tippen,
/// Speichern mit DPAPI, Löschen. Der Schlüssel wird nie angezeigt und nie protokolliert (SI-03). Ein Fehler beim Speichern
/// oder Löschen (Datei gesperrt, kein Zugriff) erscheint als Hinweis im Fenster und beendet nichts (Codex zu PR #55).
/// </summary>
public partial class KeyWindow : Window
{
    private readonly ApiKeyStore _store;

    public KeyWindow(ApiKeyStore store)
    {
        _store = store;
        InitializeComponent();
        ShowState();
    }

    /// <summary>Gespeichert oder gelöscht; die Anzeige baut den Dienst neu.</summary>
    public event EventHandler? KeyChanged;

    /// <summary>
    /// Schliessen per Klick oder Escape. IsCancel allein schliesst nur ein modales Fenster; dieses ist nicht modal
    /// (Handtest vom 18.11.2026 am Lernfenster).
    /// </summary>
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        try
        {
            KeyBox.Password = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : string.Empty;
            State.Text = KeyBox.Password.Length == 0 ? "Die Zwischenablage enthält keinen Text." : "Eingefügt – jetzt „Speichern“.";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            State.Text = "Die Zwischenablage ist gerade belegt; bitte nochmals versuchen.";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0)
        {
            State.Text = "Kein Schlüssel eingefügt.";
            return;
        }

        if (!ApiKeyFormat.IsValid(key))
        {
            State.Text = "Das sieht nicht wie ein Schlüssel aus (Leerzeichen, Zeilenumbruch oder Sonderzeichen). "
                + "Bitte in der Anthropic Console mit „Copy“ nochmals kopieren.";
            return;
        }

        if (Store(() => _store.Save(key), "Speichern"))
        {
            KeyBox.Clear();
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Store(_store.Delete, "Löschen"))
        {
            KeyBox.Clear();
        }
    }

    /// <summary>Speichern oder Löschen; ein Dateifehler wird gemeldet statt geworfen.</summary>
    private bool Store(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            State.Text = $"{what} gescheitert ({exception.GetType().Name}); bitte nochmals versuchen.";
            return false;
        }

        KeyChanged?.Invoke(this, EventArgs.Empty);
        ShowState();
        return true;
    }

    private void ShowState() =>
        State.Text = _store.HasKey ? "Ein Schlüssel ist gespeichert." : "Noch kein Schlüssel gespeichert.";
}
