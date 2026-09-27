using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sportarr.Api.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using AppTaskStatus = Sportarr.Api.Models.TaskStatus;

namespace Sportarr.Api.Tests.SearchValidation;

public class RssSyncExecutionTests
{
    [Fact]
    public void BackgroundRegistration_SharesRssInstanceWithTaskResolution()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSportarrBackgroundServices();
        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().OfType<RssSyncService>().Single();
        using var taskScope = provider.CreateScope();
        hosted.Should().BeSameAs(taskScope.ServiceProvider.GetRequiredService<RssSyncService>());
    }

    [Fact]
    public async Task RssTask_FailsWhenTheRealSyncCannotStart()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<SportarrDbContext>(o => o.UseInMemoryDatabase(databaseName));
        services.AddSingleton<RssSyncService>(_ => throw new InvalidOperationException("RSS cycle failed"));
        using var provider = services.BuildServiceProvider();
        var service = new TaskService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TaskService>.Instance);
        int taskId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();
            var task = new AppTask { Name = "RSS Sync", CommandName = "RssSync" };
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
        }

        await ExecuteTaskAsync(service, taskId);

        var result = await service.GetTaskAsync(taskId);
        result.Should().NotBeNull();
        result!.Status.Should().Be(AppTaskStatus.Failed);
        result.Exception.Should().Contain("RSS cycle failed");
    }

    [Fact]
    public async Task SyncNow_SerializesOverlappingCycles()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var sync = new ControlledRssSync(provider);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.Cycle = token => release.Task;
        var first = sync.SyncNowAsync(CancellationToken.None);
        var second = sync.SyncNowAsync(CancellationToken.None);
        try
        {
            sync.Calls.Should().Be(1);
            second.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }
        sync.Calls.Should().Be(2);
    }

    [Fact]
    public async Task SyncNow_CancelledWaiterDoesNotEnterOrReleaseAnotherCyclesGate()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var sync = new ControlledRssSync(provider);
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.Cycle = token => release.Task;
        var first = sync.SyncNowAsync(CancellationToken.None);
        try
        {
            var waiting = sync.SyncNowAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            sync.Calls.Should().Be(1);
            var next = sync.SyncNowAsync(CancellationToken.None);
            next.IsCompleted.Should().BeFalse();
            sync.Calls.Should().Be(1);
            release.SetResult();
            await Task.WhenAll(first, next).WaitAsync(TimeSpan.FromSeconds(5));
            sync.Calls.Should().Be(2);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SyncNow_PreCancelledCallDoesNoWork()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var sync = new ControlledRssSync(provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.SyncNowAsync(cancellation.Token));
        sync.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SyncNow_FailureReleasesTheGateAndKeepsTheError()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var sync = new ControlledRssSync(provider);
        sync.Cycle = token => Task.FromException(new InvalidOperationException("Feed cycle failed"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncNowAsync(CancellationToken.None));
        error.Message.Should().Be("Feed cycle failed");
        sync.Cycle = token => Task.CompletedTask;
        await sync.SyncNowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        sync.Calls.Should().Be(2);
    }

    [Fact]
    public async Task SyncNow_CancellationWaitsForStartedWorkBeforeReleasingTheGate()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var sync = new ControlledRssSync(provider);
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.Cycle = token => release.Task;
        var first = sync.SyncNowAsync(cancellation.Token);
        cancellation.Cancel();
        var second = sync.SyncNowAsync(CancellationToken.None);
        try
        {
            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse();
            sync.Calls.Should().Be(1);
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
        sync.Calls.Should().Be(2);
    }

    [Fact]
    public async Task RssTask_CompletesOnlyAfterTheCycleFinishes()
    {
        using var fixture = new TaskFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sync.Cycle = token =>
        {
            entered.SetResult();
            return release.Task;
        };
        var id = await fixture.AddTaskAsync();
        var execution = ExecuteTaskAsync(fixture.Tasks, id);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = await fixture.Tasks.GetTaskAsync(id);
            running!.Status.Should().Be(AppTaskStatus.Running);
            running.Progress.Should().BeLessThan(100);
            execution.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var completed = await fixture.Tasks.GetTaskAsync(id);
        completed!.Status.Should().Be(AppTaskStatus.Completed);
        completed.Progress.Should().Be(100);
        completed.Ended.Should().NotBeNull();
        fixture.Sync.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RssTask_PersistsCycleFailure()
    {
        using var fixture = new TaskFixture();
        fixture.Sync.Cycle = token => Task.FromException(new InvalidOperationException("Feed processing failed"));
        var id = await fixture.AddTaskAsync();
        await ExecuteTaskAsync(fixture.Tasks, id);
        var failed = await fixture.Tasks.GetTaskAsync(id);
        failed!.Status.Should().Be(AppTaskStatus.Failed);
        failed.Message.Should().Be("Feed processing failed");
        failed.Exception.Should().Contain("Feed processing failed");
        failed.Ended.Should().NotBeNull();
    }

    [Fact]
    public async Task RssTask_CancellationDoesNotReportCompletedOrAbandonStartedWork()
    {
        using var fixture = new TaskFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken cycleToken = default;
        fixture.Sync.Cycle = token =>
        {
            cycleToken = token;
            entered.SetResult();
            return release.Task;
        };
        var id = await fixture.AddTaskAsync();
        var execution = ExecuteTaskAsync(fixture.Tasks, id);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (await fixture.Tasks.CancelTaskAsync(id)).Should().BeTrue();
            cycleToken.IsCancellationRequested.Should().BeTrue();
            execution.IsCompleted.Should().BeFalse();
            (await fixture.Tasks.GetTaskAsync(id))!.Status.Should().Be(AppTaskStatus.Aborting);
        }
        finally
        {
            release.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var cancelled = await fixture.Tasks.GetTaskAsync(id);
        cancelled!.Status.Should().Be(AppTaskStatus.Cancelled);
        cancelled.Progress.Should().BeLessThan(100);
        cancelled.Ended.Should().NotBeNull();
    }

    private sealed class ControlledRssSync(IServiceProvider provider)
        : RssSyncService(provider, NullLogger<RssSyncService>.Instance)
    {
        private int _calls;
        public int Calls => _calls;
        public Func<CancellationToken, Task> Cycle { get; set; } = token => Task.CompletedTask;

        protected override Task PerformRssSyncAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Cycle(cancellationToken);
        }
    }

    private sealed class TaskFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public ControlledRssSync Sync { get; }
        public TaskService Tasks { get; }

        public TaskFixture()
        {
            var name = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<SportarrDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton<RssSyncService>(p => new ControlledRssSync(p));
            _provider = services.BuildServiceProvider();
            Sync = (ControlledRssSync)_provider.GetRequiredService<RssSyncService>();
            Tasks = new TaskService(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TaskService>.Instance);
        }

        public async Task<int> AddTaskAsync()
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();
            var task = new AppTask { Name = "RSS Sync", CommandName = "RssSync" };
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            return task.Id;
        }

        public void Dispose() => _provider.Dispose();
    }

    private static Task ExecuteTaskAsync(TaskService service, int taskId)
    {
        var method = typeof(TaskService).GetMethod("ExecuteTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, new object[] { taskId })!;
    }
}
