using FluentAssertions;
using Sportarr.Api.Helpers;

namespace Sportarr.Api.Tests.Services;

public class TorrentHashHelperTests
{
    [Fact]
    public void ResolveTrackedInfoHash_prefers_the_indexer_hash()
    {
        TorrentHashHelper.ResolveTrackedInfoHash(
                "Torrent",
                "indexer-hash",
                "0123456789abcdef0123456789abcdef01234567")
            .Should().Be("indexer-hash");
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void ResolveTrackedInfoHash_uses_a_valid_torrent_client_hash(string downloadId)
    {
        TorrentHashHelper.ResolveTrackedInfoHash("Torrent", null, downloadId)
            .Should().Be(downloadId);
    }

    [Theory]
    [InlineData("Usenet", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("Torrent", "not-a-hash")]
    [InlineData("Torrent", "0123456789abcdef0123456789abcdef0123456g")]
    public void ResolveTrackedInfoHash_does_not_treat_other_client_ids_as_torrent_hashes(
        string protocol,
        string downloadId)
    {
        TorrentHashHelper.ResolveTrackedInfoHash(protocol, null, downloadId).Should().BeNull();
    }
}
