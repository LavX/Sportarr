using System.Net;
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

internal sealed class CachePolicyHttpHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _directory;
    public SportarrDbContext Db { get; }
    public HttpClient Client { get; }
    public SourceTransport Transport { get; }
    public IServiceProvider Services => _app.Services;
    public Event Event { get; private set; } = null!;
    public Indexer Indexer { get; private set; } = null!;
    public QualityProfile Profile { get; private set; } = null!;
    public DateTime Now { get; } = DateTime.UtcNow;

    private CachePolicyHttpHarness(WebApplication app, string directory, SourceTransport transport)
    {
        _app = app;
        _directory = directory;
        Transport = transport;
        Db = Services.GetRequiredService<SportarrDbContext>();
        Client = app.GetTestClient();
    }

    public static async Task<CachePolicyHttpHarness> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sportarr-cache-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sportarr:DataPath"] = directory });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContextFactory<SportarrDbContext>(options => options.UseInMemoryDatabase(directory));
        builder.Services.AddSingleton(new SportarrDbContext(
            new DbContextOptionsBuilder<SportarrDbContext>().UseInMemoryDatabase(directory).Options));
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<DownloadOwnershipCoordinator>();
        var transport = new SourceTransport("cache-" + Guid.NewGuid().ToString("N") + ".invalid");
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
        }) builder.Services.AddSingleton(type);
        var app = builder.Build();
        app.MapManualEventSearchEndpoints();
        await app.StartAsync();
        var rig = new CachePolicyHttpHarness(app, directory, transport);
        var configService = rig.Services.GetRequiredService<ConfigService>();
        var config = await configService.GetConfigAsync();
        config.EnableMultiPartEpisodes = false;
        config.IndexerRetention = 0;
        config.SearchCacheDuration = 300;
        await configService.SaveConfigAsync(config);
        var library = Path.Combine(directory, "library");
        var drop = Path.Combine(directory, "drop");
        var watch = Path.Combine(directory, "watch");
        foreach (var path in new[] { library, drop, watch }) Directory.CreateDirectory(path);
        var root = new RootFolder { Path = library };
        rig.Profile = new QualityProfile
        {
            Name = "Cache policy", IsDefault = true,
            Items = new List<QualityItem> { new() { Name = "WEBDL-1080p", Quality = 3, Allowed = true } }
        };
        rig.Db.AddRange(root, rig.Profile);
        await rig.Db.SaveChangesAsync();
        var league = new League { Name = "FIFA World Cup", Sport = "Soccer", Monitored = true,
            RootFolderId = root.Id, QualityProfileId = rig.Profile.Id, SearchQueryTemplate = "cache-query" };
        rig.Db.Leagues.Add(league);
        await rig.Db.SaveChangesAsync();
        rig.Event = new Event { Title = "Spain vs Belgium", Sport = "Soccer", ExternalId = "ev-2336155",
            HomeTeamName = "Spain", AwayTeamName = "Belgium", LeagueId = league.Id, League = league,
            EventDate = rig.Now.Date.AddDays(-40), Status = "Completed", Monitored = true,
            Season = rig.Now.Date.AddDays(-40).Year.ToString(), QualityProfileId = rig.Profile.Id };
        rig.Indexer = new Indexer { Name = "Cache source", Type = IndexerType.Torznab,
            Url = "http://" + transport.Host, ApiKey = "fixture", Categories = new List<string> { "5060" },
            Enabled = true, EnableAutomaticSearch = true, EnableInteractiveSearch = true, EnableRss = false };
        rig.Db.AddRange(rig.Event, rig.Indexer, new DownloadClient { Name = "Cache blackhole",
            Type = DownloadClientType.TorrentBlackhole, Host = "localhost", Enabled = true,
            BlackholeFolder = drop, WatchFolder = watch, ReadOnly = true });
        await rig.Db.SaveChangesAsync();
        return rig;
    }

    public ReleaseSearchResult Release(string guid = "release-one", int ageDays = 1) => new()
    {
        Title = $"Spain.vs.Belgium.{Event.EventDate:yyyy.MM.dd}.1080p.WEB-DL.H264-GROUP",
        Guid = guid, DownloadUrl = "http://" + Transport.Host + "/payload/" + guid,
        Indexer = Indexer.Name, IndexerId = Indexer.Id, Protocol = "Torrent", Seeders = 20,
        PublishDate = Now.AddDays(-ageDays), Size = 1_073_741_824, SportarrEventId = Event.ExternalId
    };

    public async Task<List<ReleaseSearchResult>> ManualAsync(int? eventId = null)
    {
        using var response = await Client.PostAsJsonAsync($"/api/event/{eventId ?? Event.Id}/search", new { });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("results").Deserialize<List<ReleaseSearchResult>>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    public Task<AutomaticSearchResult> AutomaticAsync() => Services.GetRequiredService<AutomaticSearchService>()
        .SearchAndDownloadEventAsync(Event.Id, Profile.Id);

    public async Task RetentionAsync(int days)
    {
        var service = Services.GetRequiredService<ConfigService>();
        var config = await service.GetConfigAsync();
        config.IndexerRetention = days;
        await service.SaveConfigAsync(config);
    }

    public async Task AddProfileAsync(string rule, string scope)
    {
        Db.ReleaseProfiles.Add(new ReleaseProfile { Name = "Cache " + rule,
            IndexerId = scope == "global" ? new List<int>() : new List<int> { scope == "source" ? Indexer.Id : Indexer.Id + 10 },
            Ignored = rule == "ignored" ? "H264" : "",
            Preferred = rule == "preferred" ? new List<PreferredKeyword> { new() { Key = "H264", Value = 100 } } : new() });
        await Db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        Assert.Empty(Transport.Unexpected);
    }

    internal sealed class SourceTransport : HttpMessageHandler, IHttpClientFactory
    {
        public string Host { get; }
        public List<Dictionary<string, string>> Searches { get; } = new();
        public List<string> Unexpected { get; } = new();
        public int DescriptorAttempts { get; private set; }
        public Func<Dictionary<string, string>, IEnumerable<ReleaseSearchResult>> Results { get; set; } = _ => Array.Empty<ReleaseSearchResult>();
        public SourceTransport(string host) => Host = host;
        public HttpClient CreateClient(string name) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host != Host)
            {
                Unexpected.Add(uri.GetLeftPart(UriPartial.Path));
                throw new InvalidOperationException("Unexpected cache test HTTP destination.");
            }
            if (uri.AbsolutePath.StartsWith("/payload/", StringComparison.Ordinal))
            {
                DescriptorAttempts++;
                // Selection is observable without simulating a successful download.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
            }
            var query = QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            string xml;
            if (query.GetValueOrDefault("t") == "caps")
                xml = "<caps><searching><search available=\"yes\" supportedParams=\"q,sportarrid\"/></searching><categories><category id=\"5000\" name=\"TV\"><subcat id=\"5060\" name=\"Sport\"/></category></categories></caps>";
            else if (query.GetValueOrDefault("t") == "search")
            {
                Searches.Add(query);
                XNamespace ns = "http://torznab.com/schemas/2015/feed";
                var items = Results(query).Select(r => new XElement("item",
                    new XElement("title", r.Title), new XElement("guid", r.Guid), new XElement("link", r.DownloadUrl),
                    new XElement("pubDate", r.PublishDate.ToString("r", System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement("enclosure", new XAttribute("url", r.DownloadUrl), new XAttribute("length", r.Size), new XAttribute("type", "application/x-bittorrent")),
                    new XElement(ns + "attr", new XAttribute("name", "seeders"), new XAttribute("value", r.Seeders ?? 20)),
                    new XElement(ns + "attr", new XAttribute("name", "category"), new XAttribute("value", "5060")),
                    r.TorrentInfoHash == null ? null : new XElement(ns + "attr", new XAttribute("name", "infohash"), new XAttribute("value", r.TorrentInfoHash)),
                    r.SportarrEventId == null ? null : new XElement(ns + "attr", new XAttribute("name", "sportarrid"), new XAttribute("value", r.SportarrEventId))));
                xml = new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "torznab", ns),
                    new XElement("channel", new XElement("title", "Cache source"), items)).ToString();
            }
            else
            {
                Unexpected.Add(uri.PathAndQuery);
                throw new InvalidOperationException("Unexpected cache test source operation.");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") });
        }
    }
}
