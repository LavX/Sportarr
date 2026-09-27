using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sportarr.Api.Migrations
{
    /// <inheritdoc />
    public partial class PreservePendingPackIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fresh SQLite databases reach this migration before the legacy table repair.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "PendingReleases" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "EventId" INTEGER NOT NULL,
                "Title" TEXT NOT NULL,
                "Guid" TEXT NOT NULL,
                "DownloadUrl" TEXT NOT NULL,
                "InfoUrl" TEXT NULL,
                "Indexer" TEXT NOT NULL,
                "IndexerId" INTEGER NULL,
                "TorrentInfoHash" TEXT NULL,
                "Protocol" TEXT NOT NULL,
                "Size" INTEGER NOT NULL,
                "Quality" TEXT NULL,
                "Source" TEXT NULL,
                "Codec" TEXT NULL,
                "Language" TEXT NULL,
                "ReleaseGroup" TEXT NULL,
                "QualityScore" INTEGER NOT NULL,
                "CustomFormatScore" INTEGER NOT NULL,
                "Score" INTEGER NOT NULL,
                "MatchScore" INTEGER NOT NULL,
                "Part" TEXT NULL,
                "Seeders" INTEGER NULL,
                "Leechers" INTEGER NULL,
                "PublishDate" TEXT NOT NULL,
                "AddedToPendingAt" TEXT NOT NULL,
                "ReleasableAt" TEXT NOT NULL,
                "Reason" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                CONSTRAINT "FK_PendingReleases_Events_EventId" FOREIGN KEY ("EventId") REFERENCES "Events" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_PendingReleases_EventId" ON "PendingReleases" ("EventId");
                CREATE INDEX IF NOT EXISTS "IX_PendingReleases_Status_ReleasableAt" ON "PendingReleases" ("Status", "ReleasableAt");
                """);

            migrationBuilder.AddColumn<bool>(
                name: "IsPack",
                table: "PendingReleases",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPack",
                table: "PendingReleases");
        }
    }
}
