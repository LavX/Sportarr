using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Xunit;

namespace Sportarr.Api.Tests.SearchValidation;

public class RawIndexerRetrievalCacheTests
{
    [Theory]
    [InlineData(IndexerType.Newznab)]
    [InlineData(IndexerType.Torznab)]
    public async Task UnsupportedEventIdsReuseRawDataWithoutSharingMutations(IndexerType protocol)
    {
        using var rig = new Rig(protocol);
        var first = await rig.Search("ev-1000001");
        var expected = JsonSerializer.Serialize(first.Releases);
        first.Releases[0].Title = "Mutated";
        first.Releases[0].SourceQuality = "Mutated";
        first.Releases[0].Rejections.Add("Current event rejection");
        first.Releases[0].MultiLanguageNames = new() { "French" };
        var second = await rig.Search("ev-1000002");
        Assert.Equal(1, rig.Searches);
        Assert.Equal(expected, JsonSerializer.Serialize(second.Releases));
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(first.CacheExpiresAt, second.CacheExpiresAt);
        Assert.False(string.IsNullOrWhiteSpace(second.Releases[0].Quality));
        Assert.Equal(second.Releases[0].Quality, second.Releases[0].SourceQuality);
        Assert.Equal(first.Pages, second.Pages);
    }

    [Theory]
    [InlineData(IndexerType.Newznab)]
    [InlineData(IndexerType.Torznab)]
    public async Task AdvertisedEventIdsRemainDistinct(IndexerType protocol)
    {
        using var rig = new Rig(protocol) { NativeIds = true };
        var first = await rig.Search("ev-1000001");
        var second = await rig.Search("ev-1000002");
        Assert.Equal(2, rig.Searches);
        Assert.NotEqual(first.Releases[0].Title, second.Releases[0].Title);
        Assert.False(second.FromCache);
        var repeat = await rig.Search("ev-1000002");
        Assert.Equal(2, rig.Searches);
        Assert.True(repeat.FromCache);
    }

    [Theory]
    [InlineData(IndexerType.Newznab)]
    [InlineData(IndexerType.Torznab)]
    public async Task RetrievalInputsAndSourcePartitionsRemainSeparate(IndexerType protocol)
    {
        using var rig = new Rig(protocol);
        await rig.Search("ev-1000001");
        rig.Config.Categories = new() { "5030" };
        await rig.Search("ev-1000002");
        rig.Config.ApiKey = "second-key";
        await rig.Search("ev-1000002");
        rig.Http.DefaultRequestHeaders.Add("Authorization", "Bearer fixture");
        await rig.Search("ev-1000002");
        await rig.Search("ev-1000002", maximum: 50);
        rig.Partition = "another-source";
        await rig.Search("ev-1000002", maximum: 50);
        Assert.Equal(6, rig.Searches);
    }

    [Theory]
    [InlineData(IndexerType.Newznab)]
    [InlineData(IndexerType.Torznab)]
    public async Task ConcurrentCallsFillOnceAndReturnIndependentRows(IndexerType protocol)
    {
        using var rig = new Rig(protocol);
        var outcomes = await Task.WhenAll(Enumerable.Range(1, 12).Select(i => rig.Search("ev-" + (1000000 + i))))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, rig.Searches);
        Assert.Single(outcomes.Where(r => !r.FromCache));
        outcomes[0].Releases[0].Rejections.Add("Changed");
        Assert.All(outcomes.Skip(1), r => Assert.Empty(r.Releases[0].Rejections));
    }

    [Theory]
    [InlineData(IndexerType.Newznab)]
    [InlineData(IndexerType.Torznab)]
    public async Task ExplicitRefreshReplacesRawEvidence(IndexerType protocol)
    {
        using var rig = new Rig(protocol);
        var first = await rig.Search("ev-1000001");
        rig.Title = "Changed.1080p.WEB-DL.H264-GROUP";
        var cached = await rig.Search("ev-1000002");
        Assert.Equal(first.Releases[0].Title, cached.Releases[0].Title);
        var refreshed = await rig.Search("ev-1000002", force: true);
        Assert.Equal(rig.Title, refreshed.Releases[0].Title);
        Assert.False(refreshed.FromCache);
        Assert.Equal(2, rig.Searches);
        var repeat = await rig.Search("ev-1000001");
        Assert.True(repeat.FromCache);
        Assert.Equal(rig.Title, repeat.Releases[0].Title);
    }

    [Theory]
    [InlineData(IndexerType.Newznab)]
    [InlineData(IndexerType.Torznab)]
    public async Task PositiveAndEmptyEntriesExpireWithoutRenewal(IndexerType protocol)
    {
        using var rig = new Rig(protocol) { Seconds = 120 };
        var first = await rig.Search("ev-1000001");
        rig.Clock.Advance(100);
        var hit = await rig.Search("ev-1000002");
        Assert.Equal(first.CacheExpiresAt, hit.CacheExpiresAt);
        Assert.Equal(1, rig.Searches);
        rig.Clock.Advance(21);
        await rig.Search("ev-1000003");
        Assert.Equal(2, rig.Searches);
        rig.Cache.Clear();
        rig.Empty = true;
        await rig.Search("ev-1000001");
        rig.Clock.Advance(59);
        await rig.Search("ev-1000002");
        Assert.Equal(3, rig.Searches);
        rig.Clock.Advance(2);
        await rig.Search("ev-1000003");
        Assert.Equal(4, rig.Searches);
    }

    [Theory]
    [InlineData(IndexerType.Newznab, false)]
    [InlineData(IndexerType.Newznab, true)]
    [InlineData(IndexerType.Torznab, false)]
    [InlineData(IndexerType.Torznab, true)]
    public async Task FailedAndMalformedResponsesDoNotHideRecovery(IndexerType protocol, bool malformed)
    {
        using var rig = new Rig(protocol) { Failure = !malformed, Malformed = malformed };
        var failed = await rig.Search("ev-1000001");
        Assert.NotNull(failed.Failure);
        rig.Failure = false; rig.Malformed = false;
        var recovered = await rig.Search("ev-1000002");
        Assert.Null(recovered.Failure);
        Assert.Single(recovered.Releases);
        Assert.Equal(2, rig.Searches);
    }

    [Theory]
    [InlineData(IndexerType.Newznab)]
    [InlineData(IndexerType.Torznab)]
    public async Task RefreshWaitsForAnOlderFillAndReplacesItsEvidence(IndexerType protocol)
    {
        using var rig = new Rig(protocol);
        var barrier = new ResponseBarrier();
        rig.NextBarrier = barrier;
        var original = rig.Search("ev-1000001");
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Title = "Fresh.1080p.WEB-DL.H264-GROUP";
        var refresh = rig.Search("ev-1000002", force: true);
        Assert.False(refresh.IsCompleted);
        Assert.Equal(1, rig.Searches);
        barrier.Release.TrySetResult();
        var first = await original.WaitAsync(TimeSpan.FromSeconds(5));
        var fresh = await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(first.Releases[0].Title, fresh.Releases[0].Title);
        Assert.Equal(rig.Title, fresh.Releases[0].Title);
        var hit = await rig.Search("ev-1000003");
        Assert.Equal(fresh.Releases[0].Title, hit.Releases[0].Title);
        Assert.Equal(2, rig.Searches);
    }

    [Theory]
    [InlineData(IndexerType.Newznab)]
    [InlineData(IndexerType.Torznab)]
    public async Task AnInterruptedSecondPageCannotBecomeReusableEvidence(IndexerType protocol)
    {
        using var rig = new Rig(protocol) { PagedResponse = true, FailSecondPage = true };
        var partial = await rig.Search("ev-1000001");
        Assert.Single(partial.Releases);
        Assert.NotNull(partial.Failure);
        Assert.Equal(2, rig.Searches);
        rig.FailSecondPage = false;
        var recovered = await rig.Search("ev-1000002");
        Assert.Null(recovered.Failure);
        Assert.Equal(2, recovered.Releases.Count);
        Assert.Equal(4, rig.Searches);
        var repeat = await rig.Search("ev-1000003");
        Assert.True(repeat.FromCache);
        Assert.Equal(2, repeat.Releases.Count);
        Assert.Equal(4, rig.Searches);
    }

    [Fact]
    public async Task ChangedCapabilityLimitsRequireNewRetrieval()
    {
        using var rig = new Rig(IndexerType.Newznab);
        var cache = new RawIndexerRetrievalCache(rig.Cache, 300, false, "source");
        var caps = new TorznabCapabilities { MaxPageSize = 100, DefaultPageSize = 100 };
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://fixture.invalid/api?t=search&q=fixture");
        var fetches = 0;
        Task<IndexerSearchOutcome> Fetch()
        {
            fetches++;
            return Task.FromResult(new IndexerSearchOutcome(new(), SearchTermination.Exhausted, 0, Array.Empty<SearchPageObservation>()));
        }
        await cache.GetOrFetchAsync(rig.Http, rig.Config, request, 100, caps, false, Fetch);
        await cache.GetOrFetchAsync(rig.Http, rig.Config, request, 100, caps, false, Fetch);
        Assert.Equal(1, fetches);
        caps.MaxPageSize = 50;
        await cache.GetOrFetchAsync(rig.Http, rig.Config, request, 100, caps, false, Fetch);
        Assert.Equal(2, fetches);
        caps.DefaultPageSize = 50;
        await cache.GetOrFetchAsync(rig.Http, rig.Config, request, 100, caps, false, Fetch);
        Assert.Equal(3, fetches);
    }

    [Fact]
    public void PromotedSourceEvidenceCannotExtendItsDeadline()
    {
        var clock = new ManualClock();
        var cache = new SourceOutcomeCache(clock);
        var outcome = new IndexerSearchOutcome(new() { new ReleaseSearchResult { Title = "Fixture", Guid = "fixture", Indexer = "Fixture", DownloadUrl = "http://fixture.invalid/download" } },
            SearchTermination.Exhausted, 1, Array.Empty<SearchPageObservation>());
        cache.Store("raw", outcome, 60);
        clock.Advance(20);
        var restored = cache.TryGet("raw", 60)!;
        cache.Store("source", restored, 300);
        Assert.Equal(restored.CacheExpiresAt, cache.TryGet("source", 300)!.CacheExpiresAt);
        clock.Advance(41);
        Assert.Null(cache.TryGet("source", 300));
    }

    private sealed class ResponseBarrier
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(int seconds) => now += TimeSpan.FromSeconds(seconds);
    }

    private sealed class Rig : HttpMessageHandler
    {
        internal readonly ManualClock Clock = new();
        internal readonly SourceOutcomeCache Cache;
        internal readonly HttpClient Http;
        internal readonly Indexer Config;
        internal string Partition = "source";
        internal string Title = "NFL.2026.08.14.Broncos.vs.Falcons.1080p.WEB-DL.H264-GROUP";
        internal bool NativeIds, Empty, Failure, Malformed, PagedResponse, FailSecondPage;
        internal ResponseBarrier? NextBarrier;
        internal int Seconds = 300;
        private int searches;
        internal int Searches => Volatile.Read(ref searches);
        private readonly IndexerType protocol;
        internal Rig(IndexerType protocol)
        {
            this.protocol = protocol;
            Cache = new(Clock);
            Http = new(this, disposeHandler: false);
            Config = new Indexer { Id = 1, Name = "Fixture", Type = protocol,
                Url = "http://fixture.invalid/" + Guid.NewGuid().ToString("N"), ApiPath = "/api", ApiKey = "fixture",
                Categories = new() { "5060" }, MultiLanguages = new() { "English" } };
        }
        internal Task<IndexerSearchOutcome> Search(string eventId, int maximum = 100, bool force = false)
        {
            var cache = new RawIndexerRetrievalCache(Cache, Seconds, force, Partition);
            if (protocol == IndexerType.Torznab)
                return new TorznabClient(Http, NullLogger<TorznabClient>.Instance) { RetrievalCache = cache }
                    .SearchDetailedAsync(Config, "NFL 2026 08", maximum, eventId);
            return new NewznabClient(Http, NullLogger<NewznabClient>.Instance) { RetrievalCache = cache }
                .SearchDetailedAsync(Config, "NFL 2026 08", maximum, eventId);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (query["t"] == "caps")
                return Xml("<caps><limits max=\"100\" default=\"100\"/><searching><search available=\"yes\" supportedParams=\"q" + (NativeIds ? ",sportarrid" : "") + "\"/></searching></caps>");
            Interlocked.Increment(ref searches);
            var title = Title;
            if (Interlocked.Exchange(ref NextBarrier, null) is { } barrier)
            {
                barrier.Started.TrySetResult();
                await barrier.Release.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            await Task.Delay(10, cancellationToken);
            var offset = query.TryGetValue("offset", out var offsetValue) && int.TryParse(offsetValue, out var parsedOffset) ? parsedOffset : 0;
            if (offset > 0 && FailSecondPage) return new(HttpStatusCode.TooManyRequests);
            if (Failure) return new(HttpStatusCode.TooManyRequests);
            if (Malformed) return Xml("<not-rss/>");
            var id = query.TryGetValue("sportarrid", out var native) ? native.ToString() : "";
            if (PagedResponse) id += "-page-" + offset;
            var item = Empty ? "" : "<item><title>" + title + id + "</title><guid>fixture" + id + "</guid><link>http://fixture.invalid/download</link><enclosure url=\"http://fixture.invalid/download\" length=\"1500000000\" type=\"application/x-bittorrent\"/><pubDate>Sat, 15 Aug 2026 12:00:00 GMT</pubDate><newznab:attr name=\"size\" value=\"1500000000\"/><newznab:attr name=\"seeders\" value=\"10\"/></item>";
            return Xml("<rss xmlns:newznab=\"http://www.newznab.com/DTD/2010/feeds/attributes/\"><channel><newznab:response offset=\"" + offset + "\" total=\"" + (Empty ? 0 : PagedResponse ? 2 : 1) + "\"/>" + item + "</channel></rss>");
        }
        private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
        protected override void Dispose(bool disposing) { if (disposing) Http.Dispose(); base.Dispose(disposing); }
    }
}
