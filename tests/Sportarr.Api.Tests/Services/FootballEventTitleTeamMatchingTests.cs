using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class FootballEventTitleTeamMatchingTests
{
    private readonly ReleaseMatchingService _service = new(
        NullLogger<ReleaseMatchingService>.Instance,
        new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
        new EventPartDetector(NullLogger<EventPartDetector>.Instance));

    [Fact]
    public void ValidateRelease_FullTeamNameFromEventTitle_AcceptsExactFixture()
    {
        var evt = CreateEvent();
        var release = CreateRelease("Ligue 1 2026 Paris Saint Germain vs AS Monaco 04 09 1080p30fps EN beIN");

        var result = _service.ValidateRelease(release, evt);

        result.IsMatch.Should().BeTrue();
        result.IsHardRejection.Should().BeFalse();
        result.MatchReasons.Should().Contain("Both team names found");
    }

    [Fact]
    public void ValidateRelease_FullNameWithDifferentOpponent_StillRejectsFixture()
    {
        var evt = CreateEvent();
        var release = CreateRelease("Ligue 1 2026 Paris Saint Germain vs Lyon 04 09 1080p30fps EN beIN");

        var result = _service.ValidateRelease(release, evt);

        result.IsMatch.Should().BeFalse();
        result.IsHardRejection.Should().BeTrue();
    }

    [Fact]
    public void ValidateRelease_ReversedEventTitle_DoesNotCountOneClubTwice()
    {
        var evt = CreateEvent();
        evt.Title = "Monaco vs Paris Saint-Germain";
        var release = CreateRelease("Ligue 1 2026 Monaco vs Lyon 04 09 1080p30fps EN beIN");

        var result = _service.ValidateRelease(release, evt);

        result.IsMatch.Should().BeFalse();
        result.IsHardRejection.Should().BeTrue();
    }

    [Fact]
    public void ValidateRelease_ReversedEventTitle_StillAcceptsBothParticipants()
    {
        var evt = CreateEvent();
        evt.Title = "Monaco vs Paris Saint-Germain";
        var release = CreateRelease("Ligue 1 2026 Paris Saint Germain vs AS Monaco 04 09 1080p30fps EN beIN");

        var result = _service.ValidateRelease(release, evt);

        result.IsMatch.Should().BeTrue();
        result.IsHardRejection.Should().BeFalse();
    }

    [Fact]
    public void ValidateRelease_UnresolvedEventTitleNames_DoNotReplaceStoredParticipants()
    {
        var evt = CreateEvent();
        evt.Title = "Lyon vs Marseille";
        var release = CreateRelease("Ligue 1 2026 Lyon vs Marseille 04 09 1080p30fps EN beIN");

        var result = _service.ValidateRelease(release, evt);

        result.IsMatch.Should().BeFalse();
        result.MatchReasons.Should().NotContain("Both team names found");
    }

    private static Event CreateEvent() => new()
    {
        ExternalId = "ev-2339466",
        Title = "Paris Saint-Germain vs Monaco",
        Sport = "Soccer",
        HomeTeamName = "Paris SG",
        AwayTeamName = "Monaco",
        EventDate = new DateTime(2026, 9, 4, 19, 5, 0, DateTimeKind.Utc),
        BroadcastDate = new DateTime(2026, 9, 4),
        BroadcastDateVerified = true,
        League = new League
        {
            ExternalId = "lg-000006",
            Name = "French Ligue 1",
            Sport = "Soccer",
            AlternateName = "Ligue 1 Conforama France"
        }
    };

    private static ReleaseSearchResult CreateRelease(string title) => new()
    {
        Title = title,
        Guid = title,
        DownloadUrl = "http://fixture.invalid/" + Uri.EscapeDataString(title),
        Indexer = "Fixture"
    };
}
