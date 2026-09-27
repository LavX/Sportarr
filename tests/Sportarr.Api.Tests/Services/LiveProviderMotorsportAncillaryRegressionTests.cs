using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class LiveProviderMotorsportAncillaryRegressionTests
{
    private readonly ReleaseMatchScorer _scorer = new();
    private readonly ReleaseMatchingService _matching = new(
        NullLogger<ReleaseMatchingService>.Instance,
        new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
        new EventPartDetector(NullLogger<EventPartDetector>.Instance));

    [Fact]
    public void F1ShowRelease_DoesNotMatchRaceEvent()
    {
        const string releaseTitle = "Formula1.2026.Italian.Grand.Prix.The.F1.Show.1080p.AHDTV.x264-DARKSPORT";

        _scorer.CalculateMatchScore(releaseTitle, ItalianGrandPrix("Race"))
            .Should().Be(0, because: "the provider result is a studio show rather than the race");
    }

    [Fact]
    public void F1ShowRelease_DoesNotMatchPracticeEvent()
    {
        const string releaseTitle = "Formula1.2026.Italian.Grand.Prix.The.F1.Show.720p.HDTV.H264-JFF";

        _scorer.CalculateMatchScore(releaseTitle, ItalianGrandPrix("Practice 2"))
            .Should().Be(0, because: "the provider result is a studio show rather than a track session");
    }

    [Fact]
    public void F1ShowRelease_IsHardRejectedByReleaseValidation()
    {
        const string releaseTitle = "Formula1.2026.Italian.Grand.Prix.The.F1.Show.HDTV.H264-RBB";
        var release = new ReleaseSearchResult
        {
            Title = releaseTitle,
            Guid = releaseTitle,
            DownloadUrl = "http://test.invalid/f1-show",
            Indexer = "Test"
        };

        var result = _matching.ValidateRelease(release, ItalianGrandPrix("Race"));

        result.IsHardRejection.Should().BeTrue();
        result.Rejections.Should().Contain(reason => reason.Contains("non-event content", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShowcaseToken_DoesNotDeclareAStudioShow()
    {
        const string releaseTitle = "Formula1.2026.Italian.Grand.Prix.Race.Showcase.1080p.WEB.h264-GROUP";

        _scorer.CalculateMatchScore(releaseTitle, ItalianGrandPrix("Race"))
            .Should().BeGreaterThan(ReleaseMatchScorer.MinimumMatchScore);
    }

    [Fact]
    public void RaceRelease_StillMatchesRaceEvent()
    {
        const string releaseTitle = "Formula1.2026.Italian.Grand.Prix.Race.1080p.AHDTV.x264-DARKSPORT";

        _scorer.CalculateMatchScore(releaseTitle, ItalianGrandPrix("Race"))
            .Should().BeGreaterThan(ReleaseMatchScorer.MinimumMatchScore);
    }

    private static Event ItalianGrandPrix(string session) => new()
    {
        Id = 1,
        Title = $"Italian Grand Prix - {session}",
        Sport = "Motorsport",
        EventDate = new DateTime(2026, 9, 6, 13, 0, 0, DateTimeKind.Utc),
        Location = "Italy",
        Round = "13",
        League = new League { Id = 1, Name = "Formula 1", Sport = "Motorsport" }
    };
}
