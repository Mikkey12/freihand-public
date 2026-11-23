using Freihand.Core.Routing;

namespace Freihand.Core.Transform;

/// <summary>
/// Auftrag zum Umformen. Anweisung und Text sind getrennt: Der Text, auch gelesener Fremdtext, geht nur
/// als Daten an das Sprachmodell, nie als Anweisung (SI-12).
/// </summary>
public sealed record TransformRequest(string Instruction, string Text, DataClass DataClass);

/// <summary>Ergebnis des Umformens. Es durchläuft nie die Befehlserkennung (SI-09).</summary>
public sealed record TransformResult(string Text);

/// <summary>Einheitliche Schnittstelle für jedes Umformen (WE-02).</summary>
public interface ITextTransformer
{
    TransformRoute Route { get; }

    Task<TransformResult> TransformAsync(TransformRequest request, CancellationToken cancellationToken = default);
}
