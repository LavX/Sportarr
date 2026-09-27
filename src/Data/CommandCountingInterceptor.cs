using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sportarr.Api.Helpers;
using Sportarr.Api.Services;

namespace Sportarr.Api.Data;

/// <summary>
/// Counts executed DB commands into the ambient <see cref="SyncMetrics"/>
/// counter so a measured league sync can report objective DB round-trip
/// totals. Registered on the DbContext options for both the scoped context
/// and the context factory.
///
/// Every override is a no-op outside a <see cref="SyncMetrics"/> measured
/// block (a single AsyncLocal read), so this imposes no meaningful overhead
/// on normal request-path queries.
///
/// It also reports failed commands to the <see cref="DatabaseHealthTracker"/>.
/// Every query in the app passes through here, so a damaged database is seen
/// wherever it is first touched, and no health surface has to run a PRAGMA of
/// its own to find out.
/// </summary>
public sealed class CommandCountingInterceptor : DbCommandInterceptor
{
    private readonly DatabaseHealthTracker _databaseHealth;

    public CommandCountingInterceptor(DatabaseHealthTracker databaseHealth)
    {
        _databaseHealth = databaseHealth;
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        _databaseHealth.RecordFailure(eventData.Exception);
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _databaseHealth.RecordFailure(eventData.Exception);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        SyncMetrics.IncrementDbCommands(command.CommandText);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        SyncMetrics.IncrementDbCommands(command.CommandText);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        SyncMetrics.IncrementDbCommands(command.CommandText);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        SyncMetrics.IncrementDbCommands(command.CommandText);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command, CommandExecutedEventData eventData, int result)
    {
        SyncMetrics.IncrementDbCommands(command.CommandText);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        SyncMetrics.IncrementDbCommands(command.CommandText);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }
}
