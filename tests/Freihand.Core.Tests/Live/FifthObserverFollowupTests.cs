using System.Reflection;
using System.Runtime.CompilerServices;
namespace Freihand.Core.Tests.Live;
public sealed class FifthObserverFollowupTests
{
    // Separat in einem frischen Testhost ausführen: Der erste Matrixfall darf keine blinde Phase haben.
    [Fact]
    public async Task FirstObserverAssertionDetectsAnAlreadyFinalizedFailure()
    {
        var marker = Guid.NewGuid().ToString("N");
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<UnobservedTaskExceptionEventArgs> witness = (_, e) =>
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message.Contains(marker, StringComparison.Ordinal)))
            {
                delivered.TrySetResult();
                e.SetObserved();
            }
        };
        TaskScheduler.UnobservedTaskException += witness;
        try
        {
            var weak = LoseFailure(marker);
            for (var i = 0; i < 20 && !delivered.Task.IsCompleted; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                await Task.Delay(10);
            }
            Assert.True(delivered.Task.IsCompleted, "Kontrolle: .NET hat die verlorene Ausnahme tatsächlich gemeldet.");
            Assert.False(weak.IsAlive);
            var helper = typeof(ReleaseRuleMatrixTests).GetNestedType("UnobservedFailures", BindingFlags.NonPublic)!;
            var method = helper.GetMethod("AssertNoneAsync", BindingFlags.Static | BindingFlags.Public)!;
            var failure = await Record.ExceptionAsync(() => (Task)method.Invoke(null, [marker])!);
            Assert.NotNull(failure);
        }
        finally { TaskScheduler.UnobservedTaskException -= witness; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoseFailure(string marker) => new(Task.FromException(new IOException(marker)));
}
