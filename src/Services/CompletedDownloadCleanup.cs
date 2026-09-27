using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Helpers;
using Sportarr.Api.Models;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Api.Services;

internal sealed class CompletedDownloadCleanup(IServiceProvider serviceProvider, ILogger logger)
{
    internal async Task RunAsync(DownloadQueueItem download, DownloadClientService downloadClientService, SportarrDbContext? db)
    {
        // The caller holds the job scope for pack completion.
        if (download.IsPack)
        {
            if (db == null) return;
            using var configScope = serviceProvider.CreateScope();
            var completionConfig = await configScope.ServiceProvider.GetRequiredService<ConfigService>().GetConfigAsync();
            if (!await PackImportJobScope.HasCompleteCoverageAsync(db, download, completionConfig)) return;
        }

        // Remove from download client if configured in the client's settings
        // Pass deleteFiles: true to also remove the download folder from disk
        // The video files have already been moved/hardlinked to the library, but non-video files (nfo, srr, etc.)
        // and the folder itself may remain - the download client should clean these up
        //
        // Uses per-client RemoveCompletedDownloads setting which allows users to configure
        // differently for each client (e.g., remove for Usenet, preserve for seeding torrents)
        if (download.DownloadClient?.RemoveCompletedDownloads == true)
        {
            // For torrents with indexer seed settings, check if seeding goals are met before removal.
            // Torrents seed until ratio/time limits are reached.
            if (download.Protocol == "Torrent" && db != null)
            {
                var indexer = download.IndexerId != null
                    ? await db.Indexers.FindAsync(download.IndexerId)
                    : !string.IsNullOrEmpty(download.Indexer)
                        ? await db.Indexers.FirstOrDefaultAsync(i => i.Name == download.Indexer)
                        : null;

                if (indexer != null && (indexer.SeedRatio.HasValue || indexer.SeedTime.HasValue))
                {
                    var status = await downloadClientService.GetDownloadStatusAsync(
                        download.DownloadClient, download.DownloadId, download.GrabCategory);

                    if (status != null && !HasReachedSeedLimit(status, indexer))
                    {
                        logger.LogInformation(
                            "[Enhanced Download Monitor] Torrent still seeding, skipping removal: {Title} " +
                            "(Ratio: {Ratio:F2}/{Target}, Time: {Time})",
                            download.Title,
                            status.Ratio ?? 0,
                            indexer.SeedRatio?.ToString("F1") ?? "N/A",
                            indexer.SeedTime.HasValue ? $"{indexer.SeedTime}min" : "N/A");

                        // Keep the imported row for the next monitor check.
                        return;
                    }
                }
            }

            // Ask the client where the job landed BEFORE removing it. Once
            // the job is gone from history the path is gone with it, and
            // that path is the only safe way to find the folder to tidy up.
            string? completedFolder = null;
            try
            {
                var statusForPath = await downloadClientService.GetDownloadStatusAsync(
                    download.DownloadClient, download.DownloadId, download.GrabCategory);
                completedFolder = statusForPath?.SavePath;

                // The client reports its own view of the path. Behind a
                // remote path mapping the local view differs, and only a
                // local path can be resolved and removed.
                if (!string.IsNullOrWhiteSpace(completedFolder))
                {
                    using var mapScope = serviceProvider.CreateScope();
                    var pathMapping = mapScope.ServiceProvider.GetRequiredService<IRemotePathMappingService>();
                    completedFolder = await pathMapping.RemapRemoteToLocalAsync(
                        download.DownloadClient.Host, completedFolder);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[Enhanced Download Monitor] Could not read the completed path for {Title}", download.Title);
            }

            // SABnzbd reports a single-file job as the file itself, which
            // the move-mode import has already taken away. Resolve to the
            // job folder that owns it now, while the directory still
            // exists. The removal below can delete the directory, and a
            // path that stopped existing reads as a vanished file, whose
            // parent would then be judged in its place.
            completedFolder = LeftoverFolderPolicy.ResolveOwnedFolder(completedFolder, download.Title);

            try
            {
                var removed = await downloadClientService.RemoveDownloadAsync(
                    download.DownloadClient,
                    download.DownloadId,
                    deleteFiles: true);

                if (!removed)
                {
                    logger.LogWarning("[Enhanced Download Monitor] Import succeeded, but {Client} did not confirm removal of {Title}; skipping remaining download folder cleanup",
                        download.DownloadClient.Name, download.Title);
                    return;
                }

                // Info, not Debug: whether the download-dir folder was
                // cleaned up is the question every "empty folders left
                // behind" support thread turns on, and at Debug the
                // default log couldn't answer it in either direction.
                // SABnzbd only deletes files for FAILED history entries;
                // del_files on a completed job removes the history row
                // and nothing else. The folder tidy-up below is what
                // actually cleans the disk for usenet.
                logger.LogInformation("[Enhanced Download Monitor] Removed completed download from client: {Title}", download.Title);

                await TryRemoveCompletedFolderAsync(completedFolder, download.Title, db);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Enhanced Download Monitor] Failed to remove download from client: {Title}", download.Title);
                // Don't fail the import if we can't remove from client
            }
        }
        else if (download.DownloadClient == null)
        {
            // Log when download client removal is skipped due to missing client association
            // This helps diagnose why folders might not be removed from the download client
            logger.LogInformation("[Enhanced Download Monitor] Skipped removal from download client: No download client associated with {Title}",
                download.Title);
        }
        else
        {
            // The toggle-off case was completely silent, which made every
            // "empty folders left behind" report unanswerable from a log:
            // no line existed in either direction. One info line per
            // import names the setting that decides the behavior.
            logger.LogInformation("[Enhanced Download Monitor] Leaving download in client (Remove Completed Downloads is off for '{Client}'): {Title}",
                download.DownloadClient!.Name, download.Title);
        }
    }

    internal async Task TryRemoveCompletedFolderAsync(string? folder, string title, SportarrDbContext? db)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            var rootFolders = new List<string>();
            var clientFolders = new List<string>();
            var categoryNames = new List<string>();
            if (db != null)
            {
                rootFolders.AddRange(await db.RootFolders.Select(r => r.Path).ToListAsync());
                foreach (var client in await db.DownloadClients
                    .Select(c => new { c.Directory, c.BlackholeFolder, c.WatchFolder, c.Category, c.PostImportCategory })
                    .ToListAsync())
                {
                    clientFolders.Add(client.Directory ?? "");
                    clientFolders.Add(client.BlackholeFolder ?? "");
                    clientFolders.Add(client.WatchFolder ?? "");
                    categoryNames.Add(client.Category ?? "");
                    categoryNames.Add(client.PostImportCategory ?? "");
                    if (!string.IsNullOrWhiteSpace(client.Directory))
                    {
                        if (!string.IsNullOrWhiteSpace(client.Category))
                            clientFolders.Add(Path.Combine(client.Directory, client.Category));
                        if (!string.IsNullOrWhiteSpace(client.PostImportCategory))
                            clientFolders.Add(Path.Combine(client.Directory, client.PostImportCategory));
                    }
                }
            }

            if (!LeftoverFolderPolicy.IsSafeTarget(folder, rootFolders, clientFolders, categoryNames, out var full) || full == null)
            {
                logger.LogDebug("[Enhanced Download Monitor] Leaving the leftover folder alone for {Title}: {Folder}", title, folder);
                return;
            }

            Directory.Delete(full, recursive: true);
            logger.LogInformation("[Enhanced Download Monitor] Removed the leftover download folder for {Title}: {Folder}", title, full);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Enhanced Download Monitor] Could not remove the leftover folder for {Title}", title);
        }
    }

    internal static bool HasReachedSeedLimit(DownloadClientStatus status, Indexer indexer)
    {
        // Check ratio limit
        if (indexer.SeedRatio.HasValue && indexer.SeedRatio.Value > 0)
        {
            if ((status.Ratio ?? 0) < indexer.SeedRatio.Value)
                return false;
        }

        // Check time limit (SeedTime is in minutes)
        if (indexer.SeedTime.HasValue && indexer.SeedTime.Value > 0)
        {
            var seedingMinutes = status.CompletedAt.HasValue
                ? (DateTime.UtcNow - status.CompletedAt.Value).TotalMinutes
                : 0;

            if (seedingMinutes < indexer.SeedTime.Value)
                return false;
        }

        return true;
    }
}
