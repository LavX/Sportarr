using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Xunit.Abstractions;

namespace Sportarr.Api.Tests.Services;

public class NonEventContentFreshTurkishCultureTests
{
    private readonly ITestOutputHelper _output;

    public NonEventContentFreshTurkishCultureTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void FreshTurkishInitializationKeepsNonEventContentCaseIndependent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("tr-TR", CultureInfo.CurrentCulture.Name);
            var matchingType = Assembly.Load("Sportarr").GetType("Sportarr.Api.Services.ReleaseMatchingService", throwOnError: true)!;
            _output.WriteLine(JsonSerializer.Serialize(new { phase = "before-explicit-class-initialization",
                culture = CultureInfo.CurrentCulture.Name, processId = Environment.ProcessId }));
            RuntimeHelpers.RunClassConstructor(matchingType.TypeHandle);
            _output.WriteLine(JsonSerializer.Serialize(new { phase = "after-explicit-class-initialization",
                culture = CultureInfo.CurrentCulture.Name, processId = Environment.ProcessId }));
            EvaluateContentCases();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EvaluateContentCases()
    {
        var matching = new ReleaseMatchingService(
            NullLogger<ReleaseMatchingService>.Instance,
            new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
            new EventPartDetector(NullLogger<EventPartDetector>.Instance));
        var results = new List<(bool Hard, bool Match, string[] Rejections)>();
        foreach (var content in new[] { "Review", "Recap", "interview", "Interview", "INTERVIEW" })
        foreach (var previewMetadata in new[] { false, true })
        {
            var evt = new Event
            {
                Id = 1, Title = "Aster Falcons vs Iris Comets" + (previewMetadata ? " Preview" : ""),
                Sport = "Basketball", Season = "2021-2022",
                EventDate = new DateTime(2022, 2, 2, 18, 0, 0, DateTimeKind.Utc),
                HomeTeamName = "Aster Falcons", AwayTeamName = "Iris Comets",
                League = new League { Id = 1, Name = "NBA", Sport = "Basketball" }
            };
            var release = new ReleaseSearchResult
            {
                Title = "NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets." + (previewMetadata ? "Preview." : "") + content + ".720p.WEB-DL.H264-GROUP",
                Guid = "non-event-culture-" + content, DownloadUrl = "http://preview-fixture.invalid/descriptor",
                Indexer = "Non-event culture fixture"
            };
            var result = matching.ValidateRelease(release, evt);
            _output.WriteLine(JsonSerializer.Serialize(new { phase = "actual-content-matching", culture = CultureInfo.CurrentCulture.Name,
                content, previewMetadata, release.Title, result.IsHardRejection, result.IsMatch, result.Rejections }));
            results.Add((result.IsHardRejection, result.IsMatch, result.Rejections.ToArray()));
        }
        Assert.All(results, result =>
        {
            Assert.True(result.Hard);
            Assert.False(result.Match);
            Assert.Contains(result.Rejections, reason => reason.StartsWith("Non-event content", StringComparison.OrdinalIgnoreCase));
        });
    }
}
