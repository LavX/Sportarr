using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit.Abstractions;

namespace Sportarr.Api.Tests.SearchValidation;

[Collection(IndexerStatusFixtureCollection.Name)]
public sealed class CombinedProbeCacheTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MetadataEligibleDefaultNegativePlanCachesAllQueriesAndOneProbe()
    {
        await using var rig = await CombinedProbeCacheHarness.CreateAsync(output);
        Assert.Null(rig.Event.League!.SearchQueryTemplate);
        Assert.True(rig.Queries.Length > 2);
        Assert.Equal("Belgian Grand Prix", rig.Probe);
        var contract = await rig.ContractHashAsync();
        var initial = await rig.ReadStateAsync();
        rig.StartMeasurement();
        var first = await rig.RunAsync("complete-empty-plan-and-probe", automatic: true);
        var repeat = await rig.RunAsync("negative-cache-repeat", automatic: true);
        var queue = await rig.QueueCountAsync();
        using var scope = new AssertionScope();
        initial.Queries.Should().Be(0);
        initial.LastSuccess.Should().BeNull();
        first.Attempts.Select(attempt => attempt.Mode).Should().Equal(Enumerable.Repeat("search", rig.Queries.Length + 1));
        first.Attempts.Select(attempt => attempt.Query).Should().Equal(rig.Queries.Append(rig.Probe));
        first.Attempts.Select(attempt => attempt.Offset).Should().Equal(Enumerable.Repeat(0, rig.Queries.Length + 1));
        first.Attempts.SelectMany(attempt => attempt.Guids).Should().BeEmpty();
        repeat.Attempts.Should().BeEmpty();
        rig.Transport.Attempts.Should().HaveCount(rig.Queries.Length + 1);
        first.State.Queries.Should().Be(rig.Queries.Length + 1);
        first.State.LastSuccess.Should().NotBeNull();
        first.State.ResetAt.Should().Be(initial.ResetAt);
        repeat.State.Should().Be(first.State);
        foreach (var phase in new[] { first, repeat })
        {
            Complete(phase).Should().BeTrue();
            phase.Found.Should().Be(0);
            phase.CacheEntries.Should().HaveCount(2);
            foreach (var entry in phase.CacheEntries)
            {
                entry.Guids.Should().NotBeNull();
                entry.Guids.Should().BeEmpty();
                entry.LifetimeSeconds.Should().BeInRange(60 - (int)Math.Ceiling((first.EndMilliseconds - first.StartMilliseconds) / 1000), 60);
            }
            AssertPhase(rig, phase, contract);
        }
        repeat.CacheEntries.Select(entry => entry.Key).Should().Equal(first.CacheEntries.Select(entry => entry.Key));
        repeat.CacheEntries.Select(entry => entry.LifetimeSeconds).Should().Equal(first.CacheEntries.Select(entry => entry.LifetimeSeconds));
        queue.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteMetadataProbeRefetchesAfterOnlyQuotaWindowExpiry(bool pagedOffers)
    {
        await using var rig = await CombinedProbeCacheHarness.CreateAsync(output, pagedOffers,
            initialQueries: pagedOffers ? 4 : 6);
        Assert.Equal("Belgian Grand Prix", rig.Probe);
        Assert.True(rig.Queries.Length > 2);
        var contract = await rig.ContractHashAsync();
        var initial = await rig.ReadStateAsync();
        rig.StartMeasurement();
        var incomplete = await rig.RunAsync("incomplete-probe", automatic: true);
        await rig.ExpireWindowOnlyAsync();
        var expired = await rig.ReadStateAsync();
        var recovered = await rig.RunAsync("recovered-probe", automatic: true);
        var warm = await rig.RunAsync("complete-probe-cache", automatic: true);
        var queue = await rig.QueueCountAsync();
        var expected = pagedOffers ? Enumerable.Range(1, 3).Select(number => "probe-offer-" + number).ToArray() : Array.Empty<string>();
        using var scope = new AssertionScope();
        initial.Queries.Should().Be(pagedOffers ? 4 : 6);
        initial.LastSuccess.Should().BeNull();
        incomplete.Attempts.Select(attempt => attempt.Mode).Should().Equal(pagedOffers
            ? new[] { "caps" }.Concat(Enumerable.Repeat("search", rig.Queries.Length + 1)) : Enumerable.Repeat("search", rig.Queries.Length));
        var firstSearches = incomplete.Attempts.Where(attempt => attempt.Mode == "search").ToArray();
        firstSearches.Select(attempt => attempt.Query).Should().Equal(pagedOffers
            ? rig.Queries.Append(rig.Probe) : rig.Queries);
        firstSearches.Select(attempt => attempt.Offset).Should().Equal(Enumerable.Repeat(0, rig.Queries.Length + (pagedOffers ? 1 : 0)));
        firstSearches.SelectMany(attempt => attempt.Guids).Should().Equal(expected.Take(2));
        incomplete.Found.Should().Be(pagedOffers ? 2 : 0);
        incomplete.State.Queries.Should().Be(rig.Indexer.QueryLimit);
        incomplete.State.LastSuccess.Should().NotBeNull("the complete primary plan ran before probe admission");
        Complete(incomplete).Should().BeFalse();
        var probeDiagnostic = Diagnostics(incomplete).Where(row => row.GetProperty("Query").GetString() == rig.Probe).ToArray();
        probeDiagnostic.Should().ContainSingle();
        if (probeDiagnostic.Length == 1)
        {
            probeDiagnostic[0].GetProperty("Termination").GetString().Should().Be(pagedOffers ? "LocalDenied" : "Unavailable");
            probeDiagnostic[0].GetProperty("SatisfiesRequest").GetBoolean().Should().BeFalse();
            probeDiagnostic[0].GetProperty("RawCursor").GetInt32().Should().Be(pagedOffers ? 2 : 0);
            probeDiagnostic[0].GetProperty("Pages").GetArrayLength().Should().Be(pagedOffers ? 1 : 0);
            probeDiagnostic[0].GetProperty("KnownPageLimit").GetBoolean().Should().Be(pagedOffers);
        }
        incomplete.CacheEntries.Should().HaveCount(2);
        incomplete.CacheEntries[0].Guids.Should().NotBeNull();
        incomplete.CacheEntries[0].Guids.Should().BeEmpty();
        incomplete.CacheEntries[0].LifetimeSeconds.Should().BeInRange(60 - (int)Math.Ceiling((incomplete.EndMilliseconds - incomplete.StartMilliseconds) / 1000), 60);
        incomplete.CacheEntries[1].Guids.Should().BeNull();
        incomplete.CacheEntries[1].LifetimeSeconds.Should().BeNull();
        expired.Should().BeEquivalentTo(incomplete.State, options => options.Excluding(state => state.ResetAt));
        expired.ResetAt.Should().NotBe(incomplete.State.ResetAt);
        expired.ResetAt.Should().BeBefore(DateTime.UtcNow);
        recovered.Attempts.Select(attempt => attempt.Mode).Should().Equal(pagedOffers ? new[] { "search", "search" } : new[] { "search" });
        recovered.Attempts.Select(attempt => attempt.Query).Should().Equal(Enumerable.Repeat(rig.Probe, pagedOffers ? 2 : 1));
        recovered.Attempts.Select(attempt => attempt.Offset).Should().Equal(pagedOffers ? new[] { 0, 2 } : new[] { 0 });
        recovered.Attempts.SelectMany(attempt => attempt.Guids).Should().Equal(expected);
        recovered.State.Queries.Should().Be(pagedOffers ? 2 : 1);
        recovered.State.LastSuccess.Should().NotBeNull();
        warm.Attempts.Should().BeEmpty();
        warm.State.Should().Be(recovered.State);
        foreach (var phase in new[] { recovered, warm })
        {
            Complete(phase).Should().BeTrue();
            phase.Found.Should().Be(expected.Length);
            phase.CacheEntries.Should().HaveCount(2);
            phase.CacheEntries[0].Guids.Should().NotBeNull();
            phase.CacheEntries[0].Guids.Should().BeEmpty();
            phase.CacheEntries[0].LifetimeSeconds.Should().Be(incomplete.CacheEntries[0].LifetimeSeconds);
            phase.CacheEntries[1].Guids.Should().Equal(expected);
            var lifetime = pagedOffers ? 300 : 60;
            phase.CacheEntries[1].LifetimeSeconds.Should().BeInRange(lifetime - (int)Math.Ceiling((recovered.EndMilliseconds - recovered.StartMilliseconds) / 1000), lifetime);
        }
        warm.CacheEntries.Select(entry => entry.LifetimeSeconds).Should().Equal(recovered.CacheEntries.Select(entry => entry.LifetimeSeconds));
        foreach (var phase in new[] { incomplete, recovered, warm })
        {
            phase.CacheEntries.Select(entry => entry.Key).Should().Equal(incomplete.CacheEntries.Select(entry => entry.Key));
            AssertPhase(rig, phase, contract);
        }
        rig.Transport.Attempts.Should().HaveCount(rig.Queries.Length + (pagedOffers ? 4 : 1));
        queue.Should().Be(0);
    }

    private static bool Complete(CombinedProbeCacheHarness.Phase phase)
    {
        using var body = JsonDocument.Parse(phase.Body);
        return body.RootElement.GetProperty("SearchComplete").GetBoolean();
    }

    private static JsonElement[] Diagnostics(CombinedProbeCacheHarness.Phase phase)
    {
        using var body = JsonDocument.Parse(phase.Body);
        return body.RootElement.GetProperty("SearchDiagnostics").EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    private static void AssertPhase(CombinedProbeCacheHarness rig, CombinedProbeCacheHarness.Phase phase, string contract)
    {
        phase.ContractHash.Should().Be(contract);
        phase.State.QueryFailures.Should().Be(0);
        phase.State.ConnectionErrors.Should().Be(0);
        phase.State.Grabs.Should().Be(0);
        phase.State.RateLimitedUntil.Should().BeNull();
        phase.Selected.Should().BeNull("the fixed 2160p catalogue is excluded by the unchanged 1080p profile");
        phase.EndMilliseconds.Should().BeLessThan(45_000);
        phase.EndMilliseconds.Should().BeGreaterThanOrEqualTo(phase.StartMilliseconds);
        rig.Transport.Violations.Should().BeEmpty();
        foreach (var attempt in phase.Attempts)
        {
            attempt.Status.Should().Be(200);
            attempt.ResponseSent.Should().BeTrue();
            attempt.RowId.Should().Be(rig.Indexer.Id.ToString());
            attempt.ResponseSha256.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(attempt.ResponseBody))));
            if (attempt.Mode != "search") continue;
            attempt.EventId.Should().Be(rig.Event.ExternalId);
            var document = XDocument.Parse(attempt.ResponseBody);
            var items = document.Descendants("item").ToArray();
            items.Select(item => item.Element("guid")!.Value).Should().Equal(attempt.Guids);
            foreach (var item in items)
            {
                var row = rig.Transport.Releases.Single(release => release.Guid == item.Element("guid")!.Value);
                item.Element("title")!.Value.Should().Be(row.Title);
                item.Element("enclosure")!.Attribute("length")!.Value.Should().Be(row.Size.ToString());
                item.Elements(XName.Get("attr", "http://www.newznab.com/DTD/2010/feeds/attributes/"))
                    .Single(attribute => attribute.Attribute("name")!.Value == "sportarrid")
                    .Attribute("value")!.Value.Should().Be(row.SportarrEventId);
            }
        }
    }
}
