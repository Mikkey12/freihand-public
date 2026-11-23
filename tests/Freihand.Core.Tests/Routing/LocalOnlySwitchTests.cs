using Freihand.Core.Routing;
using static Freihand.Core.Tests.Routing.RouteTestData;

namespace Freihand.Core.Tests.Routing;

/// <summary>Notschalter „nur lokal“: sperrt auch Heim und verwirft wartende externe Aufträge (SI-07).</summary>
public class LocalOnlySwitchTests
{
    private static readonly RouteRequest OnDevice =
        Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.Local);

    private static readonly RouteRequest ViaHome =
        Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.Home);

    private static readonly RouteRequest Online =
        Request(DataClass.Public, RecognitionRoute.Online, TransformRoute.None);

    private static readonly RouteRequest ToProvider =
        Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.PrivateProvider);

    [Fact]
    public void EngagingDiscardsEveryPendingExternalJobIncludingHome()
    {
        var localOnly = new LocalOnlySwitch();
        using var device = localOnly.Track(OnDevice);
        using var home = localOnly.Track(ViaHome);
        using var online = localOnly.Track(Online);
        using var provider = localOnly.Track(ToProvider);
        DiscardRequestedEventArgs? reported = null;
        localOnly.DiscardRequested += (_, e) => reported = e;

        var discarded = localOnly.Engage();

        Assert.True(localOnly.IsEngaged);
        Assert.Equal(
            new[] { home.Id, online.Id, provider.Id }.Order(),
            discarded.Select(j => j.Id).Order());
        Assert.NotNull(reported);
        Assert.Equal(discarded, reported.Jobs);
        Assert.All(discarded, j => Assert.True(j.DiscardToken.IsCancellationRequested));
        Assert.False(device.IsDiscarded);
        Assert.Equal([device], localOnly.Pending);
    }

    [Fact]
    public void ExternalJobTrackedWhileEngagedIsDiscardedAtOnce()
    {
        var localOnly = new LocalOnlySwitch();
        localOnly.Engage();

        using var home = localOnly.Track(ViaHome);
        using var device = localOnly.Track(OnDevice);

        Assert.True(home.IsDiscarded);
        Assert.False(device.IsDiscarded);
        Assert.Equal([device], localOnly.Pending);
    }

    [Fact]
    public void FinishedJobsAreNotReported()
    {
        var localOnly = new LocalOnlySwitch();
        var finished = localOnly.Track(Online);
        finished.Dispose();
        var raised = false;
        localOnly.DiscardRequested += (_, _) => raised = true;

        var discarded = localOnly.Engage();

        Assert.Empty(discarded);
        Assert.False(raised);
        Assert.False(finished.IsDiscarded);
    }

    [Fact]
    public void ReleasingAllowsNewJobsButDiscardedJobsStayDiscarded()
    {
        var localOnly = new LocalOnlySwitch();
        using var before = localOnly.Track(Online);
        localOnly.Engage();
        localOnly.Release();

        using var after = localOnly.Track(Online);

        Assert.False(localOnly.IsEngaged);
        Assert.True(before.IsDiscarded);
        Assert.False(after.IsDiscarded);
        Assert.Equal([after], localOnly.Pending);
    }

    [Fact]
    public void EndpointOutsideTheDeviceCountsAsExternalEvenOnADeviceRoute()
    {
        // Eine Anfrage mit falschem Endpunkt verweigert die Richtlinie; wartet sie trotzdem, wird sie verworfen
        var localOnly = new LocalOnlySwitch();
        using var job = localOnly.Track(OnDevice with { TransformEndpoint = Home });

        localOnly.Engage();

        Assert.True(job.IsDiscarded);
    }

    [Fact]
    public void ThrowingCancellationCallbackDoesNotStopDiscardingOtherJobs()
    {
        // Codex-Review R3: ein werfender Rückruf darf weder weitere Abbrüche noch die Meldung verhindern (SI-07)
        var localOnly = new LocalOnlySwitch();
        using var first = localOnly.Track(ViaHome);
        using var second = localOnly.Track(ViaHome);
        using var firstCallback = first.DiscardToken.Register(() => throw new InvalidOperationException("Rückruf 1"));
        using var secondCallback = second.DiscardToken.Register(() => throw new InvalidOperationException("Rückruf 2"));
        DiscardRequestedEventArgs? reported = null;
        localOnly.DiscardRequested += (_, e) => reported = e;

        var exception = Assert.Throws<LocalOnlyDiscardException>(() => localOnly.Engage());

        Assert.True(localOnly.IsEngaged);
        Assert.True(first.IsDiscarded);
        Assert.True(second.IsDiscarded);
        Assert.Empty(localOnly.Pending);
        Assert.NotNull(reported);
        Assert.Equal(2, reported.Jobs.Count);
        Assert.Equal(reported.Jobs, exception.Discarded);
        Assert.Equal(2, exception.Flatten().InnerExceptions.Count);
    }

    [Fact]
    public void ThrowingEventHandlerDoesNotStopOtherHandlers()
    {
        var localOnly = new LocalOnlySwitch();
        using var job = localOnly.Track(Online);
        var secondHandlerCalled = false;
        localOnly.DiscardRequested += (_, _) => throw new InvalidOperationException("Meldung 1");
        localOnly.DiscardRequested += (_, _) => secondHandlerCalled = true;

        var exception = Assert.Throws<LocalOnlyDiscardException>(() => localOnly.Engage());

        Assert.True(secondHandlerCalled);
        Assert.True(job.IsDiscarded);
        Assert.Equal([job], exception.Discarded);
    }
}
