using System.Net;
using System.Text;
using System.Collections.Concurrent;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Sportarr.Api.Tests.Services;

public class HealthCheckServiceTests : IDisposable
{
    private readonly string _tempDataPath;

    public HealthCheckServiceTests()
    {
        _tempDataPath = Path.Combine(Path.GetTempPath(), "sportarr-healthcheck-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
            Directory.Delete(_tempDataPath, recursive: true);
    }

    private static SportarrDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<SportarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SportarrDbContext(options);
    }

    private class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;
        public StubHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Simulates a real connectivity failure (DNS/refused/timeout), distinct
    /// from an HTTP error response - PingAsync treats ANY response, even a
    /// 5xx, as proof of reachability, so only this proves "unreachable".
    /// </summary>
    private class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Simulated connection failure");
        }
    }

    private HealthCheckService CreateService(
        SportarrDbContext db,
        HttpMessageHandler? githubHandler = null,
        HttpMessageHandler? hubHandler = null,
        DatabaseHealthTracker? databaseHealth = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sportarr:DataPath"] = _tempDataPath })
            .Build();

        var configService = new ConfigService(
            configuration,
            Mock.Of<ILogger<ConfigService>>());

        var downloadClientService = new DownloadClientService(
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ILoggerFactory>(),
            Mock.Of<ILogger<DownloadClientService>>(),
            new MemoryCache(new MemoryCacheOptions()),
            configService,
            Mock.Of<Sportarr.Api.Services.Interfaces.IRemotePathMappingService>(), new DownloadOwnershipCoordinator());

        var sportarrApiClient = new SportarrApiClient(
            new HttpClient(hubHandler ?? new StubHandler(HttpStatusCode.OK, "{}")),
            Mock.Of<ILogger<SportarrApiClient>>(),
            new ConfigurationBuilder().Build(),
            configService,
            new MemoryCache(new MemoryCacheOptions()));

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient("TrashGuides"))
            .Returns(() => new HttpClient(githubHandler ?? new StubHandler(HttpStatusCode.ServiceUnavailable, "")));

        return new HealthCheckService(
            db,
            Mock.Of<ILogger<HealthCheckService>>(),
            downloadClientService,
            configService,
            new DiskSpaceService(Mock.Of<ILogger<DiskSpaceService>>()),
            sportarrApiClient,
            httpClientFactory.Object,
            new FileNamingService(Mock.Of<ILogger<FileNamingService>>()),
            databaseHealth ?? new DatabaseHealthTracker(),
            new BackupService(db, Mock.Of<ILogger<BackupService>>(), configuration, configService));
    }

    [Fact]
    public async Task PerformAllChecksAsync_EnabledNotificationWithFailedLastSend_SurfacesNotificationTestFailed()
    {
        using var db = CreateDb();
        db.Notifications.Add(new Notification
        {
            Name = "Broken Discord",
            Implementation = "Discord",
            Enabled = true,
            LastNotificationSucceeded = false,
            LastNotificationError = "401 Unauthorized"
        });
        await db.SaveChangesAsync();

        var results = await CreateService(db).PerformAllChecksAsync();

        results.Should().Contain(r => r.Type == HealthCheckType.NotificationTestFailed
            && r.Message.Contains("Broken Discord")
            && r.Details == "401 Unauthorized");
    }

    [Fact]
    public async Task PerformAllChecksAsync_DisabledNotificationWithFailedLastSend_NotSurfaced()
    {
        using var db = CreateDb();
        db.Notifications.Add(new Notification
        {
            Name = "Disabled Discord",
            Implementation = "Discord",
            Enabled = false,
            LastNotificationSucceeded = false
        });
        await db.SaveChangesAsync();

        var results = await CreateService(db).PerformAllChecksAsync();

        results.Should().NotContain(r => r.Type == HealthCheckType.NotificationTestFailed);
    }

    [Fact]
    public async Task PerformAllChecksAsync_NotificationNeverSent_NotSurfaced()
    {
        using var db = CreateDb();
        db.Notifications.Add(new Notification
        {
            Name = "Never Tested",
            Implementation = "Discord",
            Enabled = true,
            LastNotificationSucceeded = null
        });
        await db.SaveChangesAsync();

        var results = await CreateService(db).PerformAllChecksAsync();

        results.Should().NotContain(r => r.Type == HealthCheckType.NotificationTestFailed);
    }

    [Fact]
    public async Task PerformAllChecksAsync_NotificationLastSendSucceeded_NotSurfaced()
    {
        using var db = CreateDb();
        db.Notifications.Add(new Notification
        {
            Name = "Working Discord",
            Implementation = "Discord",
            Enabled = true,
            LastNotificationSucceeded = true
        });
        await db.SaveChangesAsync();

        var results = await CreateService(db).PerformAllChecksAsync();

        results.Should().NotContain(r => r.Type == HealthCheckType.NotificationTestFailed);
    }

    [Fact]
    public async Task PerformAllChecksAsync_GitHubReleaseIsNewer_SurfacesUpdateAvailable()
    {
        using var db = CreateDb();
        var githubJson = $$"""{"tag_name": "v99.0.0"}""";
        var service = CreateService(db, new StubHandler(HttpStatusCode.OK, githubJson));

        var results = await service.PerformAllChecksAsync();

        results.Should().Contain(r => r.Type == HealthCheckType.UpdateAvailable
            && r.Message.Contains("99.0.0"));
    }

    [Fact]
    public async Task PerformAllChecksAsync_GitHubReleaseIsSameOrOlder_NoUpdateAvailable()
    {
        using var db = CreateDb();
        var githubJson = $$"""{"tag_name": "v0.0.1"}""";
        var service = CreateService(db, new StubHandler(HttpStatusCode.OK, githubJson));

        var results = await service.PerformAllChecksAsync();

        results.Should().NotContain(r => r.Type == HealthCheckType.UpdateAvailable);
    }

    [Fact]
    public async Task PerformAllChecksAsync_GitHubUnreachable_DoesNotThrowOrSurfaceUpdateAvailable()
    {
        using var db = CreateDb();
        var service = CreateService(db, new StubHandler(HttpStatusCode.ServiceUnavailable, ""));

        var act = async () => await service.PerformAllChecksAsync();

        await act.Should().NotThrowAsync();
        var results = await service.PerformAllChecksAsync();
        results.Should().NotContain(r => r.Type == HealthCheckType.UpdateAvailable);
    }

    [Fact]
    public async Task PerformAllChecksAsync_MetadataApiConnectionFails_SurfacesMetadataApiUnavailable()
    {
        using var db = CreateDb();
        var service = CreateService(db, hubHandler: new ThrowingHandler());

        var results = await service.PerformAllChecksAsync();

        results.Should().Contain(r => r.Type == HealthCheckType.MetadataApiUnavailable);
    }

    [Fact]
    public async Task PerformAllChecksAsync_MetadataApiReturnsErrorStatus_StillCountsAsReachable()
    {
        // Any HTTP response (even a 5xx) proves connectivity - only a
        // connection-level exception should surface MetadataApiUnavailable.
        using var db = CreateDb();
        var service = CreateService(db, hubHandler: new StubHandler(HttpStatusCode.ServiceUnavailable, ""));

        var results = await service.PerformAllChecksAsync();

        results.Should().NotContain(r => r.Type == HealthCheckType.MetadataApiUnavailable);
    }

    // Issue #288. A damaged database had no health check of its own. The
    // CorruptedDatabase type existed but only the catch-all raised it, under
    // the message "Health check system error", and only if a check happened
    // to touch a damaged table. An instance ran 15 days without a word.

    [Fact]
    public async Task PerformAllChecksAsync_DamagedDatabase_SurfacesAnError()
    {
        using var db = CreateDb();
        var damaged = new DatabaseHealthTracker();
        damaged.RecordFailure(new Microsoft.Data.Sqlite.SqliteException("database disk image is malformed", 11));
        var service = CreateService(db, databaseHealth: damaged);

        var results = await service.PerformAllChecksAsync();

        var damage = results.Should().ContainSingle(r => r.Type == HealthCheckType.CorruptedDatabase).Subject;
        damage.Level.Should().Be(HealthCheckLevel.Error);
        damage.Message.Should().Contain("damaged");
        damage.Details.Should().Contain("Restoring a backup", "a restore from before the damage is the usual repair");
        damage.Details.Should().Contain("REINDEX", "index damage can sometimes be undone without a restore");
    }

    [Fact]
    public async Task PerformAllChecksAsync_HealthyDatabase_SaysNothingAboutDamage()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var results = await service.PerformAllChecksAsync();

        results.Should().NotContain(r => r.Type == HealthCheckType.CorruptedDatabase);
    }

    [Fact]
    public async Task PerformAllChecksAsync_OrdinaryQueryFailure_IsNotReportedAsDamage()
    {
        // A constraint violation must never take an instance unhealthy.
        using var db = CreateDb();
        var tracker = new DatabaseHealthTracker();
        tracker.RecordFailure(new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed", 19));
        var service = CreateService(db, databaseHealth: tracker);

        var results = await service.PerformAllChecksAsync();

        results.Should().NotContain(r => r.Type == HealthCheckType.CorruptedDatabase);
    }

    [Fact]
    public async Task PerformAllChecksAsync_NoBackupsYet_SaysNothing()
    {
        // A fresh install and an install whose backups broke look identical
        // from here, so silence until a first backup exists.
        using var db = CreateDb();
        var service = CreateService(db);

        var results = await service.PerformAllChecksAsync();

        results.Should().NotContain(r => r.Type == HealthCheckType.BackupsFailing);
    }

    [Fact]
    public async Task PerformAllChecksAsync_BackupsStoppedAfterWorking_SurfacesAWarning()
    {
        // The scheduled run logs its failure and moves on, so backups that
        // stop leave no trace a user would see.
        using var db = CreateDb();
        var backupFolder = Path.Combine(_tempDataPath, "Backups");
        Directory.CreateDirectory(backupFolder);
        var stale = Path.Combine(backupFolder, "sportarr_backup_20260101_000000.zip");
        await File.WriteAllTextAsync(stale, "not a real archive");
        File.SetCreationTimeUtc(stale, DateTime.UtcNow.AddDays(-60));

        var service = CreateService(db);

        var results = await service.PerformAllChecksAsync();

        var backups = results.Should().ContainSingle(r => r.Type == HealthCheckType.BackupsFailing).Subject;
        backups.Level.Should().Be(HealthCheckLevel.Warning);
        backups.Message.Should().Contain("stopped");
    }

    [Fact]
    public async Task PerformAllChecksAsync_DoesNotWriteToAnIdleRootFolder()
    {
        using var db = CreateDb();
        var root = Path.Combine(_tempDataPath, "library");
        Directory.CreateDirectory(root);
        db.RootFolders.Add(new RootFolder { Path = root });
        await db.SaveChangesAsync();
        var writes = new ConcurrentBag<string>();
        using var watcher = new FileSystemWatcher(root)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, args) => writes.Add(args.Name ?? string.Empty);
        watcher.Changed += (_, args) => writes.Add(args.Name ?? string.Empty);

        await CreateService(db).PerformAllChecksAsync();
        await Task.Delay(100);

        writes.Should().BeEmpty("recurring health checks must not wake an idle media disk");
    }
}
