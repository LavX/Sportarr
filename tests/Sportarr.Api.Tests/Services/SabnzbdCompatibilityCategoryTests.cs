using System.Net;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class SabnzbdCompatibilityCategoryTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Upload { get; private set; }
        public string? UploadBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath != "/api")
            {
                var nzb = "<?xml version=\"1.0\"?><nzb><file subject=\"x\">" + new string('x', 200) + "</file></nzb>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nzb) };
            }

            Upload = request;
            UploadBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":true,\"nzo_ids\":[\"nzo-270\"]}")
            };
        }
    }

    private sealed class FallbackCapturingHandler : HttpMessageHandler
    {
        public List<(string Query, string Body)> Uploads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath != "/api")
            {
                var nzb = "<?xml version=\"1.0\"?><nzb><file subject=\"x\">" + new string('x', 200) + "</file></nzb>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nzb) };
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Uploads.Add((request.RequestUri!.Query, body));
            return Uploads.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"status\":false,\"error\":\"unsupported request layout\"}")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":true,\"nzo_ids\":[\"nzo-fallback\"]}")
                };
        }
    }

    private static SabnzbdClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Mock.Of<ILogger<SabnzbdClient>>());

    private static string[] Keys(string queryOrForm) =>
        HttpUtility.ParseQueryString(queryOrForm.TrimStart('?'))
            .AllKeys
            .Where(key => key != null)
            .Select(key => key!)
            .ToArray();

    [Fact]
    public async Task AddFile_SendsCategoryAliasesWithoutDuplicatingAKey()
    {
        var handler = new CapturingHandler();
        var config = new DownloadClient
        {
            Name = "NZBdav",
            Type = DownloadClientType.NZBdav,
            Host = "nzbdav",
            Port = 3000,
            ApiKey = "key"
        };

        var id = await Client(handler).AddNzbAsync(
            config,
            "http://indexer/get/release.nzb",
            "sports",
            "EPL.Match");

        id.Should().Be("nzo-270");
        handler.Upload.Should().NotBeNull();
        handler.Upload!.RequestUri!.Query.Should().Contain("category=sports");
        handler.UploadBody.Should().Contain("name=cat\r\n");
        handler.UploadBody.Should().NotContain("name=category\r\n");
        Keys(handler.Upload.RequestUri.Query)
            .Intersect(new[] { "cat", "category" })
            .Should().ContainSingle().Which.Should().Be("category");
    }

    [Fact]
    public async Task AddFile_QueryFallback_SplitsCategoryAliasesAcrossQueryAndBody()
    {
        var handler = new FallbackCapturingHandler();
        var config = new DownloadClient
        {
            Name = "NZBdav",
            Type = DownloadClientType.NZBdav,
            Host = "nzbdav",
            Port = 3000,
            ApiKey = "key"
        };

        var id = await Client(handler).AddNzbAsync(
            config,
            "http://indexer/get/release.nzb",
            "sports",
            "EPL.Match");

        id.Should().Be("nzo-fallback");
        handler.Uploads.Should().HaveCount(2);

        var fallback = handler.Uploads[1];
        fallback.Query.Should().Contain("mode=addfile").And.Contain("cat=sports");
        fallback.Query.Should().NotContain("category=sports");
        fallback.Body.Should().Contain("name=category\r\n");
        fallback.Body.Should().NotContain("name=cat\r\n");
    }

    [Fact]
    public void HistorySlot_BindsEitherCategoryField()
    {
        var longName = JsonSerializer.Deserialize<SabnzbdHistoryItem>(
            "{\"category\":\"sports\"}");
        var shortName = JsonSerializer.Deserialize<SabnzbdHistoryItem>(
            "{\"cat\":\"sports\"}");

        longName!.CategoryName.Should().Be("sports");
        shortName!.CategoryName.Should().Be("sports");
    }
}
