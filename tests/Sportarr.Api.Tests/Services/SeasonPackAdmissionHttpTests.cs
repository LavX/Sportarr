using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.Services;

public sealed class SeasonPackAdmissionHttpTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SeasonSelectionKeepsExactOwnersAcrossCalendarYears(bool unmonitored, bool hasFile)
    {
        await using var rig = await CreateAsync();
        var selected = await AddEventAsync(rig, "Selected January event", new DateTime(2021, 1, 10), "2020-2021");
        selected.Monitored = !unmonitored;
        selected.HasFile = hasFile;
        var absent = await AddEventAsync(rig, "Selected absent member", new DateTime(2021, 2, 10), "2020-2021");
        var unselected = await AddEventAsync(rig, "Unselected same-season event", new DateTime(2020, 10, 10), "2020-2021");
        await rig.Db.SaveChangesAsync();

        using var response = await rig.Client.PostAsJsonAsync("/api/release/grab",
            ModalBody(rig, new[] { rig.Event.Id, selected.Id, absent.Id }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var rows = await rig.Db.DownloadQueue.AsNoTracking().OrderBy(row => row.EventId).ToListAsync();
        Assert.Equal(new[] { rig.Event.Id, selected.Id, absent.Id }, rows.Select(row => row.EventId));
        Assert.All(rows, row => Assert.True(row.IsPack));
        Assert.All(rows, row => Assert.True(row.IsManualSearch));
        Assert.NotNull(rows[0].PackGroupId);
        Assert.All(rows, row => Assert.Equal(rows[0].PackGroupId, row.PackGroupId));
        Assert.False(string.IsNullOrWhiteSpace(rows[0].DownloadId));
        Assert.All(rows, row => Assert.Equal(rows[0].DownloadId, row.DownloadId));
        Assert.All(rows, row => Assert.Equal(rows[0].DownloadClientId, row.DownloadClientId));
        Assert.DoesNotContain(rows, row => row.EventId == unselected.Id);
        Assert.Empty(await rig.Db.EventFiles.AsNoTracking().ToListAsync());
        Assert.Equal(hasFile, (await rig.Db.Events.AsNoTracking().SingleAsync(e => e.Id == selected.Id)).HasFile);
        Assert.Equal(1, rig.Transport.ClientAdds);
        Assert.Empty(rig.Transport.UnexpectedRequests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("foreign-league")]
    [InlineData("foreign-season")]
    [InlineData("unknown")]
    [InlineData("no-anchor")]
    [InlineData("string-id")]
    [InlineData("not-array")]
    public async Task InvalidSeasonSelectionRejectsBeforeRemoteAdd(string variant)
    {
        await using var rig = await CreateAsync();
        var member = await AddEventAsync(rig, "Second event", new DateTime(2021, 1, 10), "2020-2021");
        var body = ModalBody(rig, new[] { rig.Event.Id, member.Id });
        switch (variant)
        {
            case "missing": body.Remove("matchedEventIds"); break;
            case "empty": body["matchedEventIds"] = new JsonArray(); break;
            case "duplicate": body["matchedEventIds"] = Ids(rig.Event.Id, member.Id, member.Id); break;
            case "foreign-league":
                var league = new League { Name = "Foreign league", Sport = "American Football", Monitored = true };
                rig.Db.Leagues.Add(league);
                await rig.Db.SaveChangesAsync();
                member.LeagueId = league.Id;
                member.League = league;
                break;
            case "foreign-season": member.Season = "2021-2022"; break;
            case "unknown": body["matchedEventIds"] = Ids(rig.Event.Id, 999999); break;
            case "no-anchor": body["matchedEventIds"] = Ids(member.Id); break;
            case "string-id": body["matchedEventIds"] = new JsonArray(JsonValue.Create(rig.Event.Id), JsonValue.Create(member.Id.ToString())); break;
            case "not-array": body["matchedEventIds"] = JsonValue.Create(member.Id); break;
        }
        await rig.Db.SaveChangesAsync();

        using var response = await rig.Client.PostAsJsonAsync("/api/release/grab", body);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, responseBody);
        Assert.Equal(0, rig.Transport.ClientAdds);
        Assert.Empty(await rig.Db.DownloadQueue.AsNoTracking().ToListAsync());
        Assert.Empty(await rig.Db.GrabHistory.AsNoTracking().ToListAsync());
        Assert.Empty(await rig.Db.EventFiles.AsNoTracking().ToListAsync());
        Assert.Empty(rig.Transport.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryGrabKeepsSingleOwnerWhenSeasonMarkerIsFalseOrAbsent(bool omit)
    {
        await using var rig = await CreateAsync();
        var member = await AddEventAsync(rig, "Second event", new DateTime(2020, 10, 10), "2020-2021");
        var body = ModalBody(rig, new[] { rig.Event.Id, member.Id });
        if (omit) body.Remove("isSeasonPack"); else body["isSeasonPack"] = false;
        using var response = await rig.Client.PostAsJsonAsync("/api/release/grab", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var row = await rig.Db.DownloadQueue.AsNoTracking().SingleAsync();
        Assert.Equal(rig.Event.Id, row.EventId);
        Assert.False(row.IsPack);
        Assert.Null(row.PackGroupId);
        Assert.Equal(1, rig.Transport.ClientAdds);
        Assert.Empty(rig.Transport.UnexpectedRequests);
    }

    [Fact]
    public async Task ExplicitLegacyPackKeepsTitleDerivedMissingMonitoredOwners()
    {
        await using var rig = await CreateAsync();
        var member = await AddEventAsync(rig, "Calendar-year member", new DateTime(2020, 10, 10), "2020-2021");
        var excluded = await AddEventAsync(rig, "Unmonitored member", new DateTime(2020, 10, 11), "2020-2021");
        excluded.Monitored = false;
        await rig.Db.SaveChangesAsync();
        var body = ModalBody(rig, new[] { rig.Event.Id });
        body.Remove("isSeasonPack");
        body.Remove("matchedEventIds");
        body["isPack"] = true;
        using var response = await rig.Client.PostAsJsonAsync("/api/release/grab", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var rows = await rig.Db.DownloadQueue.AsNoTracking().OrderBy(row => row.EventId).ToListAsync();
        Assert.Equal(new[] { rig.Event.Id, member.Id }, rows.Select(row => row.EventId));
        Assert.All(rows, row => Assert.True(row.IsPack));
        Assert.NotNull(rows[0].PackGroupId);
        Assert.All(rows, row => Assert.Equal(rows[0].PackGroupId, row.PackGroupId));
        Assert.Equal(1, rig.Transport.ClientAdds);
        Assert.Empty(rig.Transport.UnexpectedRequests);
    }

    private static async Task<PartIdentityIntegrationHarness> CreateAsync()
    {
        var rig = await PartIdentityIntegrationHarness.CreateAsync(multipart: false,
            title: "September event", sport: "American Football", leagueName: "NFL");
        rig.Event.Season = "2020-2021";
        await rig.Db.SaveChangesAsync();
        return rig;
    }

    private static async Task<Event> AddEventAsync(PartIdentityIntegrationHarness rig, string title, DateTime date, string season)
    {
        var member = new Event { Title = title, Sport = rig.Event.Sport,
            LeagueId = rig.Event.LeagueId, League = rig.Event.League, EventDate = date,
            Season = season, Monitored = true, Status = "Completed", QualityProfileId = rig.Event.QualityProfileId };
        rig.Db.Events.Add(member);
        await rig.Db.SaveChangesAsync();
        return member;
    }

    private static JsonArray Ids(params int[] ids) => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());

    private static JsonObject ModalBody(PartIdentityIntegrationHarness rig, int[] ids) => new()
    {
        ["title"] = "NFL.2020.Season.Pack.720p.WEB-DL",
        ["guid"] = "season-admission-fixture",
        ["downloadUrl"] = "http://part-source.invalid/season.nzb",
        ["indexer"] = "Part fixture",
        ["protocol"] = "Usenet",
        ["size"] = 1000000000,
        ["quality"] = "WEBDL-720p",
        ["source"] = "WEBDL",
        ["codec"] = "H264",
        ["language"] = "English",
        ["seeders"] = 20,
        ["leechers"] = 0,
        ["publishDate"] = "2020-09-02T00:00:00Z",
        ["score"] = 0,
        ["qualityScore"] = 0,
        ["torrentInfoHash"] = null,
        ["eventId"] = rig.Event.Id,
        ["matchedEventIds"] = Ids(ids),
        ["isSeasonPack"] = true,
        ["overrideBlocklist"] = false
    };
}
