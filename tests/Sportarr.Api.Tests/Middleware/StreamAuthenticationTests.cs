using FluentAssertions;
using Sportarr.Api.Middleware;
using Xunit;

namespace Sportarr.Api.Tests.Middleware;

public class StreamAuthenticationTests
{
    [Theory]
    [InlineData("/api/v1/stream/abc123/playlist.m3u8")]
    [InlineData("/api/v1/stream/abc123/segment001.ts")]
    public void Hls_assets_are_public_for_media_players(string path)
    {
        DynamicAuthenticationMiddleware.IsPublicPath(path, "GET").Should().BeTrue();
    }

    [Theory]
    [InlineData("POST", "/api/v1/stream/123/start")]
    [InlineData("POST", "/api/v1/stream/123/stop")]
    [InlineData("GET", "/api/v1/stream/sessions")]
    [InlineData("GET", "/api/v1/stream/123/start")]
    public void Stream_control_routes_require_authentication(string method, string path)
    {
        DynamicAuthenticationMiddleware.IsPublicPath(path, method).Should().BeFalse();
    }
}
