using CloudSync.Core.Storage;
using CloudSync.Infrastructure.Metadata;

namespace CloudSync.Api;

/// <summary>Hourly housekeeping: empty 30-day-old trash, drop abandoned upload sessions, free unreferenced chunks.</summary>
public sealed class Janitor(SqliteDrive drive, IBlobStore blobs, StorageOptions storage, ILogger<Janitor> log) : BackgroundService
{
    public static readonly TimeSpan GcGrace = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await drive.PurgeTrashAsync(DateTimeOffset.UtcNow.AddDays(-30));
                foreach (var id in await drive.DeleteStaleUploadsAsync(DateTimeOffset.UtcNow.AddDays(-7)))
                    File.Delete(Endpoints.PartPath(storage, id));
                if (await drive.CollectGarbageAsync(blobs, GcGrace) is > 0 and var freed)
                    log.LogInformation("Garbage collection freed {Count} chunks", freed);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Housekeeping run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
