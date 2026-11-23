using System.Text.RegularExpressions;
using Freihand.Adapters.Dictation;
using Freihand.Core.Routing;
using Freihand.Core.Transform;

namespace Freihand.Adapters.Transform;

public enum DiffKind
{
    Same,
    Removed,
    Added,
}

/// <param name="Text">Wörter, Leerraum und Satzzeichen dieses Teils.</param>
public sealed record DiffPart(DiffKind Kind, string Text);

/// <summary>
/// Ein Vorschlag: Original, überarbeiteter Text, die Teile für die Anzeige und Warnungen
/// zu geänderten Zahlen und Verneinungen (BA-18). Er ersetzt nie von selbst etwas.
/// </summary>
public sealed record Suggestion(string Original, string Text, IReadOnlyList<DiffPart> Parts, IReadOnlyList<string> Warnings)
{
    public bool Unchanged => Original == Text;
}

/// <summary>
/// Vorschlag holen: Anweisung je Art, Zeitgrenze, danach die Schweizer Schreibweise von Freihand (BA-17) und der Vergleich.
/// Ohne Schlüssel, bei Zeitüberschreitung oder Fehler kommt kein Vorschlag, sondern immer <see cref="TransformFailedException"/>
/// mit Grund, nie eine andere Ausnahme: So zeigt jedes Fenster einen Hinweis und bleibt nie bei „Korrigiere …“ stehen.
/// Die Ränder (Leerraum und Zeilenumbrüche am Anfang und Ende) gehen nicht ans Modell und bleiben im Vorschlag, wie sie
/// waren: Eine markierte Zeile samt Absatzende bleibt so ein eigener Absatz (B9).
/// </summary>
public sealed class CorrectionService(Func<ITextTransformer?> transformer, Func<string, string> format, TimeSpan timeout)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <exception cref="TransformFailedException">Kein Vorschlag; der Text bleibt, wie er war.</exception>
    /// <param name="dataClass">Einstufung des Texts; gelesener Fremdtext kommt als „vertraulich“ (SI-12), sonst „intern“.</param>
    public async Task<Suggestion> SuggestAsync(
        string text, CorrectionKind kind, CancellationToken cancellationToken = default, DataClass? dataClass = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var original = PadText.ToCore(text);
        var core = original.Trim();
        if (core.Length == 0)
        {
            return new Suggestion(original, original, [new DiffPart(DiffKind.Same, original)], []);
        }

        var lead = original[..(original.Length - original.TrimStart().Length)];
        var trail = original[original.TrimEnd().Length..];

        ITextTransformer? service;
        try
        {
            service = transformer();
        }
        catch (Exception exception) when (exception is not TransformFailedException and not OutOfMemoryException)
        {
            throw new TransformFailedException(TransformFailure.Failed);
        }

        if (service is null)
        {
            throw new TransformFailedException(TransformFailure.NoKey);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        TransformResult result;
        try
        {
            result = await service.TransformAsync(
                new TransformRequest(CorrectionInstructions.For(kind), core, dataClass ?? DataClass.Internal), limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransformFailedException(TransformFailure.Timeout);
        }
        catch (Exception exception) when (exception is not TransformFailedException and not OperationCanceledException
                                              and not OutOfMemoryException)
        {
            throw new TransformFailedException(TransformFailure.Failed);
        }

        var answer = format(PadText.ToCore(result.Text).Trim());
        if (answer.Length == 0)
        {
            // Ein leerer Vorschlag würde den Text löschen: kein Vorschlag
            throw new TransformFailedException(TransformFailure.Rejected);
        }

        var suggested = lead + answer + trail;
        return new Suggestion(original, suggested, SuggestionDiff.Parts(original, suggested), SuggestionDiff.Warnings(original, suggested));
    }
}

/// <summary>Vergleich Wort für Wort und Warnungen nach BA-18; ohne Netz und ohne WPF prüfbar.</summary>
public static partial class SuggestionDiff
{
    /// <summary>Darüber wird nicht mehr Wort für Wort verglichen, sondern das Ganze als ersetzt gezeigt.</summary>
    private const long MaxCells = 4_000_000;

    private static readonly HashSet<string> Negations = new(StringComparer.OrdinalIgnoreCase)
    {
        "nicht", "kein", "keine", "keinen", "keinem", "keiner", "keines", "nie", "niemals", "nichts", "niemand", "weder", "ohne",
    };

    public static IReadOnlyList<DiffPart> Parts(string original, string suggested)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(suggested);
        var a = Tokens(original);
        var b = Tokens(suggested);
        if ((long)a.Count * b.Count > MaxCells)
        {
            return Merge([new DiffPart(DiffKind.Removed, original), new DiffPart(DiffKind.Added, suggested)]);
        }

        // Längste gemeinsame Teilfolge von hinten, dann von vorn ablesen
        var table = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var parts = new List<DiffPart>();
        int x = 0, y = 0;
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && a[x] == b[y])
            {
                parts.Add(new DiffPart(DiffKind.Same, a[x]));
                x++;
                y++;
            }
            else if (y < b.Count && (x == a.Count || table[x, y + 1] >= table[x + 1, y]))
            {
                parts.Add(new DiffPart(DiffKind.Added, b[y]));
                y++;
            }
            else
            {
                parts.Add(new DiffPart(DiffKind.Removed, a[x]));
                x++;
            }
        }

        return Merge(parts);
    }

    /// <summary>Geänderte Zahlen und Verneinungen; Namen sind im Deutschen nicht verlässlich von Nomen zu trennen.</summary>
    public static IReadOnlyList<string> Warnings(string original, string suggested)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(suggested);
        var warnings = new List<string>();
        if (!Same(Numbers(original), Numbers(suggested)))
        {
            warnings.Add("Zahlen geändert – bitte prüfen.");
        }

        if (!Same(Words(original).Where(Negations.Contains).Select(word => word.ToLowerInvariant()),
                Words(suggested).Where(Negations.Contains).Select(word => word.ToLowerInvariant())))
        {
            warnings.Add("Verneinung geändert – bitte prüfen.");
        }

        return warnings;
    }

    private static List<string> Tokens(string text) => [.. TokenPattern().Matches(text).Select(match => match.Value)];

    private static IEnumerable<string> Words(string text) => WordPattern().Matches(text).Select(match => match.Value);

    private static IEnumerable<string> Numbers(string text) => NumberPattern().Matches(text).Select(match => match.Value.Replace("'", string.Empty, StringComparison.Ordinal));

    private static bool Same(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Gleichartige Teile hintereinander zusammenfassen.</summary>
    private static List<DiffPart> Merge(List<DiffPart> parts)
    {
        var merged = new List<DiffPart>();
        foreach (var part in parts.Where(part => part.Text.Length > 0))
        {
            if (merged.Count > 0 && merged[^1].Kind == part.Kind)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + part.Text };
            }
            else
            {
                merged.Add(part);
            }
        }

        return merged;
    }

    [GeneratedRegex(@"\s+|[\p{L}\p{N}]+|[^\s\p{L}\p{N}]")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\d+(?:['.,]\d+)*")]
    private static partial Regex NumberPattern();
}
