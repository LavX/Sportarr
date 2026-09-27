using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.SearchValidation;

public sealed class PaginationOutcomeTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var protocol in new[] { IndexerType.Torznab, IndexerType.Newznab })
        foreach (var mode in new[] { "negative-total", "overflow-total", "bad-offset", "under-total",
                     "page-bound", "raw-duplicates", "unknown-tail", "ambiguous", "rate-limit", "malformed-page", "empty-before-total", "empty-no-total", "empty-zero-total", "changing-total" })
            yield return new object[] { protocol, mode };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DetailedOutcomePreservesRowsAndBoundsTraversal(IndexerType protocol, string mode)
    {
        var row = Row(protocol);
        if (mode == "ambiguous") row.AdditionalParameters = "limit=99&offset=7";
        using var handler = new SourceHandler(mode);
        using var http = new HttpClient(handler);
        var outcome = await Search(http, row, 20, "ev-2336155");
        var expected = mode switch
        {
            "page-bound" => SearchTermination.PageCeiling,
            "raw-duplicates" or "empty-no-total" or "empty-zero-total" => SearchTermination.Exhausted,
            "unknown-tail" => SearchTermination.UnknownTail,
            "ambiguous" => SearchTermination.AmbiguousPaging,
            "rate-limit" => SearchTermination.ProviderRateLimited,
            "malformed-page" => SearchTermination.ProviderFailure,
            _ => SearchTermination.InvalidMetadata
        };
        Assert.Equal(expected, outcome.Termination);
        Assert.Equal(mode is "raw-duplicates" or "empty-no-total" or "empty-zero-total", outcome.SatisfiesRequest);
        Assert.True(outcome.KnownPageLimit);
        var searches = handler.Requests.Where(uri => QueryHelpers.ParseQuery(uri.Query)["t"] == "search").ToArray();
        var expectedOffsets = mode == "ambiguous" ? new[] { 7 } : mode == "page-bound" ? new[] { 0, 2, 4, 6, 8 }
            : mode is "raw-duplicates" or "rate-limit" or "malformed-page" or "empty-before-total" or "empty-no-total" or "changing-total" ? new[] { 0, 2 } : new[] { 0 };
        Assert.Equal(expectedOffsets, searches.Select(ReadOffset));
        Assert.Equal(expectedOffsets.Length + 1, handler.Requests.Count);
        foreach (var uri in searches)
        {
            var limits = QueryHelpers.ParseQuery(uri.Query)["limit"];
            Assert.Equal("2", limits[0]);
            Assert.Equal(mode == "ambiguous" ? 2 : 1, limits.Count);
        }
        var expectedGuids = mode == "page-bound" ? Enumerable.Range(1, 10).Select(i => "offer-" + i)
            : mode == "ambiguous" ? new[] { "offer-8", "offer-9" }
            : mode == "raw-duplicates" ? new[] { "offer-1", "offer-3", "offer-4" }
            : mode == "changing-total" ? new[] { "offer-1", "offer-2", "offer-3", "offer-4" }
            : mode == "empty-zero-total" ? Array.Empty<string>()
            : mode == "unknown-tail" ? new[] { "offer-1" } : new[] { "offer-1", "offer-2" };
        Assert.Equal(expectedGuids, outcome.Releases.Select(row => row.Guid));
        Assert.All(outcome.Releases, result => Assert.Equal(row.Name, result.Indexer));
        Assert.Equal(mode is "rate-limit" or "malformed-page" ? 1 : expectedOffsets.Length, outcome.Pages.Count);
        Assert.Equal(mode is "raw-duplicates" or "changing-total" ? 4 : mode == "page-bound" ? 10 : mode == "unknown-tail" ? 1 : mode == "empty-zero-total" ? 0 : 2, outcome.RawCursor);
        if (mode == "ambiguous")
        {
            Assert.Equal(0, Assert.Single(outcome.Pages).RequestedOffset);
            Assert.Equal(7, Assert.Single(outcome.Pages).ReportedOffset);
        }
        if (mode == "raw-duplicates")
        {
            Assert.Equal(new[] { 2, 2 }, outcome.Pages.Select(page => page.RawCount));
            Assert.Equal(new[] { 2, 2 }, outcome.Pages.Select(page => page.ParsedCount));
        }
        if (mode == "rate-limit") Assert.Equal(TimeSpan.FromSeconds(17), Assert.IsType<IndexerRateLimitException>(outcome.Failure).RetryAfter);
        else if (mode == "malformed-page") Assert.NotNull(outcome.Failure);
        else Assert.Null(outcome.Failure);
    }

    [Theory]
    [InlineData(IndexerType.Torznab)]
    [InlineData(IndexerType.Newznab)]
    public async Task ColdNoIdKeepsOneSendAndAccountPathEditsDiscardCachedLimits(IndexerType protocol)
    {
        var row = Row(protocol);
        using var handler = new SourceHandler("identity");
        using var http = new HttpClient(handler);
        var cold = await Search(http, row, 1, null);
        Assert.Single(handler.Requests);
        Assert.False(cold.KnownPageLimit);
        Assert.Equal(SearchTermination.CallerCeiling, cold.Termination);
        Assert.Single(cold.Releases);

        var warm = await Search(http, row, 1, "ev-2336155");
        Assert.True(warm.KnownPageLimit);
        Assert.Equal(3, handler.Requests.Count);
        var again = await Search(http, row, 1, null);
        Assert.True(again.KnownPageLimit);
        Assert.Equal(4, handler.Requests.Count);

        row.ApiKey = "changed-account";
        var changed = await Search(http, row, 1, null);
        Assert.False(changed.KnownPageLimit);
        Assert.Equal(5, handler.Requests.Count);
        await Search(http, row, 1, "ev-2336155");
        Assert.Equal(7, handler.Requests.Count);
        row.ApiPath = "changed-path";
        var pathChanged = await Search(http, row, 1, null);
        Assert.False(pathChanged.KnownPageLimit);
        Assert.Equal(8, handler.Requests.Count);
        row.ApiPath = "api";
        row.AdditionalParameters = "account=other";
        var extraChanged = await Search(http, row, 1, null);
        Assert.False(extraChanged.KnownPageLimit);
        Assert.Equal(9, handler.Requests.Count);
        Assert.Equal(2, handler.Requests.Count(uri => QueryHelpers.ParseQuery(uri.Query)["t"] == "caps"));
        Assert.All(handler.Requests.Where(uri => QueryHelpers.ParseQuery(uri.Query)["t"] == "search"),
            uri => Assert.Equal("1", QueryHelpers.ParseQuery(uri.Query)["limit"].ToString()));
    }

    [Theory]
    [InlineData(IndexerType.Torznab)]
    [InlineData(IndexerType.Newznab)]
    public async Task AdvertisedMaximumCanExceedDefaultWithoutAddingPages(IndexerType protocol)
    {
        var row = Row(protocol);
        using var handler = new SourceHandler("max-above-default");
        using var http = new HttpClient(handler);
        var outcome = await Search(http, row, 1000, "ev-2336155");
        Assert.Equal(2, handler.Requests.Count);
        var search = handler.Requests.Single(uri => QueryHelpers.ParseQuery(uri.Query)["t"] == "search");
        Assert.Equal("1000", QueryHelpers.ParseQuery(search.Query)["limit"].ToString());
        Assert.Equal(1000, outcome.Releases.Count);
        Assert.Equal(Enumerable.Range(1, 1000).Select(i => "offer-" + i), outcome.Releases.Select(row => row.Guid));
        Assert.Equal(1000, outcome.RawCursor);
        var page = Assert.Single(outcome.Pages);
        Assert.Equal(1000, page.RequestedLimit);
        Assert.Equal(1000, page.RawCount);
        Assert.True(outcome.KnownPageLimit);
        Assert.True(outcome.SatisfiesRequest);
        Assert.Equal(SearchTermination.CallerCeiling, outcome.Termination);
    }

    [Theory]
    [InlineData("0", "-1", null, null)]
    [InlineData("bad", "2147483648", null, null)]
    [InlineData("2", "7", 2, 2)]
    public void CapsLimitsRejectInvalidValuesAndClampDefault(string max, string preferred, int? expectedMax, int? expectedDefault)
    {
        var caps = new TorznabCapabilities();
        TorznabClient.ParseCapabilitiesXml($"<caps><limits max='{max}' default='{preferred}'/></caps>", caps);
        Assert.Equal(expectedMax, caps.MaxPageSize);
        Assert.Equal(expectedDefault, caps.DefaultPageSize);
    }

    private static int ReadOffset(Uri uri)
    {
        var query = QueryHelpers.ParseQuery(uri.Query);
        return query.TryGetValue("offset", out var values) ? int.Parse(values[0]!) : 0;
    }

    private static Indexer Row(IndexerType protocol) => new()
    {
        Id = 71, Name = "Pagination outcome source", Type = protocol,
        Url = "http://" + Guid.NewGuid().ToString("N") + ".invalid", ApiPath = "api", ApiKey = "initial-account"
    };

    private static Task<IndexerSearchOutcome> Search(HttpClient http, Indexer row, int maximum, string? id) =>
        row.Type == IndexerType.Torznab
            ? new TorznabClient(http, NullLogger<TorznabClient>.Instance).SearchDetailedAsync(row, "fixed catalogue", maximum, id)
            : new NewznabClient(http, NullLogger<NewznabClient>.Instance).SearchDetailedAsync(row, "fixed catalogue", maximum, id);

    private sealed class SourceHandler(string mode) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (query["t"] == "caps" && mode == "max-above-default")
                return Reply("<caps><limits max='1000' default='100'/><searching><search available='yes' supportedParams='q,sportarrid'/></searching></caps>");
            if (query["t"] == "caps") return Reply("<caps><limits max='2' default='2'/><searching><search available='yes' supportedParams='q,sportarrid'/></searching></caps>");
            var offset = ReadOffset(uri);
            if (offset > 0 && mode == "rate-limit")
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
                return Task.FromResult(response);
            }
            if (offset > 0 && mode == "malformed-page") return Reply("<rss><channel>");
            var count = mode == "max-above-default" ? Math.Min(1000, int.Parse(query["limit"].First()!))
                : mode == "unknown-tail" ? 1
                : mode == "empty-zero-total" || (offset > 0 && mode is "empty-before-total" or "empty-no-total") ? 0 : 2;
            var ids = Enumerable.Range(offset + 1, count).ToArray();
            if (mode == "raw-duplicates" && offset == 0) ids = new[] { 1, 1 };
            var total = mode switch { "negative-total" => "-1", "overflow-total" => "2147483648", "under-total" => "1", "raw-duplicates" => "4", "empty-zero-total" => "0", _ => "1000" };
            if (mode == "changing-total" && offset > 0) total = "4";
            var reportedOffset = mode == "bad-offset" ? "bad" : offset.ToString();
            XNamespace ns = "http://www.newznab.com/DTD/2010/feeds/attributes/";
            var metadata = new XElement(ns + "response", new XAttribute("offset", reportedOffset));
            if (mode is not "unknown-tail" and not "empty-no-total") metadata.Add(new XAttribute("total", total));
            var items = ids.Select(id => new XElement("item", new XElement("title", "Fixed.Catalogue.1080p-GROUP"),
                new XElement("guid", "offer-" + id), new XElement("link", "http://owned.invalid/offer-" + id)));
            return Reply(new XDocument(new XElement("rss", new XElement("channel", metadata, items))).ToString());
        }
        private static Task<HttpResponseMessage> Reply(string xml) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml") });
    }
}
