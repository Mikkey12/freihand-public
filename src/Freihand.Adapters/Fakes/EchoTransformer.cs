using Freihand.Core.Routing;
using Freihand.Core.Transform;

namespace Freihand.Adapters.Fakes;

/// <summary>Attrappe für Tests: gibt den Text unverändert zurück, ohne das Gerät zu verlassen.</summary>
public sealed class EchoTransformer : ITextTransformer
{
    public TransformRoute Route => TransformRoute.Local;

    public Task<TransformResult> TransformAsync(TransformRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TransformResult(request.Text));
    }
}
