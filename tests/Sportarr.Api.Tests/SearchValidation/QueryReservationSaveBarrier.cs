using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.SearchValidation;

internal sealed class QueryReservationSaveBarrier : SaveChangesInterceptor
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<int> _rows = new();
    private int _rowId;
    internal Task Entered => _entered.Task;
    internal int[] ObservedRows => _rows.ToArray();
    internal bool ThrowPersistenceFailure { get; set; }
    internal bool InjectedPersistenceFailure { get; private set; }
    internal bool CancellationObserved { get; private set; }
    internal bool DeadlineExpired { get; private set; }
    internal void Arm(int rowId) => Volatile.Write(ref _rowId, rowId);
    internal void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var rowId = Volatile.Read(ref _rowId);
        if (rowId == 0 || eventData.Context == null) return result;
        var reservation = eventData.Context.ChangeTracker.Entries<IndexerStatus>().Any(entry =>
            entry.Entity.IndexerId == rowId && entry.Entity.QueriesThisHour > entry.Property(item => item.QueriesThisHour).OriginalValue);
        if (!reservation) return result;
        _rows.Enqueue(rowId);
        _entered.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CancellationObserved = true;
            throw;
        }
        catch (TimeoutException)
        {
            DeadlineExpired = true;
            throw;
        }
        if (ThrowPersistenceFailure)
        {
            InjectedPersistenceFailure = true;
            throw new InvalidOperationException("FIXTURE_SAVE_FAILURE");
        }
        return result;
    }
}
