using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Sportarr.Api.Models;

namespace Sportarr.Api.Services;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SearchTermination
{
    Exhausted, CallerCeiling, PageCeiling, UnknownTail, InvalidMetadata,
    RepeatedPage, OffsetMismatch, ParseIncomplete, AmbiguousPaging,
    LocalDenied, LocalPersistence, LocalCancelled, ProviderRateLimited, ProviderFailure,
    Unavailable, NoSources
}

public sealed record SearchPageObservation(int RequestedOffset, int RequestedLimit, int RawCount,
    int ParsedCount, int? ReportedOffset, int? ReportedTotal, bool MetadataValid, string Fingerprint);

public sealed record IndexerSearchOutcome(List<ReleaseSearchResult> Releases, SearchTermination Termination,
    int RawCursor, IReadOnlyList<SearchPageObservation> Pages, bool KnownPageLimit = false,
    Exception? Failure = null)
{
    internal DateTimeOffset? CacheExpiresAt { get; init; }
    internal bool FromCache { get; init; }
    public bool SatisfiesRequest => Termination is SearchTermination.Exhausted or SearchTermination.CallerCeiling;

    public static IndexerSearchOutcome Failed(Exception failure) => new(new(), Classify(failure), 0,
        Array.Empty<SearchPageObservation>(), Failure: failure);

    internal static SearchTermination Classify(Exception failure) => failure switch
    {
        IndexerQueryAdmissionException { Kind: QueryAdmissionFailure.Denied } => SearchTermination.LocalDenied,
        IndexerQueryAdmissionException { Kind: QueryAdmissionFailure.Persistence } => SearchTermination.LocalPersistence,
        IndexerQueryAdmissionException { Kind: QueryAdmissionFailure.Cancelled } => SearchTermination.LocalCancelled,
        IndexerRateLimitException => SearchTermination.ProviderRateLimited,
        _ => SearchTermination.ProviderFailure
    };
}

public sealed record IndexerSearchDiagnostic(int IndexerId, string Name, string Query,
    SearchTermination Termination, int RawCursor, IReadOnlyList<SearchPageObservation> Pages,
    bool KnownPageLimit, bool SatisfiesRequest);

public sealed record SearchOperationOutcome(List<ReleaseSearchResult> Releases,
    IReadOnlyList<IndexerSearchDiagnostic> Diagnostics, bool SatisfiesRequest)
{
    internal DateTimeOffset? CacheExpiresAt { get; init; }
    // Reuse successful pages without claiming exhaustive coverage.
    public bool CanCache => SatisfiesRequest || (Diagnostics.Count > 0 && Diagnostics.All(diagnostic =>
        diagnostic.SatisfiesRequest || diagnostic.Termination is SearchTermination.UnknownTail or SearchTermination.PageCeiling));
}

internal static class IndexerSearchPaging
{
    internal const int MaxPages = 5;

    internal static string CapabilityKey(Indexer config, string capsUrl) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { config.Type, capsUrl }))));

    internal static int? PositiveLimit(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;

    internal static bool HasAmbiguousPaging(Indexer config)
    {
        foreach (var parameter in (config.AdditionalParameters ?? "").TrimStart('?', '&').Split('&'))
        {
            var key = Uri.UnescapeDataString(parameter.Split('=', 2)[0]).Replace('+', ' ');
            if (key.Equals("limit", StringComparison.OrdinalIgnoreCase) || key.Equals("offset", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static async Task<IndexerSearchOutcome> FetchAsync(int maximum, TorznabCapabilities? caps,
        bool ambiguousPaging, Func<int, int, Task<string>> fetch,
        Func<string, List<ReleaseSearchResult>> parse)
    {
        var rows = new List<ReleaseSearchResult>();
        var pages = new List<SearchPageObservation>();
        var seenOffers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        var cursor = 0;
        int? previousTotal = null;
        var knownLimit = caps?.MaxPageSize is > 0 || caps?.DefaultPageSize is > 0;
        IndexerSearchOutcome Finish(SearchTermination reason, Exception? failure = null) =>
            new(rows, reason, cursor, pages.ToArray(), knownLimit, failure);
        if (maximum <= 0) return Finish(SearchTermination.CallerCeiling);

        for (var pageNumber = 0; pageNumber < MaxPages; pageNumber++)
        {
            var limit = Math.Min(maximum - rows.Count, caps?.MaxPageSize ?? caps?.DefaultPageSize ?? int.MaxValue);
            string xml;
            XDocument document;
            List<ReleaseSearchResult> parsed;
            try
            {
                xml = await fetch(cursor, limit);
                document = XDocument.Parse(xml);
                if (document.Root?.Name.LocalName != "rss" || document.Root.Element("channel") == null)
                    throw new IndexerRequestException("The indexer returned an invalid search document", HttpStatusCode.OK);
                parsed = parse(xml);
            }
            catch (Exception failure)
            {
                return Finish(IndexerSearchOutcome.Classify(failure), failure);
            }

            var rawItems = document.Descendants("item").ToArray();
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("\n", rawItems.Select(item => item.ToString(SaveOptions.DisableFormatting))))));
            var metadata = document.Descendants(XNamespace.Get("http://www.newznab.com/DTD/2010/feeds/attributes/") + "response").ToArray();
            var validMetadata = metadata.Length <= 1;
            int? ReadNumber(string name)
            {
                var attribute = metadata.FirstOrDefault()?.Attribute(name);
                if (attribute == null) return null;
                if (int.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return number;
                validMetadata = false;
                return null;
            }
            var reportedOffset = ReadNumber("offset");
            var total = ReadNumber("total");
            if (previousTotal.HasValue && total.HasValue && previousTotal.Value != total.Value) validMetadata = false;
            if (total.HasValue) previousTotal = total;
            var rawEnd = (long)cursor + rawItems.Length;
            if (rawEnd > int.MaxValue || (total.HasValue && total.Value < rawEnd) ||
                (rawItems.Length == 0 && total.HasValue && total.Value > rawEnd)) validMetadata = false;
            pages.Add(new(cursor, limit, rawItems.Length, parsed.Count, reportedOffset, total, validMetadata, fingerprint));

            foreach (var row in parsed)
            {
                var key = !string.IsNullOrEmpty(row.Guid) ? row.Guid : row.DownloadUrl ?? row.Title;
                if (!string.IsNullOrEmpty(key) && !seenOffers.Add(key)) continue;
                if (rows.Count < maximum) rows.Add(row);
            }

            var repeated = rawItems.Length > 0 && !seenPages.Add(fingerprint);
            if (rawEnd <= int.MaxValue) cursor = (int)rawEnd;
            if (ambiguousPaging) return Finish(SearchTermination.AmbiguousPaging);
            if (!validMetadata) return Finish(SearchTermination.InvalidMetadata);
            if (repeated) return Finish(SearchTermination.RepeatedPage);
            if (reportedOffset.HasValue && reportedOffset.Value != pages[^1].RequestedOffset)
                return Finish(SearchTermination.OffsetMismatch);
            if (parsed.Count != rawItems.Length) return Finish(SearchTermination.ParseIncomplete);
            if (rows.Count >= maximum) return Finish(SearchTermination.CallerCeiling);
            if (rawItems.Length == 0 || (total.HasValue && cursor >= total.Value))
                return Finish(SearchTermination.Exhausted);
            if (!total.HasValue && rawItems.Length < limit) return Finish(SearchTermination.UnknownTail);
        }
        return Finish(SearchTermination.PageCeiling);
    }
}
