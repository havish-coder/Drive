namespace CloudSync.Core.Chunking;

public sealed class ChunkingOptions
{
    /// <summary>"FixedSize" or "FastCdc".</summary>
    public string Algorithm { get; set; } = "FixedSize";

    public int ChunkSize { get; set; } = 4 * 1024 * 1024;

    public int MinSize { get; set; } = 1 * 1024 * 1024;
    public int AvgSize { get; set; } = 4 * 1024 * 1024;
    public int MaxSize { get; set; } = 16 * 1024 * 1024;
}

/// <summary>Factory: picks the chunking strategy from configuration.</summary>
public static class ChunkerFactory
{
    public static IChunker Create(ChunkingOptions options) => options.Algorithm.ToLowerInvariant() switch
    {
        "fixedsize" or "fixed" => new FixedSizeChunker(options.ChunkSize),
        "fastcdc" or "cdc" => new FastCdcChunker(options.MinSize, options.AvgSize, options.MaxSize),
        _ => throw new ArgumentException($"Unknown chunking algorithm '{options.Algorithm}'."),
    };
}
