using Sportarr.Api.Models;
using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public class TaskSearchModeTests
{
    [Fact]
    public void UserStartedEventSearchCountsInteractiveRows()
    {
        var interactive = new Indexer { Name = "Interactive", Type = IndexerType.Torznab, Url = "http://interactive.invalid",
            Enabled = true, EnableInteractiveSearch = true, EnableAutomaticSearch = false };
        var automatic = new Indexer { Name = "Automatic", Type = IndexerType.Torznab, Url = "http://automatic.invalid",
            Enabled = true, EnableInteractiveSearch = false, EnableAutomaticSearch = true };

        var filter = TaskService.EventSearchIndexerFilter.Compile();
        Assert.True(filter(interactive));
        Assert.False(filter(automatic));
    }
}
