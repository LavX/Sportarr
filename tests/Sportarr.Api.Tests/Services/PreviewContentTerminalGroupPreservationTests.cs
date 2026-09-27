using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class PreviewContentTerminalGroupPreservationTests
{
    [Fact]
    public void CodecInsideTerminalGroupDoesNotReclassifyGroupPreviewAsContent()
    {
        var matching = new ReleaseMatchingService(
            NullLogger<ReleaseMatchingService>.Instance,
            new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
            new EventPartDetector(NullLogger<EventPartDetector>.Instance));
        var release = new ReleaseSearchResult
        {
            Title = "NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.720p.WEB-DL.H264-PREVIEW-H264-GROUP",
            Guid = "preview-terminal-group-codec", DownloadUrl = "http://preview-fixture.invalid/descriptor",
            Indexer = "Preview group fixture"
        };
        var evt = new Event
        {
            Id = 1, Title = "Aster Falcons vs Iris Comets", Sport = "Basketball", Season = "2021-2022",
            EventDate = new DateTime(2022, 2, 2, 18, 0, 0, DateTimeKind.Utc),
            HomeTeamName = "Aster Falcons", AwayTeamName = "Iris Comets",
            League = new League { Id = 1, Name = "NBA", Sport = "Basketball" }
        };
        var result = matching.ValidateRelease(release, evt);
        Assert.DoesNotContain(result.Rejections, reason => reason.StartsWith("Non-event content", StringComparison.OrdinalIgnoreCase));
    }
}
