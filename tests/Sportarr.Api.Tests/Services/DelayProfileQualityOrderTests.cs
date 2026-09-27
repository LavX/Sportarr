using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Xunit;

namespace Sportarr.Api.Tests.Services;

public class DelayProfileQualityOrderTests
{
    [Fact]
    public void ProfileOrderWinsBeforeCustomFormatScore()
    {
        using var db = Db();
        var service = Service(db);
        var profile = Profile(
            Item("HDTV-1080p", 6),
            Item("WEBDL-2160p", 19));
        var preferred = Release("Event.1080p.HDTV", "HDTV-1080p", 0);
        var other = Release("Event.2160p.WEB-DL", "WEBDL-2160p", 5000);

        service.SelectBestReleaseWithDelayProfile(
                [other, preferred], new DelayProfile(), profile, "preferAndUpgrade")
            .Should().BeSameAs(preferred);
    }

    [Fact]
    public void GroupedQualitiesUseCustomFormatScore()
    {
        using var db = Db();
        var service = Service(db);
        var profile = Profile(Group("Preferred", Item("HDTV-1080p", 6), Item("WEBDL-2160p", 19)));
        var existingScore = Release("Event.1080p.HDTV", "HDTV-1080p", 2000);
        var lowerScore = Release("Event.2160p.WEB-DL", "WEBDL-2160p", 560);

        service.SelectBestReleaseWithDelayProfile(
                [lowerScore, existingScore], new DelayProfile(), profile, "preferAndUpgrade")
            .Should().BeSameAs(existingScore);
    }

    private static SportarrDbContext Db() => new(
        new DbContextOptionsBuilder<SportarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DelayProfileService Service(SportarrDbContext db)
    {
        var evaluator = new ReleaseEvaluator(
            Mock.Of<ILogger<ReleaseEvaluator>>(),
            new EventPartDetector(Mock.Of<ILogger<EventPartDetector>>()),
            new CustomFormatMatchCache(Mock.Of<ILogger<CustomFormatMatchCache>>()));
        return new DelayProfileService(db, evaluator, Mock.Of<ILogger<DelayProfileService>>());
    }

    private static ReleaseSearchResult Release(string title, string quality, int customFormatScore) => new()
    {
        Title = title,
        Guid = title,
        DownloadUrl = "https://example.invalid/download",
        Indexer = "TestIndexer",
        Protocol = "Usenet",
        Quality = quality,
        CustomFormatScore = customFormatScore,
        PublishDate = DateTime.UtcNow,
        Size = 1024,
    };

    private static QualityProfile Profile(params QualityItem[] items) => new()
    {
        Name = "Test",
        Items = items.ToList(),
    };

    private static QualityItem Item(string name, int quality) => new()
    {
        Name = name,
        Quality = quality,
        Allowed = true,
    };

    private static QualityItem Group(string name, params QualityItem[] items) => new()
    {
        Name = name,
        Quality = 0,
        Allowed = true,
        Items = items.ToList(),
    };
}
