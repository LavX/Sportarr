using Microsoft.Data.Sqlite;
using Npgsql;

namespace Sportarr.Api.Services;

/// <summary>
/// Remembers database failures that mean the database itself is damaged.
///
/// The state is held in memory rather than queried, so a probe still answers
/// when no query can run. It is sticky because a damaged file does not repair
/// itself, and only a restore clears it.
/// </summary>
public sealed class DatabaseHealthTracker
{
    private readonly object _gate = new();
    private long _count;
    private string? _lastError;
    private DateTime? _firstSeenUtc;
    private DateTime? _lastSeenUtc;

    /// <summary>Whether a command has failed because the database is damaged.</summary>
    public bool IsDamaged
    {
        get { lock (_gate) { return _count > 0; } }
    }

    /// <summary>A snapshot of what has been seen, for the health check message.</summary>
    public (long Count, string? LastError, DateTime? FirstSeenUtc, DateTime? LastSeenUtc) Snapshot()
    {
        lock (_gate) { return (_count, _lastError, _firstSeenUtc, _lastSeenUtc); }
    }

    /// <summary>
    /// Record a failed command. Anything that is not a damage signal is
    /// ignored, so an ordinary constraint violation never marks the database
    /// as broken.
    /// </summary>
    public void RecordFailure(Exception? exception)
    {
        if (!IsDamageSignal(exception))
            return;

        lock (_gate)
        {
            _count++;
            _lastError = exception!.Message;
            _lastSeenUtc = DateTime.UtcNow;
            _firstSeenUtc ??= _lastSeenUtc;
        }
    }

    /// <summary>
    /// Clear the recorded damage after a restore. Damage that is still there
    /// re-reports itself on the next failing command.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _count = 0;
            _lastError = null;
            _firstSeenUtc = null;
            _lastSeenUtc = null;
        }
    }

    /// <summary>
    /// Whether an exception says the stored data is damaged, rather than that
    /// a statement was wrong. These codes never recover on their own, so one
    /// is enough to act on.
    /// </summary>
    public static bool IsDamageSignal(Exception? exception)
    {
        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            switch (ex)
            {
                // SQLITE_CORRUPT is the malformed-disk-image error. SQLITE_NOTADB
                // means the file is not a database at all, which is what a
                // truncated or overwritten file looks like.
                case SqliteException sqlite when sqlite.SqliteErrorCode is 11 or 26:
                    return true;

                // Postgres class XX: data_corrupted and index_corrupted.
                case PostgresException postgres when postgres.SqlState is "XX001" or "XX002":
                    return true;
            }
        }

        return false;
    }
}
