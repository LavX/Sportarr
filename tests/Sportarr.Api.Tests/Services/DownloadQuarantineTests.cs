using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class DownloadQuarantineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(true, true)]
    public async Task Quarantined_download_cannot_import_until_recovered(bool knownOwner, bool reboundId = false)
    {
        await using var rig = await DownloadOwnershipRaceHttpHarness.CreateAsync();
        if (knownOwner) await rig.GrabAsync();
        if (reboundId) await rig.WithDatabaseAsync(async db =>
        {
            (await db.DownloadQueue.SingleAsync()).DownloadId = "old-client-id";
            (await db.DownloadClients.SingleAsync()).Type = Sportarr.Api.Models.DownloadClientType.QBittorrent;
            await db.SaveChangesAsync();
        });
        rig.ClientTransport.PublishCompleted();
        var clientId = 0;
        await rig.WithDatabaseAsync(async db => clientId = (await db.DownloadClients.SingleAsync()).Id);
        using var quarantine = rig.Ownership.QuarantineDownload(clientId, DownloadOwnershipRaceHttpHarness.JobId);
        await (knownOwner ? rig.MonitorAsync() : rig.DetectExternalAsync());
        var protectedState = await rig.ReadAsync();
        Assert.Empty(protectedState.Files);
        Assert.Empty(protectedState.Pending);
        Assert.True(File.Exists(rig.SourcePath));
        Assert.Null(rig.Ownership.TryEnterExternalDecision(clientId, DownloadOwnershipRaceHttpHarness.JobId));
        using (var unrelated = rig.Ownership.TryEnterExternalDecision(clientId + 1, DownloadOwnershipRaceHttpHarness.JobId))
            Assert.NotNull(unrelated);
        quarantine.Dispose();
        await (knownOwner ? rig.MonitorAsync() : rig.DetectExternalAsync());
        Assert.Single((await rig.ReadAsync()).Files);
    }

    [Fact]
    public async Task Event_gate_cancellation_preserves_the_owner_without_blocking_other_event_ids()
    {
        var coordinator = new DownloadOwnershipCoordinator();
        using var first = await coordinator.EnterEventDecisionAsync(1);
        using var other = await coordinator.EnterEventDecisionAsync(257).WaitAsync(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.EnterEventDecisionAsync(1, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        using var next = await coordinator.EnterEventDecisionAsync(1).WaitAsync(TimeSpan.FromSeconds(1));
    }
}
