using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class MotorsportImportSessionMatchingTests
{
    private static readonly SportsFileNameParser Parser =
        new(NullLogger<SportsFileNameParser>.Instance);

    private static Event RaceEvent() => new()
    {
        Id = 1,
        Title = "Coke Zero Sugar 400",
        Sport = "Motorsport",
        EventDate = new DateTime(2026, 8, 29, 18, 30, 0, DateTimeKind.Utc),
        League = new League { Id = 1, Name = "NASCAR Cup Series", Sport = "Motorsport" }
    };

    [Theory]
    [InlineData(
        "NASCAR.2026.Daytona.Practice.1080p.WEB-DL.H264-MWR",
        "NASCAR.2026.Daytona.Race.1080p.WEB-DL.H264-MWR")]
    [InlineData(
        "NASCAR.Cup.Series.2026.Daytona.Practice.1080p.WEB-DL.H264-MWR",
        "NASCAR.Cup.Series.2026.Daytona.Race.1080p.WEB-DL.H264-MWR")]
    public void Practice_scores_below_race_without_a_round_number(
        string practiceFilename, string raceFilename)
    {
        var service = ImportMatchingTestHarness.Service();
        var evt = RaceEvent();

        var practice = service.ScoreMatch(evt.Title, evt.Title, null, evt, Parser.Parse(practiceFilename));
        var race = service.ScoreMatch(evt.Title, evt.Title, null, evt, Parser.Parse(raceFilename));

        practice.Core.Should().BeLessThan(0);
        race.Core.Should().BeGreaterThanOrEqualTo(60);
    }

    [Fact]
    public void Indycar_final_practice_remains_a_matching_session_without_a_round_number()
    {
        var service = ImportMatchingTestHarness.Service();
        var evt = new Event
        {
            Id = 2,
            Title = "110th Running of the Indianapolis 500 Final Practice",
            Sport = "Motorsport",
            EventDate = new DateTime(2026, 5, 22, 15, 0, 0, DateTimeKind.Utc),
            League = new League { Id = 2, Name = "IndyCar Series", Sport = "Motorsport" }
        };
        var parsed = new SportsParseResult
        {
            OriginalFilename = "IndyCar.2026.Indy500.Final.Practice.1080p.WEB-GRP",
            Sport = "Motorsport",
            Organization = "IndyCar",
            Session = "Practice 1"
        };

        var score = service.ScoreMatch(evt.Title, evt.Title, null, evt, parsed);

        parsed.RoundNumber.Should().BeNull();
        score.Core.Should().BeGreaterThanOrEqualTo(60);
    }

    [Fact]
    public void Ancillary_content_does_not_regain_the_legacy_session_label()
    {
        var service = ImportMatchingTestHarness.Service();
        var evt = new Event
        {
            Id = 3,
            Title = "Monaco Grand Prix Sprint",
            Sport = "Motorsport",
            EventDate = new DateTime(2026, 6, 7, 14, 0, 0, DateTimeKind.Utc),
            League = new League { Id = 3, Name = "Formula 1", Sport = "Motorsport" }
        };
        var parsed = new SportsParseResult
        {
            OriginalFilename = "Formula1.2026.Monaco.Sprint.Notebook.1080p.WEB-GRP",
            Sport = "Motorsport",
            Organization = "Formula 1",
            Session = "Sprint"
        };

        var score = service.ScoreMatch(evt.Title, evt.Title, null, evt, parsed);

        score.Core.Should().BeLessThan(90,
            "ancillary releases must not receive a session-match boost");
    }
}
