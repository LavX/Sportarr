using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

public enum QueryAdmissionFailure
{
    Denied,
    Persistence,
    Cancelled
}

public sealed class IndexerQueryAdmissionException : Exception
{
    public int IndexerId { get; }
    public QueryAdmissionFailure Kind { get; }

    public IndexerQueryAdmissionException(int indexerId, QueryAdmissionFailure kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        IndexerId = indexerId;
        Kind = kind;
    }
}

public sealed record IndexerQueryContext(int IndexerId);

public static class IndexerQueryRequest
{
    internal static readonly HttpRequestOptionsKey<int> RowId = new("Sportarr.QueryAttempt.RowId");

    public static HttpRequestMessage Create(Indexer indexer, string url, IndexerQueryContext? context = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Indexer-Id", indexer.Id.ToString());
        if (indexer.RequestDelayMs > 0)
            request.Headers.Add("X-Rate-Limit-Ms", indexer.RequestDelayMs.ToString());
        if (context != null)
        {
            if (context.IndexerId <= 0 || context.IndexerId != indexer.Id)
            {
                request.Dispose();
                throw new IndexerQueryAdmissionException(indexer.Id, QueryAdmissionFailure.Denied, "Query context does not identify this saved row");
            }
            request.Options.Set(RowId, context.IndexerId);
        }
        return request;
    }
}

public sealed class IndexerQueryQuotaHandler(IServiceProvider services, IRateLimitService? rateLimitService = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(IndexerQueryRequest.RowId, out var indexerId))
        {
            try
            {
                if (rateLimitService != null)
                {
                    var host = request.RequestUri?.Host ?? "unknown";
                    await rateLimitService.WaitAndPulseAsync(
                        host, indexerId.ToString(), RequestDelay(request),
                        token => services.GetRequiredService<IndexerStatusService>()
                            .ReserveQueryAttemptAsync(indexerId, token),
                        cancellationToken);
                }
                else
                {
                    // Retry invokes this handler again with the same request.
                    await services.GetRequiredService<IndexerStatusService>()
                        .ReserveQueryAttemptAsync(indexerId, cancellationToken);
                }
            }
            catch (IndexerQueryAdmissionException)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new IndexerQueryAdmissionException(indexerId, QueryAdmissionFailure.Cancelled,
                    "Query admission was cancelled before dispatch", ex);
            }
            catch (Exception ex)
            {
                throw new IndexerQueryAdmissionException(indexerId, QueryAdmissionFailure.Persistence,
                    "Could not initialize query admission", ex);
            }
        }
        else if (rateLimitService != null)
        {
            // Diagnostics and plain feeds still need transport pacing.
            var host = request.RequestUri?.Host ?? "unknown";
            var row = request.Headers.TryGetValues("X-Indexer-Id", out var values)
                ? values.FirstOrDefault()
                : null;
            await rateLimitService.WaitAndPulseAsync(host, row, RequestDelay(request), cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static TimeSpan RequestDelay(HttpRequestMessage request) =>
        request.Headers.TryGetValues("X-Rate-Limit-Ms", out var values)
        && int.TryParse(values.FirstOrDefault(), out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : RateLimitHandler.DefaultRateLimit;
}
