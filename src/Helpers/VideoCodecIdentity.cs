namespace Sportarr.Api.Helpers;

public static class VideoCodecIdentity
{
    public static bool Matches(string? reference, string? candidate)
    {
        if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(candidate))
            return true;

        return Normalize(reference) == Normalize(candidate);
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant() switch
    {
        "X264" or "H264" or "H.264" or "AVC" or "AVC1" => "H264",
        "X265" or "H265" or "H.265" or "HEVC" => "H265",
        var other => other
    };
}
