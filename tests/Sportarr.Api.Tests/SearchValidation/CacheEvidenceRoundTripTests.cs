using FluentAssertions;
using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.SearchValidation;

public class CacheEvidenceRoundTripTests
{
    [Theory]
    [InlineData(nameof(ReleaseSearchResult.IndexerId))]
    [InlineData(nameof(ReleaseSearchResult.SportarrEventId))]
    [InlineData(nameof(ReleaseSearchResult.SportarrLeagueId))]
    [InlineData(nameof(ReleaseSearchResult.MultiLanguageNames))]
    [InlineData(nameof(ReleaseSearchResult.ReleaseGroup))]
    public void SuppliedEvidenceSurvivesTheRawCache(string field)
    {
        var cold = CacheEvidenceFixtures.Release("Spain.vs.Belgium.2026-07-10.MULTI.1080p.WEB-DL.H264-GROUP");
        cold.Language = "Multi";
        cold.MultiLanguageNames = new List<string> { "French", "English" };
        if (field == nameof(ReleaseSearchResult.SportarrEventId)) cold.SportarrEventId = "ev-2336155";
        if (field == nameof(ReleaseSearchResult.SportarrLeagueId)) cold.SportarrLeagueId = "lg-000123";
        var property = typeof(ReleaseSearchResult).GetProperty(field)!;
        var expected = property.GetValue(cold);
        Assert.NotNull(expected);

        var warm = CacheEvidenceFixtures.RoundTrip(cold);

        property.GetValue(warm).Should().BeEquivalentTo(expected, $"the raw cache must preserve {field}");
    }

    [Fact]
    public void ChangingTheProviderLanguageListCannotChangeStoredEvidence()
    {
        using var cache = CacheEvidenceFixtures.Cache();
        var cold = CacheEvidenceFixtures.Release("Spain.vs.Belgium.2026-07-10.MULTI.1080p.WEB-DL.H264-GROUP");
        cold.Language = "Multi";
        cold.MultiLanguageNames = new List<string> { "French", "English" };
        cache.Store("fixture-query", new[] { cold });
        cold.MultiLanguageNames.Clear();
        cold.MultiLanguageNames.Add("German");

        CacheEvidenceFixtures.Restore(cache).MultiLanguageNames.Should().Equal("French", "English");
    }

    [Fact]
    public void ChangingOneRestoredLanguageListCannotChangeAnotherReader()
    {
        using var cache = CacheEvidenceFixtures.Cache();
        var cold = CacheEvidenceFixtures.Release("Spain.vs.Belgium.2026-07-10.MULTI.1080p.WEB-DL.H264-GROUP");
        cold.Language = "Multi";
        cold.MultiLanguageNames = new List<string> { "French", "English" };
        cache.Store("fixture-query", new[] { cold });
        var first = CacheEvidenceFixtures.Restore(cache);
        first.MultiLanguageNames.Should().Equal("French", "English");
        first.MultiLanguageNames!.Clear();
        first.MultiLanguageNames.Add("German");

        CacheEvidenceFixtures.Restore(cache).MultiLanguageNames.Should().Equal("French", "English");
        cold.MultiLanguageNames.Should().Equal("French", "English");
    }

    [Fact]
    public void ExistingRawFieldsRemainAvailableToEachReader()
    {
        var cold = CacheEvidenceFixtures.Release();
        cold.IsPack = true;
        var warm = CacheEvidenceFixtures.RoundTrip(cold);
        foreach (var field in new[] { "Title", "Guid", "DownloadUrl", "InfoUrl", "Indexer", "Protocol",
            "TorrentInfoHash", "Size", "PublishDate", "Seeders", "Leechers", "IndexerFlags",
            "Language", "Source", "Codec", "IsPack" })
        {
            var property = typeof(ReleaseSearchResult).GetProperty(field)!;
            Assert.Equal(property.GetValue(cold), property.GetValue(warm));
        }
    }

    [Fact]
    public void ProfileDecisionsAndRequestedPartAreResetWithoutSharingMutableLists()
    {
        using var cache = CacheEvidenceFixtures.Cache();
        var cold = CacheEvidenceFixtures.Release();
        cold.Quality = "old-quality";
        cold.Score = 123;
        cold.QualityScore = 100;
        cold.CustomFormatScore = 23;
        cold.SizeScore = 77;
        cold.MatchScore = 99;
        cold.Approved = false;
        cold.Rejections.Add("Old profile rejection");
        cold.MatchedFormats.Add(new MatchedFormat { Name = "Old format", Score = 23 });
        cold.IsBlocklisted = true;
        cold.BlocklistReason = "Old blocklist entry";
        cold.Part = "Main Card";
        cache.Store("fixture-query", new[] { cold });

        var first = CacheEvidenceFixtures.Restore(cache);
        Assert.Null(first.Quality);
        Assert.Equal(0, first.Score);
        Assert.Equal(0, first.QualityScore);
        Assert.Equal(0, first.CustomFormatScore);
        Assert.Equal(0L, first.SizeScore);
        Assert.Equal(0, first.MatchScore);
        Assert.True(first.Approved);
        Assert.Empty(first.Rejections);
        Assert.Empty(first.MatchedFormats);
        Assert.False(first.IsBlocklisted);
        Assert.Null(first.BlocklistReason);
        Assert.Null(first.Part);
        first.Rejections.Add("Current reader decision");
        first.MatchedFormats.Add(new MatchedFormat { Name = "Current reader format", Score = 9 });

        var second = CacheEvidenceFixtures.Restore(cache);
        Assert.Empty(second.Rejections);
        Assert.Empty(second.MatchedFormats);
        Assert.Equal("Old profile rejection", Assert.Single(cold.Rejections));
        Assert.Equal("Old format", Assert.Single(cold.MatchedFormats).Name);
    }
}
