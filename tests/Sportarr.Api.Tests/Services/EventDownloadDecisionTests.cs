using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class EventDownloadDecisionTests
{
    [Fact]
    public async Task SameEventWaitsForOwnershipButAnotherEventCanProceed()
    {
        var coordinator = new DownloadOwnershipCoordinator();
        using var first = await coordinator.EnterEventDecisionAsync(1);
        var second = coordinator.EnterEventDecisionAsync(1);
        Assert.False(second.IsCompleted);
        using var other = await coordinator.EnterEventDecisionAsync(2).WaitAsync(TimeSpan.FromSeconds(2));
        first.Dispose();
        using var next = await second.WaitAsync(TimeSpan.FromSeconds(2));
        first.Dispose();
        var third = coordinator.EnterEventDecisionAsync(1);
        Assert.False(third.IsCompleted);
        next.Dispose();
        using var last = await third.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelledWaitDoesNotReleaseTheCurrentOwnerOrBlockLaterAcquisition()
    {
        var coordinator = new DownloadOwnershipCoordinator();
        using var first = await coordinator.EnterEventDecisionAsync(1);
        using var cancellation = new CancellationTokenSource();
        var cancelled = coordinator.EnterEventDecisionAsync(1, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var next = coordinator.EnterEventDecisionAsync(1);
        Assert.False(next.IsCompleted);
        first.Dispose();
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
