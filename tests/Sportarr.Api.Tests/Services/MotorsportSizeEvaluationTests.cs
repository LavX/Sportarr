using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class MotorsportSizeEvaluationTests
{
    private readonly ReleaseEvaluator _evaluator = new(
        NullLogger<ReleaseEvaluator>.Instance,
        new EventPartDetector(NullLogger<EventPartDetector>.Instance),
        new CustomFormatMatchCache(NullLogger<CustomFormatMatchCache>.Instance));

    [Theory]
    [InlineData("MotoGP.2026.Aragon.Qualifying.Two.720p.WEB.H.264-JFF", 805306368)]
    [InlineData("MotoGP.2026.Aragon.Qualifying.Two.WEB.H264-RBB", 311385129)]
    [InlineData("MotoGP.2026.Aragon.Qualifying.Two.1080p.WEB.h264-BILLIE", 1352914698)]
    [InlineData("MotoGP.2026.Aragon.Qualifying.Two.720p.WEB.H264-JFF", 687194767)]
    [InlineData("MotoGP.2026.Aragon.Qualifying.Two.1080p.WEB.H.264-BILLIE", 1589137899)]
    public void QualifyingSizeFloorKeepsObservedReleases(string title, long size)
    {
        var release = Release(title, size);

        var result = _evaluator.EvaluateRelease(
            release,
            Profile(),
            qualityDefinitions: Definitions(),
            sport: "Motorsport",
            eventTitle: "Aragon Qualifying 2",
            runtimeMinutes: 180);

        result.Rejections.Should().NotContain(x => x.StartsWith("Size "));
        result.Approved.Should().BeTrue();
    }

    [Theory]
    [InlineData("Motorsport", "Aragon Qualifying 2", 524288000)]
    [InlineData("Motorsport", "Aragon Race", 1352914698)]
    [InlineData("Alpine Skiing", "World Cup Qualifying", 1352914698)]
    public void SizeFloorStillRejectsUndersizedOrNonSessionReleases(string sport, string eventTitle, long size)
    {
        var result = _evaluator.EvaluateRelease(
            Release("MotoGP.2026.Aragon.1080p.WEB.h264-BILLIE", size),
            Profile(),
            qualityDefinitions: Definitions(),
            sport: sport,
            eventTitle: eventTitle,
            runtimeMinutes: 180);

        result.Rejections.Should().ContainSingle(x => x.StartsWith("Size "));
        result.Approved.Should().BeFalse();
    }

    [Fact]
    public void QualifyingFloorDoesNotLowerTheConfiguredSizeCeiling()
    {
        var definitions = Definitions();
        var definition = definitions.Single(x => x.Title == "WEBDL-1080p");
        definition.MinSize = 0;
        definition.MaxSize = 10;

        var result = _evaluator.EvaluateRelease(
            Release("MotoGP.2026.Aragon.Qualifying.Two.1080p.WEB.h264-BILLIE", 1073741824),
            Profile(),
            qualityDefinitions: definitions,
            sport: "Motorsport",
            eventTitle: "Aragon Qualifying 2",
            runtimeMinutes: 180);

        result.Rejections.Should().NotContain(x => x.StartsWith("Size "));
        result.Approved.Should().BeTrue();
    }

    [Theory]
    [InlineData("Aragon Q1")]
    [InlineData("Aragon Q2")]
    [InlineData("Aragon Sprint Qualifying")]
    public void QualifyingAliasesUseTheShorterSizeFloor(string eventTitle)
    {
        var result = _evaluator.EvaluateRelease(
            Release("MotoGP.2026.Aragon.1080p.WEB.h264-BILLIE", 1352914698),
            Profile(),
            qualityDefinitions: Definitions(),
            sport: "Motorsport",
            eventTitle: eventTitle,
            runtimeMinutes: 180);

        result.Rejections.Should().NotContain(x => x.StartsWith("Size "));
        result.Approved.Should().BeTrue();
    }

    [Fact]
    public void ConfiguredRuntimeBelowQualifyingCapRemainsUnchanged()
    {
        var result = _evaluator.EvaluateRelease(
            Release("MotoGP.2026.Aragon.1080p.WEB.h264-BILLIE", 524288000),
            Profile(),
            qualityDefinitions: Definitions(),
            sport: "Motorsport",
            eventTitle: "Aragon Q2",
            runtimeMinutes: 30);

        result.Rejections.Should().NotContain(x => x.StartsWith("Size "));
        result.Approved.Should().BeTrue();
    }

    [Fact]
    public void QualifyingFloorDoesNotChangePreferredSizeScore()
    {
        var definitions = Definitions();
        var definition = definitions.Single(x => x.Title == "WEBDL-1080p");
        definition.MinSize = 0;
        definition.PreferredSize = 10;
        const long size = 1887436800;

        var qualifying = _evaluator.EvaluateRelease(
            Release("MotoGP.2026.Aragon.Qualifying.1080p.WEB.h264-BILLIE", size),
            Profile(),
            qualityDefinitions: definitions,
            sport: "Motorsport",
            eventTitle: "Aragon Q2",
            runtimeMinutes: 180);
        var race = _evaluator.EvaluateRelease(
            Release("MotoGP.2026.Aragon.Race.1080p.WEB.h264-BILLIE", size),
            Profile(),
            qualityDefinitions: definitions,
            sport: "Motorsport",
            eventTitle: "Aragon Race",
            runtimeMinutes: 180);

        qualifying.SizeScore.Should().Be(0);
        qualifying.SizeScore.Should().Be(race.SizeScore);
    }

    [Fact]
    public async Task PushedQualifyingReleaseUsesQualityDefinitionSizeFloor()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync(
            title: "Aragon Qualifying 2", sport: "Motorsport", leagueName: "MotoGP", relational: true);
        rig.Event.ExternalId = "ev-2561902";
        rig.Event.Round = "13";
        var profile = await rig.Db.QualityProfiles.SingleAsync(x => x.Id == rig.Event.QualityProfileId);
        profile.Items = new List<QualityItem>
        {
            new() { Name = "WEBDL-1080p", Quality = 15, Allowed = true }
        };
        var definition = await rig.Db.QualityDefinitions.SingleAsync(x => x.Title == "WEBDL-1080p");
        definition.MinSize = 15;
        definition.MaxSize = 1000;
        definition.PreferredSize = 995;
        await rig.Db.SaveChangesAsync();

        var release = Release("MotoGP.2020.Aragon.Qualifying.Two.1080p.WEB.h264-BILLIE", 524288000);
        release.SportarrEventId = rig.Event.ExternalId;
        release.Indexer = "Part fixture";
        release.IndexerId = await rig.Db.Indexers.Select(x => x.Id).SingleAsync();
        release.Protocol = "Usenet";
        release.PublishDate = new DateTime(2020, 9, 2, 0, 0, 0, DateTimeKind.Utc);

        var outcome = await rig.Services.GetRequiredService<RssSyncService>()
            .ProcessPushedReleaseAsync(release, CancellationToken.None);

        outcome.Grabbed.Should().BeFalse();
        outcome.Rejections.Should().ContainSingle(x => x.StartsWith("Size "));
        rig.Transport.ClientAdds.Should().Be(0);
        (await rig.Db.DownloadQueue.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PushedQualifyingReleaseUsesTheShorterSizeFloor()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync(
            title: "Aragon Q2", sport: "Motorsport", leagueName: "MotoGP", relational: true);
        rig.Event.ExternalId = "ev-2561902";
        rig.Event.Round = "13";
        var profile = await rig.Db.QualityProfiles.SingleAsync(x => x.Id == rig.Event.QualityProfileId);
        profile.Items = new List<QualityItem>
        {
            new() { Name = "WEBDL-1080p", Quality = 15, Allowed = true }
        };
        var definition = await rig.Db.QualityDefinitions.SingleAsync(x => x.Title == "WEBDL-1080p");
        definition.MinSize = 15;
        definition.MaxSize = 1000;
        definition.PreferredSize = 995;
        await rig.Db.SaveChangesAsync();

        var release = Release("MotoGP.2020.Aragon.Q2.1080p.WEB.h264-BILLIE", 1352914698);
        release.SportarrEventId = rig.Event.ExternalId;
        release.Indexer = "Part fixture";
        release.IndexerId = await rig.Db.Indexers.Select(x => x.Id).SingleAsync();
        release.Protocol = "Usenet";
        release.PublishDate = new DateTime(2020, 9, 2, 0, 0, 0, DateTimeKind.Utc);

        var outcome = await rig.Services.GetRequiredService<RssSyncService>()
            .ProcessPushedReleaseAsync(release, CancellationToken.None);

        outcome.Grabbed.Should().BeTrue();
        outcome.Rejections.Should().BeEmpty();
        rig.Transport.ClientAdds.Should().Be(1);
        (await rig.Db.DownloadQueue.SingleAsync()).EventId.Should().Be(rig.Event.Id);
    }

    private static ReleaseSearchResult Release(string title, long size) => new()
    {
        Title = title,
        Guid = "motogp-size-fixture",
        DownloadUrl = "http://part-source.invalid/motogp.nzb",
        Indexer = "Fixture",
        Protocol = "Usenet",
        Size = size,
        Seeders = 20,
        PublishDate = DateTime.UtcNow.AddHours(-1)
    };

    private static QualityProfile Profile() => new()
    {
        Name = "Motorsport size fixture",
        Items = new List<QualityItem>
        {
            new() { Name = "WEBDL-480p", Quality = 2, Allowed = true },
            new() { Name = "WEBDL-720p", Quality = 3, Allowed = true },
            new() { Name = "WEBDL-1080p", Quality = 15, Allowed = true }
        }
    };

    private static List<QualityDefinition> Definitions() =>
    [
        new() { Quality = 2, Title = "WEBDL-480p", MinSize = 2, MaxSize = 1000, PreferredSize = 95 },
        new() { Quality = 3, Title = "WEBDL-720p", MinSize = 10, MaxSize = 1000, PreferredSize = 995 },
        new() { Quality = 15, Title = "WEBDL-1080p", MinSize = 15, MaxSize = 1000, PreferredSize = 995 }
    ];
}
