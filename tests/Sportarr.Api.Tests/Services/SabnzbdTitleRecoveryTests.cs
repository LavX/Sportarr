using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class SabnzbdTitleRecoveryTests
{
    private sealed class SabHistoryHandler(
        string historySlots,
        string queueSlots = "[]") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var mode = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["mode"];
            var body = mode == "history"
                ? "{\"history\":{\"slots\":" + historySlots + "}}"
                : "{\"queue\":{\"slots\":" + queueSlots + "}}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private static DownloadClient Config() => new()
    {
        Name = "NZBdav",
        Type = DownloadClientType.NZBdav,
        Host = "nzbdav",
        Port = 3000,
        ApiKey = "key",
        Category = "sports"
    };

    private static SabnzbdClient Client(string historySlots, string queueSlots = "[]") =>
        new(
            new HttpClient(new SabHistoryHandler(historySlots, queueSlots)),
            Mock.Of<ILogger<SabnzbdClient>>());

    private const string CompletedMismatch = """
        [{
            "nzo_id": "nzo-270",
            "name": "EPL.26-27.Matchday.1.Arsenal.vs.Coventry",
            "status": "Completed",
            "category": "uncategorized",
            "bytes": 1000,
            "storage": "/completed/EPL.26-27.Matchday.1.Arsenal.vs.Coventry"
        }]
        """;

    [Fact]
    public async Task KnownId_RemainsAuthoritativeWhenCategoryWasMisfiled()
    {
        var status = await Client(CompletedMismatch)
            .GetDownloadStatusAsync(Config(), "nzo-270", "sports");

        status.Should().NotBeNull();
        status!.Status.Should().Be("completed");
    }

    [Fact]
    public async Task KnownId_RemainsAuthoritativeInTheQueueWhenCategoryWasMisfiled()
    {
        const string queue = """
            [{
                "nzo_id": "nzo-queue",
                "filename": "EPL.26-27.Matchday.1.Arsenal.vs.Coventry",
                "status": "Downloading",
                "cat": "uncategorized",
                "mb": "1000",
                "mbleft": "500",
                "percentage": "50"
            }]
            """;

        var status = await Client("[]", queue)
            .GetDownloadStatusAsync(Config(), "nzo-queue", "sports");

        status.Should().NotBeNull();
        status!.Status.Should().Be("downloading");
        status.Progress.Should().Be(50);
    }

    [Fact]
    public async Task ExactTitle_RecoversAChangedIdWhenOnlyOneCandidateMatches()
    {
        var (status, id) = await Client(CompletedMismatch).FindDownloadByTitleAsync(
            Config(),
            "EPL.26-27.Matchday.1.Arsenal.vs.Coventry.nzb",
            "sports");

        id.Should().Be("nzo-270");
        status.Should().NotBeNull();
        status!.Status.Should().Be("completed");
    }

    [Fact]
    public async Task TitleRecovery_RejectsPrefixOnlyMatches()
    {
        var (status, id) = await Client(CompletedMismatch).FindDownloadByTitleAsync(
            Config(),
            "EPL.26-27.Matchday.1.Arsenal",
            "sports");

        status.Should().BeNull();
        id.Should().BeNull();
    }

    [Fact]
    public async Task TitleRecovery_RejectsAmbiguousMatchesOutsideTheExpectedCategory()
    {
        const string duplicateHistory = """
            [
                { "nzo_id": "old", "name": "Same.Release", "status": "Completed", "category": "other", "storage": "/completed/old" },
                { "nzo_id": "new", "name": "Same.Release.nzb", "status": "Completed", "category": "uncategorized", "storage": "/completed/new" }
            ]
            """;

        var (status, id) = await Client(duplicateHistory).FindDownloadByTitleAsync(
            Config(),
            "Same.Release",
            "sports");

        status.Should().BeNull();
        id.Should().BeNull();
    }

    [Fact]
    public async Task TitleRecovery_PrefersTheExpectedCategory()
    {
        const string duplicateHistory = """
            [
                { "nzo_id": "wrong", "name": "Same.Release", "status": "Completed", "category": "other", "storage": "/completed/wrong" },
                { "nzo_id": "right", "name": "Same.Release.nzb", "status": "Completed", "category": "sports", "storage": "/completed/right" }
            ]
            """;

        var (status, id) = await Client(duplicateHistory).FindDownloadByTitleAsync(
            Config(),
            "Same.Release",
            "sports");

        id.Should().Be("right");
        status.Should().NotBeNull();
        status!.SavePath.Should().Be("/completed/right");
    }
}
