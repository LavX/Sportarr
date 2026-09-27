using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public sealed class TeamGameIdentityTests
{
    private const string CapturedTitle = "NHL Stanley Cup 2026 Final Game 05 Vegas Golden Knights vs Carolina Hurricanes 1080p WEB DL AC3 2 0 AAC 2 0 H 264 Mosgortrans";
    private readonly ReleaseMatchingService _matching = new(
        NullLogger<ReleaseMatchingService>.Instance,
        new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
        new EventPartDetector(NullLogger<EventPartDetector>.Instance));

    [Theory]
    [InlineData(11)]
    [InlineData(14)]
    public void UndatedCapturedGameCannotIdentifyEitherScheduledMatch(int day)
    {
        var result = _matching.ValidateRelease(Release(CapturedTitle), Event(day));
        Assert.True(result.IsHardRejection);
        Assert.Contains(result.Rejections, r => r.Contains("game", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Game 5")]
    [InlineData("Game.05")]
    [InlineData("Game05")]
    public void ExplicitMatchingGameAllowsUndatedRelease(string marker)
    {
        var evt = Event(11);
        evt.Title += " - Game 5";
        var result = _matching.ValidateRelease(Release(CapturedTitle.Replace("Game 05", marker)), evt);
        Assert.False(result.IsHardRejection);
        Assert.True(result.IsMatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingGameRejectsEvenWhenDateMatches(bool includeDate)
    {
        var evt = Event(14);
        evt.Title += " - Game 6";
        var title = CapturedTitle.Replace("2026 Final", includeDate ? "2026.06.14 Final" : "2026 Final");
        Assert.True(_matching.ValidateRelease(Release(title), evt).IsHardRejection);
    }

    [Theory]
    [InlineData(11, "NHL Stanley Cup Final 2026 Vegas Golden Knights vs Carolina Hurricanes Game 5 11 06 720pEN60fps ABC")]
    [InlineData(14, "NHL Stanley Cup Final 2026 Carolina Hurricanes vs Vegas Golden Knights Game 6 14 06 720pEN60fps ABC")]
    public void CapturedDateIdentifiesTheScheduledGame(int day, string title)
    {
        var result = _matching.ValidateRelease(Release(title), Event(day));
        Assert.False(result.IsHardRejection);
        Assert.True(result.IsMatch);
    }

    [Fact]
    public void MatchingGameDoesNotBypassConflictingDate()
    {
        var evt = Event(14);
        evt.Title += " - Game 5";
        Assert.True(_matching.ValidateRelease(Release(CapturedTitle.Replace("2026 Final", "2026.06.11 Final")), evt).IsHardRejection);
    }

    [Fact]
    public void MatchingGameDoesNotBypassWrongTeams()
    {
        var evt = Event(11);
        evt.Title += " - Game 5";
        var title = CapturedTitle.Replace("Vegas Golden Knights", "Boston Bruins");
        Assert.True(_matching.ValidateRelease(Release(title), evt).IsHardRejection);
    }

    [Fact]
    public void MatchingCanonicalEventIdRemainsAuthoritative()
    {
        var release = Release(CapturedTitle);
        release.SportarrEventId = "ev-2280632";
        Assert.False(_matching.ValidateRelease(release, Event(14)).IsHardRejection);
    }

    [Fact]
    public void ExplicitPackKeepsItsExistingDisposition()
    {
        var release = Release(CapturedTitle);
        release.IsPack = true;
        Assert.False(_matching.ValidateRelease(release, Event(14)).IsHardRejection);
    }

    [Theory]
    [InlineData("Game5")]
    [InlineData("Game05")]
    public void ReleaseGroupDoesNotDeclareGameIdentity(string group)
    {
        var title = "NHL 2026 Vegas Golden Knights vs Carolina Hurricanes 1080p WEB H264-" + group;
        Assert.False(_matching.ValidateRelease(Release(title), Event(14)).IsHardRejection);
    }

    [Fact]
    public void MultipleGameMarkersCannotIdentifyOneGame()
    {
        var evt = Event(11);
        evt.Title += " - Game 5";
        var title = CapturedTitle.Replace("Game 05", "Game 5 Game 6");
        Assert.True(_matching.ValidateRelease(Release(title), evt).IsHardRejection);
    }

    [Fact]
    public void InferredPackKeepsItsExistingDisposition()
    {
        var release = Release("NHL 2026 Stanley Cup Game 5 PACK Vegas Golden Knights and Carolina Hurricanes 1080p WEB H264-GROUP");
        var result = _matching.ValidateRelease(release, Event(14));
        Assert.False(result.IsHardRejection);
    }

    [Fact]
    public void UndatedTeamReleaseWithoutGameMarkerKeepsItsDisposition()
    {
        Assert.False(_matching.ValidateRelease(Release(CapturedTitle.Replace("Game 05 ", "")), Event(14)).IsHardRejection);
    }

    [Theory]
    [InlineData("Game 5-6")]
    [InlineData("Game 5 & 6")]
    [InlineData("Game 5 and 6")]
    [InlineData("Game05+06")]
    public void GameRangeCannotIdentifyOneGame(string marker)
    {
        var evt = Event(11);
        evt.Title += " - Game 5";
        Assert.True(_matching.ValidateRelease(Release(CapturedTitle.Replace("Game 05", marker)), evt).IsHardRejection);
    }

    [Theory]
    [InlineData("Pregame5")]
    [InlineData("Game2026")]
    [InlineData("Game1080p")]
    public void IncidentalTokensDoNotDeclareGameIdentity(string token)
    {
        var title = CapturedTitle.Replace("Game 05", token);
        Assert.False(_matching.ValidateRelease(Release(title), Event(14)).IsHardRejection);
    }

    [Theory]
    [InlineData("Game-05")]
    [InlineData("-Game05")]
    public void TerminalGameMarkerIsNotAReleaseGroup(string marker)
    {
        var title = "NHL 2026 Vegas Golden Knights vs Carolina Hurricanes " + marker;
        Assert.True(_matching.ValidateRelease(Release(title), Event(14)).IsHardRejection);
    }

    [Fact]
    public void BracketedGroupAfterCodecDoesNotDeclareGameIdentity()
    {
        var title = "NHL 2026 Vegas Golden Knights vs Carolina Hurricanes 1080p WEB H264[Game05]";
        Assert.False(_matching.ValidateRelease(Release(title), Event(14)).IsHardRejection);
    }

    [Theory]
    [InlineData("NHL Stanley Cup Final 2026 Vegas Golden Knights vs Carolina Hurricanes Game 5-11 06 720pEN60fps ABC")]
    [InlineData("NHL Vegas Golden Knights vs Carolina Hurricanes Game05-11.06.2026 720p WEB H264-GROUP")]
    [InlineData("NHL Stanley Cup Final 2026 Vegas Golden Knights vs Carolina Hurricanes Game 5-11-06 720pEN60fps ABC")]
    [InlineData("NHL Vegas Golden Knights vs Carolina Hurricanes Game05-11-06-2026 720p WEB H264-GROUP")]
    public void DateAfterGameMarkerIsNotAnotherGame(string title)
    {
        var evt = Event(11);
        evt.Title += " - Game 5";
        Assert.Equal(new DateTime(2026, 6, 11), _matching.ParseRelease(title).EventDate);
        Assert.False(_matching.ValidateRelease(Release(title), evt).IsHardRejection);
    }

    [Fact]
    public void DateDoesNotEraseASeparateConflictingGame()
    {
        var evt = Event(11);
        evt.Title += " - Game 5";
        var title = "NHL 2026.06.11 Vegas Golden Knights vs Carolina Hurricanes Game 5-6 720p WEB H264-GROUP";
        Assert.True(_matching.ValidateRelease(Release(title), evt).IsHardRejection);
    }

    private static ReleaseSearchResult Release(string title) => new()
    {
        Title = title, Guid = "fixture", DownloadUrl = "http://fixture.invalid/release", Indexer = "Fixture",
        PublishDate = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc)
    };

    private static Event Event(int day) => new()
    {
        Id = 634834, ExternalId = "ev-2280632", Title = "Vegas Golden Knights vs Carolina Hurricanes",
        Sport = "Ice Hockey", Season = "2025-2026", Round = "200",
        EventDate = new DateTime(2026, 6, day + 1, 0, 0, 0, DateTimeKind.Utc),
        BroadcastDate = new DateTime(2026, 6, day, 0, 0, 0, DateTimeKind.Utc), BroadcastDateVerified = false,
        HomeTeamName = "Vegas Golden Knights", AwayTeamName = "Carolina Hurricanes",
        League = new League { Id = 34, ExternalId = "lg-000028", Name = "NHL", Sport = "Ice Hockey" }
    };
}
