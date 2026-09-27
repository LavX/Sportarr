using FluentAssertions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class BlocklistMatcherTests
{
    private static readonly ReleaseSearchResult Release = new()
    {
        Guid = "release-id",
        DownloadUrl = "https://indexer.invalid/download/release-id",
        Title = "UFC.9999.Prelims.720p.WEB-DL",
        Protocol = "Usenet",
        Indexer = "Provider B"
    };

    [Theory]
    [InlineData(null)]
    [InlineData("Unknown")]
    [InlineData("unknown")]
    public void A_title_only_usenet_block_matches_when_the_source_indexer_is_unknown(string? indexer)
    {
        var blocked = new BlocklistItem
        {
            Title = Release.Title,
            Protocol = "Usenet",
            Indexer = indexer,
            Reason = BlocklistReason.ManualBlock
        };

        new BlocklistMatcher(new[] { blocked })
            .MatchesTitleIdentity(Release.Title, Release.Indexer, Release.Protocol).Should().BeTrue();
    }

    [Fact]
    public void A_known_indexer_block_does_not_hide_the_same_title_from_another_indexer()
    {
        var blocked = new BlocklistItem
        {
            Title = Release.Title,
            Protocol = "Usenet",
            Indexer = "Provider A",
            Reason = BlocklistReason.ManualBlock
        };

        new BlocklistMatcher(new[] { blocked })
            .MatchesTitleIdentity(Release.Title, Release.Indexer, Release.Protocol).Should().BeFalse();
    }

    [Fact]
    public void A_legacy_torrent_block_without_a_hash_matches_a_hashed_release_by_title_and_indexer()
    {
        var blocked = new BlocklistItem
        {
            Title = Release.Title,
            Protocol = "Torrent",
            Indexer = Release.Indexer,
            TorrentInfoHash = null,
            Reason = BlocklistReason.ManualBlock
        };

        new BlocklistMatcher(new[] { blocked })
            .MatchesTitleIdentity(Release.Title, Release.Indexer, "Torrent").Should().BeTrue();
    }

    [Fact]
    public void A_usenet_title_block_does_not_hide_a_torrent_with_the_same_title()
    {
        var blocked = new BlocklistItem
        {
            Title = Release.Title,
            Protocol = "Usenet",
            Indexer = Release.Indexer,
            Reason = BlocklistReason.ManualBlock
        };

        new BlocklistMatcher(new[] { blocked })
            .MatchesTitleIdentity(Release.Title, Release.Indexer, "Torrent").Should().BeFalse();
    }

    [Fact]
    public void MatchTitleIdentity_returns_the_matched_reason()
    {
        var blocked = new BlocklistItem
        {
            Title = Release.Title,
            Protocol = Release.Protocol,
            Indexer = Release.Indexer,
            Reason = BlocklistReason.ManualBlock,
            Message = "Deleted from file management"
        };

        new BlocklistMatcher(new[] { blocked })
            .MatchTitleIdentity(Release.Title, Release.Indexer, Release.Protocol)
            .Should().BeSameAs(blocked);
    }
}
