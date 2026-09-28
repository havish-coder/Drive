namespace CloudSync.Core.Metadata;

public sealed record ChunkRecord(string ChunkHash, string StoragePath, long ByteSize);

public sealed record FileRecord(
    Guid FileId, string UserId, string FileName, long TotalSize, string? Sha256, string Chunker, DateTimeOffset CreatedAt);

public sealed record FileChunkRecord(int SequenceIndex, string ChunkHash);

public sealed record ManifestEntry(int SequenceIndex, string ChunkHash, long ByteSize);

public sealed record FileManifest(FileRecord File, IReadOnlyList<ManifestEntry> Chunks);

public sealed record StorageStats(long FileCount, long ChunkCount, long LogicalBytes, long PhysicalBytes)
{
    public double DedupRatio => PhysicalBytes == 0 ? 0 : (double)LogicalBytes / PhysicalBytes;
}

/// <summary>Repository over the Files / Chunks / File_Chunks catalog.</summary>
public interface IMetadataRepository
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<bool> ChunkExistsAsync(string hash, CancellationToken ct = default);

    /// <summary>Sizes of the given hashes that are stored; absent keys are missing.</summary>
    Task<IReadOnlyDictionary<string, long>> GetChunkSizesAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default);

    /// <summary>INSERT OR IGNORE. Returns true if this call inserted the row.</summary>
    Task<bool> TryAddChunkAsync(ChunkRecord chunk, CancellationToken ct = default);

    /// <summary>Writes the file row and its ordered chunk list in one transaction.</summary>
    Task SaveFileAsync(FileRecord file, IReadOnlyList<FileChunkRecord> chunks, CancellationToken ct = default);

    Task<FileManifest?> GetManifestAsync(Guid fileId, CancellationToken ct = default);

    Task<IReadOnlyList<FileRecord>> ListFilesAsync(string userId, CancellationToken ct = default);

    Task<StorageStats> GetStatsAsync(CancellationToken ct = default);
}
