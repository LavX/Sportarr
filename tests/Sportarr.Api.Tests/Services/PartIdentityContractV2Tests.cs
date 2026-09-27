using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class PartIdentityContractV2Tests
{
    [Theory]
    [InlineData(false, "UFC.9999.2020.09.01.Main.Card.720p.WEB-DL")]
    [InlineData(true, "UFC.9999.2020.09.01.Main.Card.720p.WEB-DL")]
    [InlineData(false, "UFC.9999.2020.09.01.Main.Card.COMPLETE.BLURAY")]
    public async Task AcquiredSingleEvent_PreservesStoredPartThroughOpaqueRename(bool automatic, string title)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        await Acquire(rig, title, automatic);
        var queue = await rig.Db.DownloadQueue.SingleAsync();
        queue.Part.Should().Be("Main Card"); queue.IsPack.Should().BeFalse();
        (await rig.Db.GrabHistory.SingleAsync()).PartName.Should().Be("Main Card");
        var file = await rig.ImportAsync(title, "opaque.720p.WEB-DL.mkv", acquiredQueue: queue);
        file.PartName.Should().Be("Main Card"); file.PartNumber.Should().Be(3);
        Path.GetFileName(file.FilePath).Should().Contain("pt3");
        rig.Event.HasFile.Should().BeFalse();
    }

    [Fact]
    public async Task BluraySpecimen_DefaultProfileRejectsItsActualParsedQuality()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var release = rig.Release("UFC.9999.2020.09.01.Main.Card.COMPLETE.BLURAY");
        var profile = await rig.Db.QualityProfiles.SingleAsync();
        var evaluation = rig.Services.GetRequiredService<ReleaseEvaluator>().EvaluateRelease(release, profile);
        evaluation.Quality.Should().Be("Bluray-1080p");
        evaluation.Rejections.Should().Contain("Quality Bluray-1080p is not wanted in quality profile");
        var result = await rig.AutomaticAsync(release);
        result.Success.Should().BeFalse(); rig.Transport.ClientAdds.Should().Be(0);
    }

    [Fact]
    public async Task PermittedBluraySpecimen_AcquiresStoredPartBeforeOpaqueImport()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var profile = await rig.Db.QualityProfiles.SingleAsync();
        profile.Items.Add(new QualityItem { Name = "Bluray-1080p", Quality = 7, Allowed = true });
        await rig.Db.SaveChangesAsync();
        const string title = "UFC.9999.2020.09.01.Main.Card.COMPLETE.BLURAY";
        await Acquire(rig, title, automatic: true);
        var queue = await rig.Db.DownloadQueue.SingleAsync();
        queue.Part.Should().Be("Main Card"); queue.IsPack.Should().BeFalse();
        (await rig.Db.GrabHistory.SingleAsync()).PartName.Should().Be("Main Card");
        var file = await rig.ImportAsync(title, "opaque.720p.WEB-DL.mkv", acquiredQueue: queue);
        file.PartName.Should().Be("Main Card"); file.PartNumber.Should().Be(3);
    }

    [Fact]
    public async Task CompleteEventMarker_DoesNotPermitASecondActiveWholeEventGrab()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        const string title = "UFC.9999.Complete.Event.720p.WEB-DL";
        await Acquire(rig, title, automatic: true);
        (await rig.Db.DownloadQueue.SingleAsync()).Part.Should().Be("Full Event");
        var result = await rig.AutomaticAsync(rig.Release(title, suffix: "another-source"));
        result.Success.Should().BeFalse(); rig.Transport.ClientAdds.Should().Be(1);
        (await rig.Db.DownloadQueue.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(false, "opaque.720p.WEB-DL.mkv")]
    [InlineData(true, "opaque.720p.WEB-DL.mkv")]
    [InlineData(false, "UFC.9999.Main.Card.2020.09.01.720p.WEB-DL.mkv")]
    [InlineData(true, "UFC.9999.PPV.2020.09.01.720p.WEB-DL.mkv")]
    public async Task AcquiredCompleteEvent_PersistsWholeEventIdentityBeforeChildImport(bool automatic, string basename)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        const string title = "UFC.9999.Complete.Event.720p.WEB-DL";
        await Acquire(rig, title, automatic);
        var queue = await rig.Db.DownloadQueue.SingleAsync();
        queue.Part.Should().Be("Full Event");
        (await rig.Db.GrabHistory.SingleAsync()).PartName.Should().Be("Full Event");
        var file = await rig.ImportAsync(title, basename, acquiredQueue: queue);
        file.PartName.Should().BeNull(); file.PartNumber.Should().BeNull();
        rig.Event.HasFile.Should().BeTrue();
    }

    [Theory]
    [InlineData("opaque.720p.WEB-DL.mkv", null, null)]
    [InlineData("UFC.9999.2020.09.01.Prelims.720p.WEB-DL.mkv", "Prelims", 2)]
    public async Task ArbitraryPackTitle_AfterFlagLossCannotSupplyChildPart(string basename, string? part, int? number)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        const string title = "UFC.2020.Collection.Main.Card.720p.WEB-DL";
        await rig.GrabAsync(rig.Release(title, isPack: true));
        var history = await rig.Db.GrabHistory.SingleAsync();
        history.PartName.Should().BeNull();
        var restored = new DownloadQueueItem { EventId = history.EventId, Title = history.Title,
            Part = history.PartName, Quality = history.Quality, Protocol = history.Protocol,
            DownloadId = "restored-arbitrary-pack", Status = DownloadStatus.Completed };
        rig.Db.DownloadQueue.Add(restored); await rig.Db.SaveChangesAsync();
        restored.IsPack.Should().BeFalse();
        var file = await rig.ImportAsync(title, basename, acquiredQueue: restored);
        file.PartName.Should().Be(part); file.PartNumber.Should().Be(number);
    }

    [Fact]
    public async Task UnstampedOpaqueImport_CannotTreatParentTitleAsSingleEventProof()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var file = await rig.ImportAsync("UFC.9999.2020.09.01.Main.Card", "opaque.720p.WEB-DL.mkv");
        file.PartName.Should().BeNull(); file.PartNumber.Should().BeNull();
    }

    [Theory]
    [InlineData(false, "WEBDL-720p", "720p", null)]
    [InlineData(true, "WEBDL-720p", "720p", null)]
    [InlineData(false, "WEBDL-2160p", "2160p", null)]
    [InlineData(true, "WEBDL-2160p", "2160p", null)]
    [InlineData(false, "WEBDL-720p", "720p", "Full Event")]
    [InlineData(true, "WEBDL-2160p", "2160p", "Full Event")]
    public async Task DistinctPart_CoexistsWithoutChangingSurvivingFullSummary(bool selected, string quality, string token, string? fullPartName)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var full = await CreateFull(rig, fullPartName);
        var held = await File.ReadAllBytesAsync(full.FilePath);
        var heldSize = rig.Event.FileSize;
        var row = new DownloadQueueItem { EventId = rig.Event.Id,
            Title = $"UFC.9999.2020.09.01.Prelims.{token}.WEB-DL", Part = selected ? "Prelims" : null,
            Quality = quality, Protocol = "Usenet", DownloadId = "partial-coexist", Status = DownloadStatus.Completed,
            IsManualSearch = selected };
        rig.Db.DownloadQueue.Add(row); await rig.Db.SaveChangesAsync();
        var part = await rig.ImportAsync(row.Title, row.Title + ".mkv", acquiredQueue: row);
        part.Id.Should().NotBe(full.Id); part.PartName.Should().Be("Prelims"); part.PartNumber.Should().Be(2);
        part.FilePath.Should().NotBe(full.FilePath); part.Quality.Should().Be(quality);
        (await rig.Db.EventFiles.CountAsync()).Should().Be(2);
        (await File.ReadAllBytesAsync(full.FilePath)).Should().Equal(held);
        rig.Event.FilePath.Should().Be(full.FilePath); rig.Event.FileSize.Should().Be(heldSize);
        rig.Event.Quality.Should().Be("WEBDL-1080p"); rig.Event.HasFile.Should().BeTrue();
        full.Quality.Should().Be("WEBDL-1080p"); full.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task PartDestinationCollision_RefusesBeforeTouchingFullBytesOrRow()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        rig.Settings.StandardFileFormat = "same-name"; await rig.Db.SaveChangesAsync();
        var full = await CreateFull(rig);
        var held = await File.ReadAllBytesAsync(full.FilePath);
        var directory = Path.Combine(Path.GetTempPath(), "sportarr-part-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "UFC.9999.2020.09.01.Prelims.2160p.WEB-DL.mkv");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)'p', 4096).ToArray());
            var row = new DownloadQueueItem { EventId = rig.Event.Id, Title = Path.GetFileNameWithoutExtension(source),
                Part = "Prelims", Quality = "WEBDL-2160p", Protocol = "Usenet", DownloadId = "collision", Status = DownloadStatus.Completed };
            rig.Db.DownloadQueue.Add(row); await rig.Db.SaveChangesAsync();
            var result = await rig.Services.GetRequiredService<FileImportService>().ImportDownloadAsync(row, source, PostImportMode.Copy);
            result.Should().BeNull(); row.Status.Should().Be(DownloadStatus.ImportWarning);
            row.ErrorMessage.Should().NotBeNullOrWhiteSpace();
            (await rig.Db.EventFiles.SingleAsync()).Id.Should().Be(full.Id);
            (await File.ReadAllBytesAsync(full.FilePath)).Should().Equal(held);
            File.Exists(source).Should().BeTrue(); rig.Event.FilePath.Should().Be(full.FilePath);
            rig.Event.Quality.Should().Be("WEBDL-1080p"); rig.Event.HasFile.Should().BeTrue();
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task<EventFile> CreateFull(PartIdentityIntegrationHarness rig, string? storedName = null)
    {
        var full = await rig.ImportAsync("UFC.9999.Full.Event", "full-event.1080p.WEB-DL.mkv", "Full Event");
        full.PartName = storedName; full.PartNumber = null; full.Quality = "WEBDL-1080p";
        rig.Event.Quality = full.Quality; await rig.Db.SaveChangesAsync();
        return full;
    }

    private static async Task Acquire(PartIdentityIntegrationHarness rig, string title, bool automatic)
    {
        if (automatic)
        {
            var result = await rig.AutomaticAsync(rig.Release(title));
            result.Success.Should().BeTrue(result.Message);
        }
        else await rig.GrabAsync(rig.Release(title));
    }
}
