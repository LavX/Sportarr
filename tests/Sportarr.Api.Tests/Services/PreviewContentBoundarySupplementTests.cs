using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class PreviewContentBoundarySupplementTests
{
    [Theory]
    [InlineData("Previewé")]
    [InlineData("Preview\u0301")]
    [InlineData("Preview2")]
    [InlineData("2Preview")]
    public void UnicodeAndDigitWordContinuationsAreNotPreviewTokens(string token)
    {
        var result = Matching().ValidateRelease(Release(token), Event());
        Assert.DoesNotContain(result.Rejections, reason => reason.StartsWith("Non-event content", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AsciiPreviewCaseDoesNotDependOnCurrentTurkishCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var matching = Matching();
            var results = new[] { "preview", "PREVIEW" }
                .Select(token => matching.ValidateRelease(Release(token), Event())).ToList();
            Assert.All(results, result =>
            {
                Assert.True(result.IsHardRejection);
                Assert.False(result.IsMatch);
                Assert.Contains(result.Rejections, reason => reason.StartsWith("Non-event content", StringComparison.OrdinalIgnoreCase)
                    && reason.Contains("Preview", StringComparison.OrdinalIgnoreCase));
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ReportedReleaseGroupCannotHideEarlierContentPreview()
    {
        var release = Release("Preview");
        release.Title = "NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.Preview.720p.WEB-DL.H264-PREVIEW";
        release.ReleaseGroup = "Preview";
        var result = Matching().ValidateRelease(release, Event());
        Assert.True(result.IsHardRejection);
        Assert.False(result.IsMatch);
        Assert.Contains(result.Rejections, reason => reason.StartsWith("Non-event content", StringComparison.OrdinalIgnoreCase)
            && reason.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseMatchingService Matching() => new(
        NullLogger<ReleaseMatchingService>.Instance,
        new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
        new EventPartDetector(NullLogger<EventPartDetector>.Instance));

    private static ReleaseSearchResult Release(string token) => new()
    {
        Title = "NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets." + token + ".720p.WEB-DL.H264-GROUP",
        Guid = "preview-boundary-" + token, DownloadUrl = "http://preview-fixture.invalid/descriptor", Indexer = "Preview boundary fixture"
    };

    private static Event Event() => new()
    {
        Id = 1, Title = "Aster Falcons vs Iris Comets", Sport = "Basketball", Season = "2021-2022",
        EventDate = new DateTime(2022, 2, 2, 18, 0, 0, DateTimeKind.Utc),
        HomeTeamName = "Aster Falcons", AwayTeamName = "Iris Comets",
        League = new League { Id = 1, Name = "NBA", Sport = "Basketball" }
    };
}
