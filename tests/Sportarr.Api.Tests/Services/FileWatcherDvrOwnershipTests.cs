using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class FileWatcherDvrOwnershipTests
{
    [Theory]
    [InlineData("/media/.League - S2026E05 - Event.ts", "/media/League - S2026E05 - Event.ts")]
    [InlineData("/media/.League - S2026E05 - Event.ts", "/media/League - S2026E05 - Event.mp4")]
    public void MatchesCaptureAcrossRevealAndRemux(string recordingPath, string candidatePath)
    {
        FileWatcherService.IsSameDvrCapture(recordingPath, candidatePath).Should().BeTrue();
    }

    [Fact]
    public void DoesNotMatchAnotherCaptureInTheSameDirectory()
    {
        FileWatcherService.IsSameDvrCapture(
            "/media/.League - S2026E05 - Event.ts",
            "/media/League - S2026E06 - Other Event.ts").Should().BeFalse();
    }

    [Fact]
    public void DoesNotMatchTheSameNameInAnotherDirectory()
    {
        FileWatcherService.IsSameDvrCapture(
            "/recordings/.League - S2026E05 - Event.ts",
            "/library/League - S2026E05 - Event.ts").Should().BeFalse();
    }

    [Fact]
    public async Task CompletedCaptureRemainsOwnedDuringDelayedWatcherCallback()
    {
        var options = new DbContextOptionsBuilder<SportarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new SportarrDbContext(options);
        var now = DateTime.UtcNow;
        db.DvrRecordings.Add(new DvrRecording
        {
            Title = "Fixture",
            ChannelId = 1,
            ScheduledStart = now.AddHours(-2),
            ScheduledEnd = now.AddMinutes(-5),
            Status = DvrRecordingStatus.Completed,
            OutputPath = "/media/.League - S2026E05 - Event.ts"
        });
        await db.SaveChangesAsync();

        var owned = await FileWatcherService.IsDvrOwnedFileAsync(
            db,
            "/media/League - S2026E05 - Event.mp4");

        owned.Should().BeTrue();
    }
}
