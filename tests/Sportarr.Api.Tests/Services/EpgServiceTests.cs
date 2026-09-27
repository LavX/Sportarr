using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using Sportarr.Api.Services.Interfaces;
using System.Net;
using System.Text;

namespace Sportarr.Api.Tests.Services;

/// <summary>
/// Covers EpgService.DeleteSourceAsync's cleanup of stale IptvChannel.TvgId references.
/// Regression guard for "removing and re-adding an EPG source leaves channels stuck
/// showing Mapped, and auto-map refuses to touch them since it only maps channels with
/// a null TvgId" (issue #80).
///
/// Uses a real SQLite ":memory:" connection rather than EF's InMemory provider: the fix
/// relies on ExecuteUpdateAsync/ExecuteDeleteAsync (kept as real bulk operations here
/// since EpgPrograms/EpgChannels can run into the thousands of rows for a busy EPG feed),
/// which the InMemory provider doesn't support at all.
/// </summary>
public class EpgServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public EpgServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private static EpgService CreateService(
        SportarrDbContext db,
        INotificationService? notificationService = null,
        string? xml = null)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(
            xml == null
                ? new HttpClient()
                : new HttpClient(new XmlBodyHandler(xml)));
        var xmltvParser = new XmltvParserService(
            NullLogger<XmltvParserService>.Instance,
            httpClientFactory.Object,
            new ConfigService(new ConfigurationBuilder().Build(), NullLogger<ConfigService>.Instance));
        notificationService ??= Mock.Of<INotificationService>();
        return new EpgService(NullLogger<EpgService>.Instance, db, xmltvParser, notificationService);
    }

    private sealed class XmlBodyHandler(string xml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });
        }
    }

    private SportarrDbContext CreateDb()
    {
        var db = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task DeleteSourceAsync_ClearsStaleTvgIdOnMappedChannels()
    {
        await using var db = CreateDb();

        var source = new EpgSource { Name = "Test EPG", Url = "http://example.com/epg.xml" };
        db.EpgSources.Add(source);
        await db.SaveChangesAsync();

        var epgChannel = new EpgChannel { EpgSourceId = source.Id, ChannelId = "espn.us", DisplayName = "ESPN" };
        db.EpgChannels.Add(epgChannel);

        var iptvSource = new IptvSource { Name = "Test M3U", Url = "http://example.com/playlist.m3u" };
        db.IptvSources.Add(iptvSource);
        await db.SaveChangesAsync();

        var mappedChannel = new IptvChannel { SourceId = iptvSource.Id, Name = "ESPN HD", StreamUrl = "http://x/1", TvgId = "espn.us" };
        var unrelatedChannel = new IptvChannel { SourceId = iptvSource.Id, Name = "Fox Sports", StreamUrl = "http://x/2", TvgId = "fox.us" };
        db.IptvChannels.AddRange(mappedChannel, unrelatedChannel);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DeleteSourceAsync(source.Id);

        result.Should().BeTrue();

        // Fresh context for verification: ExecuteUpdateAsync writes straight to the
        // database and bypasses db's change tracker, so re-querying on the SAME
        // context would return the stale tracked copy of mappedChannel via identity
        // resolution rather than what's actually in the database now. A real request
        // would use a fresh scoped DbContext to read this back anyway.
        await using var verifyDb = CreateDb();

        var reloadedMapped = await verifyDb.IptvChannels.FirstAsync(c => c.Id == mappedChannel.Id);
        reloadedMapped.TvgId.Should().BeNull(
            because: "the EPG channel it pointed at no longer exists once its source is deleted, " +
                     "so the stale id must be cleared or the channel can never be auto-mapped again");

        var reloadedUnrelated = await verifyDb.IptvChannels.FirstAsync(c => c.Id == unrelatedChannel.Id);
        reloadedUnrelated.TvgId.Should().Be("fox.us", because: "channels mapped to a different source's EPG must be left alone");
    }

    [Fact]
    public async Task DeleteSourceAsync_RemovesOrphanedEpgChannels()
    {
        await using var db = CreateDb();

        var source = new EpgSource { Name = "Test EPG", Url = "http://example.com/epg.xml" };
        db.EpgSources.Add(source);
        await db.SaveChangesAsync();

        db.EpgChannels.Add(new EpgChannel { EpgSourceId = source.Id, ChannelId = "espn.us", DisplayName = "ESPN" });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DeleteSourceAsync(source.Id);

        (await db.EpgChannels.AnyAsync(c => c.EpgSourceId == source.Id)).Should().BeFalse(
            because: "leftover EpgChannel rows for a deleted source would otherwise sit in the database forever");
    }

    [Fact]
    public async Task DeleteSourceAsync_UnknownId_ReturnsFalse()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        (await service.DeleteSourceAsync(999)).Should().BeFalse();
    }

    [Fact]
    public async Task SyncSourceAsync_NotifiesOnceWithFinalCountsAfterAutoMapping()
    {
        await using var db = CreateDb();
        var source = new EpgSource { Name = "Sports Guide", Url = "http://example.com/epg.xml" };
        var iptvSource = new IptvSource { Name = "Sports Streams", Url = "http://example.com/playlist.m3u" };
        db.AddRange(source, iptvSource);
        await db.SaveChangesAsync();
        db.IptvChannels.Add(new IptvChannel
        {
            SourceId = iptvSource.Id,
            Name = "ESPN HD",
            StreamUrl = "http://example.com/espn"
        });
        await db.SaveChangesAsync();

        NotificationEventData? sentData = null;
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(n => n.SendNotificationAsync(
                NotificationTrigger.OnEpgSyncCompleted,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<NotificationEventData>(),
                null))
            .Callback<NotificationTrigger, string, string, NotificationEventData?, List<int>?>(
                (_, _, _, data, _) => sentData = data)
            .ReturnsAsync(true);

        const string xml = "<tv>" +
            "<channel id=\"espn.us\"><display-name>ESPN</display-name></channel>" +
            "<programme start=\"20990101000000 +0000\" stop=\"20990101010000 +0000\" channel=\"espn.us\"><title>Live Sports</title></programme>" +
            "</tv>";
        var service = CreateService(db, notifications.Object, xml);

        var result = await service.SyncSourceAsync(source.Id);

        result.Success.Should().BeTrue(result.Error);
        result.ChannelCount.Should().Be(1);
        result.ProgramCount.Should().Be(1);
        result.MappedChannelCount.Should().Be(1);
        sentData.Should().NotBeNull();
        sentData!.EpgSourceId.Should().Be(source.Id);
        sentData.EpgSourceName.Should().Be("Sports Guide");
        sentData.ChannelCount.Should().Be(1);
        sentData.ProgramCount.Should().Be(1);
        sentData.AutoMappedChannelCount.Should().Be(1);
        sentData.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        (await db.IptvChannels.SingleAsync()).TvgId.Should().Be("espn.us");
        notifications.Verify(n => n.SendNotificationAsync(
            NotificationTrigger.OnEpgSyncCompleted,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<NotificationEventData>(),
            null), Times.Once);
    }

    [Fact]
    public async Task SyncSourceAsync_StaysSuccessfulWhenCompletionNotificationFails()
    {
        await using var db = CreateDb();
        var source = new EpgSource { Name = "Sports Guide", Url = "http://example.com/epg.xml" };
        db.EpgSources.Add(source);
        await db.SaveChangesAsync();

        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(n => n.SendNotificationAsync(
                NotificationTrigger.OnEpgSyncCompleted,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<NotificationEventData>(),
                null))
            .ThrowsAsync(new InvalidOperationException("notification unavailable"));

        const string xml = "<tv><channel id=\"espn.us\"><display-name>ESPN</display-name></channel></tv>";
        var service = CreateService(db, notifications.Object, xml);

        var result = await service.SyncSourceAsync(source.Id);

        result.Success.Should().BeTrue(result.Error);
        result.ChannelCount.Should().Be(1);
        notifications.VerifyAll();
    }

    [Fact]
    public async Task SyncSourceAsync_DoesNotNotifyWhenParsingFails()
    {
        await using var db = CreateDb();
        var source = new EpgSource { Name = "Broken Guide", Url = "http://example.com/epg.xml" };
        db.EpgSources.Add(source);
        await db.SaveChangesAsync();

        var notifications = new Mock<INotificationService>();
        var service = CreateService(db, notifications.Object, "<guide />");

        var result = await service.SyncSourceAsync(source.Id);

        result.Success.Should().BeFalse();
        notifications.Verify(n => n.SendNotificationAsync(
            NotificationTrigger.OnEpgSyncCompleted,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<NotificationEventData>(),
            null), Times.Never);
    }
}
