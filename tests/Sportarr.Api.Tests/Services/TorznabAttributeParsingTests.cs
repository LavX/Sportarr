using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class TorznabAttributeParsingTests
{
    private sealed class FeedHandler(string xml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });
    }

    [Theory]
    [InlineData("http://torznab.com/schemas/2015/feed")]
    [InlineData("http://www.newznab.com/DTD/2010/feeds/attributes/")]
    public async Task Search_parses_release_attributes_from_supported_feed_namespaces(string attributeNamespace)
    {
        var xml = $$"""
            <?xml version="1.0"?>
            <rss version="2.0" xmlns:a="{{attributeNamespace}}"
                 xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/">
              <channel>
                <newznab:response offset="0" total="1" />
                <item>
                  <title>UFC.9999.2026.09.01.Main.Card.1080p.WEB-DL.H264-GROUP</title>
                  <guid>fixture-release</guid>
                  <link>http://indexer/release.torrent</link>
                  <pubDate>Tue, 01 Sep 2026 20:00:00 GMT</pubDate>
                  <a:attr name="infohash" value="0123456789abcdef0123456789abcdef01234567" />
                  <a:attr name="size" value="123456" />
                  <a:attr name="seeders" value="42" />
                  <a:attr name="peers" value="7" />
                  <a:attr name="sportarrid" value="ev-1234567" />
                </item>
              </channel>
            </rss>
            """;
        var client = new TorznabClient(
            new HttpClient(new FeedHandler(xml)),
            NullLogger<TorznabClient>.Instance);
        var indexer = new Indexer
        {
            Id = 1,
            Name = "Fixture",
            Url = "http://indexer",
            ApiPath = "/api",
            ApiKey = "key"
        };

        var release = (await client.SearchAsync(indexer, "UFC 9999", maxResults: 1)).Single();

        release.TorrentInfoHash.Should().Be("0123456789abcdef0123456789abcdef01234567");
        release.Size.Should().Be(123456);
        release.Seeders.Should().Be(42);
        release.Leechers.Should().Be(7);
        release.SportarrEventId.Should().Be("ev-1234567");
    }
}
