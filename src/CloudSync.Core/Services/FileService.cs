using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading.Channels;
using CloudSync.Core.Chunking;
using CloudSync.Core.Metadata;
using CloudSync.Core.Storage;
using CloudSync.Core.Sync;

namespace CloudSync.Core.Services;

/// <summary>Facade over upload, download and delta-sync orchestration.</summary>
public sealed class FileService(
    IMetadataRepository metadata,
    IBlobStore blobs,
    ChunkStore chunkStore,
    UploadPipeline pipeline,
    IChunker chunker)
{
    public async Task<UploadResult> UploadAsync(
        string userId, string fileName, PipeReader source, CancellationToken ct = default, Guid? folderId = null,
        Guid? replaceFileId = null)
    {
        var timings = new Timings();
        var start = Stopwatch.GetTimestamp();
        var run = await pipeline.RunAsync(chunker, source, ct, timings);

        // Manifest last, in one transaction: a failed upload never leaves a visible half-file.
        var t = Stopwatch.GetTimestamp();
        var file = new FileRecord(replaceFileId ?? Guid.NewGuid(), userId, fileName, run.TotalSize, run.Sha256, chunker.Name, DateTimeOffset.UtcNow, folderId);
        await SaveAsync(file, run.Chunks.Select(c => new FileChunkRecord(c.SequenceIndex, c.Hash)).ToList(), replaceFileId is not null, ct);
        timings.Add("manifest_save", t);
        timings.Add("total", start);

        var written = run.Chunks.Count(c => c.Written);
        return new UploadResult(file.FileId, fileName, file.TotalSize, file.Sha256, run.Chunks.Count, written,
            run.Chunks.Count - written, chunker.Name, timings.ToMilliseconds());
    }

    public Task<FileManifest?> GetManifestAsync(Guid fileId, CancellationToken ct = default) =>
        metadata.GetManifestAsync(fileId, ct);

    public Task<StorageStats> GetStatsAsync(CancellationToken ct = default) => metadata.GetStatsAsync(ct);

    /// <summary>
    /// Streams the file in manifest order. A read-ahead task fetches and verifies the next chunk while the
    /// current one is written. A chunk that fails verification aborts the download: never serve corrupt bytes.
    /// </summary>
    public async Task DownloadAsync(FileManifest manifest, Stream destination, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<(byte[] Buffer, int Length)>(2);

        var reader = Task.Run(async () =>
        {
            try
            {
                foreach (var entry in manifest.Chunks)
                {
                    var size = checked((int)entry.ByteSize);
                    var buffer = ArrayPool<byte>.Shared.Rent(size);
                    try
                    {
                        var read = await blobs.ReadAsync(entry.ChunkHash, buffer.AsMemory(0, size), cts.Token);
                        if (read != size)
                            throw new ChunkIntegrityException(entry.ChunkHash, $"expected {size} bytes, read {read}");
                        if (ContentHash.Compute(buffer.AsSpan(0, size)) != entry.ChunkHash)
                            throw new ChunkIntegrityException(entry.ChunkHash, "hash mismatch");
                        await channel.Writer.WriteAsync((buffer, size), cts.Token);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        });

        try
        {
            await foreach (var (buffer, length) in channel.Reader.ReadAllAsync(cts.Token))
            {
                try
                {
                    await destination.WriteAsync(buffer.AsMemory(0, length), cts.Token);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        finally
        {
            cts.Cancel();
            await reader;
            while (channel.Reader.TryRead(out var leftover)) ArrayPool<byte>.Shared.Return(leftover.Buffer);
        }
    }

    /// <summary>
    /// Reads one chunk and verifies it before returning, so the caller can still send a clean error status.
    /// The returned chunk owns a pooled buffer: dispose it.
    /// </summary>
    public async Task<RawChunk> ReadChunkAsync(ManifestEntry entry, CancellationToken ct = default)
    {
        var size = checked((int)entry.ByteSize);
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var read = await blobs.ReadAsync(entry.ChunkHash, buffer.AsMemory(0, size), ct);
            if (read != size)
                throw new ChunkIntegrityException(entry.ChunkHash, $"expected {size} bytes, read {read}");
            if (ContentHash.Compute(buffer.AsSpan(0, size)) != entry.ChunkHash)
                throw new ChunkIntegrityException(entry.ChunkHash, "hash mismatch");
            return new RawChunk(entry.SequenceIndex, 0, buffer, size);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>Seekable read-only view of a file for HTTP Range requests (media seeking, resumed downloads).</summary>
    public Stream OpenRead(FileManifest manifest) => new ChunkedFileStream(manifest, this);

    private Task SaveAsync(FileRecord file, IReadOnlyList<FileChunkRecord> chunks, bool newVersion, CancellationToken ct) =>
        newVersion ? metadata.SaveVersionAsync(file, chunks, ct) : metadata.SaveFileAsync(file, chunks, ct);

    /// <summary>Maps a byte position to its chunk (binary search over cumulative sizes); every chunk is hash-verified on load.</summary>
    private sealed class ChunkedFileStream(FileManifest manifest, FileService files) : Stream
    {
        private readonly long[] _starts = Starts(manifest);
        private RawChunk? _chunk;

        private static long[] Starts(FileManifest m)
        {
            var starts = new long[m.Chunks.Count];
            for (var i = 1; i < starts.Length; i++) starts[i] = starts[i - 1] + m.Chunks[i - 1].ByteSize;
            return starts;
        }
        private int _loaded = -1;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => manifest.File.TotalSize;
        public override long Position { get; set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= Length || buffer.IsEmpty) return 0;
            var index = Array.BinarySearch(_starts, Position);
            if (index < 0) index = ~index - 1;
            if (index != _loaded)
            {
                _chunk?.Dispose();
                _chunk = await files.ReadChunkAsync(manifest.Chunks[index], ct);
                _loaded = index;
            }
            var offset = (int)(Position - _starts[index]);
            var count = Math.Min(buffer.Length, _chunk!.Length - offset);
            _chunk.Memory.Slice(offset, count).CopyTo(buffer);
            Position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => Length + offset,
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _chunk?.Dispose(); base.Dispose(disposing); }
    }

    // ---- Delta sync: client chunks locally, uploads only what the server lacks ----

    public async Task<IReadOnlyList<string>> FindMissingAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        var distinct = hashes.Select(ContentHash.EnsureValid).Distinct().ToList();
        var present = await metadata.GetChunkSizesAsync(distinct, ct);
        return distinct.Where(h => !present.ContainsKey(h)).ToList();
    }

    /// <summary>Never trust a client-claimed hash: re-hash, or one client could poison every file sharing that chunk.</summary>
    public async Task<ChunkStoreOutcome> PutChunkAsync(string claimedHash, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ContentHash.EnsureValid(claimedHash);
        var actual = ContentHash.Compute(data.Span);
        if (actual != claimedHash)
            throw new ChunkIntegrityException(claimedHash, $"body hashes to {actual}");
        return await chunkStore.StoreAsync(claimedHash, data, ct);
    }

    public async Task<UploadResult> CommitAsync(string userId, CommitRequest request, CancellationToken ct = default, Guid? replaceFileId = null)
    {
        if (string.IsNullOrWhiteSpace(request.FileName)) throw new ArgumentException("FileName is required.");
        foreach (var hash in request.Chunks) ContentHash.EnsureValid(hash);

        // Sizes come from the catalog, never from the client.
        var sizes = await metadata.GetChunkSizesAsync(request.Chunks.Distinct().ToList(), ct);
        var missing = request.Chunks.Where(h => !sizes.ContainsKey(h)).Distinct().ToList();
        if (missing.Count > 0) throw new MissingChunksException(missing);

        var total = request.Chunks.Sum(h => sizes[h]);
        var chunkerName = request.Chunker ?? "client";
        var file = new FileRecord(replaceFileId ?? Guid.NewGuid(), userId, request.FileName, total, null, chunkerName, DateTimeOffset.UtcNow, request.FolderId);
        await SaveAsync(file, request.Chunks.Select((h, i) => new FileChunkRecord(i, h)).ToList(), replaceFileId is not null, ct);

        return new UploadResult(file.FileId, file.FileName, total, null, request.Chunks.Count, 0, request.Chunks.Count, chunkerName);
    }
}
