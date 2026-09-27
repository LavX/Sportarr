using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Helpers;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

internal static class PackImportBoundary
{
    internal static bool IsPackRelease(string title, bool explicitPack = false, string? leagueId = null, string? eventId = null) =>
        explicitPack || ReleaseTypeDetector.DetectForImport(title,
            string.IsNullOrWhiteSpace(leagueId) ? SportarrIdToken.ExtractLeagueId(title) : leagueId,
            string.IsNullOrWhiteSpace(eventId) ? SportarrIdToken.ExtractEventId(title) : eventId) == ReleaseType.Pack;

    internal const string WarningPrefix = "Pack member unresolved: ";
    internal static bool IsHeld(DownloadQueueItem item) => item.IsPack && item.Status == DownloadStatus.ImportWarning &&
        item.ErrorMessage?.StartsWith(WarningPrefix, StringComparison.Ordinal) == true;
    internal static bool CanRetryImport(DownloadQueueItem item) => item.Progress >= 100 &&
        (item.Status == DownloadStatus.Failed || IsHeld(item));

    internal static async Task<List<DownloadQueueItem>> ReadOwnersAsync(SportarrDbContext db, DownloadQueueItem item) =>
        await db.DownloadQueue.AsNoTracking().Include(x => x.Event).ThenInclude(x => x.League)
            .Where(x => x.DownloadClientId == item.DownloadClientId && x.DownloadId == item.DownloadId)
            .ToListAsync();

    internal static string? OwnershipError(DownloadQueueItem item, IReadOnlyList<DownloadQueueItem> owners)
    {
        if (item.Id == 0 || !item.DownloadClientId.HasValue || string.IsNullOrWhiteSpace(item.DownloadId))
            return "The directory has no persisted client job identity.";
        if (owners.Count == 0 || owners.All(x => x.Id != item.Id) || owners.Any(x => !x.IsPack || x.Event == null))
            return "The persisted job owners are inconsistent.";
        if (owners.Select(x => x.PackGroupId).Distinct().Count() != 1 ||
            owners.Select(x => x.EventId).Distinct().Count() != owners.Count)
            return "The persisted pack group or event ownership is ambiguous.";
        var ids = owners.Select(x => SportarrIdToken.Normalize(x.Event.ExternalId)).Where(x => x != null).ToList();
        return ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
            ? "More than one owner has the same event identity." : null;
    }

    internal static (string? File, string? Error) SelectMember(
        DownloadQueueItem item, IReadOnlyList<DownloadQueueItem> owners, IReadOnlyList<string> files)
    {
        var error = OwnershipError(item, owners);
        if (error != null) return (null, error);
        var candidates = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var ids = SportarrIdToken.ExtractEventIds(name);
            if (ids.Count > 1)
            {
                if (owners.Any(owner => owner.EventId == item.EventId && ids.Contains(SportarrIdToken.Normalize(owner.Event.ExternalId))))
                    return (null, "A member contains conflicting event identity tokens for this owner.");
                continue;
            }
            var matches = ids.Count == 1
                ? owners.Where(x => SportarrIdToken.Normalize(x.Event.ExternalId) == ids[0]).ToList()
                : owners.Where(x => MatchesParticipants(name, x.Event)).ToList();
            if (matches.Count > 1)
            {
                if (matches.Any(owner => owner.EventId == item.EventId))
                    return (null, "A member identifies this event and another job owner.");
                continue;
            }
            if (matches.Count == 1 && matches[0].EventId == item.EventId) candidates.Add(file);
        }
        return candidates.Count == 1 ? (candidates[0], null) :
            (null, candidates.Count == 0 ? "No member uniquely identifies this event." : "Multiple members identify this event.");
    }

    private static bool MatchesParticipants(string filename, Event owner)
    {
        if (string.IsNullOrWhiteSpace(owner.HomeTeamName) || string.IsNullOrWhiteSpace(owner.AwayTeamName) ||
            string.IsNullOrWhiteSpace(owner.League?.Name)) return false;
        var name = Tokens(filename);
        if (!Contains(name, owner.HomeTeamName) || !Contains(name, owner.AwayTeamName) || !Contains(name, owner.League.Name))
            return false;
        var dates = Regex.Matches(filename, @"(?<!\d)(\d{4})[._ -](\d{2})[._ -](\d{2})(?!\d)")
            .Select(x => x.Groups[1].Value + "-" + x.Groups[2].Value + "-" + x.Groups[3].Value).Distinct().ToList();
        return dates.Count == 1 && DateTime.TryParseExact(dates[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) && date.Date == (owner.BroadcastDate ?? owner.EventDate).Date;
    }

    private static string Tokens(string text) => " " + Regex.Replace(text.Normalize(NormalizationForm.FormC).ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}]+", " ").Trim() + " ";
    private static bool Contains(string text, string evidence)
    {
        var token = Tokens(evidence);
        return token.Trim().Length > 0 && text.Contains(token, StringComparison.Ordinal);
    }

}
