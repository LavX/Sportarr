using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.SearchValidation;

internal static class CacheEvidenceFixtures
{
    internal static ReleaseSearchResult Release(string title = "Spain.vs.Belgium.2026-07-10.1080p.WEB-DL.H264-GROUP") => new()
    {
        Title = title,
        Guid = "source-release-1",
        DownloadUrl = "http://source.invalid/release.torrent",
        InfoUrl = "http://source.invalid/details/1",
        Indexer = "Source (via Proxy)",
        IndexerId = 7,
        Protocol = "Torrent",
        TorrentInfoHash = new string('a', 40),
        Size = 1024L * 1024 * 1024,
        PublishDate = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc),
        Seeders = 10,
        Leechers = 2,
        IndexerFlags = "freeleech",
        Language = "English",
        Source = "WEB-DL",
        Codec = "H264",
        ReleaseGroup = "GROUP"
    };

    internal static SearchResultCache Cache() => new(NullLogger<SearchResultCache>.Instance);

    internal static ReleaseSearchResult Restore(SearchResultCache cache)
    {
        var cached = cache.TryGetCached("fixture-query", 300);
        Assert.NotNull(cached);
        return Assert.Single(cache.ToSearchResults(cached!));
    }

    internal static ReleaseSearchResult RoundTrip(ReleaseSearchResult release)
    {
        using var cache = Cache();
        cache.Store("fixture-query", new[] { release });
        return Restore(cache);
    }

    internal static Event Event() => new()
    {
        Id = 1,
        ExternalId = "ev-2336155",
        Title = "Spain vs Belgium",
        Sport = "Soccer",
        HomeTeamName = "Spain",
        AwayTeamName = "Belgium",
        EventDate = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
        League = new League { Id = 1, ExternalId = "lg-000123", Name = "FIFA World Cup", Sport = "Soccer" }
    };

    internal static ReleaseMatchingService Matcher() => new(
        NullLogger<ReleaseMatchingService>.Instance,
        new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
        new EventPartDetector(NullLogger<EventPartDetector>.Instance));
}
