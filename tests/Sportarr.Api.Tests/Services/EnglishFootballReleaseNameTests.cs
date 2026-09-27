using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class EnglishFootballReleaseNameTests
{
    private readonly ReleaseMatchingService _service = new(
        NullLogger<ReleaseMatchingService>.Instance,
        new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
        new EventPartDetector(NullLogger<EventPartDetector>.Instance));

    [Theory]
    [InlineData("BWSL.2022.11.06.Manchester.United.vs.Chelsea.720p.WEB.h264-ULTRAS")]
    [InlineData("England Womens Super League 2025 26 Manchester United vs Chelsea 03 10 2025 1080p 50fps EN BBC")]
    public void ValidateRelease_WslBaseClubNames_AcceptsObservedRelease(string title)
    {
        var eventDate = title.StartsWith("BWSL", StringComparison.Ordinal)
            ? new DateTime(2022, 11, 6)
            : new DateTime(2025, 10, 3);
        var evt = TeamEvent(
            "English Womens Super League",
            "Chelsea Women vs Manchester United WFC",
            "Chelsea Women",
            "Manchester United WFC",
            eventDate);

        var result = _service.ValidateRelease(Release(title), evt);

        result.IsMatch.Should().BeTrue();
        result.IsHardRejection.Should().BeFalse();
        result.MatchReasons.Should().Contain("Both team names found");
    }

    [Fact]
    public void ValidateRelease_WslExplicitWomensNamesWithoutLeague_AcceptsRelease()
    {
        var evt = TeamEvent(
            "English Womens Super League",
            "Chelsea Women vs Manchester United WFC",
            "Chelsea Women",
            "Manchester United WFC",
            new DateTime(2022, 11, 6));

        var result = _service.ValidateRelease(
            Release("Chelsea.Women.vs.Manchester.United.WFC.2022.11.06.1080p"),
            evt);

        result.IsMatch.Should().BeTrue();
        result.IsHardRejection.Should().BeFalse();
    }

    [Fact]
    public void ValidateRelease_WslUserAliasesWithoutLeague_AcceptsRelease()
    {
        var evt = TeamEvent(
            "English Womens Super League",
            "Chelsea Women vs Manchester United WFC",
            "Chelsea Women",
            "Manchester United WFC",
            new DateTime(2022, 11, 6));
        evt.HomeTeam = new Team { Name = "Chelsea Women", UserAliases = "Chelsea Ladies" };
        evt.AwayTeam = new Team { Name = "Manchester United WFC", UserAliases = "Manchester United Ladies" };

        var result = _service.ValidateRelease(
            Release("Chelsea.Ladies.vs.Manchester.United.Ladies.2022.11.06.1080p"),
            evt);

        result.IsMatch.Should().BeTrue();
        result.IsHardRejection.Should().BeFalse();
    }

    [Fact]
    public void ValidateRelease_MensFixture_DoesNotUseWslBaseClubNames()
    {
        var evt = TeamEvent(
            "English Womens Super League",
            "Chelsea Women vs Manchester United WFC",
            "Chelsea Women",
            "Manchester United WFC",
            new DateTime(2026, 5, 16));

        var result = _service.ValidateRelease(
            Release("EPL 2026 Chelsea vs Manchester United 16 05 1080p"),
            evt);

        result.IsMatch.Should().BeFalse();
        result.IsHardRejection.Should().BeTrue();
    }

    [Fact]
    public void ValidateRelease_ChampionshipCityName_AcceptsObservedRelease()
    {
        var evt = TeamEvent(
            "English League Championship",
            "Sheffield United vs Wolverhampton Wanderers",
            "Sheffield United",
            "Wolverhampton Wanderers",
            new DateTime(2026, 9, 13));

        var result = _service.ValidateRelease(
            Release("EFL Championship 2026 Sheffield United vs Wolverhampton 13 09 1080p60fps EN Paramount"),
            evt);

        result.IsMatch.Should().BeTrue();
        result.IsHardRejection.Should().BeFalse();
        result.MatchReasons.Should().Contain("Both team names found");
    }

    [Fact]
    public void ValidateRelease_SlashDatedOtherCompetition_RejectsObservedRelease()
    {
        var evt = TeamEvent(
            "FA Cup",
            "Chelsea vs Manchester City",
            "Chelsea",
            "Manchester City",
            new DateTime(2026, 5, 16));

        var result = _service.ValidateRelease(
            Release("EPL Manchester City Vs Chelsea 04/01/2026 Sky Sports Premier League UHD 2160p"),
            evt);

        result.IsMatch.Should().BeFalse();
        result.IsHardRejection.Should().BeTrue();
        result.Rejections.Should().Contain(reason => reason.StartsWith("Date mismatch:"));
    }

    [Theory]
    [InlineData("United Cup 2026 Group A Spain V Argentina Full Day Replay 1080p")]
    [InlineData("United_Cup_2026_Group_A_Spain_V_Argentina_Full_Day_Replay_1080p")]
    public void ValidateRelease_UnitedCup_DoesNotMatchFifaWorldCup(string title)
    {
        var evt = TeamEvent(
            "FIFA World Cup",
            "Spain vs Argentina",
            "Spain",
            "Argentina",
            new DateTime(2026, 7, 19));

        var result = _service.ValidateRelease(Release(title), evt);

        result.IsMatch.Should().BeFalse();
        result.IsHardRejection.Should().BeTrue();
        result.Rejections.Should().Contain("Different competition found in release");
    }

    [Theory]
    [InlineData("FA Cup 2025 26 Final Chelsea vs Manchester City Live from Wembley Pre Match 16 05 2026 1080p 50fps EN BBC")]
    [InlineData("FA Cup Final 2nd Half Presentation Chelsea v Manchester City 1080p50fps")]
    [InlineData("FIFA World Cup 2026 07 19 Final Argentina vs Spain Halftime Show 2160p50fps")]
    [InlineData("FA_Cup_2026_05_16_Chelsea_vs_Manchester_City_Pre_Match_1080p")]
    [InlineData("FA_Cup_2026_05_16_Chelsea_vs_Manchester_City_1st_Half_1080p")]
    [InlineData("FA_Cup_2026_05_16_Chelsea_vs_Manchester_City_2nd_Half_1080p")]
    [InlineData("FIFA_World_Cup_2026_07_19_Argentina_vs_Spain_Halftime_Show_2160p")]
    [InlineData("FA Cup 2026 05 16 Chelsea vs Manchester City First Half 1080p")]
    [InlineData("FA Cup 2026 05 16 Chelsea vs Manchester City Second Half 1080p")]
    [InlineData("FA Cup 2026 05 16 Chelsea vs Manchester City 1080p Pre-Match")]
    public void ValidateRelease_NonEventMatchSegment_RejectsObservedRelease(string title)
    {
        var evt = title.StartsWith("FIFA", StringComparison.Ordinal)
            ? TeamEvent("FIFA World Cup", "Spain vs Argentina", "Spain", "Argentina", new DateTime(2026, 7, 19))
            : TeamEvent("FA Cup", "Chelsea vs Manchester City", "Chelsea", "Manchester City", new DateTime(2026, 5, 16));

        var result = _service.ValidateRelease(Release(title), evt);

        result.IsMatch.Should().BeFalse();
        result.IsHardRejection.Should().BeTrue();
        result.Rejections.Should().ContainSingle(reason => reason.StartsWith("Non-event content detected:"));
    }

    [Fact]
    public void ValidateRelease_SeasonPrefixedSlashDate_RejectsDifferentCompetitionDate()
    {
        var evt = TeamEvent(
            "FA Cup",
            "Chelsea vs Manchester City",
            "Chelsea",
            "Manchester City",
            new DateTime(2026, 5, 16));

        var result = _service.ValidateRelease(
            Release("EPL 2025/26 Chelsea vs Manchester City 04/01/2026 1080p"),
            evt);

        result.IsMatch.Should().BeFalse();
        result.IsHardRejection.Should().BeTrue();
        result.Rejections.Should().Contain(reason => reason.StartsWith("Date mismatch:"));
    }

    private static Event TeamEvent(
        string leagueName,
        string title,
        string home,
        string away,
        DateTime date) => new()
    {
        Title = title,
        Sport = "Soccer",
        HomeTeamName = home,
        AwayTeamName = away,
        EventDate = DateTime.SpecifyKind(date, DateTimeKind.Utc),
        BroadcastDate = date,
        BroadcastDateVerified = true,
        League = new League { Name = leagueName, Sport = "Soccer" }
    };

    private static ReleaseSearchResult Release(string title) => new()
    {
        Title = title,
        Guid = title,
        DownloadUrl = "http://fixture.invalid/release",
        Indexer = "Fixture"
    };
}
