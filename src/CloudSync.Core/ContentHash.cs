using System.Security.Cryptography;

namespace CloudSync.Core;

/// <summary>SHA-256 content addressing: a chunk's identity is the lowercase hex digest of its bytes.</summary>
public static class ContentHash
{
    public static string Compute(ReadOnlySpan<byte> data)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(data, digest);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static bool IsValid(string? hash) =>
        hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string EnsureValid(string? hash) =>
        IsValid(hash) ? hash! : throw new ArgumentException($"'{hash}' is not a lowercase SHA-256 hex digest.");
}
