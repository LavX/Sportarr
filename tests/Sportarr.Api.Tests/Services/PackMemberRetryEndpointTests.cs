using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.Services;

public class PackMemberRetryEndpointTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public async Task ActualRetryAllowsRecognizedPackHoldAndExistingFailureOnly(bool isPack, bool failed, bool recognized)
    {
        await using var rig = await PackMemberLifecycleHarness.CreateAsync(rename: false, multipart: false,
            title: "Team A vs Team B", sport: "American Football", leagueName: "NFL");
        rig.Event.ExternalId = "ev-9900301";
        var client = await rig.Db.DownloadClients.SingleAsync();
        var root = (await rig.Db.RootFolders.SingleAsync()).Path;
        var folder = Path.Combine(Path.GetDirectoryName(root)!, "incoming", "NFL.2020.Week1.720p.WEB-DL.H264-PackLifecycle");
        Directory.CreateDirectory(folder);
        var bytes = Enumerable.Repeat((byte)65, 4096).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(folder, "NFL.2020.09.01.Team.A.vs.Team.B.720p.WEB-DL.H264.{sportarr-ev-9900301}.mkv"), bytes);
        rig.Transport.JobPath = folder;
        var row = new DownloadQueueItem { EventId = rig.Event.Id, Event = rig.Event, DownloadClientId = client.Id,
            DownloadClient = client, DownloadId = "owned-pack-job", IsPack = isPack, PackGroupId = isPack ? Guid.NewGuid() : null,
            Title = "NFL.2020.Week1.720p.WEB-DL.H264-PackLifecycle", Quality = "WEBDL-720p", Protocol = "Torrent",
            Status = failed ? DownloadStatus.Failed : DownloadStatus.ImportWarning, CompletedAt = DateTime.UtcNow,
            Progress = 100, RetryCount = 2, ErrorMessage = recognized ? "Pack member unresolved: No member uniquely identifies this event." : "Existing unrelated warning" };
        rig.Db.DownloadQueue.Add(row); await rig.Db.SaveChangesAsync();
        using var response = await rig.Client.PostAsync("/api/queue/" + row.Id + "/retry", null);
        var body = await response.Content.ReadAsStringAsync();
        var allowed = failed || (isPack && recognized);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { route = "/api/queue/" + row.Id + "/retry", status = (int)response.StatusCode, body, allowed }));
        Assert.Equal(allowed, response.IsSuccessStatusCode);
        if (allowed)
        {
            var file = await rig.Db.EventFiles.SingleAsync(f => f.EventId == rig.Event.Id);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file.FilePath));
            Assert.Equal(DownloadStatus.Imported, row.Status);
            Assert.Equal(failed ? 3 : 2, row.RetryCount);
            Assert.Equal("owned-pack-job", row.DownloadId);
        }
        else
        {
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(DownloadStatus.ImportWarning, row.Status);
            Assert.Equal(2, row.RetryCount); Assert.Empty(await rig.Db.EventFiles.ToListAsync());
        }
        Assert.DoesNotContain(rig.Transport.Calls, x => x.Contains("/torrents/add"));
        Assert.Empty(rig.Transport.UnexpectedRequests);
    }
}
