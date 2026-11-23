using System.Globalization;
using System.Text;
using Freihand.Core.Platform;

namespace Freihand.Adapters.Dictation;

/// <summary>Ein selbst gemessenes Feld: genau so, wie es sich bei der Messung gemeldet hat.</summary>
/// <param name="Process">Prozessname ohne .exe.</param>
/// <param name="ControlType">UIA-Steuerelementtyp.</param>
/// <param name="ClassName">Klasse, auch leer.</param>
/// <param name="FrameworkId">Oberfläche (Win32, Chrome, WPF …), auch leer.</param>
/// <param name="MultiLine">Mehrzeilig laut Windows, null wenn unbekannt; gehört zum Wiedererkennen, auch als unbekannt (Codex zu PR #70).</param>
/// <param name="Characters">Zeichen je Schritt, 1 bis <see cref="FieldMeasurement.MaxCharacters"/>.</param>
/// <param name="Date">Tag der Messung.</param>
/// <param name="Manual">Von Hand festgelegt statt gemessen (Felder, die Windows nicht zurücklesen lässt).</param>
public sealed record MeasuredField(
    string Process, string ControlType, string ClassName, string FrameworkId, bool? MultiLine, int Characters, DateOnly Date,
    bool Manual = false)
{
    /// <summary>
    /// Zeile der Zieltabelle. Regel: Ein gemessenes Feld wird nur an genau denselben Merkmalen wiedererkannt (auch eine leere
    /// Klasse muss leer sein, und die Ein- oder Mehrzeiligkeit muss gleich sein, auch „unbekannt“; siehe
    /// <see cref="TargetRuleTable.Decide"/>), damit es nie ein anderes Feld überdeckt, etwa die Adressleiste oder ein einzeiliges
    /// Feld im Browser. Umbrüche bleiben gesperrt.
    /// </summary>
    public TargetRule ToRule() => new(
        Process, ControlType, ClassName, FrameworkId, MultiLine,
        MultiLine == true ? FieldKinds.MultiLine : FieldKinds.SingleLine,
        $"{Process} – {(Manual ? "von Hand festgelegt" : "gemessenes Feld")}", LineBreakMode.Refuse, Characters, IdentityMode.RuntimeId);

    /// <summary>Wie sich das Feld meldet, für Anzeige und Protokoll; nur Merkmale, nie Inhalt oder Fenstertitel.</summary>
    public string Identity => $"Programm {Process}, Steuerelement {ControlType}, Klasse {Shown(ClassName)}, Oberfläche {Shown(FrameworkId)}";

    private static string Shown(string value) => value.Length == 0 ? "(leer)" : value;
}

/// <summary>Ergebnis einer Messung; <see cref="Field"/> ist null, wenn nichts gespeichert werden soll.</summary>
/// <param name="Text">Ergebnis in einfachen Worten.</param>
/// <param name="ClearByHand">Der Prüftext steht womöglich noch im Feld.</param>
/// <param name="Candidate">Das sicher erkannte Feld, für einen Wert von Hand; sonst null.</param>
public sealed record MeasurementResult(MeasuredField? Field, string Text, bool ClearByHand, MeasuredField? Candidate = null);

/// <summary>
/// „Feld messen“ (Bedienregel vom 21.11.2026): wertet die Messung des Messwerkzeugs aus. Regel: Es gilt die
/// grösste Stückgrösse, die mindestens <see cref="Confirmations"/>-mal fehlerfrei durchging und nie scheiterte, höchstens
/// <see cref="MaxCharacters"/>. Ohne solche Grösse ist nichts gemessen: Es bleibt beim bisherigen Wert, ein Wert von Hand
/// ist möglich. Messregel (Codex zu PR #70, zweite Prüfung): Eine Messung wirkt nur auf Grund nachgewiesener Tatsachen am selben Element. Was nicht nachgewiesen ist (ein anderes Element, ein Vorinhalt, ein abgebrochener Durchgang, ein unbekanntes Merkmal), führt zum Abbruch ohne Wirkung: nichts schreiben, nichts löschen, nichts speichern, nichts auf andere Felder übertragen. Ein Durchgang zählt darum nur, wenn er zeichengetreu ankam,
/// lesbar war und geleert wurde; endet die Messung mit einem Abbruch, wird nichts gespeichert. Das Messwerkzeug grenzt von oben ein (50, dann 20, 10 …, dazwischen die Mitte) und
/// wiederholt nur die gefundene Grösse.
/// </summary>
public static class FieldMeasurement
{
    public const int MaxCharacters = 50;

    /// <summary>So oft muss eine Stückgrösse fehlerfrei durchgehen (wie im Messwerkzeug).</summary>
    public const int Confirmations = 3;

    /// <summary>Wahl für einen Wert von Hand: dieselben Stufen wie beim Messen.</summary>
    public static IReadOnlyList<int> Steps { get; } = [1, 2, 3, 5, 7, 10, 15, 20, 30, 50];

    /// <summary>Prozessname von Freihand; in die eigenen Fenster schreibt Freihand nie.</summary>
    public const string OwnProcess = "Freihand";

    /// <summary>
    /// Wertet die Zusammenfassung des Messwerkzeugs aus (Zeilen „schlüssel = wert“, kein JSON: JSON liest Freihand nur an
    /// geprüften Stellen, JsonInputUnicodeMatrixTests). Schlüssel: prozess, steuerelement, klasse, oberflaeche, mehrzeilig
    /// (ja/nein/-), entscheid, grund und je Durchgang „durchgang = Stückgrösse;zeichengetreu ja/nein/-;lesbar ja/nein;Abbruch oder -“.
    /// </summary>
    public static MeasurementResult Evaluate(string summary, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var passes = new List<(int Size, bool? Exact, bool Readable, string Abort)>();
        foreach (var line in summary.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var at = line.IndexOf(" = ", StringComparison.Ordinal);
            if (at <= 0)
            {
                continue;
            }

            var key = line[..at];
            var value = line[(at + 3)..];
            if (key != "durchgang")
            {
                values.TryAdd(key, value);
                continue;
            }

            var parts = value.Split(';', 4);
            if (parts.Length == 4 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > 0)
            {
                passes.Add((size, parts[1] switch { "ja" => true, "nein" => false, _ => null }, parts[2] == "ja", parts[3] == "-" ? string.Empty : parts[3]));
            }
        }

        if (values.GetValueOrDefault("fehler") == "Fokuswechsel")
        {
            return new(null, "Während Freihand das Feld las, wechselte der Fokus. Nichts geschrieben und nichts gespeichert. Bitte nochmals und nach dem Klick nichts anfassen.", false);
        }

        if (values.TryGetValue("fehler", out var failure))
        {
            return new(null, $"Messung gescheitert ({failure}). Bitte das Feld prüfen; steht noch Prüftext darin, von Hand löschen und nicht speichern.", true);
        }

        if (!values.TryGetValue("prozess", out var process) || process.Length == 0)
        {
            return new(null, "Kein Feld gefunden. Nach „Messen starten“ innert 10 Sekunden in ein leeres Textfeld klicken.", false);
        }

        if (string.Equals(process, OwnProcess, StringComparison.OrdinalIgnoreCase))
        {
            return new(null, "Das war ein Fenster von Freihand. Bitte in ein Feld eines anderen Programms klicken.", false);
        }

        if (values.GetValueOrDefault("entscheid") == "NotBindable")
        {
            return new(null, $"In dieses Feld schreibt Freihand nicht ({Reason(values.GetValueOrDefault("grund") ?? string.Empty)}). Nichts gemessen.", false);
        }

        if (values.ContainsKey("abweichung"))
        {
            return new(null,
                "Während der Messung war ein anderes Feld im Fokus. Nichts gespeichert. Bitte den Prüftext in den Feldern prüfen und von Hand löschen.",
                true);
        }

        var field = new MeasuredField(
            process, values.GetValueOrDefault("steuerelement") ?? string.Empty, values.GetValueOrDefault("klasse") ?? string.Empty,
            values.GetValueOrDefault("oberflaeche") ?? string.Empty,
            values.GetValueOrDefault("mehrzeilig") switch { "ja" => true, "nein" => false, _ => null }, 1, today);
        if (field.ControlType.Length == 0 || $"{field.Process}{field.ControlType}{field.ClassName}{field.FrameworkId}".Contains('|', StringComparison.Ordinal)
            || field.ClassName == "-" || field.FrameworkId == "-"
            || new[] { field.Process, field.ControlType, field.ClassName, field.FrameworkId }.Any(value => value != value.Trim() || value.Any(char.IsControl)))
        {
            return new(null, "Das Feld meldet sich so, dass Freihand es nicht wiedererkennen kann. Nichts gespeichert.", false);
        }

        // Ab hier ist das Feld sicher erkannt: ein Wert von Hand ist möglich (Bedienregel vom 21.11.2026)
        var candidate = field;

        if (values.GetValueOrDefault("modus") == "erkennen")
        {
            return new(null, "Feld erkannt. Lege unten einen Wert von Hand fest und prüfe ihn beim Diktieren.", false, candidate);
        }

        // Nicht lesbar: Das Werkzeug schreibt dann gar nicht (eigene Vorprüfung)
        if (values.GetValueOrDefault("lesbar") == "nein")
        {
            return new(null,
                "Windows lässt dieses Feld nicht zurücklesen. Freihand hat nichts geschrieben; es bleibt beim bisherigen Wert. Unten kannst du einen Wert von Hand festlegen und beim Diktieren prüfen.",
                false, candidate);
        }

        // Hinweistext (Bedienregel, 21.11.2026): mit einem Zeichen geprüft, bevor gemessen wird
        if (HintResult(values.GetValueOrDefault("hinweistext"), values.GetValueOrDefault("pruefzeichen"), candidate) is { } hint)
        {
            return hint;
        }

        var clearByHand = passes.Any(pass => pass.Abort.StartsWith("Feld nicht geleert", StringComparison.Ordinal)
            || pass.Abort.StartsWith(Deviating, StringComparison.Ordinal) || !pass.Readable);
        var clear = clearByHand ? " Der Prüftext steht vielleicht noch im Feld: bitte von Hand löschen und nicht speichern." : string.Empty;

        if (passes.FindIndex(pass => pass.Abort.Length > 0 && !pass.Abort.StartsWith("Feld nicht geleert", StringComparison.Ordinal)) is >= 0 and var abortedAt)
        {
            var aborted = passes[abortedAt];
            var text = aborted.Abort.StartsWith(Deviating, StringComparison.Ordinal)
                ? $"Mit {aborted.Size} Zeichen am Stück kam der Prüftext verändert an, und das Feld wurde nicht innert 90 Sekunden von Hand geleert. Messung abgebrochen."
                : !aborted.Abort.StartsWith("Feld nicht leer", StringComparison.Ordinal) ? $"Messung abgebrochen ({aborted.Abort})."
                : abortedAt == 0 && values.ContainsKey("hinweistext")
                ? "Der Inhalt des Felds änderte sich nach der Prüfung des Hinweistexts; Freihand hat nichts geschrieben. Bitte nochmals messen oder „Nur Feld erkennen“ und den Wert von Hand festlegen."
                : abortedAt == 0
                ? "Das Feld war nicht leer; Freihand hat nichts geschrieben. Auch Leerzeilen und Leerzeichen zählen als Inhalt. Bei manchen Feldern zählt auch ein grauer Hinweistext. Bitte in einem eindeutig leeren Testfeld messen oder „Nur Feld erkennen“ und den Wert von Hand festlegen."
                : $"Vor dem Durchgang mit {aborted.Size} Zeichen war das Feld nicht mehr leer; vermutlich steht noch Prüftext darin. Bitte von Hand löschen. Nichts gespeichert.";
            clearByHand |= abortedAt > 0;
            clear = clearByHand ? " Der Prüftext steht vielleicht noch im Feld: bitte von Hand löschen und nicht speichern." : string.Empty;
            return new(null, text + clear, clearByHand, candidate);
        }

        if (passes.Count == 0)
        {
            return new(null, "Es wurde nichts geschrieben. Nichts gespeichert." + clear, clearByHand, candidate);
        }

        if (passes.Any(pass => !pass.Readable))
        {
            return new(null,
                "Windows lässt dieses Feld nicht zurücklesen; Freihand kann es nicht prüfen und bleibt dort beim bisherigen Wert." + clear,
                clearByHand, candidate);
        }

        // Grösste Grösse, die nie scheiterte und mindestens dreimal zeichengetreu ankam
        var proven = passes.GroupBy(pass => pass.Size)
            .Where(group => group.All(pass => pass.Exact == true) && group.Count() >= Confirmations)
            .Select(group => group.Key)
            .DefaultIfEmpty(0)
            .Max();

        // Messregel, Stufe Auswerten: Liess sich das Feld nach einem Durchgang nicht leeren, ist dieser Durchgang nicht
        // gelungen und die Messung abgebrochen; nichts speichern, auch wenn der Text vorher dreimal genau ankam
        if (passes.LastOrDefault(pass => pass.Abort.StartsWith("Feld nicht geleert", StringComparison.Ordinal)) is { Abort.Length: > 0 } notCleared)
        {
            return new(null,
                $"Die Messung musste abbrechen: Nach dem Durchgang mit {notCleared.Size} Zeichen am Stück liess sich das Feld nicht sicher leeren. Bitte den Text von Hand löschen und nochmals messen. Nichts gespeichert, es bleibt beim bisherigen Wert.",
                true, candidate);
        }

        // Gespeichert wird nur eine dreimal bestätigte Grösse. Ohne sie ist nichts gemessen; es bleibt beim bisherigen Wert
        if (proven == 0)
        {
            var reason = passes.Any(pass => pass.Size == 1 && pass.Exact != true)
                ? "Schon Zeichen für Zeichen kam der Text verändert an: Dieses Programm verändert getippten Text selbst (z. B. Formatierung oder Autokorrektur)."
                : "Keine Stückgrösse kam dreimal fehlerfrei an.";
            return new(null, reason + " Nichts gespeichert, es bleibt beim bisherigen Wert. Unten kannst du einen Wert von Hand festlegen und beim Diktieren prüfen." + clear,
                clearByHand, candidate);
        }

        var characters = Math.Min(proven, MaxCharacters);
        var byHand = values.ContainsKey("von_hand_geleert")
            ? " Grössere Stücke kamen verändert an; sie zählen nicht."
            : string.Empty;
        return new(field with { Characters = characters },
            $"Dreimal fehlerfrei mit {proven} Zeichen am Stück. Freihand schreibt hier ab sofort {characters} Zeichen je Schritt." + byHand + clear,
            clearByHand, candidate);
    }

    /// <summary>
    /// Ergebnis der Prüfung mit einem Zeichen, ob der Vorinhalt ein grauer Hinweistext ist; null, wenn gemessen wurde (ja, leer
    /// oder ohne Prüfung). Regel: nur ein Hinweistext, der beim ersten Zeichen verschwindet, erlaubt die Messung.
    /// </summary>
    private static MeasurementResult? HintResult(string? hint, string? character, MeasuredField candidate)
    {
        var shown = character is { Length: 1 } ? $"„{character}“" : "ein einzelnes Zeichen";
        return hint switch
        {
            null or "ja" or "leer" => null,
            "belegt" => new(null,
                "Das Feld meldet bei Windows einen Inhalt. Freihand tippt nicht in einen bestehenden Wert, weil manche Felder ihn schon beim ersten Zeichen ersetzen (z. B. eine Tabellenzelle in Dynamics). Nichts geschrieben. Bitte in einem eindeutig leeren Testfeld messen oder unten einen Wert von Hand festlegen.",
                false, candidate),
            "ersetzt" => new(null,
                "Nach dem Entfernen des Prüfzeichens zeigte das Feld seinen früheren Inhalt nicht wieder. Entweder ersetzt dieses Feld seinen Inhalt beim Tippen (z. B. eine Tabellenzelle): dann mit Strg+Z oder Esc zurückholen und nicht speichern. Oder es meldet seinen Hinweistext danach nicht mehr. Nichts gemessen; unten kannst du einen Wert von Hand festlegen.",
                true, candidate),
            "echter_text" => new(null,
                $"Im Feld steht echter Text, kein grauer Hinweistext: Er blieb beim ersten getippten Zeichen stehen. Freihand hat zum Prüfen {shown} eingefügt und löscht in echtem Text nie selbst: bitte {shown} von Hand löschen. Sonst ist nichts geändert, nichts gemessen.",
                true, candidate),
            "nicht_angekommen" => new(null,
                $"Das Prüfzeichen {shown} kam innert drei Sekunden nicht an. Bitte prüfen, ob es doch im Feld steht; Strg+Z macht es rückgängig. Nichts gemessen; unten kannst du einen Wert von Hand festlegen.",
                true, candidate),
            "anderes_feld" => new(null,
                $"Während der Prüfung des Hinweistexts war ein anderes Feld im Fokus. Nichts gemessen. Bitte die Felder prüfen; {shown} kann zu viel darin stehen.",
                true),
            "nicht_bindbar" => new(null, "In dieses Feld kann Freihand nicht schreiben. Nichts gemessen.", false, candidate),
            "vorher_abgebrochen" => new(null,
                "Die Prüfung des Hinweistexts brach ab, bevor etwas getippt wurde (anderes Feld im Fokus, nicht lesbar oder ohne feste Kennung). Nichts geschrieben, nichts gespeichert.",
                false, candidate),
            _ => new(null,
                $"Das Feld reagierte auf das Prüfzeichen {shown} unerwartet. Nichts gemessen. Bitte den Text im Feld prüfen; Strg+Z macht die letzten Schritte rückgängig.",
                true, candidate),
        };
    }

    /// <summary>Abbruch des Messwerkzeugs, wenn der Prüftext abwich und stehen blieb (Schreiben.Abweichend).</summary>
    private const string Deviating = "Prüftext weicht ab";

    private static string Reason(string reason) => reason switch
    {
        nameof(BindRefusal.PasswordField) => "Passwortfeld",
        nameof(BindRefusal.Elevated) => "Programm mit Administratorrechten",
        nameof(BindRefusal.OwnProcess) => "eigenes Fenster",
        _ => reason,
    };
}

/// <summary>
/// Gemessene Felder in <c>ziele-gemessen.txt</c> im Datenordner: eine Zeile je Feld, lesbar und von Hand löschbar. Freihand
/// schreibt nur auf den gelesenen Stand (<see cref="SafeTextFile"/>); eine neue Messung desselben Felds ersetzt seine Zeile.
/// </summary>
public sealed class MeasuredFieldStore(string path)
{
    public const string FileName = "ziele-gemessen.txt";

    public const string Header =
        "# Freihand – selbst gemessene Felder („Feld messen“ im Menü). Eine Zeile je Feld; Zeile löschen = wieder der bisherige Wert.\n" +
        "# Programm | Steuerelement | Klasse | Oberfläche | mehrzeilig | Zeichen je Schritt | Datum | gemessen oder von Hand\n";

    public string Path { get; } = path;

    /// <summary>
    /// Alle Felder der Liste. Fehlt die Datei, gibt es keine. Regel (Codex zu PR #70): Eine ungültige Datenzeile macht die
    /// ganze Liste ungültig, damit ein Tippfehler beim Bearbeiten (etwa 500 statt 50) keine geltende Messung stillschweigend
    /// entfernt; der Aufrufer behält dann den zuletzt gelesenen Stand. Leer- und Kommentarzeilen (#) zählen nicht.
    /// </summary>
    /// <exception cref="MeasuredListInvalidException">Eine Datenzeile ist ungültig.</exception>
    /// <exception cref="IOException">Datei nicht lesbar; der Aufrufer behält dann den bisherigen Stand (eigene Vorprüfung).</exception>
    /// <exception cref="UnauthorizedAccessException">Datei gesperrt.</exception>
    public IReadOnlyList<MeasuredField> Load() => File.Exists(Path) ? Fields(SafeTextFile.Read(Path, out _)) : [];

    /// <summary>Zeilen der Datei, getrennt an CRLF, CR oder LF (Codex zu PR #70: von Hand gespeicherte Dateien).</summary>
    private static string[] Lines(string text) => text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

    private static List<MeasuredField> Fields(string text)
    {
        var fields = new List<MeasuredField>();
        var lines = Lines(text);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            fields.Add(Parse(line) ?? throw new MeasuredListInvalidException(index + 1));
        }

        return fields;
    }

    /// <summary>Legt die Datei mit ihrem Kopf an, falls sie fehlt; eine inzwischen vorhandene bleibt, wie sie ist.</summary>
    public void EnsureExists()
    {
        if (File.Exists(Path))
        {
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        SafeTextFile.ReplaceIfUnchanged(Path, null, Header, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Speichert das Feld; eine Zeile mit denselben Merkmalen wird ersetzt.</summary>
    /// <exception cref="MeasuredListInvalidException">Eine Datenzeile der Liste ist ungültig; nichts geschrieben.</exception>
    /// <exception cref="IOException">Datei nicht schreibbar oder inzwischen geändert; nichts geschrieben.</exception>
    /// <exception cref="UnauthorizedAccessException">Datei gesperrt.</exception>
    public void Save(MeasuredField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        string? read = File.Exists(Path) ? SafeTextFile.Read(Path, out _) : null;

        // Eine ungültige Liste würde nach dem Speichern nicht übernommen; erst korrigieren lassen
        _ = Fields(read ?? string.Empty);
        var lines = Lines(read ?? Header).Where(line => line.Length > 0).ToList();
        var replaced = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (Parse(lines[index]) is { } existing && Same(existing, field))
            {
                lines[index] = Line(field);
                replaced = true;
            }
        }

        if (!replaced)
        {
            lines.Add(Line(field));
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        if (!SafeTextFile.ReplaceIfUnchanged(Path, read, string.Join('\n', lines) + '\n', new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            throw new IOException("Die Liste der gemessenen Felder wurde inzwischen geändert; nichts geschrieben.");
        }
    }

    public static string Line(MeasuredField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return string.Join(" | ", field.Process, field.ControlType, Dash(field.ClassName), Dash(field.FrameworkId),
            field.MultiLine switch { true => "ja", false => "nein", null => "-" },
            field.Characters.ToString(CultureInfo.InvariantCulture), field.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            field.Manual ? "von Hand" : "gemessen");
    }

    public static MeasuredField? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.TrimStart().StartsWith('#'))
        {
            return null;
        }

        var parts = line.Split('|').Select(part => part.Trim()).ToArray();
        if (parts.Length is not (7 or 8) || parts[0].Length == 0 || parts[1].Length == 0 ||
            !int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var characters) ||
            characters is < 1 or > FieldMeasurement.MaxCharacters ||
            !DateOnly.TryParseExact(parts[6], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            parts[4] is not ("ja" or "nein" or "-") || (parts.Length == 8 && parts[7] is not ("gemessen" or "von Hand")))
        {
            // Eigene Vorprüfung: Ein Tippfehler bei „mehrzeilig“ machte sonst aus der Zeile eine für ein- und mehrzeilige Felder
            return null;
        }

        bool? multi = parts[4] switch { "ja" => true, "nein" => false, _ => null };
        return new MeasuredField(parts[0], parts[1], Undash(parts[2]), Undash(parts[3]), multi, characters, date,
            parts.Length == 8 && parts[7] == "von Hand");
    }

    /// <summary>
    /// Hinweis, wenn die Liste nicht gilt. Regel (eigene Vorprüfung zu Codex-Befund 5): nie still. Beim Start gibt es noch
    /// keinen gelesenen Stand, dann gelten nur die festen Werte; beim Neuladen und Speichern die zuletzt gelesenen.
    /// </summary>
    public static string Problem(Exception exception, bool atStart)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var kept = atStart ? "bis dahin gelten nur die festen Werte" : "bis dahin gelten die zuletzt gelesenen Werte";
        return exception is MeasuredListInvalidException invalid
            ? $"In der Liste der gemessenen Felder ist Zeile {invalid.LineNumber} ungültig. Bitte über „Feld messen …“ und „Gemessene Felder öffnen“ korrigieren oder die Zeile löschen; {kept}."
            : $"Die Liste der gemessenen Felder lässt sich nicht lesen oder schreiben; {kept}.";
    }

    private static bool Same(MeasuredField a, MeasuredField b) =>
        string.Equals(a.Process, b.Process, StringComparison.OrdinalIgnoreCase) && a.ControlType == b.ControlType &&
        a.ClassName == b.ClassName && a.FrameworkId == b.FrameworkId && a.MultiLine == b.MultiLine;

    private static string Dash(string value) => value.Length == 0 ? "-" : value;

    private static string Undash(string value) => value == "-" ? string.Empty : value;
}

/// <summary>Eine Datenzeile der Liste der gemessenen Felder ist ungültig; die Liste gilt dann nicht (Codex zu PR #70).</summary>
/// <param name="lineNumber">Zeile in der Datei, ab 1.</param>
public sealed class MeasuredListInvalidException(int lineNumber)
    : IOException($"Zeile {lineNumber} der Liste der gemessenen Felder ist ungültig.")
{
    public int LineNumber { get; } = lineNumber;
}
