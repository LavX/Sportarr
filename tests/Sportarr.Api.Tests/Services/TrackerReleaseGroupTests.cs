using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class TrackerReleaseGroupTests
{
    public static IEnumerable<object?[]> Titles()
    {
        yield return ["UFC 330 Makhachev vs Machado Garry Main Card 1080p WEB DL H264 nVa", "nVa"];
        yield return ["UFC 328 Chimaev vs Strickland Main Card 1080p WEB DL H264 nVa", "nVa"];
        yield return ["UFC 329 McGregor vs Holloway 2 Main Card 1080p WEB DL AAC2 0 H 264 nVa", "nVa"];
        yield return ["UFC 2026 Africa Nigeria 1080p STAN WEB DL AAC2 0 H 264 nVa", "nVa"];
        yield return ["Event 1080p WEB H.264 nVa.mkv", "nVa"];
        yield return ["Event 1080p WEB h264 nVa 1080pEN50fps", "nVa"];
        yield return ["Event 1080p WEB h264 CBS", null];
        yield return ["Event 1080p WEB h264 ESPN", null];
        yield return ["Event 1080p WEB h264 SkyF1", null];
        yield return ["Event 1080p WEB h264 English", null];
        yield return ["Event 1080p WEB h264 NVAextra", null];
        yield return ["Event 1080p WEB h264 nVa Extra", null];
        yield return ["Event 1080p WEB h264 nVa 1080pEN500fps", null];
        yield return ["Event 1080p WEB h264 nVa.random", null];
        yield return ["Event 1080p WEB h264 nVa mkv", null];
        yield return ["Event nVa", null];
        yield return ["Event H264-nVa-extra words", null];
        yield return ["nVa Grand Prix 1080p WEB h264", null];
        yield return ["Event h264 Something nVa", null];
        yield return ["Event 1080p WEB h264 nVa-OTHER", "OTHER"];
        yield return ["Event 1080p WEB h264 nVa [OTHER]", "OTHER"];
        yield return ["Formula1 2026 Belgian Grand Prix WEB h264 BILLIE 1080pEN50fps", "BILLIE"];
        yield return ["Formula1 2026 R10 Belgium Full Event 2160p SkyF1 HDTV HDR H265 egortech", "egortech"];
        yield return ["NBA.2026.04.02.Cleveland.Cavaliers.vs.Golden.State.Warriors.1080p.WEB.h264-BILLIE", "BILLIE"];
        yield return ["Event.1080p.WEB.h264 [OTHER]", "OTHER"];
        yield return ["Event.1080p.WEB.h264-BILLIE[trackertag].mkv", "BILLIE"];
        yield return ["Event.1080p.WEB.h264 BILLIE-OTHER", "OTHER"];
        yield return ["Event.1080p.WEB.h264 BILLIE [OTHER]", "OTHER"];
        yield return ["Event 1080p WEB H.264 billie.mkv", "billie"];
        foreach (var extension in new[] { "webm", "mov", "ogm", "rar", "zip", "mp4", "m4v", "m2ts" })
        {
            yield return [$"Event.1080p.WEB.h264-BILLIE.{extension}", "BILLIE"];
            yield return [$"Event.1080p.WEB.h264 [OTHER].{extension}", "OTHER"];
        }
        yield return ["Event 1080p WEB h264 BILLIE.random", null];
        foreach (var suffix in new[] { "ESPN", "SkyF1", "SNP", "English", "Unknown", "1080p", "x264", "RMZ", "BILLIEextra", "BILLIE 1080pEN50fpsExtra", "BILLIE 1080pEN500fps" })
            yield return [$"Event 1080p WEB h264 {suffix}", null];
        yield return ["BILLIE Grand Prix 1080p WEB h264", null];
        yield return ["Event BILLIE", null];
        yield return ["Event.WEB-DL", null];
        yield return ["Event.WEBDL-2160p", null];
        yield return ["Event.WEB-H264", null];
    }

    [Theory]
    [MemberData(nameof(Titles))]
    public void File_parser_recognizes_supported_groups_without_guessing(string title, string? expected)
    {
        var parser = new MediaFileParser(NullLogger<MediaFileParser>.Instance);
        parser.Parse(title).ReleaseGroup.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Titles))]
    public void Missing_group_format_uses_the_same_title_evidence(string title, string? expected)
    {
        var evaluator = new ReleaseEvaluator(NullLogger<ReleaseEvaluator>.Instance,
            new EventPartDetector(NullLogger<EventPartDetector>.Instance),
            new CustomFormatMatchCache(NullLogger<CustomFormatMatchCache>.Instance));
        var release = new ReleaseSearchResult { Title = title, Guid = "fixture", DownloadUrl = "http://fixture/release", Indexer = "Fixture", ReleaseGroup = "UntrustedOverride" };
        var format = new CustomFormat { Id = 53, Name = "No-RlsGroup", Specifications = [new FormatSpecification {
            Name = "Missing group", Implementation = "ReleaseGroupSpecification", Negate = true,
            Fields = new Dictionary<string, object> { ["value"] = "." }
        }] };
        var profile = new QualityProfile { Name = "Fixture", FormatItems = [new ProfileFormatItem { FormatId = 53, Score = -10000 }] };
        evaluator.EvaluateRelease(release, profile, [format]).CustomFormatScore.Should().Be(expected == null ? -10000 : 0);
    }

    [Theory]
    [MemberData(nameof(Titles))]
    public async Task Indexer_clients_preserve_title_and_extract_group(string title, string? expected)
    {
        var xml = new XDocument(new XElement("rss", new XElement("channel", new XElement("item",
            new XElement("title", title), new XElement("guid", "fixture"),
            new XElement("link", "http://fixture/release"))))).ToString();
        using var http = new HttpClient(new FeedHandler(xml));
        var indexer = new Indexer { Name = "Fixture", Url = "http://fixture", ApiPath = "/api", ApiKey = "fixture" };
        var torrent = (await new TorznabClient(http, NullLogger<TorznabClient>.Instance).SearchAsync(indexer, "Event", maxResults: 1)).Single();
        var nzb = (await new NewznabClient(http, NullLogger<NewznabClient>.Instance).SearchAsync(indexer, "Event", maxResults: 1)).Single();
        torrent.Title.Should().Be(title);
        nzb.Title.Should().Be(title);
        torrent.ReleaseGroup.Should().Be(expected);
        nzb.ReleaseGroup.Should().Be(expected);
    }

    private sealed class FeedHandler(string xml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") });
    }
}
