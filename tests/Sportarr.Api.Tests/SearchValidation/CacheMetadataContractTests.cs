using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Xunit;

namespace Sportarr.Api.Tests.SearchValidation;

public class CacheMetadataContractTests
{
    [Fact]
    public void CallerAndReaderMutationsCannotRewriteCachedCoverageEvidence()
    {
        using var cache = new SearchResultCache(NullLogger<SearchResultCache>.Instance);
        var firstPages = new[]
        {
            new SearchPageObservation(0, 2, 2, 2, 0, null, true, "row-11-page-0"),
            new SearchPageObservation(2, 2, 1, 1, 2, null, true, "row-11-page-2")
        };
        var secondPages = new[]
        {
            new SearchPageObservation(0, 2, 1, 1, 0, null, true, "row-22-page-0")
        };
        var supplied = new[]
        {
            new IndexerSearchDiagnostic(11, "source-11", "fixture-query", SearchTermination.UnknownTail,
                3, firstPages, true, false),
            new IndexerSearchDiagnostic(22, "source-22", "fixture-query", SearchTermination.UnknownTail,
                1, secondPages, true, false)
        };
        var expectedEvidence = JsonSerializer.Serialize(supplied);
        cache.Store("fixture-query", Offers(), searchComplete: false, diagnostics: supplied);

        supplied[0] = supplied[0] with { Termination = SearchTermination.Exhausted, SatisfiesRequest = true };
        firstPages[1] = firstPages[1] with { RawCount = 0, ReportedTotal = 2, Fingerprint = "caller-rewrite" };
        Assert.True(supplied[0].SatisfiesRequest);
        Assert.Equal("caller-rewrite", firstPages[1].Fingerprint);

        var firstRead = cache.TryGetCached("fixture-query", 300);
        Assert.NotNull(firstRead);
        Assert.False(firstRead.SearchComplete);
        Assert.Equal(expectedEvidence, JsonSerializer.Serialize(firstRead.SearchDiagnostics));
        Assert.Equal("retained-offer", Assert.Single(cache.ToSearchResults(firstRead)).Guid);

        var readerDiagnostics = Assert.IsType<IndexerSearchDiagnostic[]>(firstRead.SearchDiagnostics);
        var readerPages = Assert.IsType<SearchPageObservation[]>(readerDiagnostics[1].Pages);
        readerDiagnostics[0] = readerDiagnostics[0] with
        {
            IndexerId = 999, Termination = SearchTermination.Exhausted, SatisfiesRequest = true
        };
        readerPages[0] = readerPages[0] with
        {
            RawCount = 0, ParsedCount = 0, ReportedTotal = 0, Fingerprint = "reader-rewrite"
        };
        Assert.Equal(999, readerDiagnostics[0].IndexerId);
        Assert.Equal("reader-rewrite", readerDiagnostics[1].Pages[0].Fingerprint);

        Assert.Equal(expectedEvidence, JsonSerializer.Serialize(firstRead.SearchDiagnostics));
        var nextRead = cache.TryGetCached("fixture-query", 300);
        Assert.NotNull(nextRead);
        Assert.False(nextRead.SearchComplete);
        Assert.Equal(expectedEvidence, JsonSerializer.Serialize(nextRead.SearchDiagnostics));
        Assert.Equal("retained-offer", Assert.Single(cache.ToSearchResults(nextRead)).Guid);
    }

    [Theory]
    [InlineData(SearchTermination.Unavailable)]
    [InlineData(SearchTermination.LocalDenied)]
    [InlineData(SearchTermination.ProviderRateLimited)]
    [InlineData(SearchTermination.ParseIncomplete)]
    [InlineData(SearchTermination.InvalidMetadata)]
    public void ReusableUnknownTailCannotHideAnotherRowsRecoveryRequirement(SearchTermination failure)
    {
        var reusable = Diagnostic(11, SearchTermination.UnknownTail);
        var incomplete = Diagnostic(22, failure);
        foreach (var releases in new[] { new List<ReleaseSearchResult>(), Offers() })
        {
            var blocked = new SearchOperationOutcome(releases, new[] { reusable, incomplete }, false);
            Assert.False(blocked.CanCache);
            Assert.False(blocked.SatisfiesRequest);
            Assert.Equal(failure, blocked.Diagnostics[1].Termination);

            var reversed = blocked with { Diagnostics = new[] { incomplete, reusable } };
            Assert.False(reversed.CanCache);

            var recovered = blocked with { Diagnostics = new[] { reusable, Diagnostic(22, SearchTermination.Exhausted) } };
            Assert.True(recovered.CanCache);
            Assert.False(recovered.SatisfiesRequest);
            Assert.Equal(SearchTermination.UnknownTail, recovered.Diagnostics[0].Termination);
            Assert.False(blocked.CanCache);
        }
    }

    [Fact]
    public void HealthyPageLimitAndUnknownTailKeepTheirIncompleteEvidence()
    {
        var diagnostics = new[] { Diagnostic(11, SearchTermination.UnknownTail), Diagnostic(22, SearchTermination.PageCeiling) };
        var outcome = new SearchOperationOutcome(Offers(), diagnostics, false);
        Assert.True(outcome.CanCache);
        Assert.False(outcome.SatisfiesRequest);
        Assert.Equal(SearchTermination.PageCeiling, outcome.Diagnostics[1].Termination);
        Assert.Equal(5, outcome.Diagnostics[1].Pages.Count);
        Assert.True((outcome with { Diagnostics = diagnostics.Reverse().ToArray() }).CanCache);
    }

    [Fact]
    public void CacheabilityDoesNotInventCompletenessOrSourceEvidence()
    {
        var complete = new SearchOperationOutcome(Offers(), new[] { Diagnostic(11, SearchTermination.Exhausted) }, true);
        Assert.True(complete.CanCache);
        Assert.True(complete.SatisfiesRequest);

        var unknown = new SearchOperationOutcome(Offers(), new[]
        {
            Diagnostic(11, SearchTermination.UnknownTail), Diagnostic(22, SearchTermination.UnknownTail)
        }, false);
        Assert.True(unknown.CanCache);
        Assert.False(unknown.SatisfiesRequest);
        Assert.All(unknown.Diagnostics, diagnostic => Assert.False(diagnostic.SatisfiesRequest));

        var noSources = new SearchOperationOutcome(new(), Array.Empty<IndexerSearchDiagnostic>(), false);
        Assert.False(noSources.CanCache);
        Assert.False(noSources.SatisfiesRequest);
        Assert.False((noSources with { Releases = Offers() }).CanCache);

        using var cache = new SearchResultCache(NullLogger<SearchResultCache>.Instance);
        cache.Store("legacy-complete-empty", Array.Empty<ReleaseSearchResult>());
        var legacy = cache.TryGetCached("legacy-complete-empty", 300);
        Assert.NotNull(legacy);
        Assert.True(legacy.SearchComplete);
        Assert.Empty(legacy.SearchDiagnostics);
        Assert.Empty(legacy.RawReleases);
        Assert.Equal(60, legacy.LifetimeSeconds);
    }

    private static IndexerSearchDiagnostic Diagnostic(int rowId, SearchTermination termination)
    {
        var pages = termination switch
        {
            SearchTermination.Unavailable or SearchTermination.LocalDenied or SearchTermination.ProviderRateLimited
                => Array.Empty<SearchPageObservation>(),
            SearchTermination.PageCeiling => Enumerable.Range(0, 5).Select(page =>
                new SearchPageObservation(page * 2, 2, 2, 2, page * 2, null, true, $"row-{rowId}-page-{page}")).ToArray(),
            _ => new[] { new SearchPageObservation(0, 2, 1,
                termination == SearchTermination.ParseIncomplete ? 0 : 1, 0,
                termination == SearchTermination.Exhausted ? 1 : null,
                termination != SearchTermination.InvalidMetadata, $"row-{rowId}-page-0") }
        };
        return new(rowId, $"source-{rowId}", "fixture-query", termination, pages.Sum(page => page.RawCount),
            pages, true, termination == SearchTermination.Exhausted);
    }

    private static List<ReleaseSearchResult> Offers() => new()
    {
        new ReleaseSearchResult
        {
            Title = "Belgian.Grand.Prix.2026.1080p.WEB-DL.H264-GROUP",
            Guid = "retained-offer",
            DownloadUrl = "http://fixture.invalid/descriptor/retained-offer",
            Indexer = "source-11",
            IndexerId = 11,
            Size = 4L * 1024 * 1024 * 1024
        }
    };
}
