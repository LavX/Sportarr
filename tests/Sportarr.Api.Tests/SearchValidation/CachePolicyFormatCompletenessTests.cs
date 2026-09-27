using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.SearchValidation;

public class CachePolicyFormatCompletenessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameTitleWithDifferentSizeUsesTheCurrentSize(bool firstMatches)
    {
        using var cache = Cache();
        using var independentCache = Cache();
        var evaluator = Evaluator(cache);
        var formats = SizeFormats();
        var profile = Profile(100, 50);
        var first = Release(firstMatches ? 1 : 3);
        var second = Release(firstMatches ? 3 : 1);
        Assert.Equal(first.Title, second.Title);
        AssertScore(evaluator.EvaluateRelease(first, profile, formats), firstMatches ? 100 : 0, firstMatches);
        Assert.NotNull(cache.TryGetCached(first.Title));
        AssertScore(Evaluator(independentCache).EvaluateRelease(second, profile, formats), firstMatches ? 0 : 100, !firstMatches);

        var warm = evaluator.EvaluateRelease(second, profile, formats);

        AssertScore(warm, firstMatches ? 0 : 100, !firstMatches);
    }

    [Fact]
    public void IdenticalEvidenceReusesMatchesForAnotherRelease()
    {
        using var cache = Cache();
        var evaluator = Evaluator(cache);
        var formats = SizeFormats();
        var profile = Profile(100, 50);
        var first = Release(1);
        var second = Release(1);
        second.Guid = "another-guid";
        second.DownloadUrl = "https://another-source.invalid/payload";
        second.Indexer = "Another source";
        AssertScore(evaluator.EvaluateRelease(first, profile, formats), 100, true);
        var stored = cache.TryGetCached(first.Title);
        Assert.NotNull(stored);

        AssertScore(evaluator.EvaluateRelease(second, profile, formats), 100, true);

        Assert.Same(stored, cache.TryGetCached(second.Title));
        Assert.Equal(1, cache.GetStats().EntryCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdenticalEvidenceUsesTheCurrentProfilesScores(bool firstPositive)
    {
        using var cache = Cache();
        var evaluator = Evaluator(cache);
        var formats = SizeFormats();
        var firstProfile = Profile(firstPositive ? 100 : -20, 50);
        firstProfile.Id = 1;
        var secondProfile = Profile(firstPositive ? -20 : 100, 50);
        secondProfile.Id = 2;
        var first = Release(1);
        AssertScore(evaluator.EvaluateRelease(first, firstProfile, formats), firstPositive ? 100 : -20, firstPositive);
        var stored = cache.TryGetCached(first.Title);
        Assert.NotNull(stored);

        var warm = evaluator.EvaluateRelease(Release(1), secondProfile, formats);

        AssertScore(warm, firstPositive ? -20 : 100, !firstPositive);
        Assert.Same(stored, cache.TryGetCached(first.Title));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdenticalEvidenceAppliesPackPenaltyRulesForEachEvaluation(bool firstPack)
    {
        using var cache = Cache();
        var evaluator = Evaluator(cache);
        var formats = SizeFormats();
        formats[0].Name = "No-RlsGroup";
        var profile = Profile(-100, 0);
        var first = Release(1);
        var cold = evaluator.EvaluateRelease(first, profile, formats, isPack: firstPack);
        Assert.Equal(firstPack ? 0 : -100, cold.CustomFormatScore);
        Assert.Equal(firstPack, cold.Approved);
        Assert.Equal(firstPack ? 0 : 1, cold.MatchedFormats.Count);
        var stored = cache.TryGetCached(first.Title);
        Assert.NotNull(stored);
        Assert.Equal(new[] { 1 }, stored.MatchedFormatIds);

        var warm = evaluator.EvaluateRelease(Release(1), profile, formats, isPack: !firstPack);

        Assert.Equal(firstPack ? -100 : 0, warm.CustomFormatScore);
        Assert.Equal(!firstPack, warm.Approved);
        Assert.Equal(firstPack ? 1 : 0, warm.MatchedFormats.Count);
        Assert.Same(stored, cache.TryGetCached(first.Title));
    }

    [Fact]
    public void MutatingTheOriginalLanguageListDoesNotChangeCachedEvidence()
    {
        using var cache = Cache();
        var evaluator = Evaluator(cache);
        var formats = new List<CustomFormat> { new() { Id = 1, Name = "French audio",
            Specifications = new List<FormatSpecification> { new() { Name = "French", Required = true,
                Implementation = "Language", Fields = new Dictionary<string, object> { ["value"] = "French" } } } } };
        var profile = Profile(100, 50);
        var first = Release(1);
        first.Title = "Opaque.MULTI.2026.1080p.WEB-DL.H264-GROUP";
        first.MultiLanguageNames = new List<string> { "French" };
        AssertScore(evaluator.EvaluateRelease(first, profile, formats), 100, true);
        Assert.NotNull(cache.TryGetCached(first.Title));

        first.MultiLanguageNames[0] = "German";
        var second = Release(1);
        second.Title = first.Title;
        second.MultiLanguageNames = new List<string> { "German" };

        AssertScore(evaluator.EvaluateRelease(second, profile, formats), 0, false);
    }

    private static void AssertScore(ReleaseEvaluation result, int score, bool approved)
    {
        Assert.Equal(score, result.CustomFormatScore);
        Assert.Equal(approved, result.Approved);
        if (score == 0)
            Assert.Empty(result.MatchedFormats);
        else
            Assert.Equal(score, Assert.Single(result.MatchedFormats).Score);
        if (approved)
            Assert.Empty(result.Rejections);
        else
            Assert.Contains(result.Rejections, reason => reason.Contains("format score", StringComparison.OrdinalIgnoreCase));
    }

    private static CustomFormatMatchCache Cache() => new(NullLogger<CustomFormatMatchCache>.Instance);

    private static ReleaseEvaluator Evaluator(CustomFormatMatchCache cache) => new(
        NullLogger<ReleaseEvaluator>.Instance, new EventPartDetector(NullLogger<EventPartDetector>.Instance), cache);

    private static List<CustomFormat> SizeFormats() => new() { new() { Id = 1, Name = "Size window",
        Specifications = new List<FormatSpecification> { new() { Name = "Half to two GiB", Required = true,
            Implementation = "Size", Fields = new Dictionary<string, object> { ["min"] = 0.5, ["max"] = 2.0 } } } } };

    private static QualityProfile Profile(int score, int minimum) => new() { Name = "Current profile", MinFormatScore = minimum,
        Items = new List<QualityItem> { new() { Name = "WEBDL-1080p", Quality = 3, Allowed = true } },
        FormatItems = new List<ProfileFormatItem> { new() { FormatId = 1, Score = score } } };

    private static ReleaseSearchResult Release(int sizeGiB) => new()
    {
        Title = "Opaque.2026.1080p.WEB-DL.H264-GROUP", Guid = "first-guid",
        DownloadUrl = "https://format-source.invalid/payload", Indexer = "Format source",
        Protocol = "Torrent", Size = sizeGiB * 1_073_741_824L, Seeders = 20
    };
}
