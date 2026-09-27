using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;

namespace Sportarr.Api.Tests.Migrations;

public class PendingPackIntentMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteMigrationSupportsFreshAndExistingPendingTables(bool existing)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new SportarrDbContext(new DbContextOptionsBuilder<SportarrDbContext>()
            .UseSqlite(connection).Options);
        if (existing)
        {
            await db.Database.EnsureCreatedAsync();
            var evt = new Event { Title = "Preserved event", Sport = "American Football", EventDate = DateTime.UtcNow };
            db.Events.Add(evt);
            await db.SaveChangesAsync();
            db.PendingReleases.Add(new PendingRelease { EventId = evt.Id, Title = "Preserved pack", Guid = "preserved" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE PendingReleases DROP COLUMN IsPack");
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)");
            foreach (var migration in db.Database.GetMigrations()
                .Where(migration => migration != "20260908224021_PreservePendingPackIntent"))
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO __EFMigrationsHistory VALUES ({migration}, '9.0.9')");
        }

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(existing ? 1 : 0, await db.PendingReleases.CountAsync());
        if (existing)
        {
            var row = await db.PendingReleases.SingleAsync();
            Assert.Equal("Preserved pack", row.Title);
            Assert.Null(row.IsPack);
            foreach (var value in new bool?[] { true, false, null })
            {
                row.IsPack = value;
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                row = await db.PendingReleases.SingleAsync();
                Assert.Equal(value, row.IsPack);
            }
            Assert.Equal(1, await db.Events.CountAsync());
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_index_list('PendingReleases') WHERE name IN ('IX_PendingReleases_EventId', 'IX_PendingReleases_Status_ReleasableAt')";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_list('PendingReleases') WHERE \"table\" = 'Events' AND \"from\" = 'EventId' AND on_delete = 'CASCADE'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }
}
