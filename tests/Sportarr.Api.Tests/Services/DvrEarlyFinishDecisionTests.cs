using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class DvrEarlyFinishDecisionTests
{
    [Fact]
    public async Task OptionalCheckBudgetPreservesAlreadyDueStopsDuringOutage()
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        fixture.Db.DvrRecordings.Add(new DvrRecording
        {
            Id = 10, ChannelId = 1, Title = "Already due", Status = DvrRecordingStatus.Recording,
            Method = DvrRecordingMethod.Live, ScheduledStart = DateTime.UtcNow.AddHours(-1),
            ScheduledEnd = DateTime.UtcNow.AddMinutes(-1), PostPadding = 0
        });
        await fixture.Db.SaveChangesAsync();
        var candidates = await fixture.Service.GetRecordingsToStopAsync().WaitAsync(TimeSpan.FromSeconds(14));
        Assert.Contains(candidates, recording => recording.Id == 10);
        Assert.DoesNotContain(candidates, recording => recording.Id == fixture.Recording.Id);
    }

    [Fact]
    public async Task DisablingGuardDuringSourceRequestPreventsEarlyStop()
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        var decision = fixture.Service.IsReadyToStopAsync(fixture.Recording);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var config = await fixture.Config.GetConfigAsync();
        config.DvrEarlyFinishGuardEnabled = false;
        await fixture.Config.SaveConfigAsync(config);
        fixture.Handler.ReturnFinal();

        Assert.False(await decision);
    }

    [Fact]
    public async Task IncreasingBufferDuringSourceRequestPreventsEarlyStop()
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        var decision = fixture.Service.IsReadyToStopAsync(fixture.Recording);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var config = await fixture.Config.GetConfigAsync();
        config.DvrEarlyFinishBufferMinutes = 5;
        await fixture.Config.SaveConfigAsync(config);
        fixture.Handler.ReturnFinal();

        Assert.False(await decision);
    }

    [Fact]
    public async Task ChangingMetadataSourceDuringRequestDiscardsOldSourceEvidence()
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        var decision = fixture.Service.IsReadyToStopAsync(fixture.Recording);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var config = await fixture.Config.GetConfigAsync();
        config.CustomMetadataApiUrl = "https://different-metadata.invalid/api/v2/json";
        await fixture.Config.SaveConfigAsync(config);
        fixture.Handler.ReturnFinal();

        Assert.False(await decision);
    }

    [Fact]
    public async Task FinalizingRecordingIsNotCheckedForAnotherStop()
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        using var finalizing = fixture.Recorder.BeginFinalizing(fixture.Recording.Id);

        Assert.False(await fixture.Service.IsReadyToStopAsync(fixture.Recording));
        Assert.False(fixture.Handler.Entered.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizingStartedDuringSourceRequestPreventsAnotherStop(bool overdue)
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        if (overdue)
        {
            fixture.Recording.ScheduledEnd = DateTime.UtcNow.AddMinutes(-5);
            await fixture.Db.SaveChangesAsync();
        }
        var decision = fixture.Service.IsReadyToStopAsync(fixture.Recording);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var finalizing = fixture.Recorder.BeginFinalizing(fixture.Recording.Id);
        fixture.Handler.ReturnFinal();

        Assert.False(await decision);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("catchup")]
    [InlineData("event_link")]
    [InlineData("event_external_id")]
    [InlineData("league_external_id")]
    [InlineData("restarted")]
    public async Task ChangedRecordingOrIdentityDuringSourceRequestPreventsEarlyStop(string change)
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        var decision = fixture.Service.IsReadyToStopAsync(fixture.Recording);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var writer = fixture.CreateDb())
        {
            var recording = await writer.DvrRecordings.SingleAsync();
            if (change == "completed") recording.Status = DvrRecordingStatus.Completed;
            if (change == "catchup") recording.Method = DvrRecordingMethod.Catchup;
            if (change == "event_link") recording.EventId = 3;
            if (change == "restarted") recording.ActualStart = DateTime.UtcNow;
            if (change == "event_external_id")
                (await writer.Events.SingleAsync(e => e.Id == 2)).ExternalId = "ev-000099";
            if (change == "league_external_id")
                (await writer.Leagues.SingleAsync()).ExternalId = "lg-000099";
            await writer.SaveChangesAsync();
        }
        fixture.Handler.ReturnFinal();

        Assert.False(await decision);
    }

    [Fact]
    public async Task UnchangedRecordingWithSecondFreshFinalIsReady()
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        var decision = fixture.Service.IsReadyToStopAsync(fixture.Recording);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Handler.ReturnFinal();

        Assert.True(await decision);
    }

    [Fact]
    public async Task ReloadingExtendedSchedulePreventsStopFromStaleTrackedRow()
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        var config = await fixture.Config.GetConfigAsync();
        config.DvrEarlyFinishGuardEnabled = false;
        await fixture.Config.SaveConfigAsync(config);
        fixture.Recording.ScheduledEnd = DateTime.UtcNow.AddMinutes(-5);
        await fixture.Db.SaveChangesAsync();
        await using (var writer = fixture.CreateDb())
        {
            (await writer.DvrRecordings.SingleAsync()).ScheduledEnd = DateTime.UtcNow.AddHours(1);
            await writer.SaveChangesAsync();
        }

        Assert.False(await fixture.Service.IsReadyToStopAsync(fixture.Recording));
        Assert.False(fixture.Handler.Entered.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringSourceRequestDoesNotProduceStopDecision(bool overdue)
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        if (overdue)
        {
            fixture.Recording.ScheduledEnd = DateTime.UtcNow.AddMinutes(-5);
            await fixture.Db.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var decision = fixture.Service.IsReadyToStopAsync(fixture.Recording, cancellation.Token);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        fixture.Handler.ReturnFinal();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decision);
    }

    [Fact]
    public async Task ExtendingScheduleDuringOvertimeRequestPreventsScheduledStop()
    {
        await using var fixture = await DecisionFixture.CreateAsync();
        var config = await fixture.Config.GetConfigAsync();
        config.DvrEarlyFinishGuardEnabled = false;
        await fixture.Config.SaveConfigAsync(config);
        fixture.Recording.ScheduledEnd = DateTime.UtcNow.AddMinutes(-5);
        await fixture.Db.SaveChangesAsync();
        var decision = fixture.Service.IsReadyToStopAsync(fixture.Recording);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var writer = fixture.CreateDb())
        {
            (await writer.DvrRecordings.SingleAsync()).ScheduledEnd = DateTime.UtcNow.AddHours(1);
            await writer.SaveChangesAsync();
        }
        fixture.Handler.ReturnFinal();

        Assert.False(await decision);
    }

    private sealed class HeldResponseHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v2/json/livescore/league/lg-000001", request.RequestUri!.AbsolutePath);
            Entered.TrySetResult();
            return await _response.Task.WaitAsync(cancellationToken);
        }

        public void ReturnFinal()
        {
            var json = JsonSerializer.Serialize(new { data = new[] { FinalScore(DateTimeOffset.UtcNow) } });
            _response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static DvrLiveScore FinalScore(DateTimeOffset fetched) => new()
    {
        EventId = "ev-000002", LeagueId = "lg-000001", Status = "FT", Progress = "Final",
        Source = "thesportsdb:livescore", SourceFetchedAt = fetched,
        RequestOrigin = "https://metadata.invalid/api/v2/json"
    };

    private sealed class DecisionFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly MemoryCache _cache;
        private readonly HttpClient _http;
        private readonly string _directory;
        public SportarrDbContext Db { get; }
        public ConfigService Config { get; }
        public DvrRecording Recording { get; }
        public DvrRecordingService Service { get; }
        public HeldResponseHandler Handler { get; }
        public FFmpegRecorderService Recorder { get; }

        private DecisionFixture(SqliteConnection connection, SportarrDbContext db, ConfigService config,
            DvrRecording recording, MemoryCache cache, HttpClient http, HeldResponseHandler handler,
            DvrRecordingService service, FFmpegRecorderService recorder, string directory)
        {
            _connection = connection;
            Db = db;
            Config = config;
            Recording = recording;
            _cache = cache;
            _http = http;
            Handler = handler;
            Service = service;
            Recorder = recorder;
            _directory = directory;
        }

        public SportarrDbContext CreateDb() => new(new DbContextOptionsBuilder<SportarrDbContext>().UseSqlite(_connection).Options);

        public static async Task<DecisionFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"dvr-decision-{Guid.NewGuid():N}");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sportarr:DataPath"] = directory,
                ["SportarrApi:BaseUrl"] = "https://metadata.invalid/api/v2/json"
            }).Build();
            var config = new ConfigService(configuration, NullLogger<ConfigService>.Instance);
            var settings = await config.GetConfigAsync();
            settings.DvrEarlyFinishGuardEnabled = true;
            settings.DvrEarlyFinishBufferMinutes = 0;
            settings.DvrOvertimeGuardEnabled = true;
            await config.SaveConfigAsync(settings);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.Leagues.Add(new League { Id = 1, Name = "Fixture", Sport = "Soccer", ExternalId = "lg-000001" });
            db.Events.AddRange(
                new Event { Id = 2, LeagueId = 1, Title = "Fixture", Sport = "Soccer", ExternalId = "ev-000002", EventDate = DateTime.UtcNow },
                new Event { Id = 3, LeagueId = 1, Title = "Other fixture", Sport = "Soccer", ExternalId = "ev-000003", EventDate = DateTime.UtcNow });
            db.IptvSources.Add(new IptvSource { Id = 1, Name = "Fixture", Url = "https://iptv.invalid/list" });
            db.IptvChannels.Add(new IptvChannel { Id = 1, SourceId = 1, Name = "Fixture", StreamUrl = "https://iptv.invalid/stream" });
            var now = DateTimeOffset.UtcNow;
            var recording = new DvrRecording
            {
                Id = 1, EventId = 2, ChannelId = 1, Title = "Fixture", Method = DvrRecordingMethod.Live,
                Status = DvrRecordingStatus.Recording, ActualStart = now.AddHours(-1).UtcDateTime,
                ScheduledStart = now.AddHours(-1).UtcDateTime, ScheduledEnd = now.AddHours(2).UtcDateTime,
                PostPadding = 0
            };
            db.DvrRecordings.Add(recording);
            await db.SaveChangesAsync();
            var guard = new DvrEarlyFinishGuard();
            var previous = now.AddSeconds(-65);
            Assert.False(guard.Observe(recording, FinalScore(previous), "ev-000002", "lg-000001", previous, 0));
            var handler = new HeldResponseHandler();
            var http = new HttpClient(handler);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var api = new SportarrApiClient(http, NullLogger<SportarrApiClient>.Instance, configuration, config, cache);
            var recorder = new FFmpegRecorderService(NullLogger<FFmpegRecorderService>.Instance, config, null!);
            var service = new DvrRecordingService(NullLogger<DvrRecordingService>.Instance, db,
                recorder, null!, config, null!, null!, null!, api, null!, guard);
            return new DecisionFixture(connection, db, config, recording, cache, http, handler, service, recorder, directory);
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            _cache.Dispose();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
