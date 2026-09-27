using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class EpisodeNumberResolverTests
{
    [Fact]
    public async Task ExactHubIdentityReplacesAStaleStoredNumber()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.OK,
            """{"id":"ev-1969707","episode_number":55}""");
        var evt = await fixture.AddEventAsync(1, "ev-1969707", 88, new DateTime(2026, 9, 13, 17, 0, 0));

        var result = await fixture.Resolver.ResolveAsync(evt);

        Assert.Equal(55, result);
        Assert.Equal(55, evt.EpisodeNumber);
        Assert.Equal("/api/metadata/agents/episode/ev-1969707", fixture.RequestPaths.Single());
    }

    [Fact]
    public async Task BulkSnapshotUsesExactIdentityWithoutPerEventRequests()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.ServiceUnavailable, "{}");
        var evt = await fixture.AddEventAsync(1, "ev-1969705", 86, new DateTime(2026, 9, 13, 20, 25, 0));
        var snapshot = new Dictionary<string, int> { ["ev-1969705"] = 58 };

        var result = await fixture.Resolver.ResolveAsync(evt, snapshot);

        Assert.Equal(58, result);
        Assert.Equal(58, evt.EpisodeNumber);
        Assert.Empty(fixture.RequestPaths);
    }

    [Fact]
    public async Task RepeatedResolutionReusesTheExactIdentityRequestWithinTheScope()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.OK,
            """{"id":"ev-1969707","episode_number":55}""");
        var evt = await fixture.AddEventAsync(1, "ev-1969707", 88, new DateTime(2026, 9, 13, 17, 0, 0));

        Assert.Equal(55, await fixture.Resolver.ResolveAsync(evt));
        Assert.Equal(55, await fixture.Resolver.ResolveAsync(evt));

        Assert.Single(fixture.RequestPaths);
    }

    [Fact]
    public async Task FailedBatchSnapshotDoesNotFanOutIntoExactRequests()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.ServiceUnavailable, "{}");
        var first = await fixture.AddEventAsync(1, "ev-1969707", 88, new DateTime(2026, 9, 13, 17, 0, 0));
        var second = await fixture.AddEventAsync(2, "ev-1969705", 86, new DateTime(2026, 9, 13, 20, 25, 0));

        Assert.Equal(88, await fixture.Resolver.ResolveBatchAsync(first));
        Assert.Equal(86, await fixture.Resolver.ResolveBatchAsync(second));

        Assert.Single(fixture.RequestPaths);
        Assert.Contains("/api/metadata/plex/series/lg-000032/season/2026/episodes", fixture.RequestPaths);
    }

    [Fact]
    public async Task HubFailurePreservesAStoredPositiveNumber()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.ServiceUnavailable, "{}");
        var evt = await fixture.AddEventAsync(1, "ev-1969707", 88, new DateTime(2026, 9, 13, 17, 0, 0));

        var result = await fixture.Resolver.ResolveAsync(evt);

        Assert.Equal(88, result);
        Assert.Equal(88, evt.EpisodeNumber);
    }

    [Fact]
    public async Task UnconfirmedEpisodeOneDoesNotReplaceAStoredPositiveNumber()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.OK,
            """{"id":"ev-1969707","episode_number":1}""");
        var evt = await fixture.AddEventAsync(1, "ev-1969707", 88, new DateTime(2026, 9, 13, 17, 0, 0));

        var result = await fixture.Resolver.ResolveAsync(evt);

        Assert.Equal(88, result);
        Assert.Equal(88, evt.EpisodeNumber);
    }

    [Fact]
    public async Task ConfirmedEpisodeOneCanCorrectAStoredPositiveNumber()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.OK,
            """{"id":"ev-1969707","episode_number":1,"episode_number_authoritative":true}""");
        var evt = await fixture.AddEventAsync(1, "ev-1969707", 88, new DateTime(2026, 9, 13, 17, 0, 0));

        var result = await fixture.Resolver.ResolveAsync(evt);

        Assert.Equal(1, result);
        Assert.Equal(1, evt.EpisodeNumber);
    }

    [Fact]
    public async Task MissingHubAndStoredNumbersUseDeterministicSeasonOrder()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.ServiceUnavailable, "{}");
        await fixture.AddEventAsync(1, "ev-1969704", 50, new DateTime(2026, 9, 10, 0, 0, 0));
        await fixture.AddEventAsync(2, "ev-1969727", 51, new DateTime(2026, 9, 11, 0, 35, 0));
        var evt = await fixture.AddEventAsync(3, "ev-1969707", null, new DateTime(2026, 9, 13, 17, 0, 0));

        var result = await fixture.Resolver.ResolveAsync(evt);

        Assert.Equal(3, result);
        Assert.Equal(3, evt.EpisodeNumber);
    }

    [Fact]
    public async Task LocalFallbackExcludesEventsFromAnotherExplicitSeason()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.ServiceUnavailable, "{}");
        await fixture.AddEventAsync(1, "ev-previous", 50, new DateTime(2026, 1, 4, 18, 0, 0), "2025", 2025);
        await fixture.AddEventAsync(2, "ev-current-first", 51, new DateTime(2026, 9, 10, 0, 0, 0));
        var evt = await fixture.AddEventAsync(3, "ev-current-second", null, new DateTime(2026, 9, 13, 17, 0, 0));

        var result = await fixture.Resolver.ResolveAsync(evt);

        Assert.Equal(2, result);
        Assert.Equal(2, evt.EpisodeNumber);
    }

    [Fact]
    public async Task ManualPreviewUsesTheAuthoritativeNumber()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.OK,
            """{"id":"ev-1969707","episode_number":55}""");
        var evt = await fixture.AddEventAsync(1, "ev-1969707", 88, new DateTime(2026, 9, 13, 17, 0, 0));

        var preview = await fixture.CreateLibraryImportService()
            .BuildDestinationPreviewForEventAsync(evt.Id, "NFL.2026.HOU.vs.BUF.1080p.WEB-DL.mkv");

        Assert.Contains("S2026E55", preview);
        Assert.DoesNotContain("S2026E88", preview);
    }

    [Fact]
    public async Task InPlaceManualReplacementStillCorrectsAStaleNumber()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.OK,
            """{"id":"ev-1969707","episode_number":55}""");
        var evt = await fixture.AddEventAsync(1, "ev-1969707", 88, new DateTime(2026, 9, 13, 17, 0, 0));
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Directory, "beside"));
        var heldPath = Path.Combine(directory.FullName, "held.mkv");
        var replacementPath = Path.Combine(
            directory.FullName,
            "NFL.2026.HOU.vs.BUF.1080p.WEB-DL.{sportarr-ev-1969707}.mkv");
        await File.WriteAllBytesAsync(heldPath, new byte[1024]);
        await File.WriteAllBytesAsync(replacementPath, new byte[1024]);
        fixture.Db.EventFiles.Add(new EventFile
        {
            EventId = evt.Id,
            FilePath = heldPath,
            Size = 1024,
            Quality = "WEBDL-1080p",
            Added = DateTime.UtcNow,
            Exists = true
        });
        evt.HasFile = true;
        evt.FilePath = heldPath;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.CreateLibraryImportService().ImportFilesAsync(new List<FileImportRequest>
        {
            new()
            {
                FilePath = replacementPath,
                EventId = evt.Id,
                CreateNew = false,
                ImportMode = "move"
            }
        });

        Assert.Empty(result.Failed);
        Assert.Contains(replacementPath, result.Imported);
        Assert.Equal(55, evt.EpisodeNumber);
        Assert.Single(fixture.RequestPaths);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly SqliteConnection _connection;
        private readonly HttpClient _http;
        private readonly MemoryCache _cache;
        private readonly RecordingHandler _handler;
        private readonly ConfigService _config;

        private Fixture(string directory, SqliteConnection connection, HttpClient http,
            MemoryCache cache, RecordingHandler handler, ConfigService config, SportarrDbContext db,
            EpisodeNumberResolver resolver)
        {
            _directory = directory;
            _connection = connection;
            _http = http;
            _cache = cache;
            _handler = handler;
            _config = config;
            Db = db;
            Resolver = resolver;
        }

        public SportarrDbContext Db { get; }
        public EpisodeNumberResolver Resolver { get; }
        public IReadOnlyList<string> RequestPaths => _handler.RequestPaths;
        public string Directory => _directory;

        public static async Task<Fixture> CreateAsync(HttpStatusCode statusCode, string responseBody)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"episode-resolver-{Guid.NewGuid():N}");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sportarr:DataPath"] = directory,
                ["SportarrApi:BaseUrl"] = "https://metadata.invalid/api/v2/json"
            }).Build();
            var config = new ConfigService(configuration, NullLogger<ConfigService>.Instance);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.Leagues.Add(new League
            {
                Id = 1,
                Name = "NFL",
                Sport = "American Football",
                ExternalId = "lg-000032"
            });
            db.MediaManagementSettings.Add(new MediaManagementSettings
            {
                RenameEvents = true
            });
            await db.SaveChangesAsync();
            var handler = new RecordingHandler(statusCode, responseBody);
            var http = new HttpClient(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var api = new SportarrApiClient(http, NullLogger<SportarrApiClient>.Instance, configuration, config, cache);
            var resolver = new EpisodeNumberResolver(db, api, NullLogger<EpisodeNumberResolver>.Instance);
            return new Fixture(directory, connection, http, cache, handler, config, db, resolver);
        }

        public LibraryImportService CreateLibraryImportService()
        {
            var parser = new MediaFileParser(NullLogger<MediaFileParser>.Instance);
            return new LibraryImportService(
                Db,
                NullLogger<LibraryImportService>.Instance,
                parser,
                new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
                new FileNamingService(NullLogger<FileNamingService>.Instance),
                new EventPartDetector(NullLogger<EventPartDetector>.Instance),
                _config,
                Resolver,
                new DiskSpaceService(NullLogger<DiskSpaceService>.Instance),
                new CustomFormatService(parser),
                null!,
                null!);
        }

        public async Task<Event> AddEventAsync(
            int id,
            string externalId,
            int? episodeNumber,
            DateTime eventDate,
            string season = "2026",
            int seasonNumber = 2026)
        {
            var evt = new Event
            {
                Id = id,
                LeagueId = 1,
                Title = externalId,
                Sport = "American Football",
                ExternalId = externalId,
                EventDate = eventDate,
                BroadcastDate = eventDate.Date,
                Season = season,
                SeasonNumber = seasonNumber,
                EpisodeNumber = episodeNumber
            };
            Db.Events.Add(evt);
            await Db.SaveChangesAsync();
            return evt;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            _http.Dispose();
            _cache.Dispose();
            if (System.IO.Directory.Exists(_directory))
                System.IO.Directory.Delete(_directory, true);
        }
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
