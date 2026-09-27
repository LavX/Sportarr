using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Startup;

namespace Sportarr.Api.Tests.Migrations;

public class EventTypeFolderMigrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SqliteStartupKeepsOrAddsFolderColumn(bool existingDatabase, bool existingColumn)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>()
            .UseSqlite(connection).Options);

        if (existingDatabase)
        {
            await db.Database.EnsureCreatedAsync();
            if (!existingColumn)
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE MediaManagementSettings DROP COLUMN CreateEventTypeFolders");

            await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)");
            foreach (var migration in db.Database.GetMigrations()
                .Where(migration => migration != "20260927070006_AddEventTypeFolders"))
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO __EFMigrationsHistory VALUES ({migration}, '9.0.9')");
        }

        DatabaseInitializer.ReconcileExistingColumnMigrations(db);
        await db.Database.MigrateAsync();
        Assert.Contains("20260927070006_AddEventTypeFolders", await db.Database.GetAppliedMigrationsAsync());
        DatabaseInitializer.EnsureEventTypeFolderColumn(db);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('MediaManagementSettings') WHERE name='CreateEventTypeFolders'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
