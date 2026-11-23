namespace Freihand.Core.Live;

/// <summary>
/// Liest eine fremde asynchrone Folge so, dass ein Abbruch nie auf sie warten muss (BE-06, Kapitel 5.4; Befunde U03, N01).
/// <list type="bullet">
/// <item>Wurde ein Lesen abgebrochen, gilt die Folge als verworfen, gleich ob das fremde <c>MoveNextAsync</c> danach
/// noch endet: Die Freigabe läuft im Hintergrund, nach dem Ende des Lesens, genau einmal; jede Ausnahme wird beobachtet.</item>
/// <item>Sonst wird regulär freigegeben und gewartet, ausser der Lauf ist schon abgebrochen: dann wird die Freigabe nur
/// angestossen und ihre Ausnahme beobachtet.</item>
/// </list>
/// Es gibt nie gleichzeitige Aufrufe von <c>MoveNextAsync</c> und <c>DisposeAsync</c>.
/// </summary>
internal sealed class CancellableEnumerator<T> : IAsyncDisposable
{
    private readonly IAsyncEnumerator<T> _inner;
    private readonly CancellationToken _releaseToken;
    private Task<bool>? _pending;
    private bool _abandoned;
    private bool _disposed;

    public CancellableEnumerator(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
        : this(source, cancellationToken, cancellationToken)
    {
    }

    /// <param name="readToken">Beendet das Lesen; die Folge erhält ihn beim Öffnen.</param>
    /// <param name="releaseToken">
    /// Abbruch oder Fehler des Laufs: Nur er beendet das Warten auf eine reguläre Freigabe. Ein blosses Ende des Lesens
    /// (etwa weil die Erkennung vor dem Audio endete) bricht eine schon reguläre Freigabe nicht ab (W01).
    /// </param>
    public CancellableEnumerator(IAsyncEnumerable<T> source, CancellationToken readToken, CancellationToken releaseToken)
    {
        _inner = source.GetAsyncEnumerator(readToken);
        _releaseToken = releaseToken;
    }

    public T Current => _inner.Current;

    /// <summary>
    /// Einziger Besitzer einer fremden Folge für einen Lauf (R02, Z01): öffnet sie, liest sie mit <paramref name="body"/>
    /// und gibt sie genau einmal frei. Freigaberegel (Kapitel 5.4):
    /// <list type="bullet">
    /// <item>Jede Ausnahme aus Öffnen, Lesen, <c>Current</c> oder <paramref name="body"/> geht an
    /// <paramref name="onError"/>, <b>bevor</b> freigegeben wird.</item>
    /// <item><b>Reguläres Ende</b>: <paramref name="body"/> endete ohne Fehler, weder <paramref name="token"/> noch
    /// <paramref name="releaseToken"/> sind abgebrochen und <paramref name="aborted"/> meldet keinen Fehler oder Abbruch
    /// des Laufs. Dann wird auf die Freigabe gewartet, bis sie endet oder <paramref name="releaseToken"/> abbricht; ein
    /// späteres Ende des Lesens über <paramref name="token"/> ändert daran nichts (W01). Ihr Fehler geht an
    /// <paramref name="onReleaseError"/>, wird also Fehler des Laufs und beendet ihn (W02).</item>
    /// <item><b>Fehler, Abbruch oder vorzeitiges Ende</b>: sonst. Dann bricht <paramref name="abort"/> das Token dieses
    /// Besitzers ab, die Freigabe wird nur angestossen und ihr Fehler beobachtet.</item>
    /// </list>
    /// Wirft nie.
    /// </summary>
    /// <param name="token">Beendet das Lesen.</param>
    /// <param name="releaseToken">Abbruch oder Fehler des Laufs; mit <paramref name="token"/> verknüpft oder gleich.</param>
    public static async Task ConsumeAsync(
        Func<CancellationToken, IAsyncEnumerable<T>> open,
        CancellationToken token,
        CancellationToken releaseToken,
        Func<CancellableEnumerator<T>, Task> body,
        Action<Exception> onError,
        Func<bool> aborted,
        Action abort,
        Action<Exception> onReleaseError)
    {
        CancellableEnumerator<T>? reader = null;
        var readStopped = false;
        try
        {
            reader = new CancellableEnumerator<T>(open(token), token, releaseToken);
            await body(reader).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Abbruch, Ende des Laufs oder vorzeitiges Ende des Lesens
            readStopped = true;
        }
        catch (Exception exception)
        {
            onError(exception);
        }
        finally
        {
            // Entschieden wird einmal, am Ende des Lesens. Endete es von selbst (Streamende, Aufnahmestopp), ist ein
            // gleichzeitiges Ende des Lesens über token kein Abbruch; für das Warten zählt nur noch releaseToken (W01)
            var regular = !readStopped && !releaseToken.IsCancellationRequested && !aborted();
            if (!regular)
            {
                abort();
            }

            if (reader is not null)
            {
                if (!regular)
                {
                    // Fehler, Abbruch oder vorzeitiges Ende: nur anstossen, unabhängig davon, welche Tokens schon
                    // abgebrochen sind (etwa ein Detektorfehler, der den Lauf erst danach abbricht)
                    reader._abandoned = true;
                }

                try
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (releaseToken.IsCancellationRequested)
                {
                    // Der Lauf wurde während der Freigabe abgebrochen; ihr Ausgang wird beobachtet
                }
                catch (Exception exception) when (regular)
                {
                    onReleaseError(exception);
                }
                catch (Exception)
                {
                    // Nach Fehler oder Abbruch ändert ein Freigabefehler nichts mehr am Ausgang des Laufs
                }
            }
        }
    }

    /// <summary>Wartet abbrechbar auf das nächste Element.</summary>
    public async Task<bool> MoveNextAsync(CancellationToken cancellationToken)
    {
        var next = _inner.MoveNextAsync().AsTask();
        _pending = next;
        try
        {
            var more = await next.WaitAsync(cancellationToken).ConfigureAwait(false);
            _pending = null;
            return more;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Der Abbruch gilt auch, wenn das fremde Lesen gleich danach noch endet (N01)
            _abandoned = true;
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_abandoned || _pending is { IsCompleted: false })
        {
            _ = ReleaseAfterAsync(_pending);
            return ValueTask.CompletedTask;
        }

        return ReleaseAsync();
    }

    /// <summary>Hängt eine Beobachtung an, damit eine spätere Ausnahme nie unbeobachtet bleibt.</summary>
    internal static void Observe(Task task) =>
        _ = task.ContinueWith(
            static done => done.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async ValueTask ReleaseAsync()
    {
        Task release;
        try
        {
            release = _inner.DisposeAsync().AsTask();
        }
        catch (Exception exception) when (_releaseToken.IsCancellationRequested)
        {
            release = Task.FromException(exception);
        }

        if (_releaseToken.IsCancellationRequested)
        {
            // Der Lauf ist abgebrochen oder gescheitert: anstossen, nicht darauf warten
            Observe(release);
            return;
        }

        try
        {
            await release.WaitAsync(_releaseToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_releaseToken.IsCancellationRequested)
        {
            Observe(release);
        }
    }

    private async Task ReleaseAfterAsync(Task<bool>? pending)
    {
        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Verworfen: das Ergebnis eines abgebrochenen Laufs wird nicht mehr verwendet
            }
        }

        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fehler beim späten Freigeben einer verworfenen Quelle ändern am beendeten Lauf nichts
        }
    }
}
