using System.Globalization;

namespace Freihand.Zielprobe;

/// <summary>Befehlszeile: Befehl, dann Optionen <c>--name wert</c>; <c>--einzeilig</c> und <c>--help</c> ohne Wert.</summary>
internal sealed class Optionen
{
    private readonly Dictionary<string, string> _werte = new(StringComparer.OrdinalIgnoreCase);

    public string? Befehl { get; private init; }

    public bool Einzeilig { get; private set; }

    /// <summary>Ein kurzer Inhalt vor dem Schreiben ist der Platzhalter des Felds (z. B. Teams), nicht fremder Text.</summary>
    public bool Platzhalter { get; private set; }

    /// <summary>„Feld messen“ in Freihand: nach einem Durchgang in ein nachweislich leeres Feld darf es geleert werden.</summary>
    public bool Messmodus { get; private set; }

    public bool Hilfe { get; private set; }

    /// <exception cref="ArgumentException">Fehlender Wert oder freies Argument.</exception>
    /// <summary>„messen“: RuntimeId des Felds, das beim Countdown erkannt wurde; jeder Durchgang schreibt nur dorthin.</summary>
    public string? Anker => Text("anker");

    public static Optionen Lesen(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var befehl = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToLowerInvariant() : null;
        var result = new Optionen { Befehl = befehl };
        for (var i = befehl is null ? 0 : 1; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unerwartetes Argument „{args[i]}“.");
            }

            var name = args[i][2..].ToLowerInvariant();
            if (name == "einzeilig")
            {
                result.Einzeilig = true;
            }
            else if (name == "platzhalter")
            {
                result.Platzhalter = true;
            }
            else if (name == "messmodus")
            {
                result.Messmodus = true;
            }
            else if (name == "help")
            {
                result.Hilfe = true;
            }
            else if (i + 1 >= args.Count)
            {
                throw new ArgumentException($"{args[i]} braucht einen Wert.");
            }
            else
            {
                result._werte[name] = args[++i];
            }
        }

        return result;
    }

    public string? Text(string name) => _werte.GetValueOrDefault(name);

    public string Pflicht(string name) =>
        Text(name) is { Length: > 0 } wert ? wert : throw new ArgumentException($"--{name} fehlt.");

    public int Zahl(string name, int standard, int minimum = 0)
    {
        if (Text(name) is not { } wert)
        {
            return standard;
        }

        if (!int.TryParse(wert, NumberStyles.None, CultureInfo.InvariantCulture, out var zahl) || zahl < minimum)
        {
            throw new ArgumentException($"--{name} muss eine ganze Zahl ab {minimum} sein.");
        }

        return zahl;
    }

    /// <summary>Teilgrössen aus <c>--teile 1,4,10,20</c>; jede mindestens 1.</summary>
    public static IReadOnlyList<int> Teile(string? liste)
    {
        var teile = new List<int>();
        foreach (var stueck in (liste ?? "1,4,10,20").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(stueck, NumberStyles.None, CultureInfo.InvariantCulture, out var zahl) || zahl < 1)
            {
                throw new ArgumentException($"--teile: „{stueck}“ ist keine Teilgrösse ab 1.");
            }

            teile.Add(zahl);
        }

        return teile.Count > 0 ? teile : throw new ArgumentException("--teile ist leer.");
    }
}
