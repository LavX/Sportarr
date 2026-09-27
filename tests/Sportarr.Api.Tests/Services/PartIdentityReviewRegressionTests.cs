using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class PartIdentityReviewRegressionTests
{
    [Theory]
    [InlineData("opaque.720p.WEB-DL.mkv", null, null)]
    [InlineData("UFC.9999.2020.09.01.Prelims.720p.WEB-DL.mkv", "Prelims", 2)]
    public async Task ReconstructedPackQueue_UsesChildIdentityAfterFlagLoss(string basename, string? expected, int? number)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        await rig.GrabAsync(rig.Release("UFC.2020.Season.Pack.Main.Card.720p.WEB-DL", isPack: true));
        var history = await rig.Db.GrabHistory.SingleAsync();
        var restored = new DownloadQueueItem { EventId = history.EventId, Title = history.Title,
            Part = history.PartName, Quality = history.Quality, Protocol = history.Protocol,
            DownloadId = "reconstructed-pack", Status = DownloadStatus.Completed };
        rig.Db.DownloadQueue.Add(restored); await rig.Db.SaveChangesAsync();
        restored.IsPack.Should().BeFalse("the shipped history reconstruction has no persisted pack field");
        var file = await rig.ImportAsync(restored.Title, basename, acquiredQueue: restored);
        file.PartName.Should().Be(expected); file.PartNumber.Should().Be(number);
    }

    [Fact]
    public async Task InferredImport_ReconcilesReplacedHistoryByResolvedSlot()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        rig.Settings.StandardFileFormat = "held-{Event Title}{Part}"; await rig.Db.SaveChangesAsync();
        var held = await rig.ImportAsync("Old selected main", "held-main.720p.WEB-DL.mkv", "Main Card");
        var oldMain = History(rig.Event.Id, "Previous Main Card release", "Main Card", held.FilePath);
        var full = History(rig.Event.Id, "Separate full-event history", null, held.FilePath + ".full");
        rig.Db.GrabHistory.AddRange(oldMain, full);
        rig.Settings.StandardFileFormat = "new-{Event Title}{Part}"; await rig.Db.SaveChangesAsync();
        var file = await rig.ImportAsync("UFC.9999.2020.09.01.Main.Card.720p.WEB-DL",
            "UFC.9999.2020.09.01.Main.Card.720p.WEB-DL.mkv");
        file.PartName.Should().Be("Main Card"); file.FilePath.Should().NotBe(held.FilePath);
        File.Exists(held.FilePath).Should().BeFalse();
        oldMain.FileExists.Should().BeFalse("its Main Card file was replaced");
        full.FileExists.Should().BeTrue("an inferred part must not reconcile the unrelated null slot");
    }

    [Theory]
    [InlineData("WEBDL-720p", "720p")]
    [InlineData("WEBDL-2160p", "2160p")]
    public async Task InferredPartialImport_CoexistsWithExistingFullCoverage(string quality, string token)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var full = await rig.ImportAsync("UFC.9999.Full.Event", "full-event.1080p.WEB-DL.mkv", "Full Event");
        full.Quality = "WEBDL-1080p"; rig.Event.Quality = full.Quality;
        await rig.Db.SaveChangesAsync();
        var fullBytes = await File.ReadAllBytesAsync(full.FilePath);
        var sourceDirectory = Path.Combine(Path.GetTempPath(), "sportarr-part-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        try
        {
            var basename = $"UFC.9999.2020.09.01.Main.Card.{token}.WEB-DL.mkv";
            var source = Path.Combine(sourceDirectory, basename);
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)'p', 4096).ToArray());
            var row = new DownloadQueueItem { EventId = rig.Event.Id, Title = Path.GetFileNameWithoutExtension(basename),
                DownloadId = "partial-over-full", Status = DownloadStatus.Completed, Quality = quality, Protocol = "Usenet" };
            rig.Db.DownloadQueue.Add(row); await rig.Db.SaveChangesAsync();
            var result = await rig.Services.GetRequiredService<FileImportService>().ImportDownloadAsync(row, source, PostImportMode.Copy);
            result.Should().NotBeNull("a distinct part can coexist with full-event coverage");
            var files = await rig.Db.EventFiles.ToListAsync();
            files.Should().HaveCount(2);
            files.Single(f => f.Id != full.Id).PartName.Should().Be("Main Card");
            (await File.ReadAllBytesAsync(full.FilePath)).Should().Equal(fullBytes);
            rig.Event.FilePath.Should().Be(full.FilePath); rig.Event.Quality.Should().Be("WEBDL-1080p");
            rig.Event.HasFile.Should().BeTrue(); File.Exists(source).Should().BeTrue();
        }
        finally { Directory.Delete(sourceDirectory, true); }
    }

    private static GrabHistory History(int eventId, string title, string? part, string path) => new()
    {
        EventId = eventId, Title = title, PartName = part, Indexer = "Part fixture",
        DownloadUrl = "http://part-source.invalid/old.nzb", Guid = title, Protocol = "Usenet",
        WasImported = true, FileExists = true, DestinationPath = path, GrabbedAt = DateTime.UtcNow.AddDays(-1)
    };
}
