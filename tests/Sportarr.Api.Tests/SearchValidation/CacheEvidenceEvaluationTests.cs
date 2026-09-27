using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.SearchValidation;

public class CacheEvidenceEvaluationTests
{
    [Theory]
    [InlineData("required", 7, true, 0)]
    [InlineData("ignored", 7, true, 0)]
    [InlineData("preferred", 7, false, 100)]
    [InlineData("required", 8, false, 0)]
    [InlineData("ignored", 8, false, 0)]
    [InlineData("preferred", 8, false, 0)]
    public void NumericSourceIdentityRetainsReleaseProfileDecisions(string rule, int sourceId,
        bool expectedRejected, int expectedScore)
    {
        using var db = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>().Options);
        var service = new ReleaseProfileService(NullLogger<ReleaseProfileService>.Instance, db);
        var profile = new ReleaseProfile
        {
            Name = "Source-specific rule",
            IndexerId = new List<int> { 7 },
            Required = rule == "required" ? "PROPER" : "",
            Ignored = rule == "ignored" ? "H264" : "",
            Preferred = rule == "preferred"
                ? new List<PreferredKeyword> { new() { Key = "H264", Value = 100 } }
                : new List<PreferredKeyword>()
        };
        var cold = CacheEvidenceFixtures.Release();
        cold.IndexerId = sourceId;
        var profiles = new List<ReleaseProfile> { profile };
        var before = service.EvaluateRelease(cold, profiles);
        Assert.Equal(expectedRejected, before.IsRejected);
        Assert.Equal(expectedScore, before.PreferredScore);

        var after = service.EvaluateRelease(CacheEvidenceFixtures.RoundTrip(cold), profiles);

        Assert.Equal(before.IsRejected, after.IsRejected);
        Assert.Equal(before.PreferredScore, after.PreferredScore);
        after.Rejections.Should().Equal(before.Rejections);
        after.MatchedPreferred.Should().BeEquivalentTo(before.MatchedPreferred);
    }

    [Theory]
    [InlineData("French")]
    [InlineData("2")]
    public void ConfiguredMultiLanguageMatchesSurviveFreshFormatEvaluation(string specificationValue)
    {
        var release = CacheEvidenceFixtures.Release("Spain.vs.Belgium.2026-07-10.MULTI.1080p.WEB-DL.H264-GROUP");
        release.Language = "Multi";
        release.MultiLanguageNames = new List<string> { "French", "English" };

        AssertSameFreshEvaluation(release, "Language", specificationValue, true);
    }

    [Fact]
    public void EnglishMultiLanguageControlDoesNotNeedConfiguredLanguageNames()
    {
        var release = CacheEvidenceFixtures.Release("Spain.vs.Belgium.2026-07-10.MULTI.1080p.WEB-DL.H264-GROUP");
        release.Language = "Multi";

        AssertSameFreshEvaluation(release, "Language", "English", true);
    }

    [Fact]
    public void AConfiguredLanguageDoesNotImplyEveryLanguage()
    {
        var release = CacheEvidenceFixtures.Release("Spain.vs.Belgium.2026-07-10.MULTI.1080p.WEB-DL.H264-GROUP");
        release.Language = "Multi";
        release.MultiLanguageNames = new List<string> { "German" };

        AssertSameFreshEvaluation(release, "Language", "French", false);
    }

    [Theory]
    [InlineData("SingleEvent", "ev-2336155")]
    [InlineData("Pack", "lg-000123")]
    public void SuppliedIdentityRetainsReleaseTypeWithoutTitleKeywords(string releaseType, string suppliedId)
    {
        var release = CacheEvidenceFixtures.Release("Opaque.2026.1080p.WEB-DL.H264-GROUP");
        if (releaseType == "SingleEvent") release.SportarrEventId = suppliedId;
        else release.SportarrLeagueId = suppliedId;

        AssertSameFreshEvaluation(release, "ReleaseType", releaseType, true);
    }

    [Theory]
    [InlineData("SingleEvent")]
    [InlineData("Pack")]
    public void MissingIdentityDoesNotInventAReleaseTypeForAnOpaqueTitle(string releaseType)
    {
        var release = CacheEvidenceFixtures.Release("Opaque.2026.1080p.WEB-DL.H264-GROUP");

        AssertSameFreshEvaluation(release, "ReleaseType", releaseType, false);
    }

    private static void AssertSameFreshEvaluation(ReleaseSearchResult release, string implementation,
        string specificationValue, bool expectedMatch)
    {
        using var rawCache = CacheEvidenceFixtures.Cache();
        using var formatCache = new CustomFormatMatchCache(NullLogger<CustomFormatMatchCache>.Instance);
        var evaluator = new ReleaseEvaluator(NullLogger<ReleaseEvaluator>.Instance,
            new EventPartDetector(NullLogger<EventPartDetector>.Instance), formatCache);
        var formats = new List<CustomFormat>
        {
            new()
            {
                Id = 1,
                Name = "Evidence-dependent format",
                Specifications = new List<FormatSpecification>
                {
                    new()
                    {
                        Name = "Evidence condition",
                        Implementation = implementation,
                        Required = true,
                        Fields = new Dictionary<string, object> { ["value"] = specificationValue }
                    }
                }
            }
        };
        var profile = new QualityProfile
        {
            Name = "Evidence threshold",
            Items = new List<QualityItem>
            {
                new() { Name = "WEBDL-1080p", Quality = 3, Allowed = true }
            },
            FormatItems = new List<ProfileFormatItem> { new() { FormatId = 1, Score = 100 } },
            MinFormatScore = 100
        };
        var before = evaluator.EvaluateRelease(release, profile, formats, sport: "Soccer");
        Assert.Equal(expectedMatch ? 100 : 0, before.CustomFormatScore);
        Assert.Equal(expectedMatch, before.Approved);
        Assert.NotNull(formatCache.TryGetCached(release.Title));
        rawCache.Store("fixture-query", new[] { release });
        var stored = rawCache.TryGetCached("fixture-query", 300);
        Assert.NotNull(stored);

        // Title-only format matches would mask evidence lost by the raw cache.
        formatCache.InvalidateAll();
        Assert.Null(formatCache.TryGetCached(release.Title));
        Assert.Same(stored, rawCache.TryGetCached("fixture-query", 300));
        var warm = CacheEvidenceFixtures.Restore(rawCache);
        var after = evaluator.EvaluateRelease(warm, profile, formats, sport: "Soccer");

        Assert.Equal(before.CustomFormatScore, after.CustomFormatScore);
        Assert.Equal(before.Approved, after.Approved);
        Assert.Equal(before.QualityScore, after.QualityScore);
        Assert.Equal(before.TotalScore, after.TotalScore);
        after.MatchedFormats.Should().BeEquivalentTo(before.MatchedFormats);
        after.Rejections.Should().Equal(before.Rejections);
    }
}
