using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Api.Tests.SearchValidation;

public class RssAcceptedDownloadTests
{
    [Fact]
    public async Task CancellationAfterClientAcceptance_PreservesQueueAndGrabHistory()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "sportarr-rss-accepted-" + Guid.NewGuid());
        Directory.CreateDirectory(dataPath);
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var http = new HttpClient(new AcceptingClientHandler(cancellation));
            var httpFactory = Mock.Of<IHttpClientFactory>(f => f.CreateClient(It.IsAny<string>()) == http);
            var databaseName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<SportarrDbContext>(o => o.UseInMemoryDatabase(databaseName));
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();
            var client = new DownloadClient
            {
                Name = "Accepted job fixture", Type = DownloadClientType.NZBdav,
                Host = "client.invalid", Port = 8080, Enabled = true, Category = "sports"
            };
            var evt = new Event { Title = "UFC 9999", Sport = "Fighting", EventDate = DateTime.UtcNow.AddDays(-1) };
            db.DownloadClients.Add(client);
            db.Events.Add(evt);
            await db.SaveChangesAsync();
            var config = new ConfigService(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Sportarr:DataPath"] = dataPath }).Build(),
                NullLogger<ConfigService>.Instance);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var downloads = new DownloadClientService(httpFactory, NullLoggerFactory.Instance,
                NullLogger<DownloadClientService>.Instance, cache, config, Mock.Of<IRemotePathMappingService>(), new DownloadOwnershipCoordinator());
            var notifications = new NotificationService(provider, NullLogger<NotificationService>.Instance, http, httpFactory);
            using var sync = new RssSyncService(provider, NullLogger<RssSyncService>.Instance);
            var release = new ReleaseSearchResult
            {
                Title = "UFC.9999.Main.Card.720p.WEB-DL", Protocol = "Usenet", Indexer = "Fixture",
                DownloadUrl = "http://source.invalid/release.nzb", Guid = "fixture-main-card", Size = 1000000
            };
            var method = typeof(RssSyncService).GetMethod("GrabReleaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var grab = (Task<bool>)method.Invoke(sync, new object?[]
                { db, evt, release, downloads, notifications, "Main Card", cancellation.Token })!;

            (await grab).Should().BeTrue();

            cancellation.IsCancellationRequested.Should().BeTrue();
            db.ChangeTracker.Clear();
            var queued = await db.DownloadQueue.SingleAsync();
            queued.DownloadId.Should().Be("accepted-job-1");
            queued.EventId.Should().Be(evt.Id);
            queued.Part.Should().Be("Main Card");
            queued.IsManualSearch.Should().BeFalse();
            var history = await db.GrabHistory.SingleAsync();
            history.DownloadId.Should().Be("accepted-job-1");
            history.EventId.Should().Be(evt.Id);
            history.PartName.Should().Be("Main Card");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationAfterClientRemoval_RemovesTheStaleQueueRow()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "sportarr-rss-removed-" + Guid.NewGuid());
        Directory.CreateDirectory(dataPath);
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var handler = new RemovingClientHandler(cancellation);
            using var http = new HttpClient(handler);
            var httpFactory = Mock.Of<IHttpClientFactory>(f => f.CreateClient(It.IsAny<string>()) == http);
            var databaseName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<SportarrDbContext>(o => o.UseInMemoryDatabase(databaseName));
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();
            var client = new DownloadClient
            {
                Name = "Removed job fixture", Type = DownloadClientType.NZBdav,
                Host = "client.invalid", Port = 8080, Enabled = true, Category = "sports"
            };
            var evt = new Event { Title = "UFC 9999", Sport = "Fighting", EventDate = DateTime.UtcNow.AddDays(-1) };
            db.DownloadClients.Add(client);
            db.Events.Add(evt);
            await db.SaveChangesAsync();
            var queued = new DownloadQueueItem
            {
                EventId = evt.Id, DownloadClientId = client.Id, DownloadId = "removed-job-1",
                Title = "UFC.9999.Main.Card.720p.WEB-DL", Part = "Main Card", Protocol = "Usenet"
            };
            db.DownloadQueue.Add(queued);
            await db.SaveChangesAsync();
            var config = new ConfigService(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Sportarr:DataPath"] = dataPath }).Build(),
                NullLogger<ConfigService>.Instance);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var downloads = new DownloadClientService(httpFactory, NullLoggerFactory.Instance,
                NullLogger<DownloadClientService>.Instance, cache, config, Mock.Of<IRemotePathMappingService>(), new DownloadOwnershipCoordinator());
            using var sync = new RssSyncService(provider, NullLogger<RssSyncService>.Instance);
            var method = typeof(RssSyncService).GetMethod("RemoveAndCancelQueueItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var removal = (Task)method.Invoke(sync, new object[] { db, queued, downloads, cancellation.Token })!;

            await removal;

            cancellation.IsCancellationRequested.Should().BeTrue();
            handler.Deleted.Should().BeTrue();
            db.ChangeTracker.Clear();
            (await db.DownloadQueue.AnyAsync()).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private sealed class RemovingClientHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        public bool Deleted { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            request.RequestUri!.Host.Should().Be("client.invalid");
            var query = request.RequestUri.Query;
            if (query.Contains("mode=queue") && !query.Contains("name=delete"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"queue\":{\"slots\":[{\"nzo_id\":\"removed-job-1\"}]}}")
                });
            }
            if (query.Contains("mode=history") && !query.Contains("name=delete"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"history\":{\"slots\":[]}}")
                });
            }
            query.Should().Contain("name=delete");
            query.Should().Contain("del_files=1");
            Deleted = true;
            cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":true,\"nzo_ids\":[\"removed-job-1\"]}")
            });
        }
    }

    private sealed class AcceptingClientHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            request.RequestUri!.Host.Should().Be("client.invalid");
            cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":true,\"nzo_ids\":[\"accepted-job-1\"]}")
            });
        }
    }
}
