namespace CloudSync.Core;

/// <summary>Bytes don't match the hash they are stored or claimed under.</summary>
public sealed class ChunkIntegrityException(string hash, string detail)
    : Exception($"Chunk {hash} failed integrity check: {detail}")
{
    public string Hash { get; } = hash;
}

/// <summary>A manifest references chunks the store doesn't have.</summary>
public sealed class MissingChunksException(IReadOnlyList<string> missing)
    : Exception($"{missing.Count} referenced chunk(s) are not stored.")
{
    public IReadOnlyList<string> Missing { get; } = missing;
}
