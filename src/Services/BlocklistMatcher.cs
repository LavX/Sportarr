using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

public sealed class BlocklistMatcher
{
    private readonly List<BlocklistItem> _items;

    public BlocklistMatcher(IEnumerable<BlocklistItem> items)
    {
        _items = items
            .Where(item => item.Protocol == "Usenet" || string.IsNullOrEmpty(item.TorrentInfoHash))
            .ToList();
    }

    public bool MatchesTitleIdentity(string title, string? indexer, string? protocol) =>
        MatchTitleIdentity(title, indexer, protocol) != null;

    public BlocklistItem? MatchTitleIdentity(string title, string? indexer, string? protocol) =>
        _items.FirstOrDefault(item =>
            string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(item.Indexer) ||
             string.Equals(item.Indexer, "Unknown", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Indexer, indexer, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(item.Protocol) ||
             string.Equals(item.Protocol, protocol, StringComparison.OrdinalIgnoreCase)));
}
