using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.Services;

public class RegrabMissingNeedHttpTests
{
    [Fact]
    public async Task TwoImportedOwnersOfDeletedPath_QueueOnlyNewestAndCannotFallbackOnRepeat()
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var file = await rig.AddFileAsync();
        var older = rig.History(file.FilePath, "older-owner");
        var newest = rig.History(file.FilePath, "newest-owner"); newest.GrabbedAt = older.GrabbedAt.AddHours(1);
        rig.Db.GrabHistory.AddRange(older, newest); await rig.Db.SaveChangesAsync();
        await rig.DeleteAsync(file);
        (await rig.Db.GrabHistory.AsNoTracking().ToListAsync()).Should().OnlyContain(g => !g.FileExists && !g.Superseded);
        var result = await rig.BatchAsync(""); result.GetProperty("regrabbed").GetInt32().Should().Be(1);
        result.GetProperty("failed").GetInt32().Should().Be(0);
        (await rig.Db.DownloadQueue.AsNoTracking().SingleAsync()).Title.Should().Be(newest.Title);
        rig.ClientAdds.Should().Be(1);
        (await Stored(rig, newest.Id)).RegrabCount.Should().Be(1);
        (await Stored(rig, older.Id)).RegrabCount.Should().Be(0);
        (await rig.BatchAsync("")).GetProperty("regrabbed").GetInt32().Should().Be(0);
        rig.ClientAdds.Should().Be(1);
        (await rig.Db.DownloadQueue.AsNoTracking().CountAsync()).Should().Be(1);
        (await Stored(rig, older.Id)).LastRegrabAttempt.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LatestOwnerCooldown_IsAppliedBeforeAnyOlderOwnerCanBeSelected(bool recent)
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var file = await rig.AddFileAsync();
        var older = rig.History(file.FilePath, "cooldown-older");
        var latest = rig.History(file.FilePath, "cooldown-latest"); latest.GrabbedAt = older.GrabbedAt.AddHours(1);
        latest.LastRegrabAttempt = DateTime.UtcNow.AddMinutes(recent ? -1 : -10); latest.RegrabCount = 2;
        rig.Db.GrabHistory.AddRange(older, latest); await rig.Db.SaveChangesAsync(); await rig.DeleteAsync(file);
        var result = await rig.BatchAsync(""); result.GetProperty("regrabbed").GetInt32().Should().Be(recent ? 0 : 1);
        rig.ClientAdds.Should().Be(recent ? 0 : 1);
        var rows = await rig.Db.DownloadQueue.AsNoTracking().ToListAsync();
        if (recent) rows.Should().BeEmpty(); else rows.Single().Title.Should().Be(latest.Title);
        (await Stored(rig, older.Id)).RegrabCount.Should().Be(0);
        (await Stored(rig, older.Id)).LastRegrabAttempt.Should().BeNull();
        (await Stored(rig, latest.Id)).RegrabCount.Should().Be(recent ? 2 : 3);
    }

    [Fact]
    public async Task EqualGrabTimestamps_SelectHighestHistoryIdForTheSameNeed()
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync(); var file = await rig.AddFileAsync();
        var first = rig.History(file.FilePath, "tie-first"); var second = rig.History(file.FilePath, "tie-second");
        second.GrabbedAt = first.GrabbedAt;
        rig.Db.GrabHistory.Add(first); await rig.Db.SaveChangesAsync();
        rig.Db.GrabHistory.Add(second); await rig.Db.SaveChangesAsync(); second.Id.Should().BeGreaterThan(first.Id);
        await rig.DeleteAsync(file);
        (await rig.BatchAsync("")).GetProperty("regrabbed").GetInt32().Should().Be(1);
        (await rig.Db.DownloadQueue.AsNoTracking().SingleAsync()).Title.Should().Be(second.Title);
        (await Stored(rig, first.Id)).RegrabCount.Should().Be(0);
    }

    [Fact]
    public async Task DistinctPartPathsAndDifferentEvents_RemainDistinctNeeds()
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var main = await rig.AddFileAsync(); var prelims = await rig.AddFileAsync("other-prelims.mkv", part: "Prelims");
        var otherEvent = new Event { Title = "UFC 9998", Sport = "Fighting", LeagueId = rig.Event.LeagueId, EventDate = rig.Event.EventDate.AddDays(-7) };
        rig.Db.Events.Add(otherEvent); await rig.Db.SaveChangesAsync();
        var older = rig.History(main.FilePath, "distinct-older"); var latest = rig.History(main.FilePath, "distinct-latest"); latest.GrabbedAt = older.GrabbedAt.AddHours(1);
        var otherPart = rig.History(prelims.FilePath, "distinct-prelims"); otherPart.PartName = "Prelims";
        var unrelated = rig.History(main.FilePath, "distinct-event", otherEvent.Id); unrelated.FileExists = false;
        rig.Db.GrabHistory.AddRange(older, latest, otherPart, unrelated); await rig.Db.SaveChangesAsync();
        await rig.DeleteAsync(main); await rig.DeleteAsync(prelims);
        (await rig.BatchAsync("")).GetProperty("regrabbed").GetInt32().Should().Be(3);
        var rows = await rig.Db.DownloadQueue.AsNoTracking().ToListAsync();
        rows.Select(r => r.Title).Should().BeEquivalentTo(new[] { latest.Title, otherPart.Title, unrelated.Title });
        rows.Single(r => r.Title == otherPart.Title).Part.Should().Be("Prelims");
        rows.Single(r => r.Title == unrelated.Title).EventId.Should().Be(otherEvent.Id);
        rig.ClientAdds.Should().Be(3); (await Stored(rig, older.Id)).RegrabCount.Should().Be(0);
    }

    [Fact]
    public async Task LimitIsAppliedAfterDuplicateOwnersAreRemoved()
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var firstFile = await rig.AddFileAsync(); var secondFile = await rig.AddFileAsync("second-need.mkv");
        var secondNeed = rig.History(secondFile.FilePath, "limit-second-need");
        var firstOlder = rig.History(firstFile.FilePath, "limit-first-older"); firstOlder.GrabbedAt = secondNeed.GrabbedAt.AddHours(1);
        var firstLatest = rig.History(firstFile.FilePath, "limit-first-latest"); firstLatest.GrabbedAt = secondNeed.GrabbedAt.AddHours(2);
        rig.Db.GrabHistory.AddRange(secondNeed, firstOlder, firstLatest); await rig.Db.SaveChangesAsync();
        await rig.DeleteAsync(firstFile); await rig.DeleteAsync(secondFile);
        (await rig.BatchAsync("?limit=2")).GetProperty("regrabbed").GetInt32().Should().Be(2);
        (await rig.Db.DownloadQueue.AsNoTracking().OrderBy(q => q.Id).Select(q => q.Title).ToListAsync()).Should().Equal(firstLatest.Title, secondNeed.Title);
        (await Stored(rig, firstOlder.Id)).RegrabCount.Should().Be(0); rig.ClientAdds.Should().Be(2);
    }

    [Fact]
    public async Task SuccessfulRecycleMove_PreservesBytesAndMarksEveryExactOwnerMissing()
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync(); var file = await rig.AddFileAsync();
        var recycle = Path.Combine(rig.DirectoryPath, "recycle-success"); Directory.CreateDirectory(recycle);
        await rig.SetRecycleBinAsync(recycle); var bytes = await File.ReadAllBytesAsync(file.FilePath);
        var first = rig.History(file.FilePath, "recycle-first"); var second = rig.History(file.FilePath, "recycle-second");
        rig.Db.GrabHistory.AddRange(first, second); await rig.Db.SaveChangesAsync();
        var expected = (await rig.Db.GrabHistory.AsNoTracking().ToListAsync()).ToDictionary(g => g.Id, g => JsonSerializer.SerializeToNode(g)!.AsObject());
        foreach (var row in expected.Values) row[nameof(GrabHistory.FileExists)] = false;
        await rig.DeleteAsync(file);
        File.Exists(file.FilePath).Should().BeFalse(); (await rig.Db.EventFiles.AsNoTracking().CountAsync()).Should().Be(0);
        var recycledFiles = Directory.GetFiles(recycle); recycledFiles.Should().ContainSingle();
        var destination = recycledFiles.Single();
        (await File.ReadAllBytesAsync(destination)).Should().Equal(bytes);
        foreach (var id in expected.Keys) JsonNode.DeepEquals(expected[id], JsonSerializer.SerializeToNode(await Stored(rig, id))).Should().BeTrue();
        (await rig.ReadAsync($"/api/grab-history/{first.Id}")).GetProperty("fileExists").GetBoolean().Should().BeFalse();
        (await rig.ReadAsync("/api/grab-history?missingOnly=true")).GetProperty("totalRecords").GetInt32().Should().Be(2);
        (await rig.Db.EventFileHistory.AsNoTracking().SingleAsync()).Reason.Should().Be("Deleted by user");
        rig.ClientAdds.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public async Task MissingDestinationIdentity_RemainsRowSpecific(string? path)
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var first = rig.History(path, "legacy-first"); var second = rig.History(path, "legacy-second");
        first.FileExists = false; second.FileExists = false;
        rig.Db.GrabHistory.AddRange(first, second); await rig.Db.SaveChangesAsync();
        (await rig.BatchAsync("")).GetProperty("regrabbed").GetInt32().Should().Be(2);
        rig.ClientAdds.Should().Be(2);
    }

    [Theory]
    [InlineData("superseded")]
    [InlineData("no-url")]
    [InlineData("not-imported")]
    public async Task NewerOwnerOutsideBaseEligibility_DoesNotSuppressUsableOlderOwner(string exclusion)
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync(); var file = await rig.AddFileAsync();
        var older = rig.History(file.FilePath, "eligible-older");
        var newer = rig.History(file.FilePath, "excluded-newer"); newer.GrabbedAt = older.GrabbedAt.AddHours(1);
        if (exclusion == "superseded") newer.Superseded = true;
        if (exclusion == "no-url") newer.DownloadUrl = "";
        if (exclusion == "not-imported") newer.WasImported = false;
        rig.Db.GrabHistory.AddRange(older, newer); await rig.Db.SaveChangesAsync(); await rig.DeleteAsync(file);
        (await rig.BatchAsync("")).GetProperty("regrabbed").GetInt32().Should().Be(1);
        (await rig.Db.DownloadQueue.AsNoTracking().SingleAsync()).Title.Should().Be(older.Title);
        (await Stored(rig, newer.Id)).RegrabCount.Should().Be(0); rig.ClientAdds.Should().Be(1);
    }

    [Fact]
    public async Task DistinctNeedsWithEqualGrabTimes_UseDescendingHistoryIdBeforeLimit()
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var first = rig.History(Path.Combine(rig.DirectoryPath, "tie-need-first.mkv"), "tie-need-first");
        var second = rig.History(Path.Combine(rig.DirectoryPath, "tie-need-second.mkv"), "tie-need-second");
        first.FileExists = false; second.FileExists = false; second.GrabbedAt = first.GrabbedAt;
        rig.Db.GrabHistory.Add(first); await rig.Db.SaveChangesAsync();
        rig.Db.GrabHistory.Add(second); await rig.Db.SaveChangesAsync();
        (await rig.BatchAsync()).GetProperty("regrabbed").GetInt32().Should().Be(1);
        (await rig.Db.DownloadQueue.AsNoTracking().SingleAsync()).Title.Should().Be(second.Title);
        (await Stored(rig, first.Id)).RegrabCount.Should().Be(0);
    }

    private static Task<GrabHistory> Stored(RegrabMissingHttpHarness rig, int id) =>
        rig.Db.GrabHistory.AsNoTracking().SingleAsync(g => g.Id == id);
}
