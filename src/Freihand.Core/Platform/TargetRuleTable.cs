using Freihand.Core.Profiles;

namespace Freihand.Core.Platform;

/// <summary>Zieltabelle und Zielentscheid (P7-Konzept, Kapitel 5.4).</summary>
public sealed class TargetRuleTable
{
    private readonly List<TargetRule> _rules;

    /// <summary>Selbst gemessene Zeilen („Feld messen“, 21.11.2026); gelten vor den festen Zeilen, als Ganzes ersetzt.</summary>
    private volatile TargetRule[] _measured = [];

    /// <summary>
    /// <see cref="ArgumentException"/> bei <see cref="LineBreakMode.Enter"/> für Chat-Eingabe, Betreff, einzeilige oder
    /// unbekannte Felder und bei <see cref="TargetRule.MaxPartLength"/> ≤ 0.
    /// </summary>
    public TargetRuleTable(IEnumerable<TargetRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules];
        Validate(_rules);
    }

    /// <summary>
    /// Ersetzt die selbst gemessenen Zeilen; sie gelten ab dem nächsten Zielentscheid und vor den festen Zeilen, weil sie
    /// am Gerät gemessen sind. Gleiche Prüfungen wie im Konstruktor.
    /// </summary>
    public void SetMeasured(IEnumerable<TargetRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        TargetRule[] measured = [.. rules];
        Validate(measured);
        _measured = measured;
    }

    private static void Validate(IEnumerable<TargetRule> rules)
    {
        foreach (var rule in rules)
        {
            // Dort kann Enter senden (AU-13)
            if (rule.LineBreak == LineBreakMode.Enter &&
                rule.FieldKind is FieldKinds.ChatInput or FieldKinds.Subject or FieldKinds.SingleLine or FieldKinds.Unknown)
            {
                throw new ArgumentException($"Enter ist für die Feldart „{rule.FieldKind}“ nicht zulässig.", nameof(rules));
            }

            if (rule.MaxPartLength <= 0)
            {
                throw new ArgumentException("MaxPartLength muss grösser als 0 sein.", nameof(rules));
            }
        }
    }

    /// <summary>
    /// Eigener Prozess, Integritätsstufe, Passwortfeld, dann die erste passende Zeile. Gesperrt wird nur, was das Feld
    /// nachweislich meldet: ein Passwortfeld oder ein Fenster mit erhöhten Rechten. Nicht Lesbares sperrt nicht, Schreibschutz
    /// sperrt nie (Produktregel vom 11.11.2026, SI-04).
    /// </summary>
    public TargetDecision Decide(TargetFacts facts, ProfileStrictness strictness)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.IsOwnProcess)
        {
            return new NotBindable(BindRefusal.OwnProcess);
        }

        if (facts.IsElevated == true)
        {
            return new NotBindable(BindRefusal.Elevated);
        }

        if (facts.IsPassword == true)
        {
            return new NotBindable(BindRefusal.PasswordField);
        }

        // Regel (eigene Vorprüfung, Codex zu PR #70): Eine Messung ändert nur die Geschwindigkeit des Felds, das sich genau so
        // meldet wie bei der Messung; ein dort unbekanntes Merkmal passt nur auf ein Feld, bei dem es ebenso unbekannt ist
        // (sonst ginge ein Wert von einem Feld unbekannter Art auf ein- und mehrzeilige über). Passt auch eine feste Zeile auf
        // das aktuelle Feld, gelten deren Feldart, Anzeige, Umbrüche und Identität (z. B. Word mit Umbrüchen), nur mit der
        // gemessenen Teilgrösse
        var fixedRule = _rules.FirstOrDefault(rule => Matches(rule, facts));
        if (_measured.FirstOrDefault(rule => Matches(rule, facts) && rule.IsMultiLine == facts.IsMultiLine) is { } measured)
        {
            return new Bindable(fixedRule is null ? measured : fixedRule with { MaxPartLength = measured.MaxPartLength }, Known: true);
        }

        if (fixedRule is not null)
        {
            return new Bindable(fixedRule, Known: true);
        }

        return strictness == ProfileStrictness.Strict
            ? new NotBindable(BindRefusal.UnknownFieldStrict)
            : new Bindable(UnknownField(facts.ProcessName), Known: false);
    }

    /// <summary>
    /// Regel für unbekannte Felder; die Anzeige kommt nur aus dem Prozessnamen (SI-12). Ein Zeichen je Teil, weil nur das
    /// gemessen verlustfrei ist (Editor), bis WM03/WM04 ein Programm messen.
    /// </summary>
    private static TargetRule UnknownField(string processName) => new(
        processName, null, null, null, null, FieldKinds.Unknown, $"{processName} – unbekanntes Feld", LineBreakMode.Refuse, 1,
        IdentityMode.RuntimeId);

    /// <summary>Prozessname ohne Gross- und Kleinschreibung, die übrigen Muster genau; leere Muster passen immer.</summary>
    private static bool Matches(TargetRule rule, TargetFacts facts) =>
        string.Equals(rule.ProcessName, facts.ProcessName, StringComparison.OrdinalIgnoreCase) &&
        (rule.ControlType is null || rule.ControlType == facts.ControlType) &&
        (rule.ClassName is null || rule.ClassName == facts.ClassName) &&
        (rule.FrameworkId is null || rule.FrameworkId == facts.FrameworkId) &&
        (rule.IsMultiLine is null || rule.IsMultiLine == facts.IsMultiLine);
}
