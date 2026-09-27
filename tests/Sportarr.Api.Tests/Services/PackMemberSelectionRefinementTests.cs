using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class PackMemberSelectionRefinementTests
{
    [Fact]
    public async Task UnrelatedContradictoryMemberDoesNotBlockTheUniqueLargerOwnedMember()
    {
        await using var rig = await PackRig.CreateAsync();
        await rig.MemberAsync(0, 8192);
        await rig.MemberAsync(1, 4096, tokens: "{sportarr-ev-9900302}.{sportarr-ev-9900303}");
        Assert.NotNull(await rig.ImportAsync(0)); await rig.AssertOwnedBytesAsync(0);
        Assert.Null(await rig.ImportAsync(1)); Assert.Null(await rig.ImportAsync(2));
        await rig.AssertHeldAsync(1); await rig.AssertHeldAsync(2); await rig.AssertOwnedBytesAsync(0);
    }

    [Fact]
    public async Task NonLatinParticipantTokensSelectTheUniqueDatedPair()
    {
        await using var rig = await PackRig.CreateAsync();
        rig.Events[0].HomeTeamName = "東京"; rig.Events[0].AwayTeamName = "京都"; rig.Events[0].Title = "東京 vs 京都";
        rig.Events[1].HomeTeamName = "大阪"; rig.Events[1].AwayTeamName = "神戸"; rig.Events[1].Title = "大阪 vs 神戸";
        rig.Events[1].EventDate = rig.Events[0].EventDate; await rig.Db.SaveChangesAsync();
        await rig.MemberAsync(0, 8192, tokens: ""); await rig.MemberAsync(1, 4096, tokens: "");
        Assert.NotNull(await rig.ImportAsync(0)); await rig.AssertOwnedBytesAsync(0);
    }

    [Fact]
    public async Task CanonicallyEquivalentAccentsMatchWithoutDiscardingTheirLetters()
    {
        await using var rig = await PackRig.CreateAsync();
        rig.Events[0].HomeTeamName = "Café United"; rig.Events[0].AwayTeamName = "São Paulo";
        rig.Events[0].Title = "Cafe\u0301 United vs Sa\u0303o Paulo"; await rig.Db.SaveChangesAsync();
        await rig.MemberAsync(0, 8192, tokens: "");
        Assert.NotNull(await rig.ImportAsync(0)); await rig.AssertOwnedBytesAsync(0);
    }

    [Theory]
    [InlineData("東京", "京都", "大阪 vs 神戸")]
    [InlineData("Café United", "São Paulo", "Cafe United vs Sao Paulo")]
    public async Task DistinctUnicodeOrUnaccentedTextCannotClaimTheDatedOwner(string home, string away, string title)
    {
        await using var rig = await PackRig.CreateAsync();
        rig.Events[0].HomeTeamName = home; rig.Events[0].AwayTeamName = away; rig.Events[0].Title = title;
        await rig.Db.SaveChangesAsync(); await rig.MemberAsync(0, 4096, tokens: "");
        Assert.Null(await rig.ImportAsync(0)); await rig.AssertHeldAsync(0);
        Assert.True(File.Exists(rig.Paths[0]));
    }
    private sealed class PackRig : IAsyncDisposable
    {
        private readonly PartIdentityIntegrationHarness _base;
        public Sportarr.Api.Data.SportarrDbContext Db => _base.Db;
        public MediaManagementSettings Settings => _base.Settings;
        public Event[] Events { get; private set; } = null!;
        public DownloadQueueItem[] Owners { get; private set; } = null!;
        public string Folder { get; private set; } = null!;
        public Dictionary<int, string> Paths { get; } = new();
        public Dictionary<int, byte[]> Bytes { get; } = new();
        private const string ReleaseTitle = "NFL.2020.Week1.720p.WEB-DL.H264-PackBoundary";
        private PackRig(PartIdentityIntegrationHarness basis) { _base = basis; }

        public static async Task<PackRig> CreateAsync()
        {
            var basis = await PartIdentityIntegrationHarness.CreateAsync(rename: false, multipart: false,
                title: "Team A vs Team B", sport: "American Football", leagueName: "NFL");
            var rig = new PackRig(basis);
            var league = basis.Event.League!;
            rig.Events = new[] { basis.Event,
                new Event { Title = "Team C vs Team D", Sport = "American Football", LeagueId = league.Id, League = league },
                new Event { Title = "Team E vs Team F", Sport = "American Football", LeagueId = league.Id, League = league } };
            for (var i = 0; i < 3; i++)
            {
                var item = rig.Events[i]; item.ExternalId = "ev-990030" + (i + 1);
                item.HomeTeamName = "Team " + (char)('A' + 2 * i); item.AwayTeamName = "Team " + (char)('B' + 2 * i);
                item.EventDate = new DateTime(2020, 9, i + 1, 20, 0, 0, DateTimeKind.Utc);
                item.Season = "2020"; item.SeasonNumber = 2020; item.EpisodeNumber = i + 1;
                item.Monitored = true; item.QualityProfileId = league.QualityProfileId;
                if (i > 0) rig.Db.Events.Add(item);
            }
            await rig.Db.SaveChangesAsync();
            var client = await rig.Db.DownloadClients.SingleAsync();
            var group = Guid.NewGuid();
            rig.Owners = rig.Events.Select(item => new DownloadQueueItem { EventId = item.Id, Event = item,
                DownloadClientId = client.Id, DownloadClient = client, DownloadId = "owned-pack-job",
                Title = ReleaseTitle, IsPack = true, PackGroupId = group, Quality = "WEBDL-720p",
                Protocol = "Usenet", Status = DownloadStatus.Completed, CompletedAt = DateTime.UtcNow }).ToArray();
            rig.Db.DownloadQueue.AddRange(rig.Owners); await rig.Db.SaveChangesAsync();
            var library = (await rig.Db.RootFolders.SingleAsync()).Path;
            rig.Folder = Path.Combine(Path.GetDirectoryName(library)!, "incoming", ReleaseTitle);
            Directory.CreateDirectory(rig.Folder); return rig;
        }

        public async Task MemberAsync(int index, int length, string? tokens = null, string suffix = "")
        {
            var item = Events[index]; tokens ??= "{sportarr-" + item.ExternalId + "}";
            var name = "NFL." + item.EventDate.ToString("yyyy.MM.dd") + "." + item.Title.Replace(' ', '.')
                + ".720p.WEB-DL.H264." + tokens + suffix + ".mkv";
            var path = Path.Combine(Folder, name);
            var bytes = Enumerable.Repeat((byte)(65 + index), length).ToArray();
            await File.WriteAllBytesAsync(path, bytes); Paths[index] = path; Bytes[index] = bytes;
        }

        public Task<ImportHistory> ImportAsync(int index, PostImportMode mode = PostImportMode.Copy) =>
            _base.Services.GetRequiredService<FileImportService>()
                .ImportDownloadAsync(Owners[index], Folder, mode).WaitAsync(TimeSpan.FromSeconds(20));

        public async Task AssertOwnedBytesAsync(int index)
        {
            var file = await Db.EventFiles.SingleAsync(f => f.EventId == Events[index].Id);
            Assert.Equal(Bytes[index], await File.ReadAllBytesAsync(file.FilePath));
            Assert.Equal(DownloadStatus.Imported, Owners[index].Status);
            Assert.True(Events[index].HasFile); Assert.Equal(file.FilePath, Events[index].FilePath);
        }

        public async Task AssertHeldAsync(int index, bool requireWarning = true)
        {
            Assert.NotEqual(DownloadStatus.Imported, Owners[index].Status);
            Assert.False(Events[index].HasFile);
            Assert.False(await Db.EventFiles.AnyAsync(f => f.EventId == Events[index].Id));
            if (requireWarning) Assert.False(string.IsNullOrWhiteSpace(Owners[index].ErrorMessage));
        }

        public ValueTask DisposeAsync() => _base.DisposeAsync();
    }
}
