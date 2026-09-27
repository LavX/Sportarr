using MonoTorrent;

namespace Sportarr.Api.Helpers;

/// <summary>
/// Computes a torrent's BitTorrent v1 infohash locally, from either a magnet link
/// or raw .torrent file bytes.
///
/// Why this exists: the download id stored for a grab must be the REAL torrent so
/// that later status lookups, removal, and "delete data" operations target the
/// correct download. Deriving the hash from the indexer payload at add time is
/// deterministic. The alternative, asking the download client "what did I just
/// add?" and guessing (e.g. the most-recently-added torrent), can return an
/// unrelated torrent and cause the wrong data to be removed later.
///
/// All methods are total: they never throw, returning null when the input is not a
/// valid v1 torrent/magnet (including v2-only torrents, which carry no v1 hash).
/// The returned hash is uppercase hex (40 chars), matching rTorrent's own format.
/// </summary>
public static class TorrentHashHelper
{
    public static string? ResolveTrackedInfoHash(
        string? protocol,
        string? indexerInfoHash,
        string? downloadId)
    {
        if (!string.IsNullOrWhiteSpace(indexerInfoHash))
            return indexerInfoHash;

        if (!string.Equals(protocol, "Torrent", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(downloadId) ||
            (downloadId.Length != 40 && downloadId.Length != 64) ||
            !downloadId.All(Uri.IsHexDigit))
            return null;

        return downloadId;
    }

    public static bool IsMagnet(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parse the v1 infohash (uppercase hex) from a magnet link. Returns null if the
    /// magnet is malformed or only carries a v2 (btmh) hash.
    /// </summary>
    public static string? TryGetHashFromMagnet(string? magnetUrl)
    {
        if (string.IsNullOrEmpty(magnetUrl)) return null;

        try
        {
            if (MagnetLink.TryParse(magnetUrl, out var magnet) && magnet != null)
            {
                return magnet.InfoHashes.V1?.ToHex();
            }
        }
        catch
        {
            // Malformed magnet; fall through to null.
        }

        return null;
    }

    /// <summary>
    /// Compute the v1 infohash (uppercase hex) from raw .torrent file bytes. Returns
    /// null if the bytes are not a valid torrent or it is a v2-only torrent.
    /// </summary>
    public static string? TryGetHashFromTorrentBytes(byte[]? torrentBytes)
    {
        if (torrentBytes == null || torrentBytes.Length == 0) return null;

        try
        {
            var torrent = Torrent.Load(torrentBytes);
            return torrent.InfoHashes.V1?.ToHex();
        }
        catch
        {
            // Not a valid torrent file; fall through to null.
        }

        return null;
    }
}
