using FluentAssertions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class LeagueEventBroadcastDateProvenanceTests
{
    [Fact]
    public void Equal_wire_date_promotes_provenance_as_an_update()
    {
        var date = new DateTime(2026, 9, 9);
        var existing = new Event
        {
            Title = "Athletics vs Toronto Blue Jays",
            Sport = "Baseball",
            BroadcastDate = date,
            BroadcastDateVerified = false
        };
        var fromApi = new Event
        {
            Title = "Athletics vs Toronto Blue Jays",
            Sport = "Baseball",
            BroadcastDate = date,
            BroadcastDateIsFallback = false
        };

        var update = LeagueEventSyncService.ApplyBroadcastDate(existing, fromApi);

        update.Changed.Should().BeTrue();
        update.DateChanged.Should().BeFalse();
        existing.BroadcastDateVerified.Should().BeTrue();
    }

    [Fact]
    public void Equal_fallback_date_does_not_promote_provenance()
    {
        var date = new DateTime(2026, 9, 9);
        var existing = new Event
        {
            Title = "Athletics vs Toronto Blue Jays",
            Sport = "Baseball",
            BroadcastDate = date,
            BroadcastDateVerified = false
        };
        var fromApi = new Event
        {
            Title = "Athletics vs Toronto Blue Jays",
            Sport = "Baseball",
            BroadcastDate = date,
            BroadcastDateIsFallback = true
        };

        var update = LeagueEventSyncService.ApplyBroadcastDate(existing, fromApi);

        update.Changed.Should().BeFalse();
        update.DateChanged.Should().BeFalse();
        existing.BroadcastDateVerified.Should().BeFalse();
    }
}
