using Microsoft.EntityFrameworkCore;
using FluentAssertions;

namespace Sportarr.Api.Tests.Services;

public class PartIdentityImportBoundaryTests
{
    [Theory]
    [InlineData("UFC.9999.Complete.Event.720p.WEB-DL", "UFC.9999.Main.Card.2020.09.01.720p.WEB-DL.mkv")]
    [InlineData("UFC.9999.Complete.Event.720p.WEB-DL", "UFC.9999.PPV.2020.09.01.720p.WEB-DL.mkv")]
    public async Task CompleteEventLabel_CannotBecomePackPermissionForPartialInference(string title, string basename)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        await rig.GrabAsync(rig.Release(title));
        var acquired = await rig.Db.DownloadQueue.SingleAsync();
        acquired.Part.Should().Be("Full Event");
        (await rig.Db.GrabHistory.SingleAsync()).PartName.Should().Be("Full Event");
        var file = await rig.ImportAsync(title, basename, acquiredQueue: acquired);
        file.PartName.Should().BeNull(); file.PartNumber.Should().BeNull();
    }

    [Fact]
    public async Task RecoveredPackContext_StillPreservesStoredChildSelection()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var file = await rig.ImportAsync("UFC.2020.Season.Pack.Main.Card", "UFC.9999.2020.09.01.Prelims.720p.WEB-DL.mkv", "Main Card");
        file.PartName.Should().Be("Main Card"); file.PartNumber.Should().Be(3);
    }

    [Fact]
    public async Task CompleteSeasonPack_UsesTheChildRatherThanTheParentPart()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var file = await rig.ImportAsync("UFC.2020.Complete.Season.Pack.Main.Card", "UFC.9999.2020.09.01.Prelims.720p.WEB-DL.mkv");
        file.PartName.Should().Be("Prelims"); file.PartNumber.Should().Be(2);
    }
    [Theory]
    [InlineData("Wrestling", "WrestleMania 40", "WWE", "PPV", "Main Show", 2)]
    [InlineData("Combat", "ONE 170", "ONE Championship", "Main.Card.Prelims", "Prelims", 1)]
    public async Task LegacyLabel_UsesCurrentPromotionNameAndNumber(string sport, string eventTitle, string league,
        string labels, string expected, int number)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync(title: eventTitle, sport: sport, leagueName: league);
        var title = $"{league}.{labels}.2020.09.01.720p.WEB-DL";
        var file = await rig.ImportAsync(title, title + ".mkv");
        file.PartName.Should().Be(expected); file.PartNumber.Should().Be(number);
        Path.GetFileName(file.FilePath).Should().Contain("pt" + number);
    }

}
