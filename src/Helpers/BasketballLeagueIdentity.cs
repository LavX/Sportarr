using System.Text.RegularExpressions;

namespace Sportarr.Api.Helpers;

internal static class BasketballLeagueIdentity
{
    private static readonly Regex WomensLeague = new(
        @"(?<![A-Za-z0-9])(?:WNBA|Women['’]?s[ ._-]+National[ ._-]+Basketball[ ._-]+Association)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MensLeague = new(
        @"(?<![A-Za-z0-9])(?:NBA|National[ ._-]+Basketball[ ._-]+Association)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static string? Detect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (WomensLeague.IsMatch(value)) return "WNBA";
        return MensLeague.IsMatch(value) ? "NBA" : null;
    }
}
