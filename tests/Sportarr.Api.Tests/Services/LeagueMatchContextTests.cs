using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class LeagueMatchContextTests
{
    [Fact]
    public async Task LoadAsync_returns_untracked_league_identity_fields()
    {
        await using var db = new SportarrDbContext(
            new DbContextOptionsBuilder<SportarrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        db.Leagues.Add(new League
        {
            Id = 7,
            ExternalId = "lg-000007",
            Name = "Nippon Professional Baseball",
            AlternateName = "NPB, Pacific League",
            Sport = "Baseball",
            Description = "Not needed for release matching"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var snapshot = await LeagueMatchContext.LoadAsync(db);

        snapshot.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = 7,
            ExternalId = "lg-000007",
            Name = "Nippon Professional Baseball",
            AlternateName = "NPB, Pacific League",
            Sport = "Baseball"
        });
        snapshot[0].Description.Should().BeNull();
        db.ChangeTracker.Entries<League>().Should().BeEmpty();
    }
}
