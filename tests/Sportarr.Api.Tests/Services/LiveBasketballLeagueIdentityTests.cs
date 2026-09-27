using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public sealed class LiveBasketballLeagueIdentityTests
{
    private const string LiveTitle = "WNBA RS 2026 New York Liberty vs Indiana Fever 11 08 1080pEN60fps ESPN";

    private static Event Fixture(string league) => new()
    {
        Title = "New York Liberty vs Indiana Fever", Sport = "Basketball", Season = "2026",
        EventDate = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc),
        League = new League { Name = league, Sport = "Basketball" }
    };

    [Theory]
    [InlineData("WNBA")]
    [InlineData("Women's National Basketball Association")]
    [InlineData("Women’s National Basketball Association")]
    public void WomensLeagueUsesItsOwnQueryAndTemplatePrefix(string league)
    {
        var query = new EventQueryService(NullLogger<EventQueryService>.Instance);
        var evt = Fixture(league);
        Assert.Equal("WNBA 2026 08", query.BuildEventQueries(evt)[0]);
        Assert.Equal("WNBA 2026", query.BuildQueryFromTemplate("{League} {Year}", evt));
        Assert.Equal("WNBA", new ReleaseMatchScorer().GetSportPrefix(league, "Basketball"));
    }

    [Fact]
    public void RealWomensReleaseCannotMatchTheMensLeagueWithIncompleteTeamMetadata()
    {
        var scorer = new ReleaseMatchScorer();
        Assert.Equal("WNBA", scorer.DetectSportPrefix(LiveTitle));
        Assert.Equal(0, scorer.CalculateMatchScore(LiveTitle, Fixture("NBA")));
        Assert.True(scorer.CalculateMatchScore(LiveTitle, Fixture("WNBA")) >= ReleaseMatchScorer.MinimumMatchScore);
    }

    [Theory]
    [InlineData("NBA", "NBA")]
    [InlineData("National Basketball Association", "NBA")]
    [InlineData("Women's.National.Basketball.Association", "WNBA")]
    [InlineData("Women’s_National_Basketball_Association", "WNBA")]
    public void ExplicitLeagueAliasesDoNotCrossMatch(string releaseLeague, string expected)
    {
        var title = releaseLeague + " 2026.08.11 New York Liberty vs Indiana Fever 1080p";
        var scorer = new ReleaseMatchScorer();
        Assert.Equal(expected, scorer.DetectSportPrefix(title));
        Assert.True(scorer.CalculateMatchScore(title, Fixture(expected)) >= ReleaseMatchScorer.MinimumMatchScore);
        Assert.Equal(0, scorer.CalculateMatchScore(title, Fixture(expected == "NBA" ? "WNBA" : "NBA")));
        Assert.Null(scorer.DetectSportPrefix("UNBALANCED 2026.08.11"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FreshAndLegacyCachedWomensReleasesKeepTheirLeague(bool legacy, bool fullName)
    {
        await using var db = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var cache = new ReleaseCacheService(db, NullLogger<ReleaseCacheService>.Instance, new ReleaseMatchScorer());
        await cache.CacheReleasesAsync([new ReleaseSearchResult { Title = fullName ? LiveTitle.Replace("WNBA", "Women's National Basketball Association") : LiveTitle, Guid = "fixture-wnba",
            DownloadUrl = "http://fixture.invalid/fixture.nzb", Indexer = "Fixture", Protocol = "Usenet",
            PublishDate = DateTime.UtcNow }], fromRss: true);
        var stored = await db.ReleaseCache.SingleAsync();
        if (legacy) { stored.SportPrefix = fullName ? null : "NBA"; await db.SaveChangesAsync(); }
        else Assert.Equal("WNBA", stored.SportPrefix);
        await cache.CacheReleasesAsync(Enumerable.Range(0, 1000).Select(i => new ReleaseSearchResult
        {
            Title = "NBA 2026.08.11 New York Liberty vs Indiana Fever 1080p-" + i,
            Guid = "fixture-nba-" + i, DownloadUrl = "http://fixture.invalid/fixture.nzb",
            Indexer = "Fixture", Protocol = "Usenet", PublishDate = DateTime.UtcNow.AddMinutes(1)
        }).ToList(), fromRss: true);
        Assert.Single(await cache.FindMatchingReleasesAsync(Fixture("WNBA")));
        Assert.DoesNotContain(await cache.FindMatchingReleasesAsync(Fixture("NBA")), r => r.Title == stored.Title);
    }
}
