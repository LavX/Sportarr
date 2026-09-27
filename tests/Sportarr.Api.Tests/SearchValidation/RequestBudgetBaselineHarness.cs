using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Startup;
using Xunit.Abstractions;

namespace Sportarr.Api.Tests.SearchValidation;

internal sealed class RequestBudgetBaselineHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _directory;
    private readonly ITestOutputHelper _output;
    private readonly List<object> _results = new();
    private readonly List<Task> _operations = new();
    public BudgetSource Source { get; }
    public RequestBudgetAdmissionWitness Admission => _services.GetRequiredService<RequestBudgetAdmissionWitness>();
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private RequestBudgetBaselineHarness(ServiceProvider services, BudgetSource source, string directory, ITestOutputHelper output)
    {
        _services = services;
        Source = source;
        _directory = directory;
        _output = output;
    }

    public static async Task<RequestBudgetBaselineHarness> CreateAsync(ITestOutputHelper output, QueryReservationSaveBarrier? reservationBarrier = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sportarr-request-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        BudgetSource? source = null;
        ServiceProvider? provider = null;
        try
        {
            source = await BudgetSource.StartAsync();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.ClearProviders());
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Sportarr:DataPath"] = directory }).Build());
            services.AddDbContextFactory<SportarrDbContext>(options =>
            {
                options.UseSqlite("Data Source=" + Path.Combine(directory, "fixture.db") + ";Pooling=False;Default Timeout=5");
                if (reservationBarrier != null) options.AddInterceptors(reservationBarrier);
            });
            services.AddSingleton<ConfigService>();
            services.AddSingleton<RateLimitService>();
            services.AddSingleton<RequestBudgetAdmissionWitness>();
            services.AddSingleton<IRateLimitService>(provider => provider.GetRequiredService<RequestBudgetAdmissionWitness>());
            services.AddTransient<RateLimitHandler>();
            services.AddSingleton<CustomFormatMatchCache>();
            services.AddSingleton<EventPartDetector>();
            services.AddSingleton<ReleaseEvaluator>();
            services.AddSingleton<QualityDetectionService>();
            services.AddSingleton<IndexerStatusService>();
            services.AddScoped<ReleaseProfileService>();
            services.AddScoped<IndexerSearchService>();
            services.AddSportarrHttpClients();
            var sourceUri = new Uri(source.Url);
            services.AddHttpClient("IndexerClient")
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    ConnectCallback = async (context, cancellationToken) =>
                    {
                        if (context.DnsEndPoint.Host != "127.0.0.1" || context.DnsEndPoint.Port != sourceUri.Port)
                            throw new InvalidOperationException("The request left the isolated source boundary.");
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }
                })
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(20));
            provider = services.BuildServiceProvider(validateScopes: true);
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.DownloadClients.AddRange(
                new DownloadClient { Name = "Fixture torrent", Type = DownloadClientType.TorrentBlackhole, Host = "localhost", Enabled = true },
                new DownloadClient { Name = "Fixture usenet", Type = DownloadClientType.UsenetBlackhole, Host = "localhost", Enabled = true });
            await db.SaveChangesAsync();
            var configuration = provider.GetRequiredService<ConfigService>();
            var config = await configuration.GetConfigAsync();
            config.IndexerRetention = 0;
            config.IndexerHttpTimeoutSeconds = 20;
            await configuration.SaveConfigAsync(config);
            return new RequestBudgetBaselineHarness(provider, source, directory, output);
        }
        catch
        {
            if (provider != null) await provider.DisposeAsync();
            if (source != null) await source.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    public Indexer Row(string name, IndexerType type = IndexerType.Torznab) => new()
    {
        Name = name, Type = type, Url = Source.Url, ApiKey = BudgetSource.KeyA, ApiPath = "/api",
        Categories = new List<string> { "5060" }, Enabled = true, EnableRss = true,
        EnableAutomaticSearch = true, EnableInteractiveSearch = true, MinimumSeeders = 0,
        RequestDelayMs = 20, MultiLanguages = new List<string> { "English" }
    };

    public async Task SaveRowsAsync(params Indexer[] rows)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();
        db.Indexers.AddRange(rows);
        await db.SaveChangesAsync();
        foreach (var row in rows)
            db.IndexerStatuses.Add(new IndexerStatus { IndexerId = row.Id, HourResetTime = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
    }

    public Task<List<ReleaseSearchResult>> SearchOneAsync(Indexer row, string query = "budget-fixture", int maximum = 5, string? eventId = null)
        => TrackAsync(service => service.SearchIndexerAsync(row, query, maximum, eventId));

    public Task<List<ReleaseSearchResult>> SearchAllAsync(bool interactiveSearch = true)
        => TrackAsync(service => service.SearchAllIndexersAsync("budget-fixture", 5,
            enableMultiPartEpisodes: false, interactiveSearch: interactiveSearch));

    public Task<List<ReleaseSearchResult>> RssAsync()
        => TrackAsync(service => service.FetchAllRssFeedsAsync(5));

    private Task<List<ReleaseSearchResult>> TrackAsync(Func<IndexerSearchService, Task<List<ReleaseSearchResult>>> action)
    {
        var operation = ExecuteAsync(action);
        _operations.Add(operation);
        return operation;
    }

    private async Task<List<ReleaseSearchResult>> ExecuteAsync(Func<IndexerSearchService, Task<List<ReleaseSearchResult>>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        var rows = await action(scope.ServiceProvider.GetRequiredService<IndexerSearchService>());
        lock (_results)
            _results.Add(rows.Select(row => new { row.Guid, row.IndexerId, row.Indexer, row.Language, row.MultiLanguageNames }).ToArray());
        return rows;
    }

    public async Task<int> QueryCountAsync(int rowId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SportarrDbContext>().IndexerStatuses
            .Where(row => row.IndexerId == rowId).Select(row => row.QueriesThisHour).SingleAsync();
    }

    public async Task SeedQuotaWindowAsync(int rowId, int queries, int grabs, DateTime resetAt)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();
        var status = await db.IndexerStatuses.SingleAsync(row => row.IndexerId == rowId);
        status.QueriesThisHour = queries;
        status.GrabsThisHour = grabs;
        status.HourResetTime = resetAt;
        await db.SaveChangesAsync();
        _output.WriteLine(JsonSerializer.Serialize(new { FixtureSeed = true, rowId, queries, grabs, resetAt }));
    }

    public async Task<QuotaState> ReadQuotaStateAsync(int rowId)
    {
        await using var scope = _services.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<SportarrDbContext>().IndexerStatuses
            .AsNoTracking().SingleAsync(row => row.IndexerId == rowId);
        var state = new QuotaState(status.QueriesThisHour, status.GrabsThisHour, status.HourResetTime,
            status.QueryFailures, status.ConnectionErrors, status.RateLimitedUntil);
        _output.WriteLine(JsonSerializer.Serialize(new { ObservedQuota = true, rowId, state }));
        return state;
    }

    public sealed record QuotaState(int Queries, int Grabs, DateTime? ResetAt, int QueryFailures,
        int ConnectionErrors, DateTime? RateLimitedUntil);

    public async Task<QueryHealth> ReadQueryHealthAsync(int rowId)
    {
        await using var scope = _services.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<SportarrDbContext>().IndexerStatuses
            .AsNoTracking().SingleAsync(row => row.IndexerId == rowId);
        var health = new QueryHealth(status.LastSuccess, status.LastQueryFailure,
            status.QueryDisabledUntil, status.LastConnectionError);
        _output.WriteLine(JsonSerializer.Serialize(new { ObservedQueryHealth = true, rowId, health }));
        return health;
    }

    public sealed record QueryHealth(DateTime? LastSuccess, DateTime? LastQueryFailure,
        DateTime? QueryDisabledUntil, DateTime? LastConnectionError);

    public HttpClient CompositionClient() => _services.GetRequiredService<IHttpClientFactory>().CreateClient("IndexerClient");

    public Task<SearchOperationOutcome> CompositionSearchAsync(int maximum = 5, string? eventId = null)
    {
        var operation = RunCompositionAsync(maximum, eventId);
        _operations.Add(operation);
        return operation;
    }

    private async Task<SearchOperationOutcome> RunCompositionAsync(int maximum, string? eventId)
    {
        await using var scope = _services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IndexerSearchService>()
            .SearchAllIndexersDetailedAsync("budget-fixture", maximum, enableMultiPartEpisodes: false, sportarrId: eventId);
        lock (_results)
            _results.Add(outcome.Releases.Select(row => new { row.Guid, row.IndexerId, row.Indexer, row.Protocol }).ToArray());
        _output.WriteLine(JsonSerializer.Serialize(new { CompositionOutcome = outcome }));
        return outcome;
    }

    public async Task<int[]> SavedRowIdsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SportarrDbContext>().Indexers
            .OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Source.ReleaseFirst();
        try
        {
            try { await Task.WhenAll(_operations).WaitAsync(Deadline); }
            catch (Exception exception) { _output.WriteLine("Operation completion: " + exception.GetType().Name); }
            await using var scope = _services.CreateAsyncScope();
            var status = await scope.ServiceProvider.GetRequiredService<SportarrDbContext>().IndexerStatuses
                .OrderBy(row => row.IndexerId).Select(row => new { row.IndexerId, row.QueriesThisHour, row.RateLimitedUntil }).ToListAsync();
            _output.WriteLine(JsonSerializer.Serialize(new { Source.TotalArrivals, Attempts = Source.Attempts, Admission = Admission.Observations, Results = _results, Status = status, Violations = Source.Violations }));
        }
        finally
        {
            await _services.DisposeAsync();
            await Source.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        Assert.Empty(Source.Violations);
    }

    internal sealed class BudgetSource : IAsyncDisposable
    {
        public const string KeyA = "fixture-account-a";
        public const string KeyB = "fixture-account-b";
        private readonly WebApplication _app;
        private readonly string _prefix;
        private readonly ConcurrentQueue<Attempt> _attempts = new();
        private readonly ConcurrentQueue<string> _violations = new();
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _attemptCount;
        private int _dataCount;
        private int _capsCount;
        private int _holdingFirst;
        public string Url { get; private set; } = "";
        public bool Paged { get; set; }
        public enum PaginationShape { Default, FullPagesWithoutTotal, ShortPageWithLargerTotal, RepeatedRawPage }
        public PaginationShape PageShape { get; set; }
        public bool HoldFirst { get; set; }
        public int FailuresBeforeSuccess { get; set; }
        public int? ForcedStatus { get; set; }
        public string? RetryAfter { get; set; }
        public int CapsFailuresBeforeSuccess { get; set; }
        public Attempt[] Attempts => _attempts.OrderBy(attempt => attempt.Sequence).ToArray();
        public string[] Violations => _violations.ToArray();
        public int TotalArrivals => Volatile.Read(ref _attemptCount);
        public int MaxArrivals { get; set; } = 12;
        public Task FirstArrival => _first.Task;
        public bool FirstResponseIsHeld => Volatile.Read(ref _holdingFirst) == 1;
        public Task SecondArrival => _second.Task;

        private BudgetSource(WebApplication app, string prefix) { _app = app; _prefix = prefix; }

        public static async Task<BudgetSource> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var source = new BudgetSource(app, "/budget-" + Guid.NewGuid().ToString("N"));
            app.Run(source.HandleAsync);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await app.StartAsync(timeout.Token);
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                source.Url = addresses.Addresses.Single() + source._prefix;
                return source;
            }
            catch { await app.DisposeAsync(); throw; }
        }

        public void ReleaseFirst() => _release.TrySetResult();

        private async Task HandleAsync(HttpContext context)
        {
            var sequence = Interlocked.Increment(ref _attemptCount);
            var path = context.Request.Path.Value;
            var query = context.Request.Query;
            var mode = query["t"].ToString();
            var account = query["apikey"].ToString() switch { KeyA => "account-a", KeyB => "account-b", _ => "invalid" };
            var source = path == _prefix + "/api" ? "source-a" : path == _prefix + "/alternate" ? "source-b" : "invalid";
            var edition = query["edition"].ToString() switch { "" => "default", "alternate" => "alternate", _ => "invalid" };
            var text = query["q"].ToString();
            var allowedKeys = new[] { "t", "apikey", "q", "limit", "offset", "cat", "extended", "sportarrid", "edition" };
            if (sequence > MaxArrivals || context.Request.Method != "GET" || source == "invalid" || account == "invalid" || edition == "invalid"
                || (mode != "caps" && mode != "search") || (text != "" && text != "budget-fixture" && text != "budget-competitor")
                || query.Keys.Any(key => !allowedKeys.Contains(key)))
            {
                _violations.Enqueue(sequence > MaxArrivals ? "attempt-ceiling" : "unexpected-request-shape");
                context.Response.StatusCode = 400;
                return;
            }
            var offset = int.TryParse(query["offset"], out var parsedOffset) ? parsedOffset : 0;
            var limit = int.TryParse(query["limit"], out var parsedLimit) ? parsedLimit : 0;
            var ordinal = mode == "search" ? Interlocked.Increment(ref _dataCount) : 0;
            var capsOrdinal = mode == "caps" ? Interlocked.Increment(ref _capsCount) : 0;
            var status = (ordinal > 0 && ordinal <= FailuresBeforeSuccess)
                || (capsOrdinal > 0 && capsOrdinal <= CapsFailuresBeforeSuccess) ? 503 : 200;
            if (ForcedStatus.HasValue) status = ForcedStatus.Value;
            var guids = mode == "caps" || status != 200 ? Array.Empty<string>() : Paged
                ? PageGuids(offset, limit)
                : new[] { "offer-" + source + "-" + account + "-" + edition };
            var xml = status != 200 ? "unavailable" : mode == "caps" ? Capabilities() : Feed(guids, Paged && PageShape == PaginationShape.RepeatedRawPage ? 0 : offset);
            var bytes = Encoding.UTF8.GetBytes(xml);
            var attempt = new Attempt(sequence, mode == "search" && text == "" ? "rss" : mode, source, account, edition,
                context.Request.Headers["X-Indexer-Id"].ToString(), text, offset, limit,
                query["sportarrid"].ToString(), status, _clock.ElapsedMilliseconds, guids,
                Convert.ToHexString(SHA256.HashData(bytes))) { ResponseBody = xml };
            _attempts.Enqueue(attempt);
            if (ordinal == 1)
            {
                if (HoldFirst) Volatile.Write(ref _holdingFirst, 1);
                _first.TrySetResult();
            }
            if (ordinal == 2) _second.TrySetResult();
            try
            {
                if (HoldFirst && ordinal == 1)
                    await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), context.RequestAborted);
                context.Response.StatusCode = status;
                if (RetryAfter != null) context.Response.Headers["Retry-After"] = RetryAfter;
                context.Response.ContentType = "application/xml";
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                attempt.ResponseSent = true;
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                _violations.Enqueue("source-response-deadline");
                context.Abort();
            }
            finally
            {
                if (ordinal == 1) Volatile.Write(ref _holdingFirst, 0);
            }
        }

        private string[] PageGuids(int requestedOffset, int requestedLimit)
        {
            var catalogueSize = PageShape == PaginationShape.FullPagesWithoutTotal ? 4 : 5;
            var offset = PageShape == PaginationShape.RepeatedRawPage ? 0 : requestedOffset;
            var pageSize = PageShape == PaginationShape.ShortPageWithLargerTotal && offset == 0 ? 1 : 2;
            return Enumerable.Range(1, catalogueSize).Skip(offset)
                .Take(Math.Min(pageSize, Math.Max(0, requestedLimit)))
                .Select(number => "offer-" + number).ToArray();
        }

        private static string Capabilities() => new XElement("caps",
            new XElement("limits", new XAttribute("max", 2), new XAttribute("default", 2)),
            new XElement("searching", new XElement("search", new XAttribute("available", "yes"),
                new XAttribute("supportedParams", "q,sportarrid")))).ToString();

        private string Feed(string[] guids, int offset)
        {
            XNamespace ns = "http://www.newznab.com/DTD/2010/feeds/attributes/";
            XNamespace torrent = "http://torznab.com/schemas/2015/feed";
            var response = new XElement(ns + "response", new XAttribute("offset", offset));
            if (!Paged || PageShape != PaginationShape.FullPagesWithoutTotal)
                response.Add(new XAttribute("total", Paged ? 5 : guids.Length));
            var channel = new XElement("channel", response);
            foreach (var guid in guids)
            {
                var title = guid == "offer-5" ? "Spain.vs.Belgium.2026.09.01.MULTI.1080p.WEB-DL.H264-GROUP"
                    : "Fixture.Other.2026.09.01.MULTI.1080p.WEB-DL.H264-GROUP";
                channel.Add(new XElement("item", new XElement("title", title), new XElement("guid", guid),
                    new XElement("pubDate", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc).ToString("r", CultureInfo.InvariantCulture)),
                    new XElement("enclosure", new XAttribute("url", Url + "/payload/" + guid),
                        new XAttribute("length", 1_073_741_824L), new XAttribute("type", "application/octet-stream")),
                    new XElement(torrent + "attr", new XAttribute("name", "seeders"), new XAttribute("value", 20)),
                    new XElement(torrent + "attr", new XAttribute("name", "size"), new XAttribute("value", 1_073_741_824L)),
                    new XElement(ns + "attr", new XAttribute("name", "size"), new XAttribute("value", 1_073_741_824L))));
            }
            return new XElement("rss", new XAttribute("version", "2.0"), channel).ToString();
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseFirst();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token); }
            finally { await _app.DisposeAsync(); }
        }

        internal sealed record Attempt(int Sequence, string Mode, string SourceAlias, string AccountAlias, string ParameterAlias,
            string RowId, string Query, int Offset, int Limit, string EventId, int Status, long ArrivalMs, string[] Guids, string ResponseHash)
        {
            public bool ResponseSent { get; set; }
            public string ResponseBody { get; init; } = "";
        }
    }
}

internal sealed class RequestBudgetAdmissionWitness : IRateLimitService
{
    private readonly RateLimitService _inner;
    private readonly ConcurrentQueue<AdmissionObservation> _observations = new();
    private readonly TaskCompletionSource<AdmissionObservation> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AdmissionTarget? _target;

    public RequestBudgetAdmissionWitness(RateLimitService inner) => _inner = inner;
    public Task<AdmissionObservation> Entered => _entered.Task;
    public AdmissionObservation[] Observations => _observations.ToArray();

    public void Arm(string host, string rowId) => Volatile.Write(ref _target, new AdmissionTarget(host, rowId));

    public Task WaitAndPulseAsync(string baseKey, string? subKey, TimeSpan rateLimit,
        CancellationToken cancellationToken = default)
    {
        var target = Volatile.Read(ref _target);
        if (target != null && target.Host == baseKey && target.RowId == subKey)
        {
            // Observe admission before the real pacing wait can block.
            var observation = new AdmissionObservation("competitor-entered-real-pacing", "fixture-loopback", subKey!);
            if (_entered.TrySetResult(observation)) _observations.Enqueue(observation);
        }
        return _inner.WaitAndPulseAsync(baseKey, subKey, rateLimit, cancellationToken);
    }

    public Task WaitAndPulseAsync(string baseKey, string? subKey, TimeSpan rateLimit,
        Func<CancellationToken, Task> beforePulse, CancellationToken cancellationToken = default)
    {
        var target = Volatile.Read(ref _target);
        if (target != null && target.Host == baseKey && target.RowId == subKey)
        {
            var observation = new AdmissionObservation("competitor-entered-real-pacing", "fixture-loopback", subKey!);
            if (_entered.TrySetResult(observation)) _observations.Enqueue(observation);
        }
        return _inner.WaitAndPulseAsync(baseKey, subKey, rateLimit, beforePulse, cancellationToken);
    }

    public TimeSpan GetTimeUntilAllowed(string baseKey, string? subKey) => _inner.GetTimeUntilAllowed(baseKey, subKey);

    private sealed record AdmissionTarget(string Host, string RowId);
    public sealed record AdmissionObservation(string Event, string SourceAlias, string RowId);
}
