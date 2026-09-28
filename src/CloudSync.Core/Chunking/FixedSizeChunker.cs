using System.Buffers;

namespace CloudSync.Core.Chunking;

/// <summary>Cuts every <c>chunkSize</c> bytes. Simple and fast, but an insert shifts every later boundary.</summary>
public sealed class FixedSizeChunker : ChunkerBase
{
    private readonly int _chunkSize;

    public FixedSizeChunker(int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        _chunkSize = chunkSize;
    }

    public override string Name => $"fixed-{_chunkSize}";
    public override int MaxChunkSize => _chunkSize;

    protected override int FindCutPoint(ReadOnlySequence<byte> buffer, bool isFinal) =>
        (int)Math.Min(buffer.Length, _chunkSize);
}
