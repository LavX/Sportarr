using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class PartIdentityLegacyRegressionTests
{
    public static IEnumerable<object[]> LegacyCases()
    {
        yield return new object[] { "UFC 9999", "UFC.9999", "Main.Card.Prelims", "Prelims", 2 };
        yield return new object[] { "UFC 9999", "UFC.9999", "Prelims.Main.Card", "Main Card", 3 };
        yield return new object[] { "UFC 9999", "UFC.9999", "Early.Prelims.Main.Card", "Early Prelims", 1 };
        yield return new object[] { "UFC 9999", "UFC.9999", "PPV", "Main Card", 3 };
        yield return new object[] { "UFC 9999", "UFC.9999", "MC", "Main Card", 3 };
        yield return new object[] { "UFC 9999", "UFC.9999", "EP", "Early Prelims", 1 };
        yield return new object[] { "UFC 9999", "UFC.9999", "Countdown.Main.Card", "Main Card", 3 };
        yield return new object[] { "UFC Fight Night 280", "UFC.Fight.Night", "Early.Prelims.Main.Card", "Main Card", 2 };
    }

    public static IEnumerable<object[]> LegacyImports() => LegacyCases().SelectMany(c =>
        new[] { c.Concat(new object[] { true }).ToArray(), c.Concat(new object[] { false }).ToArray() });

    [Theory]
    [MemberData(nameof(LegacyCases))]
    public void LegacyParserAndDetector_ExposeTheActualBeforeDateContract(string eventTitle, string prefix,
        string labels, string expected, int number)
    {
        var parser = new MediaFileParser(NullLogger<MediaFileParser>.Instance);
        var detector = new EventPartDetector(NullLogger<EventPartDetector>.Instance);
        var parsed = parser.Parse($"{prefix}.{labels}.2020.09.01.720p.WEB-DL.mkv");
        parsed.EventTitle.Should().Be($"{prefix.Replace('.', ' ')} {labels.Replace('.', ' ')} 2020 09 01");
        var legacy = detector.DetectPart(parsed.EventTitle, "Fighting", eventTitle);
        legacy.Should().NotBeNull(); legacy!.SegmentName.Should().Be(expected); legacy.PartNumber.Should().Be(number);
    }

    [Theory]
    [MemberData(nameof(LegacyImports))]
    public async Task Import_PreservesPreviouslyRecognizedPartialSlots(string eventTitle, string prefix,
        string labels, string expected, int number, bool rename)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync(rename, title: eventTitle);
        rig.Event.MonitoredParts = eventTitle.Contains("Fight Night") ? "Prelims,Main Card" : "Early Prelims,Prelims,Main Card";
        await rig.Db.SaveChangesAsync();
        var title = $"{prefix}.{labels}.2020.09.01.720p.WEB-DL";
        var file = await rig.ImportAsync(title, title + ".mkv");
        file.PartName.Should().Be(expected); file.PartNumber.Should().Be(number);
        if (rename) Path.GetFileName(file.FilePath).Should().Contain("pt" + number).And.Contain(expected);
        else Path.GetFileName(file.FilePath).Should().Be(title + ".mkv");
        File.Exists(file.FilePath).Should().BeTrue();
        (await File.ReadAllBytesAsync(file.FilePath)).Should().Equal(Enumerable.Repeat((byte)'x', 4096));
        rig.Event.HasFile.Should().BeFalse("a legacy partial file must not acquire full coverage");
    }

    [Theory]
    [InlineData("PPV", null, null)]
    [InlineData("MC", null, null)]
    [InlineData("EP", null, null)]
    [InlineData("Main.Card.Prelims", null, null)]
    [InlineData("Main.Card", "Main Card", 3)]
    public async Task AfterDateControls_DoNotIntroduceRawWeakInference(string label, string? expected, int? number)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var title = $"UFC.9999.2020.09.01.{label}.720p.WEB-DL";
        var file = await rig.ImportAsync(title, title + ".mkv");
        file.PartName.Should().Be(expected); file.PartNumber.Should().Be(number);
    }

    [Theory]
    [InlineData("Main Card", "Main Card", 3)]
    [InlineData("Prelims", "Prelims", 2)]
    [InlineData("Full Event", null, null)]
    [InlineData("Unknown selected part", null, null)]
    public async Task StoredValue_RemainsAuthoritativeOverLegacyAlias(string stored, string? expected, int? number)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        const string title = "UFC.9999.EP.2020.09.01.720p.WEB-DL";
        var file = await rig.ImportAsync(title, title + ".mkv", stored);
        file.PartName.Should().Be(expected); file.PartNumber.Should().Be(number);
    }

    [Theory]
    [InlineData("UFC 9999", "Fighting", "UFC", "Full.Event.Main.Card")]
    [InlineData("UFC Fight Night 280", "Fighting", "UFC", "Early.Prelims")]
    [InlineData("WWE Monday Night Raw", "Wrestling", "WWE", "PPV")]
    [InlineData("UFC Contender Series", "Fighting", "UFC", "PPV")]
    [InlineData("ONE Friday Fights 145", "Fighting", "ONE Championship", "PPV")]
    [InlineData("Fixture Grand Prix Qualifying", "Motorsport", "Formula 1", "PPV")]
    public async Task LegacyFallback_CannotCrossContextOrCompleteLabelBoundaries(string eventTitle, string sport,
        string league, string label)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync(title: eventTitle, sport: sport, leagueName: league);
        var title = $"UFC.9999.{label}.2020.09.01.720p.WEB-DL";
        var file = await rig.ImportAsync(title, title + ".mkv");
        file.PartName.Should().BeNull(); file.PartNumber.Should().BeNull();
    }

    [Fact]
    public async Task LegacyPart_DoesNotReplaceAnotherPartOrSuppressRemainingSearch()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        rig.Event.MonitoredParts = "Early Prelims,Prelims,Main Card";
        await rig.Db.SaveChangesAsync();
        var main = await rig.ImportAsync("UFC.9999.Main.Card.720p.WEB-DL", "held-main.720p.WEB-DL.mkv", "Main Card");
        await File.WriteAllBytesAsync(main.FilePath, new byte[] { 7, 9, 11 });
        var prelims = await rig.ImportAsync("UFC.9999.Main.Card.Prelims.2020.09.01.720p.WEB-DL",
            "UFC.9999.Main.Card.Prelims.2020.09.01.720p.WEB-DL.mkv");
        prelims.PartNumber.Should().Be(2);
        (await File.ReadAllBytesAsync(main.FilePath)).Should().Equal(new byte[] { 7, 9, 11 });
        (await rig.Db.EventFiles.CountAsync()).Should().Be(2); rig.Event.HasFile.Should().BeFalse();
        var result = await rig.AutomaticAsync(rig.Release("UFC.9999.2020.09.01.Early.Prelims.720p.WEB-DL", "Early Prelims", suffix: "missing-early"), "Early Prelims");
        result.Success.Should().BeTrue(result.Message); rig.Transport.ClientAdds.Should().Be(1);
    }

    [Fact]
    public async Task PackLegacyFallback_UsesOnlyTheChildParsedBasename()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var file = await rig.ImportAsync("UFC.2020.Season.Pack.Main.Card", "UFC.9999.EP.2020.09.01.720p.WEB-DL.mkv", isPack: true);
        file.PartName.Should().Be("Early Prelims"); file.PartNumber.Should().Be(1);
    }
}
