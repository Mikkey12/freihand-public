using System.Reflection;
namespace Freihand.App;
public sealed class DeltaReleaseProbe
{
    [Fact]
    public async Task ReleaseDeadlineIncludesSynchronousPart()
    {
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var method = typeof(App).GetMethod("ReleaseAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        Func<ValueTask> release = () => { entered.TrySetResult(); gate.Wait(); return ValueTask.CompletedTask; };
        var pending = Task.Run(async () => await (Task)method.Invoke(null, [release])!);
        bool bounded;
        try
        {
            await DiagnosticWait.WaitOrReport(entered.Task, TimeSpan.FromSeconds(10), "Delta entered");
            bounded = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(6))) == pending;
        }
        finally
        {
            gate.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(bounded, "Die 5-s-Frist erfasst den synchronen Teil der Freigabe nicht.");
    }
}
