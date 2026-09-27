using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class PartIdentityContractSupplementalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrelimsDestinationOccupiedByMainCard_RefusesWithoutDeletingEitherFile(bool caseOnly)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var full = await CreateFull(rig);
        rig.Settings.StandardFileFormat = "shared-name";
        await rig.Db.SaveChangesAsync();
        var main = await rig.ImportAsync("UFC.9999.Main.Card.720p.WEB-DL", "main.720p.WEB-DL.mkv", "Main Card");
        Path.GetFileName(main.FilePath).Should().Be("shared-name.mkv");
        var fullBytes = await File.ReadAllBytesAsync(full.FilePath);
        var mainBytes = await File.ReadAllBytesAsync(main.FilePath);
        var fullSize = full.Size;
        rig.Settings.StandardFileFormat = caseOnly ? "SHARED-NAME" : "shared-name";
        await rig.Db.SaveChangesAsync();
        using var incoming = await Incoming.CreateAsync(rig, "Prelims", "collision");

        var imported = await rig.Services.GetRequiredService<FileImportService>()
            .ImportDownloadAsync(incoming.Queue, incoming.Path, PostImportMode.Copy);

        using var assertions = new AssertionScope();
        imported.Should().BeNull();
        incoming.Queue.Status.Should().Be(DownloadStatus.ImportWarning);
        incoming.Queue.ErrorMessage.Should().Contain("destination");
        (await rig.Db.EventFiles.Select(f => f.Id).ToListAsync()).Should().BeEquivalentTo(new[] { full.Id, main.Id });
        (await File.ReadAllBytesAsync(full.FilePath)).Should().Equal(fullBytes);
        (await File.ReadAllBytesAsync(main.FilePath)).Should().Equal(mainBytes);
        (await File.ReadAllBytesAsync(incoming.Path)).Should().Equal(incoming.Bytes);
        if (caseOnly)
        {
            var distinctTarget = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(main.FilePath)!, "SHARED-NAME.mkv");
            Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(main.FilePath)!)
                .Should().NotContain(p => string.Equals(p, distinctTarget, StringComparison.Ordinal));
        }
        AssertFullSummary(rig, full, fullSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePartUpgradeAtItsOwnDestination_PreservesFullFileAndSummary(bool summaryInitiallyPointsAtPart)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var full = await CreateFull(rig);
        rig.Settings.StandardFileFormat = "shared-name";
        await rig.Db.SaveChangesAsync();
        var oldMain = await rig.ImportAsync("UFC.9999.Main.Card.720p.WEB-DL", "main.720p.WEB-DL.mkv", "Main Card");
        var fullBytes = await File.ReadAllBytesAsync(full.FilePath);
        var fullSize = full.Size;
        var oldMainId = oldMain.Id;
        if (summaryInitiallyPointsAtPart)
        {
            rig.Event.FilePath = oldMain.FilePath;
            rig.Event.FileSize = oldMain.Size;
            rig.Event.Quality = oldMain.Quality;
            await rig.Db.SaveChangesAsync();
        }
        using var incoming = await Incoming.CreateAsync(rig, "Main Card", "upgrade");

        var imported = await rig.Services.GetRequiredService<FileImportService>()
            .ImportDownloadAsync(incoming.Queue, incoming.Path, PostImportMode.Copy);

        imported.Should().NotBeNull();
        imported!.Decision.Should().Be(ImportDecision.Approved);
        var newMain = await rig.Db.EventFiles.SingleAsync(f => f.PartNumber == 3);
        using var assertions = new AssertionScope();
        newMain.Id.Should().NotBe(oldMainId);
        newMain.FilePath.Should().Be(oldMain.FilePath);
        newMain.PartName.Should().Be("Main Card");
        newMain.Quality.Should().Be("WEBDL-2160p");
        (await rig.Db.EventFiles.Select(f => f.Id).ToListAsync()).Should().BeEquivalentTo(new[] { full.Id, newMain.Id });
        (await File.ReadAllBytesAsync(newMain.FilePath)).Should().Equal(incoming.Bytes);
        (await File.ReadAllBytesAsync(full.FilePath)).Should().Equal(fullBytes);
        (await File.ReadAllBytesAsync(incoming.Path)).Should().Equal(incoming.Bytes);
        AssertFullSummary(rig, full, fullSize);
    }

    [Fact]
    public async Task CaseOnlyFullFileDestinationCollision_RetainsDeclaredConservativeRefusal()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var full = await CreateFull(rig);
        var fullBytes = await File.ReadAllBytesAsync(full.FilePath);
        rig.Settings.StandardFileFormat = "WHOLE-PRESERVED";
        await rig.Db.SaveChangesAsync();
        using var incoming = await Incoming.CreateAsync(rig, "Prelims", "full-case-collision");

        var imported = await rig.Services.GetRequiredService<FileImportService>()
            .ImportDownloadAsync(incoming.Queue, incoming.Path, PostImportMode.Copy);

        using var assertions = new AssertionScope();
        imported.Should().BeNull();
        incoming.Queue.Status.Should().Be(DownloadStatus.ImportWarning);
        incoming.Queue.ErrorMessage.Should().Contain("destination");
        (await rig.Db.EventFiles.SingleAsync()).Id.Should().Be(full.Id);
        (await File.ReadAllBytesAsync(full.FilePath)).Should().Equal(fullBytes);
        (await File.ReadAllBytesAsync(incoming.Path)).Should().Equal(incoming.Bytes);
        AssertFullSummary(rig, full, full.Size);
    }

    [Fact]
    public async Task DistinctCompleteCardCandidate_IsRefusedByActiveGateRatherThanChurn()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var first = rig.Release("UFC.9999.Complete.Event.720p.WEB-DL", suffix: "first-complete");
        var initial = await rig.AutomaticAsync(first);
        initial.Success.Should().BeTrue(initial.Message);
        (await rig.Db.DownloadQueue.SingleAsync()).Part.Should().Be(EventPartDetector.FullEventSegmentName);
        var second = rig.Release("UFC.9999.Complete.Card.720p.WEB-DL", suffix: "different-complete");
        second.Title.Should().NotBe(first.Title);
        second.Guid.Should().NotBe(first.Guid);
        second.DownloadUrl.Should().NotBe(first.DownloadUrl);

        var result = await rig.AutomaticAsync(second);

        using var assertions = new AssertionScope();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Already downloading");
        rig.Transport.ClientAdds.Should().Be(1);
        (await rig.Db.DownloadQueue.CountAsync()).Should().Be(1);
        (await rig.Db.GrabHistory.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(EventPartDetector.FullEventSegmentName, null)]
    [InlineData("full event", null)]
    [InlineData(null, EventPartDetector.FullEventSegmentName)]
    [InlineData(EventPartDetector.FullEventSegmentName, "")]
    [InlineData(EventPartDetector.FullEventSegmentName, "full event")]
    public async Task ActiveWholeEventSlot_StopsUncachedSearchBeforeSourceRequests(string? storedPart, string? requestedPart)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        await AddSearchSource(rig);
        EventPartDetector.IsFullEvent(storedPart).Should().BeTrue();
        EventPartDetector.IsFullEvent(requestedPart).Should().BeTrue();
        rig.Db.DownloadQueue.Add(ActiveQueue(rig, storedPart));
        await rig.Db.SaveChangesAsync();

        var result = await rig.Services.GetRequiredService<AutomaticSearchService>()
            .SearchAndDownloadEventAsync(rig.Event.Id, null, requestedPart, false);

        using var assertions = new AssertionScope();
        rig.Transport.UnexpectedRequests.Should().BeEmpty("the early gate must run before capability or search HTTP requests");
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Already downloading");
        rig.Transport.ClientAdds.Should().Be(0);
        (await rig.Db.DownloadQueue.CountAsync()).Should().Be(1);
        (await rig.Db.GrabHistory.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncachedWholeEventSearch_ReachesConfiguredSourceWithoutWholeEventInFlight(bool hasMainCardInFlight)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var source = await AddSearchSource(rig);
        if (hasMainCardInFlight)
        {
            rig.Db.DownloadQueue.Add(ActiveQueue(rig, "Main Card"));
            await rig.Db.SaveChangesAsync();
        }

        var result = await rig.Services.GetRequiredService<AutomaticSearchService>()
            .SearchAndDownloadEventAsync(rig.Event.Id, null, null, false);

        using var assertions = new AssertionScope();
        rig.Transport.UnexpectedRequests.Should().Contain(p => p.StartsWith(source.Url, StringComparison.Ordinal));
        result.Success.Should().BeFalse("the local handler deliberately rejects the source request");
        result.Message.Should().NotContain("Already downloading");
        rig.Transport.ClientAdds.Should().Be(0);
        (await rig.Db.DownloadQueue.CountAsync()).Should().Be(hasMainCardInFlight ? 1 : 0);
    }

    [Theory]
    [InlineData("Complete.Event", EventPartDetector.FullEventSegmentName)]
    [InlineData("", null)]
    public async Task ActualWholeEventGrab_SupersedesEquivalentWholeSlotsButPreservesPartHistories(string titleLabel, string? expectedPart)
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var whole = new[] { null, "", EventPartDetector.FullEventSegmentName, "full event" }
            .Select((part, i) => History(rig.Event.Id, part, "old-whole-" + i)).ToArray();
        var parts = new[] { "Main Card", "Prelims" }
            .Select((part, i) => History(rig.Event.Id, part, "old-part-" + i)).ToArray();
        rig.Db.GrabHistory.AddRange(whole.Concat(parts));
        await rig.Db.SaveChangesAsync();
        var title = string.IsNullOrEmpty(titleLabel)
            ? "UFC.9999.720p.WEB-DL"
            : $"UFC.9999.{titleLabel}.720p.WEB-DL";
        var release = rig.Release(title, suffix: "replacement-whole");

        var result = await rig.AutomaticAsync(release);

        result.Success.Should().BeTrue(result.Message);
        using var assertions = new AssertionScope();
        whole.Should().OnlyContain(g => g.Superseded);
        parts.Should().OnlyContain(g => !g.Superseded);
        var replacement = await rig.Db.GrabHistory.SingleAsync(g => g.Guid == release.Guid);
        replacement.PartName.Should().Be(expectedPart);
        replacement.Superseded.Should().BeFalse();
        (await rig.Db.DownloadQueue.SingleAsync()).Part.Should().Be(expectedPart);
        rig.Transport.ClientAdds.Should().Be(1);
        (await rig.Db.GrabHistory.CountAsync()).Should().Be(7);
    }

    [Fact]
    public async Task ActualMainCardGrab_SupersedesOnlyMainCardHistory()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        var main = History(rig.Event.Id, "Main Card", "old-main");
        var untouched = new[] { null, "", EventPartDetector.FullEventSegmentName, "Prelims" }
            .Select((part, i) => History(rig.Event.Id, part, "untouched-" + i)).ToArray();
        rig.Db.GrabHistory.Add(main);
        rig.Db.GrabHistory.AddRange(untouched);
        await rig.Db.SaveChangesAsync();

        var result = await rig.AutomaticAsync(rig.Release("UFC.9999.Main.Card.720p.WEB-DL", suffix: "replacement-main"));

        result.Success.Should().BeTrue(result.Message);
        using var assertions = new AssertionScope();
        main.Superseded.Should().BeTrue();
        untouched.Should().OnlyContain(g => !g.Superseded);
        (await rig.Db.DownloadQueue.SingleAsync()).Part.Should().Be("Main Card");
        rig.Transport.ClientAdds.Should().Be(1);
        (await rig.Db.GrabHistory.CountAsync()).Should().Be(6);
    }

    private static async Task<EventFile> CreateFull(PartIdentityIntegrationHarness rig)
    {
        rig.Settings.StandardFileFormat = "whole-preserved";
        await rig.Db.SaveChangesAsync();
        var full = await rig.ImportAsync("UFC.9999.Full.Event", "full.1080p.WEB-DL.mkv", EventPartDetector.FullEventSegmentName);
        full.PartName = null;
        full.PartNumber = null;
        full.Quality = "WEBDL-1080p";
        rig.Event.Quality = full.Quality;
        await rig.Db.SaveChangesAsync();
        Path.GetFileName(full.FilePath).Should().Be("whole-preserved.mkv");
        return full;
    }

    private static void AssertFullSummary(PartIdentityIntegrationHarness rig, EventFile full, long size)
    {
        rig.Event.FilePath.Should().Be(full.FilePath);
        rig.Event.FileSize.Should().Be(size);
        rig.Event.Quality.Should().Be("WEBDL-1080p");
        rig.Event.HasFile.Should().BeTrue();
        full.Exists.Should().BeTrue();
        full.Quality.Should().Be("WEBDL-1080p");
    }

    private static DownloadQueueItem ActiveQueue(PartIdentityIntegrationHarness rig, string? part) => new()
    {
        EventId = rig.Event.Id, Title = "UFC.9999.Existing.720p.WEB-DL", Part = part,
        Quality = "WEBDL-720p", Protocol = "Usenet", DownloadId = "existing-active",
        Status = DownloadStatus.Downloading, LastUpdate = DateTime.UtcNow
    };

    private static async Task<Indexer> AddSearchSource(PartIdentityIntegrationHarness rig)
    {
        // Each URL isolates the static capability cache from other tests.
        var indexer = new Indexer { Name = "Source-attempt control", Type = IndexerType.Newznab,
            Url = "http://part-gate-" + Guid.NewGuid().ToString("N") + ".invalid", ApiKey = "fixture",
            Enabled = true, EnableAutomaticSearch = true, RequestDelayMs = 0 };
        rig.Db.Indexers.Add(indexer);
        await rig.Db.SaveChangesAsync();
        return indexer;
    }

    private static GrabHistory History(int eventId, string? part, string identity) => new()
    {
        EventId = eventId, Title = "UFC.9999." + identity, Indexer = "Part fixture",
        DownloadUrl = "http://part-source.invalid/" + identity + ".nzb", Guid = identity,
        Protocol = "Usenet", PartName = part, GrabbedAt = DateTime.UtcNow.AddDays(-2), WasImported = true
    };

    private sealed class Incoming : IDisposable
    {
        private readonly string _directory;
        public string Path { get; }
        public byte[] Bytes { get; } = Enumerable.Repeat((byte)'p', 8192).ToArray();
        public DownloadQueueItem Queue { get; }

        private Incoming(int eventId, string part, string identity)
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sportarr-part-review-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "incoming.2160p.WEB-DL.mkv");
            Queue = new DownloadQueueItem { EventId = eventId, Title = "UFC.9999." + part.Replace(' ', '.') + ".2160p.WEB-DL",
                Part = part, Quality = "WEBDL-2160p", Protocol = "Usenet", DownloadId = identity, Status = DownloadStatus.Completed };
        }

        public static async Task<Incoming> CreateAsync(PartIdentityIntegrationHarness rig, string part, string identity)
        {
            var incoming = new Incoming(rig.Event.Id, part, identity);
            await File.WriteAllBytesAsync(incoming.Path, incoming.Bytes);
            rig.Db.DownloadQueue.Add(incoming.Queue);
            await rig.Db.SaveChangesAsync();
            return incoming;
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
