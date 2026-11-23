using System.Globalization;

namespace Freihand.Benchmark;

/// <summary>Einfache Befehlszeile: Befehl, ein freies Argument und Optionen <c>--name wert</c>.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private init; }

    public string? Positional { get; private set; }

    public static Arguments Parse(string[] args)
    {
        var result = new Arguments { Command = args.Length > 0 ? args[0].ToLowerInvariant() : null };
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{args[i]} braucht einen Wert.");
                }

                result._options[args[i][2..]] = args[++i];
            }
            else
            {
                result.Positional ??= args[i];
            }
        }

        return result;
    }

    public string? String(string name) => _options.GetValueOrDefault(name);

    public int Int(string name, int fallback) =>
        String(name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public double Double(string name, double fallback) =>
        String(name) is { } value ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;

    /// <summary>Ganzzahl ab <paramref name="minimum"/>; sonst ein Fehler vor dem Modellstart.</summary>
    public int Int(string name, int fallback, int minimum) =>
        Int(name, fallback) is var value && value >= minimum
            ? value
            : throw new ArgumentException($"--{name} muss mindestens {minimum} sein.");

    /// <summary>Kleinste Zeit, die das Werkzeug annimmt; darunter kommen Schritte und Sätze nicht voran.</summary>
    public const double MinimumSeconds = 0.001;

    /// <summary>
    /// Endliche Zeit ab <see cref="MinimumSeconds"/> (mit <paramref name="allowZero"/> auch genau 0); sonst ein Fehler
    /// vor dem Modellstart.
    /// </summary>
    public double Positive(string name, double fallback, bool allowZero = false) =>
        Double(name, fallback) is var value && double.IsFinite(value) && (value >= MinimumSeconds || (allowZero && value == 0))
            ? value
            : throw new ArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"--{name} muss eine endliche Zahl ab {MinimumSeconds} sein{(allowZero ? " oder 0" : "")}."));
}
