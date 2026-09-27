using FluentAssertions;
using Sportarr.Api.Helpers;
using Sportarr.Api.Models;
using Xunit;

namespace Sportarr.Api.Tests.Helpers;

public class ReleasePreferenceComparerTests
{
    [Fact]
    public void RevisionWinsBeforeCustomFormatScoreWhenPreferred()
    {
        var profile = Profile();

        ReleasePreferenceComparer.Compare(
                profile,
                "WEBDL-1080p", "Event.PROPER.1080p.WEB-DL", 0,
                "WEBRip-1080p", "Event.1080p.WEBRip", 500,
                "preferAndUpgrade")
            .Should().BePositive();
    }

    [Fact]
    public void CustomFormatScoreWinsWhenRevisionsAreNotPreferred()
    {
        var profile = Profile();

        ReleasePreferenceComparer.Compare(
                profile,
                "WEBDL-1080p", "Event.PROPER.1080p.WEB-DL", 0,
                "WEBRip-1080p", "Event.1080p.WEBRip", 500,
                "doNotPrefer")
            .Should().BeNegative();
    }

    private static QualityProfile Profile() => new()
    {
        Name = "Test",
        Items =
        [
            new QualityItem
            {
                Name = "WEB 1080p",
                Quality = 0,
                Allowed = true,
                Items =
                [
                    new QualityItem { Name = "WEBDL-1080p", Quality = 15, Allowed = true },
                    new QualityItem { Name = "WEBRip-1080p", Quality = 14, Allowed = true },
                ],
            },
        ],
    };
}
