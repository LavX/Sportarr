using System.Security.Cryptography;
using System.Text.Json;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

internal sealed class RawIndexerRetrievalCache(SourceOutcomeCache cache, int lifetimeSeconds,
    bool forceRefresh, string partition)
{
    internal async Task<IndexerSearchOutcome> GetOrFetchAsync(HttpClient client, Indexer config,
        HttpRequestMessage request, int maximum, TorznabCapabilities? caps, bool ambiguousPaging,
        Func<Task<IndexerSearchOutcome>> fetch)
    {
        if (lifetimeSeconds <= 0) return await fetch();
        var requestKey = IndexerSearchRequestBatch.RequestKey(client, config.Type.ToString(), config.RequestDelayMs, request);
        var key = "retrieval:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            partition, requestKey, maximum, caps?.MaxPageSize, caps?.DefaultPageSize, ambiguousPaging
        })));
        using var fill = await cache.EnterFillAsync(key);
        if (forceRefresh) cache.Invalidate(key);
        else if (cache.TryGet(key, lifetimeSeconds) is { } hit)
        {
            // Restore parser quality before the service applies current policy.
            foreach (var release in hit.Releases) release.Quality = release.SourceQuality;
            return hit with { FromCache = true };
        }
        var outcome = await fetch();
        foreach (var release in outcome.Releases) release.SourceQuality = release.Quality;
        cache.Store(key, outcome, lifetimeSeconds);
        var stored = cache.TryGet(key, lifetimeSeconds);
        return outcome with { CacheExpiresAt = stored?.CacheExpiresAt };
    }
}
