using System.Text.RegularExpressions;

namespace Sportarr.Api.Helpers;

public static class ReleaseGroupParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Regex Extension = new(@"\.(?:mkv|mp4|avi|m4v|ts|m2ts|mts|wmv|mpg|mpeg|iso|bdmv|img|vob|flv|nzb|par2)$", Options);
    private static readonly Regex DashedGroup = new(@"-([A-Za-z0-9]+)(?:\[[^\]\r\n]*\])*(?:\.[a-z]{2,4})?$", Options);
    private static readonly Regex BracketedGroup = new(@"\[([A-Za-z0-9]+)\](?:\.[a-z]{2,4})?$", Options);

    // Unmarked names need a known group and a codec boundary. Broadcaster names use the same position.
    private static readonly Regex TrackerGroup = new(
        @"(?:^|[ ._])(?:[xh][ ._]?26[45]|HEVC|AVC)[ ._]+(?<group>BILLIE|egortech|nVa)(?:[ ._]+(?:720|1080|2160)pEN(?:25|30|50|60)fps)?$", Options);

    public static string? Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var name = title.Trim();
        var explicitGroup = DashedGroup.Match(name);
        if (!explicitGroup.Success) explicitGroup = BracketedGroup.Match(name);
        if (!explicitGroup.Success)
        {
            name = Extension.Replace(name, "");
            explicitGroup = DashedGroup.Match(name);
            if (!explicitGroup.Success) explicitGroup = BracketedGroup.Match(name);
        }
        if (explicitGroup.Success)
        {
            var group = explicitGroup.Groups[1].Value;
            return LooksLikeQualityToken(group) ? null : group;
        }

        var trackerGroup = TrackerGroup.Match(name);
        return trackerGroup.Success ? trackerGroup.Groups["group"].Value : null;
    }

    private static bool LooksLikeQualityToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return true;
        var t = token.ToUpperInvariant();

        if (Regex.IsMatch(t, @"^(360|480|540|576|720|1080|1440|2160)P?I?$")) return true;
        if (t is "4K" or "UHD" or "FHD" or "HD" or "SD" or "QHD" or "FULLHD") return true;
        if (t is "WEBDL" or "WEB" or "WEBRIP" or "WEBHD" or "WEBCAP" or "WEBMUX"
            or "BLURAY" or "BLU" or "BD" or "BDRIP" or "BRRIP" or "BDREMUX" or "BDMUX"
            or "HDDVD"
            or "HDTV" or "PDTV" or "SDTV" or "DSR" or "TVRIP"
            or "DVD" or "DVDRIP" or "DVDR" or "DVD5" or "DVD9"
            or "RAWHD" or "REMUX" or "VHSRIP"
            or "TS" or "TELESYNC" or "HDCAM" or "CAM" or "TELECINE"
            or "DL" or "RIP" or "MUX") return true;
        if (t is "X264" or "X265" or "H264" or "H265" or "HEVC" or "AVC"
            or "XVID" or "DIVX" or "AV1" or "VP9" or "MPEG2" or "MPEG4") return true;
        if (t is "AAC" or "AC3" or "EAC3" or "DD" or "DDP" or "DTS" or "DTSHD" or "DTSMA"
            or "TRUEHD" or "FLAC" or "MP3" or "OPUS" or "ATMOS"
            or "5" or "7" or "2") return true;

        return false;
    }

}
