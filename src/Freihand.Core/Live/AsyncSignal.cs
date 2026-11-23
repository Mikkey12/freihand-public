namespace Freihand.Core.Live;

/// <summary>
/// Weckruf zwischen Annahme und Decoderarbeit. Wer wartet, holt <see cref="Next"/> unter derselben Sperre, unter der er
/// den Zustand geprüft hat; wer den Zustand ändert, ruft danach <see cref="Set"/>. So geht kein Weckruf verloren.
/// </summary>
internal sealed class AsyncSignal
{
    private TaskCompletionSource _next = New();

    /// <summary>Wird beim nächsten <see cref="Set"/> erfüllt.</summary>
    public Task Next => Volatile.Read(ref _next).Task;

    public void Set() => Interlocked.Exchange(ref _next, New()).TrySetResult();

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
