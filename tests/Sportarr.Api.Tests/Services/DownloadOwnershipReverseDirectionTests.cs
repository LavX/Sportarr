using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.Services;

public class DownloadOwnershipReverseDirectionTests
{
    [Fact]
    public async Task ExternalAuditNotCommitted_ActualManualGrabWaitsBeforeClientAdd()
    {
        await using var rig = await DownloadOwnershipRaceHttpHarness.CreateAsync();
        rig.ClientTransport.PublishCompleted();
        rig.ClientTransport.AddOrdinalOffset = 1;
        rig.Saves.PauseNextExternalAuditSave();
        var detection = rig.DetectExternalAsync();
        await rig.Saves.ExternalAuditSave.Entered.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var held = await rig.ReadAsync();
        Assert.Single(held.Files);
        Assert.Empty(held.Pending); Assert.Empty(held.Queue); Assert.Empty(held.Grabs);
        var auditAtClientAdd = new TaskCompletionSource<PendingImport>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.ClientTransport.BeforeAdd = async () =>
        {
            var state = await rig.ReadAsync();
            var audit = Assert.Single(state.Pending);
            Assert.Equal(PendingImportStatus.Completed, audit.Status);
            Assert.Equal(DownloadOwnershipRaceHttpHarness.JobId, audit.DownloadId);
            auditAtClientAdd.TrySetResult(audit);
        };
        var grab = rig.GrabAsync();
        await rig.WaitForBlockedManualAcquisitionAsync(grab);
        Assert.Equal(0, rig.ClientTransport.Adds);
        Assert.False(grab.IsCompleted);
        Assert.Empty((await rig.ReadAsync()).Pending);
        rig.Saves.ExternalAuditSave.Release();
        await Task.WhenAll(detection, grab).WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var witnessedAudit = await auditAtClientAdd.Task.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var completed = await rig.ReadAsync();
        Assert.Equal(witnessedAudit.Id, Assert.Single(completed.Pending).Id);
        Assert.Equal("ownership-job-2", Assert.Single(completed.Queue).DownloadId);
        Assert.Single(completed.Grabs);
        var file = Assert.Single(completed.Files);
        Assert.Equal(rig.Payload, await File.ReadAllBytesAsync(file.FilePath));
        Assert.Equal(1, rig.ClientTransport.Adds);
        Assert.False(rig.Ownership.HasAcquisitions);
        Assert.Empty(rig.ClientTransport.UnexpectedRequests);
    }

    [Fact]
    public async Task ManualOwnerCommitsDuringActualFileScan_FreshDecisionSkipsExternalImport()
    {
        await using var rig = await DownloadOwnershipRaceHttpHarness.CreateAsync();
        rig.ClientTransport.PublishCompleted();
        rig.Analysis.PauseNextScanQuery();
        var detection = rig.DetectExternalAsync();
        await rig.Analysis.ScanQuery.Entered.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        Assert.NotNull(rig.Analysis.CapturedCommand);
        await rig.GrabAsync().WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var owned = await rig.ReadAsync();
        Assert.Equal(DownloadOwnershipRaceHttpHarness.JobId, Assert.Single(owned.Queue).DownloadId);
        Assert.Equal("Main Card", Assert.Single(owned.Grabs).PartName);
        Assert.Empty(owned.Files); Assert.Empty(owned.Pending);
        rig.Analysis.ScanQuery.Release();
        await detection.WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var after = await rig.ReadAsync();
        Assert.Equal(Assert.Single(owned.Queue).Id, Assert.Single(after.Queue).Id);
        Assert.Single(after.Grabs);
        Assert.Empty(after.Files); Assert.Empty(after.Pending); Assert.Equal(0, after.Imports);
        await rig.ImportOwnedAsync().WaitAsync(DownloadOwnershipRaceHttpHarness.TestTimeout);
        var imported = await rig.ReadAsync();
        Assert.Equal(DownloadStatus.Imported, Assert.Single(imported.Queue).Status);
        var file = Assert.Single(imported.Files);
        Assert.Equal("Main Card", file.PartName);
        Assert.Equal(rig.Payload, await File.ReadAllBytesAsync(file.FilePath));
        Assert.Empty(imported.Pending);
        Assert.Equal(1, rig.ClientTransport.Adds);
        Assert.Empty(rig.ClientTransport.UnexpectedRequests);
    }
}
