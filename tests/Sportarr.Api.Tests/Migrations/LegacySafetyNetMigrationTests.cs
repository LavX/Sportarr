using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Startup;

namespace Sportarr.Api.Tests.Migrations;

public class LegacySafetyNetMigrationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SqliteUpgradeAcceptsColumnsFromEarlierSafetyNets(bool hasPackColumn, bool hasFailureTimeColumn)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        if (!hasPackColumn)
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE PendingReleases DROP COLUMN IsPack");
        if (!hasFailureTimeColumn)
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DownloadQueue DROP COLUMN FailedAt");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_PendingReleases_EventId");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_PendingReleases_Status_ReleasableAt");

        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)");
        foreach (var migration in db.Database.GetMigrations()
            .Where(migration => migration is not "20260908224021_PreservePendingPackIntent"
                and not "20260926044143_AddDownloadFailureTime"))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO __EFMigrationsHistory VALUES ({migration}, '9.0.9')");

        DatabaseInitializer.ReconcileExistingColumnMigrations(db);
        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains("20260908224021_PreservePendingPackIntent", applied);
        Assert.Contains("20260926044143_AddDownloadFailureTime", applied);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('PendingReleases') WHERE name='IsPack'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('DownloadQueue') WHERE name='FailedAt'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'IX_PendingReleases_%'";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }
}
