using System.Collections.Concurrent;
using System.Diagnostics;
using CloudSync.Core.Metadata;
using CloudSync.Core.Storage;

namespace CloudSync.Core.Services;

public enum ChunkStoreOutcome
{
    /// <summary>Bytes were physically written.</summary>
    Written,
    /// <summary>Chunk already existed; only a reference is recorded.</summary>
    Deduplicated,
}

/// <summary>
/// Dedup-aware chunk persistence. Order is always blob → Chunks row, so the catalog never
/// references bytes that aren't durable. Must be a singleton so the in-flight map is shared.
/// </summary>
public sealed class ChunkStore(IBlobStore blobs, IMetadataRepository metadata)
{
    // Single-flight: concurrent writers of the same new hash in this process wait on one write.
    private readonly ConcurrentDictionary<string, Task> _inFlight = new();

    public async Task<ChunkStoreOutcome> StoreAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct = default, Timings? timings = null)
    {
        var t = Stopwatch.GetTimestamp();
        var exists = await metadata.ChunkExistsAsync(hash, ct);
        timings?.Add("dedup_lookup", t);
        if (exists)
            return ChunkStoreOutcome.Deduplicated;

        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = _inFlight.GetOrAdd(hash, mine.Task);
        if (owner != mine.Task)
        {
            await owner.WaitAsync(ct);
            return ChunkStoreOutcome.Deduplicated;
        }

        try
        {
            // Cross-process races are settled by the blob store (atomic publish) and the PK (INSERT OR IGNORE).
            t = Stopwatch.GetTimestamp();
            var written = await blobs.PutAsync(hash, data, ct);
            timings?.Add("blob_write", t);
            t = Stopwatch.GetTimestamp();
            await metadata.TryAddChunkAsync(new ChunkRecord(hash, blobs.GetStorageKey(hash), data.Length), ct);
            timings?.Add("db_register", t);
            mine.SetResult();
            return written ? ChunkStoreOutcome.Written : ChunkStoreOutcome.Deduplicated;
        }
        catch (Exception ex)
        {
            mine.SetException(ex);
            _ = mine.Task.Exception; // observed; waiters still receive it
            throw;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Task>(hash, mine.Task));
        }
    }
}
