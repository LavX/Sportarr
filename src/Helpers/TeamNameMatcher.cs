namespace Sportarr.Api.Helpers;

internal static class TeamNameMatcher
{
    public static bool ContainsRegularPluralVariant(string normalizedText, string normalizedTerm)
    {
        return TryCreateRegularPluralVariant(normalizedTerm, out var variant)
            && ContainsWholeTerm(normalizedText, variant);
    }

    public static bool AllowsRegularPluralVariant(
        string normalizedRelease,
        bool titleNamesEventLeague,
        string normalizedHomeTeam,
        string normalizedAwayTeam)
    {
        if (titleNamesEventLeague)
            return true;

        // A no-league title is safe only when both matchup sides name this event's teams.
        // This two-team requirement keeps Athletic Bilbao from matching Oakland Athletics.
        // Do not weaken it to a one-team identity check.
        return IsTeamLedMatchup(normalizedRelease, normalizedHomeTeam, normalizedAwayTeam);
    }

    private static bool TryCreateRegularPluralVariant(string normalizedTerm, out string variant)
    {
        variant = "";
        if (string.IsNullOrWhiteSpace(normalizedTerm))
            return false;

        var lastWordLength = normalizedTerm.Length - normalizedTerm.LastIndexOf(' ') - 1;
        if (normalizedTerm.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            // Removing s needs one extra character because the generated word is shorter.
            // This keeps both sides of the comparison at four characters or more.
            if (lastWordLength < 5 || normalizedTerm.EndsWith("ss", StringComparison.OrdinalIgnoreCase)
                || normalizedTerm.EndsWith("us", StringComparison.OrdinalIgnoreCase)
                || normalizedTerm.EndsWith("is", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            variant = normalizedTerm[..^1];
        }
        else
        {
            if (lastWordLength < 4)
                return false;

            variant = normalizedTerm + "s";
        }

        return true;
    }

    private static bool IsTeamLedMatchup(string title, string homeTeam, string awayTeam)
    {
        foreach (var separator in new[] { " vs ", " v ", " @ ", " at " })
        {
            var separatorIndex = title.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (separatorIndex < 0)
                continue;

            var left = title[..separatorIndex].Trim();
            var right = title[(separatorIndex + separator.Length)..].Trim();
            if ((IsWholeTeamSide(left, homeTeam) && StartsWithTeamSide(right, awayTeam))
                || (IsWholeTeamSide(left, awayTeam) && StartsWithTeamSide(right, homeTeam)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWholeTeamSide(string side, string teamName)
    {
        return side.Equals(teamName, StringComparison.OrdinalIgnoreCase)
            || (TryCreateRegularPluralVariant(teamName, out var variant)
                && side.Equals(variant, StringComparison.OrdinalIgnoreCase));
    }

    private static bool StartsWithTeamSide(string side, string teamName)
    {
        if (StartsWithTerm(side, teamName))
            return true;

        return TryCreateRegularPluralVariant(teamName, out var variant)
            && StartsWithTerm(side, variant);
    }

    private static bool StartsWithTerm(string side, string term)
    {
        if (!side.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            return false;

        if (side.Length == term.Length)
            return true;

        return !char.IsLetterOrDigit(side[term.Length]);
    }

    private static bool ContainsWholeTerm(string text, string term)
    {
        var searchStart = 0;
        while (searchStart <= text.Length - term.Length)
        {
            var matchIndex = text.IndexOf(term, searchStart, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
                return false;

            var endIndex = matchIndex + term.Length;
            var startsAtBoundary = matchIndex == 0 || !char.IsLetterOrDigit(text[matchIndex - 1]);
            var endsAtBoundary = endIndex == text.Length || !char.IsLetterOrDigit(text[endIndex]);
            if (startsAtBoundary && endsAtBoundary)
                return true;

            searchStart = matchIndex + 1;
        }

        return false;
    }
}
