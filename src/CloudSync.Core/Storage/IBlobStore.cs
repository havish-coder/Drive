namespace CloudSync.Core.Storage;

/// <summary>Content-addressable blob storage. Implementations: local filesystem, remote gRPC worker.</summary>
public interface IBlobStore
{
    /// <summary>Relative key the blob lives under, e.g. "ab/cd/abcd…".</summary>
    string GetStorageKey(string hash);

    /// <summary>Atomically stores the blob. Returns true if bytes were physically written, false if it already existed.</summary>
    Task<bool> PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Reads the blob into <paramref name="destination"/> and returns the byte count.</summary>
    Task<int> ReadAsync(string hash, Memory<byte> destination, CancellationToken ct = default);

    Task<bool> ExistsAsync(string hash, CancellationToken ct = default);
}

public static class BlobKey
{
    /// <summary>Two levels of hash-prefix fan-out: 65,536 directories instead of one huge one.</summary>
    public static string For(string hash)
    {
        ContentHash.EnsureValid(hash);
        return $"{hash[..2]}/{hash[2..4]}/{hash}";
    }
}
