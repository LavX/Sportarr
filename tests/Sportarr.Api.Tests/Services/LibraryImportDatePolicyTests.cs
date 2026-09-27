using FluentAssertions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Xunit;

namespace Sportarr.Api.Tests.Services;

public class LibraryImportDatePolicyTests
{
    private static Event Game(DateTime broadcastDate, bool verified) => new()
    {
        Title = "Toronto Blue Jays vs Boston Red Sox",
        Sport = "Baseball",
        EventDate = broadcastDate.AddDays(1).AddHours(2),
        BroadcastDate = broadcastDate,
        BroadcastDateVerified = verified,
        HomeTeamId = 10,
        AwayTeamId = 20,
        Season = "2026",
        League = new League { Name = "MLB", Sport = "Baseball" }
    };

    private static int Score(Event evt, DateTime fileDate, string searchTitle = "Blue Jays vs Red Sox") =>
        LibraryImportService.CalculateMatchConfidence(
            searchTitle: searchTitle,
            eventTitle: evt.Title,
            organization: "MLB",
            evt: evt,
            parsedDate: fileDate,
            parsedYear: 2026,
            parsedRoundNumber: null,
            seasonYearEnd: null,
            explicitEpisodeNumber: null,
            parsedLocation: null,
            parsedSport: "Baseball",
            seriesLabel: null);

    [Fact]
    public void A_verified_neighbouring_fixture_is_rejected()
    {
        var neighbouringGame = Game(new DateTime(2026, 8, 13), verified: true);

        Score(neighbouringGame, new DateTime(2026, 8, 12)).Should().Be(0);
    }

    [Fact]
    public void An_exact_title_still_uses_the_broadcast_date_to_break_a_match_tie()
    {
        var namedGame = Game(new DateTime(2026, 8, 12), verified: true);
        var neighbouringGame = Game(new DateTime(2026, 8, 13), verified: false);

        Score(namedGame, new DateTime(2026, 8, 12), namedGame.Title).Should()
            .BeGreaterThan(Score(neighbouringGame, new DateTime(2026, 8, 12), namedGame.Title));
    }

    [Fact]
    public void An_unverified_neighbour_keeps_the_one_day_fallback()
    {
        var neighbouringGame = Game(new DateTime(2026, 8, 13), verified: false);

        Score(neighbouringGame, new DateTime(2026, 8, 12)).Should().BePositive();
    }
}
