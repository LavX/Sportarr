using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.Services;

public class RegrabAcquisitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Regrab_waits_for_the_event_decision_before_adding(bool bulk)
    {
        await using var rig = await RegrabMissingHttpHarness.CreateAsync();
        var owner = rig.History(null); owner.FileExists = false;
        rig.Db.GrabHistory.Add(owner); await rig.Db.SaveChangesAsync();
        using var decision = await rig.EnterEventDecisionAsync();
        var path = bulk ? "/api/grab-history/regrab-missing" : $"/api/grab-history/{owner.Id}/regrab";
        var request = rig.Client.PostAsync(path, null);
        try
        {
            var completed = await Task.WhenAny(request, Task.Delay(300));
            completed.Should().NotBeSameAs(request);
            rig.ClientAdds.Should().Be(0);
        }
        finally { decision.Dispose(); }
        using var response = await request;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        rig.ClientAdds.Should().Be(1);
        (await rig.Db.DownloadQueue.CountAsync()).Should().Be(1);
    }
    [Fact]
    public async Task History_replacement_cannot_publish_before_blocklist_and_removal_are_saved()
    {
        var barrier = new ReplacementBarrier();
        await using var rig = await RegrabMissingHttpHarness.CreateAsync(barrier);
        var history = new ImportHistory { EventId = rig.Event.Id, SourcePath = "Fixture.Release.mkv",
            DestinationPath = "Fixture.Import.mkv", Quality = "WEBDL-720p", Part = "Main Card" };
        rig.Db.ImportHistories.Add(history); await rig.Db.SaveChangesAsync();
        using var read = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>()
            .UseSqlite(rig.Db.Database.GetDbConnection()).Options);
        var request = rig.Client.DeleteAsync($"/api/history/{history.Id}?blocklistAction=blocklistAndSearch");
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            (await read.ImportHistories.CountAsync()).Should().Be(0);
            (await read.Blocklist.CountAsync()).Should().Be(1);
        }
        finally { barrier.Release.TrySetResult(); await request; }
        using var response = await request;
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await read.Tasks.CountAsync()).Should().Be(1);
    }

    private sealed class ReplacementBarrier : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AppTask>().Any(e => e.State == EntityState.Added))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
