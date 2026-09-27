using System.Text.RegularExpressions;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

public static class EventFolderGroupResolver
{
    private const string WwePleNames = @"\b(?:WrestleMania|Royal\s+Rumble|SummerSlam|Survivor\s+Series|Money\s+in\s+the\s+Bank|Elimination\s+Chamber|Backlash|Clash\s+at\s+the\s+Castle|Night\s+of\s+Champions|Crown\s+Jewel|Bad\s+Blood|Bash\s+in\s+Berlin|Hell\s+in\s+a\s+Cell|Extreme\s+Rules|Payback|Fastlane|Evolution)\b";

    public static string? Resolve(Event eventInfo)
    {
        var leagueName = eventInfo.League?.Name ?? eventInfo.ApiLeagueName;
        if (string.IsNullOrWhiteSpace(leagueName))
            return null;

        if (EventPartDetector.IsMotorsport(eventInfo.Sport))
        {
            if (EventPartDetector.GetMotorsportSessionTypes(leagueName).Count == 0)
                return null;

            return EventPartDetector.DetectMotorsportSessionType(eventInfo.Title, leagueName) ?? "Other";
        }

        if (!EventPartDetector.IsFightingSport(eventInfo.Sport) ||
            EventPartDetector.GetFightingEventTypes(leagueName).Count == 0)
            return null;

        var title = eventInfo.Title.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
        var promotion = EventPartDetector.DetectWrestlingPromotion(leagueName);
        if (promotion == EventPartDetector.WrestlingPromotion.Wwe)
            return ResolveWwe(title);

        var typeName = EventPartDetector.DetectFightingEventTypeName(title, leagueName);
        if (promotion == EventPartDetector.WrestlingPromotion.Aew && typeName == "Weekly")
        {
            var show = Regex.Match(title, @"\b(Dynamite|Rampage|Collision)\b", RegexOptions.IgnoreCase);
            if (show.Success)
                return CanonicalShowName(show.Value);
        }

        return typeName switch
        {
            "PPV" => "PPV",
            "FightNight" => "Fight Night",
            "ContenderSeries" => "Contender Series",
            "FridayFights" => "Friday Fights",
            "Numbered" => "Numbered Event",
            "Weekly" => "Weekly",
            "Special" => "Special",
            _ => "Other"
        };
    }

    private static string ResolveWwe(string title)
    {
        if (Regex.IsMatch(title, @"\b(?:Saturday\s+Night['’]?s\s+Main\s+Event|SNME)\b", RegexOptions.IgnoreCase))
            return "Saturday Night's Main Event";

        var type = EventPartDetector.DetectWweEventType(title);
        if (type == EventPartDetector.WweEventType.NxtSpecial)
            return "PLE";

        if (type == EventPartDetector.WweEventType.Weekly)
        {
            var show = Regex.Match(title, @"\b(SmackDown|Raw|NXT|Main\s+Event|Evolve)\b", RegexOptions.IgnoreCase);
            if (show.Success)
                return CanonicalShowName(show.Value);
        }

        if (Regex.IsMatch(title, WwePleNames, RegexOptions.IgnoreCase))
            return "PLE";

        return "Other";
    }

    private static string CanonicalShowName(string show) => show.ToLowerInvariant() switch
    {
        "raw" => "RAW",
        "smackdown" => "SmackDown",
        "nxt" => "NXT",
        "main event" => "Main Event",
        "evolve" => "Evolve",
        "dynamite" => "Dynamite",
        "rampage" => "Rampage",
        "collision" => "Collision",
        _ => show
    };
}
