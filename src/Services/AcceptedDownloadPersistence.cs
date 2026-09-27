namespace Sportarr.Api.Services;

internal static class AcceptedDownloadPersistence
{
    internal const int SaveAttempts = 3;

    internal static CancellationToken AfterAdd(string? downloadId, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(downloadId) ? cancellationToken : CancellationToken.None;

    internal static async Task<bool> PersistOwnerAsync(
        Func<CancellationToken, Task> saveOwner)
    {
        for (var attempt = 1; attempt <= SaveAttempts; attempt++)
        {
            try
            {
                await saveOwner(CancellationToken.None);
                return true;
            }
            catch when (attempt < SaveAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}
