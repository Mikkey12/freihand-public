using System.Globalization;
using System.Text;
using Freihand.Core.Platform;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Einstellungen des Prototyps (D6 a): eine lokale Datei mit Modell, VAD, Gerät, Erkennung und Tasten, ohne Texte. Die
/// Tasten sind je Person änderbar (D2); eine ungültige Belegung meldet die App mit Grund und läuft weiter. Die Datei hat
/// Zeilen „schlüssel = wert“ und Kommentare mit „#“; sie ist bewusst kein JSON, damit kein weiterer JSON-Eingang neben
/// der Unicode-Matrix entsteht (JsonInputUnicodeMatrixTests).
/// </summary>
public sealed record PrototypeSettings
{
    public string Model { get; init; } = string.Empty;

    public string Vad { get; init; } = string.Empty;

    /// <summary>Kennung des Aufnahmegeräts; null heisst Standardgerät von Windows.</summary>
    public string? Device { get; init; }

    public string Language { get; init; } = "de-CH";

    /// <summary>Laufzeiten in dieser Reihenfolge: cuda, vulkan, cpu.</summary>
    public IReadOnlyList<string> Runtimes { get; init; } = ["cuda", "cpu"];

    public int Threads { get; init; } = 4;

    public int AudioContext { get; init; } = 1500;

    /// <summary>Tastenkombinationen als Text, z. B. „Strg+Alt+Y“; Standard nach P7 Kapitel 10.2.</summary>
    public HotkeyTexts Hotkeys { get; init; } = new();

    /// <summary>Leiser Ton beim Start und Ende der Aufnahme (P9 Diktatfenster, Kapitel 3.5); Standard aus.</summary>
    public bool Sound { get; init; }

    /// <summary>Verlustschutz ein (ER-06); aus heisst: nichts wird gesichert, Vorhandenes beim Start gelöscht.</summary>
    public bool Recovery { get; init; } = true;

    /// <summary>Haltefrist nach der Übernahme in Minuten, 0 bis 1440 (ER-06); ausserhalb gilt der Standard 15.</summary>
    public int HoldMinutes { get; init; } = 15;

    /// <summary>Modell fürs Korrigieren und Glätten (Beta B8, Entscheid B-E8: Claude Haiku 5.5).</summary>
    public string CorrectionModel { get; init; } = "claude-haiku-5-5";

    /// <summary>Standardwerte mit den Modellen unter <paramref name="folder"/>\Modelle.</summary>
    public static PrototypeSettings Defaults(string folder) => new()
    {
        Model = Path.Combine(folder, "Modelle", "ggml-large-v3-turbo.bin"),
        Vad = Path.Combine(folder, "Modelle", "ggml-silero-v5.1.2.bin"),
    };

    /// <summary>
    /// Standardwerte der Geschäftsausgabe für ein Notebook ohne NVIDIA-Grafik, wie das
    /// Referenznotebook: Modell <c>small</c> und VAD aus dem Unterordner „Modelle“ (das Modellpaket bringt sie mit, nichts
    /// wird zur Laufzeit geladen, WE-14), Laufzeiten Vulkan vor CPU, Audiokontext 850 (Entscheid vom 05.11.2026 für
    /// Notebooks, RM-Abnahme Referenznotebook). Startwerte, bis die Messung auf dem Zielgerät sie bestätigt (QM-08).
    /// </summary>
    /// <param name="folder">Datenordner (Einstellungen, Sicherungen, Protokoll).</param>
    /// <param name="modelParent">Ordner mit dem Unterordner „Modelle“; seit den zwei Paketen (22.11.2026) der Datenordner.</param>
    public static PrototypeSettings BusinessDefaults(string folder, string modelParent) => Defaults(folder) with
    {
        Model = Path.Combine(modelParent, "Modelle", "ggml-small.bin"),
        Vad = Path.Combine(modelParent, "Modelle", "ggml-silero-v5.1.2.bin"),
        Runtimes = ["vulkan", "cpu"],
        AudioContext = 850,
    };

    /// <summary>
    /// Regel der Geschäftsausgabe mit zwei Paketen (22.11.2026): Modell und VAD liegen immer unter
    /// <paramref name="modelParent"/>\Modelle, getrennt vom Programm, damit ein Update nur das Programm ersetzt. Aus der
    /// Einstellung zählt nur der Dateiname; so gilt auch eine Einstellung aus einem früheren Paket mit den Modellen im
    /// Programmordner, und der Ordner lässt sich nicht verstellen.
    /// </summary>
    public PrototypeSettings WithModelsIn(string modelParent)
    {
        ArgumentNullException.ThrowIfNull(modelParent);
        return this with
        {
            Model = Path.Combine(modelParent, "Modelle", Path.GetFileName(Model)),
            Vad = Path.Combine(modelParent, "Modelle", Path.GetFileName(Vad)),
        };
    }

    /// <summary>
    /// Liest die Datei; fehlt sie, wird sie mit Standardwerten angelegt. Unbekannte Schlüssel bleiben ohne Wirkung. Fehlt
    /// ein Schlüssel, den Freihand kennt (z. B. nach einem Update), wird er mit seinem Wert und Kommentar am Ende ergänzt:
    /// So steht jede Einstellung in der Datei und ist änderbar; Vorhandenes bleibt Zeichen für Zeichen, wie es war.
    /// </summary>
    public static PrototypeSettings LoadOrCreate(string path, PrototypeSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(defaults);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (ReplaceIfUnchanged(path, null, Write(defaults), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                return defaults with { AudioContext = ModelMenu.WindowFor(defaults.Model) ?? defaults.AudioContext };
            }

            // Inzwischen angelegt (z. B. von Hand): diese Datei gilt
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            var equals = text.IndexOf('=', StringComparison.Ordinal);
            if (text.Length == 0 || text[0] == '#' || equals <= 0)
            {
                continue;
            }

            values[text[..equals].Trim()] = text[(equals + 1)..].Trim();
        }

        string Text(string key, string fallback) => values.TryGetValue(key, out var value) ? value : fallback;
        int Number(string key, int fallback) =>
            values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number : fallback;

        bool Switch(string key, bool fallback) =>
            values.TryGetValue(key, out var value) ? value.ToUpperInvariant() switch
            {
                "EIN" or "JA" or "AN" => true,
                "AUS" or "NEIN" => false,
                _ => fallback,
            } : fallback;

        var device = Text("geraet", defaults.Device ?? string.Empty);
        var hold = Number("haltefrist", defaults.HoldMinutes);
        var modelPath = Text("modell", defaults.Model);
        var loaded = new PrototypeSettings
        {
            Model = modelPath,
            Vad = Text("vad", defaults.Vad),
            Device = device.Length == 0 ? null : device,
            Language = Text("sprache", defaults.Language),
            Runtimes = values.TryGetValue("laufzeiten", out var runtimes)
                ? [.. runtimes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]
                : defaults.Runtimes,
            Threads = Number("threads", defaults.Threads),
            // Regel (Codex zu PR #69): Für Modelle der Tabelle gehört das Fenster zum Modell, nie zur Datei
            AudioContext = ModelMenu.WindowFor(modelPath) ?? Number("audiokontext", defaults.AudioContext),
            Hotkeys = new HotkeyTexts
            {
                Record = Text("aufnahme", defaults.Hotkeys.Record),
                Command = Text("befehl", defaults.Hotkeys.Command),
                Cancel = Text("abbrechen", defaults.Hotkeys.Cancel),
                DictationWindow = Text("diktatfenster", defaults.Hotkeys.DictationWindow),
                Correct = Text("korrigieren", defaults.Hotkeys.Correct),
                Smooth = Text("glaetten", defaults.Hotkeys.Smooth),
                Mark = Text("problem", defaults.Hotkeys.Mark),
            },
            Sound = Switch("ton", defaults.Sound),
            Recovery = Switch("sicherung", defaults.Recovery),
            HoldMinutes = hold is >= 0 and <= 1440 ? hold : defaults.HoldMinutes,
            CorrectionModel = Text("korrektur_modell", defaults.CorrectionModel) is { Length: > 0 } model ? model : defaults.CorrectionModel,
        };
        AppendMissing(path, values.Keys, loaded);
        return loaded;
    }

    /// <summary>
    /// Hängt fehlende bekannte Schlüssel samt ihrem Kommentar an, in der Kodierung und mit den Zeilenenden der Datei (Codex
    /// zu PR #55: eine UTF-16-Datei bleibt UTF-16); ein Schreibfehler ändert nichts am Gelesenen.
    /// </summary>
    private static void AppendMissing(string path, IEnumerable<string> present, PrototypeSettings loaded)
    {
        string existing;
        Encoding encoding;
        try
        {
            existing = ReadText(path, out encoding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Fehlende Schlüssel aus genau dem Stand, der ersetzt wird (Codex zu PR #69): ein inzwischen ergänzter Schlüssel
        // kommt nie ein zweites Mal mit dem Standardwert dazu
        var known = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        known.UnionWith(Keys(existing));
        var missing = new List<string>();
        var comments = new List<string>();
        foreach (var line in Write(loaded).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#'))
            {
                comments.Add(line);
                continue;
            }

            if (!known.Contains(line[..line.IndexOf('=', StringComparison.Ordinal)].Trim()))
            {
                missing.AddRange(comments);
                missing.Add(line);
            }

            comments.Clear();
        }

        if (missing.Count == 0)
        {
            return;
        }

        try
        {
            // Ganze Fassung neu schreiben und nur diesen Stand ersetzen; sonst beim nächsten Start erneut (Codex zu PR #69)
            var newline = NewlineOf(existing);
            var separator = existing.Length == 0 || existing.EndsWith('\n') || existing.EndsWith('\r') ? string.Empty : newline;
            ReplaceIfUnchanged(path, existing, existing + separator + string.Join(newline, missing) + newline, encoding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Die Werte gelten trotzdem; beim nächsten Start wird es erneut versucht
        }
    }

    /// <summary>
    /// Setzt genau einen Schlüssel, z. B. nach „Ton“ oder „Mikrofon“ im Menü (Beta B3): Ersetzt wird die Zeile, die beim
    /// Laden gilt, also die letzte des Schlüssels (Codex zu PR #56); fehlt er, wird sie angehängt. Alles andere bleibt
    /// Zeichen für Zeichen, in Kodierung und Zeilenenden der Datei (CRLF, LF oder CR wie beim Laden). So gehen Notizen und
    /// Änderungen von Hand nie verloren. Die Datei muss bestehen (<see cref="LoadOrCreate"/> legt sie an).
    /// </summary>
    /// <exception cref="IOException">Datei nicht lesbar oder nicht schreibbar.</exception>
    /// <exception cref="UnauthorizedAccessException">Datei gesperrt oder schreibgeschützt; sie bleibt dann unverändert.</exception>
    public static void SetValue(string path, string key, string value) => SetValues(path, (key, value));

    /// <summary>
    /// Wie <see cref="SetValue"/> für mehrere Schlüssel in einem Schreibvorgang, z. B. Modell und Rechenfenster aus dem Menü
    /// „Modell“: Entweder gelten beide oder keiner.
    /// </summary>
    /// <exception cref="IOException">Datei nicht lesbar oder nicht schreibbar.</exception>
    /// <exception cref="UnauthorizedAccessException">Datei gesperrt oder schreibgeschützt; sie bleibt dann unverändert.</exception>
    public static void SetValues(string path, params (string Key, string Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(values);
        foreach (var (key, value) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
        }

        var read = ReadText(path, out var encoding);
        var text = read;
        foreach (var (key, value) in values)
        {
            text = WithValue(text, key, value);
        }

        if (!ReplaceIfUnchanged(path, read, text, encoding))
        {
            throw new IOException("Die Einstellungsdatei wurde inzwischen geändert; nichts geschrieben.");
        }
    }

    /// <summary>Text und Kodierung der Datei, wie beim Laden (UTF-8 ohne Kennung, sonst nach BOM).</summary>
    private static string ReadText(string path, out Encoding encoding) => SafeTextFile.Read(path, out encoding);

    /// <summary>Schlüssel der Zeilen „schlüssel = wert“, wie beim Laden.</summary>
    private static IEnumerable<string> Keys(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (trimmed.Length > 0 && trimmed[0] != '#' && equals > 0)
            {
                yield return trimmed[..equals].Trim();
            }
        }
    }

    /// <summary>Ersetzt den Stand, der jetzt in der Datei steht (für <see cref="Save"/>); siehe <see cref="ReplaceIfUnchanged"/>.</summary>
    /// <returns>Falsch, wenn die Datei sich während des Schreibens geändert hat; sie bleibt dann, wie sie ist.</returns>
    private static bool Replace(string path, string text, Encoding encoding) =>
        ReplaceIfUnchanged(path, File.Exists(path) ? ReadText(path, out _) : null, text, encoding);

    private static bool ReplaceIfUnchanged(string path, string? expected, string text, Encoding encoding) =>
        SafeTextFile.ReplaceIfUnchanged(path, expected, text, encoding);

    private static string WithValue(string text, string key, string value)
    {
        var line = key + " = " + value.Replace('\r', ' ').Replace('\n', ' ');

        // Zeilenanfang nach jedem Zeilenende (CR, LF, CRLF), wie File.ReadAllLines beim Laden
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?<=\A|[\r\n])[ \t]*" + System.Text.RegularExpressions.Regex.Escape(key) + @"[ \t]*=[^\r\n]*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var matches = pattern.Matches(text);
        if (matches.Count > 0)
        {
            var last = matches[^1];
            text = string.Concat(text.AsSpan(0, last.Index), line, text.AsSpan(last.Index + last.Length));
        }
        else
        {
            var newline = NewlineOf(text);
            text += (text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r') ? string.Empty : newline) + line + newline;
        }

        return text;
    }

    /// <summary>Zeilenende der Datei: CRLF, sonst CR allein, sonst LF (auch für eine leere Datei).</summary>
    private static string NewlineOf(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r') ? "\r" : "\n";

    /// <summary>Schreibt die Datei ganz neu mit Kommentaren; ohne Texte. Für Änderungen aus dem Menü gilt <see cref="SetValue"/>.</summary>
    public static void Save(string path, PrototypeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Replace(path, Write(settings), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            throw new IOException("Die Einstellungsdatei wurde inzwischen geändert; nichts geschrieben.");
        }
    }

    /// <summary>Inhalt der Datei mit Kommentaren; ohne Texte.</summary>
    public static string Write(PrototypeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Join('\n',
            "# Freihand – Einstellungen des Prototyps (keine Texte). Zeilen „schlüssel = wert“.",
            "# Modell und VAD; in der Geschäftsausgabe zählt nur der Dateiname, die Modelle liegen im Datenordner unter Modelle",
            "modell = " + settings.Model,
            "vad = " + settings.Vad,
            "# leer: Standardgerät von Windows",
            "geraet = " + (settings.Device ?? string.Empty),
            "sprache = " + settings.Language,
            "laufzeiten = " + string.Join(", ", settings.Runtimes),
            "threads = " + settings.Threads.ToString(CultureInfo.InvariantCulture),
            "# Rechenfenster; für small (850) und die grossen Modelle (1500) fest, gilt nur für andere Modelle",
            "audiokontext = " + settings.AudioContext.ToString(CultureInfo.InvariantCulture),
            "# Tasten, z. B. Strg+Alt+Y, Umschalt+F8, Pause",
            "aufnahme = " + settings.Hotkeys.Record,
            "befehl = " + settings.Hotkeys.Command,
            "abbrechen = " + settings.Hotkeys.Cancel,
            "diktatfenster = " + settings.Hotkeys.DictationWindow,
            "# Markierten Text in Word, Outlook usw. korrigieren oder glätten lassen (online, mit Vorschau)",
            "korrigieren = " + settings.Hotkeys.Correct,
            "glaetten = " + settings.Hotkeys.Smooth,
            "# Problem merken: Marke ins Protokoll, ohne Text",
            "problem = " + settings.Hotkeys.Mark,
            "# Ton beim Start und Ende der Aufnahme: ein oder aus",
            "ton = " + (settings.Sound ? "ein" : "aus"),
            "# Verlustschutz: ein oder aus; Haltefrist nach der Übernahme in Minuten (0 bis 1440)",
            "sicherung = " + (settings.Recovery ? "ein" : "aus"),
            "haltefrist = " + settings.HoldMinutes.ToString(CultureInfo.InvariantCulture),
            "# Modell fürs Korrigieren und Glätten im Diktatfenster (online, nur auf Knopfdruck)",
            "korrektur_modell = " + settings.CorrectionModel) + '\n';
    }

    /// <summary>Belegung für die Steuerung; nicht lesbare Einträge kommen in <paramref name="invalid"/> statt zu werfen.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyGesture> Gestures(out IReadOnlyList<HotkeyAction> invalid) =>
        Gestures(out invalid, out _);

    /// <summary>
    /// Belegung für die Steuerung (D10): Nicht lesbare Einträge kommen in <paramref name="invalid"/>. Ist eine Kombination
    /// doppelt belegt, behält sie die erste Aktion in der Reihenfolge Aufnahme, Befehl, Abbrechen, Diktatfenster,
    /// Korrigieren, Glätten, Problem merken; jede
    /// weitere kommt mit ihrer Vorgängerin in <paramref name="duplicates"/> und wird nicht angemeldet, nie still ersetzt.
    /// </summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyGesture> Gestures(
        out IReadOnlyList<HotkeyAction> invalid, out IReadOnlyList<HotkeyDuplicate> duplicates) =>
        Gestures([], out invalid, out duplicates);

    /// <summary>
    /// Wie <see cref="Gestures(out IReadOnlyList{HotkeyAction}, out IReadOnlyList{HotkeyDuplicate})"/>, aber Aktionen aus
    /// <paramref name="unavailable"/> nehmen gar nicht teil: nicht gelesen, nicht gemeldet und ohne Einfluss auf die Prüfung
    /// auf doppelte Belegung (Codex-Prüfung PR #57: Korrigieren verdrängte sonst in der Geschäftsausgabe die Problemtaste).
    /// </summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyGesture> Gestures(
        IReadOnlyCollection<HotkeyAction> unavailable, out IReadOnlyList<HotkeyAction> invalid,
        out IReadOnlyList<HotkeyDuplicate> duplicates)
    {
        ArgumentNullException.ThrowIfNull(unavailable);
        var gestures = new Dictionary<HotkeyAction, HotkeyGesture>();
        var failed = new List<HotkeyAction>();
        var doubled = new List<HotkeyDuplicate>();
        foreach (var (action, text) in new[]
                 {
                     (HotkeyAction.Record, Hotkeys.Record), (HotkeyAction.Command, Hotkeys.Command), (HotkeyAction.Cancel, Hotkeys.Cancel),
                     (HotkeyAction.DictationWindow, Hotkeys.DictationWindow), (HotkeyAction.Correct, Hotkeys.Correct),
                     (HotkeyAction.Smooth, Hotkeys.Smooth), (HotkeyAction.Mark, Hotkeys.Mark),
                 })
        {
            if (unavailable.Contains(action))
            {
                continue;
            }

            if (ParseGesture(text) is not { } gesture)
            {
                failed.Add(action);
            }
            else if (gestures.FirstOrDefault(entry => entry.Value == gesture) is { Value: not null } first)
            {
                doubled.Add(new HotkeyDuplicate(action, first.Key));
            }
            else
            {
                gestures[action] = gesture;
            }
        }

        invalid = failed;
        duplicates = doubled;
        return gestures;
    }

    /// <summary>
    /// „Strg+Alt+Y“, „Umschalt+F8“, „Pause“: Modifikatoren Strg/Ctrl, Alt, Umschalt/Shift, Win; Taste A–Z, 0–9, F1–F24,
    /// Pause, Leertaste. Sonst null. Ob die Kombination zulässig ist, prüft danach <see cref="HotkeyRules"/>.
    /// </summary>
    public static HotkeyGesture? ParseGesture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "STRG" or "CTRL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "UMSCHALT" or "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => (HotkeyModifiers?)null,
            };
            if (modifier is not { } value || modifiers.HasFlag(value))
            {
                return null;
            }

            modifiers |= value;
        }

        return Key(parts[^1]) is { } key ? new HotkeyGesture(modifiers, key) : null;
    }

    private static int? Key(string key)
    {
        var upper = key.ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            return upper[0];
        }

        if (upper.Length is 2 or 3 && upper[0] == 'F' &&
            int.TryParse(upper[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 24)
        {
            return 0x70 + number - 1;
        }

        return upper switch
        {
            "PAUSE" => 0x13,
            "LEERTASTE" or "SPACE" => 0x20,
            _ => null,
        };
    }
}

/// <summary>Tasten als Text in der Einstellungsdatei.</summary>
public sealed record HotkeyTexts
{
    public string Record { get; init; } = "Strg+Alt+Y";

    public string Command { get; init; } = "Strg+Alt+X";

    public string Cancel { get; init; } = "Strg+Alt+Q";

    /// <summary>Diktatfenster öffnen und schliessen (P9 Diktatfenster, Kapitel 3.5).</summary>
    public string DictationWindow { get; init; } = "Strg+Alt+D";

    /// <summary>Markierten Text korrigieren lassen.</summary>
    /// <remarks>Mit der linken Hand allein greifbar (Bedienregel vom 11.11.2026): F wie „Fehler“.</remarks>
    public string Correct { get; init; } = "Strg+Alt+F";

    /// <summary>Markierten Text glätten lassen (B9).</summary>
    public string Smooth { get; init; } = "Strg+Alt+G";

    /// <summary>
    /// Tastenkürzel für eine nummerierte Problemmarke im Tagesprotokoll.
    /// </summary>
    public string Mark { get; init; } = "Strg+Alt+B";
}

/// <summary>Eine Kombination ist schon für <paramref name="SameAs"/> belegt; <paramref name="Action"/> bleibt ohne Taste (D10).</summary>
public sealed record HotkeyDuplicate(HotkeyAction Action, HotkeyAction SameAs);
