using FluentAssertions;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class EventFolderGroupResolverTests
{
    [Theory]
    [InlineData("WWE", "Wrestling", "WWE Monday Night Raw #1702", "RAW")]
    [InlineData("WWE", "Wrestling", "WWE Raw After WrestleMania", "RAW")]
    [InlineData("WWE", "Wrestling", "WWE Friday Night SmackDown #1376", "SmackDown")]
    [InlineData("WWE", "Wrestling", "WWE NXT #819", "NXT")]
    [InlineData("WWE", "Wrestling", "WWE NXT Vengeance Day", "PLE")]
    [InlineData("WWE", "Wrestling", "WWE Royal Rumble", "PLE")]
    [InlineData("WWE", "Wrestling", "WWE Saturday Night's Main Event", "Saturday Night's Main Event")]
    [InlineData("WWE", "Wrestling", "WWE Saturday Night’s Main Event", "Saturday Night's Main Event")]
    [InlineData("WWE", "Wrestling", "WWE Money in the Bank", "PLE")]
    [InlineData("WWE", "Wrestling", "WWE Main Event #650", "Main Event")]
    [InlineData("UFC", "Fighting", "UFC 310", "PPV")]
    [InlineData("UFC", "Fighting", "UFC Fight Night 262", "Fight Night")]
    [InlineData("UFC", "Fighting", "Dana White's Contender Series 2026 Week 1", "Contender Series")]
    [InlineData("AEW", "Wrestling", "AEW Dynamite #400", "Dynamite")]
    [InlineData("AEW", "Wrestling", "AEW All In", "PPV")]
    [InlineData("Ring of Honor", "Wrestling", "ROH on HonorClub 2026", "Weekly")]
    [InlineData("ONE Championship", "Fighting", "ONE Friday Fights 100", "Friday Fights")]
    [InlineData("Formula 1", "Motorsport", "Dutch Grand Prix - Practice 1", "Practice 1")]
    [InlineData("Formula 1", "Motorsport", "Dutch Grand Prix - Sprint Qualifying", "Sprint Qualifying")]
    [InlineData("Formula 1", "Motorsport", "Dutch Grand Prix - Sprint", "Sprint")]
    [InlineData("Formula 1", "Motorsport", "Dutch Grand Prix - Race", "Race")]
    [InlineData("MotoGP", "Motorsport", "Spanish GP - Qualifying 2", "Qualifying 2")]
    public void Resolve_UsesRecognizedEventType(string leagueName, string sport, string title, string expected)
    {
        var evt = CreateEvent(leagueName, sport, title);

        EventFolderGroupResolver.Resolve(evt).Should().Be(expected);
    }

    [Fact]
    public void Resolve_UsesApiLeagueNameWhenNavigationIsMissing()
    {
        var evt = CreateEvent("UFC", "Fighting", "UFC 310");
        evt.League = null;
        evt.ApiLeagueName = "UFC";

        EventFolderGroupResolver.Resolve(evt).Should().Be("PPV");
    }

    [Theory]
    [InlineData("Premier League", "Soccer", "Arsenal vs Chelsea")]
    [InlineData("Unknown Racing", "Motorsport", "Race 1")]
    [InlineData("Unknown Wrestling", "Wrestling", "Weekly Show")]
    public void Resolve_ReturnsNullForLeaguesWithoutTypeSelectors(string leagueName, string sport, string title)
    {
        EventFolderGroupResolver.Resolve(CreateEvent(leagueName, sport, title)).Should().BeNull();
    }

    [Theory]
    [InlineData("AEW", "Wrestling", "AEW Unlisted Show")]
    [InlineData("WWE", "Wrestling", "WWE Unlisted Show")]
    [InlineData("UFC", "Fighting", "UFC Unlisted Show")]
    [InlineData("Formula 1", "Motorsport", "Untitled Broadcast")]
    public void Resolve_UsesOtherWhenSupportedLeagueHasNoMatchingType(string leagueName, string sport, string title)
    {
        EventFolderGroupResolver.Resolve(CreateEvent(leagueName, sport, title)).Should().Be("Other");
    }

    private static Event CreateEvent(string leagueName, string sport, string title) => new()
    {
        Title = title,
        Sport = sport,
        EventDate = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc),
        League = new League { Name = leagueName, Sport = sport }
    };
}
