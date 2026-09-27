using Sportarr.Api.Helpers;
using Xunit;

namespace Sportarr.Api.Tests.Helpers;

public class VideoCodecIdentityTests
{
    [Theory]
    [InlineData("x264", "H.264")]
    [InlineData("H264", "avc")]
    [InlineData("AVC1", "x264")]
    [InlineData("x265", "H.265")]
    [InlineData("HEVC", "h265")]
    [InlineData(" av1 ", "AV1")]
    [InlineData("VP9", "vp9")]
    [InlineData(null, "H.264")]
    [InlineData("x264", null)]
    [InlineData("", "AV1")]
    public void MatchesEquivalentOrUnspecifiedCodec(string? reference, string? candidate)
    {
        Assert.True(VideoCodecIdentity.Matches(reference, candidate));
        Assert.True(VideoCodecIdentity.Matches(candidate, reference));
    }

    [Theory]
    [InlineData("x264", "H.265")]
    [InlineData("HEVC", "H.264")]
    [InlineData("AV1", "H.264")]
    [InlineData("VP9", "AV1")]
    [InlineData("x264-10bit", "H.264")]
    [InlineData("H.264 Hi10P", "x264")]
    [InlineData("unknown", "H.264")]
    [InlineData(" ", "H.264")]
    public void RejectsDifferentCodecsAndUnrecognizedQualifiers(string reference, string candidate)
    {
        Assert.False(VideoCodecIdentity.Matches(reference, candidate));
        Assert.False(VideoCodecIdentity.Matches(candidate, reference));
    }
}
