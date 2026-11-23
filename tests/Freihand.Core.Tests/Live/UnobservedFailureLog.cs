using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Freihand.Core.Tests.Live;

/// <summary>
/// Protokoll unbeobachteter Ausnahmen für T70 (Kapitel 5.4). Es meldet sich beim Laden der Testassembly an, also vor
/// jedem Test, damit keine blinde Phase vor dem ersten Fall entsteht (Codex-Nachprüfung 5, Y02).
/// </summary>
internal static class UnobservedFailureLog
{
    private static readonly ConcurrentBag<string> Seen = [];

    [ModuleInitializer]
    internal static void Start() => TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        foreach (var inner in e.Exception.Flatten().InnerExceptions)
        {
            for (var current = inner; current is not null; current = current.InnerException)
            {
                Seen.Add(current.Message);
            }
        }
    };

    /// <summary>Räumt auf, bis gemeldet ist, was zu melden ist, und liefert, ob eine Meldung die Kennung trägt.</summary>
    public static async Task<bool> SeenAsync(string marker)
    {
        // Fortsetzungen laufen lassen, dann mehrfach aufräumen: Finalizer melden verlorene Tasks erst dabei
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(10);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (Seen.Any(message => message.Contains(marker, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
