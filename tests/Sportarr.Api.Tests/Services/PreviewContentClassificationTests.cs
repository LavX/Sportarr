using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class PreviewContentClassificationTests
{
    private readonly ReleaseMatchingService _matching = new(
        NullLogger<ReleaseMatchingService>.Instance,
        new SportsFileNameParser(NullLogger<SportsFileNameParser>.Instance),
        new EventPartDetector(NullLogger<EventPartDetector>.Instance));

    private const string NbaPreview = "NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.Preview.720p.WEB-DL.H264.MULTi-FIELD";

    public static IEnumerable<object[]> CapturedOffers()
    {
        yield return new object[]
        {
            "participant-fixtures", "NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.Preview.720p.WEB-DL.H264.MULTi-FIELD", "release-5941b422a80a11768927ad50a6eb63ae",
            "NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.720p.WEB-DL.H264.MULTi-FIELD", "release-b2599879429d8d5020820660444351d9", null,
            new Event
            {
                Id = 1, ExternalId = "fixture:bfd6831dd63557d9ac8befea8b67a634", Title = "Aster Falcons vs Iris Comets",
                Sport = "Basketball", Season = "2021-2022", Round = null,
                EventDate = DateTime.Parse("2022-02-02T18:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                BroadcastDate = null,
                HomeTeamName = "Aster Falcons", AwayTeamName = "Iris Comets",
                League = new League { Id = 1, Name = "NBA", Sport = "Basketball", AllowHighlights = false }
            }
        };
        yield return new object[]
        {
            "fight-cards", "UFC.2022.04.02.UFC.Fight.Night.Birchvale.vs.Elmcrest.Prelims.Preview.720p.WEB-DL.H264.MULTi-FIELD", "release-4864cae958c7f36f7c895c8afc2240c6",
            "UFC.2022.04.02.UFC.Fight.Night.Birchvale.vs.Elmcrest.Prelims.720p.WEB-DL.H264.MULTi-FIELD", "release-78e73140d4de18a143888011b07c0d9d", "Prelims",
            new Event
            {
                Id = 1, ExternalId = "fixture:7aeab14df012d56bdc8780e34c27a216", Title = "UFC Fight Night Birchvale vs Elmcrest",
                Sport = "Fighting", Season = "2022", Round = null,
                EventDate = DateTime.Parse("2022-04-02T18:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                BroadcastDate = null,
                HomeTeamName = null, AwayTeamName = null,
                League = new League { Id = 1, Name = "UFC", Sport = "Fighting", AllowHighlights = false }
            }
        };
        yield return new object[]
        {
            "motorsport-sessions", "Formula.1.2022.05.27.Round.6.Fir.Grand.Prix.Practice.1.Preview.720p.WEB-DL.H264.MULTi-FIELD", "release-0a9a73336fc89156e30a612281b64c68",
            "Formula.1.2022.05.27.Round.6.Fir.Grand.Prix.Practice.1.720p.WEB-DL.H264.MULTi-FIELD", "release-aa9556c9d6ac5ef8e3a5643b74c9c5f8", null,
            new Event
            {
                Id = 1, ExternalId = "fixture:4ca1bb864f9120d99a8bf78de7592a9d", Title = "Fir Grand Prix Practice 1",
                Sport = "Motorsport", Season = "2022", Round = "6",
                EventDate = DateTime.Parse("2022-05-27T18:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                BroadcastDate = null,
                HomeTeamName = null, AwayTeamName = null,
                League = new League { Id = 1, Name = "Formula 1", Sport = "Motorsport", AllowHighlights = false }
            }
        };
        yield return new object[]
        {
            "tournament-matches", "WTA.Tour.2022.05.22.Delta.Open.Quarter.Final.Deltawood.vs.Granitedale.Preview.720p.WEB-DL.H264.MULTi-FIELD", "release-fd5fa9b056752f3884b3bed84d454f6f",
            "WTA.Tour.2022.05.22.Delta.Open.Quarter.Final.Deltawood.vs.Granitedale.720p.WEB-DL.H264.MULTi-FIELD", "release-388a23568b9f003868419efc165f56bb", null,
            new Event
            {
                Id = 1, ExternalId = "fixture:f98be115737e37aa6bf0d1912b0fd455", Title = "Delta Open Quarter Final Deltawood vs Granitedale",
                Sport = "Tennis", Season = "2022", Round = null,
                EventDate = DateTime.Parse("2022-05-22T18:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                BroadcastDate = null,
                HomeTeamName = null, AwayTeamName = null,
                League = new League { Id = 1, Name = "WTA Tour", Sport = "Tennis", AllowHighlights = false }
            }
        };
        yield return new object[]
        {
            "multi-day-stages", "UCI.World.Tour.2022.07.10.Tour.de.France.Stage.10.Preview.720p.WEB-DL.H264.MULTi-FIELD", "release-2640e47f35df99bf44fc76aeefd7bc6c",
            "UCI.World.Tour.2022.07.10.Tour.de.France.Stage.10.720p.WEB-DL.H264.MULTi-FIELD", "release-e0cc2f60ee0f16f7d59b21f9cccb820b", null,
            new Event
            {
                Id = 1, ExternalId = "fixture:f4289e73c667c6a192487f807e8102a6", Title = "Tour de France Stage 10",
                Sport = "Cycling", Season = "2022", Round = null,
                EventDate = DateTime.Parse("2022-07-10T12:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                BroadcastDate = null,
                HomeTeamName = null, AwayTeamName = null,
                League = new League { Id = 1, Name = "UCI World Tour", Sport = "Cycling", AllowHighlights = false }
            }
        };
        yield return new object[]
        {
            "discipline-meets", "Diamond.League.2022.07.15.Womens.100.metres.Final.at.Fir.Meeting.Preview.720p.WEB-DL.H264.MULTi-FIELD", "release-7e03beb3d14b32d8a9abe9a3817c1b04",
            "Diamond.League.2022.07.15.Womens.100.metres.Final.at.Fir.Meeting.720p.WEB-DL.H264.MULTi-FIELD", "release-8ccfd0bcdcb693216e58803f9c808a41", null,
            new Event
            {
                Id = 1, ExternalId = "fixture:71fd4260b242d98918fbc534c47bbd53", Title = "Womens 100 metres Final at Fir Meeting",
                Sport = "Athletics", Season = "2022", Round = null,
                EventDate = DateTime.Parse("2022-07-15T18:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                BroadcastDate = null,
                HomeTeamName = null, AwayTeamName = null,
                League = new League { Id = 1, Name = "Diamond League", Sport = "Athletics", AllowHighlights = false }
            }
        };
        yield return new object[]
        {
            "recurring-broadcasts", "WWE.2022.03.28.RAW.#1512.Preview.720p.WEB-DL.H264.MULTi-FIELD", "release-ed8e1f358c3434825013cb32edeb6d1a",
            "WWE.2022.03.28.RAW.#1512.720p.WEB-DL.H264.MULTi-FIELD", "release-7bf3931a450a55592f694c7a82c6e9b1", null,
            new Event
            {
                Id = 1, ExternalId = "fixture:b5dce3abd4adfb528fcd575cccb63f4e", Title = "RAW #1512",
                Sport = "Fighting", Season = "2022", Round = null,
                EventDate = DateTime.Parse("2022-03-29T01:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                BroadcastDate = DateTime.Parse("2022-03-28T00:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                HomeTeamName = null, AwayTeamName = null,
                League = new League { Id = 1, Name = "WWE", Sport = "Fighting", AllowHighlights = false }
            }
        };
    }

    [Theory]
    [MemberData(nameof(CapturedOffers))]
    public void CapturedPreviewCannotFillOrdinaryEventWhileFullOfferKeepsContentEligibility(
        string shape, string previewTitle, string previewGuid, string fullTitle, string fullGuid,
        string? requestedPart, Event evt)
    {
        var full = _matching.ValidateRelease(Release(fullTitle, fullGuid), evt, requestedPart);
        Assert.DoesNotContain(full.Rejections, IsNonEvent);

        var preview = _matching.ValidateRelease(Release(previewTitle, previewGuid), evt, requestedPart);
        Assert.True(preview.IsHardRejection, shape);
        Assert.False(preview.IsMatch);
        Assert.Contains(preview.Rejections, reason => IsNonEvent(reason) && reason.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.[PREVIEW].720p.WEB-DL.H264-GROUP")]
    [InlineData("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets_Preview_720p.WEB-DL.H264-GROUP")]
    [InlineData("NBA 2022.02.02 Aster Falcons vs Iris Comets-Preview-720p WEB-DL H264-GROUP")]
    public void StandaloneContentTokenAcceptsSceneSeparators(string title)
    {
        var result = _matching.ValidateRelease(Release(title), NbaEvent());
        Assert.True(result.IsHardRejection);
        Assert.False(result.IsMatch);
        Assert.Contains(result.Rejections, reason => IsNonEvent(reason) && reason.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.Previewer.720p.WEB-DL.H264-GROUP")]
    [InlineData("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.SuperPreview.720p.WEB-DL.H264-GROUP")]
    [InlineData("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.720p.WEB-DL.H264-PREVIEW")]
    [InlineData("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.720p.WEB-DL.H264-PREVIEWTEAM")]
    public void SubstringsAndTerminalReleaseGroupsDoNotDeclarePreviewContent(string title)
    {
        var result = _matching.ValidateRelease(Release(title), NbaEvent());
        Assert.DoesNotContain(result.Rejections, IsNonEvent);
    }

    [Fact]
    public void ExplicitPreviewProgramMetadataKeepsPreviewContentEligible()
    {
        var evt = NbaEvent();
        evt.Title = "Aster Falcons vs Iris Comets Preview";
        var result = _matching.ValidateRelease(Release(NbaPreview), evt);
        Assert.DoesNotContain(result.Rejections, IsNonEvent);
    }

    [Fact]
    public void MetadataSubstringDoesNotGrantPreviewProgramException()
    {
        var evt = NbaEvent();
        evt.Title = "Aster Falcons vs Iris Comets Previewer";
        var result = _matching.ValidateRelease(Release(NbaPreview), evt);
        Assert.True(result.IsHardRejection);
        Assert.Contains(result.Rejections, reason => IsNonEvent(reason) && reason.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Interview")]
    [InlineData("Review")]
    [InlineData("Recap")]
    public void PreviewProgramExceptionDoesNotAdmitOtherNonEventContent(string content)
    {
        var evt = NbaEvent();
        var ordinary = _matching.ValidateRelease(Release("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets." + content + ".720p.WEB-DL.H264-GROUP"), evt);
        Assert.True(ordinary.IsHardRejection);
        Assert.False(ordinary.IsMatch);
        Assert.Contains(ordinary.Rejections, IsNonEvent);

        evt.Title = "Aster Falcons vs Iris Comets Preview";
        var program = _matching.ValidateRelease(Release("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.Preview." + content + ".720p.WEB-DL.H264-GROUP"), evt);
        Assert.True(program.IsHardRejection);
        Assert.False(program.IsMatch);
        Assert.Contains(program.Rejections, reason => IsNonEvent(reason) && reason.Contains(content, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighlightsKeepsItsExistingLeagueOptIn(bool allowHighlights)
    {
        var evt = NbaEvent();
        evt.League!.AllowHighlights = allowHighlights;
        var result = _matching.ValidateRelease(Release("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.Highlights.720p.WEB-DL.H264-GROUP"), evt);
        if (allowHighlights) Assert.DoesNotContain(result.Rejections, IsNonEvent);
        else
        {
            Assert.True(result.IsHardRejection);
            Assert.False(result.IsMatch);
            Assert.Contains(result.Rejections, IsNonEvent);
        }
    }

    [Fact]
    public void HighlightsOptInDoesNotAdmitPreviewOfOrdinaryEvent()
    {
        var evt = NbaEvent();
        evt.League!.AllowHighlights = true;
        var result = _matching.ValidateRelease(Release("NBA.2022.02.02.Aster.Falcons.vs.Iris.Comets.Preview.Highlights.720p.WEB-DL.H264-GROUP"), evt);
        Assert.True(result.IsHardRejection);
        Assert.Contains(result.Rejections, reason => IsNonEvent(reason) && reason.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNonEvent(string reason) => reason.StartsWith("Non-event content", StringComparison.OrdinalIgnoreCase);

    private static ReleaseSearchResult Release(string title, string? guid = null) => new()
    {
        Title = title, Guid = guid ?? title,
        DownloadUrl = "http://preview-fixture.invalid/descriptor", Indexer = "Preview classification fixture"
    };

    private static Event NbaEvent() => new()
    {
        Id = 1, Title = "Aster Falcons vs Iris Comets", Sport = "Basketball", Season = "2021-2022",
        EventDate = new DateTime(2022, 2, 2, 18, 0, 0, DateTimeKind.Utc),
        HomeTeamName = "Aster Falcons", AwayTeamName = "Iris Comets",
        League = new League { Id = 1, Name = "NBA", Sport = "Basketball", AllowHighlights = false }
    };
}
