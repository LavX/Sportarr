using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Sportarr.Api.Tests.Services;

public class PartIdentityPackBoundaryTests
{
    [Fact]
    public async Task PackWithOneMonitoredChildStillUsesItsFileIdentity()
    {
        await using var rig = await PartIdentityIntegrationHarness.CreateAsync();
        await rig.GrabAsync(rig.Release("UFC.2020.Season.Pack.Main.Card.720p.WEB-DL", isPack: true));
        var queue = await rig.Db.DownloadQueue.SingleAsync();
        queue.IsPack.Should().BeTrue("the catalogue's pack identity does not depend on monitored child count");
        queue.Part.Should().BeNull();
        var file = await rig.ImportAsync(queue.Title, "UFC.9999.2020.09.01.Prelims.720p.WEB-DL.mkv",
            acquiredQueue: queue);
        file.PartName.Should().Be("Prelims");
        file.PartNumber.Should().Be(2);
        Path.GetFileName(file.FilePath).Should().Contain("pt2").And.NotContain("pt3");
    }
}
