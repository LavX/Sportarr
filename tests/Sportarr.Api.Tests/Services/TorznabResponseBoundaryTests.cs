using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Helpers;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class TorznabResponseBoundaryTests
{
    [Theory]
    [InlineData("search")]
    [InlineData("rss")]
    [InlineData("caps")]
    public async Task RemoteBodiesUseStreamingAndRejectOversizedContent(string route)
    {
        var completionOptions = new List<HttpCompletionOption>();
        var handler = new OversizedHandler(completionOptions);
        using var http = new HttpClient(handler);
        var client = new TorznabClient(http, NullLogger<TorznabClient>.Instance);
        client.SearchRequestSender = async (request, option) =>
        {
            completionOptions.Add(option);
            return await http.SendAsync(request, option);
        };
        var row = new Indexer { Id = 701, Name = "Boundary source", Type = IndexerType.Torznab,
            Url = "http://fixture.invalid", ApiPath = "/api", ApiKey = "fixture" };

        if (route == "search")
        {
            var outcome = await client.SearchDetailedAsync(row, "fixture");
            Assert.IsType<InvalidOperationException>(outcome.Failure);
        }
        else if (route == "rss")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchRssFeedAsync(row));
        }
        else
        {
            var caps = await client.GetCapabilitiesAsync(row);
            Assert.Null(caps);
        }

        Assert.All(completionOptions, option => Assert.Equal(HttpCompletionOption.ResponseHeadersRead, option));
        Assert.Equal(HttpCompletionOption.ResponseHeadersRead, handler.CompletionOption);
    }

    private sealed class OversizedHandler(List<HttpCompletionOption> completionOptions) : HttpMessageHandler
    {
        public HttpCompletionOption CompletionOption => completionOptions.LastOrDefault();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent("<rss><channel /></rss>");
            content.Headers.ContentLength = BoundedHttpContent.DefaultMaxBytes + 1;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }
}
