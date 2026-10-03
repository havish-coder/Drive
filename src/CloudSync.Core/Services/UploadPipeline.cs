using System.Collections.Concurrent;
using System.Diagnostics;
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

/// <summary>Per-phase elapsed time. Phases done by parallel workers are summed across workers, so they can exceed wall time.</summary>
public sealed class Timings
{
    private readonly ConcurrentDictionary<string, long> _ticks = new();

    public void Add(string phase, long startTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).Ticks;
        _ticks.AddOrUpdate(phase, elapsed, (_, total) => total + elapsed);
    }

    public Dictionary<string, double> ToMilliseconds() =>
        _ticks.ToDictionary(p => p.Key, p => Math.Round(TimeSpan.FromTicks(p.Value).TotalMilliseconds, 1));
}

public sealed record ChunkResult(int SequenceIndex, string Hash, int Length, bool Written);

public sealed record PipelineResult(IReadOnlyList<ChunkResult> Chunks, long TotalSize, string Sha256);

/// <summary>
/// Producer–consumer: one producer chunks the stream into a bounded channel (backpressure),
/// N workers hash and store chunks in parallel. Results are re-ordered by SequenceIndex.
/// </summary>
public sealed class UploadPipeline(ChunkStore store, PipelineOptions options)
{
    public async Task<PipelineResult> RunAsync(IChunker chunker, PipeReader source, CancellationToken ct = default, Timings? timings = null)
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
                var t = Stopwatch.GetTimestamp();
                await foreach (var chunk in chunker.ChunkAsync(source, cts.Token))
                {
                    fileHash.AppendData(chunk.Span);
                    totalSize += chunk.Length;
                    timings?.Add("read_chunk", t);
                    t = Stopwatch.GetTimestamp();
                    try
                    {
                        await channel.Writer.WriteAsync(chunk, cts.Token);
                        timings?.Add("backpressure_wait", t);
                        t = Stopwatch.GetTimestamp();
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
                    var t = Stopwatch.GetTimestamp();
                    var hash = ContentHash.Compute(chunk.Span);
                    timings?.Add("hash", t);
                    var outcome = await store.StoreAsync(hash, chunk.Memory, cts.Token, timings);
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
