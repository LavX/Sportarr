using System.Net.Http.Json;
using System.Text.Json;
using Sportarr.Api.Endpoints;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;
using Sportarr.Api.Startup;
using Xunit.Abstractions;

namespace Sportarr.Api.Tests.SearchValidation;

internal sealed class MotorsportManualQueryHttpHarness : IAsyncDisposable
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly WebApplication _app;
    private readonly string _directory;
    private readonly ITestOutputHelper _output;
    private Task? _operation;
    private HttpClient? _client;
    internal SportarrDbContext Db { get; }
    internal Event Event { get; private set; } = null!;
    internal Source Transport { get; }
    internal IServiceProvider Services => _app.Services;
    internal int ProfileId { get; private set; }

    private MotorsportManualQueryHttpHarness(WebApplication app, string directory, Source source, ITestOutputHelper output)
    {
        _app = app; _directory = directory; Transport = source; _output = output;
        Db = Services.GetRequiredService<SportarrDbContext>();
    }

    internal static async Task<MotorsportManualQueryHttpHarness> CreateAsync(ITestOutputHelper output, string mode = "phrase")
    {
        var directory = Path.Combine(Path.GetTempPath(), "sportarr-motorsport-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Source? source = null;
        WebApplication? app = null;
        try
        {
            source = await Source.StartAsync(mode);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sportarr:DataPath"] = directory });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddDbContextFactory<SportarrDbContext>(options => options.UseInMemoryDatabase(directory));
            builder.Services.AddMemoryCache();
            builder.Services.AddSingleton<DownloadOwnershipCoordinator>();
            builder.Services.AddSingleton(Mock.Of<IRemotePathMappingService>());
            builder.Services.AddSingleton<IRateLimitService, RateLimitService>();
            builder.Services.AddTransient<RateLimitHandler>();
            builder.Services.AddSportarrHttpClients();
            builder.Services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new LoopbackFilter(source));
            builder.Services.AddSingleton(provider => provider.GetRequiredService<IHttpClientFactory>().CreateClient("metadata"));
            foreach (var type in new[]
            {
                typeof(ConfigService), typeof(DownloadClientService), typeof(NotificationService), typeof(SportarrApiClient),
                typeof(MediaFileParser), typeof(SportsFileNameParser), typeof(EventPartDetector), typeof(CustomFormatService),
                typeof(CustomFormatMatchCache), typeof(ReleaseEvaluator), typeof(EventQueryService), typeof(DelayProfileService),
                typeof(ReleaseMatchingService), typeof(ReleaseCacheService), typeof(ReleaseMatchScorer), typeof(SearchResultCache),
                typeof(ReleaseProfileService), typeof(QualityDetectionService), typeof(IndexerStatusService),
                typeof(IndexerSearchService), typeof(AutomaticSearchService)
            }) builder.Services.AddSingleton(type);
            foreach (var type in new[]
            {
                typeof(DownloadClientService), typeof(NotificationService), typeof(CustomFormatService),
                typeof(ReleaseEvaluator), typeof(DelayProfileService), typeof(ReleaseMatchingService),
                typeof(ReleaseCacheService), typeof(ReleaseProfileService), typeof(QualityDetectionService),
                typeof(IndexerStatusService), typeof(IndexerSearchService), typeof(AutomaticSearchService)
            }) builder.Services.AddScoped(type);
            foreach (var type in new[]
            {
                typeof(DownloadClientService), typeof(NotificationService), typeof(CustomFormatService),
                typeof(ReleaseEvaluator), typeof(DelayProfileService), typeof(ReleaseMatchingService),
                typeof(ReleaseCacheService), typeof(ReleaseProfileService), typeof(QualityDetectionService),
                typeof(IndexerStatusService), typeof(IndexerSearchService), typeof(AutomaticSearchService)
            }) builder.Services.AddScoped(type);
            app = builder.Build();
            app.MapManualEventSearchEndpoints();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StartAsync(startup.Token);
            var rig = new MotorsportManualQueryHttpHarness(app, directory, source, output);
            var configService = rig.Services.GetRequiredService<ConfigService>();
            var config = await configService.GetConfigAsync();
            config.EnableMultiPartEpisodes = false;
            config.IndexerRetention = 0;
            config.SearchCacheDuration = 300;
            config.IndexerHttpTimeoutSeconds = 10;
            await configService.SaveConfigAsync(config);
            var library = Path.Combine(directory, "library");
            var drop = Path.Combine(directory, "drop");
            var watch = Path.Combine(directory, "watch");
            foreach (var path in new[] { library, drop, watch }) Directory.CreateDirectory(path);
            var root = new RootFolder { Path = library };
            var profile = new QualityProfile { Name = "Query fixture", IsDefault = true,
                Items = new List<QualityItem> { new() { Name = "WEBDL-1080p", Quality = 3, Allowed = true } } };
            rig.Db.AddRange(root, profile);
            await rig.Db.SaveChangesAsync();
            rig.ProfileId = profile.Id;
            var league = new League { Name = "Formula 1", Sport = "Motorsport", Monitored = true,
                RootFolderId = root.Id, QualityProfileId = profile.Id };
            rig.Db.Leagues.Add(league);
            await rig.Db.SaveChangesAsync();
            rig.Event = new Event { Title = "Silverstone Grand Prix Practice 1", Sport = "Motorsport", ExternalId = "ev-2336155",
                LeagueId = league.Id, League = league, EventDate = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc),
                Status = "Completed", Monitored = true, Season = "2026", Round = "6", QualityProfileId = profile.Id };
            rig.Db.AddRange(rig.Event, new Indexer { Name = "Query fixture source", Type = IndexerType.Torznab,
                Url = source.Url, ApiPath = "/api", ApiKey = "fixture", Categories = new() { "5060" }, RequestDelayMs = 1,
                Enabled = true, EnableAutomaticSearch = true, EnableInteractiveSearch = true, EnableRss = false },
                new DownloadClient { Name = "Descriptor refusal", Type = DownloadClientType.TorrentBlackhole,
                    Host = "localhost", Enabled = true, BlackholeFolder = drop, WatchFolder = watch, ReadOnly = true });
            await rig.Db.SaveChangesAsync();
            return rig;
        }
        catch
        {
            if (app != null) await app.DisposeAsync();
            if (source != null) await source.DisposeAsync();
            Directory.Delete(directory, true);
            throw;
        }
    }

    internal async Task ConfigureAsync(string title, string round = "8")
    {
        Event.Title = title;
        Event.Round = round;
        Event.EventDate = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);
        await Db.SaveChangesAsync();
    }

    internal string AddRelease(string title, string? suppliedEventId = "ev-2336155", string guid = "offer-target")
    {
        Transport.Releases.Add(new ReleaseSearchResult { Guid = guid, Title = title, Indexer = "Query fixture source",
            DownloadUrl = Transport.Url + "/payload/" + guid, PublishDate = DateTime.UtcNow.AddDays(-1),
            Size = 1_073_741_824L, Seeders = 20, SportarrEventId = suppliedEventId });
        return title;
    }

    internal async Task<ReleaseSearchResult[]> ManualAsync(string? customQuery = null)
    {
        _client ??= _app.GetTestClient();
        var operation = SendManualAsync(customQuery);
        _operation = operation;
        return await operation.WaitAsync(Deadline);
    }

    internal async Task<ReleaseSearchResult[][]> ConcurrentManualAsync(string? customQuery = null)
    {
        _client ??= _app.GetTestClient();
        var operations = new[] { SendManualAsync(customQuery), SendManualAsync(customQuery) };
        _operation = Task.WhenAll(operations);
        return await Task.WhenAll(operations).WaitAsync(Deadline);
    }

    private async Task<ReleaseSearchResult[]> SendManualAsync(string? customQuery)
    {
        using var response = await _client!.PostAsJsonAsync($"/api/event/{Event.Id}/search", new { customQuery });
        var body = await response.Content.ReadAsStringAsync();
        _output.WriteLine(JsonSerializer.Serialize(new { ManualStatus = (int)response.StatusCode, Body = body }));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("results").Deserialize<ReleaseSearchResult[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_operation != null)
            {
                try { await _operation.WaitAsync(Deadline); }
                catch (Exception exception) { _output.WriteLine("Operation completion: " + exception.GetType().Name); }
            }
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            { Transport.Mode, Transport.Arrivals, Transport.CapsRequests, Queries = Transport.Searches, Transport.DescriptorAttempts, Transport.DescriptorGuids, Transport.Violations }));
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(shutdown.Token); }
            finally
            {
                _client?.Dispose();
                await _app.DisposeAsync();
                await Transport.DisposeAsync();
                if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            }
        }
        Assert.Empty(Transport.Violations);
    }

    private sealed class LoopbackFilter(Source source) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler.Dispose();
            builder.PrimaryHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var target = new Uri(source.Url);
                    if (context.DnsEndPoint.Host != "127.0.0.1" || context.DnsEndPoint.Port != target.Port)
                    {
                        source.Reject("outside-loopback-boundary");
                        throw new InvalidOperationException("The request left the fixture boundary.");
                    }
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
        };
    }

    internal sealed class Source : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<QueryEvidence> _searches = new();
        private readonly ConcurrentQueue<string> _violations = new();
        private readonly ConcurrentQueue<string> _descriptorGuids = new();
        private int _arrivals;
        private int _descriptors;
        private int _caps;
        private readonly string _prefix = "/query-" + Guid.NewGuid().ToString("N");
        internal string Url { get; private set; } = "";
        internal string Mode { get; }
        internal List<ReleaseSearchResult> Releases { get; } = new();
        internal QueryEvidence[] Searches => _searches.ToArray();
        internal string[] Violations => _violations.ToArray();
        internal int Arrivals => Volatile.Read(ref _arrivals);
        internal int DescriptorAttempts => Volatile.Read(ref _descriptors);
        internal string[] DescriptorGuids => _descriptorGuids.ToArray();
        internal int CapsRequests => Volatile.Read(ref _caps);
        internal int SearchDelayMs { get; set; }
        private Source(WebApplication app, string mode) { _app = app; Mode = mode; }
        internal void Reject(string reason) => _violations.Enqueue(reason);

        internal static async Task<Source> StartAsync(string mode)
        {
            if (mode is not ("and" or "phrase" or "ordered")) throw new ArgumentException("Unknown source semantics.", nameof(mode));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var source = new Source(app, mode);
            app.Run(source.RespondAsync);
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await app.StartAsync(startup.Token);
                source.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + source._prefix;
                return source;
            }
            catch { await app.DisposeAsync(); throw; }
        }

        private async Task RespondAsync(HttpContext context)
        {
            if (Interlocked.Increment(ref _arrivals) > 16 || context.Request.Method != "GET")
            { Reject("source-attempt-ceiling-or-method"); context.Response.StatusCode = 400; return; }
            var descriptor = Releases.FirstOrDefault(release => context.Request.Path == _prefix + "/payload/" + release.Guid);
            if (descriptor != null)
            {
                _descriptorGuids.Enqueue(descriptor.Guid);
                Interlocked.Increment(ref _descriptors);
                context.Response.StatusCode = 410;
                return;
            }
            var query = context.Request.Query;
            if (context.Request.Path != _prefix + "/api" || query["apikey"] != "fixture" || query.ContainsKey("sportarrid"))
            { Reject("unexpected-source-request"); context.Response.StatusCode = 400; return; }
            string xml;
            if (query["t"] == "caps")
            {
                Interlocked.Increment(ref _caps);
                xml = "<caps><searching><search available=\"yes\" supportedParams=\"q\"/></searching></caps>";
            }
            else if (query["t"] == "search")
            {
                if (SearchDelayMs > 0)
                    await Task.Delay(SearchDelayMs, context.RequestAborted);
                var text = query["q"].ToString();
                if (text.Length is 0 or > 256) { Reject("query-size"); context.Response.StatusCode = 400; return; }
                var releases = Releases.Where(release => Matches(release.Title, text)).ToArray();
                _searches.Enqueue(new QueryEvidence(text, releases.Select(release => release.Guid).ToArray(), releases.Select(release => release.SportarrEventId).ToArray()));
                XNamespace ns = "http://torznab.com/schemas/2015/feed";
                xml = new XElement("rss", new XElement("channel", releases.Select(release => new XElement("item",
                    new XElement("title", release.Title), new XElement("guid", release.Guid),
                    new XElement("pubDate", release.PublishDate.ToString("r", System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement("enclosure", new XAttribute("url", release.DownloadUrl), new XAttribute("length", release.Size), new XAttribute("type", "application/x-bittorrent")),
                    new XElement(ns + "attr", new XAttribute("name", "seeders"), new XAttribute("value", "20")),
                    release.SportarrEventId == null ? null : new XElement(ns + "attr", new XAttribute("name", "sportarrid"), new XAttribute("value", release.SportarrEventId)))))).ToString();
            }
            else { Reject("unknown-source-operation"); context.Response.StatusCode = 400; return; }
            context.Response.ContentType = "application/xml";
            using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            responseDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            await context.Response.WriteAsync(xml, responseDeadline.Token);
        }

        private bool Matches(string title, string query)
        {
            static string[] Tokens(string value) => Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(match => match.Value).ToArray();
            var wanted = Tokens(query);
            var actual = Tokens(title);
            if (Mode == "phrase") return Enumerable.Range(0, actual.Length).Any(index => actual.Skip(index).Take(wanted.Length).SequenceEqual(wanted));
            if (Mode == "and") return wanted.All(actual.Contains);
            var cursor = 0;
            foreach (var token in actual)
            {
                if (cursor < wanted.Length && token == wanted[cursor]) cursor++;
                if (cursor == wanted.Length) return true;
            }
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(shutdown.Token); }
            finally { await _app.DisposeAsync(); }
        }
        internal sealed record QueryEvidence(string Query, string[] Guids, string?[] SuppliedEventIds);
    }
}
