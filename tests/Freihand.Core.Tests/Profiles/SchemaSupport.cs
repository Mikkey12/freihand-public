using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Freihand.Core.Tests.Profiles;

/// <summary>Liest Dateien aus dem Repository, etwa die Schemas unter <c>schemas/</c>.</summary>
internal static class RepositoryFiles
{
    public static string Read(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Freihand.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Wurzel des Repositorys nicht gefunden.");
        }

        return File.ReadAllText(Path.Combine([directory.FullName, .. relativePath]));
    }

    public static JsonObject ReadSchema(string fileName) =>
        JsonNode.Parse(Read("schemas", fileName))!.AsObject();
}

/// <summary>
/// Kleiner Prüfer für den Teil von JSON Schema 2020-12, den die Schemas unter <c>schemas/</c> nutzen.
/// Ein unbekanntes Schlüsselwort lässt den Test scheitern, damit das Schema nicht still ungeprüft bleibt.
/// </summary>
internal sealed class MiniSchemaValidator(JsonObject root)
{
    private static readonly HashSet<string> Annotations = ["$schema", "$id", "title", "description", "$defs"];

    public IReadOnlyList<string> Validate(JsonNode? instance)
    {
        var errors = new List<string>();
        Validate(root, instance, "$", errors);
        return errors;
    }

    public bool IsValid(JsonNode? instance) => Validate(instance).Count == 0;

    private void Validate(JsonObject schema, JsonNode? instance, string path, List<string> errors)
    {
        foreach (var (keyword, value) in schema)
        {
            switch (keyword)
            {
                case var _ when Annotations.Contains(keyword):
                    break;

                case "$ref":
                    Validate(Resolve(value!.GetValue<string>()), instance, path, errors);
                    break;

                case "type":
                    if (!HasType(instance, value!.GetValue<string>()))
                    {
                        errors.Add($"{path}: erwartet {value}");
                    }

                    break;

                case "const":
                    if (!JsonNode.DeepEquals(value, instance))
                    {
                        errors.Add($"{path}: erwartet {value!.ToJsonString()}");
                    }

                    break;

                case "enum":
                    if (!value!.AsArray().Any(candidate => JsonNode.DeepEquals(candidate, instance)))
                    {
                        errors.Add($"{path}: nicht in {value.ToJsonString()}");
                    }

                    break;

                case "pattern":
                    if (instance is JsonValue patternValue && patternValue.TryGetValue<string>(out var text)
                        && !Regex.IsMatch(ToCodePointProxy(text), EcmaPattern(value!.GetValue<string>())))
                    {
                        errors.Add($"{path}: passt nicht zu {value}");
                    }

                    break;

                case "minLength":
                case "maxLength":
                    if (instance is JsonValue lengthValue && lengthValue.TryGetValue<string>(out var lengthText))
                    {
                        var length = lengthText.EnumerateRunes().Count();
                        var limit = value!.GetValue<int>();
                        if (keyword == "minLength" ? length < limit : length > limit)
                        {
                            errors.Add($"{path}: {keyword} {limit}");
                        }
                    }

                    break;

                case "minItems":
                case "maxItems":
                    if (instance is JsonArray sizedArray)
                    {
                        var limit = value!.GetValue<int>();
                        if (keyword == "minItems" ? sizedArray.Count < limit : sizedArray.Count > limit)
                        {
                            errors.Add($"{path}: {keyword} {limit}");
                        }
                    }

                    break;

                case "required":
                    if (instance is JsonObject requiredObject)
                    {
                        foreach (var name in value!.AsArray().Select(node => node!.GetValue<string>()))
                        {
                            if (!requiredObject.ContainsKey(name))
                            {
                                errors.Add($"{path}.{name}: fehlt");
                            }
                        }
                    }

                    break;

                case "properties":
                    if (instance is JsonObject propertyObject)
                    {
                        foreach (var (name, propertySchema) in value!.AsObject())
                        {
                            if (propertyObject.TryGetPropertyValue(name, out var propertyValue))
                            {
                                Validate(propertySchema!.AsObject(), propertyValue, $"{path}.{name}", errors);
                            }
                        }
                    }

                    break;

                case "additionalProperties":
                    if (value!.GetValue<bool>())
                    {
                        throw new NotSupportedException("additionalProperties: true wird nicht genutzt.");
                    }

                    if (instance is JsonObject closedObject)
                    {
                        var known = schema["properties"]?.AsObject().Select(entry => entry.Key).ToHashSet() ?? [];
                        foreach (var (name, _) in closedObject)
                        {
                            if (!known.Contains(name))
                            {
                                errors.Add($"{path}.{name}: unbekanntes Feld");
                            }
                        }
                    }

                    break;

                case "items":
                    if (instance is JsonArray array)
                    {
                        for (var index = 0; index < array.Count; index++)
                        {
                            Validate(value!.AsObject(), array[index], $"{path}[{index}]", errors);
                        }
                    }

                    break;

                case "allOf":
                    foreach (var part in value!.AsArray())
                    {
                        Validate(part!.AsObject(), instance, path, errors);
                    }

                    break;

                case "not":
                    var inner = new List<string>();
                    Validate(value!.AsObject(), instance, path, inner);
                    if (inner.Count == 0)
                    {
                        errors.Add($"{path}: verbotene Form");
                    }

                    break;

                case "if":
                    var condition = new List<string>();
                    Validate(value!.AsObject(), instance, path, condition);
                    var branch = condition.Count == 0 ? schema["then"] : schema["else"];
                    if (branch is not null)
                    {
                        Validate(branch.AsObject(), instance, path, errors);
                    }

                    break;

                case "then":
                case "else":
                    break;

                default:
                    throw new NotSupportedException($"Schlüsselwort „{keyword}“ wird vom Testprüfer nicht unterstützt.");
            }
        }
    }

    /// <summary>
    /// ECMA 262, <c>WhiteSpace</c> und <c>LineTerminator</c>, als Inhalt einer Zeichenklasse: Tabulator, Zeilenvorschub,
    /// vertikaler Tabulator, Seitenvorschub, Wagenrücklauf, Leerzeichen, geschütztes Leerzeichen, alle Zeichen der
    /// Kategorie Zs (U+1680, U+2000 bis U+200A, U+202F, U+205F, U+3000), U+2028, U+2029 und U+FEFF. Anders als
    /// <c>\s</c> in .NET gehört U+FEFF dazu und U+0085 nicht.
    /// </summary>
    internal const string EcmaWhiteSpaceClass =
        @"\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";

    /// <summary>
    /// Übersetzt ein Muster aus dem Schema (JSON Schema, Syntax von ECMA 262, mit Unicode-Bedeutung für <c>\p{…}</c>)
    /// in die Schreibweise von .NET mit derselben Bedeutung. Die Unterschiede, die in den Mustern unter
    /// <c>schemas/</c> vorkommen oder vorkommen könnten:
    /// <list type="bullet">
    /// <item><c>$</c> passt nur am Ende (in .NET auch vor einem Zeilenvorschub am Schluss): <c>\z</c>.</item>
    /// <item><c>.</c> passt auf keinen Zeilenumbruch (<c>\n</c>, <c>\r</c>, U+2028, U+2029; in .NET nur auf <c>\n</c>).</item>
    /// <item><c>\s</c> und <c>\S</c> meinen <see cref="EcmaWhiteSpaceClass"/> (in .NET U+0085 statt U+FEFF), auch in Zeichenklassen.</item>
    /// <item><c>\d</c> und <c>\w</c> sind ASCII (in .NET Unicode).</item>
    /// </list>
    /// <c>RegexOptions.ECMAScript</c> hilft nicht (siehe <c>SchemaPatternTests</c>): Es behebt nur <c>\d</c> und
    /// <c>\w</c>, lässt <c>$</c> und den Punkt unverändert und macht <c>\s</c> zu einem ASCII-Leerraum ohne U+00A0
    /// und U+FEFF, also zu einer neuen Abweichung. Was nicht übersetzt ist (<c>\S</c> in
    /// einer Zeichenklasse, <c>\b</c>, <c>\B</c>, <c>\D</c> und <c>\W</c> in einer Zeichenklasse), wirft
    /// <see cref="NotSupportedException"/>, damit ein neues Muster nicht still falsch geprüft wird. Zeichenklassen,
    /// <c>^</c> und sonstige maskierte Zeichen bleiben unverändert (Codex-Prüfung PR #25, V02 und N01).
    /// </summary>
    internal static string EcmaPattern(string pattern)
    {
        RequireSupportedFeatures(pattern);
        var result = new System.Text.StringBuilder();
        var inClass = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var c = pattern[index];
            if (c == '\\' && index + 1 < pattern.Length)
            {
                AppendEscape(result, pattern[++index], inClass);
                continue;
            }

            if (inClass)
            {
                inClass = c != ']';
                result.Append(c);
                continue;
            }

            switch (c)
            {
                case '[':
                    inClass = true;
                    result.Append(c);
                    break;
                case '$':
                    result.Append(@"\z");
                    break;
                case '.':
                    result.Append(@"[^\n\r\u2028\u2029]");
                    break;
                default:
                    result.Append(c);
                    break;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Stellvertreter für Zeichen ausserhalb der Basisebene, nach allgemeiner Unicode-Kategorie: ein Zeichen der
    /// Basisebene derselben Kategorie, nicht ASCII und kein Leerraum (ausser bei Zs). ECMA 262 mit dem Flag <c>u</c>
    /// wertet Muster nach Codepunkten aus, eine .NET-Regex nach UTF-16-Einheiten; ein Zeichenpaar wäre dort zwei
    /// Zeichen und nie ein Buchstabe. Die Schemamuster unterscheiden Codepunkte nur nach Kategorie, Leerraum,
    /// Zeilenende und ASCII, darum genügt der Stellvertreter (<see cref="RequireSupportedFeatures"/> stellt das sicher).
    /// </summary>
    internal static readonly IReadOnlyDictionary<System.Globalization.UnicodeCategory, char> CategoryProxies =
        new Dictionary<System.Globalization.UnicodeCategory, char>
        {
            [System.Globalization.UnicodeCategory.UppercaseLetter] = (char)0x0410,
            [System.Globalization.UnicodeCategory.LowercaseLetter] = (char)0x0430,
            [System.Globalization.UnicodeCategory.TitlecaseLetter] = (char)0x01C5,
            [System.Globalization.UnicodeCategory.ModifierLetter] = (char)0x02B0,
            [System.Globalization.UnicodeCategory.OtherLetter] = (char)0x05D0,
            [System.Globalization.UnicodeCategory.NonSpacingMark] = (char)0x0300,
            [System.Globalization.UnicodeCategory.SpacingCombiningMark] = (char)0x0903,
            [System.Globalization.UnicodeCategory.EnclosingMark] = (char)0x20DD,
            [System.Globalization.UnicodeCategory.DecimalDigitNumber] = (char)0x0663,
            [System.Globalization.UnicodeCategory.LetterNumber] = (char)0x2160,
            [System.Globalization.UnicodeCategory.OtherNumber] = (char)0x00B2,
            [System.Globalization.UnicodeCategory.SpaceSeparator] = (char)0x3000,
            [System.Globalization.UnicodeCategory.LineSeparator] = (char)0x2028,
            [System.Globalization.UnicodeCategory.ParagraphSeparator] = (char)0x2029,
            [System.Globalization.UnicodeCategory.Control] = (char)0x0085,
            [System.Globalization.UnicodeCategory.Format] = (char)0x00AD,
            [System.Globalization.UnicodeCategory.PrivateUse] = (char)0xE000,
            [System.Globalization.UnicodeCategory.OtherNotAssigned] = (char)0x0378,
            [System.Globalization.UnicodeCategory.ConnectorPunctuation] = (char)0x203F,
            [System.Globalization.UnicodeCategory.DashPunctuation] = (char)0x2013,
            [System.Globalization.UnicodeCategory.OpenPunctuation] = (char)0x2768,
            [System.Globalization.UnicodeCategory.ClosePunctuation] = (char)0x2769,
            [System.Globalization.UnicodeCategory.InitialQuotePunctuation] = (char)0x00AB,
            [System.Globalization.UnicodeCategory.FinalQuotePunctuation] = (char)0x00BB,
            [System.Globalization.UnicodeCategory.OtherPunctuation] = (char)0x00A1,
            [System.Globalization.UnicodeCategory.MathSymbol] = (char)0x00B1,
            [System.Globalization.UnicodeCategory.CurrencySymbol] = (char)0x20AC,
            [System.Globalization.UnicodeCategory.ModifierSymbol] = (char)0x02C2,
            [System.Globalization.UnicodeCategory.OtherSymbol] = (char)0x00A6,
        };

    /// <summary>
    /// Ersetzt jedes Zeichenpaar (Codepunkt ab U+10000) durch den Stellvertreter seiner Kategorie, damit eine
    /// .NET-Regex es als ein Zeichen sieht, wie ECMA 262 mit dem Flag <c>u</c>. Ein einzelnes Ersatzzeichen bleibt, wie es
    /// ist: Es ist ein Zeichen der Kategorie Cs, gleich in beiden Auswertungen.
    /// </summary>
    internal static string ToCodePointProxy(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsSurrogatePair(text, index))
            {
                result.Append(CategoryProxies[System.Globalization.CharUnicodeInfo.GetUnicodeCategory(text, index)]);
                index++;
            }
            else
            {
                result.Append(text[index]);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Der Stellvertreter trägt nur, wenn ein Muster Codepunkte nach Kategorie, Leerraum, Zeilenende und ASCII
    /// unterscheidet. Alles andere (Zeichen ausserhalb von ASCII im Muster, <c>\u</c>, <c>\x</c>, <c>\c</c>, Skripte
    /// und Blöcke in <c>\p{…}</c>, benannte Gruppen) wirft, statt still falsch zu prüfen.
    /// </summary>
    private static void RequireSupportedFeatures(string pattern)
    {
        if (pattern.Any(c => c > 127))
        {
            throw new NotSupportedException("Zeichen ausserhalb von ASCII im Muster werden vom Testprüfer nicht übersetzt.");
        }

        for (var index = 0; index + 1 < pattern.Length; index++)
        {
            if (pattern[index] != '\\')
            {
                continue;
            }

            var escaped = pattern[++index];
            if (escaped is 'u' or 'x' or 'c' or 'k' or 'P' or 'p')
            {
                if (escaped is 'p' or 'P')
                {
                    var close = pattern.IndexOf('}', index);
                    var name = close > index + 2 && pattern[index + 1] == '{' ? pattern[(index + 2)..close] : string.Empty;
                    if (name.Length is < 1 or > 2 || !name.All(char.IsAsciiLetter))
                    {
                        throw new NotSupportedException($"\\{escaped}{{…}} ist nur mit einer allgemeinen Kategorie (ein oder zwei Buchstaben) übersetzt.");
                    }
                }
                else
                {
                    throw new NotSupportedException($"\\{escaped} wird vom Testprüfer nicht übersetzt.");
                }
            }
        }
    }

    private static void AppendEscape(System.Text.StringBuilder result, char escaped, bool inClass)
    {
        switch (escaped)
        {
            case 's':
                result.Append(inClass ? EcmaWhiteSpaceClass : "[" + EcmaWhiteSpaceClass + "]");
                break;
            case 'd':
                result.Append(inClass ? "0-9" : "[0-9]");
                break;
            case 'w':
                result.Append(inClass ? "A-Za-z0-9_" : "[A-Za-z0-9_]");
                break;
            case 'S' or 'D' or 'W' when !inClass:
                result.Append("[^").Append(escaped switch
                {
                    'S' => EcmaWhiteSpaceClass,
                    'D' => "0-9",
                    _ => "A-Za-z0-9_",
                }).Append(']');
                break;
            case 'S' or 'D' or 'W' or 'b' or 'B':
                throw new NotSupportedException($"\\{escaped} wird vom Testprüfer in dieser Stellung nicht übersetzt.");
            default:
                result.Append('\\').Append(escaped);
                break;
        }
    }

    private JsonObject Resolve(string reference)
    {
        const string prefix = "#/$defs/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Verweis „{reference}“ wird nicht unterstützt.");
        }

        return root["$defs"]![reference[prefix.Length..]]!.AsObject();
    }

    private static bool HasType(JsonNode? instance, string type) => type switch
    {
        "object" => instance is JsonObject,
        "array" => instance is JsonArray,
        "string" => instance?.GetValueKind() == JsonValueKind.String,
        "integer" => instance?.GetValueKind() == JsonValueKind.Number && instance.GetValue<decimal>() % 1 == 0,
        "boolean" => instance?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        _ => throw new NotSupportedException($"Typ „{type}“ wird nicht unterstützt."),
    };
}
