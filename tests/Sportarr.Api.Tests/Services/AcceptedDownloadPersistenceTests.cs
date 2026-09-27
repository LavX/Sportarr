using Sportarr.Api.Services;

namespace Sportarr.Api.Tests.Services;

public sealed class AcceptedDownloadPersistenceTests
{
    [Fact]
    public void AcceptedClientJobCannotBeAbandonedByCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var persistenceToken = AcceptedDownloadPersistence.AfterAdd("accepted-id", cancellation.Token);

        Assert.False(persistenceToken.IsCancellationRequested);
        Assert.Equal(CancellationToken.None, persistenceToken);
    }

    [Fact]
    public void RefusedClientJobKeepsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var persistenceToken = AcceptedDownloadPersistence.AfterAdd(null, cancellation.Token);

        Assert.True(persistenceToken.IsCancellationRequested);
    }

    [Fact]
    public async Task OwnershipSaveRetriesWithoutRemovingAcceptedJob()
    {
        var saves = 0;

        var persisted = await AcceptedDownloadPersistence.PersistOwnerAsync(
            _ => ++saves == 1
                ? Task.FromException(new InvalidOperationException("transient save failure"))
                : Task.CompletedTask);

        Assert.True(persisted);
        Assert.Equal(2, saves);
    }

    [Fact]
    public async Task TerminalOwnershipSaveReportsRecoveryRequired()
    {
        var saves = 0;

        var persisted = await AcceptedDownloadPersistence.PersistOwnerAsync(
            _ => { saves++; return Task.FromException(new InvalidOperationException("terminal save failure")); });

        Assert.False(persisted);
        Assert.Equal(AcceptedDownloadPersistence.SaveAttempts, saves);
    }
}
