using System.Net;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Sportarr.Api.Data;
using Sportarr.Api.Endpoints;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Api.Tests.SearchValidation;

internal sealed class CompetitionDateRouteHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _directory;
    private readonly IServiceScope _scope;
    public TaskTerminalCapture Terminal { get; }
    public RouteLogCapture Logs { get; }
    public SportarrDbContext Db { get; }
    public HttpClient Client { get; }
    public DateSourceTransport Transport { get; }
    public string DropFolder => Path.Combine(_directory, "drop");
    public IServiceProvider Services => _app.Services;
    public Event Event { get; private set; } = null!;
    public Indexer Indexer { get; private set; } = null!;
    public QualityProfile Profile { get; private set; } = null!;
    public DateTime Now { get; } = DateTime.UtcNow;

    private CompetitionDateRouteHarness(WebApplication app, string directory, DateSourceTransport transport)
    {
        _app = app;
        _directory = directory;
        Transport = transport;
        _scope = Services.CreateScope();
        Db = _scope.ServiceProvider.GetRequiredService<SportarrDbContext>();
        Terminal = Services.GetRequiredService<TaskTerminalCapture>();
        Logs = Services.GetRequiredService<RouteLogCapture>();
        Client = app.GetTestClient();
    }

    public static async Task<CompetitionDateRouteHarness> CreateAsync(int variant)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sportarr-date-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sportarr:DataPath"] = directory });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var logs = new RouteLogCapture();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Services.AddSingleton(logs);
        var terminal = new TaskTerminalCapture();
        builder.Services.AddSingleton(terminal);
        builder.Services.AddDbContextFactory<SportarrDbContext>(options => options.UseInMemoryDatabase(directory).AddInterceptors(terminal));
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<DownloadOwnershipCoordinator>();
        var transport = new DateSourceTransport("date-route.invalid");
        builder.Services.AddSingleton<IHttpClientFactory>(transport);
        builder.Services.AddSingleton(transport.CreateClient("metadata"));
        builder.Services.AddSingleton(Mock.Of<IRemotePathMappingService>());
        builder.Services.AddSingleton(Mock.Of<IRateLimitService>());
        foreach (var type in new[]
        {
            typeof(ConfigService), typeof(DownloadClientService), typeof(NotificationService), typeof(SportarrApiClient),
            typeof(MediaFileParser), typeof(SportsFileNameParser), typeof(EventPartDetector), typeof(CustomFormatService),
            typeof(CustomFormatMatchCache), typeof(ReleaseEvaluator), typeof(EventQueryService), typeof(DelayProfileService),
            typeof(ReleaseMatchingService), typeof(ReleaseCacheService), typeof(ReleaseMatchScorer), typeof(SearchResultCache),
            typeof(ReleaseProfileService), typeof(QualityDetectionService), typeof(IndexerStatusService),
            typeof(IndexerSearchService), typeof(AutomaticSearchService)
        }) builder.Services.AddScoped(type);
        builder.Services.AddSingleton<TaskService>();
        builder.Services.AddSingleton<RssSyncService>();
        builder.Services.AddSingleton<DiskScanService>();
        builder.Services.AddSingleton<HubChangesPollerService>();
        var app = builder.Build();
        app.MapManualEventSearchEndpoints();
        app.MapEventSearchAndGrabEndpoints();
        app.MapTaskEndpoints();
        await app.StartAsync();
        var rig = new CompetitionDateRouteHarness(app, directory, transport);
        var configService = rig._scope.ServiceProvider.GetRequiredService<ConfigService>();
        var config = await configService.GetConfigAsync();
        config.EnableMultiPartEpisodes = false;
        config.IndexerRetention = 0;
        config.SearchCacheDuration = 300;
        config.IndexerMinimumAgeMinutes = 0;
        config.RssReleaseAgeLimit = 14;
        await configService.SaveConfigAsync(config);
        var library = Path.Combine(directory, "library");
        var drop = Path.Combine(directory, "drop");
        var watch = Path.Combine(directory, "watch");
        foreach (var path in new[] { library, drop, watch }) Directory.CreateDirectory(path);
        var root = new RootFolder { Path = library };
        rig.Profile = new QualityProfile
        {
            Name = "Date route diagnostic", IsDefault = true,
            Items = new List<QualityItem> { new() { Name = "WEBDL-720p", Quality = 3, Allowed = true } }
        };
        rig.Db.AddRange(root, rig.Profile);
        await rig.Db.SaveChangesAsync();
        var league = new League { Name = "Diamond League", Sport = "Athletics", Monitored = true,
            RootFolderId = root.Id, QualityProfileId = rig.Profile.Id, SearchQueryTemplate = "{EventTitle}" };
        rig.Db.Leagues.Add(league);
        await rig.Db.SaveChangesAsync();
        rig.Event = new Event { Title = "Womens 100 metres Final at Fir Meeting", Sport = "Athletics",
            ExternalId = variant >= 2 ? "ev-2336155" : "fixture:71fd4260b242d98918fbc534c47bbd53",
            LeagueId = league.Id, League = league,
            EventDate = new DateTime(2022, 7, 15, 18, 0, 0, DateTimeKind.Utc), Status = "Completed", Monitored = true,
            Season = "2022", QualityProfileId = rig.Profile.Id };
        rig.Indexer = new Indexer { Name = "Cache source", Type = IndexerType.Torznab,
            Url = "http://" + transport.Host, ApiKey = "fixture", Categories = new List<string> { "5060" },
            Enabled = true, EnableAutomaticSearch = true, EnableInteractiveSearch = true, EnableRss = true };
        rig.Db.AddRange(rig.Event, rig.Indexer, new DownloadClient { Name = "Cache blackhole",
            Type = DownloadClientType.TorrentBlackhole, Host = "localhost", Enabled = true,
            BlackholeFolder = drop, WatchFolder = watch, ReadOnly = true });
        await rig.Db.SaveChangesAsync();
        return rig;
    }

    public const string ExactTitle = "Diamond.League.2022.07.15.Womens.100.metres.Final.at.Fir.Meeting.720p.WEB-DL.H264.MULTi-FIELD";
    public DateTime Publication => Now.AddDays(-1);
    public ReleaseSearchResult Release(int variant) => new()
    {
        Title = variant == 1 ? ExactTitle.Replace("2022.07.15", "2022.07.17") : ExactTitle,
        Guid = "athletics-date-offer", DownloadUrl = "http://" + Transport.Host + "/payload/athletics-date-offer",
        Indexer = Indexer.Name, IndexerId = Indexer.Id, Protocol = "Torrent", Seeders = 20,
        PublishDate = Publication, Size = 4_294_967_296,
        SportarrEventId = variant == 2 ? "ev-2336155" : variant == 3 ? "ev-2336156" : null
    };

    public async Task<JsonElement> RequestAsync(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(20));
        var body = await response.Content.ReadAsStringAsync();
        Http.Add(new { method, path, requestBody = method == "POST" ? "{}" : null, status = (int)response.StatusCode, body });
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public List<object> Http { get; } = new();

    public async Task<AppTask> RunTaskAsync(string route)
    {
        var before = await RequestAsync("GET", "/api/task");
        Assert.Empty(before.EnumerateArray());
        var response = await RequestAsync("POST", route == "automatic" ? $"/api/event/{Event.Id}/automatic-search" : "/api/task/scheduled/rss-sync/trigger");
        var expectedCommand = route == "automatic" ? "EventSearch" : "RssSync";
        if (route == "rss") Assert.True(response.GetProperty("queued").GetBoolean());
        var terminal = await Terminal.Completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var after = await RequestAsync("GET", "/api/task");
        var owned = Assert.Single(after.EnumerateArray());
        Assert.Equal(expectedCommand, owned.GetProperty("commandName").GetString());
        if (route == "automatic") Assert.Equal(response.GetProperty("taskId").GetInt32(), terminal);
        Assert.Equal(owned.GetProperty("id").GetInt32(), terminal);
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SportarrDbContext>().Tasks.AsNoTracking().SingleAsync(t => t.Id == terminal);
    }

    public async ValueTask DisposeAsync()
    {
        // Drain the owned processor before disposing its database or temporary paths.
        var service = Services.GetRequiredService<TaskService>();
        using (var scope = Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<SportarrDbContext>().Tasks.AsNoTracking().ToListAsync();
            foreach (var task in rows.Where(t => t.Ended == null)) await service.CancelTaskAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(5));
        }
        var gate = (SemaphoreSlim)typeof(TaskService).GetField("_taskLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("Fixture drain failed. The owned task processor remains active; temporary paths are retained.");
        gate.Release();
        Client.Dispose();
        _scope.Dispose();
        await _app.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        Assert.Empty(Transport.Unexpected);
    }

    internal sealed class DateSourceTransport : HttpMessageHandler, IHttpClientFactory
    {
        public string Host { get; }
        public List<Dictionary<string, string>> Searches { get; } = new();
        public List<string> Unexpected { get; } = new();
        public int DescriptorAttempts { get; private set; }
        public bool DescriptorSucceeds { get; set; }
        public List<object> Responses { get; } = new();
        public Func<Dictionary<string, string>, IEnumerable<ReleaseSearchResult>> Results { get; set; } = _ => Array.Empty<ReleaseSearchResult>();
        public DateSourceTransport(string host) => Host = host;
        public HttpClient CreateClient(string name) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host != Host)
            {
                Unexpected.Add(uri.GetLeftPart(UriPartial.Path));
                throw new InvalidOperationException("Unexpected cache test HTTP destination.");
            }
            if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/payload/athletics-date-offer")
            {
                DescriptorAttempts++;
                if (!DescriptorSucceeds)
                {
                    Responses.Add(new { path = uri.PathAndQuery, status = 410, body = "" });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
                }

                var payload = Encoding.ASCII.GetBytes(
                    "d8:announce31:http://tracker.invalid/announce4:infod6:lengthi1e4:name16:sportarr-fixture12:piece lengthi16384e6:pieces20:00000000000000000000ee");
                Responses.Add(new { path = uri.PathAndQuery, status = 200, bytes = payload.Length });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                });
            }
            if (request.Method != HttpMethod.Get || uri.AbsolutePath != "/api")
            {
                Unexpected.Add(request.Method + " " + uri.PathAndQuery);
                throw new InvalidOperationException("Unexpected date route source path or method.");
            }
            var query = QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            string xml;
            if (query.GetValueOrDefault("t") == "caps")
                xml = "<caps><limits max=\"100\" default=\"100\"/><searching><search available=\"yes\" supportedParams=\"q,sportarrid\"/></searching><categories><category id=\"5000\" name=\"TV\"><subcat id=\"5060\" name=\"Sport\"/></category></categories></caps>";
            else if (query.GetValueOrDefault("t") == "search")
            {
                if (Searches.Count >= 10) throw new InvalidOperationException("Date route fixture source request ceiling exceeded.");
                Searches.Add(query);
                XNamespace ns = "http://torznab.com/schemas/2015/feed";
                var items = Results(query).Select(r => new XElement("item",
                    new XElement("title", r.Title), new XElement("guid", r.Guid), new XElement("link", r.DownloadUrl),
                    new XElement("pubDate", r.PublishDate.ToString("r", System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement("enclosure", new XAttribute("url", r.DownloadUrl), new XAttribute("length", r.Size), new XAttribute("type", "application/x-bittorrent")),
                    new XElement(ns + "attr", new XAttribute("name", "seeders"), new XAttribute("value", r.Seeders ?? 20)),
                    new XElement(ns + "attr", new XAttribute("name", "category"), new XAttribute("value", "5060")),
                    r.SportarrEventId == null ? null : new XElement(ns + "attr", new XAttribute("name", "sportarrid"), new XAttribute("value", r.SportarrEventId))));
                xml = new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "torznab", ns),
                    new XElement("channel", new XElement("title", "Cache source"), new XElement(XName.Get("response", "http://www.newznab.com/DTD/2010/feeds/attributes/"), new XAttribute("offset", "0"), new XAttribute("total", "1")), items)).ToString();
            }
            else
            {
                Unexpected.Add(uri.PathAndQuery);
                throw new InvalidOperationException("Unexpected cache test source operation.");
            }
            Responses.Add(new { path = uri.PathAndQuery, status = 200, body = xml });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") });
        }
    }
}

internal sealed class TaskTerminalCapture : SaveChangesInterceptor
{
    public TaskCompletionSource<int> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        foreach (var row in eventData.Context!.ChangeTracker.Entries<AppTask>())
            if (row.Entity.Ended != null) Completed.TrySetResult(row.Entity.Id);
        return ValueTask.FromResult(result);
    }
}

internal sealed class RouteLogCapture : ILoggerProvider
{
    public ConcurrentQueue<object> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Lines);
    public void Dispose() { }
    private sealed class CaptureLogger(string category, ConcurrentQueue<object> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning ||
            category.StartsWith("Sportarr.Api.Services", StringComparison.Ordinal);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            if (IsEnabled(logLevel)) lines.Enqueue(new { category, level = logLevel.ToString(), message = formatter(state, exception), error = exception?.ToString() });
        }
    }
}
