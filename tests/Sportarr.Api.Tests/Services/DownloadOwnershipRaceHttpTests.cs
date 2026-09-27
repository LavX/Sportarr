using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.Services;

public class DownloadOwnershipRaceHttpTests
{
    [Fact]
    public async Task RemoteCompletedBeforeAddReturns_RemainsOwnedByManualGrab()
    {
        await using var rig = await DownloadOwnershipRaceHttpHarness.CreateAsync();
        rig.ClientTransport.PauseAddResponse = true;
        var grab = rig.GrabAsync();
        await rig.ClientTransport.AddResponse.Entered.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        Assert.Empty((await rig.ReadAsync()).Queue);
        await rig.DetectExternalAsync().WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var duringHandoff = await rig.ReadAsync();
        rig.ClientTransport.AddResponse.Release();
        await grab.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        AssertNoExternalMutation(duringHandoff);
        await AssertOwnedImportAsync(rig);
    }

    [Fact]
    public async Task RemoteAddReturnedBeforeQueueCommit_RemainsOwnedByManualGrab()
    {
        await using var rig = await DownloadOwnershipRaceHttpHarness.CreateAsync();
        rig.Saves.PauseNextQueueSave();
        var grab = rig.GrabAsync();
        await rig.Saves.QueueSave.Entered.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        Assert.Equal(1, rig.ClientTransport.Adds);
        var before = await rig.ReadAsync(); Assert.Empty(before.Queue); Assert.Empty(before.Grabs);
        await rig.DetectExternalAsync().WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var duringHandoff = await rig.ReadAsync();
        rig.Saves.QueueSave.Release();
        await grab.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        AssertNoExternalMutation(duringHandoff);
        await AssertOwnedImportAsync(rig);
    }

    [Fact]
    public async Task OwnerCommitsWhileExternalPollWaits_StaleSnapshotCannotAuthorizeImport()
    {
        await using var rig = await DownloadOwnershipRaceHttpHarness.CreateAsync();
        rig.ClientTransport.PauseNextExternalHistory();
        var detection = rig.DetectExternalAsync();
        await rig.ClientTransport.ExternalHistory.Entered.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        await rig.GrabAsync().WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var committed = await rig.ReadAsync();
        Assert.Equal(DownloadOwnershipRaceHttpHarness.JobId, Assert.Single(committed.Queue).DownloadId);
        Assert.Equal("Main Card", Assert.Single(committed.Grabs).PartName);
        rig.ClientTransport.ExternalHistory.Release();
        await detection.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        AssertNoExternalMutation(await rig.ReadAsync());
        await AssertOwnedImportAsync(rig);
    }

    [Fact]
    public async Task GenuineExternalCompletedFile_StillImportsAndRecordsCompletedAudit()
    {
        await using var rig = await DownloadOwnershipRaceHttpHarness.CreateAsync();
        rig.ClientTransport.PublishCompleted();
        await rig.DetectExternalAsync().WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var state = await rig.ReadAsync();
        Assert.Empty(state.Queue); Assert.Empty(state.Grabs); Assert.Equal(0, rig.ClientTransport.Adds);
        var file = Assert.Single(state.Files); Assert.Equal(rig.EventId, file.EventId);
        Assert.Equal(rig.Payload, await File.ReadAllBytesAsync(file.FilePath));
        var audit = Assert.Single(state.Pending);
        Assert.Equal(DownloadOwnershipRaceHttpHarness.JobId, audit.DownloadId);
        Assert.Equal(PendingImportStatus.Completed, audit.Status);
        Assert.Equal(rig.EventId, audit.SuggestedEventId);
        Assert.Empty(rig.ClientTransport.UnexpectedRequests);
    }

    private static void AssertNoExternalMutation(DownloadOwnershipRaceHttpHarness.State state)
    {
        Assert.Empty(state.Files); Assert.Empty(state.Pending); Assert.Equal(0, state.Imports);
    }

    private static async Task AssertOwnedImportAsync(DownloadOwnershipRaceHttpHarness rig)
    {
        await rig.DetectExternalAsync().WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        AssertNoExternalMutation(await rig.ReadAsync());
        await rig.ImportOwnedAsync().WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var state = await rig.ReadAsync();
        var queue = Assert.Single(state.Queue);
        Assert.Equal("Main Card", queue.Part); Assert.Equal(DownloadStatus.Imported, queue.Status);
        Assert.NotNull(queue.ImportedAt);
        var file = Assert.Single(state.Files); Assert.Equal("Main Card", file.PartName); Assert.Equal(3, file.PartNumber);
        Assert.Equal(rig.Payload, await File.ReadAllBytesAsync(file.FilePath));
        var grab = Assert.Single(state.Grabs); Assert.True(grab.WasImported); Assert.Equal("Main Card", grab.PartName);
        Assert.Equal(file.FilePath, grab.DestinationPath);
        Assert.Equal(1, state.Imports); Assert.Empty(state.Pending);
        Assert.Equal(1, rig.ClientTransport.Adds); Assert.Empty(rig.ClientTransport.UnexpectedRequests);
    }
}
