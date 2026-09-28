using System.IO.Pipelines;

namespace CloudSync.Core.Chunking;

/// <summary>Strategy: splits a byte stream into ordered chunks.</summary>
public interface IChunker
{
    /// <summary>Stable identifier recorded with each file, e.g. "fixed-4194304".</summary>
    string Name { get; }

    int MaxChunkSize { get; }

    IAsyncEnumerable<RawChunk> ChunkAsync(PipeReader source, CancellationToken ct = default);
}
