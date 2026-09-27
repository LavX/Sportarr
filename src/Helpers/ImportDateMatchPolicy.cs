using Sportarr.Api.Models;

namespace Sportarr.Api.Helpers;

internal static class ImportDateMatchPolicy
{
    public static ImportDateMatchResult Evaluate(Event evt, DateTime parsedDate)
    {
        var eventDate = (evt.BroadcastDate ?? evt.EventDate.Date).Date;
        var daysDifference = (int)Math.Abs((eventDate - parsedDate.Date).TotalDays);

        if (daysDifference == 0)
            return new ImportDateMatchResult(eventDate, daysDifference, 20, false);

        if (evt.BroadcastDateVerified && evt.HomeTeamId.HasValue && evt.AwayTeamId.HasValue)
            return new ImportDateMatchResult(eventDate, daysDifference, 0, true);

        var score = daysDifference switch
        {
            <= 1 => 10,
            <= 3 => 8,
            <= 7 => 5,
            _ => 0
        };

        return new ImportDateMatchResult(eventDate, daysDifference, score, false);
    }
}

internal readonly record struct ImportDateMatchResult(
    DateTime EventDate,
    int DaysDifference,
    int Score,
    bool Reject);
