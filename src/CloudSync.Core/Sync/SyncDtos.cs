namespace CloudSync.Core.Sync;

// Wire contracts shared by the REST gateway and the CLI client.

public sealed record NegotiateRequest(IReadOnlyList<string> Hashes);

public sealed record NegotiateResponse(IReadOnlyList<string> Missing);

public sealed record CommitRequest(
    string FileName, IReadOnlyList<string> Chunks, string? Chunker, Guid? FolderId = null, string? Path = null);

public sealed record UploadResult(
    Guid FileId,
    string FileName,
    long TotalSize,
    string? Sha256,
    int ChunkCount,
    int NewChunks,
    int DedupedChunks,
    string Chunker,
    Dictionary<string, double>? Timings = null);

public sealed record PutChunkResult(string Hash, bool Written);
