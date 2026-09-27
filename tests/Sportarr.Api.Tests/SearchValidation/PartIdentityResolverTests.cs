using FluentAssertions;
using Sportarr.Api.Helpers;

namespace Sportarr.Api.Tests.SearchValidation;

public class PartIdentityResolverTests
{
    [Theory]
    [InlineData("UFC.9999.2026.09.01.Main.Card.720p.WEB-DL.H264-SEARCHFIXTURE")]
    [InlineData("UFC_9999_2026_09_01_Main_Card_720p.mkv")]
    [InlineData("UFC.9999.Main.Card.2026.09.01.720p.mkv")]
    [InlineData("UFC.9999.2026.09.01.Main.Card")]
    [InlineData("UFC 9999 Full Main Card 720p")]
    public void ExplicitMainCardLabel_SurvivesDateAndExtensionPositions(string title)
    {
        var result = Resolve(release: title);
        result.Kind.Should().Be(PartIdentityKind.InferredPart);
        result.Part!.SegmentName.Should().Be("Main Card");
        result.Part.PartNumber.Should().Be(3);
        result.Part.PartSuffix.Should().Be("pt3");
    }

    [Theory]
    [InlineData("Early.Prelims", "Early Prelims", 1)]
    [InlineData("Early.Preliminary", "Early Prelims", 1)]
    [InlineData("Prelims", "Prelims", 2)]
    [InlineData("Post.Show", "Post Show", 4)]
    public void CanonicalLabels_UseTheEventDefinitions(string label, string expectedName, int expectedNumber)
    {
        var result = Resolve(release: $"UFC.9999.2026.09.01.{label}.720p");
        result.Kind.Should().Be(PartIdentityKind.InferredPart);
        result.Part!.SegmentName.Should().Be(expectedName);
        result.Part.PartNumber.Should().Be(expectedNumber);
    }

    [Theory]
    [InlineData("Fighting", "UFC Fight Night 280", "UFC", "Main Card", "Main Card", 2)]
    [InlineData("Fighting", "UFC Fight Night 280", "UFC", "Prelims", "Prelims", 1)]
    [InlineData("Wrestling", "WrestleMania 42", "WWE", "Main Card", "Main Show", 2)]
    [InlineData("Wrestling", "WrestleMania 42", "WWE", "Kickoff", "Countdown", 1)]
    [InlineData("Wrestling", "AEW All In", "AEW", "Zero Hour", "Countdown", 1)]
    [InlineData("Wrestling", "ROH Final Battle", "Ring of Honor", "Buy In", "Countdown", 1)]
    [InlineData("Combat", "ONE 170", "ONE Championship", "Lead Card", "Prelims", 1)]
    public void LeagueAndEventContext_SelectTheCorrectPart(string sport, string eventTitle,
        string league, string label, string expectedName, int expectedNumber)
    {
        var result = PartIdentityResolver.Resolve(null, $"{eventTitle}.{label}.720p", null,
            sport, eventTitle, league, true);
        result.Kind.Should().Be(PartIdentityKind.InferredPart);
        result.Part!.SegmentName.Should().Be(expectedName);
        result.Part.PartNumber.Should().Be(expectedNumber);
    }

    [Theory]
    [InlineData("Fighting", "UFC Contender Series 2026 Week 1", "UFC", "Main Card")]
    [InlineData("Wrestling", "WWE Monday Night Raw", "WWE", "Main Card")]
    [InlineData("Wrestling", "AEW Dynamite", "AEW", "Zero Hour")]
    [InlineData("Wrestling", "ROH on HonorClub", "Ring of Honor", "Countdown")]
    [InlineData("Fighting", "ONE Friday Fights 145", "ONE Championship", "Main Card")]
    [InlineData("Basketball", "NBA Finals", "NBA", "Main Card")]
    [InlineData("Motorsport", "Italian Grand Prix Qualifying", "Formula 1", "Qualifying")]
    [InlineData("Motorsport", "MotoGP Race", "MotoGP", "Main Card")]
    [InlineData("Motorsport", "Oulton Park Race One", "British Superbike", "Race 1")]
    public void UnsegmentedEvents_NeverBecomePartsEvenWithAnExplicitRequest(
        string sport, string eventTitle, string league, string label)
    {
        var result = PartIdentityResolver.Resolve(label, $"{eventTitle}.{label}.mkv", null,
            sport, eventTitle, league, true);
        result.Kind.Should().Be(PartIdentityKind.NotApplicable);
        result.Part.Should().BeNull();
    }

    [Fact]
    public void DisabledMultipart_DoesNotCreatePartMetadata()
    {
        var result = PartIdentityResolver.Resolve("Main Card", "UFC.9999.Main.Card.mkv", null,
            "Fighting", "UFC 9999", "UFC", false);
        result.Kind.Should().Be(PartIdentityKind.NotApplicable);
        result.Part.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "UFC")]
    [InlineData("UFC 9999", null)]
    public void MissingContext_DoesNotAssumeAThreePartCard(string? eventTitle, string? league)
    {
        var result = PartIdentityResolver.Resolve(null, "Main.Card.mkv", null,
            "Fighting", eventTitle, league, true);
        result.Kind.Should().Be(PartIdentityKind.ContextUnavailable);
        result.Part.Should().BeNull();
    }

    [Fact]
    public void ExplicitPart_PrecedesBothReleaseAndFilenameLabels()
    {
        var result = Resolve(requested: " prelims ", release: "UFC.9999.Main.Card", filename: "Early.Prelims.mkv");
        result.Kind.Should().Be(PartIdentityKind.ExplicitPart);
        result.Part!.SegmentName.Should().Be("Prelims");
        result.Part.PartNumber.Should().Be(2);
    }

    [Fact]
    public void ExplicitFullEvent_SuppressesInferenceAndPartZero()
    {
        var result = Resolve(requested: "Full Event", release: "UFC.9999.Main.Card", filename: "Prelims.mkv");
        result.Kind.Should().Be(PartIdentityKind.ExplicitFullEvent);
        result.Part.Should().BeNull();
    }

    [Fact]
    public void UnknownExplicitPart_DoesNotFallThroughToFilenameDetection()
    {
        var result = Resolve(requested: "Second Half", release: "UFC.9999.Main.Card");
        result.Kind.Should().Be(PartIdentityKind.UnsupportedExplicitPart);
        result.Part.Should().BeNull();
    }

    [Theory]
    [InlineData("UFC.9999.720p.WEB-DL")]
    [InlineData("UFC.9999.PPV.720p")]
    [InlineData("UFC.9999.MC.720p")]
    [InlineData("UFC.9999.EP.720p")]
    [InlineData("UFC.9999.Main.Event.720p")]
    public void UnlabelledOrWeakAliases_DoNotGuessMainCard(string title)
    {
        var result = Resolve(release: title);
        result.Kind.Should().Be(PartIdentityKind.Unlabelled);
        result.Part.Should().BeNull();
    }

    [Theory]
    [InlineData("UFC.9999.PPV.Full.Event.Main.Card")]
    [InlineData("UFC.9999.Complete.Event.Main.Card")]
    [InlineData("UFC.9999.Full.Card.Main.Card")]
    [InlineData("UFC.9999.Complete.Card.Prelims")]
    [InlineData("UFC.9999.FullEvent.Main.Card")]
    public void CompleteEventLabels_DoNotBecomeOnePartOrFallBackToAFile(string title)
    {
        var result = Resolve(release: title, filename: "Main.Card.mkv");
        result.Kind.Should().Be(PartIdentityKind.CompleteEventLabel);
        result.Part.Should().BeNull();
    }

    [Theory]
    [InlineData("UFC.9999.Main.Card.and.Prelims")]
    [InlineData("UFC.9999.Early.Prelims.Plus.Prelims")]
    [InlineData("UFC.9999.Main.Card.Post.Show")]
    public void CombinedSegments_AreNotAssignedToTheFirstMatch(string title)
    {
        var result = Resolve(release: title, filename: "Main.Card.mkv");
        result.Kind.Should().Be(PartIdentityKind.Ambiguous);
        result.Part.Should().BeNull();
    }

    [Fact]
    public void UnsupportedEarlyPrelims_DoesNotBecomeFightNightPrelims()
    {
        var result = PartIdentityResolver.Resolve(null, "UFC.Fight.Night.Early.Prelims", null,
            "Fighting", "UFC Fight Night 280", "UFC", true);
        result.Kind.Should().Be(PartIdentityKind.UnsupportedLabel);
        result.Part.Should().BeNull();
    }

    [Fact]
    public void ContextScopedAliases_DoNotCrossLeagues()
    {
        var result = Resolve(release: "UFC.9999.Zero.Hour.720p");
        result.Kind.Should().Be(PartIdentityKind.UnsupportedLabel);
        result.Part.Should().BeNull();
    }

    [Fact]
    public void OriginalReleaseIdentity_SurvivesAnOpaqueFilename()
    {
        var result = Resolve(release: "UFC.9999.Main.Card", filename: "4bc76ea8.mkv");
        result.Part!.SegmentName.Should().Be("Main Card");
    }

    [Fact]
    public void UnlabelledQueueTitle_CanUseTheRawSourceBasename()
    {
        var result = Resolve(release: "UFC 9999", filename: "UFC.9999.2026.09.01.Main.Card.mkv");
        result.Kind.Should().Be(PartIdentityKind.InferredPart);
        result.Part!.PartNumber.Should().Be(3);
    }

    [Theory]
    [InlineData("Main.Card", "Prelims.mkv")]
    [InlineData("Main.Card", "Full.Event.mkv")]
    [InlineData("Main.Card", "Main.Card.and.Prelims.mkv")]
    public void ConflictingRawEvidence_DoesNotPickOneInferredIdentity(string release, string filename)
    {
        var result = Resolve(release: release, filename: filename);
        result.Kind.Should().Be(PartIdentityKind.Ambiguous);
        result.Part.Should().BeNull();
    }

    [Fact]
    public void PackTitle_DoesNotSupplyEveryChildsPart()
    {
        var result = PartIdentityResolver.Resolve(null, "UFC.Main.Card.Collection", "Prelims.mkv",
            "Fighting", "UFC 9999", "UFC", true, isPack: true);
        result.Kind.Should().Be(PartIdentityKind.InferredPart);
        result.Part!.SegmentName.Should().Be("Prelims");
        result.Part.PartNumber.Should().Be(2);
    }

    [Fact]
    public void PackWithOpaqueChild_DoesNotInheritThePackLabel()
    {
        var result = PartIdentityResolver.Resolve(null, "UFC.Main.Card.Collection", "opaque.mkv",
            "Fighting", "UFC 9999", "UFC", true, isPack: true);
        result.Kind.Should().Be(PartIdentityKind.Unlabelled);
        result.Part.Should().BeNull();
    }

    [Theory]
    [InlineData("/downloads/Main.Card/opaque.mkv")]
    [InlineData("C:\\downloads\\Main.Card\\opaque.mkv")]
    public void DirectoryNames_DoNotSupplyPartEvidence(string filename)
    {
        var result = Resolve(filename: filename);
        result.Kind.Should().Be(PartIdentityKind.Unlabelled);
        result.Part.Should().BeNull();
    }

    private static PartIdentityResolution Resolve(string? requested = null, string? release = null, string? filename = null) =>
        PartIdentityResolver.Resolve(requested, release, filename, "Fighting", "UFC 9999", "UFC", true);
}
