using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.SearchValidation;

public class CachePolicyFormatEvidenceTests
{
    [Theory]
    [InlineData("event", true)]
    [InlineData("event", false)]
    [InlineData("league", true)]
    [InlineData("league", false)]
    [InlineData("language", true)]
    [InlineData("language", false)]
    [InlineData("flags", true)]
    [InlineData("flags", false)]
    public void SameTitleDoesNotReuseAnotherReleasesEvidence(string evidence, bool firstMatches)
    {
        using var cache = new CustomFormatMatchCache(NullLogger<CustomFormatMatchCache>.Instance);
        using var independentCache = new CustomFormatMatchCache(NullLogger<CustomFormatMatchCache>.Instance);
        var evaluator = Evaluator(cache);
        var implementation = evidence is "event" or "league" ? "ReleaseType" : evidence == "language" ? "Language" : "IndexerFlag";
        var value = evidence switch { "event" => "SingleEvent", "league" => "Pack", "language" => "French", _ => "freeleech" };
        var formats = new List<CustomFormat> { new() { Id = 1, Name = "Evidence format",
            Specifications = new List<FormatSpecification> { new() { Name = "Evidence condition", Required = true,
                Implementation = implementation, Fields = new Dictionary<string, object> { ["value"] = value } } } } };
        var profile = new QualityProfile { Name = "Evidence threshold", MinFormatScore = 100,
            Items = new List<QualityItem> { new() { Name = "WEBDL-1080p", Quality = 3, Allowed = true } },
            FormatItems = new List<ProfileFormatItem> { new() { FormatId = 1, Score = 100 } } };
        var first = Release(evidence, firstMatches, "one");
        var second = Release(evidence, !firstMatches, "two");
        Assert.Equal(first.Title, second.Title);
        var cold = evaluator.EvaluateRelease(first, profile, formats, sport: "Soccer");
        Assert.Equal(firstMatches ? 100 : 0, cold.CustomFormatScore);
        Assert.Equal(firstMatches, cold.Approved);
        Assert.NotNull(cache.TryGetCached(first.Title));
        var expected = Evaluator(independentCache).EvaluateRelease(second, profile, formats, sport: "Soccer");
        Assert.Equal(firstMatches ? 0 : 100, expected.CustomFormatScore);
        Assert.Equal(!firstMatches, expected.Approved);

        // Keep the first release's format cache entry populated.
        var actual = evaluator.EvaluateRelease(second, profile, formats, sport: "Soccer");

        Assert.Equal(expected.CustomFormatScore, actual.CustomFormatScore);
        Assert.Equal(expected.Approved, actual.Approved);
        Assert.Equal(expected.MatchedFormats.Select(f => (f.Name, f.Score)), actual.MatchedFormats.Select(f => (f.Name, f.Score)));
        Assert.Equal(expected.Rejections, actual.Rejections);
    }

    private static ReleaseEvaluator Evaluator(CustomFormatMatchCache cache) => new(
        NullLogger<ReleaseEvaluator>.Instance, new EventPartDetector(NullLogger<EventPartDetector>.Instance), cache);

    private static ReleaseSearchResult Release(string evidence, bool matches, string guid) => new()
    {
        Title = evidence == "language" ? "Opaque.MULTI.2026.1080p.WEB-DL.H264-GROUP" : "Opaque.2026.1080p.WEB-DL.H264-GROUP",
        Guid = guid, DownloadUrl = "https://cache-source.invalid/" + guid, Indexer = "Cache source",
        Protocol = "Torrent", Size = 1_073_741_824, Seeders = 20, Language = evidence == "language" ? "Multi" : "English",
        SportarrEventId = evidence == "event" && matches ? "ev-2336155" : null,
        SportarrLeagueId = evidence == "league" && matches ? "lg-000123" : null,
        MultiLanguageNames = evidence == "language" ? new List<string> { matches ? "French" : "German" } : null,
        IndexerFlags = evidence == "flags" ? matches ? "freeleech" : "internal" : null
    };
}
