using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.Services;

public class ReplacementSearchPartHttpTests
{
    [Fact]
    public async Task DeleteAll_BlocklistsEveryGrabIdentityAndMarksItsHistoryMissing()
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var prelims = await rig.AddFileAsync("prelims.mkv", part: "Prelims");
        var main = await rig.AddFileAsync("main.mkv", part: "Main Card");
        var prelimsHistory = rig.History(prelims.FilePath, "prelims");
        prelimsHistory.PartName = "Prelims";
        prelimsHistory.Protocol = "Torrent";
        prelimsHistory.Indexer = "Fixture torrent";
        prelimsHistory.TorrentInfoHash = "1111111111111111111111111111111111111111";
        var mainHistory = rig.History(main.FilePath, "main");
        mainHistory.Protocol = "Torrent";
        mainHistory.Indexer = "Fixture torrent";
        mainHistory.TorrentInfoHash = "2222222222222222222222222222222222222222";
        rig.Db.GrabHistory.AddRange(prelimsHistory, mainHistory);
        await rig.Db.SaveChangesAsync();

        using var response = await rig.Client.DeleteAsync(
            $"/api/events/{rig.Event.Id}/files?blocklistAction=blocklistOnly");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await rig.Db.Blocklist.OrderBy(b => b.Part).Select(b => new
        {
            b.Title,
            b.TorrentInfoHash,
            b.Indexer,
            b.Protocol,
            b.Part
        }).ToListAsync()).Should().BeEquivalentTo(new[]
        {
            new { prelimsHistory.Title, prelimsHistory.TorrentInfoHash, prelimsHistory.Indexer, prelimsHistory.Protocol, Part = "Prelims" },
            new { mainHistory.Title, mainHistory.TorrentInfoHash, mainHistory.Indexer, mainHistory.Protocol, Part = "Main Card" }
        });
        (await rig.Db.GrabHistory.ToListAsync()).Should().OnlyContain(history => !history.FileExists);
    }

    [Fact]
    public async Task FileRemoval_BlocklistsTheGrabIdentityAndQueuesTheDeletedPart()
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var file = await rig.AddFileAsync(part: "Prelims");
        var history = rig.History(file.FilePath, "prelims");
        history.PartName = "Prelims";
        history.Protocol = "Torrent";
        history.Indexer = "Fixture torrent";
        history.TorrentInfoHash = "0123456789abcdef0123456789abcdef01234567";
        file.OriginalTitle = history.Title;
        file.ReleaseTitle = history.Title;
        rig.Db.GrabHistory.Add(history);
        await rig.Db.SaveChangesAsync();

        await rig.DeleteAsync(file, blocklistAction: "blocklistAndSearch");

        var blocked = await rig.Db.Blocklist.SingleAsync();
        blocked.Title.Should().Be(history.Title);
        blocked.TorrentInfoHash.Should().Be(history.TorrentInfoHash);
        blocked.Indexer.Should().Be(history.Indexer);
        blocked.Protocol.Should().Be(history.Protocol);
        blocked.Part.Should().Be("Prelims");
    }

    [Theory]
    [InlineData("Prelims", "Main Card", "Prelims")]
    [InlineData(null, "Main Card", "Main Card")]
    public async Task HistoryRemoval_QueuesTheImportedPart(
        string? historyPart, string queuePart, string expectedPart)
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var queue = new DownloadQueueItem
        {
            EventId = rig.Event.Id,
            Title = "UFC.9999.Main.Card.720p.WEB-DL",
            DownloadId = "replacement Dee",
            Status = DownloadStatus.Imported,
            Part = queuePart,
            Protocol = "Usenet",
            Indexer = "Owned fixture"
        };
        rig.Db.DownloadQueue.Add(queue);
        await rig.Db.SaveChangesAsync();
        var history = new ImportHistory
        {
            EventId = rig.Event.Id,
            DownloadQueueItemId = queue.Id,
            SourcePath = Path.Combine(rig.DirectoryPath, "source.mkv"),
            DestinationPath = Path.Combine(rig.DirectoryPath, "destination.mkv"),
            Quality = "WEBDL-720p",
            Decision = ImportDecision.Approved,
            Part = historyPart
        };
        rig.Db.ImportHistories.Add(history);
        await rig.Db.SaveChangesAsync();

        using var response = await rig.Client.DeleteAsync(
            $"/api/history/{history.Id}?blocklistAction=blocklistAndSearch");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await rig.Db.Blocklist.SingleAsync()).Part.Should().Be(expectedPart);
        (await rig.Db.Tasks.SingleAsync()).Body.Should().Be($"{rig.Event.Id}|{expectedPart}");
    }
}
