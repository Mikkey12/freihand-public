namespace Freihand.Windows.Beta;

/// <summary>
/// Nur ein Freihand je Benutzer, und ein laufendes lässt sich von aussen regulär beenden:
/// Das Update-Skript ruft <c>Freihand.exe --beenden</c>; das laufende Freihand beendet sich dann wie über das Menü, sichert
/// also die Schreibfläche und entfernt das Symbol. Benannte Ereignisse der Sitzung, nicht an einen Thread gebunden.
/// </summary>
public sealed class InstanceSignal : IDisposable
{
    public const string DefaultPrefix = @"Local\Freihand";

    private readonly EventWaitHandle _running;
    private readonly EventWaitHandle _exit;
    private readonly RegisteredWaitHandle _wait;

    private InstanceSignal(EventWaitHandle running, EventWaitHandle exit)
    {
        _running = running;
        _exit = exit;
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _exit, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>Ein anderer Aufruf verlangt, dass dieses Freihand sich beendet; kommt auf einem Threadpool-Thread.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Beansprucht die Sitzung für dieses Freihand; null, wenn schon eines läuft.</summary>
    public static InstanceSignal? TryClaim(string prefix = DefaultPrefix)
    {
        var running = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Laeuft", out var created);
        if (!created)
        {
            running.Dispose();
            return null;
        }

        var exit = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Beenden");
        return new InstanceSignal(running, exit);
    }

    /// <summary>Bittet ein laufendes Freihand, sich zu beenden; falsch, wenn keines läuft.</summary>
    public static bool RequestExit(string prefix = DefaultPrefix)
    {
        if (!EventWaitHandle.TryOpenExisting(prefix + ".Laeuft", out var running))
        {
            return false;
        }

        running.Dispose();
        using var exit = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Beenden");
        exit.Set();
        return true;
    }

    /// <summary>Läuft gerade ein Freihand in dieser Sitzung?</summary>
    public static bool IsRunning(string prefix = DefaultPrefix)
    {
        if (!EventWaitHandle.TryOpenExisting(prefix + ".Laeuft", out var running))
        {
            return false;
        }

        running.Dispose();
        return true;
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _exit.Dispose();
        _running.Dispose();
    }
}
