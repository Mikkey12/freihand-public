namespace Freihand.App;

/// <summary>Meldet beim Ablauf einer Wartezeit den Zustand des Threadpools, damit ein Zeitablauf in der CI belegt ist.</summary>
internal static class DiagnosticWait
{
    internal static async Task WaitOrReport(Task task, TimeSpan limit, string what)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            ThreadPool.GetAvailableThreads(out var available, out _);
            ThreadPool.GetMinThreads(out var min, out _);
            throw new TimeoutException(
                $"{what}: nach {started.ElapsedMilliseconds} ms ThreadCount={ThreadPool.ThreadCount} " +
                $"Pending={ThreadPool.PendingWorkItemCount} Completed={ThreadPool.CompletedWorkItemCount} " +
                $"Available={available} Min={min} Cpus={System.Environment.ProcessorCount} " +
                $"Sync={SynchronizationContext.Current?.GetType().Name ?? "-"}");
        }
    }
}
