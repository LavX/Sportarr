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

internal sealed class CachePolicySourceQualityHarness : IAsyncDisposable
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

    private CachePolicySourceQualityHarness(WebApplication app, string directory, SourceTransport transport)
    {
        _app = app;
        _directory = directory;
        Transport = transport;
        Db = Services.GetRequiredService<SportarrDbContext>();
        Client = app.GetTestClient();
    }

    public static async Task<CachePolicySourceQualityHarness> CreateAsync()
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
        var rig = new CachePolicySourceQualityHarness(app, directory, transport);
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
            RootFolderId = root.Id, QualityProfileId = rig.Profile.Id, SearchQueryTemplate = "Spain Belgium" };
        rig.Db.Leagues.Add(league);
        await rig.Db.SaveChangesAsync();
        rig.Event = new Event { Title = "Spain vs Belgium", Sport = "Soccer", ExternalId = "ev-2336155",
            HomeTeamName = "Spain", AwayTeamName = "Belgium", LeagueId = league.Id, League = league,
            EventDate = rig.Now.Date.AddDays(-40), Status = "Completed", Monitored = true,
            Season = rig.Now.Date.AddDays(-40).Year.ToString(), QualityProfileId = rig.Profile.Id };
        rig.Indexer = new Indexer { Name = "Cache source", Type = IndexerType.BroadcasTheNet,
            Url = "http://" + transport.Host, ApiKey = "fixture", Categories = new List<string> { "5060" },
            Enabled = true, EnableAutomaticSearch = true, EnableInteractiveSearch = true, EnableRss = false };
        rig.Db.AddRange(rig.Event, rig.Indexer, new DownloadClient { Name = "Cache blackhole",
            Type = DownloadClientType.TorrentBlackhole, Host = "localhost", Enabled = true,
            BlackholeFolder = drop, WatchFolder = watch, ReadOnly = true });
        await rig.Db.SaveChangesAsync();
        return rig;
    }

    public async Task<List<ReleaseSearchResult>> ManualAsync(int? eventId = null)
    {
        using var response = await Client.PostAsJsonAsync($"/api/event/{eventId ?? Event.Id}/search", new { });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("results").Deserialize<List<ReleaseSearchResult>>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    public async Task RetentionAsync(int days)
    {
        var service = Services.GetRequiredService<ConfigService>();
        var config = await service.GetConfigAsync();
        config.IndexerRetention = days;
        await service.SaveConfigAsync(config);
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
        public string Title { get; set; } = "";
        public string? Resolution { get; set; } = "1080p";
        public DateTime PublishDate { get; set; } = DateTime.UtcNow.AddDays(-30);
        public bool PlainRss { get; set; }
        public SourceTransport(string host) => Host = host;
        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host != Host || uri.AbsolutePath != "/")
            {
                if (uri.AbsolutePath.StartsWith("/payload/", StringComparison.Ordinal)) DescriptorAttempts++;
                Unexpected.Add(uri.GetLeftPart(UriPartial.Path));
                throw new InvalidOperationException("Unexpected source-quality test HTTP destination.");
            }
            if (PlainRss)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Searches.Add(new Dictionary<string, string> { ["q"] = "rss" });
                var xml = new XElement("rss", new XElement("channel", new XElement("item",
                    new XElement("title", Title), new XElement("guid", "rss-one"),
                    new XElement("pubDate", PublishDate.ToString("r", System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement("enclosure", new XAttribute("url", "http://" + Host + "/payload/rss-one"),
                        new XAttribute("length", 1_073_741_824), new XAttribute("type", "application/x-bittorrent")))));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json-rpc", request.Content!.Headers.ContentType!.ToString());
            using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal("getTorrents", document.RootElement.GetProperty("method").GetString());
            var query = document.RootElement.GetProperty("params")[1].GetProperty("Search").GetString()!;
            Searches.Add(new Dictionary<string, string> { ["q"] = query });
            var id = query.Contains("fresh", StringComparison.OrdinalIgnoreCase) ? "102" : "101";
            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "1",
                result = new { Results = 1, Torrents = new Dictionary<string, object> { [id] = new {
                    TorrentID = int.Parse(id), GroupID = 501, ReleaseName = Title, Resolution,
                    Source = "WEB-DL", Codec = "H.264", Size = 1_073_741_824L, Seeders = 20,
                    Time = new DateTimeOffset(PublishDate).ToUnixTimeSeconds(),
                    DownloadURL = "http://" + Host + "/payload/" + id } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
