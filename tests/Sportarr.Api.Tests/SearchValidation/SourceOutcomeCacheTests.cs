using FluentAssertions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sportarr.Api.Tests.SearchValidation;

public sealed class SourceOutcomeCacheTests
{
    [Theory]
    [InlineData(SearchTermination.Exhausted)]
    [InlineData(SearchTermination.CallerCeiling)]
    [InlineData(SearchTermination.UnknownTail)]
    public void SuccessfulEvidenceIsCopiedAndKeepsItsOriginalExpiry(SearchTermination termination)
    {
        var clock = new ManualClock();
        var cache = new SourceOutcomeCache(clock);
        var source = Outcome(termination);
        cache.Store("row", source, 300);
        source.Releases[0].Title = "changed by caller";
        clock.Advance(299);
        var restored = cache.TryGet("row", 300)!;
        restored.Releases[0].Title.Should().Be("Fixture.1080p.H264-GROUP");
        restored.Termination.Should().Be(termination);
        restored.SatisfiesRequest.Should().Be(termination != SearchTermination.UnknownTail);
        restored.RawCursor.Should().Be(1);
        restored.Pages.Should().Equal(source.Pages);
        restored.Releases[0].Rejections.Add("rejected by current profile");
        cache.TryGet("row", 300)!.Releases[0].Rejections.Should().BeEmpty();
        clock.Advance(2);
        cache.TryGet("row", 300).Should().BeNull();
    }

    [Fact]
    public void FailedAndInterruptedAnswersCannotBeReused()
    {
        var cache = new SourceOutcomeCache();
        foreach (var termination in Enum.GetValues<SearchTermination>().Except(new[]
                 { SearchTermination.Exhausted, SearchTermination.CallerCeiling, SearchTermination.UnknownTail, SearchTermination.PageCeiling }))
        {
            cache.Store(termination.ToString(), Outcome(termination), 300);
            cache.TryGet(termination.ToString(), 300).Should().BeNull();
        }
        cache.Store("contradictory", Outcome(SearchTermination.Exhausted) with { Failure = new IOException() }, 300);
        cache.TryGet("contradictory", 300).Should().BeNull();
    }

    [Fact]
    public void EmptyAnswersAndShorterConfiguredRetentionExpireWithoutRenewal()
    {
        var clock = new ManualClock();
        var cache = new SourceOutcomeCache(clock);
        cache.Store("empty", Outcome(SearchTermination.Exhausted) with { Releases = new() }, 300);
        cache.Store("positive", Outcome(SearchTermination.Exhausted), 300);
        cache.Store("disabled", Outcome(SearchTermination.Exhausted), 0);
        clock.Advance(59);
        cache.TryGet("empty", 300).Should().NotBeNull();
        cache.TryGet("positive", 30).Should().BeNull();
        cache.TryGet("positive", 300).Should().NotBeNull();
        cache.TryGet("disabled", 300).Should().BeNull();
        clock.Advance(2);
        cache.TryGet("empty", 300).Should().BeNull();
    }

    [Fact]
    public void AggregateCacheRefusesEvidenceThatExpiredDuringRecovery()
    {
        var clock = new ManualClock();
        using var cache = new SearchResultCache(NullLogger<SearchResultCache>.Instance, clock);
        cache.Store("expired", Outcome(SearchTermination.Exhausted).Releases, 300,
            expiresAt: clock.GetUtcNow().AddSeconds(-1));
        cache.TryGetCached("expired", 300).Should().BeNull();
        cache.Store("live", Outcome(SearchTermination.Exhausted).Releases, 300,
            expiresAt: clock.GetUtcNow().AddSeconds(10));
        cache.TryGetCached("live", 300).Should().NotBeNull();
        clock.Advance(11);
        cache.TryGetCached("live", 300).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentRestoresRetainAllSourceEvidenceWithoutSharingMutableLists()
    {
        var cache = new SourceOutcomeCache();
        var published = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var release = new ReleaseSearchResult
        {
            Title = "Fixture.1080p.H264-GROUP", Indexer = "Fixture", IndexerId = 7,
            Guid = "offer", DownloadUrl = "http://127.0.0.1/offer", InfoUrl = "http://127.0.0.1/details",
            SourceQuality = "WEBDL-1080p", ReleaseGroup = "GROUP", IndexerFlags = "freeleech",
            Size = 4_294_967_296, PublishDate = published, Seeders = 14, Leechers = 3,
            TorrentInfoHash = "fixture-hash", Protocol = "Torrent", IsPack = true,
            SportarrEventId = "ev-2336155", SportarrLeagueId = "lg-1",
            Codec = "H264", Source = "WEB-DL", Language = "French", MultiLanguageNames = new() { "French", "English" }
        };
        cache.Store("row", Outcome(SearchTermination.UnknownTail) with { Releases = new() { release } }, 300);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            var restored = cache.TryGet("row", 300)!.Releases.Single();
            restored.Should().BeEquivalentTo(release);
            restored.MultiLanguageNames!.Add("changed");
            restored.Rejections.Add("current policy");
        })));
        cache.TryGet("row", 300)!.Releases.Single().Should().BeEquivalentTo(release);
    }

    [Fact]
    public void CombinedPayloadsStayWithinTheByteBudget()
    {
        var clock = new ManualClock();
        var cache = new SourceOutcomeCache(clock);
        var large = Outcome(SearchTermination.Exhausted);
        large.Releases[0].Title = new string('a', 9 * 1024 * 1024);
        cache.Store("old", large, 300);
        clock.Advance(1);
        cache.Store("new", large, 300);
        cache.TryGet("old", 300).Should().BeNull();
        cache.TryGet("new", 300).Should().NotBeNull();
    }

    [Fact]
    public void EntryAndPayloadLimitsEvictOldEvidence()
    {
        var clock = new ManualClock();
        var cache = new SourceOutcomeCache(clock);
        for (var number = 0; number < 600; number++)
        {
            cache.Store(number.ToString(), Outcome(SearchTermination.Exhausted), 300);
            clock.Advance(0.01);
        }
        cache.TryGet("0", 300).Should().BeNull();
        cache.TryGet("599", 300).Should().NotBeNull();
        var huge = Outcome(SearchTermination.Exhausted);
        huge.Releases[0].Title = new string('a', 17 * 1024 * 1024);
        cache.Store("oversized", huge, 300);
        cache.TryGet("oversized", 300).Should().BeNull();
        cache.Clear();
        cache.TryGet("599", 300).Should().BeNull();
    }

    [Fact]
    public void PageLimitedResultsKeepTheirRowsDiagnosticsAndOriginalExpiry()
    {
        var clock = new ManualClock();
        var cache = new SourceOutcomeCache(clock);
        var pages = Enumerable.Range(0, 5)
            .Select(page => new SearchPageObservation(page * 100, 100, 100, 100, null, null, true, "page-" + page)).ToArray();
        var rows = Enumerable.Range(0, 500).Select(row => new ReleaseSearchResult
        {
            Indexer = "Fixture", DownloadUrl = "http://fixture.invalid/offer/" + row, Guid = "offer-" + row, Title = "Formula1.2026.Round12.Race.1080p.WEB-DL-" + row,
            SourceQuality = "WEBDL-1080p"
        }).ToList();
        var outcome = new IndexerSearchOutcome(rows, SearchTermination.PageCeiling, 500, pages, true);
        cache.Store("limited", outcome, 60);
        clock.Advance(30);
        var restored = cache.TryGet("limited", 60);
        restored.Should().NotBeNull();
        if (restored == null) return;
        restored.SatisfiesRequest.Should().BeFalse();
        restored.Termination.Should().Be(SearchTermination.PageCeiling);
        restored.Pages.Should().Equal(pages);
        restored.RawCursor.Should().Be(500);
        restored.Releases.Select(row => row.Guid).Should().Equal(rows.Select(row => row.Guid));
        restored.Releases[0].Title = "changed";
        cache.TryGet("limited", 60)!.Releases[0].Title.Should().Be(rows[0].Title);
        using var aggregate = new SearchResultCache(NullLogger<SearchResultCache>.Instance, clock);
        aggregate.Store("limited", restored.Releases, 60, searchComplete: false, expiresAt: restored.CacheExpiresAt);
        clock.Advance(31);
        cache.TryGet("limited", 60).Should().BeNull();
        aggregate.TryGetCached("limited", 60).Should().BeNull();
    }

    [Fact]
    public void HealthyPageLimitCanBeReusedWithoutHidingAnInterruptedSource()
    {
        var page = new SearchPageObservation(400, 100, 100, 100, null, null, true, "last-page");
        var limited = new IndexerSearchDiagnostic(1, "Source", "Formula 1 2026", SearchTermination.PageCeiling,
            500, new[] { page }, true, false);
        var clean = new SearchOperationOutcome(new(), new[] { limited }, false);
        clean.CanCache.Should().BeTrue();
        clean.SatisfiesRequest.Should().BeFalse();
        foreach (var termination in new[] { SearchTermination.ProviderRateLimited, SearchTermination.ProviderFailure,
                     SearchTermination.LocalCancelled, SearchTermination.ParseIncomplete, SearchTermination.InvalidMetadata })
        {
            var interrupted = limited with { IndexerId = 2, Termination = termination };
            new SearchOperationOutcome(new(), new[] { limited, interrupted }, false).CanCache.Should().BeFalse();
        }
        var cache = new SourceOutcomeCache();
        cache.Store("failed", Outcome(SearchTermination.PageCeiling) with { Failure = new IOException() }, 60);
        cache.TryGet("failed", 60).Should().BeNull();
    }

    private static IndexerSearchOutcome Outcome(SearchTermination termination) => new(new()
    {
        new ReleaseSearchResult { Title = "Fixture.1080p.H264-GROUP", Indexer = "Fixture", IndexerId = 7,
            Guid = "fixture", DownloadUrl = "http://127.0.0.1/fixture", SourceQuality = "WEBDL-1080p", ReleaseGroup = "GROUP" }
    }, termination, 1, new[] { new SearchPageObservation(0, 100, 1, 1, 0, 1, true, "fixture") });

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(double seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
