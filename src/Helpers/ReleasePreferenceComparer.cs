using Sportarr.Api.Models;

namespace Sportarr.Api.Helpers;

public static class ReleasePreferenceComparer
{
    public static int Compare(
        QualityProfile? profile,
        string? leftQuality,
        string? leftTitle,
        int leftCustomFormatScore,
        string? rightQuality,
        string? rightTitle,
        int rightCustomFormatScore,
        string? propersSetting)
    {
        var quality = QualityProfileRanker.Compare(profile, leftQuality, rightQuality);
        if (quality != 0)
        {
            return quality;
        }

        if (!string.Equals(propersSetting, "doNotPrefer", StringComparison.OrdinalIgnoreCase))
        {
            var revision = ReleaseRevision.Parse(leftTitle ?? leftQuality)
                .CompareTo(ReleaseRevision.Parse(rightTitle ?? rightQuality));
            if (revision != 0)
            {
                return revision;
            }
        }

        return leftCustomFormatScore.CompareTo(rightCustomFormatScore);
    }
}
