using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using CloudSync.Core.Chunking;

namespace CloudSync.Core.Services;

public sealed class PipelineOptions
{
    public int Workers { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>Chunks buffered between chunker and workers. Bounds memory: ~(capacity + workers + 1) × chunk size.</summary>
    public int ChannelCapacity { get; set; } = 4;
}

public sealed record ChunkResult(int SequenceIndex, string Hash, int Length, bool Written);

public sealed record PipelineResult(IReadOnlyList<ChunkResult> Chunks, long TotalSize, string Sha256);

/// <summary>
/// Producer–consumer: one producer chunks the stream into a bounded channel (backpressure),
/// N workers hash and store chunks in parallel. Results are re-ordered by SequenceIndex.
/// </summary>
public sealed class UploadPipeline(ChunkStore store, PipelineOptions options)
{
    public async Task<PipelineResult> RunAsync(IChunker chunker, PipeReader source, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<RawChunk>(new BoundedChannelOptions(Math.Max(1, options.ChannelCapacity))
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var results = new ConcurrentBag<ChunkResult>();
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalSize = 0;

        var producer = Guard(async () =>
        {
            try
            {
                await foreach (var chunk in chunker.ChunkAsync(source, cts.Token))
                {
                    fileHash.AppendData(chunk.Span);
                    totalSize += chunk.Length;
                    try
                    {
                        await channel.Writer.WriteAsync(chunk, cts.Token);
                    }
                    catch
                    {
                        chunk.Dispose();
                        throw;
                    }
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        });

        var workers = Enumerable.Range(0, Math.Max(1, options.Workers)).Select(_ => Guard(async () =>
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cts.Token))
            {
                using (chunk)
                {
                    var hash = ContentHash.Compute(chunk.Span);
                    var outcome = await store.StoreAsync(hash, chunk.Memory, cts.Token);
                    results.Add(new ChunkResult(chunk.SequenceIndex, hash, chunk.Length, outcome == ChunkStoreOutcome.Written));
                }
            }
        }));

        var tasks = workers.Prepend(producer).ToArray();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            while (channel.Reader.TryRead(out var leftover)) leftover.Dispose();
            var root = tasks.Where(t => t.IsFaulted)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException and not ChannelClosedException);
            if (root is not null) ExceptionDispatchInfo.Throw(root);
            throw;
        }

        var ordered = results.OrderBy(r => r.SequenceIndex).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].SequenceIndex != i)
                throw new InvalidOperationException($"Chunk sequence gap at index {i}.");
        }

        return new PipelineResult(ordered, totalSize, Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant());

        // Any failure cancels the rest of the pipeline.
        Task Guard(Func<Task> body) => Task.Run(async () =>
        {
            try
            {
                await body();
            }
            catch
            {
                cts.Cancel();
                throw;
            }
        });
    }
}
