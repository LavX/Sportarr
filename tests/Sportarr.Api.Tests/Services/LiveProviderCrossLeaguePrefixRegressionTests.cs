using FluentAssertions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class LiveProviderCrossLeaguePrefixRegressionTests
{
    private const string WrongMlbRelease =
        "MLB.2026.08.07.Colorado.Rockies.vs.St.Louis.Cardinals.1080p.WEB.h264-NiGHTNiNJAS";

    private static Event NflEvent() => new()
    {
        Id = 667876,
        Title = "Arizona Cardinals vs Carolina Panthers",
        Sport = "American Football",
        Season = "2026",
        EventDate = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc),
        BroadcastDate = new DateTime(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc),
        HomeTeamId = 1152,
        AwayTeamId = 1156,
        HomeTeamName = "Arizona Cardinals",
        AwayTeamName = "Carolina Panthers",
        League = new League { Id = 22, Name = "NFL", Sport = "American Football" }
    };

    [Fact]
    public void CalculateMatchScore_RejectsRealMlbReleaseAssignedToNflEvent()
    {
        var score = new ReleaseMatchScorer().CalculateMatchScore(WrongMlbRelease, NflEvent());

        score.Should().BeLessThan(ReleaseMatchScorer.AutoGrabMatchScore);
    }

    [Fact]
    public void CalculateMatchScore_HardRejectsDifferentTeamLeagueWhenTeamNamesAreMissing()
    {
        var evt = NflEvent();
        evt.HomeTeamName = null;
        evt.AwayTeamName = null;

        var score = new ReleaseMatchScorer().CalculateMatchScore(WrongMlbRelease, evt);

        score.Should().Be(0);
    }

    [Fact]
    public void CalculateMatchScore_DoesNotHardRejectMatchingTeamLeaguePrefix()
    {
        var evt = NflEvent();
        evt.HomeTeamName = null;
        evt.AwayTeamName = null;

        var score = new ReleaseMatchScorer().CalculateMatchScore(
            "NFL.2026.08.07.Arizona.Cardinals.vs.Carolina.Panthers.1080p.WEB.h264-GROUP", evt);

        score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CalculateMatchScore_DoesNotHardRejectReleaseWithoutKnownLeaguePrefix()
    {
        var evt = NflEvent();
        evt.HomeTeamName = null;
        evt.AwayTeamName = null;

        var score = new ReleaseMatchScorer().CalculateMatchScore(
            "2026.08.07.Arizona.Cardinals.vs.Carolina.Panthers.1080p.WEB.h264-GROUP", evt);

        score.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("2026.08.07.Arizona.Cardinals.vs.Carolina.Panthers.Replay.1080p.WEB.h264-GROUP")]
    [InlineData("2026.08.07.Arizona.Cardinals.vs.Carolina.Panthers.GreenBay.1080p.WEB.h264-GROUP")]
    public void CalculateMatchScore_DoesNotTreatEmbeddedLettersAsALeaguePrefix(string releaseTitle)
    {
        var score = new ReleaseMatchScorer().CalculateMatchScore(releaseTitle, NflEvent());

        score.Should().BeGreaterThan(0);
    }
}
