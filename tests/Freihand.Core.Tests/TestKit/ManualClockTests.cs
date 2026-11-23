namespace Freihand.Core.Tests.TestKit;

/// <summary>Die Testuhr selbst: Zeitgeber laufen nur mit <see cref="ManualClock.Advance"/>, in zeitlicher Reihenfolge.</summary>
public sealed class ManualClockTests
{
    [Fact]
    public async Task DelayEndsOnlyWhenTheClockReachesIt()
    {
        var clock = new ManualClock();
        var delay = Task.Delay(TimeSpan.FromSeconds(2), clock);
        await clock.WaitForTimersAsync(1, TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(delay.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await delay.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Elapsed);
    }

    [Fact]
    public async Task TimersFireInOrderAndSeeTheirOwnDueTime()
    {
        var clock = new ManualClock();
        var seen = new List<(int Id, TimeSpan At)>();
        using var late = clock.CreateTimer(_ => seen.Add((2, clock.Elapsed)), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        using var early = clock.CreateTimer(_ => seen.Add((1, clock.Elapsed)), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        await clock.WaitForTimersAsync(2);

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal([(1, TimeSpan.FromSeconds(1)), (2, TimeSpan.FromSeconds(3))], seen);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task WaitForTimerDueInIgnoresEarlierTimers()
    {
        var clock = new ManualClock();
        using var stale = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        var waiting = clock.WaitForTimerDueInAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted, "Ein früher fälliger Zeitgeber zählt nicht.");

        var delay = Task.Delay(TimeSpan.FromSeconds(2), clock);
        await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(2));
        await delay.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CancellationAfterUsesTheClock()
    {
        var clock = new ManualClock();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1), clock);
        await clock.WaitForTimersAsync();

        Assert.False(cts.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(cts.IsCancellationRequested);
    }
}
