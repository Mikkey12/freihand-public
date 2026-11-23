using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Freihand.Adapters.Transform;
using Freihand.Core.Routing;
using Freihand.Core.Transform;

namespace Freihand.Online;

/// <summary>
/// Korrigieren und Glätten über die Claude-API:
/// nur privat, nur auf Knopfdruck. Die Anweisung steht im Systemteil, der Text nur als Daten im Benutzerteil (SI-12);
/// das Ergebnis geht nie durch die Befehlserkennung (SI-09). Für Tempo ohne Denkschritt und mit niedrigem Aufwand.
/// </summary>
public sealed class ClaudeTransformer : ITextTransformer, IDisposable
{
    /// <summary>Standardmodell nach Entscheid B-E8.</summary>
    public const string DefaultModel = "claude-haiku-5-5";

    private readonly AnthropicClient _client;
    private readonly string _model;

    /// <summary>Hat der Schlüssel die Form eines Schlüssels? Sonst wird nichts gesendet (SDK-Fehler könnten ihn enthalten).</summary>
    private readonly bool _keyUsable;

    /// <param name="apiKey">Schlüssel aus dem DPAPI-Speicher; nie protokolliert.</param>
    /// <param name="timeout">Höchstdauer eines Aufrufs samt Wiederholungen der SDK-Grenze.</param>
    public ClaudeTransformer(string apiKey, string? model, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        var key = apiKey.Trim();
        _keyUsable = ApiKeyFormat.IsValid(key);
        _client = new AnthropicClient { ApiKey = key, Timeout = timeout, MaxRetries = 1 };
    }

    public TransformRoute Route => TransformRoute.PrivateProvider;

    public async Task<TransformResult> TransformAsync(TransformRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_keyUsable)
        {
            throw new TransformFailedException(TransformFailure.KeyRejected);
        }

        // Jede Ausnahme wird zu einem Code ohne innere Ausnahme: Antworten, Kopfzeilen und Schlüssel bleiben draussen
        var boundary = CorrectionInstructions.NewBoundary(request.Text);
        Message response;
        try
        {
            response = await _client.Messages.Create(Build(request, _model, boundary), cancellationToken).ConfigureAwait(false);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new TransformFailedException(TransformFailure.KeyRejected);
        }
        catch (Exception exception) when (exception is AnthropicRateLimitException or Anthropic5xxException)
        {
            throw new TransformFailedException(TransformFailure.Busy);
        }
        catch (AnthropicIOException)
        {
            throw new TransformFailedException(TransformFailure.Unreachable);
        }
        catch (AnthropicApiException)
        {
            throw new TransformFailedException(TransformFailure.Rejected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TransformFailedException(TransformFailure.Timeout);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new TransformFailedException(TransformFailure.Failed);
        }

        return new TransformResult(Read(response, boundary));
    }

    public void Dispose() => (_client as IDisposable)?.Dispose();

    /// <summary>Die Anfrage; ohne Netz prüfbar.</summary>
    internal static MessageCreateParams Build(TransformRequest request, string model, string boundary) => new()
    {
        Model = model,
        // Genug für den Text samt Umbrüchen; ein Diktat im Diktatfenster ist selten länger
        MaxTokens = 16000,
        System = request.Instruction + CorrectionInstructions.Frame(boundary),
        Thinking = new ThinkingConfigDisabled(),
        OutputConfig = new OutputConfig { Effort = Effort.Low },
        Messages = [new UserMessageParam { Content = CorrectionInstructions.Wrap(request.Text, boundary) }],
    };

    /// <summary>Nur Text; abgelehnt oder abgeschnitten heisst kein Vorschlag, der Text bleibt, wie er war.</summary>
    internal static string Read(Message response, string boundary)
    {
        if (response.StopReason is { } reason && reason != StopReason.EndTurn)
        {
            throw new TransformFailedException(
                reason == StopReason.MaxTokens ? TransformFailure.TooLong : TransformFailure.Rejected);
        }

        var text = string.Concat(response.Content.Select(block => block.Value).OfType<TextBlock>().Select(block => block.Text));
        return CorrectionInstructions.Unwrap(text, boundary);
    }
}
