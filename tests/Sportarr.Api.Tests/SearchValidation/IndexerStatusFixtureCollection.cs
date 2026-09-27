namespace Sportarr.Api.Tests.SearchValidation;

// Separate test databases can share the process-wide status gate.
[CollectionDefinition("Indexer status fixtures", DisableParallelization = true)]
public sealed class IndexerStatusFixtureCollection
{
    public const string Name = "Indexer status fixtures";
}
