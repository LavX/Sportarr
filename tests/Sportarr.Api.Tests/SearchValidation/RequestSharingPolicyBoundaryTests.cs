using System.Net;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Xunit.Abstractions;

namespace Sportarr.Api.Tests.SearchValidation;

[Collection(IndexerStatusFixtureCollection.Name)]
public sealed class RequestSharingPolicyBoundaryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AllDeniedRowsKeepTheirOwnFailureAndCompleteWithoutTransport()
    {
        await using var rig = await RequestBudgetBaselineHarness.CreateAsync(output);
        var rows = await RowsAsync(rig, 3, limit: 1);
        foreach (var row in rows)
            await rig.SeedQuotaWindowAsync(row.Id, 1, 0, DateTime.UtcNow.AddHours(1));
        using var http = rig.CompositionClient();
        using var batch = new IndexerSearchRequestBatch();
        var first = await SendAsync(rig, batch, http, rows[0]).WaitAsync(RequestBudgetBaselineHarness.Deadline);
        var rest = await Task.WhenAll(rows.Skip(1).Select(row => SendAsync(rig, batch, http, row)))
            .WaitAsync(RequestBudgetBaselineHarness.Deadline);
        var repeats = await Task.WhenAll(rows.Select(row => SendAsync(rig, batch, http, row)))
            .WaitAsync(RequestBudgetBaselineHarness.Deadline);
        var states = await Task.WhenAll(rows.Select(row => rig.ReadQuotaStateAsync(row.Id)));
        output.WriteLine(JsonSerializer.Serialize(new { First = first, Followers = rest, Repeats = repeats, States = states }));
        using (new AssertionScope())
        {
            rest.Prepend(first).Concat(repeats).Should().OnlyContain(item =>
                item.Failure == nameof(QueryAdmissionFailure.Denied) && item.FailureRow == item.RowId && item.Unexpected == null);
            rig.Source.Attempts.Should().BeEmpty();
            states.Should().OnlyContain(state => state.Queries == 1 && state.QueryFailures == 0 && state.ConnectionErrors == 0);
            rig.Source.Violations.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task HeldReplacementOverlapsFollowersWithoutASecondTransportRequirement()
    {
        await using var rig = await RequestBudgetBaselineHarness.CreateAsync(output);
        rig.Source.HoldFirst = true;
        var rows = await RowsAsync(rig, 4, limit: 1);
        await rig.SeedQuotaWindowAsync(rows[0].Id, 1, 0, DateTime.UtcNow.AddHours(1));
        using var http = rig.CompositionClient();
        using var batch = new IndexerSearchRequestBatch();
        var denied = await SendAsync(rig, batch, http, rows[0]).WaitAsync(RequestBudgetBaselineHarness.Deadline);
        var replacement = SendAsync(rig, batch, http, rows[1]);
        Task<Observation>[] followers = Array.Empty<Task<Observation>>();
        var held = false;
        var replacementPending = false;
        var pendingFollowers = 0;
        var arrivalsWhileHeld = 0;
        try
        {
            // A completed local failure must remain a behavioral result.
            await Task.WhenAny(rig.Source.FirstArrival, replacement).WaitAsync(RequestBudgetBaselineHarness.Deadline);
            held = rig.Source.FirstResponseIsHeld;
            followers = rows.Skip(2).Select(row => SendAsync(rig, batch, http, row)).ToArray();
            replacementPending = !replacement.IsCompleted;
            pendingFollowers = followers.Count(task => !task.IsCompleted);
            arrivalsWhileHeld = rig.Source.TotalArrivals;
        }
        finally
        {
            rig.Source.ReleaseFirst();
        }
        var results = await Task.WhenAll(followers.Prepend(replacement)).WaitAsync(RequestBudgetBaselineHarness.Deadline);
        var states = await Task.WhenAll(rows.Select(row => rig.ReadQuotaStateAsync(row.Id)));
        output.WriteLine(JsonSerializer.Serialize(new { Denied = denied, Held = held, ReplacementPending = replacementPending,
            PendingFollowers = pendingFollowers, ArrivalsWhileHeld = arrivalsWhileHeld, Results = results, States = states }));
        using (new AssertionScope())
        {
            denied.Failure.Should().Be(nameof(QueryAdmissionFailure.Denied));
            denied.FailureRow.Should().Be(rows[0].Id);
            held.Should().BeTrue();
            replacementPending.Should().BeTrue();
            pendingFollowers.Should().Be(2);
            arrivalsWhileHeld.Should().Be(1);
            results.Should().HaveCount(3).And.OnlyContain(item => item.Status == 200 && item.Failure == null && item.Unexpected == null);
            results.Select(item => item.Body).Distinct().Should().ContainSingle();
            results.Should().OnlyContain(item => item.Body != null && item.Body.Contains("offer-source-a-account-a-default"));
            rig.Source.Attempts.Should().ContainSingle().Which.RowId.Should().Be(rows[1].Id.ToString());
            states.Select(state => state.Queries).Should().Equal(1, 1, 0, 0);
            rig.Source.Attempts.Should().OnlyContain(attempt => attempt.ResponseSent && attempt.Status == 200);
            rig.Source.Violations.Should().BeEmpty();
        }
    }

    [Fact]
    public Task PersistenceFailureAtActualReservationSaveDoesNotElectAnotherRow()
        => SaveFailureAsync(cancel: false);

    [Fact]
    public Task CancellationAtActualReservationSaveDoesNotElectAnotherRow()
        => SaveFailureAsync(cancel: true);

    private async Task SaveFailureAsync(bool cancel)
    {
        var barrier = new QueryReservationSaveBarrier { ThrowPersistenceFailure = !cancel };
        await using var rig = await RequestBudgetBaselineHarness.CreateAsync(output, barrier);
        var rows = await RowsAsync(rig, 3, limit: 5);
        barrier.Arm(rows[0].Id);
        using var http = rig.CompositionClient();
        using var batch = new IndexerSearchRequestBatch();
        var owner = SendAsync(rig, batch, http, rows[0]);
        Task<Observation>[] followers = Array.Empty<Task<Observation>>();
        try
        {
            var completed = await Task.WhenAny(barrier.Entered, owner).WaitAsync(RequestBudgetBaselineHarness.Deadline);
            if (completed != barrier.Entered)
                throw new InvalidOperationException("FIXTURE_SAVE_BARRIER_NOT_REACHED");
            followers = rows.Skip(1).Select(row => SendAsync(rig, batch, http, row)).ToArray();
            if (cancel)
            {
                http.CancelPendingRequests();
                await owner.WaitAsync(RequestBudgetBaselineHarness.Deadline);
            }
        }
        finally
        {
            barrier.Release();
        }
        var results = await Task.WhenAll(followers.Prepend(owner)).WaitAsync(RequestBudgetBaselineHarness.Deadline);
        var states = await Task.WhenAll(rows.Select(row => rig.ReadQuotaStateAsync(row.Id)));
        var health = await Task.WhenAll(rows.Select(row => rig.ReadQueryHealthAsync(row.Id)));
        output.WriteLine(JsonSerializer.Serialize(new { Cancel = cancel, Results = results, barrier.ObservedRows,
            barrier.InjectedPersistenceFailure, barrier.CancellationObserved, barrier.DeadlineExpired, States = states, Health = health }));
        var expected = cancel ? QueryAdmissionFailure.Cancelled : QueryAdmissionFailure.Persistence;
        using (new AssertionScope())
        {
            results.Should().HaveCount(3).And.OnlyContain(item => item.Failure == expected.ToString()
                && item.FailureRow == rows[0].Id && item.Status == null && item.Unexpected == null);
            barrier.ObservedRows.Should().Equal(rows[0].Id);
            barrier.DeadlineExpired.Should().BeFalse();
            barrier.InjectedPersistenceFailure.Should().Be(!cancel);
            barrier.CancellationObserved.Should().Be(cancel);
            rig.Source.Attempts.Should().BeEmpty();
            states.Should().OnlyContain(state => state.Queries == 0 && state.QueryFailures == 0
                && state.ConnectionErrors == 0 && state.RateLimitedUntil == null);
            health.Should().OnlyContain(state => state.LastSuccess == null && state.LastQueryFailure == null
                && state.QueryDisabledUntil == null && state.LastConnectionError == null);
            rig.Source.Violations.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(IndexerType.Newznab)]
    [InlineData(IndexerType.Torznab)]
    public async Task SharedProvider429KeepsEachRowsCooldownWithoutReplacement(IndexerType protocol)
    {
        await using var rig = await RequestBudgetBaselineHarness.CreateAsync(output);
        rig.Source.ForcedStatus = 429;
        rig.Source.RetryAfter = "60";
        var rows = await RowsAsync(rig, 3, limit: 10, protocol: protocol);
        var outcome = await rig.CompositionSearchAsync(maximum: 1).WaitAsync(RequestBudgetBaselineHarness.Deadline);
        var states = await Task.WhenAll(rows.Select(row => rig.ReadQuotaStateAsync(row.Id)));
        var health = await Task.WhenAll(rows.Select(row => rig.ReadQueryHealthAsync(row.Id)));
        using (new AssertionScope())
        {
            rig.Source.Attempts.Should().ContainSingle().Which.Status.Should().Be(429);
            rig.Source.Attempts.Should().OnlyContain(attempt => attempt.ResponseSent && attempt.Mode == "search");
            outcome.Releases.Should().BeEmpty();
            outcome.SatisfiesRequest.Should().BeFalse();
            outcome.Diagnostics.Should().HaveCount(3).And.OnlyContain(item => item.Termination == SearchTermination.ProviderRateLimited && !item.SatisfiesRequest);
            for (var index = 0; index < rows.Length; index++)
                states[index].Queries.Should().Be(rig.Source.Attempts.Count(attempt => attempt.RowId == rows[index].Id.ToString()));
            states.Sum(state => state.Queries).Should().Be(1);
            states.Should().OnlyContain(state => state.RateLimitedUntil != null && state.QueryFailures == 0 && state.ConnectionErrors == 0);
            health.Should().OnlyContain(state => state.LastSuccess == null && state.LastQueryFailure == null && state.QueryDisabledUntil == null);
            rig.Source.Violations.Should().BeEmpty();
        }
    }

    private static async Task<Indexer[]> RowsAsync(RequestBudgetBaselineHarness rig, int count, int limit, IndexerType protocol = IndexerType.Newznab)
    {
        var rows = Enumerable.Range(1, count).Select(number => rig.Row("Policy row " + number, protocol)).ToArray();
        foreach (var row in rows) row.QueryLimit = limit;
        await rig.SaveRowsAsync(rows);
        return rows;
    }

    private static async Task<Observation> SendAsync(RequestBudgetBaselineHarness rig, IndexerSearchRequestBatch batch, HttpClient http, Indexer row)
    {
        var url = rig.Source.Url + "/api?t=search&apikey=" + Uri.EscapeDataString(RequestBudgetBaselineHarness.BudgetSource.KeyA)
            + "&q=budget-fixture&limit=1&extended=1";
        using var request = IndexerQueryRequest.Create(row, url, new IndexerQueryContext(row.Id));
        try
        {
            using var response = await batch.SendAsync(http, nameof(IndexerType.Newznab), row.RequestDelayMs,
                request, HttpCompletionOption.ResponseHeadersRead);
            return new(row.Id, (int)response.StatusCode, await response.Content.ReadAsStringAsync(), null, null, null);
        }
        catch (IndexerQueryAdmissionException failure)
        {
            return new(row.Id, null, null, failure.Kind.ToString(), failure.IndexerId, null);
        }
        catch (Exception failure)
        {
            return new(row.Id, null, null, null, null, failure.GetType().FullName);
        }
    }

    private sealed record Observation(int RowId, int? Status, string? Body, string? Failure, int? FailureRow, string? Unexpected);
}
