using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Freihand.Adapters.Transform;
using Freihand.Core.Platform;

namespace Freihand.App;

/// <summary>Texte und Darstellung fürs Korrigieren und Glätten, gemeinsam für Diktatfenster und Vorschlagsfenster (B8, B9).</summary>
internal static class CorrectionTexts
{
    private static readonly Brush AddedBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x2E, 0xA0, 0x43)));

    public static string Describe(TransformFailure failure) => failure switch
    {
        TransformFailure.NoKey => "Kein Schlüssel für Korrigieren: im Menü des Infobereichs „Schlüssel für Korrigieren eintragen“.",
        TransformFailure.KeyRejected => "Der Schlüssel wurde abgelehnt; bitte im Menü neu eintragen.",
        TransformFailure.Unreachable => "Keine Verbindung zum Dienst; der Text bleibt, wie er ist.",
        TransformFailure.Busy => "Der Dienst ist gerade überlastet; bitte gleich nochmals versuchen.",
        TransformFailure.Timeout => "Keine Antwort innert 60 Sekunden; der Text bleibt, wie er ist.",
        TransformFailure.TooLong => "Der Text ist zu lang; bitte einen Teil markieren und diesen überarbeiten.",
        TransformFailure.Failed => "Korrigieren ist gescheitert; der Text bleibt, wie er ist. Bitte nochmals versuchen.",
        _ => "Der Dienst hat die Anfrage abgelehnt; der Text bleibt, wie er ist.",
    };

    /// <summary>Warum keine Markierung gelesen wurde; <paramref name="key"/> ist die gedrückte Taste.</summary>
    public static string Selection(SelectionRefusal refusal, string key) => refusal switch
    {
        SelectionRefusal.NoSelection => $"Nichts markiert. Erst den Text markieren, dann {key} drücken.",
        SelectionRefusal.TooLong => $"Die Markierung ist zu lang (über {SelectionText.MaxLength:N0} Zeichen); bitte einen Teil markieren.",
        SelectionRefusal.SpecialContent =>
            "Die Markierung enthält eine Tabelle, ein Bild, ein Feld oder mehrere Stellen; bitte nur Fliesstext markieren.",
        SelectionRefusal.PasswordField => "Aus Passwortfeldern wird nie etwas gelesen.",
        _ => "Dieses Programm gibt die Markierung nicht preis. Den Text ins Diktatfenster holen und dort korrigieren.",
    };

    /// <summary>Kein Feld beim Tastendruck (Fokus nicht in einem Textfeld oder nicht bindbar).</summary>
    public static string NoField(BindRefusal? refusal, string key) =>
        $"Kein Textfeld erkannt{(refusal is { } reason ? $" ({reason})" : string.Empty)}. Ins Feld klicken, Text markieren, dann {key} drücken.";

    /// <summary>Warum „Ersetzen“ nichts oder nur einen Teil geschrieben hat; nur Codes, nie Text.</summary>
    public static string Replace(HandOverReportView report) => report switch
    {
        { Partial: true } => "Nur teilweise ersetzt. Den Rest kopieren und selbst einfügen.",
        { Reason: "SelectionChanged" } => "Die Markierung hat sich inzwischen geändert; nichts ersetzt. Der Vorschlag lässt sich kopieren.",
        { Reason: "Busy" } => "Während eines Diktats wird nichts ersetzt. Der Vorschlag lässt sich kopieren.",
        { Reason: "NoFocus" or "NotMarkedField" or "OwnProcess" } =>
            "Das Feld hatte keinen Fokus mehr; nichts ersetzt. Der Vorschlag lässt sich kopieren.",
        { Reason: { } reason } => $"Nichts ersetzt ({reason}). Der Vorschlag lässt sich kopieren.",
        _ => "Nichts ersetzt. Der Vorschlag lässt sich kopieren.",
    };

    /// <summary>Vorschlag mit markierten Änderungen: neu grün hinterlegt, entfernt grau durchgestrichen.</summary>
    public static void Render(TextBlock target, Suggestion suggestion)
    {
        target.Inlines.Clear();
        foreach (var part in suggestion.Parts)
        {
            var run = new Run(part.Text);
            switch (part.Kind)
            {
                case DiffKind.Added:
                    run.Background = AddedBrush;
                    break;
                case DiffKind.Removed:
                    run.TextDecorations = TextDecorations.Strikethrough;
                    run.Foreground = Brushes.Gray;
                    break;
            }

            target.Inlines.Add(run);
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>Was das Vorschlagsfenster von einem Bericht zum Ersetzen braucht.</summary>
internal sealed record HandOverReportView(bool Partial, string? Reason);
