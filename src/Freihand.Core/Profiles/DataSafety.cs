using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace Freihand.Core.Profiles;

/// <summary>
/// Gemeinsame Prüfungen für Profile und Befehle als Daten (OF-01): Grösse und Aufbau des Dokuments,
/// Version, freier Text ohne Geheimnisse und ohne ausführbare Inhalte.
/// </summary>
internal static partial class DataSafety
{
    /// <summary>Obergrenze für ein Dokument; Profile und Befehle sind klein.</summary>
    public const int MaxDocumentLength = 256 * 1024;

    /// <summary>Tiefe genügt für das Schema; tiefere Verschachtelung wird abgewiesen.</summary>
    public const int MaxDepth = 8;

    private const string IdPatternBody = "^[a-z0-9]+(-[a-z0-9]+)*";

    /// <summary>
    /// Bezeichner: Kleinbuchstaben, Ziffern und Bindestriche, wie im Schema. Diese Fassung steht im JSON-Schema
    /// (<c>$</c> nach ECMA 262); der Code prüft mit <see cref="IdPatternStrict"/>.
    /// </summary>
    public const string IdPattern = IdPatternBody + "$";

    /// <summary>
    /// Wie <see cref="IdPattern"/>, aber mit <c>\z</c> als Endanker: In .NET passt <c>$</c> auch vor einem
    /// Zeilenvorschub am Schluss, das Schema weist ihn ab, und Import und Schema müssen gleich entscheiden.
    /// </summary>
    public const string IdPatternStrict = IdPatternBody + @"\z";

    public const int MaxIdLength = 64;

    /// <summary>Strenges JSON: keine Kommentare, keine nachgestellten Kommas, keine doppelten Felder.</summary>
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxDepth,
        AllowDuplicateProperties = false,
    };

    /// <summary>
    /// Liest den Rahmen eines Dokuments: Objekt, Art und Version. Die Version wird vor allem anderen
    /// geprüft, damit ein neueres Dokument eine klare Meldung erhält statt „unbekanntes Feld“ (OF-01).
    /// Gelesen werden alle Versionen von <paramref name="oldestVersion"/> bis <paramref name="newestVersion"/>;
    /// die gelesene Version kommt in <paramref name="version"/> zurück, damit die Prüfung der Felder danach
    /// wählen kann. Die Meldungen nennen nur Zahlen.
    /// </summary>
    public static JsonDocument? ParseEnvelope(
        string json, string expectedKind, int oldestVersion, int newestVersion, ImportErrors errors, out int version)
    {
        version = 0;
        if (json.Length > MaxDocumentLength)
        {
            errors.Add("$", $"Das Dokument ist grösser als {MaxDocumentLength / 1024} KB.");
            return null;
        }

        // Ein einzelnes Ersatzzeichen (halbes Zeichenpaar) ist kein gültiges Unicode, ob roh oder als Escape
        // (Backslash, u, vier Hexziffern), in Werten wie in Schlüsseln und Rahmenfeldern. System.Text.Json nimmt
        // den Escape an und wirft erst beim Lesen des Texts; darum prüft der Import den ganzen Text zuerst,
        // einheitlich am Pfad $.
        if (JsonUnicode.HasLoneSurrogate(json))
        {
            errors.Add("$", "Das Dokument enthält ein ungültiges Unicode-Zeichen (einzelnes Ersatzzeichen).");
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            errors.Add(PathOf(exception), "Kein gültiges JSON: " + exception.Message);
            return null;
        }
        catch (ArgumentException)
        {
            // Ein Text mit einem einzelnen Ersatzzeichen (halbes Zeichenpaar) lässt sich nicht als UTF-8 lesen.
            errors.Add("$", "Das Dokument enthält ein ungültiges Unicode-Zeichen (einzelnes Ersatzzeichen).");
            return null;
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("$", "Das Dokument muss ein JSON-Objekt sein.");
            document.Dispose();
            return null;
        }

        if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
            errors.Add("$.kind", $"Fehlt; erwartet wird „{expectedKind}“.");
        }
        else if (kind.GetString() != expectedKind)
        {
            errors.Add("$.kind", $"Passt nicht; erwartet wird „{expectedKind}“.");
        }

        if (!root.TryGetProperty("schemaVersion", out var versionElement))
        {
            errors.Add("$.schemaVersion", "Fehlt.");
        }
        else if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var number))
        {
            errors.Add("$.schemaVersion", "Muss eine ganze Zahl sein.");
        }
        else if (number > newestVersion)
        {
            errors.Add("$.schemaVersion", $"Version {number} ist neuer als die unterstützte Version {newestVersion}.");
        }
        else if (number < oldestVersion)
        {
            errors.Add("$.schemaVersion", $"Version {number} wird nicht unterstützt.");
        }
        else
        {
            version = number;
        }

        if (errors.Any)
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    /// <summary>
    /// Weist optionale Felder eines Objekts ab, die ausdrücklich mit <c>null</c> vorhanden sind (OF-01): Weglassen
    /// ist erlaubt, <c>null</c> nicht, wie im JSON-Schema. Die Meldung nennt nur den festen Feldnamen.
    /// </summary>
    public static void RejectExplicitNulls(JsonElement element, string path, ImportErrors errors, params string[] optionalFields)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var name in optionalFields)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null)
            {
                errors.Add($"{path}.{name}", "Darf nicht null sein; ein optionales Feld wird weggelassen.");
            }
        }
    }

    /// <summary>Pfad aus einer Meldung von System.Text.Json, sonst die Wurzel.</summary>
    public static string PathOf(JsonException exception) =>
        string.IsNullOrEmpty(exception.Path) ? "$" : exception.Path;

    /// <summary>
    /// Liest das ganze Dokument in sein Abbild. Unbekannte Felder und falsche Datentypen ergeben einen
    /// Fehler mit Pfad (OF-01).
    /// </summary>
    public static T? Deserialize<T>(JsonDocument document, JsonTypeInfo<T> typeInfo, ImportErrors errors)
        where T : class
    {
        try
        {
            return document.RootElement.Deserialize(typeInfo);
        }
        catch (JsonException exception)
        {
            errors.Add(PathOf(exception), "Unbekanntes Feld oder falscher Datentyp.");
            return null;
        }
    }

    /// <summary>
    /// Schreibt ein Dokument gleichbleibend: eingerückt, Zeilenende LF, Umlaute unverändert, mit
    /// Zeilenende am Schluss. So bleibt ein Export auf allen Plattformen Byte für Byte gleich.
    /// </summary>
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Liest einen Enum-Wert über seinen festen Namen; meldet sonst die erlaubten Namen.</summary>
    public static T? ReadEnum<T>(WireNameMap<T> map, string? value, string path, ImportErrors errors)
        where T : struct, Enum
    {
        if (map.TryParse(value, out var result))
        {
            return result;
        }

        errors.Add(path, Unknown(value, map.Names));
        return null;
    }

    /// <summary>
    /// Meldung für einen fehlenden oder unbekannten Wert. Der Wert selbst erscheint nie in einer Meldung,
    /// weil er ein Geheimnis sein könnte (SI-03).
    /// </summary>
    public static string Unknown(string? value, IEnumerable<string> allowed) =>
        value is null
            ? $"Fehlt; erlaubt: {string.Join(", ", allowed)}."
            : $"Unbekannter Wert; erlaubt: {string.Join(", ", allowed)}.";

    /// <summary>
    /// Prüft einen frei wählbaren Bezeichner nach <see cref="IdPattern"/> und auf Geheimnisse; auch ein
    /// gültiger Bezeichner darf kein Schlüssel sein (OF-01, SI-03).
    /// </summary>
    public static void CheckId(ImportErrors errors, string path, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors.Add(path, "Fehlt.");
        }
        else if (value.Length > MaxIdLength || !IdRegex().IsMatch(value))
        {
            errors.Add(path, $"Kein gültiger Bezeichner (Kleinbuchstaben, Ziffern, Bindestriche, höchstens {MaxIdLength} Zeichen).");
        }
        else
        {
            CheckSecretsAndCode(errors, path, value);
        }
    }

    /// <summary>
    /// Prüft freien Text, etwa Namen und Beschreibungen: Länge, keine Steuer- oder Richtungszeichen, keine
    /// Geheimnisse (SI-03, OF-01) und keine ausführbaren Inhalte (OF-01).
    /// </summary>
    public static void CheckText(ImportErrors errors, string path, string? value, int maxLength, bool required)
    {
        if (value is null)
        {
            if (required)
            {
                errors.Add(path, "Fehlt.");
            }

            return;
        }

        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
        {
            errors.Add(path, "Darf nicht leer sein.");
            return;
        }

        if (CodePointCount(value) > maxLength)
        {
            errors.Add(path, $"Ist länger als {maxLength} Zeichen.");
            return;
        }

        if (value != value.Trim())
        {
            errors.Add(path, "Darf nicht mit Leerraum beginnen oder enden.");
        }

        if (!value.IsNormalized(NormalizationForm.FormC))
        {
            errors.Add(path, "Muss in Unicode-Normalform C vorliegen.");
        }

        if (ContainsForbiddenCharacter(value))
        {
            errors.Add(path, "Enthält Steuer- oder Richtungszeichen.");
        }

        CheckSecretsAndCode(errors, path, value);
    }

    /// <summary>Prüft einen Wert auf Geheimnisse und ausführbare Inhalte (OF-01).</summary>
    public static void CheckSecretsAndCode(ImportErrors errors, string path, string value)
    {
        if (LooksLikeSecret(value))
        {
            errors.Add(path, "Sieht aus wie ein Schlüssel, Token oder Passwort; Geheimnisse gehören nie in Profile oder Befehle (SI-03).");
        }

        if (LooksExecutable(value))
        {
            errors.Add(path, "Enthält ausführbare Inhalte oder Platzhalter; Profile und Befehle sind reine Daten.");
        }
    }

    /// <summary>
    /// Prüft eine Adresse auf Geheimnisse und ausführbare Inhalte, roh und prozentdekodiert. Sonst verbirgt ein
    /// kodiertes Trennzeichen wie <c>%2F</c> oder <c>%28</c> die Wortgrenze vor einem Schlüssel, weil die
    /// Kodierung selbst mit einem Buchstaben oder einer Ziffer endet (Codex-Nachprüfung 2, F2; OF-01, SI-03).
    /// Mehrfach kodierte Adressen werden bis zu einer festen Tiefe dekodiert.
    /// </summary>
    public static void CheckAddressSecretsAndCode(ImportErrors errors, string path, string value)
    {
        var forms = new List<string> { value };
        for (var round = 0; round < MaxDecodeRounds; round++)
        {
            var decoded = Uri.UnescapeDataString(forms[^1]);
            if (decoded == forms[^1])
            {
                break;
            }

            forms.Add(decoded);
        }

        if (forms.Any(LooksLikeSecret))
        {
            errors.Add(path, "Sieht aus wie ein Schlüssel, Token oder Passwort; Geheimnisse gehören nie in Profile oder Befehle (SI-03).");
        }

        if (forms.Any(LooksExecutable))
        {
            errors.Add(path, "Enthält ausführbare Inhalte oder Platzhalter; Profile und Befehle sind reine Daten.");
        }
    }

    private const int MaxDecodeRounds = 4;

    /// <summary>Ob der Text ein gültiger Bezeichner ist (<see cref="IdPatternStrict"/>, höchstens <see cref="MaxIdLength"/> Zeichen).</summary>
    public static bool IsId(string value) => value.Length is > 0 and <= MaxIdLength && IdRegex().IsMatch(value);

    /// <summary>Ob der Text eine Sprachkennung nach <see cref="ProfileSchema.LanguagePatternStrict"/> ist.</summary>
    public static bool IsLanguage(string value) => LanguageTagRegex().IsMatch(value);

    /// <summary>
    /// Ob der Text ein Programmname nach <see cref="ProfileSchema.ProgramPatternStrict"/> ist, höchstens
    /// <see cref="ProfileSchema.MaxProgramLength"/> Zeichen.
    /// </summary>
    public static bool IsProgramName(string value) =>
        value.Length <= ProfileSchema.MaxProgramLength && ProgramRegex().IsMatch(value);

    /// <summary>
    /// Ob der Text der Dateiname einer Modelldatei ist: nicht leer, höchstens <paramref name="maxLength"/> Zeichen,
    /// nur Kleinbuchstaben, Ziffern, <c>.</c>, <c>-</c> und <c>_</c>, beginnt mit Kleinbuchstabe oder Ziffer und
    /// endet auf <c>.bin</c>.
    /// </summary>
    public static bool IsModelFileName(string value, int maxLength) =>
        value.Length > 0
        && value.Length <= maxLength
        && value.EndsWith(".bin", StringComparison.Ordinal)
        && value[0] is (>= 'a' and <= 'z') or (>= '0' and <= '9')
        && value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_');

    /// <summary>Erkennt verbreitete Schlüssel- und Tokenformate sowie Zuweisungen wie <c>apiKey=…</c>.</summary>
    public static bool LooksLikeSecret(string value) => SecretRegex().IsMatch(value);

    /// <summary>Erkennt Skripte, Skript-URIs und Platzhalter, die ein späterer Baustein auswerten könnte.</summary>
    public static bool LooksExecutable(string value) => ExecutableRegex().IsMatch(value);

    /// <summary>
    /// Ob der Text Steuerzeichen oder Zeichen zur Schreibrichtung enthält, die ihn unsichtbar verfälschen können
    /// (Kategorien Cc, Cf, Zl und Zp). Geprüft wird nach Codepunkten, nicht nach UTF-16-Einheiten: Auch
    /// Formatzeichen ausserhalb der Basisebene, etwa die unsichtbaren Tag-Zeichen ab U+E0000, werden erkannt.
    /// </summary>
    public static bool ContainsForbiddenCharacter(string value)
    {
        for (var index = 0; index < value.Length; index += char.IsSurrogatePair(value, index) ? 2 : 1)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(value, index)
                is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Anzahl der Zeichen als Codepunkte, wie JSON Schema <c>maxLength</c> zählt (RFC 8259): Ein Zeichenpaar
    /// (etwa ein Emoji) zählt einmal, nicht als zwei UTF-16-Einheiten.
    /// </summary>
    public static int CodePointCount(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsLowSurrogate(value[index]) || index == 0 || !char.IsHighSurrogate(value[index - 1]))
            {
                count++;
            }
        }

        return count;
    }

    [GeneratedRegex(IdPatternStrict, RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex(ProfileSchema.LanguagePatternStrict, RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTagRegex();

    [GeneratedRegex(ProfileSchema.ProgramPatternStrict, RegexOptions.CultureInvariant)]
    private static partial Regex ProgramRegex();

    // Jedes Tokenmuster beginnt am Wortanfang: Vor dem Muster steht kein Buchstabe und keine Ziffer, damit
    // gewöhnliche Bezeichner wie „task-…“ oder „desk-…“ nicht als „sk-…“ gelten (Codex-Nachprüfung N2).
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:"
        + @"sk-[A-Za-z0-9_\-]{16,}"
        + @"|(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}"
        + @"|AKIA[0-9A-Z]{16}(?![\p{L}\p{N}])"
        + @"|AIza[0-9A-Za-z_\-]{30,}"
        + @"|xox[abposr]-[A-Za-z0-9\-]{10,}"
        + @"|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\."
        + @"|bearer\s+[A-Za-z0-9._~+/\-]{12,}"
        + @"|(api[_\-]?key|apikey|secret|token|password|passwort|kennwort|client[_\-]?secret)\s*[:=])"
        + @"|-----BEGIN [A-Z ]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretRegex();

    [GeneratedRegex(
        @"<\s*/?\s*script|\b(javascript|vbscript|file)\s*:|\$\{|\{\{|\$\(|<%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutableRegex();
}
