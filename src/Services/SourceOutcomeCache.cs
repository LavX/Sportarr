using System.Text.Json;

namespace Sportarr.Api.Services;

internal sealed class SourceOutcomeCache(TimeProvider? timeProvider = null)
{
    private const int MaxEntries = 256;
    private const int MaxBytes = 16 * 1024 * 1024;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _bytes;
    private readonly SemaphoreSlim[] _fills = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private sealed record Entry(byte[] Payload, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
    private sealed record Snapshot(SearchResultCache.RawRelease[] Releases, SearchTermination Termination,
        int RawCursor, SearchPageObservation[] Pages, bool KnownPageLimit);

    internal IndexerSearchOutcome? TryGet(string key, int lifetimeSeconds)
    {
        if (lifetimeSeconds <= 0) return null;
        byte[] payload;
        DateTimeOffset expiresAt;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            var requestedExpiry = entry.CreatedAt.AddSeconds(lifetimeSeconds);
            expiresAt = entry.ExpiresAt < requestedExpiry ? entry.ExpiresAt : requestedExpiry;
            if (_clock.GetUtcNow() >= expiresAt)
            {
                if (_clock.GetUtcNow() >= entry.ExpiresAt) Remove(key);
                return null;
            }
            payload = entry.Payload;
        }
        var snapshot = JsonSerializer.Deserialize<Snapshot>(payload)!;
        return new(snapshot.Releases.Select(row => row.ToSearchResult()).ToList(), snapshot.Termination,
            snapshot.RawCursor, snapshot.Pages, snapshot.KnownPageLimit) { CacheExpiresAt = expiresAt };
    }

    internal void Store(string key, IndexerSearchOutcome outcome, int lifetimeSeconds)
    {
        if (lifetimeSeconds <= 0 || outcome.Failure != null ||
            !(outcome.SatisfiesRequest || outcome.Termination is SearchTermination.UnknownTail or SearchTermination.PageCeiling)) return;
        var snapshot = new Snapshot(outcome.Releases.Select(SearchResultCache.RawRelease.FromSearchResult).ToArray(),
            outcome.Termination, outcome.RawCursor, outcome.Pages.ToArray(), outcome.KnownPageLimit);
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (payload.Length > MaxBytes) return;
        if (snapshot.Releases.Length == 0)
            lifetimeSeconds = Math.Min(lifetimeSeconds, SearchResultCache.EmptyResultLifetimeSeconds);
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            var expiresAt = now.AddSeconds(lifetimeSeconds);
            if (outcome.CacheExpiresAt is { } deadline && deadline < expiresAt) expiresAt = deadline;
            if (expiresAt <= now) return;
            Remove(key);
            CleanupExpired();
            while (_entries.Count >= MaxEntries || _bytes + payload.Length > MaxBytes)
                Remove(_entries.MinBy(pair => pair.Value.CreatedAt).Key);
            _entries.Add(key, new(payload, now, expiresAt));
            _bytes += payload.Length;
        }
    }

    internal async Task<IDisposable> EnterFillAsync(string key)
    {
        var gate = _fills[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_fills.Length)];
        await gate.WaitAsync();
        return new FillLease(gate);
    }

    private sealed class FillLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    internal void Invalidate(string key)
    {
        lock (_sync) Remove(key);
    }

    internal void CleanupExpired()
    {
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            foreach (var key in _entries.Where(pair => now >= pair.Value.ExpiresAt)
                         .Select(pair => pair.Key).ToArray())
                Remove(key);
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _bytes = 0;
        }
    }

    private void Remove(string key)
    {
        if (_entries.Remove(key, out var entry)) _bytes -= entry.Payload.Length;
    }
}
