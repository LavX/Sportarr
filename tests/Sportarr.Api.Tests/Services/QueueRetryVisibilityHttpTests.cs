using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Models;
using Xunit.Abstractions;

namespace Sportarr.Api.Tests.Services;

[Collection(QueueRetryReadbackCollection.Name)]
public sealed class QueueRetryVisibilityHttpTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(DownloadStatus.ImportWarning, true, true, 100, true)]
    [InlineData(DownloadStatus.ImportWarning, true, true, 99, false)]
    [InlineData(DownloadStatus.ImportWarning, false, true, 100, false)]
    [InlineData(DownloadStatus.ImportWarning, true, false, 100, false)]
    [InlineData(DownloadStatus.Failed, false, false, 100, true)]
    [InlineData(DownloadStatus.Failed, false, false, 99, false)]
    [InlineData(DownloadStatus.Imported, true, true, 100, false)]
    public async Task ActualQueueResponseExposesRetryEligibilityWithoutChangingOwnedState(
        DownloadStatus status, bool isPack, bool recognized, int progress, bool expected)
    {
        await using var rig = await PackMemberLifecycleHarness.CreateAsync(multipart: false,
            title: "Queue eligibility event", sport: "American Football", leagueName: "NFL");
        var client = await rig.Db.DownloadClients.SingleAsync();
        var row = new DownloadQueueItem
        {
            EventId = rig.Event.Id, DownloadClientId = client.Id, DownloadId = "owned-visibility-job",
            Title = "NFL.2020.Week1.720p.WEB-DL.H264-QueueVisibility", Protocol = "Torrent",
            Quality = "WEBDL-720p", IsPack = isPack, PackGroupId = isPack ? Guid.NewGuid() : null,
            Status = status, Progress = progress, RetryCount = 2, ImportRetryCount = 3,
            ErrorMessage = recognized ? "Pack member unresolved: No member uniquely identifies this event." : "Other import warning",
            CompletedAt = progress == 100 ? DateTime.UtcNow.AddMinutes(-1) : null,
            ImportedAt = status == DownloadStatus.Imported ? DateTime.UtcNow : null
        };
        rig.Db.DownloadQueue.Add(row);
        await rig.Db.SaveChangesAsync();
        var before = await SnapshotAsync(rig, row.Id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await rig.Client.GetAsync("/api/queue", deadline.Token);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        output.WriteLine(JsonSerializer.Serialize(new { phase = "queue-readback", path = "/api/queue",
            status = (int)response.StatusCode, body, expected, input = new { status, isPack, recognized, progress } }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        var item = Assert.Single(document.RootElement.EnumerateArray().ToArray());
        Assert.Equal(row.Id, item.GetProperty("id").GetInt32());
        Assert.Equal(rig.Event.Id, item.GetProperty("eventId").GetInt32());
        Assert.Equal((int)status, item.GetProperty("status").GetInt32());
        Assert.Equal((double)progress, item.GetProperty("progress").GetDouble());
        var after = await SnapshotAsync(rig, row.Id);
        var calls = rig.Transport.Calls.ToArray();
        var unexpected = rig.Transport.UnexpectedRequests.ToArray();
        output.WriteLine(JsonSerializer.Serialize(new { phase = "queue-no-mutation", before, after,
            clientAdds = rig.Transport.ClientAdds, calls, unexpected }));
        Assert.Equal(before, after);
        Assert.Empty(calls);
        Assert.Equal(0, rig.Transport.ClientAdds);
        Assert.Empty(unexpected);
        Assert.True(item.TryGetProperty("canRetryImport", out var eligibility), body);
        Assert.True(eligibility.ValueKind is JsonValueKind.True or JsonValueKind.False, body);
        Assert.Equal(expected, eligibility.GetBoolean());
    }

    private static async Task<string> SnapshotAsync(PackMemberLifecycleHarness rig, int id)
    {
        var queue = await rig.Db.DownloadQueue.AsNoTracking().SingleAsync(x => x.Id == id);
        var evt = await rig.Db.Events.AsNoTracking().SingleAsync(x => x.Id == queue.EventId);
        var queueValues = rig.Db.Entry(queue).Properties.OrderBy(x => x.Metadata.Name)
            .ToDictionary(x => x.Metadata.Name, x => x.CurrentValue);
        var eventValues = rig.Db.Entry(evt).Properties.OrderBy(x => x.Metadata.Name)
            .ToDictionary(x => x.Metadata.Name, x => x.CurrentValue);
        return JsonSerializer.Serialize(new { queueValues, eventValues,
            queueCount = await rig.Db.DownloadQueue.CountAsync(),
            files = await rig.Db.EventFiles.CountAsync(), imports = await rig.Db.ImportHistories.CountAsync(),
            grabs = await rig.Db.GrabHistory.CountAsync(), blocklist = await rig.Db.Blocklist.CountAsync() });
    }
}

// Keep the bounded response-read cohort separate from parallel fixtures.
[CollectionDefinition(QueueRetryReadbackCollection.Name, DisableParallelization = true)]
public sealed class QueueRetryReadbackCollection
{
    public const string Name = "Queue retry readback fixtures";
}
