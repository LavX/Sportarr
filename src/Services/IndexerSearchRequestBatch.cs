using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sportarr.Api.Helpers;

namespace Sportarr.Api.Services;

internal sealed class IndexerSearchRequestBatch : IDisposable
{
    internal const int MaxSharedBodyBytes = 1024 * 1024;
    internal const int MaxRetainedBytes = 8 * MaxSharedBodyBytes;
    internal const int MaxEntries = 64;
    private readonly int _maxSharedBodyBytes;
    private readonly int _maxRetainedBytes;
    private readonly int _maxEntries;
    private readonly object _gate = new();
    private readonly Dictionary<string, RequestEntry> _requests = new();
    private int _reservedBytes;

    internal IndexerSearchRequestBatch(
        int maxSharedBodyBytes = MaxSharedBodyBytes,
        int maxRetainedBytes = MaxRetainedBytes,
        int maxEntries = MaxEntries)
    {
        _maxSharedBodyBytes = maxSharedBodyBytes;
        _maxRetainedBytes = maxRetainedBytes;
        _maxEntries = maxEntries;
    }

    internal async Task<HttpResponseMessage> SendAsync(HttpClient client, string protocol, int delayMs,
        HttpRequestMessage request, HttpCompletionOption completionOption)
    {
        if (request.Method != HttpMethod.Get || request.Content != null)
            return await client.SendAsync(request, completionOption);

        var key = RequestKey(client, protocol, delayMs, request);
        int? rowId = request.Options.TryGetValue(IndexerQueryRequest.RowId, out var savedRowId) && savedRowId > 0
            ? savedRowId : null;
        RequestEntry? entry;
        lock (_gate)
        {
            if (!_requests.TryGetValue(key, out entry)
                && _requests.Count < _maxEntries && _reservedBytes <= _maxRetainedBytes - _maxSharedBodyBytes)
            {
                entry = new();
                _requests.Add(key, entry);
                _reservedBytes += _maxSharedBodyBytes;
            }
        }
        if (entry == null)
            return await client.SendAsync(request, completionOption);

        while (true)
        {
            Generation generation;
            var owner = false;
            lock (_gate)
            {
                if (rowId.HasValue && entry.DeniedRows.TryGetValue(rowId.Value, out var ownDenial))
                    throw ownDenial;

                if (entry.Current == null)
                {
                    if (entry.LastDenial != null && (!rowId.HasValue || entry.AttemptedRows.Contains(rowId.Value)))
                        throw entry.LastDenial;
                    if (rowId.HasValue) entry.AttemptedRows.Add(rowId.Value);
                    entry.Current = new(rowId);
                    owner = true;
                }
                generation = entry.Current;
            }

            HttpResponseMessage? original = null;
            if (owner)
            {
                var retainedBytes = 0;
                var localDenial = false;
                try
                {
                    original = await client.SendAsync(request, completionOption);
                    var snapshot = await TrySnapshotAsync(original);
                    if (snapshot != null)
                    {
                        retainedBytes = snapshot.Body.Length;
                        original.Dispose();
                        original = null;
                    }
                    generation.Completion.SetResult(new(snapshot, null));
                }
                catch (IndexerQueryAdmissionException exception) when (
                    exception.Kind == QueryAdmissionFailure.Denied && generation.OwnerRowId == exception.IndexerId)
                {
                    original?.Dispose();
                    original = null;
                    localDenial = true;
                    lock (_gate)
                    {
                        entry.DeniedRows[exception.IndexerId] = exception;
                        entry.LastDenial = exception;
                        entry.Current = null;
                    }
                    generation.Completion.SetResult(new(null, exception));
                }
                catch (Exception exception)
                {
                    original?.Dispose();
                    original = null;
                    generation.Completion.SetException(exception);
                }
                finally
                {
                    // Keep one bounded body reservation for a replacement owner.
                    if (!localDenial)
                        lock (_gate) _reservedBytes -= _maxSharedBodyBytes - retainedBytes;
                }
            }

            var shared = await generation.Completion.Task;
            if (shared.LocalDenial != null)
            {
                if (!rowId.HasValue || rowId.Value == shared.LocalDenial.IndexerId)
                    throw shared.LocalDenial;
                continue;
            }
            if (shared.Snapshot != null) return shared.Snapshot.CreateResponse();
            // The leader keeps its body when this response cannot be retained.
            return original ?? await client.SendAsync(request, completionOption);
        }
    }

    private sealed class RequestEntry
    {
        internal Generation? Current { get; set; }
        internal HashSet<int> AttemptedRows { get; } = new();
        internal Dictionary<int, IndexerQueryAdmissionException> DeniedRows { get; } = new();
        internal IndexerQueryAdmissionException? LastDenial { get; set; }
    }

    private sealed class Generation(int? ownerRowId)
    {
        internal int? OwnerRowId { get; } = ownerRowId;
        internal TaskCompletionSource<SharedResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record SharedResult(ResponseSnapshot? Snapshot, IndexerQueryAdmissionException? LocalDenial);

    internal static string RequestKey(HttpClient client, string protocol, int delayMs, HttpRequestMessage request)
    {
        var headers = request.Headers.Concat(client.DefaultRequestHeaders.Where(header => !request.Headers.Contains(header.Key)))
            // These headers control local pacing, not the indexer query.
            .Where(header => !header.Key.Equals("X-Indexer-Id", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("X-Rate-Limit-Ms", StringComparison.OrdinalIgnoreCase))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => new { Name = header.Key.ToLowerInvariant(), Values = header.Value.ToArray() });
        var identity = JsonSerializer.Serialize(new
        {
            protocol, Method = request.Method.Method, Uri = request.RequestUri!.OriginalString,
            request.Version, request.VersionPolicy, Delay = delayMs > 0 ? delayMs : 2000,
            client.Timeout, client.MaxResponseContentBufferSize, Headers = headers
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private async Task<ResponseSnapshot?> TrySnapshotAsync(HttpResponseMessage response)
    {
        // Both protocol clients reject HTTP errors before reading the body.
        if (!response.IsSuccessStatusCode) return ResponseSnapshot.From(response, Array.Empty<byte>());
        if (response.Content.Headers.ContentLength > _maxSharedBodyBytes) return null;

        var content = response.Content;
        var stream = await content.ReadAsStreamAsync();
        using var deadline = new CancellationTokenSource(BoundedHttpContent.DefaultReadTimeout);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (buffer.Length <= _maxSharedBodyBytes)
        {
            var remaining = _maxSharedBodyBytes + 1 - (int)buffer.Length;
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), deadline.Token);
            if (read == 0) return ResponseSnapshot.From(response, buffer.ToArray());
            buffer.Write(chunk, 0, read);
        }

        // Replay the consumed prefix so the leader still parses the full feed.
        var replacement = new StreamContent(new PrefixReadStream(buffer.ToArray(), stream, content));
        foreach (var header in content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _requests.Clear();
            _reservedBytes = 0;
        }
    }

    private sealed record ResponseSnapshot(HttpStatusCode Status, System.Version Version, string? Reason,
        KeyValuePair<string, string[]>[] Headers, KeyValuePair<string, string[]>[] ContentHeaders, byte[] Body)
    {
        internal static ResponseSnapshot From(HttpResponseMessage response, byte[] body) => new(
            response.StatusCode, response.Version, response.ReasonPhrase,
            response.Headers.Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray(),
            response.Content.Headers.Where(header => !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray(), body);

        internal HttpResponseMessage CreateResponse()
        {
            var response = new HttpResponseMessage(Status) { Version = Version, ReasonPhrase = Reason, Content = new ByteArrayContent((byte[])Body.Clone()) };
            foreach (var header in Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var header in ContentHeaders) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
    }

    private sealed class PrefixReadStream(byte[] prefix, Stream tail, HttpContent owner) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var copied = ReadPrefix(buffer.AsSpan(offset, count));
            return copied > 0 ? copied : tail.Read(buffer, offset, count);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copied = ReadPrefix(buffer.Span);
            return copied > 0 ? copied : await tail.ReadAsync(buffer, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private int ReadPrefix(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) owner.Dispose();
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
