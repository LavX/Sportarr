using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Endpoints;
using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.Endpoints;

/// <summary>
/// The integration API tells consumers to page
/// GET /api/leagues/{id:int}/events, and the scheduled sync walks every page
/// with showAll=true. The endpoint used to materialize the whole league event
/// graph on every page request and page in memory, so walking a 67,516-event
/// league at 1000 per page made the server load the full graph 68 times
/// (514s measured for one NBA walk). These tests pin the fix: the unfiltered
/// paged path must cut its page window in SQL with a deterministic Id
/// tiebreaker, keep the envelope math and clamps, leave the filtered
/// in-memory path and the plain list untouched, and honor the season
/// narrowing.
/// </summary>
public class LeagueEventsPaginationTests
{
    // Several events share each EventDate on purpose: EventDate alone cannot
    // order a page boundary, so the server-side ordering needs the Id
    // tiebreaker these tests assert.
    private const int EventsPerDate = 5;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Harness(SqliteConnection connection, SportarrDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public SportarrDbContext Db { get; }

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new Harness(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static async Task<int> SeedKeepAllLeagueAsync(
        SportarrDbContext db, int eventCount, Func<int, string?>? seasonFor = null)
    {
        seasonFor ??= _ => "2025-26";

        db.Leagues.Add(new League
        {
            Id = 1,
            Name = "NBA",
            Sport = "Basketball",
            KeepAllEvents = true,
        });
        db.Teams.Add(new Team { Id = 1, ExternalId = "tm-home", Name = "Home Side", LeagueId = 1, Sport = "Basketball" });
        db.Teams.Add(new Team { Id = 2, ExternalId = "tm-away", Name = "Away Side", LeagueId = 1, Sport = "Basketball" });

        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= eventCount; i++)
        {
            db.Events.Add(new Event
            {
                Id = i,
                Title = $"Game {i}",
                Sport = "Basketball",
                LeagueId = 1,
                Season = seasonFor(i),
                Round = "1",
                HomeTeamId = 1,
                AwayTeamId = 2,
                HomeTeamExternalId = "tm-home",
                AwayTeamExternalId = "tm-away",
                HomeTeamName = "Home Side",
                AwayTeamName = "Away Side",
                EventDate = baseDate.AddDays(i / EventsPerDate),
                HasFile = i % 10 == 0,
            });

            if (i % 10 == 0)
            {
                db.EventFiles.Add(new EventFile
                {
                    EventId = i,
                    FilePath = $"/media/game-{i}.mkv",
                    Size = 1000,
                    Exists = true,
                });
            }
        }

        await db.SaveChangesAsync();
        return 1;
    }

    [Fact]
    public async Task UnfilteredPageQuery_CutsItsWindowInSqlWithTheIdTiebreaker()
    {
        await using var harness = await Harness.CreateAsync();

        var (_, pageQuery) = LeagueEndpoints.ComposeUnfilteredEventPageQueries(harness.Db, 1, null, currentPage: 3, size: 100);
        var sql = pageQuery.ToQueryString();

        sql.Should().Contain("LIMIT",
            "the page window must be cut in the database instead of loading the whole league per page");
        sql.Should().Contain("OFFSET",
            "page 3 must skip the first two pages server-side");

        var orderBy = sql[sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase)..];
        var dateIndex = orderBy.IndexOf("EventDate", StringComparison.OrdinalIgnoreCase);
        var idIndex = orderBy.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
        dateIndex.Should().BeGreaterThan(-1, "the page is ordered by event date");
        idIndex.Should().BeGreaterThan(-1, "the Id tiebreaker must be part of the ordering");
        dateIndex.Should().BeLessThan(idIndex,
            "the Id tiebreaker must follow EventDate so page boundaries stay deterministic when dates tie");
        orderBy[dateIndex..idIndex].Should().Contain("DESC", "newest events come first");
        orderBy[idIndex..].Should().Contain("DESC", "the tiebreaker also runs newest Id first");
    }

    [Fact]
    public async Task UnfilteredCountQuery_CountsRowsWithoutTheRelationshipGraph()
    {
        await using var harness = await Harness.CreateAsync();

        var (countQuery, _) = LeagueEndpoints.ComposeUnfilteredEventPageQueries(harness.Db, 1, "2025", currentPage: 1, size: 100);
        var sql = countQuery.ToQueryString();

        sql.Should().NotContain("LIMIT", "the count is one number, not a page of rows");
        sql.Should().NotContain("OFFSET");
        sql.Should().NotContain("LEFT JOIN",
            "counting a league's events must not drag teams and files into the query");
        sql.Should().NotContain("INNER JOIN");
    }

    [Fact]
    public async Task UnfilteredWalk_ReturnsEveryEventOnce_InDateThenIdOrder()
    {
        await using var harness = await Harness.CreateAsync();
        const int total = 2500;
        const int size = 1000;
        var leagueId = await SeedKeepAllLeagueAsync(harness.Db, total);

        var (countQuery, _) = LeagueEndpoints.ComposeUnfilteredEventPageQueries(harness.Db, leagueId, null, 1, size);
        var totalRecords = await countQuery.CountAsync();
        totalRecords.Should().Be(total);

        var seen = new List<int>();
        var totalPages = LeagueEndpoints.LeagueEventsTotalPages(totalRecords, size);
        for (var page = 1; page <= totalPages; page++)
        {
            var (_, pageQuery) = LeagueEndpoints.ComposeUnfilteredEventPageQueries(harness.Db, leagueId, null, page, size);
            var records = await pageQuery.ToListAsync();

            records.Should().HaveCount(Math.Min(size, totalRecords - seen.Count),
                $"page {page} must return exactly its window out of {totalRecords}");

            // Teams and files load only for the page's rows, but they must
            // still load: the response mapping reads them.
            records.Should().OnlyContain(e => e.HomeTeam != null && e.AwayTeam != null);
            records.Where(e => e.HasFile).Should().OnlyContain(e => e.Files.Count == 1);

            seen.AddRange(records.Select(e => e.Id));
        }

        seen.Should().OnlyHaveUniqueItems("a page boundary must never duplicate a row");
        seen.Should().HaveCount(total, "the walk must cover the whole league with no gaps");

        var expected = await harness.Db.Events
            .AsNoTracking()
            .OrderByDescending(e => e.EventDate)
            .ThenByDescending(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync();
        seen.Should().Equal(expected,
            "the walk must follow EventDate desc then Id desc, matching the unpaged membership");
    }

    [Fact]
    public async Task EnvelopeMath_CountsAndClampsStayExact()
    {
        await using var harness = await Harness.CreateAsync();
        const int total = 37;
        var leagueId = await SeedKeepAllLeagueAsync(harness.Db, total);

        var (countQuery, _) = LeagueEndpoints.ComposeUnfilteredEventPageQueries(harness.Db, leagueId, null, 1, 1);
        (await countQuery.CountAsync()).Should().Be(total);

        // totalPages at the sizes an integration would use, including
        // pageSize 1 and a page larger than the whole league.
        LeagueEndpoints.LeagueEventsTotalPages(total, 1).Should().Be(37);
        LeagueEndpoints.LeagueEventsTotalPages(total, 7).Should().Be(6);
        LeagueEndpoints.LeagueEventsTotalPages(total, 36).Should().Be(2);
        LeagueEndpoints.LeagueEventsTotalPages(total, 37).Should().Be(1);
        LeagueEndpoints.LeagueEventsTotalPages(total, 100).Should().Be(1);
        LeagueEndpoints.LeagueEventsTotalPages(0, 100).Should().Be(0);

        // The defaults and clamps callers already rely on.
        LeagueEndpoints.NormalizeLeagueEventsPaging(null, null).Should().Be((1, 100));
        LeagueEndpoints.NormalizeLeagueEventsPaging(0, 0).Should().Be((1, 1));
        LeagueEndpoints.NormalizeLeagueEventsPaging(-5, 9999).Should().Be((1, 1000));
        LeagueEndpoints.NormalizeLeagueEventsPaging(4, 250).Should().Be((4, 250));

        var (_, pageQuery) = LeagueEndpoints.ComposeUnfilteredEventPageQueries(harness.Db, leagueId, null, 1, 500);
        (await pageQuery.ToListAsync()).Should().HaveCount(total,
            "a page larger than the league returns everything once");
    }

    [Fact]
    public async Task FilteredLeague_StillPagesTheInMemoryVisibleList()
    {
        await using var harness = await Harness.CreateAsync();
        var db = harness.Db;

        db.Leagues.Add(new League { Id = 1, Name = "NFL", Sport = "American Football" });
        db.Teams.Add(new Team { Id = 1, ExternalId = "tm-a", Name = "Team A", LeagueId = 1, Sport = "American Football" });
        db.Teams.Add(new Team { Id = 2, ExternalId = "tm-b", Name = "Team B", LeagueId = 1, Sport = "American Football" });
        db.Teams.Add(new Team { Id = 3, ExternalId = "tm-c", Name = "Team C", LeagueId = 1, Sport = "American Football" });
        db.Teams.Add(new Team { Id = 4, ExternalId = "tm-d", Name = "Team D", LeagueId = 1, Sport = "American Football" });
        db.LeagueTeams.Add(new LeagueTeam { LeagueId = 1, TeamId = 1, Monitored = true });
        db.LeagueTeams.Add(new LeagueTeam { LeagueId = 1, TeamId = 2, Monitored = true });
        db.LeagueTeams.Add(new LeagueTeam { LeagueId = 1, TeamId = 3, Monitored = false });

        var baseDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 20; i++)
        {
            var (home, away, hasFile) = i switch
            {
                <= 8 => ("tm-a", "tm-c", false),
                <= 12 => ("tm-c", "tm-b", false),
                <= 16 => ("tm-c", "tm-d", true),
                _ => ("tm-c", "tm-d", false),
            };
            db.Events.Add(new Event
            {
                Id = i,
                Title = $"Game {i}",
                Sport = "American Football",
                LeagueId = 1,
                Season = "2026",
                Round = "1",
                HomeTeamExternalId = home,
                AwayTeamExternalId = away,
                HomeTeamName = home,
                AwayTeamName = away,
                EventDate = baseDate.AddDays(i),
                HasFile = hasFile,
            });
        }
        await db.SaveChangesAsync();

        var league = await db.Leagues
            .AsNoTracking()
            .Include(l => l.MonitoredTeams)
            .ThenInclude(lt => lt.Team)
            .FirstAsync(l => l.Id == 1);

        var all = await LeagueEndpoints.LoadLeagueEventsForVisibilityAsync(db, 1, null);
        var visible = LeagueEndpoints.SelectVisibleEvents(all, league, showAll: false);
        visible.Should().HaveCount(16,
            "monitored-team games and file-bearing events are the visible set");

        const int size = 3;
        var collected = new List<int>();
        for (var page = 1; page <= LeagueEndpoints.LeagueEventsTotalPages(visible.Count, size); page++)
        {
            var slice = await LeagueEndpoints.GetFilteredEventPageAsync(db, league, null, showAll: false, page, size);
            slice.TotalRecords.Should().Be(visible.Count,
                "the envelope count is the visible total, not the league total");
            collected.AddRange(slice.Records.Select(e => e.Id));
        }

        collected.Should().Equal(visible.Select(e => e.Id).ToList(),
            "the filtered path must keep slicing the same visible list in the same order");
    }

    [Fact]
    public async Task SeasonNarrowing_StillAppliesToTheFastPath()
    {
        await using var harness = await Harness.CreateAsync();
        const int total = 80;
        string? SeasonFor(int i) => (i % 4) switch
        {
            0 => "2024",
            1 => "2025",
            2 => null,
            _ => "",
        };

        var leagueId = await SeedKeepAllLeagueAsync(harness.Db, total, SeasonFor);

        // A named season only returns that season's rows.
        await AssertSeasonMembershipAsync(harness.Db, leagueId, "2024", i => SeasonFor(i) == "2024", total);
        await AssertSeasonMembershipAsync(harness.Db, leagueId, "2025", i => SeasonFor(i) == "2025", total);
        // "Unknown" is the season-less bucket: null and empty Season strings.
        await AssertSeasonMembershipAsync(harness.Db, leagueId, "Unknown", i => SeasonFor(i) is null or "", total);
    }

    private static async Task AssertSeasonMembershipAsync(
        SportarrDbContext db, int leagueId, string season, Func<int, bool> expected, int total)
    {
        var expectedIds = Enumerable.Range(1, total).Where(expected).ToList();

        var (countQuery, _) = LeagueEndpoints.ComposeUnfilteredEventPageQueries(db, leagueId, season, 1, 1000);
        (await countQuery.CountAsync()).Should().Be(expectedIds.Count,
            $"season {season} must narrow the count");

        var (_, pageQuery) = LeagueEndpoints.ComposeUnfilteredEventPageQueries(db, leagueId, season, 1, 1000);
        var records = await pageQuery.ToListAsync();
        records.Should().HaveCount(expectedIds.Count,
            $"season {season} must narrow the page");
        records.Select(e => e.Id).Should().BeEquivalentTo(expectedIds,
            $"season {season} must return exactly that season's rows");
    }
}
