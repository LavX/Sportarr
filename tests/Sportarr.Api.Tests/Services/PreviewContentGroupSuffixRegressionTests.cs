using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Xunit.Abstractions;

namespace Sportarr.Api.Tests.Services;

public class PreviewContentGroupSuffixRegressionTests
{
    private readonly ITestOutputHelper _output;

    public PreviewContentGroupSuffixRegressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EarlierCodecHyphenCannotHideContentPreviewBeforeFinalGroup()
    {
        const string prefix = "NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.H264-REPACK";
        foreach (var title in new[]
        {
            prefix + ".720p.WEB-DL.H264-GROUP",
            prefix + ".720p.WEB-DL.H264-PREVIEW"
        })
        {
            var control = Matching().ValidateRelease(Release(title), Event());
            _output.WriteLine(JsonSerializer.Serialize(new { phase = "content-eligibility-control", title,
                control.IsHardRejection, control.IsMatch, control.Rejections }));
            Assert.DoesNotContain(control.Rejections, reason => reason.StartsWith("Non-event content", StringComparison.OrdinalIgnoreCase));
        }

        var contentTitle = prefix + ".Preview.720p.WEB-DL.H264-GROUP";
        var result = Matching().ValidateRelease(Release(contentTitle), Event());
        _output.WriteLine(JsonSerializer.Serialize(new { phase = "earlier-codec-content-preview", title = contentTitle,
            result.IsHardRejection, result.IsMatch, result.Rejections }));
        Assert.True(result.IsHardRejection);
        Assert.False(result.IsMatch);
        Assert.Contains(result.Rejections, reason => reason.StartsWith("Non-event content", StringComparison.OrdinalIgnoreCase)
            && reason.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseMatchingService Matching() => new(
        NullLogger<ReleaseMatchingService>.Instance,
        new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
        new EventPartDetector(NullLogger<EventPartDetector>.Instance));

    private static ReleaseSearchResult Release(string title) => new()
    {
        Title = title, Guid = "preview-group-suffix-regression",
        DownloadUrl = "http://preview-fixture.invalid/descriptor", Indexer = "Preview suffix fixture"
    };

    private static Event Event() => new()
    {
        Id = 1, Title = "Aster Falcons vs Iris Comets", Sport = "Basketball", Season = "2021-2022",
        EventDate = new DateTime(2022, 2, 2, 18, 0, 0, DateTimeKind.Utc),
        HomeTeamName = "Aster Falcons", AwayTeamName = "Iris Comets",
        League = new League { Id = 1, Name = "NBA", Sport = "Basketball" }
    };
}
